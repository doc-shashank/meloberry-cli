using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;

namespace Meloberry.Domain.Layouts;

public enum LayoutPropertyType { Integer, Enum }

public sealed record LayoutPropertySpec(
    string Name,
    string Description,
    LayoutPropertyType ValueType,
    int? MinValue,
    int? MaxValue,
    IReadOnlyList<string> AllowedValues,
    string DefaultValue,
    IReadOnlyList<string> ApplicableTargets);

public static class LayoutBoundsCatalog
{
    public static readonly IReadOnlyList<string> SupportedTargets = ["mcq", "flashcard", "article"];
    public static readonly IReadOnlyList<string> GlobalOnlyProperties = ["themeMode", "slideTransitionMs"];

    private static readonly IReadOnlyList<LayoutPropertySpec> AllSpecs =
    [
        new("cardMaxWidthPx", "Maximum card container width in pixels", LayoutPropertyType.Integer, 480, 1400, [], "840", SupportedTargets),
        new("cardPaddingPx", "Inner card padding in pixels", LayoutPropertyType.Integer, 12, 64, [], "32", SupportedTargets),
        new("promptFontSizePx", "Prompt / heading font size in pixels", LayoutPropertyType.Integer, 14, 42, [], "22", SupportedTargets),
        new("bodyFontSizePx", "Body / option font size in pixels", LayoutPropertyType.Integer, 12, 28, [], "16", SupportedTargets),
        new("imageMaxHeightPx", "Maximum rendered image height in pixels", LayoutPropertyType.Integer, 120, 800, [], "360", SupportedTargets),
        new("slideTransitionMs", "Horizontal slide animation duration in milliseconds", LayoutPropertyType.Integer, 100, 1200, [], "380", SupportedTargets),
        new("contentAlign", "Horizontal text and content alignment", LayoutPropertyType.Enum, null, null, ["left", "center", "right"], "left", SupportedTargets),
        new("themeMode", "WebApp color theme", LayoutPropertyType.Enum, null, null, ["berry", "light", "dark", "paper"], "berry", SupportedTargets),
        new("optionColumns", "Number of columns for MCQ options", LayoutPropertyType.Integer, 1, 2, [], "1", ["mcq"]),
        new("optionGapPx", "Vertical/grid gap between MCQ options in pixels", LayoutPropertyType.Integer, 6, 32, [], "12", ["mcq"]),
        new("defaultTextPosition", "Default flashcard text vs image stacking", LayoutPropertyType.Enum, null, null, ["belowImage", "aboveImage"], "belowImage", ["flashcard"]),
        new("flipDurationMs", "Flashcard 3D flip animation duration in milliseconds", LayoutPropertyType.Integer, 150, 1000, [], "450", ["flashcard"]),
        new("blockSpacingPx", "Vertical spacing between article blocks in pixels", LayoutPropertyType.Integer, 8, 48, [], "20", ["article"])
    ];

