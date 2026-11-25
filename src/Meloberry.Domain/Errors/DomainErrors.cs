using Meloberry.Domain.Common;

namespace Meloberry.Domain.Errors;

public static class IndexErrors
{
    public static Error OutOfIndexBounds(int index, int min, int max) =>
        new("Index.OutOfIndexBounds", $"Index {index} is out of bounds. Allowed 1-based range is {min}..{max}.", ErrorType.Validation);
}

public static class SessionErrors
{
    public static Error NotFound(string sessionId) => new("Session.NotFound", $"Session '{sessionId}' was not found.", ErrorType.NotFound);
    public static Error InvalidWorkingDirectory(string path) => new("Session.InvalidWorkingDirectory", $"Working directory '{path}' is invalid.", ErrorType.Validation);
    public static Error NoActiveComponent() => new("Session.NoActiveComponent", "No active component is selected in this session.", ErrorType.Conflict);
    public static Error EmptyName() => new("Session.EmptyName", "Session name cannot be empty.", ErrorType.Validation);
}

public static class ComponentErrors
{
    public static Error NotFound(string componentId) => new("Component.NotFound", $"Component '{componentId}' was not found.", ErrorType.NotFound);
    public static Error InvalidType(string type) => new("Component.InvalidType", $"Invalid component type '{type}'. Supported types: 'mcq', 'flashcard', 'mixed', 'quiz', 'article'.", ErrorType.Validation);
    public static Error WrongKind(string componentId, string expectedKind) => new("Component.WrongKind", $"Component '{componentId}' is not a {expectedKind} component.", ErrorType.Conflict);
    public static Error EmptyTitle() => new("Component.EmptyTitle", "Component title cannot be empty.", ErrorType.Validation);
}

public static class ArticleErrors
{
    public static Error BlockNotFound(string artCompId) => new("ArticleBlock.NotFound", $"Article block '{artCompId}' was not found.", ErrorType.NotFound);
    public static Error WrongBlockKind(string artCompId, string expectedKind) => new("ArticleBlock.WrongBlockKind", $"Article block '{artCompId}' is not a {expectedKind} block.", ErrorType.Conflict);
    public static Error EmptyContent() => new("ArticleBlock.EmptyContent", "Article text block content cannot be empty.", ErrorType.Validation);
    public static Error EmptyImagePath() => new("ArticleBlock.EmptyImagePath", "Article image block path cannot be empty.", ErrorType.Validation);
    public static Error InvalidCalloutTone(string rawTone) => new("ArticleBlock.InvalidCalloutTone", $"Invalid callout tone '{rawTone}'. Supported tones: 'pearl', 'clinical', 'warning'.", ErrorType.Validation, Hint: "Set tone to 'pearl', 'clinical', or 'warning'.");
    public static Error EmptyCalloutContent() => new("ArticleBlock.EmptyCalloutContent", "Callout block text content cannot be empty.", ErrorType.Validation);
    public static Error InvalidTablePayload(string reason) => new("ArticleBlock.InvalidTablePayload", $"Invalid table block payload: {reason}", ErrorType.Validation);
    public static Error InvalidDiagramExplorePayload(string reason) => new("ArticleBlock.InvalidDiagramExplorePayload", $"Invalid diagram-explore block payload: {reason}", ErrorType.Validation);
    public static Error InvalidRevealPayload(string reason) => new("ArticleBlock.InvalidRevealPayload", $"Invalid reveal block payload: {reason}", ErrorType.Validation);
}

public static class QuizErrors
{
    public static Error NotFound(string quizId) => new("Quiz.NotFound", $"Quiz '{quizId}' was not found.", ErrorType.NotFound);
    public static Error InvalidType(string quizType) => new("Quiz.InvalidType", $"Invalid quiz type '{quizType}'. Supported types: 'mcq', 'flashcard', 'mixed'.", ErrorType.Validation);
    public static Error EmptyName() => new("Quiz.EmptyName", "Quiz name cannot be empty.", ErrorType.Validation);
    public static Error QuestionTypeMismatch(string quizId, string quizType, string questionType) =>
        new("Quiz.QuestionTypeMismatch", $"Quiz '{quizId}' has quizType '{quizType}' and cannot contain '{questionType}' questions.", ErrorType.Validation, Hint: "Change quizType to 'mixed' or match the question type to the quiz type.");
}

