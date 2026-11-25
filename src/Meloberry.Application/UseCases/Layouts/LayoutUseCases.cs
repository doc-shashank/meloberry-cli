using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Layouts;

namespace Meloberry.Application.UseCases.Layouts;

public sealed record GetLayoutBoundsQuery(string? TargetType = null);
public sealed record LayoutPropertyBoundDto(string TargetType, string Property, string ValueType, string CurrentValue, string DefaultValue, string AllowedBounds, string Description);
public sealed record LayoutBoundsResponse(IReadOnlyList<LayoutPropertyBoundDto> Properties);
public sealed record SetLayoutPropertyCommand(string TargetType, string Property, string Value);
public sealed record LayoutMutationResponse(string TargetIdOrType, string Property, string NormalizedValue);
public sealed record ResetLayoutProfileCommand(string TargetType, IReadOnlyList<string>? Keys = null);
public sealed record SaveLayoutProfilesCommand(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Profiles);
public sealed record SaveLayoutProfilesResponse(bool Saved, IReadOnlyList<string> UpdatedTargets, IReadOnlyList<string> Errors);

internal static class GlobalLayoutSyncHelper
{
    public static void SyncKeysInMemory(
        IDictionary<string, LayoutProfile> allProfiles,
        string sourceTarget,
        IEnumerable<string> changedCanonicalKeys,
        ISet<string> modifiedTargets)
    {
        var toSync = changedCanonicalKeys
            .Where(LayoutBoundsCatalog.IsGlobalOnlyProperty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (toSync.Count == 0 || !allProfiles.TryGetValue(sourceTarget, out var sourceProfile))
            return;

        foreach (var otherTarget in LayoutBoundsCatalog.SupportedTargets)
        {
            if (string.Equals(otherTarget, sourceTarget, StringComparison.OrdinalIgnoreCase)) continue;
            if (!allProfiles.TryGetValue(otherTarget, out var other))
            {
                other = LayoutProfile.CreateDefault(otherTarget).Value;
                allProfiles[otherTarget] = other;
            }

            foreach (var key in toSync)
            {
                var val = sourceProfile.GetValue(key);
                if (!string.IsNullOrEmpty(val) && other.SetProperty(key, val).IsSuccess)
                    modifiedTargets.Add(otherTarget);
            }
        }
    }
}

public sealed class GetLayoutBoundsHandler(ILayoutRepository layoutRepository)
{
    public async Task<Result<LayoutBoundsResponse>> HandleAsync(GetLayoutBoundsQuery query, CancellationToken cancellationToken = default)
    {
        var targets = string.IsNullOrWhiteSpace(query.TargetType)
            ? LayoutBoundsCatalog.SupportedTargets
            : [query.TargetType.Trim().ToLowerInvariant()];

        foreach (var target in targets)
            if (!LayoutBoundsCatalog.IsValidTargetType(target)) return LayoutErrors.InvalidTargetType(target);

        var allProfiles = await layoutRepository.GetAllProfilesAsync(cancellationToken);
        var items = new List<LayoutPropertyBoundDto>();
        foreach (var target in targets)
        {
            var profile = allProfiles.TryGetValue(target, out var p) ? p : LayoutProfile.CreateDefault(target).Value;
            foreach (var spec in LayoutBoundsCatalog.GetSpecsForTarget(target))
            {
                var boundsText = spec.ValueType == LayoutPropertyType.Integer ? $"{spec.MinValue}..{spec.MaxValue}" : string.Join(" | ", spec.AllowedValues);
                items.Add(new LayoutPropertyBoundDto(target, spec.Name, spec.ValueType == LayoutPropertyType.Integer ? "integer" : "enum",
                    profile.GetValue(spec.Name), spec.DefaultValue, boundsText, spec.Description));
            }
        }
        return new LayoutBoundsResponse(items);
    }
}

public sealed class SetLayoutPropertyHandler(ILayoutRepository layoutRepository)
{
    public async Task<Result<LayoutMutationResponse>> HandleAsync(SetLayoutPropertyCommand command, CancellationToken cancellationToken = default)
    {
        var normalizedType = command.TargetType.Trim().ToLowerInvariant();
        if (!LayoutBoundsCatalog.IsValidTargetType(normalizedType)) return LayoutErrors.InvalidTargetType(command.TargetType);

        var resolved = LayoutBoundsCatalog.ValidateAndResolve(normalizedType, command.Property, command.Value);
        if (!resolved.IsSuccess) return resolved.Error!;

        var allProfiles = new Dictionary<string, LayoutProfile>(
            await layoutRepository.GetAllProfilesAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        if (!allProfiles.TryGetValue(normalizedType, out var profile))
        {
            profile = LayoutProfile.CreateDefault(normalizedType).Value;
            allProfiles[normalizedType] = profile;
        }

        profile.SetProperty(resolved.Value.CanonicalName, resolved.Value.Value);
        var modifiedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedType };
        GlobalLayoutSyncHelper.SyncKeysInMemory(allProfiles, normalizedType, [resolved.Value.CanonicalName], modifiedTargets);

        var toSave = modifiedTargets.Select(t => allProfiles[t]).ToList();
        await layoutRepository.SaveProfilesAsync(toSave, cancellationToken);
        return new LayoutMutationResponse(normalizedType, resolved.Value.CanonicalName, resolved.Value.Value);
    }
}

public sealed class SaveLayoutProfilesHandler(ILayoutRepository layoutRepository)
{
    public async Task<Result<SaveLayoutProfilesResponse>> HandleAsync(SaveLayoutProfilesCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Profiles is null || command.Profiles.Count == 0)
            return new Error("Layout.EmptyUpdate", "Provide at least one profile update.", ErrorType.Validation);

