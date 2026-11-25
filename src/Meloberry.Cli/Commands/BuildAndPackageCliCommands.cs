using System.Text.RegularExpressions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Layouts;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Cli.Abstractions;
using Meloberry.Domain.Common;

namespace Meloberry.Cli.Commands;

public sealed partial class BuildCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "preview", "no-wait", "verify", "static-only", "ephemeral" };
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "out", "timeout" };

    [GeneratedRegex(@"^(Session|Set)_\d{8}_\d{6}_\d{3}$", RegexOptions.Compiled)]
    private static partial Regex StrictWorkspaceIdRegex();

    public BuildCliCommand()
        : base(new CommandDescriptor(
            Verb: "build",
            Usage: "build <addr|spec.json|article.md|dir|-> [-o webDir] [--verify] [--static-only] [--ephemeral] [--timeout 30] [--preview] [--no-wait]",
            Description: "Runs mandatory lint and emits the deterministic study WebApp (supports one-shot JSON, Markdown, directories, or stdin).",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            AllowedValueOptions: AllowedValues,
            Prerequisites: "Workspace ID must already exist in CWD or .meloberry/workspaces, or pass a spec file/dir/'-' for one-shot compile (persists workspace DB under .meloberry/workspaces unless --ephemeral is passed).",
            OptionsHelp:
            [
                "-o, --out <webDir>   Output WebApp directory (default: <projectRoot>/<id>_web or <workspaceParent>/<id>_web for existing workspaces; ./mby_web for one-shot).",
                "--verify             Run automated runtime verification after building (exits 2 on failure).",
                "--static-only        Check static HTML/JS/CSS/asset integrity without launching a browser.",
                "--ephemeral          For one-shot builds, compile inside a temporary workspace and delete it afterwards.",
                "--timeout <sec>      Headless browser verification timeout in seconds (default 30, range 5..180).",
                "--preview            Serve built WebApp locally.",
                "--no-wait            Exit immediately after printing preview URL (useful with --preview)."
            ],
            Examples:
            [
                "mby build ./articles/ -o ./study_web --verify --json",
                "mby build ./articles/ -o ./study_web --ephemeral --json",
                "mby build Session_2026... -o ./study_web --json"
            ]))
    {
    }

    public sealed class Handler(
        ApplySpecHandler applyHandler,
        BuildWebAppHandler buildWebHandler,
        ILocalPreviewHost previewHost) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {new BuildCliCommand().Descriptor.Usage}", context.GlobalOptions.Json);

            var target = context.PositionalArgs[0];
            var outDir = context.GetOptionValue("out");
            bool preview = context.HasFlag("preview");
            bool noWait = context.HasFlag("no-wait");
            bool verify = context.HasFlag("verify");
            bool staticOnly = context.HasFlag("static-only");
            bool ephemeral = context.HasFlag("ephemeral");
            int timeout = 30;
            var rawTimeout = context.GetOptionValue("timeout");
            if (!string.IsNullOrWhiteSpace(rawTimeout) && (!int.TryParse(rawTimeout, out timeout) || timeout is < 5 or > 180))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Invalid --timeout '{rawTimeout}'. Pass integer seconds between 5 and 180.", context.GlobalOptions.Json);

            bool isStdin = string.Equals(target, "-", StringComparison.Ordinal);
            bool isExistingFile = !isStdin && File.Exists(target);
            bool isExistingDir = !isStdin && !isExistingFile && Directory.Exists(target);
            bool isWorkspaceDbFolder = isExistingDir && IsWorkspaceDatabaseDirectory(target);
            bool isStrictWsId = !isStdin && !isExistingFile && StrictWorkspaceIdRegex().IsMatch(target);

            string webOutDir;
            if (isStdin || isExistingFile || (isExistingDir && !isWorkspaceDbFolder))
            {
                var (filePath, inlineJson, errExit) = await CliCommandHelpers.ReadJsonInputAsync(context, target);
                if (errExit is not null) return errExit.Value;

                var effectiveBuildOut = outDir ?? Path.Combine(Directory.GetCurrentDirectory(), "mby_web");
                var applyRes = await applyHandler.HandleAsync(
                    new ApplySpecCommand(
                        null,
                        filePath,
                        inlineJson,
                        WorkingDirectory: null,
                        BuildOutDir: effectiveBuildOut,
                        Quiet: context.GlobalOptions.Quiet,
                        Verbose: context.GlobalOptions.Verbose,
                        VerifyWeb: verify,
                        StaticVerifyOnly: staticOnly,
                        VerifyTimeoutSeconds: timeout,
                        Ephemeral: ephemeral),
                    context.CancellationToken);
                if (!applyRes.IsSuccess) return applyRes.ToExitCode(context.Console, context.GlobalOptions.Json);

                webOutDir = Path.GetDirectoryName(applyRes.Value.WebOutputPath!)!;
                if (!preview)
                {
                    CliCommandHelpers.WriteOutput(context, applyRes.Value, $"Built WebApp at '{applyRes.Value.WebOutputPath}'");
                    return ExitCodes.Success;
                }
            }
            else if (isStrictWsId || isWorkspaceDbFolder)
            {
                if (ephemeral)
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Option '--ephemeral' is only supported for one-shot spec/file/dir/'-' builds.", context.GlobalOptions.Json);

                var trimmedTarget = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var effectiveWsId = isWorkspaceDbFolder
                    ? Path.GetFileName(trimmedTarget)
                    : target;
                var workspaceParentDir = isWorkspaceDbFolder
                    ? Path.GetDirectoryName(Path.GetFullPath(trimmedTarget))
                    : null;
                var buildRes = await buildWebHandler.HandleAsync(
                    new BuildWebAppCommand(effectiveWsId, outDir, verify, context.GlobalOptions.Verbose, context.GlobalOptions.Quiet, staticOnly, timeout, workspaceParentDir),
                    context.CancellationToken);
                if (!buildRes.IsSuccess) return buildRes.ToExitCode(context.Console, context.GlobalOptions.Json);

                webOutDir = buildRes.Value.OutputDirectory;
                if (!preview)
                {
                    CliCommandHelpers.WriteOutput(context, buildRes.Value, $"Built WebApp at '{buildRes.Value.IndexHtmlPath}'");
                    return ExitCodes.Success;
                }
            }
            else
            {
                return Result.Failure(new Error(
                    "Json.FileNotFound",
                    $"Input file or directory '{target}' was not found.",
                    ErrorType.NotFound,
                    Hint: "Pass an existing spec file/directory, '-' for stdin, or a valid workspace ID (Session_YYYYMMDD_HHMMSS_fff / Set_YYYYMMDD_HHMMSS_fff)."))
                    .ToExitCode(context.Console, context.GlobalOptions.Json);
            }

            return await previewHost.ServeDirectoryAsync(webOutDir, context.Console, noWait, context.GlobalOptions.Json, silent: false, context.CancellationToken);
        }

        private static bool IsWorkspaceDatabaseDirectory(string directoryPath)
        {
            var trimmed = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dirName = Path.GetFileName(trimmed);
            return !string.IsNullOrWhiteSpace(dirName) &&
                   (dirName.StartsWith("Session_", StringComparison.Ordinal) || dirName.StartsWith("Set_", StringComparison.Ordinal)) &&
                   File.Exists(Path.Combine(trimmed, $"{dirName}.db"));
        }
    }
}