public static class QuestionErrors
{
    public static Error NotFound(string questionId) => new("Question.NotFound", $"Question '{questionId}' was not found.", ErrorType.NotFound);
    public static Error InvalidType(string type) => new("Question.InvalidType", $"Invalid question type '{type}'. Supported types: 'mcq', 'flashcard', 'fill-blank', 'assertion-reason', 'spot-error', 'diff', 'mechanism', 'relabel', 'case', 'classify', 'match', 'numeric'.", ErrorType.Validation);
    public static Error WrongQuestionType(string questionId, string expectedType) => new("Question.WrongType", $"Question '{questionId}' is not a {expectedType} question.", ErrorType.Conflict);
    public static Error InvalidMaxOptions(int requested) => new("Question.InvalidMaxOptions", $"Option count {requested} is invalid. Must be between 2 and 26.", ErrorType.Validation);
    public static Error CannotShrinkOptionNumBelowPopulated(int requested, int highestPopulatedIndex) => new("Question.CannotShrinkOptionNum", $"Cannot set max options to {requested} because option index {highestPopulatedIndex} already exists.", ErrorType.Conflict);
    public static Error OptionEmpty(int optionIndex) => new("Question.OptionEmpty", $"Option {optionIndex} must contain text, an image, or both.", ErrorType.Validation);
    public static Error OptionNotPopulated(int optionIndex) => new("Question.OptionNotPopulated", $"Option {optionIndex} has not been set yet and cannot be marked correct.", ErrorType.Conflict);
    public static Error CannotRevertToSingleWithMultipleCorrect(int correctCount) => new("Question.MultipleCorrectActive", $"Cannot convert to single-correct while {correctCount} options are marked correct.", ErrorType.Conflict);
    public static Error EmptyText() => new("Question.EmptyText", "Question text cannot be empty.", ErrorType.Validation);
    public static Error EmptyImagePath() => new("Question.EmptyImagePath", "Question image path cannot be empty.", ErrorType.Validation);
}

public static class LayoutErrors
{
    public static Error InvalidTargetType(string targetType) => new("Layout.InvalidTargetType", $"Invalid layout target type '{targetType}'. Supported types: 'mcq', 'flashcard', 'article'.", ErrorType.Validation);
    public static Error UnknownProperty(string targetType, string property) => new("Layout.UnknownProperty", $"Layout property '{property}' is not valid for target '{targetType}'.", ErrorType.Validation);
    public static Error OutOfBounds(string property, string value, string allowedBounds) => new("Layout.OutOfBounds", $"Value '{value}' for layout property '{property}' is out of bounds. Allowed: {allowedBounds}.", ErrorType.Validation);
    public static Error EntityGlobalPropertyNotAllowed(string property) => new(
        "Layout.GlobalPropertyOnEntity",
        $"Layout property '{property}' is deck-global and cannot be set as a per-component or per-question layoutOverride.",
        ErrorType.Validation,
        Hint: $"Configure '{property}' globally via 'mby layout set <article|mcq|flashcard> {property} <value>'.");
}

public static class LintErrors
{
    public static Error FailedLintCheck(string targetId, int errorCount, string summary, object? report = null) =>
        new("Lint.Failed", $"Export blocked: '{targetId}' failed mandatory lint check with {errorCount} error(s): {summary}", ErrorType.Validation, Data: report);
}

public static class AnalysisErrors
{
    public static Error WebAppOrReportNotFound(string path) => new("Analysis.TargetNotFound", $"WebApp directory or analysis report '{path}' was not found.", ErrorType.NotFound);
    public static Error NoReportsFound(string webAppDir) => new("Analysis.NoReportsFound", $"No completed session analysis reports (*.analysis.json) were found in '{webAppDir}'. Run 'mby analyze \"{webAppDir}\" --live' or 'mby build <id> --preview' to capture a run, or place a downloaded .analysis.json in '{Path.Combine(webAppDir, "reports")}'.", ErrorType.NotFound);
    public static Error InvalidReportFormat(string path, string reason) => new("Analysis.InvalidReport", $"Invalid session analysis report '{path}': {reason}", ErrorType.Validation);
}

public static class AddressErrors
{
    public static Error InvalidAddress(string address) => new("Address.Invalid", $"Invalid entity address '{address}'. Use <id> or <wsId>#c<n>[.b<m>|.q<m>] | <setId>#q<m> | <wsId>#<ref>.", ErrorType.Validation);
    public static Error NotFound(string address) => new("Address.NotFound", $"Entity at address '{address}' was not found.", ErrorType.NotFound);
    public static Error InvalidRef(string rawRef, string? suggestedSlug = null)
    {
        var trimmed = rawRef.Trim();
        var reason = trimmed.Length > RefKeyValidator.MaxLength
            ? $"exceeds max length {RefKeyValidator.MaxLength} (got {trimmed.Length})"
            : "contains disallowed characters or an invalid leading character";

        return new(
            "Address.InvalidRef",
            $"Invalid ref '{rawRef}': {reason}. Must match pattern ^[a-z0-9][a-z0-9_-]{{0,31}}$ (lowercase ASCII letters, digits, '-' or '_').",
            ErrorType.Validation,
            Hint: string.IsNullOrWhiteSpace(suggestedSlug)
                ? "Use <=32 lowercase ASCII letters, digits, '-' or '_' starting with a letter/digit, or omit ref on components to auto-generate."
                : $"Use suggested valid ref '{suggestedSlug}' (<=32 chars), or omit ref on components to auto-generate.");
    }
    public static Error ReservedRef(string rawRef, string? suggestedSlug = null) =>
        new(
            "Address.InvalidRef",
            $"Ref '{rawRef}' conflicts with reserved positional path syntax ('c<n>' or 'q<n>').",
            ErrorType.Validation,
            Hint: string.IsNullOrWhiteSpace(suggestedSlug)
                ? "Choose a slug that is not of the form c<number> or q<number>."
                : $"Use suggested valid ref '{suggestedSlug}' instead.");
    public static Error DuplicateRef(string refKey) => new("Address.DuplicateRef", $"Duplicate ref '{refKey}' within workspace.", ErrorType.Conflict);
}