        var errors = new List<string>();
        var validatedUpdates = new List<(string Target, string CanonicalKey, string NormalizedValue)>();

        foreach (var (rawTarget, propMap) in command.Profiles)
        {
            var normalizedTarget = rawTarget.Trim().ToLowerInvariant();
            if (!LayoutBoundsCatalog.IsValidTargetType(normalizedTarget))
            {
                errors.Add($"{rawTarget}: {LayoutErrors.InvalidTargetType(rawTarget).Description}");
                continue;
            }

            if (propMap is null)
            {
                errors.Add($"{normalizedTarget}: Profile update must be a key/value object.");
                continue;
            }

            foreach (var (propName, rawVal) in propMap)
            {
                var res = LayoutBoundsCatalog.ValidateAndResolve(normalizedTarget, propName, rawVal ?? string.Empty);
                if (!res.IsSuccess)
                    errors.Add($"{normalizedTarget}.{propName}: {res.Error!.Description}");
                else
                    validatedUpdates.Add((normalizedTarget, res.Value.CanonicalName, res.Value.Value));
            }
        }

        if (errors.Count > 0)
            return new SaveLayoutProfilesResponse(Saved: false, UpdatedTargets: [], Errors: errors);

        if (validatedUpdates.Count == 0)
            return new SaveLayoutProfilesResponse(Saved: true, UpdatedTargets: [], Errors: []);

        var allProfiles = new Dictionary<string, LayoutProfile>(
            await layoutRepository.GetAllProfilesAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        var modifiedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (target, canonicalKey, normalizedValue) in validatedUpdates)
        {
            if (!allProfiles.TryGetValue(target, out var profile))
            {
                profile = LayoutProfile.CreateDefault(target).Value;
                allProfiles[target] = profile;
            }

            profile.SetProperty(canonicalKey, normalizedValue);
            modifiedTargets.Add(target);
            if (LayoutBoundsCatalog.IsGlobalOnlyProperty(canonicalKey))
                globalOverrides[canonicalKey] = normalizedValue;
        }

        if (globalOverrides.Count > 0)
        {
            foreach (var target in LayoutBoundsCatalog.SupportedTargets)
            {
                if (!allProfiles.TryGetValue(target, out var profile))
                {
                    profile = LayoutProfile.CreateDefault(target).Value;
                    allProfiles[target] = profile;
                }

                foreach (var (gKey, gVal) in globalOverrides)
                {
                    if (profile.SetProperty(gKey, gVal).IsSuccess)
                        modifiedTargets.Add(target);
                }
            }
        }

        var toSave = modifiedTargets.Select(t => allProfiles[t]).ToList();
        await layoutRepository.SaveProfilesAsync(toSave, cancellationToken);
        return new SaveLayoutProfilesResponse(Saved: true, UpdatedTargets: [.. modifiedTargets], Errors: []);
    }
}

public sealed class ResetLayoutProfileHandler(ILayoutRepository layoutRepository)
{
    public async Task<Result<LayoutProfile>> HandleAsync(ResetLayoutProfileCommand command, CancellationToken cancellationToken = default)
    {
        var normalizedType = command.TargetType.Trim().ToLowerInvariant();
        if (!LayoutBoundsCatalog.IsValidTargetType(normalizedType)) return LayoutErrors.InvalidTargetType(command.TargetType);

        var defaults = LayoutProfile.CreateDefault(normalizedType).Value;
        var canonicalKeysToReset = new List<string>();

        if (command.Keys is not null)
        {
            if (command.Keys.Count == 0)
                return new Error("Layout.EmptyResetKeys", "Specify at least one property key to reset, or omit 'keys' to reset the entire profile.", ErrorType.Validation);

            foreach (var key in command.Keys)
            {
                if (string.IsNullOrWhiteSpace(key) || !LayoutBoundsCatalog.IsValidPropertyForTarget(normalizedType, key))
                    return LayoutErrors.UnknownProperty(normalizedType, key ?? string.Empty);

                var canonical = LayoutBoundsCatalog.GetCanonicalPropertyName(normalizedType, key);
                if (!canonicalKeysToReset.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                    canonicalKeysToReset.Add(canonical);
            }
        }
        else
        {
            canonicalKeysToReset.AddRange(defaults.Values.Keys);
        }

        var allProfiles = new Dictionary<string, LayoutProfile>(
            await layoutRepository.GetAllProfilesAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        LayoutProfile updated;
        if (command.Keys is not null)
        {
            updated = allProfiles.TryGetValue(normalizedType, out var existing)
                ? existing
                : LayoutProfile.CreateDefault(normalizedType).Value;
            allProfiles[normalizedType] = updated;

            foreach (var canonical in canonicalKeysToReset)
                updated.SetProperty(canonical, defaults.GetValue(canonical));
        }
        else
        {
            updated = defaults;
            allProfiles[normalizedType] = updated;
        }

        var modifiedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedType };
        GlobalLayoutSyncHelper.SyncKeysInMemory(allProfiles, normalizedType, canonicalKeysToReset, modifiedTargets);

        var toSave = modifiedTargets.Select(t => allProfiles[t]).ToList();
        await layoutRepository.SaveProfilesAsync(toSave, cancellationToken);
        return updated;
    }
}
