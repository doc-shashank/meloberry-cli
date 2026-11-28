using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Questions;
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

    public Result ReorderComponentsExact(IReadOnlyList<string> orderedComponentIds) =>
        IndexedListReorderer.ApplyExactOrder(_components, orderedComponentIds, "component");

    public Result<int> AttachComponent(SessionComponent component, int? targetIndex = null)
    {
        var insertResult = IndexedListReorderer.InsertAt(_components, component, targetIndex);
        if (!insertResult.IsSuccess) return insertResult.Error!;
        ActiveComponentId = component.Id;
        return insertResult.Value;
    }

    public Result<int> TransferBlock(string blockId, string sourceCompId, string targetCompId, int? targetIndex = null)
    {
        var src = FindComponent(sourceCompId);
        var dst = FindComponent(targetCompId);
        if (src is null) return ComponentErrors.NotFound(sourceCompId);
        if (dst is null) return ComponentErrors.NotFound(targetCompId);
        if (src.Kind != ComponentKind.Article) return ComponentErrors.WrongKind(src.Id, "article");
        if (dst.Kind != ComponentKind.Article) return ComponentErrors.WrongKind(dst.Id, "article");

        if (src.Id == dst.Id)
        {
            if (src.FindBlock(blockId) is null) return ArticleErrors.BlockNotFound(blockId);
            int maxSame = src.ArticleBlocks.Count;
            if (targetIndex is int req && (req < 1 || req > maxSame + 1))
                return IndexErrors.OutOfIndexBounds(req, 1, maxSame + 1);
            int idx = Math.Clamp(targetIndex ?? maxSame, 1, maxSame);
            var same = src.ReorderBlock(blockId, idx, ReorderMode.Shift);
            return same.IsSuccess ? idx : Result<int>.Failure(same.Error!);
        }

        if (src.FindBlock(blockId) is null) return ArticleErrors.BlockNotFound(blockId);
        int maxIdx = dst.ArticleBlocks.Count + 1;
        int insertIdx = targetIndex ?? maxIdx;
        if (insertIdx < 1 || insertIdx > maxIdx) return IndexErrors.OutOfIndexBounds(insertIdx, 1, maxIdx);

        var detached = src.DetachBlock(blockId);
        if (!detached.IsSuccess) return detached.Error!;
        return dst.AttachBlock(detached.Value, insertIdx);
    }

    public Result<int> TransferQuestion(string questionId, string sourceCompId, string targetCompId, int? targetIndex = null)
    {
        var src = FindComponent(sourceCompId);
        var dst = FindComponent(targetCompId);
        if (src is null) return ComponentErrors.NotFound(sourceCompId);
        if (dst is null) return ComponentErrors.NotFound(targetCompId);
        if (src.Kind != ComponentKind.Quiz || src.Quiz is null) return ComponentErrors.WrongKind(src.Id, "quiz");
        if (dst.Kind != ComponentKind.Quiz || dst.Quiz is null) return ComponentErrors.WrongKind(dst.Id, "quiz");

        var existing = src.Quiz.FindQuestion(questionId);
        if (existing is null) return QuestionErrors.NotFound(questionId);

        if (src.Id == dst.Id)
        {
            int maxSame = src.Quiz.Questions.Count;
            if (targetIndex is int req && (req < 1 || req > maxSame + 1))
                return IndexErrors.OutOfIndexBounds(req, 1, maxSame + 1);
            int idx = Math.Clamp(targetIndex ?? maxSame, 1, maxSame);
            var same = src.Quiz.ReorderQuestion(questionId, idx, ReorderMode.Shift);
            return same.IsSuccess ? idx : Result<int>.Failure(same.Error!);
        }

        if (dst.Quiz.Type == QuizType.Mcq && existing.Type != QuestionType.Mcq)
            return QuizErrors.QuestionTypeMismatch(dst.Quiz.Id, "mcq", Question.ToWireString(existing.Type));
        if (dst.Quiz.Type == QuizType.Flashcard && existing.Type != QuestionType.Flashcard)
            return QuizErrors.QuestionTypeMismatch(dst.Quiz.Id, "flashcard", Question.ToWireString(existing.Type));

        int maxIdx = dst.Quiz.Questions.Count + 1;
        int insertIdx = targetIndex ?? maxIdx;
        if (insertIdx < 1 || insertIdx > maxIdx) return IndexErrors.OutOfIndexBounds(insertIdx, 1, maxIdx);

        var detached = src.Quiz.DetachQuestion(questionId);
        if (!detached.IsSuccess) return detached.Error!;
        detached.Value.ReassignContainerInternal(dst.Quiz.Id);
        var ins = dst.Quiz.InsertQuestion(detached.Value, insertIdx);
        return ins.IsSuccess ? insertIdx : Result<int>.Failure(ins.Error!);
    }

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
