using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;

namespace Meloberry.Application.UseCases.Json;

public sealed record ApplySpecCommand(
    string? TargetAddress,
    string? JsonFilePath,
    string? InlineJson = null,
    string? WorkingDirectory = null,
    bool DryRun = false,
    string? BuildOutDir = null,
    string? ExportSpec = null,
    bool Quiet = false,
    bool Verbose = false,
    bool VerifyWeb = false,
    bool StaticVerifyOnly = false,
    int VerifyTimeoutSeconds = 30,
    bool Ephemeral = false);

public sealed record AppliedComponentSummary(
    string P,
    string Id,
    string? Ref,
    string Kind,
    int Count);

public sealed record ApplyLintSummary(
    bool IsClean,
    int Errors,
    int Warnings);

public sealed record ApplySpecResponse(
    string TargetId,
    string EntityKind,
    string StorageDirectory,
    bool CreatedWorkspace,
    bool DryRun,
    IReadOnlyList<AppliedComponentSummary>? Components = null,
    IReadOnlyList<string>? AddedBlockIds = null,
    IReadOnlyList<string>? AddedQuestionIds = null,
    ApplyLintSummary? Lint = null,
    string? WebOutputPath = null,
    string? PackagePath = null,
    WebAppVerificationReport? Verify = null);

