using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;
using static Meloberry.Application.UseCases.Json.JsonNodeReaders;

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
        ["articleBlock:table"] = new(StringComparer.OrdinalIgnoreCase) { "title", "caption", "stickyFirstCol", "hideableCols", "headers", "alignments", "rows", "footnote", "ref" },
        ["articleBlock:diagram-explore"] = new(StringComparer.OrdinalIgnoreCase) { "title", "caption", "alt", "imagePath", "hotspots", "ref" },
        ["articleBlock:reveal"] = new(StringComparer.OrdinalIgnoreCase) { "title", "showFirst", "steps", "ref" },
        ["question:mcq"] = new(StringComparer.OrdinalIgnoreCase) { "text", "imagePath", "explanation", "isMultiCorrect", "maxOptions", "options", "ref", "layoutOverrides" },
        ["question:flashcard"] = new(StringComparer.OrdinalIgnoreCase) { "front", "back", "ref", "layoutOverrides" },
        ["question:fill-blank"] = new(StringComparer.OrdinalIgnoreCase) { "text", "fuzzy", "caseSensitive", "placeholder", "explanation", "ref", "layoutOverrides" },
        ["question:assertion-reason"] = new(StringComparer.OrdinalIgnoreCase) { "assertion", "reason", "answer", "scheme", "explanation", "ref", "layoutOverrides" },
        ["question:spot-error"] = new(StringComparer.OrdinalIgnoreCase) { "text", "imagePath", "requireFix", "allowNoError", "noError", "explanation", "ref", "layoutOverrides" },
        ["question:diff"] = new(StringComparer.OrdinalIgnoreCase) { "mode", "title", "bank", "fuzzy", "caseSensitive", "allowNoError", "headers", "alignments", "rows", "explanation", "ref", "layoutOverrides" },
        ["question:mechanism"] = new(StringComparer.OrdinalIgnoreCase) { "title", "steps", "explanation", "ref", "layoutOverrides" },
        ["question:relabel"] = new(StringComparer.OrdinalIgnoreCase) { "imagePath", "prompt", "mode", "style", "boxes", "distractors", "fuzzy", "caseSensitive", "explanation", "ref", "layoutOverrides" },
        ["question:case"] = new(StringComparer.OrdinalIgnoreCase) { "title", "stem", "imagePath", "caption", "questions", "explanation", "ref", "layoutOverrides" },
        ["question:classify"] = new(StringComparer.OrdinalIgnoreCase) { "title", "buckets", "items", "explanation", "ref", "layoutOverrides" },
        ["question:match"] = new(StringComparer.OrdinalIgnoreCase) { "title", "pairs", "distractors", "explanation", "ref", "layoutOverrides" },
        ["question:numeric"] = new(StringComparer.OrdinalIgnoreCase) { "text", "prompt", "answer", "tolerance", "toleranceMode", "unit", "placeholder", "imagePath", "explanation", "ref", "layoutOverrides" }
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

        var schemaKey = GetSchemaKey(target);

        if (command.SetPairs is not null)
        {
            foreach (var pair in command.SetPairs)
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                    return new Error("Patch.InvalidSet", $"Invalid --set '{pair}'. Expected key=value.", ErrorType.Validation);

                var keyPath = pair[..eq].Trim();
                var rawVal = pair[(eq + 1)..];
                var setRes = ApplySetPair(patchObj, schemaKey, keyPath, rawVal);
                if (!setRes.IsSuccess) return setRes.Error!;
            }
        }

        if (patchObj.Count == 0 && string.IsNullOrWhiteSpace(command.MoveSpec))
            return new Error("Patch.Empty", "Patch payload contains no fields to update. Provide at least one field, --set k=v, or --move <idx>:<shift|exchange>.", ErrorType.Validation);

        if (AllowedKeysByKind.TryGetValue(schemaKey, out var allowedKeys))
        {
            foreach (var kv in patchObj)
            {
                if (!allowedKeys.Contains(kv.Key))
                {
                    var hint = TextSimilarity.FindClosest(kv.Key, allowedKeys);
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
        var existingAssets = ApplySpecHandler.CaptureExistingAssets(target.StorageDirectory);
        bool persisted = false;

        try
        {
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

            if (target.Session is not null && string.IsNullOrWhiteSpace(command.MoveSpec))
            {
                switch (target.EntityKind)
                {
                    case "session":
                        await repository.UpdateSessionMetadataAsync(target.Session, cancellationToken);
                        break;
                    case "component":
                        await repository.UpdateComponentAsync(target.Session, target.Component!, cancellationToken);
                        break;
                    case "articleBlock":
                        await repository.UpdateArticleBlockAsync(target.Session, target.ArticleBlock!, cancellationToken);
                        break;
                    case "question":
                        await repository.UpdateQuestionAsync(
                            new QuizOwnerContext(target.Session, target.Component, target.Quiz!, target.StorageDirectory),
                            target.Question!,
                            cancellationToken);
                        break;
                    default:
                        await repository.SaveSessionAsync(target.Session, cancellationToken);
                        break;
                }
            }
            else if (target.Quiz is not null && string.IsNullOrWhiteSpace(command.MoveSpec))
            {
                switch (target.EntityKind)
                {
                    case "quiz":
                        await repository.UpdateQuizMetadataAsync(target.Quiz, cancellationToken);
                        break;
                    case "question":
                        await repository.UpdateQuestionAsync(
                            new QuizOwnerContext(null, null, target.Quiz, target.StorageDirectory),
                            target.Question!,
                            cancellationToken);
                        break;
                    default:
                        await repository.SaveQuizAsync(target.Quiz, cancellationToken);
                        break;
                }
            }
            else if (target.Session is not null)
            {
                await repository.SaveSessionAsync(target.Session, cancellationToken);
            }
            else if (target.Quiz is not null)
            {
                await repository.SaveQuizAsync(target.Quiz, cancellationToken);
            }

            persisted = true;
            return new PatchEntityResponse(
                target.EntityId,
                target.EntityKind,
                target.WorkspaceId,
                newIndex,
                updatedFields.Count > 0 ? updatedFields : null);
        }
        finally
        {
            if (!persisted)
                ApplySpecHandler.CleanupNewlyAddedAssets(target.StorageDirectory, existingAssets);
        }
    }

    private static string GetSchemaKey(ResolvedEntity target) => target.EntityKind switch
    {
        "articleBlock" => target.ArticleBlock!.Kind switch
        {
            ArticleBlockKind.Image => "articleBlock:image",
            ArticleBlockKind.Callout => "articleBlock:callout",
            ArticleBlockKind.Table => "articleBlock:table",
            ArticleBlockKind.DiagramExplore => "articleBlock:diagram-explore",
            ArticleBlockKind.Reveal => "articleBlock:reveal",
            _ => "articleBlock:text"
        },
        "question" => $"question:{Question.ToWireString(target.Question!.Type)}",
        _ => target.EntityKind
    };

    private static Result ApplySetPair(JsonObject root, string schemaKey, string keyPath, string rawVal)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
            return new Error("Patch.InvalidSet", "Invalid --set key: key path cannot be empty.", ErrorType.Validation);

        var dot = keyPath.IndexOf('.');
        if (dot < 0)
        {
            var parsedVal = ParseSetValueForField(schemaKey, keyPath, null, rawVal);
            var existingKey = FindKeyIgnoreCase(root, keyPath);
            root[existingKey ?? keyPath] = parsedVal;
            return Result.Success();
        }

        var parentKey = keyPath[..dot].Trim();
        var childKey = keyPath[(dot + 1)..].Trim();
        if (string.IsNullOrEmpty(parentKey) || string.IsNullOrEmpty(childKey))
            return new Error("Patch.InvalidSet", $"Invalid dotted --set key '{keyPath}'. Both parent and child segments must be non-empty.", ErrorType.Validation);

        var parsedNestedVal = ParseSetValueForField(schemaKey, parentKey, childKey, rawVal);

        var existingParentKey = FindKeyIgnoreCase(root, parentKey);
        var effectiveParentKey = existingParentKey ?? parentKey;
        if (root[effectiveParentKey] is not JsonObject childObj)
        {
            childObj = [];
            root[effectiveParentKey] = childObj;
        }

        var existingChildKey = FindKeyIgnoreCase(childObj, childKey);
        childObj[existingChildKey ?? childKey] = parsedNestedVal;
        return Result.Success();
    }

    private static string? FindKeyIgnoreCase(JsonObject obj, string targetKey)
    {
        foreach (var kv in obj)
        {
            if (string.Equals(kv.Key, targetKey, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
    }

    private static JsonNode? ParseSetValueForField(string schemaKey, string topKey, string? childKey, string rawVal)
    {
        var trimmed = rawVal.Trim();
        var topLower = topKey.ToLowerInvariant();
        var childLower = childKey?.ToLowerInvariant();

        if (childLower is not null)
        {
            return string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)
                ? null
                : JsonValue.Create(rawVal);
        }

        if (schemaKey == "articleBlock:table" && topLower == "hideablecols")
        {
            if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(trimmed))
                return new JsonArray();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                try { return JsonNode.Parse(trimmed); } catch { }
            }
            var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var arr = new JsonArray();
            foreach (var p in parts)
            {
                if (int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int colIdx))
                    arr.Add(colIdx);
                else
                    return JsonValue.Create(rawVal);
            }
            return arr;
        }

        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return null;

        if (schemaKey == "question:mcq")
        {
            if (topLower == "ismulticorrect")
            {
                if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
                if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
                return JsonValue.Create(rawVal);
            }
            if (topLower == "maxoptions")
            {
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
                    return JsonValue.Create(intVal);
                return JsonValue.Create(rawVal);
            }
        }

        if ((schemaKey == "articleBlock:table" && topLower == "stickyfirstcol") ||
            (schemaKey == "articleBlock:reveal" && topLower == "showfirst") ||
            (schemaKey == "question:fill-blank" && topLower is "fuzzy" or "casesensitive") ||
            (schemaKey == "question:spot-error" && topLower is "requirefix" or "allownoerror" or "noerror") ||
            (schemaKey == "question:diff" && topLower is "bank" or "fuzzy" or "casesensitive" or "allownoerror") ||
            (schemaKey == "question:relabel" && topLower is "fuzzy" or "casesensitive"))
        {
            if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
            if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
            return JsonValue.Create(rawVal);
        }

        if (schemaKey == "question:numeric" && topLower == "answer")
        {
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double dVal))
                return JsonValue.Create(dVal);
            return JsonValue.Create(rawVal);
        }

        if (topLower is "options" or "layoutoverrides" or "front" or "back" or "headers" or "alignments" or "rows" or "steps" or "boxes" or "distractors" or "hotspots" or "buckets" or "items" or "pairs" or "questions")
        {
            if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
            {
                try { return JsonNode.Parse(trimmed); } catch { }
            }
            return JsonValue.Create(rawVal);
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
        if (block.Kind == ArticleBlockKind.Table)
        {
            return ApplyTableBlockPatch(block, patch);
        }
        if (block.Kind == ArticleBlockKind.DiagramExplore)
        {
            return await ApplyDiagramExploreBlockPatchAsync(block, storageDir, jsonDir, patch, ct);
        }
        if (block.Kind == ArticleBlockKind.Reveal)
        {
            return ApplyRevealBlockPatch(block, patch);
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
            var imgRes = await TryCopyPatchedImageAsync(imgNode, "imagePath", allowNull: false, "'imagePath' must be a non-empty string.", storageDir, jsonDir, ct);
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

    private async Task<Result<string?>> TryCopyPatchedImageAsync(
        JsonNode? imgNode,
        string path,
        bool allowNull,
        string invalidMsg,
        string storageDir,
        string jsonDir,
        CancellationToken ct,
        bool treatWhitespaceAsNull = true)
    {
        if (imgNode is null)
            return allowNull ? (string?)null : new Error("Patch.InvalidValue", invalidMsg, ErrorType.Validation, Path: path);
        if (imgNode.GetValueKind() != JsonValueKind.String)
            return new Error("Patch.InvalidValue", allowNull ? "'imagePath' must be a string or null." : invalidMsg, ErrorType.Validation, Path: path);

        var rawImg = imgNode.GetValue<string>();
        if (string.IsNullOrWhiteSpace(rawImg))
            return allowNull && treatWhitespaceAsNull ? (string?)null : new Error("Patch.InvalidValue", invalidMsg, ErrorType.Validation, Path: path);

        var copyRes = await JsonQuestionMaterializer.CopyConfinedImageAsync(
            rawImg, jsonDir, storageDir, contentInspector, ct,
            workspaceCwdDirectory: workingDirectoryProvider.GetCurrentDirectory());
        if (!copyRes.IsSuccess) return copyRes.Error!;
        if (!allowNull && string.IsNullOrWhiteSpace(copyRes.Value))
            return new Error("Patch.InvalidValue", "'imagePath' must resolve to a valid image.", ErrorType.Validation, Path: path);
        return copyRes.Value;
    }

    private static Result PatchTableGridFields(
        JsonObject patch,
        int minHeaders,
        string ownerLabel,
        ref IReadOnlyList<string> headers,
        ref IReadOnlyList<string> alignments,
        ref IReadOnlyList<IReadOnlyList<string>> rows,
        out bool recomputeSpecial)
    {
        recomputeSpecial = false;
        if (TryGetProp(patch, "headers", out var hdrNode))
        {
            if (hdrNode is not JsonArray hdrArr || hdrArr.Count < minHeaders)
                return new Error("Patch.InvalidValue", minHeaders == 1 ? "'headers' must be a non-empty array of strings." : $"'headers' must be an array of at least {minHeaders} column strings.", ErrorType.Validation, Path: "headers");
            var parsed = new List<string>(hdrArr.Count);
            foreach (var item in hdrArr)
            {
                if (item is null || item.GetValueKind() != JsonValueKind.String)
                    return new Error("Patch.InvalidValue", "'headers' entries must be strings.", ErrorType.Validation, Path: "headers");
                parsed.Add(item.GetValue<string>());
            }
            if (parsed.All(string.IsNullOrWhiteSpace))
                return new Error("Patch.InvalidValue", "'headers' must contain at least one non-whitespace column label.", ErrorType.Validation, Path: "headers");
            headers = parsed;
            if (!TryGetProp(patch, "alignments", out _) && alignments.Count != headers.Count)
                alignments = Enumerable.Repeat("left", headers.Count).ToList();
            recomputeSpecial = true;
        }
        if (TryGetProp(patch, "alignments", out var alignNode))
        {
            if (alignNode is not JsonArray alignArr)
                return new Error("Patch.InvalidValue", "'alignments' must be an array of 'left', 'center', or 'right'.", ErrorType.Validation, Path: "alignments");
            var parsed = new List<string>(alignArr.Count);
            foreach (var item in alignArr)
            {
                if (item is null || item.GetValueKind() != JsonValueKind.String)
                    return new Error("Patch.InvalidValue", "'alignments' entries must be strings.", ErrorType.Validation, Path: "alignments");
                var val = item.GetValue<string>().Trim().ToLowerInvariant();
                if (val is not ("left" or "center" or "right"))
                    return new Error("Patch.InvalidValue", $"Invalid alignment '{val}'. Use 'left', 'center', or 'right'.", ErrorType.Validation, Path: "alignments");
                parsed.Add(val);
            }
            if (parsed.Count != headers.Count)
                return new Error("Patch.InvalidValue", $"'alignments' length ({parsed.Count}) must match 'headers' length ({headers.Count}).", ErrorType.Validation, Path: "alignments");
            alignments = parsed;
        }
        if (TryGetProp(patch, "rows", out var rowsNode))
        {
            if (rowsNode is not JsonArray rowsArr || rowsArr.Count == 0)
                return new Error("Patch.InvalidValue", "'rows' must be a non-empty array of string arrays.", ErrorType.Validation, Path: "rows");
            var parsedRows = new List<IReadOnlyList<string>>(rowsArr.Count);
            foreach (var rItem in rowsArr)
            {
                if (rItem is not JsonArray cellArr)
                    return new Error("Patch.InvalidValue", "Each row in 'rows' must be an array of strings.", ErrorType.Validation, Path: "rows");
                var row = new List<string>(cellArr.Count);
                foreach (var cItem in cellArr)
                {
                    if (cItem is null || cItem.GetValueKind() != JsonValueKind.String)
                        return new Error("Patch.InvalidValue", $"{ownerLabel} cell values must be strings.", ErrorType.Validation, Path: "rows");
                    row.Add(cItem.GetValue<string>());
                }
                parsedRows.Add(row);
            }
            rows = parsedRows;
            recomputeSpecial = true;
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

        if (question is FlashcardQuestion card)
            return await ApplyFlashcardPatchAsync(card, target.StorageDirectory, jsonDir, patch, ct);

        if (question is PayloadQuestion pq)
            return await ApplyPayloadQuestionPatchAsync(pq, target.StorageDirectory, jsonDir, patch, ct);

        return new Error("Patch.UnsupportedTarget", $"Unsupported question type '{question.Type}'.", ErrorType.Validation);
    }

    private static Result ApplyTableBlockPatch(ArticleBlock block, JsonObject patch)
    {
        if (!Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(block.PayloadJson, out var current) || current is null)
            return ArticleErrors.InvalidTablePayload("Existing table block has corrupted payload JSON.");

        var title = current.Title;
        var caption = current.Caption;
        var sticky = current.StickyFirstCol;
        var hideableCols = current.HideableCols ?? [];
        var headers = current.Headers ?? [];
        var alignments = current.Alignments ?? [];
        var rows = current.Rows ?? [];
        var footnote = current.Footnote;
        bool updateTitle = false;

        if (TryReadNullableString(patch, "title", out var titleVal, out var titleErr)) { title = titleVal; updateTitle = true; }
        else if (titleErr is not null) return titleErr;
        if (TryReadNullableString(patch, "caption", out var capVal, out var capErr)) caption = capVal;
        else if (capErr is not null) return capErr;
        if (TryReadNullableString(patch, "footnote", out var fnVal, out var fnErr)) footnote = fnVal;
        else if (fnErr is not null) return fnErr;
        if (TryReadBool(patch, "stickyFirstCol", out var stickyVal, out var stickyErr)) sticky = stickyVal;
        else if (stickyErr is not null) return stickyErr;

        if (TryGetProp(patch, "hideableCols", out var hcNode))
        {
            if (hcNode is not JsonArray hcArr)
                return new Error("Patch.InvalidValue", "'hideableCols' must be an array of 1-based column integers.", ErrorType.Validation, Path: "hideableCols");
            var cols = new List<int>(hcArr.Count);
            foreach (var item in hcArr)
            {
                if (item is not JsonValue iv || !iv.TryGetValue<int>(out int col) || col < 1)
                    return new Error("Patch.InvalidValue", "'hideableCols' entries must be positive integers.", ErrorType.Validation, Path: "hideableCols");
                if (!cols.Contains(col)) cols.Add(col);
            }
            hideableCols = cols;
        }

        var gridRes = PatchTableGridFields(patch, 1, "Table", ref headers, ref alignments, ref rows, out _);
        if (!gridRes.IsSuccess) return gridRes.Error!;

        for (int r = 0; r < rows.Count; r++)
        {
            if (rows[r] is null || rows[r].Count != headers.Count)
                return new Error("Patch.InvalidValue", $"Table row {r + 1} has {rows[r]?.Count ?? 0} cell(s), expected {headers.Count} to match 'headers'.", ErrorType.Validation, Path: "rows");
        }

        foreach (var col in hideableCols)
        {
            if (col < 1 || col > headers.Count)
                return new Error("Patch.InvalidValue", $"'hideableCols' index {col} is out of bounds (1..{headers.Count}).", ErrorType.Validation, Path: "hideableCols");
        }

        var updated = new TableBlockPayload(title, caption, sticky, hideableCols, headers, alignments, rows, footnote);
        return block.UpdateTablePayload(Phase1PayloadCodec.Serialize(updated), title, updateTitle);
    }

    private async Task<Result> ApplyDiagramExploreBlockPatchAsync(
        ArticleBlock block,
        string storageDir,
        string jsonDir,
        JsonObject patch,
        CancellationToken ct)
    {
        if (!Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(block.PayloadJson, out var current) || current is null)
            return ArticleErrors.InvalidDiagramExplorePayload("Existing diagram-explore block has corrupted payload JSON.");

        var imagePath = !string.IsNullOrWhiteSpace(current.ImagePath) ? current.ImagePath : (block.ImagePath ?? string.Empty);
        var title = current.Title ?? block.CalloutTitle;
        var caption = current.Caption ?? block.Caption;
        var alt = block.Alt;
        var hotspots = current.Hotspots ?? [];
        bool updateTitle = false, updateCaption = false, updateAlt = false;

        if (TryReadNullableString(patch, "title", out var tVal, out var tErr)) { title = tVal; updateTitle = true; }
        else if (tErr is not null) return tErr;
        if (TryReadNullableString(patch, "caption", out var cVal, out var cErr)) { caption = cVal; updateCaption = true; }
        else if (cErr is not null) return cErr;
        if (TryReadNullableString(patch, "alt", out var aVal, out var aErr)) { alt = aVal; updateAlt = true; }
        else if (aErr is not null) return aErr;

        if (TryGetProp(patch, "imagePath", out var imgN))
        {
            var imgRes = await TryCopyPatchedImageAsync(imgN, "imagePath", allowNull: false, "'imagePath' must be a non-empty string.", storageDir, jsonDir, ct);
            if (!imgRes.IsSuccess) return imgRes.Error!;
            imagePath = imgRes.Value ?? imagePath;
        }
        if (TryGetProp(patch, "hotspots", out var hsN))
        {
            if (hsN is not JsonArray hArr || hArr.Count == 0)
                return new Error("Patch.InvalidValue", "'hotspots' must be a non-empty array.", ErrorType.Validation, Path: "hotspots");
            var parsed = new List<DiagramHotspot>(hArr.Count);
            for (int i = 0; i < hArr.Count; i++)
            {
                if (hArr[i] is not JsonObject ho)
                    return new Error("Patch.InvalidValue", $"Hotspot at index {i} must specify 'label' and 'note' strings.", ErrorType.Validation, Path: "hotspots");

                if (!TryReadRequiredString(ho, "label", out var labelStr, out _, trim: true) ||
                    !TryReadRequiredString(ho, "note", out var noteStr, out _, trim: true))
                {
                    if (!TryGetProp(ho, "label", out var lN) || lN is null || lN.GetValueKind() != JsonValueKind.String ||
                        !TryGetProp(ho, "note", out var nN) || nN is null || nN.GetValueKind() != JsonValueKind.String)
                        return new Error("Patch.InvalidValue", $"Hotspot at index {i} must specify 'label' and 'note' strings.", ErrorType.Validation, Path: "hotspots");
                    labelStr = lN.GetValue<string>().Trim();
                    noteStr = nN.GetValue<string>().Trim();
                }

                var rectRes = ReadNormalizedRect(ho, $"hotspots[{i}]");
                if (!rectRes.IsSuccess) return rectRes.Error!;

                var candidate = new DiagramHotspot(i, labelStr, noteStr, rectRes.Value.X, rectRes.Value.Y, rectRes.Value.W, rectRes.Value.H);
                if (!Phase3PayloadRules.HasValidHotspotBounds(candidate))
                    return new Error("Patch.InvalidValue", $"Hotspot at index {i} bounds are out of [0..1] range.", ErrorType.Validation, Path: "hotspots");
                parsed.Add(candidate);
            }
            hotspots = parsed;
        }

        var canonical = Phase1PayloadCodec.Serialize(new DiagramExplorePayload(imagePath, title, caption, hotspots));
        return block.UpdateDiagramExplorePayload(imagePath, canonical, title, updateTitle, caption, updateCaption, alt, updateAlt);
    }

    private static Result ApplyRevealBlockPatch(ArticleBlock block, JsonObject patch)
    {
        if (!Phase1PayloadCodec.TryDeserialize<RevealBlockPayload>(block.PayloadJson, out var current) || current is null)
            return ArticleErrors.InvalidRevealPayload("Existing reveal block has corrupted payload JSON.");

        var title = current.Title ?? block.CalloutTitle;
        var showFirst = current.ShowFirst;
        var steps = current.Steps ?? [];
        bool updateTitle = false;

        if (TryReadNullableString(patch, "title", out var tVal, out var tErr)) { title = tVal; updateTitle = true; }
        else if (tErr is not null) return tErr;
        if (TryReadBool(patch, "showFirst", out var sfVal, out var sfErr)) showFirst = sfVal;
        else if (sfErr is not null) return sfErr;
        if (TryGetProp(patch, "steps", out var stN))
        {
            if (stN is not JsonArray stArr || stArr.Count == 0)
                return new Error("Patch.InvalidValue", "'steps' must be a non-empty array of {heading, body}.", ErrorType.Validation, Path: "steps");
            var parsed = new List<RevealStep>(stArr.Count);
            for (int i = 0; i < stArr.Count; i++)
            {
                if (stArr[i] is not JsonObject so ||
                    !TryGetProp(so, "heading", out var hN) || hN is null || hN.GetValueKind() != JsonValueKind.String ||
                    !TryGetProp(so, "body", out var bN) || bN is null || bN.GetValueKind() != JsonValueKind.String)
                    return new Error("Patch.InvalidValue", $"Reveal step at index {i} must specify 'heading' and 'body' strings.", ErrorType.Validation, Path: "steps");
                parsed.Add(new RevealStep(i, hN.GetValue<string>().Trim(), bN.GetValue<string>().Trim()));
            }
            steps = parsed;
        }

        return block.UpdateRevealPayload(Phase1PayloadCodec.Serialize(new RevealBlockPayload(title, showFirst, steps)), title, updateTitle);
    }

    private async Task<Result> ApplyPayloadQuestionPatchAsync(PayloadQuestion pq, string storageDir, string jsonDir, JsonObject patch, CancellationToken ct)
    {
        switch (pq.Type)
        {
            case QuestionType.FillBlank:
            {
                if (!Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(pq.PayloadJson, out var current) || current is null)
                    return new Error("Patch.InvalidValue", "Existing fill-blank question has corrupted payload JSON.", ErrorType.Validation);

                var template = current.Template;
                var blanks = current.Blanks ?? [];
                var fuzzy = current.Fuzzy;
                var caseSensitive = current.CaseSensitive;
                var explanation = current.Explanation;
                var placeholder = current.Placeholder;

                if (TryGetProp(patch, "text", out var textNode))
                {
                    if (textNode is null || textNode.GetValueKind() != JsonValueKind.String)
                        return new Error("Patch.InvalidValue", "'text' must be a non-empty string.", ErrorType.Validation, Path: "text");
                    template = textNode.GetValue<string>();
                    var slotsRes = Phase1PayloadRules.ParseFillBlankTemplate(template);
                    if (!slotsRes.IsSuccess) return slotsRes.Error!;
                    blanks = slotsRes.Value;
                }
                if (TryReadBool(patch, "fuzzy", out var fv, out var fErr)) fuzzy = fv;
                else if (fErr is not null) return fErr;
                if (TryReadBool(patch, "caseSensitive", out var csVal, out var csErr)) caseSensitive = csVal;
                else if (csErr is not null) return csErr;
                if (TryReadNullableString(patch, "placeholder", out var phVal, out var phErr)) placeholder = phVal;
                else if (phErr is not null) return phErr;
                if (TryReadNullableString(patch, "explanation", out var expVal, out var expErr)) explanation = expVal;
                else if (expErr is not null) return expErr;

                return pq.SetPayloadJson(Phase1PayloadCodec.Serialize(new FillBlankPayload(template, blanks, fuzzy, caseSensitive, explanation, placeholder)));
            }
            case QuestionType.AssertionReason:
            {
                if (!Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(pq.PayloadJson, out var current) || current is null)
                    return new Error("Patch.InvalidValue", "Existing assertion-reason question has corrupted payload JSON.", ErrorType.Validation);

                var assertion = current.Assertion;
                var reason = current.Reason;
                var answer = current.Answer;
                var scheme = current.Scheme;
                var explanation = current.Explanation;
                bool schemeExplicit = false;

                if (TryReadRequiredString(patch, "assertion", out var aVal, out var aErr)) assertion = aVal;
                else if (aErr is not null) return aErr;
                if (TryReadRequiredString(patch, "reason", out var rVal, out var rErr)) reason = rVal;
                else if (rErr is not null) return rErr;
                if (TryGetProp(patch, "answer", out var ansNode))
                {
                    if (ansNode is null || ansNode.GetValueKind() != JsonValueKind.String)
                        return new Error("Patch.InvalidValue", "'answer' must be a valid assertion-reason enum string.", ErrorType.Validation, Path: "answer");
                    var rawAns = Phase1PayloadRules.NormalizeAssertionReasonAnswer(ansNode.GetValue<string>());
                    if (!Phase1PayloadRules.IsValidAssertionReasonAnswer(rawAns))
                        return new Error("Patch.InvalidValue", $"Invalid answer '{rawAns}'. Allowed: {string.Join(", ", Phase1PayloadRules.AssertionReasonAnswers)}.", ErrorType.Validation, Path: "answer");
                    answer = rawAns;
                }
                if (TryGetProp(patch, "scheme", out var schNode))
                {
                    if (schNode is null || schNode.GetValueKind() != JsonValueKind.String)
                        return new Error("Patch.InvalidValue", "'scheme' must be '4opt' or '5opt'.", ErrorType.Validation, Path: "scheme");
                    var rawSch = schNode.GetValue<string>().Trim().ToLowerInvariant();
                    if (!Phase1PayloadRules.IsValidAssertionReasonScheme(rawSch))
                        return new Error("Patch.InvalidValue", $"Invalid scheme '{rawSch}'. Use '4opt' or '5opt'.", ErrorType.Validation, Path: "scheme");
                    scheme = rawSch;
                    schemeExplicit = true;
                }
                if (answer == "both-false" && scheme == "4opt")
                {
                    if (schemeExplicit)
                        return new Error("Patch.InvalidValue", "Answer 'both-false' requires scheme '5opt'.", ErrorType.Validation, Path: "scheme");
                    scheme = "5opt";
                }
                if (TryReadNullableString(patch, "explanation", out var expVal, out var expErr)) explanation = expVal;
                else if (expErr is not null) return expErr;

                return pq.SetPayloadJson(Phase1PayloadCodec.Serialize(new AssertionReasonPayload(assertion, reason, answer, scheme, explanation)));
            }
            case QuestionType.SpotError:
            {
                if (!Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(pq.PayloadJson, out var current) || current is null)
                    return new Error("Patch.InvalidValue", "Existing spot-error question has corrupted payload JSON.", ErrorType.Validation);

                var template = current.Template;
                var segments = current.Segments ?? [];
                var requireFix = current.RequireFix;
                var allowNoError = current.AllowNoError;
                var noError = current.NoError;
                var explanation = current.Explanation;
                var imagePath = current.ImagePath;

                if (TryGetProp(patch, "text", out var textNode))
                {
                    if (textNode is null || textNode.GetValueKind() != JsonValueKind.String)
                        return new Error("Patch.InvalidValue", "'text' must be a non-empty string.", ErrorType.Validation, Path: "text");
                    template = textNode.GetValue<string>();
                    var segsRes = Phase1PayloadRules.ParseSpotErrorTemplate(template);
                    if (!segsRes.IsSuccess) return segsRes.Error!;
                    segments = segsRes.Value;
                }
                if (TryGetProp(patch, "imagePath", out var imgNode))
                {
                    var imgRes = await TryCopyPatchedImageAsync(imgNode, "imagePath", allowNull: true, "'imagePath' must be a string or null.", storageDir, jsonDir, ct);
                    if (!imgRes.IsSuccess) return imgRes.Error!;
                    imagePath = imgRes.Value;
                }
                if (TryReadBool(patch, "requireFix", out var rfVal, out var rfErr)) requireFix = rfVal;
                else if (rfErr is not null) return rfErr;
                bool allowNoErrorExplicit = TryReadBool(patch, "allowNoError", out var aneVal, out var aneErr);
                if (aneErr is not null) return aneErr;
                if (allowNoErrorExplicit) allowNoError = aneVal;
                if (TryReadBool(patch, "noError", out var neVal, out var neErr))
                {
                    noError = neVal;
                    if (noError && !allowNoErrorExplicit) allowNoError = true;
                }
                else if (neErr is not null) return neErr;
                if (noError && !allowNoError)
                    return new Error("Patch.InvalidValue", "Spot-the-error question cannot have 'noError=true' with 'allowNoError=false'.", ErrorType.Validation, Path: "allowNoError");
                if (TryReadNullableString(patch, "explanation", out var expVal, out var expErr)) explanation = expVal;
                else if (expErr is not null) return expErr;

                return pq.SetPayloadJson(Phase1PayloadCodec.Serialize(new SpotErrorPayload(template, segments, requireFix, allowNoError, noError, explanation, imagePath)));
            }
            case QuestionType.Differentiation:
            {
                if (!Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(pq.PayloadJson, out var current) || current is null)
                    return new Error("Patch.InvalidValue", "Existing differentiation question has corrupted payload JSON.", ErrorType.Validation);

                var mode = current.Mode;
                var title = current.Title;
                var bank = current.Bank;
                var fuzzy = current.Fuzzy;
                var caseSensitive = current.CaseSensitive;
                var allowNoError = current.AllowNoError;
                var headers = current.Headers ?? [];
                var alignments = current.Alignments ?? [];
                var rows = current.Rows ?? [];
                var specialCells = current.SpecialCells ?? [];
                var explanation = current.Explanation;
                bool recomputeSpecial = false;

                if (TryGetProp(patch, "mode", out var modeNode))
                {
                    if (modeNode is null || modeNode.GetValueKind() != JsonValueKind.String)
                        return new Error("Patch.InvalidValue", "'mode' must be 'fill', 'spot-wrong', or 'rearrange'.", ErrorType.Validation, Path: "mode");
                    var normMode = Phase2PayloadRules.NormalizeDiffMode(modeNode.GetValue<string>());
                    if (!Phase2PayloadRules.IsValidDiffMode(normMode))
                        return new Error("Patch.InvalidValue", $"Invalid differentiation mode '{normMode}'. Use 'fill', 'spot-wrong', or 'rearrange'.", ErrorType.Validation, Path: "mode");
                    mode = normMode;
                    recomputeSpecial = true;
                }
                if (TryReadNullableString(patch, "title", out var tVal, out var tErr)) title = tVal;
                else if (tErr is not null) return tErr;
                if (TryReadBool(patch, "bank", out var bVal, out var bErr)) bank = bVal;
                else if (bErr is not null) return bErr;
                if (TryReadBool(patch, "fuzzy", out var fVal, out var fErr)) fuzzy = fVal;
                else if (fErr is not null) return fErr;
                if (TryReadBool(patch, "caseSensitive", out var csVal, out var csErr)) caseSensitive = csVal;
                else if (csErr is not null) return csErr;
                if (TryReadBool(patch, "allowNoError", out var aneVal, out var aneErr)) allowNoError = aneVal;
                else if (aneErr is not null) return aneErr;

                var gridRes = PatchTableGridFields(patch, 2, "Differentiation", ref headers, ref alignments, ref rows, out bool gridChanged);
                if (!gridRes.IsSuccess) return gridRes.Error!;
                if (gridChanged) recomputeSpecial = true;

                if (TryReadNullableString(patch, "explanation", out var expVal, out var expErr)) explanation = expVal;
                else if (expErr is not null) return expErr;

                if (headers.Count < 2)
                    return new Error("Patch.InvalidValue", "Differentiation table requires at least 2 columns.", ErrorType.Validation, Path: "headers");
                if (mode == "rearrange" && headers.Count < 3)
                    return new Error("Patch.InvalidValue", "Differentiation mode 'rearrange' requires at least 3 columns.", ErrorType.Validation, Path: "headers");
                for (int r = 0; r < rows.Count; r++)
                {
                    if (rows[r] is null || rows[r].Count != headers.Count)
                        return new Error("Patch.InvalidValue", $"Differentiation row {r + 1} has {rows[r]?.Count ?? 0} cell(s), expected {headers.Count}.", ErrorType.Validation, Path: "rows");
                }

                if (recomputeSpecial)
                {
                    var specialRes = Phase2PayloadRules.ParseDifferentiationTable(mode, headers, rows);
                    if (!specialRes.IsSuccess) return specialRes.Error!;
                    specialCells = specialRes.Value;
                }

                return pq.SetPayloadJson(Phase1PayloadCodec.Serialize(new DifferentiationPayload(
                    mode, title, bank, fuzzy, caseSensitive, allowNoError, headers, alignments, rows, specialCells, explanation)));
            }
            case QuestionType.Mechanism:
            {
                if (!Phase1PayloadCodec.TryDeserialize<MechanismPayload>(pq.PayloadJson, out var current) || current is null)
                    return new Error("Patch.InvalidValue", "Existing mechanism question has corrupted payload JSON.", ErrorType.Validation);

                var title = current.Title;
                var steps = current.Steps ?? [];
                var explanation = current.Explanation;

                if (TryReadNullableString(patch, "title", out var tVal, out var tErr)) title = tVal;
                else if (tErr is not null) return tErr;
                if (TryGetProp(patch, "steps", out var stepsNode))
                {
                    if (stepsNode is not JsonArray stepsArr || stepsArr.Count < 2)
                        return new Error("Patch.InvalidValue", "'steps' must be an array with at least 2 step objects.", ErrorType.Validation, Path: "steps");

                    var allowedStepKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stepId", "orderGroup", "text", "isDistractor" };
                    var parsedSteps = new List<MechanismStep>(stepsArr.Count);
                    for (int sIdx = 0; sIdx < stepsArr.Count; sIdx++)
                    {
                        if (stepsArr[sIdx] is not JsonObject sObj)
                            return new Error("Patch.InvalidValue", $"Step at index {sIdx} must be a JSON object.", ErrorType.Validation, Path: "steps");
                        foreach (var kv in sObj)
                        {
                            if (!allowedStepKeys.Contains(kv.Key))
                                return new Error("Patch.UnknownField", $"Unknown field '{kv.Key}' on mechanism step.", ErrorType.Validation, Path: "steps");
                        }

                        var rawText = TryGetProp(sObj, "text", out var tNode) && tNode?.GetValueKind() == JsonValueKind.String
                            ? tNode.GetValue<string>().Trim()
                            : string.Empty;
                        if (string.IsNullOrEmpty(rawText))
                            return new Error("Patch.InvalidValue", $"Mechanism step at index {sIdx} requires non-empty 'text'.", ErrorType.Validation, Path: "steps");

                        bool isDistractor = false;
                        if (TryReadBool(sObj, "isDistractor", out var dVal, out var dErr)) isDistractor = dVal;
                        else if (dErr is not null) return new Error("Patch.InvalidValue", "'isDistractor' must be a boolean.", ErrorType.Validation, Path: "steps");

                        int orderGroup = 0;
                        if (!isDistractor)
                        {
                            if (!TryGetProp(sObj, "orderGroup", out var ogNode) || ogNode is not JsonValue ov || !ov.TryGetValue<int>(out orderGroup) || orderGroup < 1)
                                return new Error("Patch.InvalidValue", $"Ordered mechanism step at index {sIdx} requires integer 'orderGroup' >= 1.", ErrorType.Validation, Path: "steps");
                        }

                        parsedSteps.Add(new MechanismStep(sIdx + 1, isDistractor ? 0 : orderGroup, rawText, isDistractor));
                    }

                    if (parsedSteps.Count(s => !s.IsDistractor) < 2)
                        return new Error("Patch.InvalidValue", "Mechanism question requires at least 2 non-distractor ordered steps.", ErrorType.Validation, Path: "steps");
                    steps = parsedSteps;
                }
                if (TryReadNullableString(patch, "explanation", out var expVal, out var expErr)) explanation = expVal;
                else if (expErr is not null) return expErr;

                return pq.SetPayloadJson(Phase1PayloadCodec.Serialize(new MechanismPayload(title, steps, explanation)));
            }
            case QuestionType.RelabelImage:
            {
                if (!Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(pq.PayloadJson, out var current) || current is null)
                    return new Error("Patch.InvalidValue", "Existing relabel question has corrupted payload JSON.", ErrorType.Validation);

                var imagePath = current.ImagePath;
                var prompt = current.Prompt;
                var mode = current.Mode;
                var style = current.Style;
                var boxes = current.Boxes ?? [];
                var distractors = current.Distractors ?? [];
                var fuzzy = current.Fuzzy;
                var caseSensitive = current.CaseSensitive;
                var explanation = current.Explanation;

                if (TryGetProp(patch, "imagePath", out var imgNode))
                {
                    var imgRes = await TryCopyPatchedImageAsync(imgNode, "imagePath", allowNull: false, "'imagePath' must be a non-empty string for relabel questions.", storageDir, jsonDir, ct);
                    if (!imgRes.IsSuccess) return imgRes.Error!;
                    imagePath = imgRes.Value!;
                }
                if (TryReadNullableString(patch, "prompt", out var prVal, out var prErr)) prompt = prVal;
                else if (prErr is not null) return prErr;
                if (TryGetProp(patch, "mode", out var modeNode))
                {
                    if (modeNode is null || modeNode.GetValueKind() != JsonValueKind.String)
                        return new Error("Patch.InvalidValue", "'mode' must be 'reveal', 'bank', or 'recall'.", ErrorType.Validation, Path: "mode");
                    var normMode = Phase2PayloadRules.NormalizeRelabelMode(modeNode.GetValue<string>());
                    if (!Phase2PayloadRules.IsValidRelabelMode(normMode))
                        return new Error("Patch.InvalidValue", $"Invalid relabel mode '{normMode}'. Use 'reveal', 'bank', or 'recall'.", ErrorType.Validation, Path: "mode");
                    mode = normMode;
                }
                if (TryGetProp(patch, "style", out var styleNode))
                {
                    if (styleNode is null || styleNode.GetValueKind() != JsonValueKind.String)
                        return new Error("Patch.InvalidValue", "'style' must be 'solid', 'blur', 'pixelate', or 'whiteout'.", ErrorType.Validation, Path: "style");
                    var normStyle = Phase2PayloadRules.NormalizeRelabelStyle(styleNode.GetValue<string>());
                    if (!Phase2PayloadRules.IsValidRelabelStyle(normStyle))
                        return new Error("Patch.InvalidValue", $"Invalid relabel style '{normStyle}'. Use 'solid', 'blur', 'pixelate', or 'whiteout'.", ErrorType.Validation, Path: "style");
                    style = normStyle;
                }
                if (TryReadBool(patch, "fuzzy", out var fVal, out var fErr)) fuzzy = fVal;
                else if (fErr is not null) return fErr;
                if (TryReadBool(patch, "caseSensitive", out var csVal, out var csErr)) caseSensitive = csVal;
                else if (csErr is not null) return csErr;
                if (TryReadNullableString(patch, "explanation", out var expVal, out var expErr)) explanation = expVal;
                else if (expErr is not null) return expErr;
                if (TryReadStringArray(patch, "distractors", allowNull: true, allowEmpty: true, out var parsedDist, out var distErr))
                    distractors = (parsedDist ?? []).Where(s => !string.IsNullOrEmpty(s)).ToList();
                else if (distErr is not null) return distErr;

                if (TryGetProp(patch, "boxes", out var boxesNode))
                {
                    if (boxesNode is not JsonArray boxesArr || boxesArr.Count == 0)
                        return new Error("Patch.InvalidValue", "'boxes' must be a non-empty array of box objects.", ErrorType.Validation, Path: "boxes");

                    var allowedBoxKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index", "label", "aliases", "rect", "x", "y", "w", "h" };
                    var parsedBoxes = new List<RelabelBox>(boxesArr.Count);
                    for (int bIdx = 0; bIdx < boxesArr.Count; bIdx++)
                    {
                        if (boxesArr[bIdx] is not JsonObject bObj)
                            return new Error("Patch.InvalidValue", $"Relabel box at index {bIdx} must be a JSON object.", ErrorType.Validation, Path: "boxes");
                        foreach (var kv in bObj)
                        {
                            if (!allowedBoxKeys.Contains(kv.Key))
                                return new Error("Patch.UnknownField", $"Unknown field '{kv.Key}' on relabel box.", ErrorType.Validation, Path: "boxes");
                        }

                        var lbl = TryGetProp(bObj, "label", out var lNode) && lNode?.GetValueKind() == JsonValueKind.String
                            ? lNode.GetValue<string>().Trim()
                            : string.Empty;
                        if (string.IsNullOrEmpty(lbl))
                            return new Error("Patch.InvalidValue", $"Relabel box at index {bIdx} requires non-empty 'label'.", ErrorType.Validation, Path: "boxes");

                        var aliases = new List<string>();
                        if (TryReadStringArray(bObj, "aliases", allowNull: true, allowEmpty: true, out var alParsed, out var alErr))
                        {
                            if (alParsed is not null)
                                aliases.AddRange(alParsed.Where(s => !string.IsNullOrEmpty(s)));
                        }
                        else if (alErr is not null)
                        {
                            return new Error("Patch.InvalidValue", "'aliases' on relabel box must be an array of strings.", ErrorType.Validation, Path: "boxes");
                        }

                        var rectRes = ReadNormalizedRect(bObj, $"boxes[{bIdx}]");
                        if (!rectRes.IsSuccess) return rectRes.Error!;

                        var candidate = new RelabelBox(bIdx + 1, lbl, aliases, rectRes.Value.X, rectRes.Value.Y, rectRes.Value.W, rectRes.Value.H);
                        if (!Phase2PayloadRules.ValidateRelabelBox(candidate))
                            return new Error("Patch.InvalidValue", $"Relabel box at index {bIdx} coordinates are out of normalized [0..1] bounds.", ErrorType.Validation, Path: "boxes");
                        parsedBoxes.Add(candidate);
                    }
                    boxes = parsedBoxes;
                }

                return pq.SetPayloadJson(Phase1PayloadCodec.Serialize(new RelabelImagePayload(
                    imagePath, prompt, mode, style, boxes, distractors, fuzzy, caseSensitive, explanation)));
            }
            case QuestionType.Classify:
            {
                if (!Phase1PayloadCodec.TryDeserialize<ClassifyPayload>(pq.PayloadJson, out var current) || current is null)
                    return new Error("Patch.InvalidValue", "Existing classify question has corrupted payload JSON.", ErrorType.Validation);

                var title = current.Title;
                var buckets = current.Buckets ?? [];
                var items = current.Items ?? [];
                var explanation = current.Explanation;

                if (TryReadNullableString(patch, "title", out var tVal, out var tErr)) title = tVal;
                else if (tErr is not null) return tErr;
                if (TryReadNullableString(patch, "explanation", out var expVal, out var expErr)) explanation = expVal;
                else if (expErr is not null) return expErr;
                if (TryGetProp(patch, "buckets", out var bN))
                {
                    if (bN is not JsonArray bArr || bArr.Count == 0)
                        return new Error("Patch.InvalidValue", "'buckets' must be a non-empty array.", ErrorType.Validation, Path: "buckets");
                    var parsedB = new List<ClassifyBucket>(bArr.Count);
                    for (int i = 0; i < bArr.Count; i++)
                    {
                        var el = bArr[i];
                        if (el is JsonValue sv && sv.GetValueKind() == JsonValueKind.String)
                            parsedB.Add(new ClassifyBucket(i, sv.GetValue<string>().Trim()));
                        else if (el is JsonObject bo && TryGetProp(bo, "name", out var bn) && bn is not null && bn.GetValueKind() == JsonValueKind.String)
                        {
                            int bid = TryGetProp(bo, "bucketId", out var bi) && bi is JsonValue biv && biv.TryGetValue<int>(out var bIdVal) ? bIdVal : i;
                            parsedB.Add(new ClassifyBucket(bid, bn.GetValue<string>().Trim()));
                        }
                        else return new Error("Patch.InvalidValue", $"Classify bucket at index {i} must be string or {{bucketId, name}}.", ErrorType.Validation, Path: "buckets");
                    }
                    buckets = parsedB;
                }
                if (TryGetProp(patch, "items", out var itN))
                {
                    if (itN is not JsonArray itArr || itArr.Count == 0)
                        return new Error("Patch.InvalidValue", "'items' must be a non-empty array.", ErrorType.Validation, Path: "items");
                    var parsedItems = new List<ClassifyItem>(itArr.Count);
                    for (int i = 0; i < itArr.Count; i++)
                    {
                        if (itArr[i] is not JsonObject io || !TryGetProp(io, "text", out var txN) || txN is null || txN.GetValueKind() != JsonValueKind.String)
                            return new Error("Patch.InvalidValue", $"Classify item at index {i} must have 'text'.", ErrorType.Validation, Path: "items");
                        int bid = -1;
                        if (TryGetProp(io, "bucketId", out var biN) && biN is JsonValue biv && biv.TryGetValue<int>(out var bidVal))
                            bid = bidVal;
                        else if (TryGetProp(io, "bucket", out var bNameN) && bNameN is not null && bNameN.GetValueKind() == JsonValueKind.String)
                            bid = buckets.FirstOrDefault(b => string.Equals(b.Name, bNameN.GetValue<string>().Trim(), StringComparison.OrdinalIgnoreCase))?.BucketId ?? -1;
                        parsedItems.Add(new ClassifyItem(i, bid, txN.GetValue<string>().Trim()));
                    }
                    items = parsedItems;
                }

                return pq.SetPayloadJson(Phase1PayloadCodec.Serialize(new ClassifyPayload(title, buckets, items, explanation)));
            }
            case QuestionType.Match:
            {
                if (!Phase1PayloadCodec.TryDeserialize<MatchPayload>(pq.PayloadJson, out var current) || current is null)
                    return new Error("Patch.InvalidValue", "Existing match question has corrupted payload JSON.", ErrorType.Validation);

                var title = current.Title;
                var pairs = current.Pairs ?? [];
                var distractors = current.Distractors ?? [];
                var explanation = current.Explanation;

                if (TryReadNullableString(patch, "title", out var tVal, out var tErr)) title = tVal;
                else if (tErr is not null) return tErr;
                if (TryReadNullableString(patch, "explanation", out var expVal, out var expErr)) explanation = expVal;
                else if (expErr is not null) return expErr;
                if (TryGetProp(patch, "pairs", out var pairsN))
                {
                    if (pairsN is not JsonArray pArr || pArr.Count == 0)
                        return new Error("Patch.InvalidValue", "'pairs' must be a non-empty array of {left, right}.", ErrorType.Validation, Path: "pairs");
                    var parsedPairs = new List<MatchPair>(pArr.Count);
                    for (int i = 0; i < pArr.Count; i++)
                    {
                        if (pArr[i] is not JsonObject po ||
                            !TryGetProp(po, "left", out var lN) || lN is null || lN.GetValueKind() != JsonValueKind.String ||
                            !TryGetProp(po, "right", out var rN) || rN is null || rN.GetValueKind() != JsonValueKind.String)
                            return new Error("Patch.InvalidValue", $"Match pair at index {i} must specify 'left' and 'right' strings.", ErrorType.Validation, Path: "pairs");
                        parsedPairs.Add(new MatchPair(i, lN.GetValue<string>().Trim(), rN.GetValue<string>().Trim()));
                    }
                    pairs = parsedPairs;
                }
                if (TryReadStringArray(patch, "distractors", allowNull: true, allowEmpty: true, out var parsedDist, out var distErr))
                    distractors = (parsedDist ?? []).Where(s => !string.IsNullOrEmpty(s)).ToList();
                else if (distErr is not null) return distErr;

                return pq.SetPayloadJson(Phase1PayloadCodec.Serialize(new MatchPayload(title, pairs, distractors, explanation)));
            }
            case QuestionType.Numeric:
            {
                if (!Phase1PayloadCodec.TryDeserialize<NumericPayload>(pq.PayloadJson, out var current) || current is null)
                    return new Error("Patch.InvalidValue", "Existing numeric question has corrupted payload JSON.", ErrorType.Validation);

                var prompt = current.Prompt;
                var answer = current.Answer;
                var tolerance = current.Tolerance;
                var tolMode = current.ToleranceMode;
                var unit = current.Unit;
                var placeholder = current.Placeholder;
                var explanation = current.Explanation;
                var imagePath = current.ImagePath;

                if (TryGetProp(patch, "prompt", out var prN) || TryGetProp(patch, "text", out prN))
                {
                    if (prN is null || prN.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(prN.GetValue<string>()))
                        return new Error("Patch.InvalidValue", "'prompt'/'text' must be a non-empty string.", ErrorType.Validation, Path: "prompt");
                    prompt = prN.GetValue<string>().Trim();
                }
                if (TryReadDouble(patch, "answer", out var ansVal, out var ansErr)) answer = ansVal;
                else if (ansErr is not null) return ansErr;
                if (TryGetProp(patch, "tolerance", out var tolN))
                {
                    if (tolN is JsonValue tv && tv.GetValueKind() == JsonValueKind.Number && tv.TryGetValue<double>(out double dblTol) && double.IsFinite(dblTol) && dblTol >= 0)
                    {
                        tolerance = dblTol;
                    }
                    else if (tolN is JsonValue sv && sv.GetValueKind() == JsonValueKind.String &&
                             Phase3PayloadRules.TryParseToleranceSpec(sv.GetValue<string>(), tolMode, out double parsedTol, out string parsedMode))
                    {
                        tolerance = parsedTol;
                        tolMode = parsedMode;
                    }
                    else
                    {
                        return new Error("Patch.InvalidValue", "'tolerance' must be a non-negative number or percentage like '5%'.", ErrorType.Validation, Path: "tolerance");
                    }
                }
                if (TryGetProp(patch, "toleranceMode", out var tmN))
                {
                    if (tmN is null || tmN.GetValueKind() != JsonValueKind.String || !Phase3PayloadRules.IsValidToleranceMode(tmN.GetValue<string>()))
                        return new Error("Patch.InvalidValue", "'toleranceMode' must be 'absolute' or 'percent'.", ErrorType.Validation, Path: "toleranceMode");
                    tolMode = Phase3PayloadRules.NormalizeToleranceMode(tmN.GetValue<string>());
                }
                if (TryReadNullableString(patch, "unit", out var uVal, out var uErr)) unit = uVal;
                else if (uErr is not null) return uErr;
                if (TryReadNullableString(patch, "placeholder", out var phVal, out var phErr)) placeholder = phVal;
                else if (phErr is not null) return phErr;
                if (TryReadNullableString(patch, "explanation", out var expVal, out var expErr)) explanation = expVal;
                else if (expErr is not null) return expErr;
                if (TryGetProp(patch, "imagePath", out var imgN))
                {
                    var imgRes = await TryCopyPatchedImageAsync(imgN, "imagePath", allowNull: true, "'imagePath' must be a string or null.", storageDir, jsonDir, ct, treatWhitespaceAsNull: false);
                    if (!imgRes.IsSuccess) return imgRes.Error!;
                    imagePath = imgRes.Value;
                }

                return pq.SetPayloadJson(Phase1PayloadCodec.Serialize(new NumericPayload(prompt, answer, tolerance, tolMode, unit, placeholder, explanation, imagePath)));
            }
            case QuestionType.CaseGroup:
            {
                if (!Phase1PayloadCodec.TryDeserialize<CaseGroupPayload>(pq.PayloadJson, out var current) || current is null)
                    return new Error("Patch.InvalidValue", "Existing case question has corrupted payload JSON.", ErrorType.Validation);

                var title = current.Title;
                var stem = current.Stem;
                var imagePath = current.ImagePath;
                var caption = current.Caption;
                var questions = current.Questions ?? [];
                var explanation = current.Explanation;

                if (TryReadNullableString(patch, "title", out var tVal, out var tErr)) title = tVal;
                else if (tErr is not null) return tErr;
                if (TryReadRequiredString(patch, "stem", out var stemVal, out var stemErr)) stem = stemVal;
                else if (stemErr is not null) return stemErr;
                if (TryReadNullableString(patch, "caption", out var capVal, out var capErr)) caption = capVal;
                else if (capErr is not null) return capErr;
                if (TryReadNullableString(patch, "explanation", out var expVal, out var expErr)) explanation = expVal;
                else if (expErr is not null) return expErr;
                if (TryGetProp(patch, "imagePath", out var imgN))
                {
                    var imgRes = await TryCopyPatchedImageAsync(imgN, "imagePath", allowNull: true, "'imagePath' must be a string or null.", storageDir, jsonDir, ct, treatWhitespaceAsNull: false);
                    if (!imgRes.IsSuccess) return imgRes.Error!;
                    imagePath = imgRes.Value;
                }
                if (TryGetProp(patch, "questions", out var qN))
                {
                    List<CaseSubQuestion>? deserialized;
                    try { deserialized = JsonSerializer.Deserialize<List<CaseSubQuestion>>(qN?.ToJsonString() ?? "null", ReadOptions); }
                    catch (JsonException ex) { return new Error("Patch.InvalidValue", $"Invalid 'questions' array: {ex.Message}", ErrorType.Validation, Path: "questions"); }
                    if (deserialized is null || deserialized.Count < 2)
                        return new Error("Patch.InvalidValue", "'questions' must contain at least 2 valid sub-questions.", ErrorType.Validation, Path: "questions");
                    if (deserialized.Any(s => string.Equals(s.Type?.Trim(), "case", StringComparison.OrdinalIgnoreCase)))
                        return new Error("Case.NestedCaseNotAllowed", "Nested 'case' sub-questions inside 'case' are not allowed.", ErrorType.Validation, Path: "questions");
                    questions = deserialized.Select((s, idx) => s with { Index = idx }).ToList();
                }

                return pq.SetPayloadJson(Phase1PayloadCodec.Serialize(new CaseGroupPayload(title, stem, imagePath, caption, questions, explanation)));
            }
            default:
                return new Error("Patch.UnsupportedTarget", $"Unsupported question type '{pq.Type}'.", ErrorType.Validation);
        }
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
            var copyRes = await TryCopyPatchedImageAsync(imgNode, "imagePath", allowNull: true, "'imagePath' must be a string or null.", storageDir, jsonDir, ct, treatWhitespaceAsNull: false);
            if (!copyRes.IsSuccess) return copyRes.Error!;
            mcq.SetImage(copyRes.Value);
        }
        if (TryGetProp(patch, "explanation", out var expNode))
        {
            if (expNode is not null && expNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "'explanation' must be a string or null.", ErrorType.Validation, Path: "explanation");
            mcq.SetExplanation(expNode?.GetValue<string>());
        }
        bool hasMaxOptions = TryGetProp(patch, "maxOptions", out var maxNode);
        int maxVal = 0;
        if (hasMaxOptions)
        {
            if (maxNode is not JsonValue maxJsonVal || !maxJsonVal.TryGetValue<int>(out maxVal))
                return new Error("Patch.InvalidValue", "'maxOptions' must be an integer.", ErrorType.Validation, Path: "maxOptions");
        }

        bool hasMulti = TryGetProp(patch, "isMultiCorrect", out var multiNode);
        bool multiVal = false;
        if (hasMulti)
        {
            if (multiNode is not JsonValue multiJsonVal || !multiJsonVal.TryGetValue<bool>(out multiVal))
                return new Error("Patch.InvalidValue", "'isMultiCorrect' must be a boolean.", ErrorType.Validation, Path: "isMultiCorrect");
        }

        if (TryGetProp(patch, "options", out var optsNode))
        {
            if (optsNode is not JsonArray optsArr)
                return new Error("Patch.InvalidValue", "'options' must be an array.", ErrorType.Validation, Path: "options");
            if (optsArr.Count < 2)
                return new Error("Patch.InvalidValue", "'options' must contain at least 2 options.", ErrorType.Validation, Path: "options");
            if (optsArr.Count > 26)
                return QuestionErrors.InvalidMaxOptions(optsArr.Count);

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

            bool effectiveMulti = hasMulti ? multiVal : mcq.IsMultiCorrect;
            var seenIndices = new HashSet<int>();
            int correctCount = 0;
            for (int i = 0; i < parsedOpts.Count; i++)
            {
                var o = parsedOpts[i];
                int idx = o.Index > 0 ? o.Index : i + 1;
                if (o.Index < 0)
                    return new Error("Patch.InvalidValue", $"Option index {o.Index} must be >= 1.", ErrorType.Validation, Path: $"options[{i}]");
                if (!seenIndices.Add(idx))
                    return new Error("Json.DuplicateOptionIndex", $"MCQ contains duplicate option index {idx}.", ErrorType.Validation, Path: $"options[{i}]");
                if (o.IsCorrect) correctCount++;
            }
            if (!effectiveMulti && correctCount > 1)
                return new Error("Question.MultipleCorrectInSingle", $"Single-select MCQ specifies {correctCount} correct options. Set 'isMultiCorrect': true or mark only one option correct.", ErrorType.Validation, Path: "options");

            int highestIndex = seenIndices.Max();
            if (hasMaxOptions)
            {
                if (maxVal is < 2 or > 26)
                    return QuestionErrors.InvalidMaxOptions(maxVal);
                if (highestIndex > maxVal)
                    return new Error("Question.OptionIndexExceedsMaxOptions", $"Option index {highestIndex} exceeds maxOptions ({maxVal}).", ErrorType.Validation, Path: "options");
            }
            else if (highestIndex > 26)
            {
                return QuestionErrors.InvalidMaxOptions(highestIndex);
            }

            // Materialize images first before clearing existing options
            var materialized = new List<(int Index, string? Text, string? Img, bool IsCorrect)>(parsedOpts.Count);
            for (int i = 0; i < parsedOpts.Count; i++)
            {
                var o = parsedOpts[i];
                int idx = o.Index > 0 ? o.Index : i + 1;
                var imgRes = await JsonQuestionMaterializer.CopyConfinedImageAsync(o.ImagePath, jsonDir, storageDir, contentInspector, ct, workspaceCwdDirectory: workingDirectoryProvider.GetCurrentDirectory());
                if (!imgRes.IsSuccess) return imgRes.Error!;
                materialized.Add((idx, o.Text, imgRes.Value, o.IsCorrect));
            }

            mcq.ClearOptions();
            if (hasMaxOptions)
            {
                var maxRes = mcq.SetMaxOptions(maxVal);
                if (!maxRes.IsSuccess) return maxRes.Error!;
            }
            else if (highestIndex > mcq.MaxOptions)
            {
                var maxRes = mcq.SetMaxOptions(highestIndex);
                if (!maxRes.IsSuccess) return maxRes.Error!;
            }
            if (hasMulti)
            {
                if (multiVal) mcq.ConvertToMultiple();
                else
                {
                    var singleRes = mcq.ConvertToSingle();
                    if (!singleRes.IsSuccess) return singleRes.Error!;
                }
            }

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
        else
        {
            if (hasMaxOptions)
            {
                var res = mcq.SetMaxOptions(maxVal);
                if (!res.IsSuccess) return res.Error!;
            }
            if (hasMulti)
            {
                if (multiVal) mcq.ConvertToMultiple();
                else
                {
                    var res = mcq.ConvertToSingle();
                    if (!res.IsSuccess) return res.Error!;
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
            var copyRes = await TryCopyPatchedImageAsync(imgNode, $"{face.ToString().ToLowerInvariant()}.imagePath", allowNull: true, "Flashcard side 'imagePath' must be a string or null.", storageDir, jsonDir, ct);
            if (!copyRes.IsSuccess) return copyRes.Error!;
            if (copyRes.Value is null)
            {
                card.ClearSideImage(face);
            }
            else
            {
                var imgRes = card.SetSideImage(face, copyRes.Value);
                if (!imgRes.IsSuccess) return imgRes.Error!;
            }
        }

        if (TryGetProp(sideObj, "position", out var posNode) || TryGetProp(sideObj, "textPosition", out posNode))
        {
            if (posNode is null || posNode.GetValueKind() != JsonValueKind.String)
                return new Error("Patch.InvalidValue", "Flashcard side 'position' must be 'belowImage' or 'aboveImage'.", ErrorType.Validation, Path: $"{face.ToString().ToLowerInvariant()}.position");

            var rawPos = posNode.GetValue<string>().Trim().ToLowerInvariant();
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

    private sealed class PatchOptionDto
    {
        public int Index { get; set; }
        public string? Text { get; set; }
        public string? ImagePath { get; set; }
        public bool IsCorrect { get; set; }
    }
}
