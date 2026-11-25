using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;

namespace Meloberry.Application.UseCases.Json;

internal static class JsonQuestionMaterializer
{
    public static async Task<Result<Question>> MaterializeAsync(
        string questionId,
        string containerId,
        int initialIndex,
        JsonQuestionSpec spec,
        string jsonBaseDirectory,
        string storageDirectory,
        IContentInspector contentInspector,
        CancellationToken cancellationToken,
        string? specRootDirectory = null,
        string? workspaceCwdDirectory = null)
    {
        var typeNormalized = spec.Type?.Trim().ToLowerInvariant();
        if (typeNormalized == "mcq")
        {
            if (spec.Front is not null || spec.Back is not null)
                return new Error("Json.InvalidFieldForType", "MCQ question JSON cannot specify flashcard 'front' or 'back' fields.", ErrorType.Validation);

            var mcq = McqQuestion.CreateNew(questionId, containerId, initialIndex);
            if (!string.IsNullOrWhiteSpace(spec.Ref))
            {
                var refRes = mcq.SetRefKey(spec.Ref);
                if (!refRes.IsSuccess) return refRes.Error!;
            }
            if (!string.IsNullOrWhiteSpace(spec.Text))
            {
                var textRes = mcq.SetText(spec.Text);
                if (!textRes.IsSuccess) return textRes.Error!;
            }
            if (!string.IsNullOrWhiteSpace(spec.ImagePath))
            {
                var promptImg = await CopyConfinedImageAsync(spec.ImagePath, jsonBaseDirectory, storageDirectory, contentInspector, cancellationToken, specRootDirectory, workspaceCwdDirectory);
                if (!promptImg.IsSuccess) return promptImg.Error!;
                mcq.SetImage(promptImg.Value);
            }
            if (!string.IsNullOrWhiteSpace(spec.Explanation))
            {
                mcq.SetExplanation(spec.Explanation);
            }
            var seenIndices = new HashSet<int>();
            int correctCount = 0;
            foreach (var opt in spec.Options)
            {
                if (opt.Index < 1)
                    return IndexErrors.OutOfIndexBounds(opt.Index, 1, 26);
                if (!seenIndices.Add(opt.Index))
                    return new Error("Json.DuplicateOptionIndex", $"MCQ contains duplicate option index {opt.Index}.", ErrorType.Validation, Path: "options");
                if (opt.IsCorrect) correctCount++;
            }
            if (!spec.IsMultiCorrect && correctCount > 1)
                return new Error("Question.MultipleCorrectInSingle", $"Single-select MCQ specifies {correctCount} correct options. Set 'isMultiCorrect': true or mark only one option correct.", ErrorType.Validation, Path: "options");

            int highestIndex = spec.Options.Count == 0 ? 0 : spec.Options.Max(o => o.Index);
            if (spec.MaxOptions.HasValue)
            {
                var maxRes = mcq.SetMaxOptions(spec.MaxOptions.Value);
                if (!maxRes.IsSuccess) return maxRes.Error!;
                if (highestIndex > mcq.MaxOptions)
                    return new Error("Question.OptionIndexExceedsMaxOptions", $"Option index {highestIndex} exceeds configured maxOptions ({mcq.MaxOptions}). Increase maxOptions (up to 26) or remove extra options.", ErrorType.Validation, Path: "options");
            }
            else
            {
                if (highestIndex > 26 || spec.Options.Count > 26)
                    return QuestionErrors.InvalidMaxOptions(Math.Max(highestIndex, spec.Options.Count));
                if (highestIndex > mcq.MaxOptions)
                {
                    var maxRes = mcq.SetMaxOptions(highestIndex);
                    if (!maxRes.IsSuccess) return maxRes.Error!;
                }
            }
            if (spec.IsMultiCorrect) mcq.ConvertToMultiple();

            foreach (var opt in spec.Options)
            {
                var imgRes = await CopyConfinedImageAsync(opt.ImagePath, jsonBaseDirectory, storageDirectory, contentInspector, cancellationToken, specRootDirectory, workspaceCwdDirectory);
                if (!imgRes.IsSuccess) return imgRes.Error!;

                var optRes = mcq.SetOption(opt.Index, opt.Text, imgRes.Value);
                if (!optRes.IsSuccess) return optRes.Error!;

                if (opt.IsCorrect)
                {
                    var correctRes = mcq.MarkCorrect(opt.Index);
                    if (!correctRes.IsSuccess) return correctRes.Error!;
                }
            }

            var layoutRes = ApplyLayoutOverrides(mcq, spec.LayoutOverrides);
            return layoutRes.IsSuccess ? mcq : layoutRes.Error!;
        }

        if (typeNormalized == "flashcard")
        {
            if (!string.IsNullOrWhiteSpace(spec.Text) || spec.MaxOptions.HasValue || spec.IsMultiCorrect ||
                spec.Options.Count > 0 || !string.IsNullOrWhiteSpace(spec.ImagePath) || !string.IsNullOrWhiteSpace(spec.Explanation))
                return new Error("Json.InvalidFieldForType", "Flashcard question JSON cannot specify MCQ fields ('text', 'options', 'maxOptions', 'isMultiCorrect', 'imagePath', 'explanation').", ErrorType.Validation);

            var card = FlashcardQuestion.CreateNew(questionId, containerId, initialIndex);
            if (!string.IsNullOrWhiteSpace(spec.Ref))
            {
                var refRes = card.SetRefKey(spec.Ref);
                if (!refRes.IsSuccess) return refRes.Error!;
            }
            if (spec.Front is not null)
            {
                var frontRes = await ApplySideAsync(card, FlashcardFace.Front, spec.Front, jsonBaseDirectory, storageDirectory, contentInspector, cancellationToken, specRootDirectory, workspaceCwdDirectory);
                if (!frontRes.IsSuccess) return frontRes.Error!;
            }
            if (spec.Back is not null)
            {
                var backRes = await ApplySideAsync(card, FlashcardFace.Back, spec.Back, jsonBaseDirectory, storageDirectory, contentInspector, cancellationToken, specRootDirectory, workspaceCwdDirectory);
                if (!backRes.IsSuccess) return backRes.Error!;
            }

            var layoutRes = ApplyLayoutOverrides(card, spec.LayoutOverrides);
            return layoutRes.IsSuccess ? card : layoutRes.Error!;
        }

        if (Quiz.TryParseQuestionType(typeNormalized, out var qType) &&
            qType is QuestionType.FillBlank or QuestionType.AssertionReason or QuestionType.SpotError
                or QuestionType.Differentiation or QuestionType.Mechanism or QuestionType.RelabelImage
                or QuestionType.Classify or QuestionType.Match or QuestionType.Numeric or QuestionType.CaseGroup)
        {
            if (spec.Front is not null || spec.Back is not null || spec.MaxOptions.HasValue ||
                spec.IsMultiCorrect || spec.Options.Count > 0 ||
                (!string.IsNullOrWhiteSpace(spec.ImagePath) && qType is not (QuestionType.RelabelImage or QuestionType.SpotError or QuestionType.Numeric or QuestionType.CaseGroup)))
            {
                return new Error("Json.InvalidFieldForType", $"Question type '{Question.ToWireString(qType)}' cannot specify MCQ/Flashcard fields ('front', 'back', 'options', 'maxOptions', 'isMultiCorrect', 'imagePath').", ErrorType.Validation);
            }

            string? canonicalPayload = null;
            if (qType == QuestionType.FillBlank)
            {
                FillBlankPayload payload;
                if (Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(spec.PayloadJson, out var parsed) && parsed is not null)
                {
                    var slotsRes = Phase1PayloadRules.ParseFillBlankTemplate(parsed.Template);
                    var placeholder = string.IsNullOrWhiteSpace(parsed.Placeholder) ? null : parsed.Placeholder.Trim();
                    payload = parsed with { Blanks = slotsRes.IsSuccess ? slotsRes.Value : [], Placeholder = placeholder };
                }
                else
                {
                    var template = spec.Text ?? string.Empty;
                    var slotsRes = Phase1PayloadRules.ParseFillBlankTemplate(template);
                    payload = new FillBlankPayload(template, slotsRes.IsSuccess ? slotsRes.Value : [], Fuzzy: true, CaseSensitive: false, Explanation: spec.Explanation, Placeholder: null);
                }
                canonicalPayload = Phase1PayloadCodec.Serialize(payload);
            }
            else if (qType == QuestionType.AssertionReason)
            {
                if (!Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(spec.PayloadJson, out var parsed) || parsed is null)
                    return new Error("Json.InvalidQuestion", "Assertion-Reason question requires 'assertion', 'reason', and 'answer'.", ErrorType.Validation);
                var normalizedAnswer = Phase1PayloadRules.NormalizeAssertionReasonAnswer(parsed.Answer);
                var resolvedScheme = Phase1PayloadRules.ResolveAssertionReasonScheme(parsed.Scheme, normalizedAnswer);
                canonicalPayload = Phase1PayloadCodec.Serialize(parsed with { Answer = normalizedAnswer, Scheme = resolvedScheme });
            }
            else if (qType == QuestionType.SpotError)
            {
                SpotErrorPayload payload;
                if (Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(spec.PayloadJson, out var parsed) && parsed is not null)
                {
                    var segsRes = Phase1PayloadRules.ParseSpotErrorTemplate(parsed.Template);
                    var rawImg = !string.IsNullOrWhiteSpace(parsed.ImagePath) ? parsed.ImagePath : spec.ImagePath;
                    string? storedImg = null;
                    if (!string.IsNullOrWhiteSpace(rawImg))
                    {
                        var imgCopy = await CopyConfinedImageAsync(
                            rawImg, jsonBaseDirectory, storageDirectory, contentInspector, cancellationToken, specRootDirectory, workspaceCwdDirectory);
                        if (!imgCopy.IsSuccess) return imgCopy.Error!;
                        storedImg = imgCopy.Value;
                    }
                    payload = parsed with { Segments = segsRes.IsSuccess ? segsRes.Value : [], ImagePath = storedImg };
                }
                else
                {
                    var template = spec.Text ?? string.Empty;
                    var segsRes = Phase1PayloadRules.ParseSpotErrorTemplate(template);
                    string? storedImg = null;
                    if (!string.IsNullOrWhiteSpace(spec.ImagePath))
                    {
                        var imgCopy = await CopyConfinedImageAsync(
                            spec.ImagePath, jsonBaseDirectory, storageDirectory, contentInspector, cancellationToken, specRootDirectory, workspaceCwdDirectory);
                        if (!imgCopy.IsSuccess) return imgCopy.Error!;
                        storedImg = imgCopy.Value;
                    }
                    payload = new SpotErrorPayload(template, segsRes.IsSuccess ? segsRes.Value : [], RequireFix: false, AllowNoError: false, NoError: false, Explanation: spec.Explanation, ImagePath: storedImg);
                }
                canonicalPayload = Phase1PayloadCodec.Serialize(payload);
            }
            else if (qType == QuestionType.Differentiation)
            {
                if (!Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(spec.PayloadJson, out var parsed) || parsed is null)
                    return new Error("Json.InvalidQuestion", "Differentiation question requires 'mode', 'headers', and 'rows'.", ErrorType.Validation);

                var mode = Phase2PayloadRules.NormalizeDiffMode(parsed.Mode);
                var headers = parsed.Headers ?? [];
                var rows = parsed.Rows ?? [];
                var alignments = parsed.Alignments is { Count: > 0 }
                    ? parsed.Alignments
                    : Enumerable.Repeat("left", headers.Count).ToList();
                var specialRes = Phase2PayloadRules.ParseDifferentiationTable(mode, headers, rows);
                var normalized = new DifferentiationPayload(
                    mode,
                    string.IsNullOrWhiteSpace(parsed.Title) ? null : parsed.Title.Trim(),
                    parsed.Bank,
                    parsed.Fuzzy,
                    parsed.CaseSensitive,
                    parsed.AllowNoError,
                    headers,
                    alignments,
                    rows,
                    specialRes.IsSuccess ? specialRes.Value : [],
                    string.IsNullOrWhiteSpace(parsed.Explanation) ? null : parsed.Explanation.Trim());
                canonicalPayload = Phase1PayloadCodec.Serialize(normalized);
            }
            else if (qType == QuestionType.Mechanism)
            {
                if (!Phase1PayloadCodec.TryDeserialize<MechanismPayload>(spec.PayloadJson, out var parsed) || parsed is null)
                    return new Error("Json.InvalidQuestion", "Mechanism question requires a 'steps' array.", ErrorType.Validation);

                var rawSteps = parsed.Steps ?? [];
                var normalizedSteps = new List<MechanismStep>(rawSteps.Count);
                for (int idx = 0; idx < rawSteps.Count; idx++)
                {
                    var s = rawSteps[idx];
                    if (s is null)
                        return new Error("Json.InvalidQuestion", $"Mechanism step at index {idx} cannot be null.", ErrorType.Validation);
                    normalizedSteps.Add(new MechanismStep(
                        idx + 1,
                        s.IsDistractor ? 0 : s.OrderGroup,
                        s.Text?.Trim() ?? string.Empty,
                        s.IsDistractor));
                }
                var normalized = new MechanismPayload(
                    string.IsNullOrWhiteSpace(parsed.Title) ? null : parsed.Title.Trim(),
                    normalizedSteps,
                    string.IsNullOrWhiteSpace(parsed.Explanation) ? null : parsed.Explanation.Trim());
                canonicalPayload = Phase1PayloadCodec.Serialize(normalized);
            }
            else if (qType == QuestionType.RelabelImage)
            {
                if (!Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(spec.PayloadJson, out var parsed) || parsed is null)
                    return new Error("Json.InvalidQuestion", "Relabel question requires 'imagePath' and either 'masks'/'masksPath' or 'boxes'.", ErrorType.Validation);

                var rawImagePath = !string.IsNullOrWhiteSpace(parsed.ImagePath) ? parsed.ImagePath : (spec.ImagePath ?? string.Empty);
                var rawBoxes = parsed.Boxes ?? [];

                if (!string.IsNullOrWhiteSpace(parsed.SourceMasksPath) && rawBoxes.Count > 0)
                    return new Error("Json.InvalidQuestion", "Specify either 'masks'/'masksPath' sidecar or inline 'boxes', not both.", ErrorType.Validation);

                string storedImagePath = string.Empty;
                if (!string.IsNullOrWhiteSpace(rawImagePath))
                {
                    var imgCopy = await CopyConfinedImageAsync(
                        rawImagePath, jsonBaseDirectory, storageDirectory, contentInspector, cancellationToken, specRootDirectory, workspaceCwdDirectory);
                    if (!imgCopy.IsSuccess) return imgCopy.Error!;
                    storedImagePath = imgCopy.Value ?? string.Empty;
                }

                IReadOnlyList<RelabelBox> resolvedBoxes;
                if (!string.IsNullOrWhiteSpace(parsed.SourceMasksPath))
                {
                    var sidecarRes = ResolveConfinedSidecarFile(parsed.SourceMasksPath, jsonBaseDirectory, specRootDirectory, workspaceCwdDirectory);
                    if (!sidecarRes.IsSuccess) return sidecarRes.Error!;

                    var rawSidecar = await File.ReadAllTextAsync(sidecarRes.Value, cancellationToken);
                    var boxesRes = Phase2PayloadRules.ParseMaskSidecarJson(rawSidecar, parsed.IncludeLabels);
                    if (!boxesRes.IsSuccess) return boxesRes.Error!;

                    if (parsed.IncludeLabels is { Count: > 0 } && boxesRes.Value.Count == 0)
                        return new Error("Relabel.IncludeMatchedNoBoxes", $"Relabel 'include' filter matched 0 boxes in sidecar '{parsed.SourceMasksPath}'.", ErrorType.Validation);

                    resolvedBoxes = boxesRes.Value;
                }
                else
                {
                    var normalizedBoxes = new List<RelabelBox>(rawBoxes.Count);
                    for (int idx = 0; idx < rawBoxes.Count; idx++)
                    {
                        var b = rawBoxes[idx];
                        if (b is null)
                            return new Error("Json.InvalidQuestion", $"Relabel box at index {idx} cannot be null.", ErrorType.Validation);
                        var aliases = (b.Aliases ?? [])
                            .Where(a => !string.IsNullOrWhiteSpace(a))
                            .Select(a => a.Trim())
                            .ToList();
                        normalizedBoxes.Add(new RelabelBox(idx + 1, b.Label?.Trim() ?? string.Empty, aliases, b.X, b.Y, b.W, b.H));
                    }
                    resolvedBoxes = normalizedBoxes;
                }

                var mode = string.IsNullOrWhiteSpace(parsed.Mode) ? "bank" : Phase2PayloadRules.NormalizeRelabelMode(parsed.Mode);
                var style = string.IsNullOrWhiteSpace(parsed.Style) ? "solid" : Phase2PayloadRules.NormalizeRelabelStyle(parsed.Style);
                var distractors = (parsed.Distractors ?? [])
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => d.Trim())
                    .ToList();

                var normalized = new RelabelImagePayload(
                    storedImagePath,
                    string.IsNullOrWhiteSpace(parsed.Prompt) ? null : parsed.Prompt.Trim(),
                    mode,
                    style,
                    resolvedBoxes,
                    distractors,
                    parsed.Fuzzy,
                    parsed.CaseSensitive,
                    string.IsNullOrWhiteSpace(parsed.Explanation) ? null : parsed.Explanation.Trim(),
                    SourceMasksPath: null,
                    IncludeLabels: null);
                canonicalPayload = Phase1PayloadCodec.Serialize(normalized);
            }
            else if (qType == QuestionType.Classify)
            {
                if (!Phase1PayloadCodec.TryDeserialize<ClassifyPayload>(spec.PayloadJson, out var parsed) || parsed is null)
                    return new Error("Json.InvalidQuestion", "Classify question requires valid 'buckets' and 'items' payload data.", ErrorType.Validation);

                var buckets = (parsed.Buckets ?? [])
                    .Where(b => b is not null)
                    .Select((b, idx) => new ClassifyBucket(b.BucketId >= 0 ? b.BucketId : idx, b.Name?.Trim() ?? string.Empty))
                    .ToList();
                var items = (parsed.Items ?? [])
                    .Where(it => it is not null)
                    .Select((it, idx) => new ClassifyItem(idx, it.BucketId, it.Text?.Trim() ?? string.Empty))
                    .ToList();
                canonicalPayload = Phase1PayloadCodec.Serialize(new ClassifyPayload(
                    string.IsNullOrWhiteSpace(parsed.Title) ? null : parsed.Title.Trim(),
                    buckets,
                    items,
                    string.IsNullOrWhiteSpace(parsed.Explanation ?? spec.Explanation) ? null : (parsed.Explanation ?? spec.Explanation)!.Trim()));
            }
            else if (qType == QuestionType.Match)
            {
                if (!Phase1PayloadCodec.TryDeserialize<MatchPayload>(spec.PayloadJson, out var parsed) || parsed is null)
                    return new Error("Json.InvalidQuestion", "Match question requires valid 'pairs' payload data.", ErrorType.Validation);

                var pairs = (parsed.Pairs ?? [])
                    .Where(p => p is not null)
                    .Select((p, idx) => new MatchPair(idx, p.Left?.Trim() ?? string.Empty, p.Right?.Trim() ?? string.Empty))
                    .ToList();
                var distractors = (parsed.Distractors ?? [])
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => d.Trim())
                    .ToList();
                canonicalPayload = Phase1PayloadCodec.Serialize(new MatchPayload(
                    string.IsNullOrWhiteSpace(parsed.Title) ? null : parsed.Title.Trim(),
                    pairs,
                    distractors,
                    string.IsNullOrWhiteSpace(parsed.Explanation ?? spec.Explanation) ? null : (parsed.Explanation ?? spec.Explanation)!.Trim()));
            }
            else if (qType == QuestionType.Numeric)
            {
                NumericPayload parsed;
                if (!Phase1PayloadCodec.TryDeserialize<NumericPayload>(spec.PayloadJson, out var np) || np is null)
                    return new Error("Json.InvalidQuestion", "Numeric question requires valid 'answer' and prompt payload data.", ErrorType.Validation);
                parsed = np;

                if (!Phase3PayloadRules.IsValidToleranceMode(parsed.ToleranceMode))
                    return new Error("Json.InvalidQuestion", $"Invalid numeric toleranceMode '{parsed.ToleranceMode}'. Use 'absolute' or 'percent'.", ErrorType.Validation);

                string? storedImg = null;
                var rawImg = !string.IsNullOrWhiteSpace(parsed.ImagePath) ? parsed.ImagePath : spec.ImagePath;
                if (!string.IsNullOrWhiteSpace(rawImg))
                {
                    var imgCopy = await CopyConfinedImageAsync(
                        rawImg, jsonBaseDirectory, storageDirectory, contentInspector, cancellationToken, specRootDirectory, workspaceCwdDirectory);
                    if (!imgCopy.IsSuccess) return imgCopy.Error!;
                    storedImg = imgCopy.Value;
                }

                var prompt = !string.IsNullOrWhiteSpace(parsed.Prompt) ? parsed.Prompt.Trim() : (spec.Text ?? string.Empty).Trim();
                canonicalPayload = Phase1PayloadCodec.Serialize(new NumericPayload(
                    prompt,
                    parsed.Answer,
                    parsed.Tolerance,
                    Phase3PayloadRules.NormalizeToleranceMode(parsed.ToleranceMode),
                    string.IsNullOrWhiteSpace(parsed.Unit) ? null : parsed.Unit.Trim(),
                    string.IsNullOrWhiteSpace(parsed.Placeholder) ? null : parsed.Placeholder.Trim(),
                    string.IsNullOrWhiteSpace(parsed.Explanation ?? spec.Explanation) ? null : (parsed.Explanation ?? spec.Explanation)!.Trim(),
                    storedImg));
            }
            else if (qType == QuestionType.CaseGroup)
            {
                Phase1PayloadCodec.TryDeserialize<CaseGroupPayload>(spec.PayloadJson, out var parsed);
                var title = parsed?.Title;
                var stem = !string.IsNullOrWhiteSpace(parsed?.Stem) ? parsed!.Stem.Trim() : (spec.Text ?? string.Empty).Trim();
                var caption = string.IsNullOrWhiteSpace(parsed?.Caption) ? null : parsed!.Caption.Trim();
                var explanation = string.IsNullOrWhiteSpace(parsed?.Explanation ?? spec.Explanation) ? null : (parsed?.Explanation ?? spec.Explanation)!.Trim();

                string? storedStemImg = null;
                var rawStemImg = !string.IsNullOrWhiteSpace(parsed?.ImagePath) ? parsed!.ImagePath : spec.ImagePath;
                if (!string.IsNullOrWhiteSpace(rawStemImg))
                {
                    var imgCopy = await CopyConfinedImageAsync(
                        rawStemImg, jsonBaseDirectory, storageDirectory, contentInspector, cancellationToken, specRootDirectory, workspaceCwdDirectory);
                    if (!imgCopy.IsSuccess) return imgCopy.Error!;
                    storedStemImg = imgCopy.Value;
                }

                var subQuestions = new List<CaseSubQuestion>();
                if (spec.SubQuestions is { Count: > 0 })
                {
                    for (int sIdx = 0; sIdx < spec.SubQuestions.Count; sIdx++)
                    {
                        var subSpec = spec.SubQuestions[sIdx];
                        if (Quiz.TryParseQuestionType(subSpec.Type, out var subQType) && subQType == QuestionType.CaseGroup)
                            return new Error("Case.NestedCaseNotAllowed", "Nested 'case' sub-questions inside ':::case' are not allowed.", ErrorType.Validation);

                        var subMat = await MaterializeAsync(
                            $"{questionId}_sub{sIdx}",
                            containerId,
                            sIdx + 1,
                            subSpec,
                            jsonBaseDirectory,
                            storageDirectory,
                            contentInspector,
                            cancellationToken,
                            specRootDirectory,
                            workspaceCwdDirectory);
                        if (!subMat.IsSuccess) return subMat.Error!;

                        var sq = subMat.Value;
                        if (sq is McqQuestion smcq)
                        {
                            subQuestions.Add(new CaseSubQuestion(
                                sIdx,
                                "mcq",
                                Mcq: new CaseSubMcqData(
                                    smcq.Text,
                                    smcq.MaxOptions,
                                    smcq.IsMultiCorrect,
                                    smcq.Options.Select(o => new CaseSubMcqOption(o.Index, o.Text, o.ImagePath, o.IsCorrect)).ToList(),
                                    smcq.ImagePath,
                                    smcq.Explanation)));
                        }
                        else if (sq is FlashcardQuestion sfc)
                        {
                            subQuestions.Add(new CaseSubQuestion(
                                sIdx,
                                "flashcard",
                                Flashcard: new CaseSubFlashcardData(
                                    new(sfc.Front.Text, sfc.Front.ImagePath, sfc.Front.Position == MediaPosition.TextAboveImage ? "aboveImage" : "belowImage"),
                                    new(sfc.Back.Text, sfc.Back.ImagePath, sfc.Back.Position == MediaPosition.TextAboveImage ? "aboveImage" : "belowImage"))));
                        }
                        else if (sq is PayloadQuestion spq)
                        {
                            subQuestions.Add(new CaseSubQuestion(
                                sIdx,
                                Question.ToWireString(spq.Type),
                                PayloadJson: spq.PayloadJson));
                        }
                    }
                }
                else if (parsed?.Questions is { Count: > 0 })
                {
                    for (int sIdx = 0; sIdx < parsed.Questions.Count; sIdx++)
                    {
                        var sub = parsed.Questions[sIdx];
                        if (sub is null)
                            return new Error("Json.InvalidQuestion", $"Case sub-question at index {sIdx} cannot be null.", ErrorType.Validation);
                        if (string.Equals(sub.Type?.Trim(), "case", StringComparison.OrdinalIgnoreCase))
                            return new Error("Case.NestedCaseNotAllowed", "Nested 'case' sub-questions inside ':::case' are not allowed.", ErrorType.Validation);
                        subQuestions.Add(sub with { Index = sIdx });
                    }
                }

                canonicalPayload = Phase1PayloadCodec.Serialize(new CaseGroupPayload(
                    string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                    stem,
                    storedStemImg,
                    caption,
                    subQuestions,
                    explanation));
            }

