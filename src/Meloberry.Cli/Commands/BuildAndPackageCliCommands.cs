using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Layouts;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Cli.Abstractions;

namespace Meloberry.Cli.Commands;

public sealed class BuildCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "preview", "no-wait" };
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "out" };

    public BuildCliCommand()
        : base(new CommandDescriptor(
            Verb: "build",
            Usage: "build <addr|spec.json|-> [-o dir] [--preview] [--no-wait]",
            Description: "Runs mandatory lint and emits the deterministic study WebApp (supports one-shot JSON/stdin).",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            AllowedValueOptions: AllowedValues))
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

            string webOutDir;
            if (string.Equals(target, "-", StringComparison.Ordinal) ||
                (!IsWorkspaceId(target) && File.Exists(target)))
            {
                var (filePath, inlineJson, errExit) = await CliCommandHelpers.ReadJsonInputAsync(context, target);
                if (errExit is not null) return errExit.Value;

                var applyOutDir = outDir is null ? null : (Path.GetDirectoryName(Path.GetFullPath(outDir)) ?? Path.GetFullPath(outDir));
                var effectiveBuildOut = outDir ?? Path.Combine(Directory.GetCurrentDirectory(), "mby_web");
                var applyRes = await applyHandler.HandleAsync(
                    new ApplySpecCommand(null, filePath, inlineJson, WorkingDirectory: applyOutDir, BuildOutDir: effectiveBuildOut),
                    context.CancellationToken);
                if (!applyRes.IsSuccess) return applyRes.ToExitCode(context.Console, context.GlobalOptions.Json);

                webOutDir = Path.GetDirectoryName(applyRes.Value.WebOutputPath!)!;
                if (!preview)
                {
                    CliCommandHelpers.WriteOutput(context, applyRes.Value, $"Built WebApp at '{applyRes.Value.WebOutputPath}'");
                    return ExitCodes.Success;
                }
            }
            else
            {
                var buildRes = await buildWebHandler.HandleAsync(new BuildWebAppCommand(target, outDir), context.CancellationToken);
                if (!buildRes.IsSuccess) return buildRes.ToExitCode(context.Console, context.GlobalOptions.Json);

                webOutDir = buildRes.Value.OutputDirectory;
                if (!preview)
                {
                    CliCommandHelpers.WriteOutput(context, buildRes.Value, $"Built WebApp at '{buildRes.Value.IndexHtmlPath}'");
                    return ExitCodes.Success;
                }
            }

            return await previewHost.ServeDirectoryAsync(webOutDir, context.Console, noWait, context.GlobalOptions.Json, silent: false, context.CancellationToken);
        }

        private static bool IsWorkspaceId(string raw) =>
            raw.StartsWith("Session_", StringComparison.Ordinal) || raw.StartsWith("Set_", StringComparison.Ordinal);
    }
}

public sealed class PkgCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "include-layout" };

    public PkgCliCommand()
        : base(new CommandDescriptor(
            Verb: "pkg",
            Usage: "pkg <export|import> ...",
            Description: "Exports (content-only default, non-interactive) or imports .mbypkg v1.0 archives.",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags))
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
            Usage: "layout <get|set|edit> ...",
            Description: "Reads layout bounds, updates global profile properties, or launches the local GUI layout editor.",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags))
    {
    }

    public sealed class Handler(
        GetLayoutBoundsHandler boundsHandler,
        SetLayoutPropertyHandler setHandler,
        ILocalPreviewHost previewHost) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count < 1)
                return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: layout <get|set|edit> ...", context.GlobalOptions.Json));

            var sub = context.PositionalArgs[0].Trim().ToLowerInvariant();
            if (sub == "get")
            {
                if (context.PositionalArgs.Count > 2)
                    return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: layout get [mcq|flashcard|article]", context.GlobalOptions.Json));

                var targetType = context.PositionalArgs.Count == 2 ? context.PositionalArgs[1] : null;
                return CliCommandHelpers.FinishAsync(
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
                    return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: layout set <mcq|flashcard|article> <property> <value>", context.GlobalOptions.Json));

                return CliCommandHelpers.FinishAsync(
                    context,
                    setHandler.HandleAsync(new SetLayoutPropertyCommand(context.PositionalArgs[1], context.PositionalArgs[2], context.PositionalArgs[3]), context.CancellationToken),
                    v => $"Updated {v.TargetIdOrType}.{v.Property} = {v.NormalizedValue}");
            }

            if (sub == "edit")
            {
                var targetType = context.PositionalArgs.Count >= 2 ? context.PositionalArgs[1] : "article-text";
                return previewHost.ServeLayoutEditorAsync(targetType, context.Console, context.HasFlag("no-wait"), context.GlobalOptions.Json, silent: false, context.CancellationToken);
            }

            return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Unknown layout subcommand '{sub}'. Use 'get', 'set', or 'edit'.", context.GlobalOptions.Json));
        }
    }
}

public sealed class AnalyzeCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "all", "live", "no-wait" };

    public AnalyzeCliCommand()
        : base(new CommandDescriptor(
            Verb: "analyze",
            Usage: "analyze <webDir> [--all] [--live] [--no-wait]",
            Description: "Analyzes completed WebApp telemetry reports or serves a live capture endpoint.",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags))
    {
    }

    public sealed class Handler(
        AnalyzeWebAppHandler useCase,
        ILocalPreviewHost previewHost) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: analyze <webDir> [--all] [--live] [--no-wait]", context.GlobalOptions.Json));

            var targetPath = context.PositionalArgs[0];
            if (context.HasFlag("live"))
                return previewHost.ServeForLiveAnalysisAsync(targetPath, context.Console, context.HasFlag("no-wait"), context.GlobalOptions.Json, context.CancellationToken);

            return CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(new AnalyzeWebAppQuery(targetPath, context.HasFlag("all")), context.CancellationToken),
                v => v.MarkdownSummary);
        }
    }
}