    public static bool IsValidTargetType(string targetType) =>
        SupportedTargets.Contains(targetType.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsGlobalOnlyProperty(string propertyName) =>
        GlobalOnlyProperties.Contains(propertyName.Trim(), StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string> StripGlobalOnlyProperties(IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in values)
        {
            if (!IsGlobalOnlyProperty(key))
                result[key] = val;
        }
        return result;
    }

    public static bool IsValidPropertyForTarget(string targetType, string propertyName) =>
        FindSpec(targetType.Trim().ToLowerInvariant(), propertyName) is not null;

    public static IReadOnlyList<LayoutPropertySpec> GetSpecsForTarget(string targetType) =>
        AllSpecs.Where(s => s.ApplicableTargets.Contains(targetType.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();

    public static string ResolveDeckGlobalValue(
        IReadOnlyDictionary<string, LayoutProfile> layouts,
        string propertyName,
        string fallbackValue)
    {
        string? firstPresent = null;
        foreach (var target in new[] { "mcq", "article", "flashcard" })
        {
            if (!layouts.TryGetValue(target, out var profile)) continue;
            var value = profile.GetValue(propertyName);
            if (string.IsNullOrWhiteSpace(value)) continue;
            firstPresent ??= value;
            if (!string.Equals(value, fallbackValue, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return firstPresent ?? fallbackValue;
    }

    public static Result<(string CanonicalName, string Value)> ValidateAndResolve(string targetType, string propertyName, string rawValue)
    {
        var normalizedTarget = targetType.Trim().ToLowerInvariant();
        if (!IsValidTargetType(normalizedTarget))
            return LayoutErrors.InvalidTargetType(targetType);

        var spec = FindSpec(normalizedTarget, propertyName);
        if (spec is null)
            return LayoutErrors.UnknownProperty(normalizedTarget, propertyName);

        if (spec.ValueType == LayoutPropertyType.Integer)
        {
            if (!int.TryParse(rawValue.Trim(), out int parsed) || parsed < spec.MinValue || parsed > spec.MaxValue)
                return LayoutErrors.OutOfBounds(spec.Name, rawValue, $"{spec.MinValue}..{spec.MaxValue}");
            return (spec.Name, parsed.ToString());
        }

        var matched = spec.AllowedValues.FirstOrDefault(v => string.Equals(v, rawValue.Trim(), StringComparison.OrdinalIgnoreCase));
        return matched is null
            ? LayoutErrors.OutOfBounds(spec.Name, rawValue, string.Join(" | ", spec.AllowedValues))
            : (spec.Name, matched);
    }

    public static Result<(string CanonicalName, string Value)> ValidateAndResolve(IReadOnlyList<string> targetTypes, string propertyName, string rawValue)
    {
        Result<(string CanonicalName, string Value)>? firstError = null;
        Result<(string CanonicalName, string Value)>? outOfBoundsError = null;
        foreach (var target in targetTypes)
        {
            var res = ValidateAndResolve(target, propertyName, rawValue);
            if (res.IsSuccess) return res;
            firstError ??= res;
            if (outOfBoundsError is null && string.Equals(res.Error?.Code, "Layout.OutOfBounds", StringComparison.Ordinal))
                outOfBoundsError = res;
        }
        return outOfBoundsError ?? firstError ?? LayoutErrors.UnknownProperty(targetTypes.FirstOrDefault() ?? string.Empty, propertyName);
    }

    public static Result<(string CanonicalName, string Value)> ValidateAndResolveForEntityOverride(string targetType, string propertyName, string rawValue)
    {
        if (IsGlobalOnlyProperty(propertyName))
        {
            var canonical = GlobalOnlyProperties.First(p => string.Equals(p, propertyName.Trim(), StringComparison.OrdinalIgnoreCase));
            return LayoutErrors.EntityGlobalPropertyNotAllowed(canonical);
        }
        return ValidateAndResolve(targetType, propertyName, rawValue);
    }

    public static Result<(string CanonicalName, string Value)> ValidateAndResolveForEntityOverride(IReadOnlyList<string> targetTypes, string propertyName, string rawValue)
    {
        if (IsGlobalOnlyProperty(propertyName))
        {
            var canonical = GlobalOnlyProperties.First(p => string.Equals(p, propertyName.Trim(), StringComparison.OrdinalIgnoreCase));
            return LayoutErrors.EntityGlobalPropertyNotAllowed(canonical);
        }
        return ValidateAndResolve(targetTypes, propertyName, rawValue);
    }

    public static Result<string> ValidateAndNormalize(string targetType, string propertyName, string rawValue)
    {
        var res = ValidateAndResolve(targetType, propertyName, rawValue);
        return res.IsSuccess ? res.Value.Value : res.Error!;
    }

    public static string GetCanonicalPropertyName(string targetType, string propertyName) =>
        FindSpec(targetType.Trim().ToLowerInvariant(), propertyName)?.Name ?? propertyName;

    private static LayoutPropertySpec? FindSpec(string normalizedTarget, string propertyName) =>
        AllSpecs.FirstOrDefault(s =>
            string.Equals(s.Name, propertyName.Trim(), StringComparison.OrdinalIgnoreCase) &&
            s.ApplicableTargets.Contains(normalizedTarget, StringComparer.OrdinalIgnoreCase));
}
