using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;

namespace Meloberry.Domain.Layouts;

public sealed class LayoutProfile
{
    private readonly Dictionary<string, string> _values;

    private LayoutProfile(string targetType, Dictionary<string, string> values)
    {
        TargetType = targetType;
        _values = values;
    }

    public string TargetType { get; private set; }
    public IReadOnlyDictionary<string, string> Values => _values;

    public static Result<LayoutProfile> CreateDefault(string targetType)
    {
        var normalized = targetType.Trim().ToLowerInvariant();
        if (!LayoutBoundsCatalog.IsValidTargetType(normalized))
            return LayoutErrors.InvalidTargetType(targetType);

        var defaults = LayoutBoundsCatalog.GetSpecsForTarget(normalized)
            .ToDictionary(s => s.Name, s => s.DefaultValue, StringComparer.OrdinalIgnoreCase);
        return new LayoutProfile(normalized, defaults);
    }

    public static LayoutProfile Rehydrate(string targetType, IReadOnlyDictionary<string, string> storedValues)
    {
        var profile = CreateDefault(targetType).Value;
        foreach (var (key, value) in storedValues)
            profile.SetProperty(key, value);
        return profile;
    }

    public Result<string> SetProperty(string propertyName, string rawValue)
    {
        var resolved = LayoutBoundsCatalog.ValidateAndResolve(TargetType, propertyName, rawValue);
        if (!resolved.IsSuccess) return resolved.Error!;
        _values[resolved.Value.CanonicalName] = resolved.Value.Value;
        return resolved.Value.Value;
    }

    public string GetValue(string propertyName) =>
        _values.TryGetValue(propertyName, out var val) ? val : string.Empty;
}