public sealed class PkgCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "include-layout" };

    public PkgCliCommand()
        : base(new CommandDescriptor(
            Verb: "pkg",
            Usage: "pkg <export <wsId> <desktop|android> [outPath] [--include-layout] | import <file.mbypkg> [destDir]>",
            Description: "Exports (content-only default, non-interactive) or imports .mbypkg v2.0 archives (backward-compatible with v1.0).",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            Prerequisites: "Runs mandatory lint before export; default export writes <wsId>.mbypkg into the workspace's project root (for .meloberry/workspaces) or parent directory unless [outPath] is given. [outPath] resolves as a directory if it exists or ends with '/' or '\\', otherwise as a .mbypkg file path. Import defaults to CWD unless [destDir] is given.",
            OptionsHelp:
            [
                "--include-layout   Bundle non-default global layout profiles & entity layoutOverrides inside the .mbypkg."
            ],
            Examples:
            [
                "mby pkg export Session_2026... android ./chapter2.mbypkg --json",
                "mby pkg import ./chapter2.mbypkg ./.meloberry/workspaces --json"
            ]))
    {
    }

    public sealed class Handler(
        ExportPackageHandler exportHandler,
        ImportPackageHandler importHandler) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count < 1)
                return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: pkg export <wsId> <desktop|android> [outPath] [--include-layout] | pkg import <file> [destDir]", context.GlobalOptions.Json));

            var sub = context.PositionalArgs[0].Trim().ToLowerInvariant();
            if (sub == "export")
            {
                if (context.PositionalArgs.Count is < 3 or > 4)
                    return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: pkg export <wsId> <desktop|android> [outPath] [--include-layout]", context.GlobalOptions.Json));

                var wsId = context.PositionalArgs[1];
                var platform = context.PositionalArgs[2];
                var outPath = context.PositionalArgs.Count == 4 ? context.PositionalArgs[3] : null;
                bool includeLayout = context.HasFlag("include-layout");

                return CliCommandHelpers.FinishAsync(
                    context,
                    exportHandler.HandleAsync(new ExportPackageCommand(wsId, outPath, platform, includeLayout), context.CancellationToken),
                    v => $"Exported {v.TargetPlatform} package to '{v.PackagePath}'");
            }

            if (sub == "import")
            {
                if (context.PositionalArgs.Count is < 2 or > 3)
                    return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: pkg import <mbypkgPath> [destDir]", context.GlobalOptions.Json));

                var pkgPath = context.PositionalArgs[1];
                var destDir = context.PositionalArgs.Count == 3 ? context.PositionalArgs[2] : null;
                return CliCommandHelpers.FinishAsync(
                    context,
                    importHandler.HandleAsync(new ImportPackageCommand(pkgPath, destDir), context.CancellationToken),
                    v => $"Imported {v.Kind} {v.ImportedId} into '{v.StorageDirectory}'");
            }

            return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Unknown pkg subcommand '{sub}'. Use 'export' or 'import'.", context.GlobalOptions.Json));
        }
    }
}