            var createdRes = PayloadQuestion.Create(questionId, containerId, initialIndex, qType, canonicalPayload ?? "{}");
            if (!createdRes.IsSuccess) return createdRes.Error!;
            var pq = createdRes.Value;

            if (!string.IsNullOrWhiteSpace(spec.Ref))
            {
                var refRes = pq.SetRefKey(spec.Ref);
                if (!refRes.IsSuccess) return refRes.Error!;
            }

            var layoutRes = ApplyLayoutOverrides(pq, spec.LayoutOverrides);
            return layoutRes.IsSuccess ? pq : layoutRes.Error!;
        }

        return QuestionErrors.InvalidType(spec.Type ?? string.Empty);
    }

    internal static Result ApplyLayoutOverrides(Question question, Dictionary<string, string>? overrides)
    {
        if (overrides is null) return Result.Success();
        foreach (var (prop, val) in overrides)
        {
            var res = question.SetLayoutOverride(prop, val);
            if (!res.IsSuccess) return res.Error!;
        }
        return Result.Success();
    }

    internal static async Task<Result> ApplySideAsync(
        FlashcardQuestion card,
        FlashcardFace face,
        JsonFlashcardSideSpec sideSpec,
        string jsonBaseDirectory,
        string storageDirectory,
        IContentInspector contentInspector,
        CancellationToken cancellationToken,
        string? specRootDirectory = null,
        string? workspaceCwdDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(sideSpec.Text))
        {
            var textRes = card.SetSideText(face, sideSpec.Text);
            if (!textRes.IsSuccess) return textRes.Error!;
        }

        var imgCopy = await CopyConfinedImageAsync(sideSpec.ImagePath, jsonBaseDirectory, storageDirectory, contentInspector, cancellationToken, specRootDirectory, workspaceCwdDirectory);
        if (!imgCopy.IsSuccess) return imgCopy.Error!;
        if (imgCopy.Value is not null)
        {
            var imgRes = card.SetSideImage(face, imgCopy.Value);
            if (!imgRes.IsSuccess) return imgRes.Error!;
        }

        if (!string.IsNullOrWhiteSpace(sideSpec.TextPosition))
        {
            switch (sideSpec.TextPosition.Trim().ToLowerInvariant())
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
                    return new Error("Json.InvalidPosition", $"Invalid flashcard position '{sideSpec.TextPosition}'. Use 'belowImage' or 'aboveImage'.", ErrorType.Validation);
            }
        }

        return Result.Success();
    }

    internal static async Task<Result<string?>> CopyConfinedImageAsync(
        string? rawPath,
        string componentBaseDirectory,
        string storageDirectory,
        IContentInspector contentInspector,
        CancellationToken cancellationToken,
        string? specRootDirectory = null,
        string? workspaceCwdDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return (string?)null;

        string? resolvedPath = null;
        if (PathConfinement.TryResolveInside(componentBaseDirectory, rawPath, out var compCandidate))
        {
            resolvedPath = compCandidate;
        }
        else if (!string.IsNullOrWhiteSpace(specRootDirectory) &&
                 TryResolveRelativeInsideRoot(componentBaseDirectory, rawPath, specRootDirectory, out var nestedCandidate))
        {
            resolvedPath = nestedCandidate;
        }
        else if (!string.IsNullOrWhiteSpace(specRootDirectory) &&
                 PathConfinement.TryResolveInside(specRootDirectory, rawPath, out var rootCandidate))
        {
            resolvedPath = rootCandidate;
        }
        else
        {
            return new Error("Asset.InvalidPath", $"JSON imagePath '{rawPath}' must be relative and stay inside the input directory.", ErrorType.Validation);
        }

        // If the asset path wasn't found inside the component/spec folder (e.g. ./articles/ referencing ./assets/fig_1.png),
        // also check specRootDirectory and immediate workspaceCwdDirectory (only for non-traversal relative paths).
        if (!File.Exists(resolvedPath) && !StartsWithParentTraversal(rawPath))
        {
            if (!string.IsNullOrWhiteSpace(specRootDirectory) &&
                PathConfinement.TryResolveInside(specRootDirectory, rawPath, out var rootFallback) &&
                File.Exists(rootFallback))
            {
                resolvedPath = rootFallback;
            }
            else if (!string.IsNullOrWhiteSpace(workspaceCwdDirectory) &&
                     PathConfinement.TryResolveInside(workspaceCwdDirectory, rawPath, out var cwdFallback) &&
                     File.Exists(cwdFallback))
            {
                resolvedPath = cwdFallback;
            }
        }

        var copyRes = await contentInspector.CopyAndVerifyImageAsync(resolvedPath, storageDirectory, cancellationToken);
        return copyRes.IsSuccess ? copyRes.Value : copyRes.Error!;
    }

    private const long MaxSidecarBytes = 5L * 1024 * 1024;

    internal static Result<string> ResolveConfinedSidecarFile(
        string rawPath,
        string componentBaseDirectory,
        string? specRootDirectory = null,
        string? workspaceCwdDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return new Error("Asset.InvalidPath", "Relabel masksPath cannot be empty.", ErrorType.Validation);

        string? resolvedPath;
        if (PathConfinement.TryResolveInside(componentBaseDirectory, rawPath, out var compCandidate))
        {
            resolvedPath = compCandidate;
        }
        else if (!string.IsNullOrWhiteSpace(specRootDirectory) &&
                 TryResolveRelativeInsideRoot(componentBaseDirectory, rawPath, specRootDirectory, out var nestedCandidate))
        {
            resolvedPath = nestedCandidate;
        }
        else if (!string.IsNullOrWhiteSpace(specRootDirectory) &&
                 PathConfinement.TryResolveInside(specRootDirectory, rawPath, out var rootCandidate))
        {
            resolvedPath = rootCandidate;
        }
        else
        {
            return new Error("Asset.InvalidPath", $"Relabel masksPath '{rawPath}' must be relative and stay inside the input directory.", ErrorType.Validation);
        }

        if (!File.Exists(resolvedPath) && !StartsWithParentTraversal(rawPath))
        {
            if (!string.IsNullOrWhiteSpace(specRootDirectory) &&
                PathConfinement.TryResolveInside(specRootDirectory, rawPath, out var rootFallback) &&
                File.Exists(rootFallback))
            {
                resolvedPath = rootFallback;
            }
            else if (!string.IsNullOrWhiteSpace(workspaceCwdDirectory) &&
                     PathConfinement.TryResolveInside(workspaceCwdDirectory, rawPath, out var cwdFallback) &&
                     File.Exists(cwdFallback))
            {
                resolvedPath = cwdFallback;
            }
        }

        if (!File.Exists(resolvedPath))
            return new Error("Json.FileNotFound", $"Relabel masks sidecar file '{rawPath}' was not found.", ErrorType.NotFound);

        var fileInfo = new FileInfo(resolvedPath);
        if (fileInfo.Length > MaxSidecarBytes)
            return new Error("Relabel.SidecarTooLarge", $"Relabel masks sidecar '{rawPath}' exceeds the 5 MB limit ({fileInfo.Length} bytes).", ErrorType.Validation);

        return resolvedPath;
    }

    private static bool TryResolveRelativeInsideRoot(string baseDir, string rawPath, string rootDir, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Contains('\0')) return false;
        var trimmed = rawPath.Trim();
        if (Path.IsPathRooted(trimmed)) return false;
        try
        {
            var rootFull = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidateFull = Path.GetFullPath(Path.Combine(baseDir, trimmed));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
            {
                resolvedPath = candidateFull;
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartsWithParentTraversal(string rawPath)
    {
        var normalized = rawPath.Trim().Replace('/', '\\');
        return normalized.Equals("..", StringComparison.Ordinal) ||
               normalized.StartsWith("..\\", StringComparison.Ordinal);
    }
}
