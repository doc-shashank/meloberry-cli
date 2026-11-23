using System.Text.Json;
using System.Text.Json.Nodes;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;

namespace Meloberry.Application.UseCases.Json;

public sealed record PatchEntityCommand(
    string Address,
    string? JsonFilePath = null,
    string? InlineJson = null,
    IReadOnlyList<string>? SetPairs = null,
    string? MoveSpec = null);

public sealed record PatchEntityResponse(
    string TargetId,
    string EntityKind,
    string WorkspaceId,
    int? NewIndex = null,
    IReadOnlyList<string>? UpdatedFields = null);

public sealed class PatchEntityHandler(
    EntityAddressResolver addressResolver,
    IWorkspaceRepository repository,
    IWorkingDirectoryProvider workingDirectoryProvider,
    IContentInspector contentInspector)
{
    private static readonly Dictionary<string, HashSet<string>> AllowedKeysByKind = new(StringComparer.Ordinal)
    {
        ["session"] = new(StringComparer.OrdinalIgnoreCase) { "name", "description" },
        ["quiz"] = new(StringComparer.OrdinalIgnoreCase) { "name", "description" },
        ["component"] = new(StringComparer.OrdinalIgnoreCase) { "title", "ref", "layoutOverrides" },
        ["articleBlock:text"] = new(StringComparer.OrdinalIgnoreCase) { "text", "ref" },
        ["articleBlock:image"] = new(StringComparer.OrdinalIgnoreCase) { "imagePath", "caption", "alt", "ref" },
        ["articleBlock:callout"] = new(StringComparer.OrdinalIgnoreCase) { "tone", "title", "text", "ref" },
        ["question:mcq"] = new(StringComparer.OrdinalIgnoreCase) { "text", "imagePath", "explanation", "isMultiCorrect", "maxOptions", "options", "ref", "layoutOverrides" },
        ["question:flashcard"] = new(StringComparer.OrdinalIgnoreCase) { "front", "back", "ref", "layoutOverrides" }
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<Result<PatchEntityResponse>> HandleAsync(PatchEntityCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.JsonFilePath) &&
            command.InlineJson is null &&
            (command.SetPairs is null || command.SetPairs.Count == 0) &&
            string.IsNullOrWhiteSpace(command.MoveSpec))
        {
            return new Error("Patch.Empty", "Provide a patch JSON file/'-', --set k=v, or --move <idx>:<shift|exchange>.", ErrorType.Validation);
        }

        var resolved = await addressResolver.ResolveAsync(command.Address, cancellationToken);
        if (!resolved.IsSuccess) return resolved.Error!;
        var target = resolved.Value;

        JsonObject patchObj = [];
        string jsonDir = workingDirectoryProvider.GetCurrentDirectory();

        if (!string.IsNullOrWhiteSpace(command.JsonFilePath) || command.InlineJson is not null)
        {
            string rawJson;
            if (command.InlineJson is not null)
            {
                rawJson = command.InlineJson.TrimStart('\uFEFF');
            }
            else
            {
                if (!File.Exists(command.JsonFilePath))
                    return new Error("Json.FileNotFound", $"JSON file '{command.JsonFilePath}' was not found.", ErrorType.NotFound);
                rawJson = (await File.ReadAllTextAsync(command.JsonFilePath, cancellationToken)).TrimStart('\uFEFF');
                jsonDir = Path.GetDirectoryName(Path.GetFullPath(command.JsonFilePath)) ?? jsonDir;
            }

            if (string.IsNullOrWhiteSpace(rawJson))
                return new Error("Json.InvalidSpec", "Patch JSON input cannot be empty.", ErrorType.Validation);

            try
            {
                var node = JsonNode.Parse(rawJson, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                if (node is not JsonObject obj)
                    return new Error("Json.InvalidSpec", "Patch JSON must be a JSON object.", ErrorType.Validation);
                patchObj = obj;
            }
            catch (JsonException ex)
            {
                return new Error("Json.ParseError", $"Invalid patch JSON: {ex.Message}", ErrorType.Validation, Path: ex.Path);
            }
        }

        if (command.SetPairs is not null)
        {
            foreach (var pair in command.SetPairs)
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                    return new Error("Patch.InvalidSet", $"Invalid --set '{pair}'. Expected key=value.", ErrorType.Validation);

                var keyPath = pair[..eq].Trim();
                var rawVal = pair[(eq + 1)..];
                ApplySetPair(patchObj, keyPath, rawVal);
            }
        }

        var schemaKey = GetSchemaKey(target);
        if (AllowedKeysByKind.TryGetValue(schemaKey, out var allowedKeys))
        {
            foreach (var kv in patchObj)
            {
                if (!allowedKeys.Contains(kv.Key))
                {
                    var hint = FindClosest(kv.Key, allowedKeys);
                    return new Error(
                        "Patch.UnknownField",
                        $"Unknown or unsupported field '{kv.Key}' for {schemaKey}.",
                        ErrorType.Validation,
                        Path: kv.Key,
                        Hint: hint is null ? null : $"Did you mean '{hint}'?");
                }
            }
        }

        var updatedFields = patchObj.Select(kv => kv.Key).ToList();

        var mutateRes = target.EntityKind switch
        {
            "session" => ApplySessionPatch(target.Session!, patchObj),
            "quiz" => ApplyQuizPatch(target.Quiz!, patchObj),
            "component" => ApplyComponentPatch(target.Session!, target.Component!, patchObj),
            "articleBlock" => await ApplyBlockPatchAsync(target.Session!, target.ArticleBlock!, target.StorageDirectory, jsonDir, patchObj, cancellationToken),
            "question" => await ApplyQuestionPatchAsync(target, jsonDir, patchObj, cancellationToken),
            _ => new Error("Patch.UnsupportedTarget", $"Cannot patch '{target.EntityKind}'.", ErrorType.Validation)
        };

        if (!mutateRes.IsSuccess) return mutateRes.Error!;

        int? newIndex = null;
        if (!string.IsNullOrWhiteSpace(command.MoveSpec))
        {
            var moveRes = ApplyMove(target, command.MoveSpec);
            if (!moveRes.IsSuccess) return moveRes.Error!;
            newIndex = moveRes.Value;
            updatedFields.Add("index");
        }

        if (target.Session is not null)
            await repository.SaveSessionAsync(target.Session, cancellationToken);
        else if (target.Quiz is not null)
            await repository.SaveQuizAsync(target.Quiz, cancellationToken);

        return new PatchEntityResponse(
            target.EntityId,
            target.EntityKind,
            target.WorkspaceId,
            newIndex,
            updatedFields.Count > 0 ? updatedFields : null);
    }

    private static string GetSchemaKey(ResolvedEntity target) => target.EntityKind switch
    {
        "articleBlock" => target.ArticleBlock!.Kind switch
        {
            ArticleBlockKind.Image => "articleBlock:image",
            ArticleBlockKind.Callout => "articleBlock:callout",
            _ => "articleBlock:text"
        },
        "question" => target.Question is McqQuestion ? "question:mcq" : "question:flashcard",
        _ => target.EntityKind
    };

    private static void ApplySetPair(JsonObject root, string keyPath, string rawVal)
    {
        var parsedVal = ParseScalarOrJsonNode(rawVal);
        var dot = keyPath.IndexOf('.');
        if (dot < 0)
        {
            root[keyPath] = parsedVal;
            return;
        }

        var parentKey = keyPath[..dot].Trim();
        var childKey = keyPath[(dot + 1)..].Trim();
        if (root[parentKey] is not JsonObject childObj)
        {
            childObj = [];
            root[parentKey] = childObj;
        }
        childObj[childKey] = parsedVal;
    }

    private static JsonNode? ParseScalarOrJsonNode(string rawVal)
    {
        var trimmed = rawVal.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        if (int.TryParse(trimmed, out int intVal)) return JsonValue.Create(intVal);
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try { return JsonNode.Parse(trimmed); } catch { }
        }
        return JsonValue.Create(rawVal);
    }

    private static Result ApplySessionPatch(StudySession session, JsonObject patch)
    {
        if (TryGetProp(patch, "name", out var nameNode))
        {
            if (nameNode is null || nameNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'name' must be a non-empty string.", ErrorType.Validation, Path: "name");
            var res = session.SetName(nameNode.GetValue<string>());
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "description", out var descNode))
        {
            if (descNode is not null && descNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'description' must be a string or null.", ErrorType.Validation, Path: "description");
            session.SetDescription(descNode?.GetValue<string>() ?? string.Empty);
        }
        return Result.Success();
    }

    private static Result ApplyQuizPatch(Quiz quiz, JsonObject patch)
    {
        if (TryGetProp(patch, "name", out var nameNode))
        {
            if (nameNode is null || nameNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'name' must be a non-empty string.", ErrorType.Validation, Path: "name");
            var res = quiz.SetName(nameNode.GetValue<string>());
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "description", out var descNode))
        {
            if (descNode is not null && descNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'description' must be a string or null.", ErrorType.Validation, Path: "description");
            quiz.SetDescription(descNode?.GetValue<string>() ?? string.Empty);
        }
        return Result.Success();
    }

    private static Result ApplyComponentPatch(StudySession session, SessionComponent comp, JsonObject patch)
    {
        if (TryGetProp(patch, "title", out var titleNode))
        {
            if (titleNode is null || titleNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'title' must be a non-empty string.", ErrorType.Validation, Path: "title");
            var res = comp.SetTitle(titleNode.GetValue<string>());
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "ref", out var refNode))
        {
            var res = ApplyRefToSessionEntity(session, refNode, comp.Id, comp.SetRefKey);
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "layoutOverrides", out var ovNode))
        {
            if (ovNode is not JsonObject ovObj)
                return new Error("Patch.InvalidValue", "'layoutOverrides' must be a JSON object.", ErrorType.Validation, Path: "layoutOverrides");
            foreach (var kv in ovObj)
            {
                if (kv.Value is null)
                {
                    comp.RemoveLayoutOverride(kv.Key);
                }
                else
                {
                    var res = comp.SetLayoutOverride(kv.Key, kv.Value.ToString());
                    if (!res.IsSuccess) return res.Error!;
                }
            }
        }
        return Result.Success();
    }

    private async Task<Result> ApplyBlockPatchAsync(
        StudySession session,
        ArticleBlock block,
        string storageDir,
        string jsonDir,
        JsonObject patch,
        CancellationToken ct)
    {
        if (TryGetProp(patch, "ref", out var refNode))
        {
            var res = ApplyRefToSessionEntity(session, refNode, block.Id, block.SetRefKey);
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "text", out var textNode))
        {
            if (textNode is null || textNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'text' must be a non-empty string.", ErrorType.Validation, Path: "text");
            var res = block.Kind == ArticleBlockKind.Callout
                ? block.UpdateCallout(null, textNode.GetValue<string>(), null)
                : block.UpdateText(textNode.GetValue<string>());
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "tone", out var toneNode))
        {
            if (toneNode is null || toneNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'tone' must be 'pearl', 'clinical', or 'warning'.", ErrorType.Validation, Path: "tone");
            if (!ArticleBlock.TryParseCalloutTone(toneNode.GetValue<string>(), out var tone))
                return ArticleErrors.InvalidCalloutTone(toneNode.GetValue<string>());
            var res = block.UpdateCallout(tone, null, null);
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "title", out var titleNode))
        {
            if (titleNode is not null && titleNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'title' must be a string or null.", ErrorType.Validation, Path: "title");
            var res = block.UpdateCallout(null, null, titleNode?.GetValue<string>(), updateTitle: true);
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "imagePath", out var imgNode))
        {
            if (imgNode is null || imgNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'imagePath' must be a non-empty string.", ErrorType.Validation, Path: "imagePath");
            var imgRes = await JsonQuestionMaterializer.CopyConfinedImageAsync(imgNode.GetValue<string>(), jsonDir, storageDir, contentInspector, ct);
            if (!imgRes.IsSuccess) return imgRes.Error!;
            var res = block.UpdateImage(imgRes.Value!);
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "caption", out var capNode))
        {
            if (capNode is not null && capNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'caption' must be a string or null.", ErrorType.Validation, Path: "caption");
            var res = block.SetImageCaption(capNode?.GetValue<string>());
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "alt", out var altNode))
        {
            if (altNode is not null && altNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'alt' must be a string or null.", ErrorType.Validation, Path: "alt");
            var res = block.SetImageAlt(altNode?.GetValue<string>());
            if (!res.IsSuccess) return res.Error!;
        }
        return Result.Success();
    }

    private async Task<Result> ApplyQuestionPatchAsync(ResolvedEntity target, string jsonDir, JsonObject patch, CancellationToken ct)
    {
        var question = target.Question!;
        if (TryGetProp(patch, "ref", out var refNode))
        {
            var refRes = target.Session is not null
                ? ApplyRefToSessionEntity(target.Session, refNode, question.Id, question.SetRefKey)
                : ApplyRefToQuizQuestion(target.Quiz!, refNode, question);
            if (!refRes.IsSuccess) return refRes.Error!;
        }

        if (TryGetProp(patch, "layoutOverrides", out var ovNode))
        {
            if (ovNode is not JsonObject ovObj)
                return new Error("Patch.InvalidValue", "'layoutOverrides' must be a JSON object.", ErrorType.Validation, Path: "layoutOverrides");
            foreach (var kv in ovObj)
            {
                if (kv.Value is null)
                {
                    question.RemoveLayoutOverride(kv.Key);
                }
                else
                {
                    var res = question.SetLayoutOverride(kv.Key, kv.Value.ToString());
                    if (!res.IsSuccess) return res.Error!;
                }
            }
        }

        if (question is McqQuestion mcq)
            return await ApplyMcqPatchAsync(mcq, target.StorageDirectory, jsonDir, patch, ct);

        return await ApplyFlashcardPatchAsync((FlashcardQuestion)question, target.StorageDirectory, jsonDir, patch, ct);
    }

    private async Task<Result> ApplyMcqPatchAsync(McqQuestion mcq, string storageDir, string jsonDir, JsonObject patch, CancellationToken ct)
    {
        if (TryGetProp(patch, "text", out var textNode))
        {
            if (textNode is null || textNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'text' must be a non-empty string.", ErrorType.Validation, Path: "text");
            var res = mcq.SetText(textNode.GetValue<string>());
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "imagePath", out var imgNode))
        {
            if (imgNode is null)
            {
                mcq.SetImage(null);
            }
            else if (imgNode.GetValueKind() == JsonValueKind.String)
            {
                var copyRes = await JsonQuestionMaterializer.CopyConfinedImageAsync(imgNode.GetValue<string>(), jsonDir, storageDir, contentInspector, ct);
                if (!copyRes.IsSuccess) return copyRes.Error!;
                mcq.SetImage(copyRes.Value);
            }
            else return new Error("Patch.InvalidValue", "'imagePath' must be a string or null.", ErrorType.Validation, Path: "imagePath");
        }
        if (TryGetProp(patch, "explanation", out var expNode))
        {
            if (expNode is not null && expNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'explanation' must be a string or null.", ErrorType.Validation, Path: "explanation");
            mcq.SetExplanation(expNode?.GetValue<string>());
        }
        if (TryGetProp(patch, "maxOptions", out var maxNode))
        {
            if (maxNode is null || !maxNode.AsValue().TryGetValue<int>(out int maxVal))
                return new Error("Patch.InvalidValue", "'maxOptions' must be an integer.", ErrorType.Validation, Path: "maxOptions");
            var res = mcq.SetMaxOptions(maxVal);
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "isMultiCorrect", out var multiNode))
        {
            if (multiNode is null || !multiNode.AsValue().TryGetValue<bool>(out bool multi))
                return new Error("Patch.InvalidValue", "'isMultiCorrect' must be a boolean.", ErrorType.Validation, Path: "isMultiCorrect");
            if (multi) mcq.ConvertToMultiple();
            else
            {
                var res = mcq.ConvertToSingle();
                if (!res.IsSuccess) return res.Error!;
            }
        }
        if (TryGetProp(patch, "options", out var optsNode))
        {
            if (optsNode is not JsonArray optsArr)
                return new Error("Patch.InvalidValue", "'options' must be an array.", ErrorType.Validation, Path: "options");

            var parsedOpts = new List<PatchOptionDto>(optsArr.Count);
            for (int i = 0; i < optsArr.Count; i++)
            {
                if (optsArr[i] is null)
                    return new Error("Json.InvalidQuestion", "MCQ 'options' cannot contain null entries.", ErrorType.Validation, Path: $"options[{i}]");
                try
                {
                    var optDto = optsArr[i]!.Deserialize<PatchOptionDto>(new JsonSerializerOptions(ReadOptions)
                    {
                        PropertyNameCaseInsensitive = true,
                        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
                    });
                    if (optDto is null)
                        return new Error("Json.InvalidQuestion", $"Invalid option at index {i}.", ErrorType.Validation, Path: $"options[{i}]");
                    parsedOpts.Add(optDto);
                }
                catch (JsonException ex)
                {
                    return new Error("Json.ParseError", ex.Message, ErrorType.Validation, Path: $"options[{i}]");
                }
            }

            // Materialize images first before clearing existing options
            var materialized = new List<(int Index, string? Text, string? Img, bool IsCorrect)>(parsedOpts.Count);
            for (int i = 0; i < parsedOpts.Count; i++)
            {
                var o = parsedOpts[i];
                int idx = o.Index > 0 ? o.Index : i + 1;
                var imgRes = await JsonQuestionMaterializer.CopyConfinedImageAsync(o.ImagePath, jsonDir, storageDir, contentInspector, ct);
                if (!imgRes.IsSuccess) return imgRes.Error!;
                materialized.Add((idx, o.Text, imgRes.Value, o.IsCorrect));
            }

            mcq.ClearOptions();
            foreach (var (idx, txt, img, isCorrect) in materialized)
            {
                var optRes = mcq.SetOption(idx, txt, img);
                if (!optRes.IsSuccess) return optRes.Error!;
                if (isCorrect)
                {
                    var markRes = mcq.MarkCorrect(idx);
                    if (!markRes.IsSuccess) return markRes.Error!;
                }
            }
        }
        return Result.Success();
    }

    private async Task<Result> ApplyFlashcardPatchAsync(FlashcardQuestion card, string storageDir, string jsonDir, JsonObject patch, CancellationToken ct)
    {
        if (TryGetProp(patch, "front", out var frontNode))
        {
            var res = await PatchFlashcardSideAsync(card, FlashcardFace.Front, frontNode, storageDir, jsonDir, ct);
            if (!res.IsSuccess) return res.Error!;
        }
        if (TryGetProp(patch, "back", out var backNode))
        {
            var res = await PatchFlashcardSideAsync(card, FlashcardFace.Back, backNode, storageDir, jsonDir, ct);
            if (!res.IsSuccess) return res.Error!;
        }
        return Result.Success();
    }

    private async Task<Result> PatchFlashcardSideAsync(
        FlashcardQuestion card,
        FlashcardFace face,
        JsonNode? sideNode,
        string storageDir,
        string jsonDir,
        CancellationToken ct)
    {
        if (sideNode is not JsonObject sideObj)
            return new Error("Patch.InvalidValue", $"Flashcard '{face.ToString().ToLowerInvariant()}' must be a JSON object.", ErrorType.Validation);

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text", "imagePath", "position", "textPosition" };
        foreach (var kv in sideObj)
        {
            if (!allowed.Contains(kv.Key))
                return new Error("Patch.UnknownField", $"Unknown flashcard side field '{kv.Key}'.", ErrorType.Validation, Path: $"{face.ToString().ToLowerInvariant()}.{kv.Key}");
        }

        if (TryGetProp(sideObj, "text", out var textNode))
        {
            if (textNode is null) card.ClearSideText(face);
            else if (textNode.GetValueKind() == JsonValueKind.String)
            {
                var res = card.SetSideText(face, textNode.GetValue<string>());
                if (!res.IsSuccess) return res.Error!;
            }
            else return new Error("Patch.InvalidValue", "Flashcard side 'text' must be a string or null.", ErrorType.Validation);
        }

        if (TryGetProp(sideObj, "imagePath", out var imgNode))
        {
            if (imgNode is null) card.ClearSideImage(face);
            else if (imgNode.GetValueKind() == JsonValueKind.String)
            {
                var copyRes = await JsonQuestionMaterializer.CopyConfinedImageAsync(imgNode.GetValue<string>(), jsonDir, storageDir, contentInspector, ct);
                if (!copyRes.IsSuccess) return copyRes.Error!;
                if (copyRes.Value is not null) card.SetSideImage(face, copyRes.Value);
            }
            else return new Error("Patch.InvalidValue", "Flashcard side 'imagePath' must be a string or null.", ErrorType.Validation);
        }

        if (TryGetProp(sideObj, "position", out var posNode) || TryGetProp(sideObj, "textPosition", out posNode))
        {
            var rawPos = posNode?.GetValue<string>()?.Trim().ToLowerInvariant();
            switch (rawPos)
            {
                case "aboveimage":
                case "textaboveimage":
                    card.SetSidePosition(face, MediaPosition.TextAboveImage);
                    break;
                case "belowimage":
                case "textbelowimage":
                    card.SetSidePosition(face, MediaPosition.TextBelowImage);
                    break;
                default:
                    return new Error("Json.InvalidPosition", $"Invalid flashcard position '{rawPos}'. Use 'belowImage' or 'aboveImage'.", ErrorType.Validation);
            }
        }

        return Result.Success();
    }

    private static Result ApplyRefToSessionEntity(StudySession session, JsonNode? refNode, string selfId, Func<string?, Result> setRef)
    {
        if (refNode is not null && refNode.GetValueKind() != JsonValueKind.String)
            return new Error("Patch.InvalidValue", "'ref' must be a string or null.", ErrorType.Validation, Path: "ref");

        var raw = refNode?.GetValue<string>();
        var normRes = RefKeyValidator.ValidateAndNormalize(raw);
        if (!normRes.IsSuccess) return normRes.Error!;
        if (normRes.Value is not null)
        {
            foreach (var c in session.Components)
            {
                if (c.Id != selfId && string.Equals(c.RefKey, normRes.Value, StringComparison.Ordinal))
                    return AddressErrors.DuplicateRef(normRes.Value);
                foreach (var b in c.ArticleBlocks)
                    if (b.Id != selfId && string.Equals(b.RefKey, normRes.Value, StringComparison.Ordinal))
                        return AddressErrors.DuplicateRef(normRes.Value);
                if (c.Quiz is not null)
                    foreach (var q in c.Quiz.Questions)
                        if (q.Id != selfId && string.Equals(q.RefKey, normRes.Value, StringComparison.Ordinal))
                            return AddressErrors.DuplicateRef(normRes.Value);
            }
        }
        return setRef(raw);
    }

    private static Result ApplyRefToQuizQuestion(Quiz quiz, JsonNode? refNode, Question self)
    {
        if (refNode is not null && refNode.GetValueKind() != JsonValueKind.String)
            return new Error("Patch.InvalidValue", "'ref' must be a string or null.", ErrorType.Validation, Path: "ref");

        var raw = refNode?.GetValue<string>();
        var normRes = RefKeyValidator.ValidateAndNormalize(raw);
        if (!normRes.IsSuccess) return normRes.Error!;
        if (normRes.Value is not null && quiz.Questions.Any(q => q.Id != self.Id && string.Equals(q.RefKey, normRes.Value, StringComparison.Ordinal)))
            return AddressErrors.DuplicateRef(normRes.Value);
        return self.SetRefKey(raw);
    }

    private static Result<int> ApplyMove(ResolvedEntity target, string moveSpec)
    {
        var colon = moveSpec.IndexOf(':');
        if (colon <= 0 || !int.TryParse(moveSpec[..colon], out int index))
            return new Error("Patch.InvalidMove", $"Invalid --move '{moveSpec}'. Expected <index>:<shift|exchange>.", ErrorType.Validation);

        var modeRaw = moveSpec[(colon + 1)..].Trim().ToLowerInvariant();
        if (modeRaw is not ("shift" or "exchange"))
            return new Error("Patch.InvalidMove", $"Invalid move mode '{modeRaw}'. Use 'shift' or 'exchange'.", ErrorType.Validation);

        var mode = modeRaw == "shift" ? ReorderMode.Shift : ReorderMode.Exchange;
        switch (target.EntityKind)
        {
            case "component":
            {
                var res = target.Session!.ReorderComponent(target.EntityId, index, mode);
                return res.IsSuccess ? target.Component!.Index : res.Error!;
            }
            case "articleBlock":
            {
                var res = target.Component!.ReorderBlock(target.EntityId, index, mode);
                return res.IsSuccess ? target.ArticleBlock!.Index : res.Error!;
            }
            case "question":
            {
                var res = target.Quiz!.ReorderQuestion(target.EntityId, index, mode);
                return res.IsSuccess ? target.Question!.Index : res.Error!;
            }
            default:
                return new Error("Patch.UnsupportedMove", $"Cannot move entity kind '{target.EntityKind}'.", ErrorType.Validation);
        }
    }

    private static bool TryGetProp(JsonObject obj, string name, out JsonNode? value)
    {
        foreach (var kv in obj)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static string? FindClosest(string unknown, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDist = 3;
        foreach (var c in candidates)
        {
            int d = Levenshtein(unknown.ToLowerInvariant(), c.ToLowerInvariant());
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return dp[a.Length, b.Length];
    }

    private sealed class PatchOptionDto
    {
        public int Index { get; set; }
        public string? Text { get; set; }
        public string? ImagePath { get; set; }
        public bool IsCorrect { get; set; }
    }
}
