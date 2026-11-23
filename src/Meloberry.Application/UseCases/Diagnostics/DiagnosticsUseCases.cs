using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;

namespace Meloberry.Application.UseCases.Diagnostics;

public sealed record LintCommand(string Address, bool Quiet = false, bool Verbose = false);
public sealed record LintIssue(string Severity, string Code, string TargetId, string Message);
public sealed record LintReportResponse(string TargetId, bool IsClean, int ErrorCount, int WarningCount, IReadOnlyList<LintIssue> Issues);

internal sealed record ResolvedWorkspaceLint(StudySession? Session, Quiz? Quiz, string StorageDirectory, LintReportResponse Report);

public sealed class LintHandler(
    EntityAddressResolver addressResolver,
    IWorkingDirectoryProvider workingDirectoryProvider,
    IContentInspector contentInspector)
{
    public async Task<Result<LintReportResponse>> HandleAsync(LintCommand command, CancellationToken cancellationToken = default)
    {
        var resolved = await addressResolver.ResolveAsync(command.Address, cancellationToken);
        if (!resolved.IsSuccess)
            return new Error("Lint.TargetNotFound", $"Target '{command.Address}' was not found.", ErrorType.NotFound);

        var entity = resolved.Value;
        var issues = new List<LintIssue>();

        switch (entity.EntityKind)
        {
            case "session":
                await LintSessionAsync(entity.Session!, entity.StorageDirectory, issues, cancellationToken);
                break;
            case "component":
                CheckAssetDirectory(entity.StorageDirectory, entity.Component!.Id, issues);
                await LintComponentAsync(entity.Component!, entity.StorageDirectory, issues, cancellationToken);
                break;
            case "articleBlock":
                CheckAssetDirectory(entity.StorageDirectory, entity.ArticleBlock!.Id, issues);
                await LintArticleBlockAsync(entity.ArticleBlock!, entity.StorageDirectory, issues, cancellationToken);
                break;
            case "quiz":
                CheckAssetDirectory(entity.StorageDirectory, entity.Quiz!.Id, issues);
                await LintQuizAsync(entity.Quiz!, entity.StorageDirectory, issues, cancellationToken);
                break;
            case "question":
                CheckAssetDirectory(entity.StorageDirectory, entity.Question!.Id, issues);
                await LintQuestionAsync(entity.Question!, entity.StorageDirectory, issues, cancellationToken);
                break;
        }

        var report = BuildReport(entity.EntityId, issues);
        if (command.Quiet && report.ErrorCount == 0 && !command.Verbose)
            return report with { Issues = [] };
        return report;
    }

    internal async Task<Result<ResolvedWorkspaceLint>> LintAndResolveWorkspaceAsync(
        string address, string notFoundCode, string notFoundMessage, CancellationToken cancellationToken)
    {
        var resolved = await addressResolver.ResolveAsync(address, cancellationToken);
        if (!resolved.IsSuccess)
            return new Error(notFoundCode, notFoundMessage, ErrorType.NotFound);

        var entity = resolved.Value;
        var issues = new List<LintIssue>();

        if (entity.EntityKind == "session" && entity.Session is not null)
        {
            await LintSessionAsync(entity.Session, entity.StorageDirectory, issues, cancellationToken);
            var report = BuildReport(entity.Session.Id, issues);
            return report.ErrorCount > 0
                ? FailLint(entity.Session.Id, report)
                : new ResolvedWorkspaceLint(entity.Session, null, entity.StorageDirectory, report);
        }

        if (entity.EntityKind == "quiz" && entity.Quiz is not null)
        {
            CheckAssetDirectory(entity.StorageDirectory, entity.Quiz.Id, issues);
            await LintQuizAsync(entity.Quiz, entity.StorageDirectory, issues, cancellationToken);
            var report = BuildReport(entity.Quiz.Id, issues);
            return report.ErrorCount > 0
                ? FailLint(entity.Quiz.Id, report)
                : new ResolvedWorkspaceLint(null, entity.Quiz, entity.StorageDirectory, report);
        }

        return new Error(notFoundCode, notFoundMessage, ErrorType.NotFound);
    }

    internal async Task<LintReportResponse> LintSessionInMemoryAsync(StudySession session, string storageDirectory, CancellationToken ct)
    {
        var issues = new List<LintIssue>();
        await LintSessionAsync(session, storageDirectory, issues, ct);
        return BuildReport(session.Id, issues);
    }

    internal async Task<LintReportResponse> LintQuizInMemoryAsync(Quiz quiz, string storageDirectory, CancellationToken ct)
    {
        var issues = new List<LintIssue>();
        CheckAssetDirectory(storageDirectory, quiz.Id, issues);
        await LintQuizAsync(quiz, storageDirectory, issues, ct);
        return BuildReport(quiz.Id, issues);
    }

    private static Error FailLint(string targetId, LintReportResponse report)
    {
        var summary = string.Join("; ", report.Issues.Where(i => i.Severity == "Error").Select(i => $"[{i.Code}] {i.Message}"));
        return LintErrors.FailedLintCheck(targetId, report.ErrorCount, summary, report);
    }

    private static LintReportResponse BuildReport(string targetId, List<LintIssue> issues)
    {
        int errors = issues.Count(i => i.Severity == "Error");
        return new LintReportResponse(targetId, errors == 0, errors, issues.Count - errors, issues);
    }

    private void CheckAssetDirectory(string storageDirectory, string targetId, List<LintIssue> issues)
    {
        var assetsDir = Path.Combine(storageDirectory, "assets");
        if (!workingDirectoryProvider.DirectoryExists(storageDirectory) || !workingDirectoryProvider.DirectoryExists(assetsDir))
            issues.Add(new LintIssue("Error", "Lint.BrokenAssetDirectory", targetId, $"Workspace or assets directory is missing: '{assetsDir}'."));
    }

    private async Task LintSessionAsync(StudySession session, string storageDirectory, List<LintIssue> issues, CancellationToken ct)
    {
        CheckAssetDirectory(storageDirectory, session.Id, issues);
        if (session.Components.Count == 0)
        {
            issues.Add(new LintIssue("Error", "Lint.EmptySession", session.Id, "Session contains 0 components."));
            return;
        }
        foreach (var component in session.Components)
            await LintComponentAsync(component, storageDirectory, issues, ct);
    }

    private async Task LintComponentAsync(SessionComponent component, string storageDirectory, List<LintIssue> issues, CancellationToken ct)
    {
        if (component.Kind == ComponentKind.Article)
        {
            if (component.ArticleBlocks.Count == 0)
            {
                issues.Add(new LintIssue("Error", "Lint.EmptyComponent", component.Id, "Article component contains 0 blocks."));
                return;
            }
            foreach (var block in component.ArticleBlocks)
                await LintArticleBlockAsync(block, storageDirectory, issues, ct);
        }
        else if (component.Quiz is not null)
        {
            await LintQuizAsync(component.Quiz, storageDirectory, issues, ct);
        }
    }

    private Task LintArticleBlockAsync(ArticleBlock block, string storageDirectory, List<LintIssue> issues, CancellationToken ct)
    {
        if (block.Kind == ArticleBlockKind.Text)
        {
            if (string.IsNullOrWhiteSpace(block.TextContent))
                issues.Add(new LintIssue("Error", "Lint.EmptyArticleBlock", block.Id, "Article text block is empty."));
            return Task.CompletedTask;
        }
        if (block.Kind == ArticleBlockKind.Callout)
        {
            if (string.IsNullOrWhiteSpace(block.TextContent))
                issues.Add(new LintIssue("Error", "Lint.EmptyArticleBlock", block.Id, "Article callout block is empty."));
            return Task.CompletedTask;
        }
        return CheckImageAssetAsync(storageDirectory, block.ImagePath, block.Id, issues, ct);
    }

    private async Task LintQuizAsync(Quiz quiz, string storageDirectory, List<LintIssue> issues, CancellationToken ct)
    {
        if (quiz.Questions.Count == 0)
        {
            issues.Add(new LintIssue("Error", "Lint.EmptyComponent", quiz.Id, "Quiz contains 0 questions."));
            return;
        }
        foreach (var question in quiz.Questions)
        {
            if ((quiz.Type == QuizType.Mcq && question.Type != QuestionType.Mcq) ||
                (quiz.Type == QuizType.Flashcard && question.Type != QuestionType.Flashcard))
            {
                issues.Add(new LintIssue(
                    "Error",
                    "Lint.QuestionTypeMismatch",
                    question.Id,
                    $"Quiz '{quiz.Id}' has type '{quiz.Type.ToString().ToLowerInvariant()}' and cannot contain '{question.Type.ToString().ToLowerInvariant()}' question."));
            }
            await LintQuestionAsync(question, storageDirectory, issues, ct);
        }
    }

    private async Task LintQuestionAsync(Question question, string storageDirectory, List<LintIssue> issues, CancellationToken ct)
    {
        if (question is McqQuestion mcq)
        {
            if (string.IsNullOrWhiteSpace(mcq.Text))
                issues.Add(new LintIssue("Error", "Lint.EmptyQuestionPrompt", mcq.Id, "MCQ prompt text is missing."));
            if (!string.IsNullOrWhiteSpace(mcq.ImagePath))
                await CheckImageAssetAsync(storageDirectory, mcq.ImagePath, $"{mcq.Id}:prompt", issues, ct);
            if (mcq.Options.Count < 2)
                issues.Add(new LintIssue("Error", "Lint.InsufficientOptions", mcq.Id, $"MCQ has {mcq.Options.Count} populated option(s); at least 2 are required."));
            if (mcq.Options.Count < mcq.MaxOptions)
                issues.Add(new LintIssue("Warning", "Lint.UnfilledOptionSlot", mcq.Id, $"MCQ has {mcq.Options.Count} of {mcq.MaxOptions} option slots filled."));

            int correctCount = mcq.Options.Count(o => o.IsCorrect);
            if (correctCount == 0)
                issues.Add(new LintIssue("Error", "Lint.NoCorrectOption", mcq.Id, "MCQ has no option marked correct."));
            else if (!mcq.IsMultiCorrect && correctCount > 1)
                issues.Add(new LintIssue("Error", "Lint.MultipleCorrectInSingle", mcq.Id, $"Single-select MCQ has {correctCount} correct options."));

            foreach (var opt in mcq.Options)
            {
                if (string.IsNullOrWhiteSpace(opt.Text) && string.IsNullOrWhiteSpace(opt.ImagePath))
                    issues.Add(new LintIssue("Error", "Lint.EmptyOption", mcq.Id, $"Option {opt.Index} has neither text nor image."));
                if (!string.IsNullOrWhiteSpace(opt.ImagePath))
                    await CheckImageAssetAsync(storageDirectory, opt.ImagePath, $"{mcq.Id}:option-{opt.Index}", issues, ct);
            }
        }
        else if (question is FlashcardQuestion card)
        {
            if (string.IsNullOrWhiteSpace(card.Front.Text) && string.IsNullOrWhiteSpace(card.Front.ImagePath))
                issues.Add(new LintIssue("Error", "Lint.EmptyFlashcardSide", card.Id, "Flashcard Front has neither text nor image."));
            if (string.IsNullOrWhiteSpace(card.Back.Text) && string.IsNullOrWhiteSpace(card.Back.ImagePath))
                issues.Add(new LintIssue("Error", "Lint.EmptyFlashcardSide", card.Id, "Flashcard Back has neither text nor image."));
            if (!string.IsNullOrWhiteSpace(card.Front.ImagePath))
                await CheckImageAssetAsync(storageDirectory, card.Front.ImagePath, $"{card.Id}:front", issues, ct);
            if (!string.IsNullOrWhiteSpace(card.Back.ImagePath))
                await CheckImageAssetAsync(storageDirectory, card.Back.ImagePath, $"{card.Id}:back", issues, ct);
        }
    }

    private async Task CheckImageAssetAsync(string storageDirectory, string? relativeAssetPath, string targetId, List<LintIssue> issues, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativeAssetPath))
        {
            issues.Add(new LintIssue("Error", "Lint.BrokenImageFile", targetId, "Image path is empty."));
            return;
        }
        var verifyRes = await contentInspector.VerifyStoredAssetAsync(storageDirectory, relativeAssetPath, ct);
        if (!verifyRes.IsSuccess)
            issues.Add(new LintIssue("Error", "Lint.BrokenImageFile", targetId, verifyRes.Error!.Description));
    }
}
