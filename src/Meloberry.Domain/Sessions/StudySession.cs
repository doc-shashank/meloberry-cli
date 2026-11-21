using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Quizzes;

namespace Meloberry.Domain.Sessions;

public sealed class StudySession
{
    private readonly List<SessionComponent> _components;

    private StudySession(
        string id,
        string workingDirectory,
        string? activeComponentId,
        List<SessionComponent> components,
        string? name = null,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        Id = id;
        WorkingDirectory = workingDirectory;
        ActiveComponentId = activeComponentId;
        Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
        Description = description?.Trim() ?? string.Empty;
        _components = components;
        IndexedListReorderer.SortInPlace(_components);
    }

    public string Id { get; private set; }
    public string WorkingDirectory { get; private set; }
    public string? ActiveComponentId { get; private set; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public IReadOnlyList<SessionComponent> Components => _components;

    public static Result<StudySession> Create(string id, string workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory)
            ? SessionErrors.InvalidWorkingDirectory(workingDirectory)
            : new StudySession(id, workingDirectory, null, [], id, string.Empty);

    public static StudySession Rehydrate(
        string id,
        string workingDirectory,
        string? activeComponentId,
        IEnumerable<SessionComponent> components,
        string? name = null,
        string? description = null) =>
        new(id, workingDirectory, activeComponentId, [.. components], name, description);

    public Result SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return SessionErrors.EmptyName();
        Name = name.Trim();
        return Result.Success();
    }

    public Result SetDescription(string description)
    {
        Description = (description ?? string.Empty).Trim();
        return Result.Success();
    }

    public Result<SessionComponent> AddComponent(string componentId, string rawType, int? targetIndex = null)
    {
        var normalized = rawType.Trim().ToLowerInvariant();
        SessionComponent component;
        if (normalized == "article")
            component = SessionComponent.CreateArticle(componentId, Id, 1);
        else if (Quiz.TryParseQuizType(normalized, out var quizType))
            component = SessionComponent.CreateQuiz(componentId, Id, 1, quizType, WorkingDirectory);
        else
            return ComponentErrors.InvalidType(rawType);

        var insertResult = IndexedListReorderer.InsertAt(_components, component, targetIndex);
        if (!insertResult.IsSuccess) return insertResult.Error!;
        ActiveComponentId = component.Id;
        return component;
    }

    public Result ActivateComponent(string componentId)
    {
        var comp = FindComponent(componentId);
        if (comp is null) return ComponentErrors.NotFound(componentId);
        ActiveComponentId = comp.Id;
        return Result.Success();
    }

    public Result ReorderComponent(string componentId, int targetIndex, ReorderMode mode) =>
        IndexedListReorderer.Reorder(_components, componentId, targetIndex, mode, ComponentErrors.NotFound(componentId));

    public Result DeleteComponent(string componentId)
    {
        var result = IndexedListReorderer.RemoveAndShiftUp(_components, componentId, ComponentErrors.NotFound(componentId));
        if (!result.IsSuccess) return result;
        if (string.Equals(ActiveComponentId, componentId, StringComparison.Ordinal))
            ActiveComponentId = _components.FirstOrDefault()?.Id;
        return Result.Success();
    }

    public SessionComponent? FindComponent(string componentId) =>
        _components.FirstOrDefault(c => string.Equals(c.Id, componentId, StringComparison.Ordinal));

    public SessionComponent? GetActiveComponent() =>
        ActiveComponentId is null ? null : FindComponent(ActiveComponentId);
}
