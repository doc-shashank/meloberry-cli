using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Domain.Common;
using Meloberry.Domain.Layouts;

namespace Meloberry.Application.UseCases.Packaging;

public sealed record ExportPackageCommand(string Address, string? OutputPath = null, string TargetPlatform = "desktop", bool IncludeLayout = false);
public sealed record ExportPackageResponse(string TargetId, string PackagePath, LintReportResponse LintReport, string TargetPlatform = "desktop", bool IncludedLayout = false, IReadOnlyList<string>? Warnings = null);
public sealed record ImportPackageCommand(string PackagePath, string? DestinationDirectory = null);
public sealed record ImportPackageResponse(string ImportedId, string Kind, string StorageDirectory);
public sealed record BuildWebAppCommand(string Address, string? OutputDirectory = null, bool Verify = false, bool Verbose = false, bool Quiet = false, bool StaticVerifyOnly = false, int VerifyTimeoutSeconds = 30);
public sealed record BuildWebAppResponse(string TargetId, string OutputDirectory, string IndexHtmlPath, ApplyLintSummary Lint, LintReportResponse? LintReport = null, WebAppVerificationReport? Verify = null);

public sealed class ExportPackageHandler(
    ILayoutRepository layoutRepository,
    LintHandler lintHandler,
    IPackageExporter packageExporter)
{
    public async Task<Result<ExportPackageResponse>> HandleAsync(ExportPackageCommand command, CancellationToken cancellationToken = default)
    {
        var platform = command.TargetPlatform.Trim().ToLowerInvariant();
        if (platform is not ("desktop" or "android"))
            return new Error("Export.InvalidPlatform", $"Invalid export target platform '{command.TargetPlatform}'. Use 'desktop' or 'android'.", ErrorType.Validation);

        var resolved = await lintHandler.LintAndResolveWorkspaceAsync(
            command.Address, "Export.TargetNotFound", $"Export target '{command.Address}' must be a valid Session or Quiz ID.", cancellationToken);
        if (!resolved.IsSuccess) return resolved.Error!;

        var layouts = await layoutRepository.GetAllProfilesAsync(cancellationToken);
        var ws = resolved.Value;
        var wsId = ws.Session?.Id ?? ws.Quiz!.Id;

        List<string>? warnings = null;
        if (!command.IncludeLayout && await HasActiveLayoutOverridesAsync(ws, layouts))
        {
            warnings = ["Non-default layouts or entity overrides omitted (pass --include-layout to bundle)."];
        }

        var exportRes = ws.Session is not null
            ? await packageExporter.ExportSessionAsync(ws.Session, ws.StorageDirectory, layouts, command.OutputPath, platform, command.IncludeLayout, cancellationToken)
            : await packageExporter.ExportQuizAsync(ws.Quiz!, ws.StorageDirectory, layouts, command.OutputPath, platform, command.IncludeLayout, cancellationToken);

        return exportRes.IsSuccess
            ? new ExportPackageResponse(wsId, exportRes.Value, ws.Report, platform, command.IncludeLayout, warnings)
            : exportRes.Error!;
    }

    private static Task<bool> HasActiveLayoutOverridesAsync(ResolvedWorkspaceLint ws, IReadOnlyDictionary<string, LayoutProfile> profiles)
    {
        bool hasEntityOverrides = ws.Session is not null
            ? ws.Session.Components.Any(c =>
                c.LayoutOverrides.Count > 0 || (c.Quiz is not null && c.Quiz.Questions.Any(q => q.LayoutOverrides.Count > 0)))
            : ws.Quiz!.Questions.Any(q => q.LayoutOverrides.Count > 0);

        if (hasEntityOverrides) return Task.FromResult(true);

        bool hasGlobal = LayoutBoundsCatalog.SupportedTargets.Any(target =>
        {
            if (!profiles.TryGetValue(target, out var current)) return false;
            var def = LayoutProfile.CreateDefault(target).Value;
            return def.Values.Any(kv => !string.Equals(current.GetValue(kv.Key), kv.Value, StringComparison.OrdinalIgnoreCase));
        });

        return Task.FromResult(hasGlobal);
    }
}

