using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;

namespace Meloberry.Domain.Questions;

public sealed class McqQuestion : Question
{
    public const int DefaultMaxOptions = 4;
    private readonly List<McqOption> _options;

    private McqQuestion(
        string id,
        string containerId,
        int index,
        string text,
        int maxOptions,
        bool isMultiCorrect,
        List<McqOption> options,
        Dictionary<string, string>? layoutOverrides,
        string? imagePath,
        string? explanation,
        string? refKey = null)
        : base(id, containerId, index, QuestionType.Mcq, layoutOverrides, refKey)
    {
        Text = text;
        MaxOptions = maxOptions;
        IsMultiCorrect = isMultiCorrect;
        _options = options;
        _options.Sort((a, b) => a.Index.CompareTo(b.Index));
        ImagePath = string.IsNullOrWhiteSpace(imagePath) ? null : imagePath;
        Explanation = string.IsNullOrWhiteSpace(explanation) ? null : explanation;
    }

    public string Text { get; private set; }
    public int MaxOptions { get; private set; }
    public bool IsMultiCorrect { get; private set; }
    public string? ImagePath { get; private set; }
    public string? Explanation { get; private set; }
    public IReadOnlyList<McqOption> Options => _options;

    public static McqQuestion CreateNew(string id, string containerId, int index) =>
        new(id, containerId, index, string.Empty, DefaultMaxOptions, false, [], null, null, null);

    public static McqQuestion Rehydrate(
        string id,
        string containerId,
        int index,
        string text,
        int maxOptions,
        bool isMultiCorrect,
        IEnumerable<McqOption> options,
        Dictionary<string, string>? layoutOverrides = null,
        string? imagePath = null,
        string? explanation = null,
        string? refKey = null) =>
        new(id, containerId, index, text, maxOptions, isMultiCorrect, [.. options], layoutOverrides, imagePath, explanation, refKey);

    public Result SetText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return QuestionErrors.EmptyText();
        Text = text;
        return Result.Success();
    }

    public Result SetImage(string? imagePath)
    {
        ImagePath = string.IsNullOrWhiteSpace(imagePath) ? null : imagePath.Trim();
        return Result.Success();
    }

    public Result SetExplanation(string? explanation)
    {
        Explanation = string.IsNullOrWhiteSpace(explanation) ? null : explanation.Trim();
        return Result.Success();
    }

    public Result SetMaxOptions(int maxOptions)
    {
        if (maxOptions < 2 || maxOptions > 26) return QuestionErrors.InvalidMaxOptions(maxOptions);
        int highestPopulated = _options.Count == 0 ? 0 : _options.Max(o => o.Index);
        if (maxOptions < highestPopulated) return QuestionErrors.CannotShrinkOptionNumBelowPopulated(maxOptions, highestPopulated);
        MaxOptions = maxOptions;
        return Result.Success();
    }

    public void ClearOptions() => _options.Clear();

    public Result SetOption(int index, string? text, string? imagePath)
    {
        if (index < 1 || index > MaxOptions) return IndexErrors.OutOfIndexBounds(index, 1, MaxOptions);
        var normalizedText = string.IsNullOrWhiteSpace(text) ? null : text;
        var normalizedImage = string.IsNullOrWhiteSpace(imagePath) ? null : imagePath;
        if (normalizedText is null && normalizedImage is null) return QuestionErrors.OptionEmpty(index);

        var existing = _options.FirstOrDefault(o => o.Index == index);
        if (existing is not null)
            existing.UpdateContent(normalizedText, normalizedImage);
        else
        {
            _options.Add(McqOption.Create(index, normalizedText, normalizedImage));
            _options.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        return Result.Success();
    }

    public Result MarkCorrect(int optionIndex)
    {
        if (optionIndex < 1 || optionIndex > MaxOptions) return IndexErrors.OutOfIndexBounds(optionIndex, 1, MaxOptions);
        var target = _options.FirstOrDefault(o => o.Index == optionIndex);
        if (target is null) return QuestionErrors.OptionNotPopulated(optionIndex);

        if (!IsMultiCorrect)
            foreach (var option in _options) option.SetCorrect(option.Index == optionIndex);
        else
            target.SetCorrect(true);

        return Result.Success();
    }

    public Result ConvertToMultiple() { IsMultiCorrect = true; return Result.Success(); }

    public Result ConvertToSingle()
    {
        int correctCount = _options.Count(o => o.IsCorrect);
        if (correctCount > 1) return QuestionErrors.CannotRevertToSingleWithMultipleCorrect(correctCount);
        IsMultiCorrect = false;
        return Result.Success();
    }

    public override Question CloneWithNewId(string newQuestionId, string targetContainerId, string? newRefKey = null) =>
        Rehydrate(
            newQuestionId,
            targetContainerId,
            1,
            Text,
            MaxOptions,
            IsMultiCorrect,
            _options.Select(o => McqOption.Create(o.Index, o.Text, o.ImagePath, o.IsCorrect)),
            CloneLayoutOverrides(),
            ImagePath,
            Explanation,
            newRefKey);
}