public sealed class LayoutCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "no-wait" };

    public LayoutCliCommand()
        : base(new CommandDescriptor(
            Verb: "layout",
            Usage: "layout <get [mcq|flashcard|article] | set <mcq|flashcard|article> <prop> <val> | reset [mcq|flashcard|article] [prop...] | edit [target] [--no-wait]>",
            Description: "Reads layout bounds, updates or resets global profile properties, or launches the local GUI layout editor.",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            Prerequisites: "Global layout profiles persist in <app-data>/app_settings.db. 'themeMode' and 'slideTransitionMs' are deck-global only and synchronize across all three profiles ('article', 'mcq', 'flashcard').",
            OptionsHelp:
            [
                "--no-wait   Exit immediately after printing the local GUI editor URL (used with 'layout edit')."
            ],
            Examples:
            [
                "mby layout get mcq --json",
                "mby layout set article blockSpacingPx 24 --json",
                "mby layout reset mcq optionColumns --json",
                "mby layout edit fill-blank --no-wait --json"
            ]))
    {
    }

    public sealed class Handler(
        GetLayoutBoundsHandler boundsHandler,
        SetLayoutPropertyHandler setHandler,
        ResetLayoutProfileHandler resetHandler,
        ILocalPreviewHost previewHost) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count < 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: layout <get|set|reset|edit> ...", context.GlobalOptions.Json);

            var sub = context.PositionalArgs[0].Trim().ToLowerInvariant();
            if (!string.Equals(sub, "edit", StringComparison.Ordinal) && context.HasFlag("no-wait"))
                return CliResultExtensions.WriteValidationError(
                    context.Console,
                    "Cli.UnsupportedOption",
                    $"Option '--no-wait' is not supported by 'layout {sub}'. Use '--no-wait' only with 'layout edit'.",
                    context.GlobalOptions.Json);

            if (sub == "get")
            {
                if (context.PositionalArgs.Count > 2)
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: layout get [mcq|flashcard|article]", context.GlobalOptions.Json);

                var targetType = context.PositionalArgs.Count == 2 ? context.PositionalArgs[1] : null;
                return await CliCommandHelpers.FinishAsync(
                    context,
                    boundsHandler.HandleAsync(new GetLayoutBoundsQuery(targetType), context.CancellationToken),
                    res => string.Join(
                        Environment.NewLine,
                        res.Properties.Select(p =>
                            $"{p.TargetType,-9} | {p.Property,-20} | current={p.CurrentValue,-10} | bounds=[{p.AllowedBounds}] (default={p.DefaultValue})")));
            }

            if (sub == "set")
            {
                if (context.PositionalArgs.Count != 4)
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: layout set <mcq|flashcard|article> <property> <value>", context.GlobalOptions.Json);

                return await CliCommandHelpers.FinishAsync(
                    context,
                    setHandler.HandleAsync(new SetLayoutPropertyCommand(context.PositionalArgs[1], context.PositionalArgs[2], context.PositionalArgs[3]), context.CancellationToken),
                    v => $"Updated {v.TargetIdOrType}.{v.Property} = {v.NormalizedValue}");
            }

            if (sub == "reset")
            {
                if (context.PositionalArgs.Count == 1)
                {
                    var targets = Meloberry.Domain.Layouts.LayoutBoundsCatalog.SupportedTargets;
                    var resetTargets = new List<string>(targets.Count);
                    foreach (var target in targets)
                    {
                        var res = await resetHandler.HandleAsync(new ResetLayoutProfileCommand(target), context.CancellationToken);
                        if (!res.IsSuccess) return res.ToExitCode(context.Console, context.GlobalOptions.Json);
                        resetTargets.Add(res.Value.TargetType);
                    }
                    CliCommandHelpers.WriteOutput(
                        context,
                        new { reset = true, targets = resetTargets, keys = (IReadOnlyList<string>?)null },
                        $"Reset all layout profiles ({string.Join(", ", resetTargets)}) to defaults.");
                    return ExitCodes.Success;
                }

                var resetTarget = context.PositionalArgs[1];
                IReadOnlyList<string>? resetKeys = context.PositionalArgs.Count > 2
                    ? context.PositionalArgs.Skip(2).ToList()
                    : null;
                var singleRes = await resetHandler.HandleAsync(new ResetLayoutProfileCommand(resetTarget, resetKeys), context.CancellationToken);
                if (!singleRes.IsSuccess) return singleRes.ToExitCode(context.Console, context.GlobalOptions.Json);

                var summary = resetKeys is { Count: > 0 }
                    ? $"Reset {singleRes.Value.TargetType} properties ({string.Join(", ", resetKeys)}) to defaults."
                    : $"Reset {singleRes.Value.TargetType} layout profile to defaults.";
                CliCommandHelpers.WriteOutput(
                    context,
                    new { reset = true, targets = new[] { singleRes.Value.TargetType }, keys = resetKeys },
                    summary);
                return ExitCodes.Success;
            }

            if (sub == "edit")
            {
                if (context.PositionalArgs.Count > 2)
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: layout edit [target] [--no-wait]", context.GlobalOptions.Json);

                var targetType = context.PositionalArgs.Count == 2 ? context.PositionalArgs[1] : "article-text";
                return await previewHost.ServeLayoutEditorAsync(targetType, context.Console, context.HasFlag("no-wait"), context.GlobalOptions.Json, silent: context.GlobalOptions.Quiet, context.CancellationToken);
            }

            return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Unknown layout subcommand '{sub}'. Use 'get', 'set', 'reset', or 'edit'.", context.GlobalOptions.Json);
        }
    }
}

