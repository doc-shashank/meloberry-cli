using Meloberry.Domain.Common;
using Meloberry.Domain.Layouts;

namespace Meloberry.Domain.Questions;

public enum QuestionType { Mcq, Flashcard, FillBlank, AssertionReason, SpotError, Differentiation, Mechanism, RelabelImage, CaseGroup, Classify, Match, Numeric }
public enum FlashcardFace { Front, Back }
public enum MediaPosition { TextBelowImage, TextAboveImage }

public abstract class Question : IIndexedEntity
{
    private readonly Dictionary<string, string> _layoutOverrides;

    protected Question(
        string id,
        string containerId,
        int index,
        QuestionType type,
        Dictionary<string, string>? layoutOverrides = null,
        string? refKey = null,
        string? payloadJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);

        Id = id;
        ContainerId = containerId;
        Index = index;
        Type = type;
        RefKey = string.IsNullOrWhiteSpace(refKey) ? null : refKey.Trim().ToLowerInvariant();
        PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? null : payloadJson.Trim();
        _layoutOverrides = layoutOverrides is not null
            ? new Dictionary<string, string>(layoutOverrides, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public string Id { get; private set; }
    public string ContainerId { get; private set; }
    public int Index { get; private set; }
    public QuestionType Type { get; private set; }
    public string? RefKey { get; private set; }
    public string? PayloadJson { get; protected set; }
    public IReadOnlyDictionary<string, string> LayoutOverrides => _layoutOverrides;
    public string LayoutTargetKey => Type switch
    {
        QuestionType.Flashcard => "flashcard",
        _ => "mcq"
    };

    public static string ToWireString(QuestionType type) => type switch
    {
        QuestionType.Mcq => "mcq",
        QuestionType.Flashcard => "flashcard",
        QuestionType.FillBlank => "fill-blank",
        QuestionType.AssertionReason => "assertion-reason",
        QuestionType.SpotError => "spot-error",
        QuestionType.Differentiation => "diff",
        QuestionType.Mechanism => "mechanism",
        QuestionType.RelabelImage => "relabel",
        QuestionType.CaseGroup => "case",
        QuestionType.Classify => "classify",
        QuestionType.Match => "match",
        QuestionType.Numeric => "numeric",
        _ => type.ToString().ToLowerInvariant()
    };

    public Result SetRefKey(string? refKey)
    {
        var res = RefKeyValidator.ValidateAndNormalize(refKey);
        if (!res.IsSuccess) return res.Error!;
        RefKey = res.Value;
        return Result.Success();
    }

    public bool RemoveLayoutOverride(string propertyName) =>
        _layoutOverrides.Remove(propertyName);

    public Result<string> SetLayoutOverride(string propertyName, string rawValue)
    {
        var resolved = LayoutBoundsCatalog.ValidateAndResolveForEntityOverride(LayoutTargetKey, propertyName, rawValue);
        if (!resolved.IsSuccess) return resolved.Error!;
        _layoutOverrides[resolved.Value.CanonicalName] = resolved.Value.Value;
        return resolved.Value.Value;
    }

    void IIndexedEntity.SetIndexInternal(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        Index = index;
    }
}