public sealed class ApplySpecHandler(
    IIdGenerator idGenerator,
    IWorkingDirectoryProvider workingDirectoryProvider,
    IWorkspaceRepository repository,
    IDeclarativeSpecLoader specLoader,
    IContentInspector contentInspector,
    EntityAddressResolver addressResolver,
    LintHandler lintHandler,
    BuildWebAppHandler buildWebHandler,
    ExportPackageHandler exportHandler)
{
    public async Task<Result<ApplySpecResponse>> HandleAsync(ApplySpecCommand command, CancellationToken cancellationToken = default)
    {
        if ((command.VerifyWeb || command.StaticVerifyOnly) && string.IsNullOrWhiteSpace(command.BuildOutDir))
            return new Error("Cli.InvalidArgs", "Options '--verify' and '--static-only' require '--build <webDir>'.", ErrorType.Validation);

        if (command.Ephemeral && !string.IsNullOrWhiteSpace(command.TargetAddress))
            return new Error("Cli.InvalidArgs", "Option '--ephemeral' cannot be used when appending to an existing workspace.", ErrorType.Validation);

        var loadRes = command.InlineJson is not null
            ? await specLoader.LoadFromTextAsync(command.InlineJson, cancellationToken)
            : await specLoader.LoadAsync(command.JsonFilePath ?? string.Empty, cancellationToken);
        if (!loadRes.IsSuccess) return loadRes.Error!;

        var spec = loadRes.Value;
        var cwd = workingDirectoryProvider.GetCurrentDirectory();
        string jsonDir;
        if (!string.IsNullOrWhiteSpace(spec.AssetBaseDirectory))
        {
            jsonDir = spec.AssetBaseDirectory;
        }
        else if (command.InlineJson is not null || string.IsNullOrWhiteSpace(command.JsonFilePath))
        {
            jsonDir = cwd;
        }
        else
        {
            var fullSpecPath = Path.GetFullPath(command.JsonFilePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            jsonDir = workingDirectoryProvider.DirectoryExists(fullSpecPath)
                ? fullSpecPath
                : (Path.GetDirectoryName(fullSpecPath) ?? cwd);
        }

        var addedComponents = new List<SessionComponent>();
        var addedBlocks = new List<string>();
        var addedQuestions = new List<string>();

        string? ephemeralRoot = command.Ephemeral && !command.DryRun
            ? Path.Combine(Path.GetTempPath(), $"mby_ephemeral_{Guid.NewGuid():N}")
            : null;
        var effectiveWorkingDir = ephemeralRoot ?? command.WorkingDirectory;

        Result<ApplySpecResponse> applyRes;
        if (string.IsNullOrWhiteSpace(command.TargetAddress))
        {
            applyRes = await CreateNewWorkspaceAsync(spec, effectiveWorkingDir, command.DryRun, command.Quiet, command.Verbose, jsonDir, cwd, addedComponents, addedBlocks, addedQuestions, cancellationToken);
        }
        else
        {
            var resolved = await addressResolver.ResolveAsync(command.TargetAddress, cancellationToken);
            if (!resolved.IsSuccess) return resolved.Error!;
            var target = resolved.Value;

            applyRes = target.EntityKind switch
            {
                "session" => await AppendToSessionAsync(target.Session!, target.StorageDirectory, spec, command.DryRun, command.Quiet, command.Verbose, jsonDir, cwd, addedComponents, addedBlocks, addedQuestions, cancellationToken),
                "component" => await AppendToComponentAsync(target.Session!, target.Component!, target.StorageDirectory, spec, command.DryRun, command.Quiet, command.Verbose, jsonDir, cwd, addedBlocks, addedQuestions, cancellationToken),
                "quiz" => await AppendToQuizAsync(target.Quiz!, target.StorageDirectory, spec, command.DryRun, command.Quiet, command.Verbose, jsonDir, cwd, addedQuestions, cancellationToken),
                _ => new Error("Apply.UnsupportedTarget", $"Target '{command.TargetAddress}' ({target.EntityKind}) cannot be used with apply. Target a session, component, quiz, or omit target.", ErrorType.Validation)
            };
        }

        if (!applyRes.IsSuccess || command.DryRun)
        {
            if (ephemeralRoot is not null) CleanupDirectory(ephemeralRoot);
            return applyRes;
        }

        var res = applyRes.Value;
        try
        {
            string? webOut = null;
            string? pkgOut = null;
            ApplyLintSummary? lintSummary = res.Lint;
            WebAppVerificationReport? verifyReport = null;

            if (!string.IsNullOrWhiteSpace(command.BuildOutDir))
            {
                var buildRes = await buildWebHandler.HandleAsync(
                    new BuildWebAppCommand(res.TargetId, command.BuildOutDir, command.VerifyWeb, command.Verbose, command.Quiet, command.StaticVerifyOnly, command.VerifyTimeoutSeconds),
                    cancellationToken);
                if (!buildRes.IsSuccess) return buildRes.Error!;
                webOut = buildRes.Value.IndexHtmlPath;
                lintSummary = buildRes.Value.Lint;
                verifyReport = buildRes.Value.Verify;
            }

            if (!string.IsNullOrWhiteSpace(command.ExportSpec))
            {
                var colonIdx = command.ExportSpec.IndexOf(':');
                var platform = colonIdx >= 0 ? command.ExportSpec[..colonIdx] : command.ExportSpec;
                var outPath = colonIdx >= 0 && colonIdx + 1 < command.ExportSpec.Length ? command.ExportSpec[(colonIdx + 1)..] : null;
                var expRes = await exportHandler.HandleAsync(new ExportPackageCommand(res.TargetId, outPath, platform), cancellationToken);
                if (!expRes.IsSuccess) return expRes.Error!;
                pkgOut = expRes.Value.PackagePath;
                lintSummary = new ApplyLintSummary(expRes.Value.LintReport.IsClean, expRes.Value.LintReport.ErrorCount, expRes.Value.LintReport.WarningCount);
            }

            return res with
            {
                CreatedWorkspace = command.Ephemeral ? false : res.CreatedWorkspace,
                Lint = lintSummary,
                WebOutputPath = webOut,
                PackagePath = pkgOut,
                Verify = verifyReport
            };
        }
        finally
        {
            if (command.Ephemeral)
            {
                try
                {
                    if (string.Equals(res.EntityKind, "session", StringComparison.OrdinalIgnoreCase))
                        await repository.DeleteSessionAsync(res.TargetId, CancellationToken.None);
                    else if (string.Equals(res.EntityKind, "quiz", StringComparison.OrdinalIgnoreCase))
                        await repository.DeleteQuizAsync(res.TargetId, CancellationToken.None);
                }
                catch { }
                if (ephemeralRoot is not null) CleanupDirectory(ephemeralRoot);
            }
        }
    }

    private async Task<Result<ApplySpecResponse>> CreateNewWorkspaceAsync(
        DeclarativeApplySpec spec,
        string? workingDirectory,
        bool dryRun,
        bool quiet,
        bool verbose,
        string jsonDir,
        string cwd,
        List<SessionComponent> addedComponents,
        List<string> addedBlocks,
        List<string> addedQuestions,
        CancellationToken ct)
    {
        if (spec.Order is { Count: > 0 })
            return new Error("Json.InvalidFieldForType", "Top-level 'order' is only supported in directory manifests ('_session.md' or 'session.json').", ErrorType.Validation);

        var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.Combine(cwd, ".meloberry", "workspaces")
            : workingDirectoryProvider.NormalizeDirectoryPath(workingDirectory);
        var kind = spec.Kind?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(kind))
        {
            if (spec.Components is { Count: > 0 }) kind = "session";
            else if (spec.Blocks is { Count: > 0 }) kind = "article";
            else if (spec.Questions is { Count: > 0 }) kind = "quiz";
            else return new Error("Json.InvalidSpec", "Declarative JSON must specify 'kind' ('session' or 'quiz') or include 'components' / 'questions'.", ErrorType.Validation);
        }

        if (kind == "article")
        {
            if (spec.Components is { Count: > 0 } || spec.Questions is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Article spec cannot include 'components' or 'questions'.", ErrorType.Validation);

            spec = spec with
            {
                Kind = "session",
                Name = spec.Name ?? spec.Title ?? "Study Session",
                Components =
                [
                    new DeclarativeComponentSpec("article", spec.Title ?? spec.Name, null, spec.Blocks, null, spec.LayoutOverrides, spec.Ref, jsonDir, spec.Links)
                ],
                Blocks = null,
                LayoutOverrides = null,
                Ref = null,
                Links = null
            };
            kind = "session";
        }

        if (kind is "session" or "quiz" or "mcq" or "flashcard" or "mixed")
        {
            if (spec.LayoutOverrides is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Top-level 'layoutOverrides' belong on components or individual questions, not workspace roots.", ErrorType.Validation);
            if (!string.IsNullOrWhiteSpace(spec.Ref))
                return new Error("Json.InvalidFieldForType", "Top-level 'ref' belongs on components, blocks, or questions, not workspace roots.", ErrorType.Validation);
        }

        if (kind == "session")
        {
            if (spec.Blocks is { Count: > 0 } || spec.Questions is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Session JSON cannot place 'blocks' or 'questions' at root; wrap them inside 'components[]'.", ErrorType.Validation);

            var sessionId = idGenerator.NewSessionId();
            var storageDir = dryRun
                ? Path.Combine(Path.GetTempPath(), $"mby_dryrun_{Guid.NewGuid():N}")
                : Path.Combine(baseDir, sessionId);
            if (dryRun) Directory.CreateDirectory(Path.Combine(storageDir, "assets"));

            var sessionRes = StudySession.Create(sessionId, storageDir);
            if (!sessionRes.IsSuccess)
            {
                if (dryRun) CleanupDirectory(storageDir);
                return sessionRes.Error!;
            }
            var session = sessionRes.Value;

            try
            {
                if (!string.IsNullOrWhiteSpace(spec.Name))
                {
                    var nameRes = session.SetName(spec.Name);
                    if (!nameRes.IsSuccess) { CleanupDirectory(storageDir); return nameRes.Error!; }
                }
                if (spec.Description is not null)
                    session.SetDescription(spec.Description);

                var usedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var compsList = spec.Components ?? [];
                var reservedExplicitRefs = CollectExplicitSpecRefs(compsList);
                foreach (var compSpec in compsList)
                {
                    var addCompRes = await AddComponentFromSpecAsync(session, storageDir, compSpec, jsonDir, cwd, usedRefs, reservedExplicitRefs, addedComponents, addedBlocks, addedQuestions, ct);
                    if (!addCompRes.IsSuccess)
                    {
                        CleanupDirectory(storageDir);
                        return addCompRes.Error!;
                    }
                }

                if (dryRun)
                {
                    var report = await lintHandler.LintSessionInMemoryAsync(session, storageDir, ct);
                    return new ApplySpecResponse(
                        session.Id, "session", Path.Combine(baseDir, sessionId), CreatedWorkspace: false, DryRun: true,
                        Components: MapComponentSummaries(addedComponents),
                        AddedBlockIds: CompactIds(addedBlocks, quiet, verbose),
                        AddedQuestionIds: CompactIds(addedQuestions, quiet, verbose),
                        Lint: new ApplyLintSummary(report.IsClean, report.ErrorCount, report.WarningCount));
                }

                await repository.CreateSessionAsync(session, ct);
                var createdReport = await lintHandler.LintSessionInMemoryAsync(session, storageDir, ct);
                return new ApplySpecResponse(
                    session.Id, "session", storageDir, CreatedWorkspace: true, DryRun: false,
                    Components: MapComponentSummaries(addedComponents),
                    AddedBlockIds: CompactIds(addedBlocks, quiet, verbose),
                    AddedQuestionIds: CompactIds(addedQuestions, quiet, verbose),
                    Lint: new ApplyLintSummary(createdReport.IsClean, createdReport.ErrorCount, createdReport.WarningCount));
            }
            catch
            {
                CleanupDirectory(storageDir);
                throw;
            }
            finally
            {
                if (dryRun) CleanupDirectory(storageDir);
            }
        }

        if (kind is "quiz" or "mcq" or "flashcard" or "mixed")
        {
            if (spec.Components is { Count: > 0 } || spec.Blocks is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Quiz JSON cannot include 'components' or article 'blocks'.", ErrorType.Validation);

            var quizType = !string.IsNullOrWhiteSpace(spec.QuizType)
                ? spec.QuizType
                : (kind == "quiz" ? "mixed" : kind);

            var quizId = idGenerator.NewQuizId();
            var storageDir = dryRun
                ? Path.Combine(Path.GetTempPath(), $"mby_dryrun_{Guid.NewGuid():N}")
                : Path.Combine(baseDir, quizId);
            if (dryRun) Directory.CreateDirectory(Path.Combine(storageDir, "assets"));

            var quizRes = Quiz.Create(quizId, quizType, storageDir);
            if (!quizRes.IsSuccess)
            {
                if (dryRun) CleanupDirectory(storageDir);
                return quizRes.Error!;
            }
            var quiz = quizRes.Value;

            try
            {
                if (!string.IsNullOrWhiteSpace(spec.Name))
                {
                    var nameRes = quiz.SetName(spec.Name);
                    if (!nameRes.IsSuccess) { CleanupDirectory(storageDir); return nameRes.Error!; }
                }
                if (spec.Description is not null)
                    quiz.SetDescription(spec.Description);

                var usedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var qRes = await AddQuestionsFromSpecsAsync(quiz, storageDir, spec.Questions ?? [], jsonDir, jsonDir, cwd, usedRefs, addedQuestions, ct);
                if (!qRes.IsSuccess)
                {
                    CleanupDirectory(storageDir);
                    return qRes.Error!;
                }

                if (dryRun)
                {
                    var report = await lintHandler.LintQuizInMemoryAsync(quiz, storageDir, ct);
                    return new ApplySpecResponse(
                        quiz.Id, "quiz", Path.Combine(baseDir, quizId), CreatedWorkspace: false, DryRun: true,
                        AddedQuestionIds: CompactIds(addedQuestions, quiet, verbose),
                        Lint: new ApplyLintSummary(report.IsClean, report.ErrorCount, report.WarningCount));
                }

                await repository.CreateQuizAsync(quiz, ct);
                var createdReport = await lintHandler.LintQuizInMemoryAsync(quiz, storageDir, ct);
                return new ApplySpecResponse(
                    quiz.Id, "quiz", storageDir, CreatedWorkspace: true, DryRun: false,
                    AddedQuestionIds: CompactIds(addedQuestions, quiet, verbose),
                    Lint: new ApplyLintSummary(createdReport.IsClean, createdReport.ErrorCount, createdReport.WarningCount));
            }
            catch
            {
                CleanupDirectory(storageDir);
                throw;
            }
            finally
            {
                if (dryRun) CleanupDirectory(storageDir);
            }
        }

        return new Error("Json.InvalidSpec", $"Unsupported workspace kind '{spec.Kind}'. Use 'session' or 'quiz'.", ErrorType.Validation);
    }

    private async Task<Result<ApplySpecResponse>> AppendToSessionAsync(
        StudySession session,
        string realStorageDir,
        DeclarativeApplySpec spec,
        bool dryRun,
        bool quiet,
        bool verbose,
        string jsonDir,
        string cwd,
        List<SessionComponent> addedComponents,
        List<string> addedBlocks,
        List<string> addedQuestions,
        CancellationToken ct)
    {
        if (spec.Order is { Count: > 0 })
            return new Error("Json.InvalidFieldForType", "Top-level 'order' is only supported in directory manifests ('_session.md' or 'session.json').", ErrorType.Validation);

        var compsToAdd = new List<DeclarativeComponentSpec>();
        bool isSingleComponentSpec = false;
        if (spec.Components is { Count: > 0 })
        {
            if (spec.Blocks is { Count: > 0 } || spec.Questions is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Cannot mix 'components[]' with root 'blocks' or 'questions'.", ErrorType.Validation);
            if (spec.LayoutOverrides is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Top-level 'layoutOverrides' belong on components or individual questions, not session roots.", ErrorType.Validation);
            if (!string.IsNullOrWhiteSpace(spec.Ref))
                return new Error("Json.InvalidFieldForType", "Top-level 'ref' belongs on components, blocks, or questions, not session roots.", ErrorType.Validation);
            compsToAdd.AddRange(spec.Components);
        }
        else if (spec.Blocks is { Count: > 0 } || spec.Questions is { Count: > 0 } ||
                 (spec.Kind is not null && !string.Equals(spec.Kind, "session", StringComparison.OrdinalIgnoreCase)))
        {
            isSingleComponentSpec = true;
            var singleKind = spec.Kind ?? (spec.Blocks is { Count: > 0 } ? "article" : "quiz");
            compsToAdd.Add(new DeclarativeComponentSpec(
                singleKind, spec.Title ?? spec.Name, spec.QuizType, spec.Blocks, spec.Questions, spec.LayoutOverrides, spec.Ref, jsonDir, Links: spec.Links));
        }
        else
        {
            if (spec.LayoutOverrides is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Top-level 'layoutOverrides' belong on components or individual questions, not session roots.", ErrorType.Validation);
            if (!string.IsNullOrWhiteSpace(spec.Ref))
                return new Error("Json.InvalidFieldForType", "Top-level 'ref' belongs on components, blocks, or questions, not session roots.", ErrorType.Validation);
        }

        if (!isSingleComponentSpec)
        {
            if (!string.IsNullOrWhiteSpace(spec.Name))
            {
                var nameRes = session.SetName(spec.Name);
                if (!nameRes.IsSuccess) return nameRes.Error!;
            }
            if (spec.Description is not null)
                session.SetDescription(spec.Description);
        }

        var workingAssetRoot = dryRun ? CreateDryRunStagingCopy(realStorageDir) : realStorageDir;
        var existingAssets = dryRun ? null : CaptureExistingAssets(realStorageDir);
        bool persisted = false;
        try
        {
            var usedRefs = CollectSessionRefs(session);
            var reservedExplicitRefs = CollectExplicitSpecRefs(compsToAdd);
            foreach (var compSpec in compsToAdd)
            {
                var res = await AddComponentFromSpecAsync(session, workingAssetRoot, compSpec, jsonDir, cwd, usedRefs, reservedExplicitRefs, addedComponents, addedBlocks, addedQuestions, ct);
                if (!res.IsSuccess) return res.Error!;
            }

            if (dryRun)
            {
                var report = await lintHandler.LintSessionInMemoryAsync(session, workingAssetRoot, ct);
                return new ApplySpecResponse(
                    session.Id, "session", realStorageDir, CreatedWorkspace: false, DryRun: true,
                    Components: MapComponentSummaries(addedComponents),
                    AddedBlockIds: CompactIds(addedBlocks, quiet, verbose),
                    AddedQuestionIds: CompactIds(addedQuestions, quiet, verbose),
                    Lint: new ApplyLintSummary(report.IsClean, report.ErrorCount, report.WarningCount));
            }

            await repository.SaveSessionAsync(session, ct);
            persisted = true;
            var savedReport = await lintHandler.LintSessionInMemoryAsync(session, realStorageDir, ct);
            return new ApplySpecResponse(
                session.Id, "session", realStorageDir, CreatedWorkspace: false, DryRun: false,
                Components: MapComponentSummaries(addedComponents),
                AddedBlockIds: CompactIds(addedBlocks, quiet, verbose),
                AddedQuestionIds: CompactIds(addedQuestions, quiet, verbose),
                Lint: new ApplyLintSummary(savedReport.IsClean, savedReport.ErrorCount, savedReport.WarningCount));
        }
        finally
        {
            if (dryRun) CleanupDirectory(workingAssetRoot);
            else if (!persisted && existingAssets is not null) CleanupNewlyAddedAssets(realStorageDir, existingAssets);
        }
    }

    private async Task<Result<ApplySpecResponse>> AppendToComponentAsync(
        StudySession session,
        SessionComponent comp,
        string realStorageDir,
        DeclarativeApplySpec spec,
        bool dryRun,
        bool quiet,
        bool verbose,
        string jsonDir,
        string cwd,
        List<string> addedBlocks,
        List<string> addedQuestions,
        CancellationToken ct)
    {
        if (spec.Order is { Count: > 0 })
            return new Error("Json.InvalidFieldForType", "Top-level 'order' is only supported in directory manifests ('_session.md' or 'session.json').", ErrorType.Validation);
        if (spec.Components is { Count: > 0 })
            return new Error("Json.InvalidFieldForType", "Cannot pass 'components[]' when targeting a single Component_*.", ErrorType.Validation);

        var title = spec.Title ?? spec.Name;
        if (!string.IsNullOrWhiteSpace(title))
        {
            var tRes = comp.SetTitle(title);
            if (!tRes.IsSuccess) return tRes.Error!;
        }

        if (!string.IsNullOrWhiteSpace(spec.Ref))
        {
            var existingRefs = CollectSessionRefs(session);
            if (comp.RefKey is not null) existingRefs.Remove(comp.RefKey);
            var refRes = comp.SetRefKey(spec.Ref);
            if (!refRes.IsSuccess) return refRes.Error!;
            if (comp.RefKey is not null && !existingRefs.Add(comp.RefKey))
                return AddressErrors.DuplicateRef(comp.RefKey);
        }

        if (spec.LayoutOverrides is not null)
        {
            foreach (var (prop, val) in spec.LayoutOverrides)
            {
                var lRes = comp.SetLayoutOverride(prop, val);
                if (!lRes.IsSuccess) return lRes.Error!;
            }
        }

        var workingAssetRoot = dryRun ? CreateDryRunStagingCopy(realStorageDir) : realStorageDir;
        var existingAssets = dryRun ? null : CaptureExistingAssets(realStorageDir);
        bool persisted = false;
        try
        {
            var usedRefs = CollectSessionRefs(session);
            if (comp.Kind == ComponentKind.Article)
            {
                if (spec.Questions is { Count: > 0 })
                    return new Error("Json.InvalidFieldForType", "Article component cannot accept 'questions[]'.", ErrorType.Validation);

                var blockRes = await AddBlocksFromSpecsAsync(comp, workingAssetRoot, spec.Blocks ?? [], jsonDir, jsonDir, cwd, usedRefs, addedBlocks, ct);
                if (!blockRes.IsSuccess) return blockRes.Error!;
                var linksRes = await AddLinkCardsFromSpecsAsync(comp, workingAssetRoot, spec.Links ?? [], jsonDir, jsonDir, cwd, ct);
                if (!linksRes.IsSuccess) return linksRes.Error!;
            }
            else if (comp.Quiz is not null)
            {
                if (spec.Blocks is { Count: > 0 } || spec.Links is { Count: > 0 })
                    return new Error("Json.InvalidFieldForType", "Quiz component cannot accept article 'blocks[]' or 'links[]'.", ErrorType.Validation);

                var qRes = await AddQuestionsFromSpecsAsync(comp.Quiz, workingAssetRoot, spec.Questions ?? [], jsonDir, jsonDir, cwd, usedRefs, addedQuestions, ct);
                if (!qRes.IsSuccess) return qRes.Error!;
            }

            if (dryRun)
            {
                var report = await lintHandler.LintSessionInMemoryAsync(session, workingAssetRoot, ct);
                return new ApplySpecResponse(
                    comp.Id, "component", realStorageDir, CreatedWorkspace: false, DryRun: true,
                    AddedBlockIds: CompactIds(addedBlocks, quiet, verbose),
                    AddedQuestionIds: CompactIds(addedQuestions, quiet, verbose),
                    Lint: new ApplyLintSummary(report.IsClean, report.ErrorCount, report.WarningCount));
            }

            await repository.SaveSessionAsync(session, ct);
            persisted = true;
            var savedReport = await lintHandler.LintSessionInMemoryAsync(session, realStorageDir, ct);
            return new ApplySpecResponse(
                comp.Id, "component", realStorageDir, CreatedWorkspace: false, DryRun: false,
                AddedBlockIds: CompactIds(addedBlocks, quiet, verbose),
                AddedQuestionIds: CompactIds(addedQuestions, quiet, verbose),
                Lint: new ApplyLintSummary(savedReport.IsClean, savedReport.ErrorCount, savedReport.WarningCount));
        }
        finally
        {
            if (dryRun) CleanupDirectory(workingAssetRoot);
            else if (!persisted && existingAssets is not null) CleanupNewlyAddedAssets(realStorageDir, existingAssets);
        }
    }

    private async Task<Result<ApplySpecResponse>> AppendToQuizAsync(
        Quiz quiz,
        string realStorageDir,
        DeclarativeApplySpec spec,
        bool dryRun,
        bool quiet,
        bool verbose,
        string jsonDir,
        string cwd,
        List<string> addedQuestions,
        CancellationToken ct)
    {
        if (spec.Order is { Count: > 0 })
            return new Error("Json.InvalidFieldForType", "Top-level 'order' is only supported in directory manifests ('_session.md' or 'session.json').", ErrorType.Validation);
        if (spec.Components is { Count: > 0 } || spec.Blocks is { Count: > 0 })
            return new Error("Json.InvalidFieldForType", "Standalone quiz cannot accept 'components[]' or article 'blocks[]'.", ErrorType.Validation);
        if (spec.LayoutOverrides is { Count: > 0 })
            return new Error("Json.InvalidFieldForType", "Top-level 'layoutOverrides' belong on individual questions, not standalone quiz roots.", ErrorType.Validation);
        if (!string.IsNullOrWhiteSpace(spec.Ref))
            return new Error("Json.InvalidFieldForType", "Top-level 'ref' belongs on individual questions, not standalone quiz roots.", ErrorType.Validation);

        if (!string.IsNullOrWhiteSpace(spec.Name))
        {
            var nameRes = quiz.SetName(spec.Name);
            if (!nameRes.IsSuccess) return nameRes.Error!;
        }
        if (spec.Description is not null)
            quiz.SetDescription(spec.Description);

        var workingAssetRoot = dryRun ? CreateDryRunStagingCopy(realStorageDir) : realStorageDir;
        var existingAssets = dryRun ? null : CaptureExistingAssets(realStorageDir);
        bool persisted = false;
        try
        {
            var usedRefs = quiz.Questions.Where(q => q.RefKey is not null).Select(q => q.RefKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var qRes = await AddQuestionsFromSpecsAsync(quiz, workingAssetRoot, spec.Questions ?? [], jsonDir, jsonDir, cwd, usedRefs, addedQuestions, ct);
            if (!qRes.IsSuccess) return qRes.Error!;

            if (dryRun)
            {
                var report = await lintHandler.LintQuizInMemoryAsync(quiz, workingAssetRoot, ct);
                return new ApplySpecResponse(
                    quiz.Id, "quiz", realStorageDir, CreatedWorkspace: false, DryRun: true,
                    AddedQuestionIds: CompactIds(addedQuestions, quiet, verbose),
                    Lint: new ApplyLintSummary(report.IsClean, report.ErrorCount, report.WarningCount));
            }

            await repository.SaveQuizAsync(quiz, ct);
            persisted = true;
            var savedReport = await lintHandler.LintQuizInMemoryAsync(quiz, realStorageDir, ct);
            return new ApplySpecResponse(
                quiz.Id, "quiz", realStorageDir, CreatedWorkspace: false, DryRun: false,
                AddedQuestionIds: CompactIds(addedQuestions, quiet, verbose),
                Lint: new ApplyLintSummary(savedReport.IsClean, savedReport.ErrorCount, savedReport.WarningCount));
        }
        finally
        {
            if (dryRun) CleanupDirectory(workingAssetRoot);
            else if (!persisted && existingAssets is not null) CleanupNewlyAddedAssets(realStorageDir, existingAssets);
        }
    }

    private async Task<Result> AddComponentFromSpecAsync(
        StudySession session,
        string storageDir,
        DeclarativeComponentSpec compSpec,
        string specRootDir,
        string cwd,
        HashSet<string> usedRefs,
        HashSet<string> reservedExplicitRefs,
        List<SessionComponent> addedComponents,
        List<string> addedBlocks,
        List<string> addedQuestions,
        CancellationToken ct)
    {
        var rawKind = compSpec.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
        var effectiveType = rawKind == "quiz"
            ? (string.IsNullOrWhiteSpace(compSpec.QuizType) ? "mixed" : compSpec.QuizType)
            : rawKind;

        var addRes = session.AddComponent(idGenerator.NewComponentId(), effectiveType);
        if (!addRes.IsSuccess) return addRes.Error!;
        var comp = addRes.Value;

        if (!string.IsNullOrWhiteSpace(compSpec.Ref))
        {
            var refRes = comp.SetRefKey(compSpec.Ref);
            if (!refRes.IsSuccess) return refRes.Error!;
            if (comp.RefKey is not null && !usedRefs.Add(comp.RefKey))
                return AddressErrors.DuplicateRef(comp.RefKey);
        }
        else
        {
            var seed = !string.IsNullOrWhiteSpace(compSpec.Title) ? compSpec.Title : $"{rawKind}-{session.Components.Count}";
            var fallback = comp.Kind == ComponentKind.Article ? "art" : "quiz";
            var autoGenAvoid = new HashSet<string>(usedRefs, StringComparer.OrdinalIgnoreCase);
            autoGenAvoid.UnionWith(reservedExplicitRefs);
            var autoRef = RefKeyValidator.GenerateSlug(seed, fallback, autoGenAvoid);
            usedRefs.Add(autoRef);
            comp.SetRefKey(autoRef);
        }

        addedComponents.Add(comp);

        if (!string.IsNullOrWhiteSpace(compSpec.Title))
        {
            var tRes = comp.SetTitle(compSpec.Title);
            if (!tRes.IsSuccess) return tRes.Error!;
        }

        if (compSpec.LayoutOverrides is not null)
        {
            foreach (var (prop, val) in compSpec.LayoutOverrides)
            {
                var lRes = comp.SetLayoutOverride(prop, val);
                if (!lRes.IsSuccess) return lRes.Error!;
            }
        }

        var compAssetDir = !string.IsNullOrWhiteSpace(compSpec.SourceDirectory) ? compSpec.SourceDirectory : specRootDir;
        if (comp.Kind == ComponentKind.Article)
        {
            if (compSpec.Questions is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Article component cannot specify 'questions[]'.", ErrorType.Validation);

            var blocksRes = await AddBlocksFromSpecsAsync(comp, storageDir, compSpec.Blocks ?? [], compAssetDir, specRootDir, cwd, usedRefs, addedBlocks, ct);
            if (!blocksRes.IsSuccess) return blocksRes.Error!;
            return await AddLinkCardsFromSpecsAsync(comp, storageDir, compSpec.Links ?? [], compAssetDir, specRootDir, cwd, ct);
        }

        if (compSpec.Blocks is { Count: > 0 } || compSpec.Links is { Count: > 0 })
            return new Error("Json.InvalidFieldForType", "Quiz component cannot specify article 'blocks[]' or 'links[]'.", ErrorType.Validation);

        return await AddQuestionsFromSpecsAsync(comp.Quiz!, storageDir, compSpec.Questions ?? [], compAssetDir, specRootDir, cwd, usedRefs, addedQuestions, ct);
    }

    private async Task<Result> AddLinkCardsFromSpecsAsync(
        SessionComponent comp,
        string storageDir,
        IReadOnlyList<DeclarativeLinkCardSpec> links,
        string compAssetDir,
        string specRootDir,
        string cwd,
        CancellationToken ct)
    {
        foreach (var l in links)
        {
            string? storedImg = null;
            if (!string.IsNullOrWhiteSpace(l.ImagePath))
            {
                var imgRes = await JsonQuestionMaterializer.CopyConfinedImageAsync(
                    l.ImagePath, compAssetDir, storageDir, contentInspector, ct, specRootDir, cwd);
                if (!imgRes.IsSuccess) return imgRes.Error!;
                storedImg = imgRes.Value;
            }
            var addRes = comp.AddOrReplaceLinkCard(new ArticleLinkCard(l.Id, l.Title, l.Text, storedImg));
            if (!addRes.IsSuccess) return addRes.Error!;
        }
        return Result.Success();
    }

    private async Task<Result> AddBlocksFromSpecsAsync(
        SessionComponent comp,
        string storageDir,
        IReadOnlyList<DeclarativeArticleBlockSpec> blocks,
        string compAssetDir,
        string specRootDir,
        string cwd,
        HashSet<string> usedRefs,
        List<string> addedBlocks,
        CancellationToken ct)
    {
        foreach (var b in blocks)
        {
            var bKind = b.Kind?.Trim().ToLowerInvariant();
            var blockId = idGenerator.NewArticleBlockId();

            ArticleBlock created;
            if (bKind == "text")
            {
                if (!string.IsNullOrWhiteSpace(b.ImagePath) || !string.IsNullOrWhiteSpace(b.Caption) ||
                    !string.IsNullOrWhiteSpace(b.Alt) || !string.IsNullOrWhiteSpace(b.Tone) ||
                    !string.IsNullOrWhiteSpace(b.Title) || !string.IsNullOrWhiteSpace(b.PayloadJson))
                {
                    return new Error("Json.InvalidFieldForType", "Article text block only supports 'kind', 'text', and optional 'ref'.", ErrorType.Validation);
                }

                var res = comp.AddTextBlock(blockId, b.Text ?? string.Empty);
                if (!res.IsSuccess) return res.Error!;
                created = res.Value;
            }
            else if (bKind == "image")
            {
                if (!string.IsNullOrWhiteSpace(b.Text) || !string.IsNullOrWhiteSpace(b.Tone) ||
                    !string.IsNullOrWhiteSpace(b.Title) || !string.IsNullOrWhiteSpace(b.PayloadJson))
                    return new Error("Json.InvalidFieldForType", "Article image block cannot specify 'text', 'tone', 'title', or table payload fields.", ErrorType.Validation);
                if (string.IsNullOrWhiteSpace(b.ImagePath))
                    return new Error("Json.InvalidSpec", "Article image block requires 'imagePath'.", ErrorType.Validation);

                var imgRes = await JsonQuestionMaterializer.CopyConfinedImageAsync(b.ImagePath, compAssetDir, storageDir, contentInspector, ct, specRootDir, cwd);
                if (!imgRes.IsSuccess) return imgRes.Error!;
                if (string.IsNullOrWhiteSpace(imgRes.Value))
                    return new Error("Json.InvalidSpec", "Article image block requires a valid 'imagePath'.", ErrorType.Validation);

                var res = comp.AddImageBlock(blockId, imgRes.Value, caption: b.Caption, alt: b.Alt);
                if (!res.IsSuccess) return res.Error!;
                created = res.Value;
            }
            else if (bKind == "callout")
            {
                if (!string.IsNullOrWhiteSpace(b.ImagePath) || !string.IsNullOrWhiteSpace(b.Caption) ||
                    !string.IsNullOrWhiteSpace(b.Alt) || !string.IsNullOrWhiteSpace(b.PayloadJson))
                    return new Error("Json.InvalidFieldForType", "Article callout block cannot specify 'imagePath', 'caption', 'alt', or table payload fields.", ErrorType.Validation);

                CalloutTone tone = CalloutTone.Pearl;
                if (!string.IsNullOrWhiteSpace(b.Tone) && !ArticleBlock.TryParseCalloutTone(b.Tone, out tone))
                    return ArticleErrors.InvalidCalloutTone(b.Tone);

                var res = comp.AddCalloutBlock(blockId, tone, b.Text ?? string.Empty, b.Title);
                if (!res.IsSuccess) return res.Error!;
                created = res.Value;
            }
            else if (bKind == "table")
            {
                if (!string.IsNullOrWhiteSpace(b.Text) || !string.IsNullOrWhiteSpace(b.ImagePath) ||
                    !string.IsNullOrWhiteSpace(b.Alt) || !string.IsNullOrWhiteSpace(b.Tone))
                    return new Error("Json.InvalidFieldForType", "Article table block cannot specify 'text', 'imagePath', 'alt', or 'tone'.", ErrorType.Validation);

                if (!Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(b.PayloadJson, out var tablePayload) || tablePayload is null)
                    return ArticleErrors.InvalidTablePayload("Table block requires valid 'headers' and 'rows' data.");

                var canonicalJson = Phase1PayloadCodec.Serialize(tablePayload);
                var res = comp.AddTableBlock(blockId, canonicalJson, tablePayload.Title ?? b.Title);
                if (!res.IsSuccess) return res.Error!;
                created = res.Value;
            }
            else if (bKind is "diagram-explore" or "diagramexplore")
            {
                if (!string.IsNullOrWhiteSpace(b.Text) || !string.IsNullOrWhiteSpace(b.Tone))
                    return new Error("Json.InvalidFieldForType", "Article diagram-explore block cannot specify 'text' or 'tone'.", ErrorType.Validation);

                if (!Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(b.PayloadJson, out var parsedDiag) || parsedDiag is null)
                    return ArticleErrors.InvalidDiagramExplorePayload("Diagram Explorer block requires valid payload data.");

                var rawImgPath = !string.IsNullOrWhiteSpace(parsedDiag.ImagePath) ? parsedDiag.ImagePath : b.ImagePath;
                if (string.IsNullOrWhiteSpace(rawImgPath))
                    return new Error("Json.InvalidSpec", "Article diagram-explore block requires 'imagePath'.", ErrorType.Validation);

                var imgRes = await JsonQuestionMaterializer.CopyConfinedImageAsync(
                    rawImgPath, compAssetDir, storageDir, contentInspector, ct, specRootDir, cwd);
                if (!imgRes.IsSuccess) return imgRes.Error!;
                if (string.IsNullOrWhiteSpace(imgRes.Value))
                    return new Error("Json.InvalidSpec", "Article diagram-explore block requires a valid 'imagePath'.", ErrorType.Validation);

                IReadOnlyList<DiagramHotspot> hotspots = parsedDiag.Hotspots ?? [];
                if (!string.IsNullOrWhiteSpace(parsedDiag.SourceMasksPath))
                {
                    var sidecarRes = JsonQuestionMaterializer.ResolveConfinedSidecarFile(parsedDiag.SourceMasksPath, compAssetDir, specRootDir, cwd);
                    if (!sidecarRes.IsSuccess) return sidecarRes.Error!;

                    var rawSidecar = await File.ReadAllTextAsync(sidecarRes.Value, ct);
                    var boxesRes = Phase2PayloadRules.ParseMaskSidecarJson(rawSidecar);
                    if (!boxesRes.IsSuccess) return boxesRes.Error!;

                    var annotations = hotspots
                        .Where(h => h is not null)
                        .Select(h => new DiagramHotspotAnnotation(h.Label ?? string.Empty, h.Note ?? string.Empty))
                        .ToList();
                    var resolvedHotspots = Phase3PayloadRules.ResolveHotspotsFromSidecar(boxesRes.Value, annotations);
                    if (!resolvedHotspots.IsSuccess) return resolvedHotspots.Error!;
                    hotspots = resolvedHotspots.Value;
                }
                else
                {
                    hotspots = hotspots
                        .Where(h => h is not null)
                        .Select((h, idx) => new DiagramHotspot(idx, h.Label?.Trim() ?? string.Empty, h.Note?.Trim() ?? string.Empty, h.X, h.Y, h.W, h.H))
                        .ToList();
                }

                var diagTitle = parsedDiag.Title ?? b.Title;
                var diagCaption = parsedDiag.Caption ?? b.Caption;
                var canonicalDiag = Phase1PayloadCodec.Serialize(new DiagramExplorePayload(
                    imgRes.Value,
                    string.IsNullOrWhiteSpace(diagTitle) ? null : diagTitle.Trim(),
                    string.IsNullOrWhiteSpace(diagCaption) ? null : diagCaption.Trim(),
                    hotspots,
                    SourceMasksPath: null));

                var res = comp.AddDiagramExploreBlock(blockId, imgRes.Value, canonicalDiag, diagTitle, diagCaption, b.Alt);
                if (!res.IsSuccess) return res.Error!;
                created = res.Value;
            }
            else if (bKind == "reveal")
            {
                if (!string.IsNullOrWhiteSpace(b.Text) || !string.IsNullOrWhiteSpace(b.ImagePath) ||
                    !string.IsNullOrWhiteSpace(b.Caption) || !string.IsNullOrWhiteSpace(b.Alt) ||
                    !string.IsNullOrWhiteSpace(b.Tone))
                {
                    return new Error("Json.InvalidFieldForType", "Article reveal block cannot specify 'text', 'imagePath', 'caption', 'alt', or 'tone'.", ErrorType.Validation);
                }

                if (!Phase1PayloadCodec.TryDeserialize<RevealBlockPayload>(b.PayloadJson, out var parsedRev) || parsedRev is null)
                    return ArticleErrors.InvalidRevealPayload("Reveal block requires valid steps payload data.");

                var revTitle = parsedRev.Title ?? b.Title;
                var revSteps = (parsedRev.Steps ?? [])
                    .Where(s => s is not null)
                    .Select((s, idx) => new RevealStep(idx, s.Heading?.Trim() ?? string.Empty, s.Body?.Trim() ?? string.Empty))
                    .ToList();
                var canonicalRev = Phase1PayloadCodec.Serialize(new RevealBlockPayload(
                    string.IsNullOrWhiteSpace(revTitle) ? null : revTitle.Trim(),
                    parsedRev.ShowFirst,
                    revSteps));

                var res = comp.AddRevealBlock(blockId, canonicalRev, revTitle);
                if (!res.IsSuccess) return res.Error!;
                created = res.Value;
            }
            else
            {
                return new Error("Json.InvalidSpec", $"Unsupported article block kind '{b.Kind}'. Use 'text', 'image', 'callout', 'table', 'diagram-explore', or 'reveal'.", ErrorType.Validation);
            }

            if (!string.IsNullOrWhiteSpace(b.Ref))
            {
                var refRes = created.SetRefKey(b.Ref);
                if (!refRes.IsSuccess) return refRes.Error!;
                if (created.RefKey is not null && !usedRefs.Add(created.RefKey))
                    return AddressErrors.DuplicateRef(created.RefKey);
            }

            addedBlocks.Add(blockId);
        }
        return Result.Success();
    }

    private async Task<Result> AddQuestionsFromSpecsAsync(
        Quiz quiz,
        string storageDir,
        IReadOnlyList<JsonQuestionSpec> questions,
        string compAssetDir,
        string specRootDir,
        string cwd,
        HashSet<string> usedRefs,
        List<string> addedQuestions,
        CancellationToken ct)
    {
        foreach (var qSpec in questions)
        {
            var qId = idGenerator.NewQuestionId();
            var matRes = await JsonQuestionMaterializer.MaterializeAsync(
                qId, quiz.Id, quiz.Questions.Count + 1, qSpec, compAssetDir, storageDir, contentInspector, ct, specRootDir, cwd);
            if (!matRes.IsSuccess) return matRes.Error!;

            if (matRes.Value.RefKey is not null && !usedRefs.Add(matRes.Value.RefKey))
                return AddressErrors.DuplicateRef(matRes.Value.RefKey);

            var insRes = quiz.InsertQuestion(matRes.Value, null);
            if (!insRes.IsSuccess) return insRes.Error!;
            addedQuestions.Add(qId);
        }
        return Result.Success();
    }

    private static IReadOnlyList<string>? CompactIds(List<string> ids, bool quiet, bool verbose)
    {
        if (quiet || ids.Count == 0) return null;
        if (!verbose && ids.Count > 10) return null;
        return ids;
    }

    private static HashSet<string> CollectExplicitSpecRefs(IReadOnlyList<DeclarativeComponentSpec> components)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static void TryReserve(string? raw, HashSet<string> set)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var res = RefKeyValidator.ValidateAndNormalize(raw);
            if (res.IsSuccess && res.Value is not null) set.Add(res.Value);
        }

        foreach (var c in components)
        {
            TryReserve(c.Ref, reserved);
            if (c.Blocks is not null)
                foreach (var b in c.Blocks)
                    TryReserve(b.Ref, reserved);
            if (c.Questions is not null)
                foreach (var q in c.Questions)
                    TryReserve(q.Ref, reserved);
        }
        return reserved;
    }

    internal static HashSet<string> CollectSessionRefs(StudySession session)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in session.Components)
        {
            if (c.RefKey is not null) refs.Add(c.RefKey);
            foreach (var b in c.ArticleBlocks)
                if (b.RefKey is not null) refs.Add(b.RefKey);
            if (c.Quiz is not null)
                foreach (var q in c.Quiz.Questions)
                    if (q.RefKey is not null) refs.Add(q.RefKey);
        }
        return refs;
    }

    private static List<AppliedComponentSummary> MapComponentSummaries(List<SessionComponent> components) =>
        components.Select(c => new AppliedComponentSummary(
            $"c{c.Index}",
            c.Id,
            c.RefKey,
            c.Kind == ComponentKind.Article ? "article" : $"quiz:{c.Quiz!.Type.ToString().ToLowerInvariant()}",
            c.Kind == ComponentKind.Article ? c.ArticleBlocks.Count : c.Quiz?.Questions.Count ?? 0)).ToList();

    private static string CreateDryRunStagingCopy(string realStorageDir)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"mby_dryrun_{Guid.NewGuid():N}");
        var stagingAssets = Path.Combine(staging, "assets");
        Directory.CreateDirectory(stagingAssets);
        var realAssets = Path.Combine(realStorageDir, "assets");
        if (Directory.Exists(realAssets))
        {
            foreach (var f in Directory.EnumerateFiles(realAssets))
                File.Copy(f, Path.Combine(stagingAssets, Path.GetFileName(f)), overwrite: true);
        }
        return staging;
    }

    internal static HashSet<string> CaptureExistingAssets(string storageDir)
    {
        var assetsDir = Path.Combine(storageDir, "assets");
        if (!Directory.Exists(assetsDir))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(assetsDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
    }

    internal static void CleanupNewlyAddedAssets(string storageDir, HashSet<string> existingAssets)
    {
        var assetsDir = Path.Combine(storageDir, "assets");
        if (!Directory.Exists(assetsDir)) return;
        foreach (var file in Directory.EnumerateFiles(assetsDir))
        {
            var name = Path.GetFileName(file);
            if (!existingAssets.Contains(name))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private static void CleanupDirectory(string storageDir)
    {
        try
        {
            if (Directory.Exists(storageDir))
                Directory.Delete(storageDir, recursive: true);
        }
        catch { }
    }
}
