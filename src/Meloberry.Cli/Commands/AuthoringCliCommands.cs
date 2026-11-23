using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Json;
using Meloberry.Cli.Abstractions;

namespace Meloberry.Cli.Commands;

public sealed class ApplyCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "dry-run", "verify", "static-only" };
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "out", "build", "export", "timeout" };

    public ApplyCliCommand()
        : base(new CommandDescriptor(
            Verb: "apply",
            Usage: "apply [addr] <file|dir|-> [-o dir] [--build dir] [--verify] [--static-only] [--timeout 30] [--export plat[:path]] [--dry-run] [--quiet|--verbose]",
            Description: "Creates or appends to a session/quiz/component from JSON, Markdown (.md), a modular directory, or stdin ('-').",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            AllowedValueOptions: AllowedValues,
            Prerequisites: "Input must be a valid JSON spec, Markdown (.md), directory of .md/.json, or '-' (stdin). '--verify' and '--static-only' require '--build <webDir>'.",
            OptionsHelp:
            [
                "-o, --out <dir>          Base workspace folder (defaults to .meloberry/workspaces).",
                "--build <webDir>         Also compile the study WebApp into <webDir>.",
                "--verify                 Run headless verification on --build output.",
                "--static-only            Run static asset/DATA check when --build is set (without launching a browser).",
                "--timeout <sec>          Timeout for headless verification in seconds (default 30, range 5..180).",
                "--export <plat[:path]>   Also export desktop/android .mbypkg archive.",
                "--dry-run                Validate and lint in memory without writing workspace DB."
            ],
            Examples:
            [
                "mby apply ./articles/ --json",
                "mby apply ./session.json --build ./study_web --verify --json",
                "mby apply Session_... ./extra_chapter.md --json"
            ]))
    {
    }

    public sealed class Handler(ApplySpecHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count is < 1 or > 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {new ApplyCliCommand().Descriptor.Usage}", context.GlobalOptions.Json);

            int timeout = 30;
            var rawTimeout = context.GetOptionValue("timeout");
            if (!string.IsNullOrWhiteSpace(rawTimeout) && (!int.TryParse(rawTimeout, out timeout) || timeout < 5))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Invalid --timeout '{rawTimeout}'. Pass integer seconds >= 5.", context.GlobalOptions.Json);

            string? targetAddr = context.PositionalArgs.Count == 2 ? context.PositionalArgs[0] : null;
            string fileOrDash = context.PositionalArgs.Count == 2 ? context.PositionalArgs[1] : context.PositionalArgs[0];

            var (filePath, inlineJson, errExit) = await CliCommandHelpers.ReadJsonInputAsync(context, fileOrDash);
            if (errExit is not null) return errExit.Value;

            var cmd = new ApplySpecCommand(
                TargetAddress: targetAddr,
                JsonFilePath: filePath,
                InlineJson: inlineJson,
                WorkingDirectory: context.GetOptionValue("out"),
                DryRun: context.HasFlag("dry-run"),
                BuildOutDir: context.GetOptionValue("build"),
                ExportSpec: context.GetOptionValue("export"),
                Quiet: context.GlobalOptions.Quiet,
                Verbose: context.GlobalOptions.Verbose,
                VerifyWeb: context.HasFlag("verify"),
                StaticVerifyOnly: context.HasFlag("static-only"),
                VerifyTimeoutSeconds: timeout);

            return await CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(cmd, context.CancellationToken),
                v => $"{(v.DryRun ? "Dry-run valid" : (v.CreatedWorkspace ? "Created" : "Updated"))} {v.EntityKind} {v.TargetId}");
        }
    }
}