public sealed class AnalyzeCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "all", "live", "no-wait", "headless", "static-only" };
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "timeout" };

    public AnalyzeCliCommand()
        : base(new CommandDescriptor(
            Verb: "analyze",
            Usage: "analyze <webDir|report.json> [--headless] [--static-only] [--timeout 30] [--all] [--live] [--no-wait]",
            Description: "Runs zero-touch headless browser/static verification (--headless/--static-only) or analyzes captured *.analysis.json telemetry reports.",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            AllowedValueOptions: AllowedValues,
            Prerequisites: "For --headless: Edge or Chrome installed (or MBY_BROWSER_PATH set). For telemetry mode (without --headless/--static-only): requires reports/*.analysis.json from a prior --live or --preview browser run.",
            OptionsHelp:
            [
                "--headless     Launch headless Edge/Chrome via CDP, step every slide/question, check images/overflow/KaTeX/console, write reports/verify.latest.json.",
                "--static-only  Verify HTML/JS/CSS, DATA JSON, and referenced local image assets on disk without launching a browser.",
                "--timeout <n>  Timeout in seconds for --headless (default 30, range 5..180).",
                "--live         Start preview server + /api/analysis receiver to save runtime session telemetry.",
                "--all          Aggregate all saved *.analysis.json telemetry files in <webDir>/reports.",
                "--no-wait      Exit immediately after printing URL in --live mode."
            ],
            Examples:
            [
                "mby analyze ./study_web --headless --json",
                "mby analyze ./study_web --static-only --json",
                "mby analyze ./study_web --all --no-wait --json"
            ]))
    {
    }

    public sealed class Handler(
        AnalyzeWebAppHandler useCase,
        VerifyWebAppHandler verifyUseCase,
        ILocalPreviewHost previewHost) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {new AnalyzeCliCommand().Descriptor.Usage}", context.GlobalOptions.Json);

            var targetPath = context.PositionalArgs[0];
            bool headless = context.HasFlag("headless");
            bool staticOnly = context.HasFlag("static-only");

            if (headless || staticOnly)
            {
                int timeout = 30;
                var rawTimeout = context.GetOptionValue("timeout");
                if (!string.IsNullOrWhiteSpace(rawTimeout) && (!int.TryParse(rawTimeout, out timeout) || timeout is < 5 or > 180))
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Invalid --timeout '{rawTimeout}'. Pass integer seconds between 5 and 180.", context.GlobalOptions.Json);

                var verRes = await verifyUseCase.HandleAsync(new VerifyWebAppCommand(targetPath, staticOnly, timeout), context.CancellationToken);
                if (!verRes.IsSuccess) return verRes.ToExitCode(context.Console, context.GlobalOptions.Json);

                var report = verRes.Value;
                var summary = $"Verify {(report.Passed ? "PASSED" : "FAILED")} ({report.Engine}): slides={report.SlidesChecked}, images={report.ImagesChecked}, consoleErrors={report.ConsoleErrors.Count}, brokenAssets={report.BrokenAssets.Count}, overflow={report.OverflowIssues.Count}, katexErrors={report.KatexErrors.Count}";

                if (!report.Passed)
                {
                    if (context.GlobalOptions.Json)
                    {
                        return Result.Failure(new Error(
                            "Verify.Failed",
                            summary,
                            ErrorType.Validation,
                            Hint: string.IsNullOrWhiteSpace(report.ReportPath) ? null : $"Inspect verification details at '{report.ReportPath}'.",
                            Data: report)).ToExitCode(context.Console, json: true);
                    }
                    context.Console.Error.WriteLine(summary);
                    return ExitCodes.ValidationError;
                }

                CliCommandHelpers.WriteOutput(context, report, summary);
                return ExitCodes.Success;
            }

            if (context.HasFlag("live"))
                return await previewHost.ServeForLiveAnalysisAsync(targetPath, context.Console, context.HasFlag("no-wait"), context.GlobalOptions.Json, context.CancellationToken);

            return await CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(new AnalyzeWebAppQuery(targetPath, context.HasFlag("all")), context.CancellationToken),
                v => v.MarkdownSummary);
        }
    }
}
