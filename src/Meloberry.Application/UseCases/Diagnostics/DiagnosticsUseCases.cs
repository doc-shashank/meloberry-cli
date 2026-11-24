using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Payloads;
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
        else if (component.Kind == ComponentKind.Quiz)
        {
            issues.Add(new LintIssue("Error", "Lint.EmptyComponent", component.Id, "Quiz component has no associated quiz payload."));
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
        if (block.Kind == ArticleBlockKind.Table)
        {
            LintTableBlock(block, issues);
            return Task.CompletedTask;
        }
        return CheckImageAssetAsync(storageDirectory, block.ImagePath, block.Id, issues, ct);
    }

    private static void LintTableBlock(ArticleBlock block, List<LintIssue> issues)
    {
        if (!Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(block.PayloadJson, out var table) || table is null)
        {
            issues.Add(new LintIssue("Error", "Lint.InvalidTablePayload", block.Id, "Article table block has missing or invalid payload JSON."));
            return;
        }
        if (table.Headers is null || table.Headers.Count == 0)
        {
            issues.Add(new LintIssue("Error", "Lint.InvalidTablePayload", block.Id, "Table block must have at least 1 header column."));
            return;
        }
        if (table.Headers.All(string.IsNullOrWhiteSpace))
        {
            issues.Add(new LintIssue("Error", "Lint.InvalidTablePayload", block.Id, "Table block headers cannot be all whitespace."));
        }
        if (table.Alignments is { Count: > 0 })
        {
            if (table.Alignments.Count != table.Headers.Count)
                issues.Add(new LintIssue("Error", "Lint.InvalidTablePayload", block.Id, $"Table alignments count ({table.Alignments.Count}) does not match headers count ({table.Headers.Count})."));
            for (int a = 0; a < table.Alignments.Count; a++)
            {
                var align = table.Alignments[a]?.Trim().ToLowerInvariant();
                if (align is not ("left" or "center" or "right"))
                    issues.Add(new LintIssue("Error", "Lint.InvalidTablePayload", block.Id, $"Table alignment '{table.Alignments[a]}' at column {a + 1} is invalid. Use 'left', 'center', or 'right'."));
            }
        }
        if (table.Rows is null || table.Rows.Count == 0)
        {
            issues.Add(new LintIssue("Error", "Lint.InvalidTablePayload", block.Id, "Table block must have at least 1 data row."));
            return;
        }
        for (int r = 0; r < table.Rows.Count; r++)
        {
            if (table.Rows[r] is null || table.Rows[r].Count != table.Headers.Count)
                issues.Add(new LintIssue("Error", "Lint.InvalidTablePayload", block.Id, $"Table row {r + 1} has {table.Rows[r]?.Count ?? 0} cell(s), expected {table.Headers.Count}."));
        }
        if (table.HideableCols is not null)
        {
            var seenCols = new HashSet<int>();
            foreach (var col in table.HideableCols)
            {
                if (col < 1 || col > table.Headers.Count)
                    issues.Add(new LintIssue("Error", "Lint.InvalidTableHideableCol", block.Id, $"Table hideableCols index {col} is out of bounds (1..{table.Headers.Count})."));
                else if (!seenCols.Add(col))
                    issues.Add(new LintIssue("Error", "Lint.InvalidTableHideableCol", block.Id, $"Duplicate table hideableCols index {col}."));
            }
        }
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
                    $"Quiz '{quiz.Id}' has type '{quiz.Type.ToString().ToLowerInvariant()}' and cannot contain '{Question.ToWireString(question.Type)}' question."));
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
        else if (question is PayloadQuestion pq)
        {
            await LintPayloadQuestionAsync(pq, storageDirectory, issues, ct);
        }
    }

    private async Task LintPayloadQuestionAsync(PayloadQuestion pq, string storageDirectory, List<LintIssue> issues, CancellationToken ct)
    {
        switch (pq.Type)
        {
            case QuestionType.FillBlank:
            {
                if (!Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(pq.PayloadJson, out var p) || p is null)
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Fill-in-the-blank question has invalid payload JSON."));
                    return;
                }
                if (string.IsNullOrWhiteSpace(p.Template))
                {
                    issues.Add(new LintIssue("Error", "Lint.EmptyQuestionPrompt", pq.Id, "Fill-in-the-blank prompt text is missing."));
                    return;
                }
                var parsed = Phase1PayloadRules.ParseFillBlankTemplate(p.Template);
                if (!parsed.IsSuccess)
                {
                    var code = parsed.Error!.Code switch
                    {
                        "FillBlank.UnbalancedDelimiters" => "Lint.UnbalancedBlankDelimiters",
                        "FillBlank.NoBlanks" => "Lint.NoFillBlanks",
                        "FillBlank.EmptyAnswer" => "Lint.EmptyBlankAnswer",
                        _ => "Lint.InvalidQuestionPayload"
                    };
                    issues.Add(new LintIssue("Error", code, pq.Id, parsed.Error.Description));
                }
                else if (p.Blanks is null ||
                         p.Blanks.Count != parsed.Value.Count ||
                         p.Blanks.Any(b => b is null || b.Answers is null || b.Answers.Count == 0))
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Fill-in-the-blank question has missing or out-of-sync blanks array in payload JSON."));
                }
                break;
            }
            case QuestionType.AssertionReason:
            {
                if (!Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(pq.PayloadJson, out var p) || p is null)
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Assertion-Reason question has invalid payload JSON."));
                    return;
                }
                if (string.IsNullOrWhiteSpace(p.Assertion) || string.IsNullOrWhiteSpace(p.Reason))
                    issues.Add(new LintIssue("Error", "Lint.EmptyAssertionOrReason", pq.Id, "Assertion-Reason question requires non-empty Assertion (A) and Reason (R)."));
                if (!Phase1PayloadRules.IsValidAssertionReasonAnswer(p.Answer))
                    issues.Add(new LintIssue("Error", "Lint.InvalidAssertionReasonAnswer", pq.Id, $"Invalid Assertion-Reason answer '{p.Answer}'. Allowed: {string.Join(", ", Phase1PayloadRules.AssertionReasonAnswers)}."));
                if (!Phase1PayloadRules.IsValidAssertionReasonScheme(p.Scheme) ||
                    (string.Equals(p.Answer?.Trim(), "both-false", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(p.Scheme?.Trim(), "4opt", StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidAssertionReasonScheme", pq.Id, $"Invalid Assertion-Reason scheme '{p.Scheme}' for answer '{p.Answer}'. Use '5opt' when answer is 'both-false'."));
                }
                break;
            }
            case QuestionType.SpotError:
            {
                if (!Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(pq.PayloadJson, out var p) || p is null)
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Spot-the-error question has invalid payload JSON."));
                    return;
                }
                if (string.IsNullOrWhiteSpace(p.Template))
                {
                    issues.Add(new LintIssue("Error", "Lint.EmptyQuestionPrompt", pq.Id, "Spot-the-error statement text is missing."));
                    return;
                }
                var parsed = Phase1PayloadRules.ParseSpotErrorTemplate(p.Template);
                if (!parsed.IsSuccess)
                {
                    var code = parsed.Error!.Code switch
                    {
                        "SpotError.EmptyCorrection" or "SpotError.EmptySpan" => "Lint.SpotErrorEmptyCorrection",
                        _ => "Lint.InvalidQuestionPayload"
                    };
                    issues.Add(new LintIssue("Error", code, pq.Id, parsed.Error.Description));
                    return;
                }

                if (p.Segments is null || p.Segments.Count == 0 || p.Segments.Any(s => s is null || s.Text is null))
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Spot-the-error question has missing or invalid segments array in payload JSON."));
                    return;
                }

                int candidateCount = parsed.Value.Count(s => s.IsCandidate);
                int errorCount = parsed.Value.Count(s => s.IsError);
                if (candidateCount < 2)
                    issues.Add(new LintIssue("Error", "Lint.InsufficientSpotErrorSpans", pq.Id, $"Spot-the-error statement has {candidateCount} candidate span(s); at least 2 '[span]' segments are required."));
                if (p.NoError && errorCount > 0)
                    issues.Add(new LintIssue("Error", "Lint.SpotErrorConflictingNoError", pq.Id, "Spot-the-error question has 'noError=true' but also marks error span(s) with '~~wrong~~ -> right'."));
                if (p.NoError && !p.AllowNoError)
                    issues.Add(new LintIssue("Error", "Lint.SpotErrorConflictingNoError", pq.Id, "Spot-the-error question has 'noError=true' with 'allowNoError=false'."));
                if (!p.NoError && errorCount == 0)
                    issues.Add(new LintIssue("Error", "Lint.SpotErrorMissingFault", pq.Id, "Spot-the-error question has no error span marked with '[~~wrong~~ -> right]' and 'noError' is false."));
                break;
            }
            case QuestionType.Differentiation:
            {
                if (!Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(pq.PayloadJson, out var p) || p is null)
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Differentiation question has invalid payload JSON."));
                    return;
                }
                var mode = Phase2PayloadRules.NormalizeDiffMode(p.Mode);
                if (!Phase2PayloadRules.IsValidDiffMode(mode))
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidDiffMode", pq.Id, $"Invalid differentiation mode '{p.Mode}'. Supported modes: 'fill', 'spot-wrong', 'rearrange'."));
                    return;
                }
                bool tableInvalid = false;
                if (p.Headers is null || p.Headers.Count < 2 || p.Headers.Any(h => h is null) || p.Headers.All(string.IsNullOrWhiteSpace))
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidDiffTable", pq.Id, "Differentiation table requires at least 2 non-null column headers."));
                    tableInvalid = true;
                }
                else if (mode == "rearrange" && p.Headers.Count < 3)
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidDiffTable", pq.Id, "Differentiation mode 'rearrange' requires at least 3 columns (1 feature column + at least 2 entity columns)."));
                    tableInvalid = true;
                }
                if (!tableInvalid && p.Alignments is { Count: > 0 })
                {
                    if (p.Alignments.Count != p.Headers!.Count ||
                        p.Alignments.Any(a => a is null || a.Trim().ToLowerInvariant() is not ("left" or "center" or "right")))
                    {
                        issues.Add(new LintIssue("Error", "Lint.InvalidDiffTable", pq.Id, "Differentiation table alignments must match headers length and use 'left', 'center', or 'right'."));
                        tableInvalid = true;
                    }
                }
                if (!tableInvalid)
                {
                    if (p.Rows is null || p.Rows.Count == 0)
                    {
                        issues.Add(new LintIssue("Error", "Lint.InvalidDiffTable", pq.Id, "Differentiation table requires at least 1 data row."));
                        tableInvalid = true;
                    }
                    else
                    {
                        for (int r = 0; r < p.Rows.Count; r++)
                        {
                            if (p.Rows[r] is null || p.Rows[r].Count != p.Headers!.Count || p.Rows[r].Any(c => c is null))
                            {
                                issues.Add(new LintIssue("Error", "Lint.InvalidDiffTable", pq.Id, $"Differentiation row {r + 1} has {p.Rows[r]?.Count ?? 0} cell(s) or null cell, expected {p.Headers!.Count}."));
                                tableInvalid = true;
                            }
                        }
                    }
                }
                if (tableInvalid) return;

                var parsed = Phase2PayloadRules.ParseDifferentiationTable(mode, p.Headers, p.Rows);
                if (mode == "fill")
                {
                    if (!parsed.IsSuccess || parsed.Value.Count(c => c.IsBlank) == 0)
                        issues.Add(new LintIssue("Error", "Lint.DiffMissingBlanks", pq.Id, parsed.IsSuccess ? "Differentiation 'fill' mode requires at least 1 '{{answer}}' blank cell." : parsed.Error!.Description));
                    else if (p.SpecialCells is null || p.SpecialCells.Count != parsed.Value.Count ||
                             p.SpecialCells.Any(c => c is null || !c.IsBlank || c.BlankAnswers is null || c.BlankAnswers.Count == 0))
                        issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Differentiation 'fill' question has missing or out-of-sync specialCells in payload JSON."));
                }
                else if (mode == "spot-wrong")
                {
                    if (!parsed.IsSuccess)
                        issues.Add(new LintIssue("Error", "Lint.DiffMissingFaults", pq.Id, parsed.Error!.Description));
                    else if (!p.AllowNoError && parsed.Value.Count(c => c.IsError) == 0)
                        issues.Add(new LintIssue("Error", "Lint.DiffMissingFaults", pq.Id, "Differentiation 'spot-wrong' mode requires at least 1 '~~wrong~~ -> correction' cell unless 'allowNoError' is true."));
                    else if (p.SpecialCells is null || p.SpecialCells.Count != parsed.Value.Count ||
                             p.SpecialCells.Any(c => c is null || !c.IsError || c.Corrections is null || c.Corrections.Count == 0))
                        issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Differentiation 'spot-wrong' question has missing or out-of-sync specialCells in payload JSON."));
                }
                break;
            }
            case QuestionType.Mechanism:
            {
                if (!Phase1PayloadCodec.TryDeserialize<MechanismPayload>(pq.PayloadJson, out var p) || p is null || p.Steps is null || p.Steps.Any(s => s is null))
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Mechanism question has invalid or missing steps payload JSON."));
                    return;
                }
                int orderedCount = p.Steps.Count(s => !s.IsDistractor);
                int distractorCount = p.Steps.Count(s => s.IsDistractor);
                if (orderedCount < 2)
                    issues.Add(new LintIssue("Error", "Lint.InsufficientMechanismSteps", pq.Id, $"Mechanism question has {orderedCount} ordered step(s); at least 2 are required."));
                if (p.Steps.Any(s => !s.IsDistractor && s.OrderGroup < 1))
                    issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Ordered mechanism steps must specify 'orderGroup' >= 1."));
                if (p.Steps.Any(s => string.IsNullOrWhiteSpace(s.Text)))
                    issues.Add(new LintIssue("Error", "Lint.EmptyMechanismStep", pq.Id, "Mechanism question contains a step with empty text."));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in p.Steps)
                {
                    var key = (s.Text ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(key) && !seen.Add(key))
                    {
                        issues.Add(new LintIssue("Error", "Lint.DuplicateMechanismStep", pq.Id, $"Mechanism question contains duplicate step text '{key}'."));
                        break;
                    }
                }
                if (orderedCount > 0 && distractorCount > orderedCount)
                    issues.Add(new LintIssue("Warning", "Lint.ExcessiveMechanismDistractors", pq.Id, $"Mechanism question has {distractorCount} distractors vs {orderedCount} ordered steps; excessive decoys may hurt clarity."));
                break;
            }
            case QuestionType.RelabelImage:
            {
                if (!Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(pq.PayloadJson, out var p) || p is null)
                {
                    issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, "Relabel question has invalid payload JSON."));
                    return;
                }
                await CheckImageAssetAsync(storageDirectory, p.ImagePath, $"{pq.Id}:image", issues, ct);
                if (!Phase2PayloadRules.IsValidRelabelMode(p.Mode))
                    issues.Add(new LintIssue("Error", "Lint.InvalidRelabelMode", pq.Id, $"Invalid relabel mode '{p.Mode}'. Supported modes: 'reveal', 'bank', 'recall'."));
                if (!Phase2PayloadRules.IsValidRelabelStyle(p.Style))
                    issues.Add(new LintIssue("Error", "Lint.InvalidRelabelStyle", pq.Id, $"Invalid relabel style '{p.Style}'. Supported styles: 'solid', 'blur', 'pixelate', 'whiteout'."));
                if (p.Boxes is null || p.Boxes.Count == 0)
                {
                    issues.Add(new LintIssue("Error", "Lint.EmptyRelabelBoxes", pq.Id, "Relabel question must define at least 1 overlay box."));
                    return;
                }
                for (int i = 0; i < p.Boxes.Count; i++)
                {
                    var box = p.Boxes[i];
                    if (box is null)
                    {
                        issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, $"Relabel box #{i + 1} is null."));
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(box.Label))
                        issues.Add(new LintIssue("Error", "Lint.EmptyRelabelLabel", pq.Id, $"Relabel box #{i + 1} has an empty label."));
                    if (!Phase2PayloadRules.HasValidRelabelBoxBounds(box))
                        issues.Add(new LintIssue("Error", "Lint.InvalidRelabelBoxBounds", pq.Id, $"Relabel box #{i + 1} ('{box.Label}') has out-of-bounds coordinates [x={box.X}, y={box.Y}, w={box.W}, h={box.H}]. Coordinates must stay within 0..1 with x+w<=1 and y+h<=1."));
                }
                break;
            }
            default:
                issues.Add(new LintIssue("Error", "Lint.InvalidQuestionPayload", pq.Id, $"Unsupported payload question type '{pq.Type}'."));
                break;
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