public sealed class PatchCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "set", "move" };

    public PatchCliCommand()
        : base(new CommandDescriptor(
            Verb: "patch",
            Usage: "patch <addr> [file|-] [--set k=v] [--move idx:shift|exchange]",
            Description: "Surgically updates or reorders an entity by ID, path (#c1.b2), or #ref.",
            HandlerType: typeof(Handler),
            AllowedValueOptions: AllowedValues,
            Prerequisites: "Target entity must exist (addressed by direct ID, <wsId>#c<n>[.b<m>|.q<m>], <setId>#q<m>, or <wsId>#<ref>). Provide a patch JSON file/'-', --set, and/or --move.",
            OptionsHelp:
            [
                "--set <k=v>          Repeatable field update; supports dot paths (e.g. layoutOverrides.<k>=<v|null>, front.text=...).",
                "--move <idx>:<mode>  Reorder component, block, or question to 1-based <idx> using 'shift' or 'exchange'."
            ],
            Examples:
            [
                "mby patch Session_2026...#c2.q1 --set text=\"Updated prompt\" --set layoutOverrides.optionColumns=2 --json",
                "mby patch Session_2026...#c1.b2 --move 1:shift --json",
                "mby patch Session_2026...#intro ./patch.json --json"
            ]))
    {
    }

    public sealed class Handler(PatchEntityHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count is < 1 or > 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {new PatchCliCommand().Descriptor.Usage}", context.GlobalOptions.Json);

            string addr = context.PositionalArgs[0];
            string? filePath = null;
            string? inlineJson = null;

            if (context.PositionalArgs.Count == 2)
            {
                var (fp, ij, errExit) = await CliCommandHelpers.ReadJsonInputAsync(context, context.PositionalArgs[1]);
                if (errExit is not null) return errExit.Value;
                filePath = fp;
                inlineJson = ij;
            }

            var cmd = new PatchEntityCommand(
                Address: addr,
                JsonFilePath: filePath,
                InlineJson: inlineJson,
                SetPairs: context.GetOptionValues("set"),
                MoveSpec: context.GetOptionValue("move"));

            return await CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(cmd, context.CancellationToken),
                v => $"Patched {v.EntityKind} {v.TargetId}");
        }
    }
}

public sealed class BatchCliCommand : CliCommand
{
    public BatchCliCommand()
        : base(new CommandDescriptor(
            Verb: "batch",
            Usage: "batch <file|->",
            Description: "Executes a JSON operations array atomically inside one transaction.",
            HandlerType: typeof(Handler),
            Prerequisites: "Input must be a non-empty JSON array of operations ('apply', 'patch', 'rm', 'check', 'build', 'pkg', 'layout'). Any failing step rolls back the entire transaction.",
            OptionsHelp:
            [
                "(No command-specific options; supports global --json, --app-data, --quiet, --verbose. See 'mby spec batch' for operation schema.)"
            ],
            Examples:
            [
                "mby batch ./ops.json --json",
                "mby batch - --json < ops.json"
            ]))
    {
    }

    public sealed class Handler(BatchOperationsHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: batch <file|->", context.GlobalOptions.Json);

            var (filePath, inlineJson, errExit) = await CliCommandHelpers.ReadJsonInputAsync(context, context.PositionalArgs[0]);
            if (errExit is not null) return errExit.Value;

            return await CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(new BatchOperationsCommand(filePath, inlineJson), context.CancellationToken),
                v => $"Batch committed {v.StepCount} step(s).");
        }
    }
}

public sealed class RmCliCommand : CliCommand
{
    public RmCliCommand()
        : base(new CommandDescriptor(
            Verb: "rm",
            Usage: "rm <addr>",
            Description: "Deletes a workspace, component, block, or question non-interactively.",
            HandlerType: typeof(Handler),
            Prerequisites: "Deletes immediately without confirmation. Target workspace, component, block, or question must exist.",
            OptionsHelp:
            [
                "(No command-specific options; supports global --json, --app-data, --quiet, --verbose.)"
            ],
            Examples:
            [
                "mby rm Session_2026...#c1.b2 --json",
                "mby rm Session_2026... --json"
            ]))
    {
    }

    public sealed class Handler(DeleteEntityHandler useCase) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: rm <addr>", context.GlobalOptions.Json));

            return CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(new DeleteEntityCommand(context.PositionalArgs[0]), context.CancellationToken),
                v => $"Deleted {v.EntityKind} {v.DeletedId}");
        }
    }
}