public sealed class ImportPackageHandler(IWorkingDirectoryProvider workingDirectoryProvider, IPackageExporter packageExporter)
{
    public async Task<Result<ImportPackageResponse>> HandleAsync(ImportPackageCommand command, CancellationToken cancellationToken = default)
    {
        var destDir = workingDirectoryProvider.NormalizeDirectoryPath(command.DestinationDirectory);
        var importRes = await packageExporter.ImportPackageAsync(command.PackagePath, destDir, cancellationToken);
        return importRes.IsSuccess
            ? new ImportPackageResponse(importRes.Value.Id, importRes.Value.Kind, importRes.Value.StorageDirectory)
            : importRes.Error!;
    }
}

public sealed class BuildWebAppHandler(
    ILayoutRepository layoutRepository,
    LintHandler lintHandler,
    IWebAppGenerator webAppGenerator,
    VerifyWebAppHandler? verifyHandler = null)
{
    public async Task<Result<BuildWebAppResponse>> HandleAsync(BuildWebAppCommand command, CancellationToken cancellationToken = default)
    {
        var resolved = await lintHandler.LintAndResolveWorkspaceAsync(
            command.Address, "BuildWeb.TargetNotFound", $"Target '{command.Address}' must be a valid Session or Quiz ID.", cancellationToken);
        if (!resolved.IsSuccess) return resolved.Error!;

        var layouts = await layoutRepository.GetAllProfilesAsync(cancellationToken);
        var ws = resolved.Value;
        var wsId = ws.Session?.Id ?? ws.Quiz!.Id;
        var webRes = ws.Session is not null
            ? await webAppGenerator.GenerateForSessionAsync(ws.Session, ws.StorageDirectory, layouts, command.OutputDirectory, cancellationToken)
            : await webAppGenerator.GenerateForQuizAsync(ws.Quiz!, ws.StorageDirectory, layouts, command.OutputDirectory, cancellationToken);

        if (!webRes.IsSuccess) return webRes.Error!;
        var webDir = Path.GetDirectoryName(webRes.Value) ?? webRes.Value;
        var lintSummary = new ApplyLintSummary(ws.Report.IsClean, ws.Report.ErrorCount, ws.Report.WarningCount);
        LintReportResponse? detailedLint = command.Verbose ? ws.Report : null;

        WebAppVerificationReport? verifyReport = null;
        if (command.Verify || command.StaticVerifyOnly)
        {
            if (verifyHandler is null)
                return new Error("Verify.Unavailable", "Headless WebApp verifier is not configured.", ErrorType.Validation);

            var verRes = await verifyHandler.HandleAsync(
                new VerifyWebAppCommand(webDir, command.StaticVerifyOnly, command.VerifyTimeoutSeconds),
                cancellationToken);
            if (!verRes.IsSuccess) return verRes.Error!;

            verifyReport = verRes.Value;
            if (!verifyReport.Passed)
            {
                return new Error(
                    "Verify.Failed",
                    $"WebApp runtime verification failed: {verifyReport.BrokenAssets.Count} broken asset(s), {verifyReport.ConsoleErrors.Count} console error(s), {verifyReport.OverflowIssues.Count} overflow issue(s), {verifyReport.KatexErrors.Count} KaTeX error(s).",
                    ErrorType.Validation,
                    Hint: string.IsNullOrWhiteSpace(verifyReport.ReportPath) ? null : $"Inspect verification details at '{verifyReport.ReportPath}'.",
                    Data: verifyReport);
            }
        }

        return new BuildWebAppResponse(wsId, webDir, webRes.Value, lintSummary, detailedLint, verifyReport);
    }
}
