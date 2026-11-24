using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Questions;

namespace Meloberry.Domain.Quizzes;

public enum QuizType { Mcq, Flashcard, Mixed }

public sealed class Quiz
{
    private readonly List<Question> _questions;

    private Quiz(
        string id,
        QuizType type,
        string workingDirectory,
        List<Question> questions,
        string? name = null,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        Id = id;
        Type = type;
        WorkingDirectory = workingDirectory;
        Name = name?.Trim() ?? string.Empty;
        Description = description?.Trim() ?? string.Empty;
        _questions = questions;
        IndexedListReorderer.SortInPlace(_questions);
    }

    public string Id { get; private set; }
    public QuizType Type { get; private set; }
    public string WorkingDirectory { get; private set; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public IReadOnlyList<Question> Questions => _questions;

    public static Result<Quiz> Create(string id, string quizTypeText, string workingDirectory)
    {
        if (!TryParseQuizType(quizTypeText, out var quizType))
            return QuizErrors.InvalidType(quizTypeText);
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return SessionErrors.InvalidWorkingDirectory(workingDirectory);
        return new Quiz(id, quizType, workingDirectory, []);
    }

    public static Quiz CreateForComponent(string componentId, QuizType quizType, string workingDirectory) =>
        new(componentId, quizType, workingDirectory, []);

    public static Quiz Rehydrate(
        string id,
        QuizType type,
        string workingDirectory,
        IEnumerable<Question> questions,
        string? name = null,
        string? description = null) =>
        new(id, type, workingDirectory, [.. questions], name, description);

    public Result SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return QuizErrors.EmptyName();
        Name = name.Trim();
        return Result.Success();
    }

    public Result SetDescription(string description)
    {
        Description = (description ?? string.Empty).Trim();
        return Result.Success();
    }

    public static bool TryParseQuizType(string? raw, out QuizType quizType)
    {
        (bool ok, quizType) = raw?.Trim().ToLowerInvariant() switch
        {
            "mcq" => (true, QuizType.Mcq),
            "flashcard" => (true, QuizType.Flashcard),
            "mixed" or "quiz" => (true, QuizType.Mixed),
            _ => (false, default(QuizType))
        };
        return ok;
    }

    public static bool TryParseQuestionType(string? raw, out QuestionType questionType)
    {
        (bool ok, questionType) = raw?.Trim().ToLowerInvariant() switch
        {
            "mcq" => (true, QuestionType.Mcq),
            "flashcard" => (true, QuestionType.Flashcard),
            "fill-blank" or "fillblank" => (true, QuestionType.FillBlank),
            "assertion-reason" or "assertionreason" => (true, QuestionType.AssertionReason),
            "spot-error" or "spoterror" => (true, QuestionType.SpotError),
            "diff" or "differentiation" => (true, QuestionType.Differentiation),
            "mechanism" => (true, QuestionType.Mechanism),
            "relabel" or "relabel-image" or "relabelimage" => (true, QuestionType.RelabelImage),
            _ => (false, default(QuestionType))
        };
        return ok;
    }

    public Result<Question> AddQuestion(string questionId, QuestionType type, int? targetIndex = null)
    {
        Question question = type switch
        {
            QuestionType.Mcq => McqQuestion.CreateNew(questionId, Id, 1),
            QuestionType.Flashcard => FlashcardQuestion.CreateNew(questionId, Id, 1),
            _ => PayloadQuestion.Rehydrate(questionId, Id, 1, type, "{}")
        };
        return InsertQuestion(question, targetIndex);
    }

    public Result<Question> InsertQuestion(Question question, int? targetIndex = null)
    {
        if (Type == QuizType.Mcq && question.Type != QuestionType.Mcq)
            return QuizErrors.QuestionTypeMismatch(Id, "mcq", Question.ToWireString(question.Type));
        if (Type == QuizType.Flashcard && question.Type != QuestionType.Flashcard)
            return QuizErrors.QuestionTypeMismatch(Id, "flashcard", Question.ToWireString(question.Type));

        var insertResult = IndexedListReorderer.InsertAt(_questions, question, targetIndex);
        return insertResult.IsSuccess ? question : insertResult.Error!;
    }

    public Result ReplaceQuestionAt(string existingQuestionId, Question replacement, int? targetIndex = null)
    {
        if (Type == QuizType.Mcq && replacement.Type != QuestionType.Mcq)
            return QuizErrors.QuestionTypeMismatch(Id, "mcq", Question.ToWireString(replacement.Type));
        if (Type == QuizType.Flashcard && replacement.Type != QuestionType.Flashcard)
            return QuizErrors.QuestionTypeMismatch(Id, "flashcard", Question.ToWireString(replacement.Type));

        int currentIdx = _questions.FindIndex(q => string.Equals(q.Id, existingQuestionId, StringComparison.Ordinal));
        if (currentIdx < 0) return QuestionErrors.NotFound(existingQuestionId);

        int preservedIndex = _questions[currentIdx].Index;
        int desiredIndex = targetIndex ?? preservedIndex;
        if (desiredIndex < 1 || desiredIndex > _questions.Count)
            return IndexErrors.OutOfIndexBounds(desiredIndex, 1, _questions.Count);

        ((IIndexedEntity)replacement).SetIndexInternal(preservedIndex);
        _questions[currentIdx] = replacement;
        if (desiredIndex != preservedIndex)
            return IndexedListReorderer.Reorder(_questions, replacement.Id, desiredIndex, ReorderMode.Shift, QuestionErrors.NotFound(replacement.Id));

        IndexedListReorderer.SortInPlace(_questions);
        return Result.Success();
    }

    public Result DeleteQuestion(string questionId) =>
        IndexedListReorderer.RemoveAndShiftUp(_questions, questionId, QuestionErrors.NotFound(questionId));

    public Result ReorderQuestion(string questionId, int targetIndex, ReorderMode mode) =>
        IndexedListReorderer.Reorder(_questions, questionId, targetIndex, mode, QuestionErrors.NotFound(questionId));

    public Question? FindQuestion(string questionId) =>
        _questions.FirstOrDefault(q => string.Equals(q.Id, questionId, StringComparison.Ordinal));
}
