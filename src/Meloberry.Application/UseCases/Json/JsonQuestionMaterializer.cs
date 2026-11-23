using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Questions;

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
            if (spec.MaxOptions.HasValue)
            {
                var maxRes = mcq.SetMaxOptions(spec.MaxOptions.Value);
                if (!maxRes.IsSuccess) return maxRes.Error!;
            }
            if (spec.IsMultiCorrect) mcq.ConvertToMultiple();

            var seenIndices = new HashSet<int>();
            int correctCount = 0;
            foreach (var opt in spec.Options)
            {
                if (!seenIndices.Add(opt.Index))
                    return new Error("Json.DuplicateOptionIndex", $"MCQ contains duplicate option index {opt.Index}.", ErrorType.Validation, Path: "options");
                if (opt.IsCorrect) correctCount++;
            }
            if (!spec.IsMultiCorrect && correctCount > 1)
                return new Error("Question.MultipleCorrectInSingle", $"Single-select MCQ specifies {correctCount} correct options. Set 'isMultiCorrect': true or mark only one option correct.", ErrorType.Validation, Path: "options");

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

    private static bool StartsWithParentTraversal(string rawPath)
    {
        var normalized = rawPath.Trim().Replace('/', '\\');
        return normalized.Equals("..", StringComparison.Ordinal) ||
               normalized.StartsWith("..\\", StringComparison.Ordinal);
    }
}
