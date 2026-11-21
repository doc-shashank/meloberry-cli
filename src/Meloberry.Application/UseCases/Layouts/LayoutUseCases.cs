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
public sealed record SetEntityLayoutCommand(string TargetEntityId, string Property, string Value);

internal static class GlobalLayoutSyncHelper
{
    private static readonly string[] GlobalKeys = ["themeMode", "slideTransitionMs"];

    public static async Task SyncKeysIfGlobalAsync(
        ILayoutRepository repo, string sourceTarget, LayoutProfile sourceProfile, IEnumerable<string> changedCanonicalKeys, CancellationToken ct)
    {
        var toSync = changedCanonicalKeys.Where(k => GlobalKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (toSync.Count == 0) return;

        foreach (var otherTarget in LayoutBoundsCatalog.SupportedTargets)
        {
            if (string.Equals(otherTarget, sourceTarget, StringComparison.OrdinalIgnoreCase)) continue;
            var other = await repo.GetProfileAsync(otherTarget, ct);
            bool modified = false;
            foreach (var key in toSync)
            {
                if (other.SetProperty(key, sourceProfile.GetValue(key)).IsSuccess)
                    modified = true;
            }
            if (modified) await repo.SaveProfileAsync(other, ct);
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
                items.Add(new LayoutPropertyBoundDto(target, spec.Name, spec.ValueType == LayoutPropertyType.Integer ? "int" : "enum",
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

        var profile = await layoutRepository.GetProfileAsync(normalizedType, cancellationToken);
        var resolved = LayoutBoundsCatalog.ValidateAndResolve(normalizedType, command.Property, command.Value);
        if (!resolved.IsSuccess) return resolved.Error!;

        profile.SetProperty(resolved.Value.CanonicalName, resolved.Value.Value);
        await layoutRepository.SaveProfileAsync(profile, cancellationToken);
        await GlobalLayoutSyncHelper.SyncKeysIfGlobalAsync(layoutRepository, normalizedType, profile, [resolved.Value.CanonicalName], cancellationToken);
        return new LayoutMutationResponse(normalizedType, resolved.Value.CanonicalName, resolved.Value.Value);
    }
}

public sealed class ResetLayoutProfileHandler(ILayoutRepository layoutRepository)
{
    public async Task<Result<LayoutProfile>> HandleAsync(ResetLayoutProfileCommand command, CancellationToken cancellationToken = default)
    {
        var normalizedType = command.TargetType.Trim().ToLowerInvariant();
        if (!LayoutBoundsCatalog.IsValidTargetType(normalizedType)) return LayoutErrors.InvalidTargetType(command.TargetType);

        var defaults = LayoutProfile.CreateDefault(normalizedType).Value;
        LayoutProfile updated;
        List<string> resetKeys;

        if (command.Keys is { Count: > 0 })
        {
            updated = await layoutRepository.GetProfileAsync(normalizedType, cancellationToken);
            resetKeys = [];
            foreach (var key in command.Keys)
            {
                var canonical = LayoutBoundsCatalog.GetCanonicalPropertyName(normalizedType, key);
                var defVal = defaults.GetValue(canonical);
                if (!string.IsNullOrEmpty(defVal) && updated.SetProperty(canonical, defVal).IsSuccess)
                    resetKeys.Add(canonical);
            }
        }
        else
        {
            updated = defaults;
            resetKeys = [.. defaults.Values.Keys];
        }

        await layoutRepository.SaveProfileAsync(updated, cancellationToken);
        await GlobalLayoutSyncHelper.SyncKeysIfGlobalAsync(layoutRepository, normalizedType, updated, resetKeys, cancellationToken);
        return updated;
    }
}

public sealed class SetEntityLayoutHandler(IWorkspaceRepository workspaceRepository)
{
    public async Task<Result<LayoutMutationResponse>> HandleAsync(SetEntityLayoutCommand command, CancellationToken cancellationToken = default)
    {
        var questionOwner = await workspaceRepository.FindQuestionOwnerAsync(command.TargetEntityId, cancellationToken);
        if (questionOwner is not null)
        {
            var resolved = LayoutBoundsCatalog.ValidateAndResolve(questionOwner.Question.LayoutTargetKey, command.Property, command.Value);
            if (!resolved.IsSuccess) return resolved.Error!;
            questionOwner.Question.SetLayoutOverride(resolved.Value.CanonicalName, resolved.Value.Value);
            await workspaceRepository.SaveOwnerAsync(questionOwner.ToQuizOwner(), cancellationToken);
            return new LayoutMutationResponse(questionOwner.Question.Id, resolved.Value.CanonicalName, resolved.Value.Value);
        }

        var compLookup = await workspaceRepository.FindComponentAsync(command.TargetEntityId, cancellationToken);
        if (compLookup is not null)
        {
            var setRes = compLookup.Component.SetLayoutOverride(command.Property, command.Value);
            if (!setRes.IsSuccess) return setRes.Error!;
            await workspaceRepository.SaveSessionAsync(compLookup.Session, cancellationToken);
            return new LayoutMutationResponse(compLookup.Component.Id, setRes.Value.CanonicalName, setRes.Value.Value);
        }

        return new Error("Layout.EntityNotFound", $"Question or component '{command.TargetEntityId}' was not found.", ErrorType.NotFound);
    }
}
