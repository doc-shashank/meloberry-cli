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
    public static Error WrongBlockKind(string artCompId, string expectedKind) => new("ArticleBlock.WrongKind", $"Article block '{artCompId}' is not a {expectedKind} block.", ErrorType.Conflict);
    public static Error EmptyContent() => new("ArticleBlock.EmptyContent", "Article text block content cannot be empty.", ErrorType.Validation);
}

public static class QuizErrors
{
    public static Error NotFound(string quizId) => new("Quiz.NotFound", $"Quiz '{quizId}' was not found.", ErrorType.NotFound);
    public static Error InvalidType(string quizType) => new("Quiz.InvalidType", $"Invalid quiz type '{quizType}'. Supported types: 'mcq', 'flashcard', 'mixed'.", ErrorType.Validation);
    public static Error EmptyName() => new("Quiz.EmptyName", "Quiz name cannot be empty.", ErrorType.Validation);
}

public static class QuestionErrors
{
    public static Error NotFound(string questionId) => new("Question.NotFound", $"Question '{questionId}' was not found.", ErrorType.NotFound);
    public static Error InvalidType(string type) => new("Question.InvalidType", $"Invalid question type '{type}'. Supported types: 'mcq', 'flashcard'.", ErrorType.Validation);
    public static Error WrongQuestionType(string questionId, string expectedType) => new("Question.WrongType", $"Question '{questionId}' is not a {expectedType} question.", ErrorType.Conflict);
    public static Error InvalidMaxOptions(int requested) => new("Question.InvalidMaxOptions", $"Option count {requested} is invalid. Must be between 2 and 26.", ErrorType.Validation);
    public static Error CannotShrinkOptionNumBelowPopulated(int requested, int highestPopulatedIndex) => new("Question.CannotShrinkOptionNum", $"Cannot set max options to {requested} because option index {highestPopulatedIndex} already exists.", ErrorType.Conflict);
    public static Error OptionEmpty(int optionIndex) => new("Question.OptionEmpty", $"Option {optionIndex} must contain text, an image, or both.", ErrorType.Validation);
    public static Error OptionNotPopulated(int optionIndex) => new("Question.OptionNotPopulated", $"Option {optionIndex} has not been set yet and cannot be marked correct.", ErrorType.Conflict);
    public static Error CannotRevertToSingleWithMultipleCorrect(int correctCount) => new("Question.MultipleCorrectActive", $"Cannot convert to single-correct while {correctCount} options are marked correct.", ErrorType.Conflict);
    public static Error EmptyText() => new("Question.EmptyText", "Question text cannot be empty.", ErrorType.Validation);
}

public static class LayoutErrors
{
    public static Error InvalidTargetType(string targetType) => new("Layout.InvalidTargetType", $"Invalid layout target type '{targetType}'. Supported types: 'mcq', 'flashcard', 'article'.", ErrorType.Validation);
    public static Error UnknownProperty(string targetType, string property) => new("Layout.UnknownProperty", $"Layout property '{property}' is not valid for target '{targetType}'.", ErrorType.Validation);
    public static Error OutOfBounds(string property, string value, string allowedBounds) => new("Layout.OutOfBounds", $"Value '{value}' for layout property '{property}' is out of bounds. Allowed: {allowedBounds}.", ErrorType.Validation);
}

public static class LintErrors
{
    public static Error FailedLintCheck(string targetId, int errorCount, string summary) =>
        new("Lint.Failed", $"Export blocked: '{targetId}' failed mandatory lint check with {errorCount} error(s): {summary}", ErrorType.Validation);
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
    public static Error InvalidRef(string rawRef) => new("Address.InvalidRef", $"Invalid ref '{rawRef}'. Use 1..32 lowercase chars matching ^[a-z0-9][a-z0-9_-]*$.", ErrorType.Validation);
    public static Error DuplicateRef(string refKey) => new("Address.DuplicateRef", $"Duplicate ref '{refKey}' within workspace.", ErrorType.Conflict);
}
