using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Json;
using Meloberry.Cli.Abstractions;

namespace Meloberry.Cli.Commands;

public sealed class ApplyCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "dry-run", "verify", "static-only", "all-themes", "signal" };
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "out", "build", "export", "timeout", "at", "signal-dir" };

    public ApplyCliCommand()
        : base(new CommandDescriptor(
            Verb: "apply",
            Usage: "apply [addr] <file|dir|-> [-o dir] [--at idx] [--build dir] [--verify] [--static-only] [--all-themes] [--signal] [--signal-dir dir] [--timeout 30] [--export plat[:path]] [--dry-run] [--quiet|--verbose]",
            Description: "Creates or appends to a session/quiz/component from JSON, Markdown (.md), a modular directory, or stdin ('-').",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            AllowedValueOptions: AllowedValues,
            Prerequisites: "Input must be a valid JSON spec, Markdown (.md), directory of .md/.json, or '-' (stdin). Optional [addr] targets a session, component, or standalone quiz only (use 'patch' for blocks/questions). Plain 'apply' allows draft writes and returns data.lint; mandatory exit-2 lint gating runs on 'check', 'build', 'apply --build', 'apply --export', and 'pkg export'.",
            OptionsHelp:
            [
                "-o, --out <dir>          Base workspace folder (defaults to .meloberry/workspaces).",
                "--at <idx>               Insert appended item(s) starting at 1-based index <idx>.",
                "--build <webDir>         Also compile the study WebApp into <webDir>.",
                "--all-themes             When --build is set, emit all 4 CSS themes + postMessage theme bridge.",
                "--signal                 When --build is set, write atomic .mby-signal.txt notification.",
                "--signal-dir <dir>       Write .mby-signal.txt into <dir> (implies --signal when --build is set).",
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

            if (!CliCommandHelpers.TryParseTimeoutOption(context, out int timeout, out int? timeoutErr))
                return timeoutErr!.Value;

            int? insertAt = null;
            var rawAt = context.GetOptionValue("at");
            if (!string.IsNullOrWhiteSpace(rawAt))
            {
                if (!int.TryParse(rawAt, out int parsedAt))
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Invalid --at value '{rawAt}'. Pass a 1-based integer.", context.GlobalOptions.Json);
                insertAt = parsedAt;
            }

            string? targetAddr = context.PositionalArgs.Count == 2 ? context.PositionalArgs[0] : null;
            string fileOrDash = context.PositionalArgs.Count == 2 ? context.PositionalArgs[1] : context.PositionalArgs[0];

            var (filePath, inlineJson, errExit) = await CliCommandHelpers.ReadJsonInputAsync(context, fileOrDash);
            if (errExit is not null) return errExit.Value;

            var signalDir = context.GetOptionValue("signal-dir");
            bool emitSignal = context.HasFlag("signal") || !string.IsNullOrWhiteSpace(signalDir);

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
                VerifyTimeoutSeconds: timeout,
                InsertAtIndex: insertAt,
                IncludeAllThemes: context.HasFlag("all-themes"),
                EmitSignal: emitSignal,
                SignalDirectory: signalDir);

            return await CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(cmd, context.CancellationToken),
                v => $"{(v.DryRun ? "Dry-run valid" : (v.CreatedWorkspace ? "Created" : "Updated"))} {v.EntityKind} {v.TargetId}");
        }
    }
}

public sealed class PatchCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "md" };
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "set", "move", "to", "at", "order", "subtype", "image" };

    public PatchCliCommand()
        : base(new CommandDescriptor(
            Verb: "patch",
            Usage: "patch <addr> [file|-] [--set k=v] [--move idx:shift|exchange] [--to compAddr[:idx]] [--at idx] [--order id1,id2,...] [--md] [--subtype s] [--image path]",
            Description: "Surgically updates or reorganizes an entity by ID, path (#c1.b2), or #ref.",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            AllowedValueOptions: AllowedValues,
            Prerequisites: "Target entity must exist (addressed by direct ID, <sessionId>#c<n>[.b<m>|.q<m>], <setId>#q<m>, or <wsId>#<ref>). Provide a non-empty patch JSON file/'-', --set, --move, --to, --order, or --md.",
            OptionsHelp:
            [
                "--set <k=v>              Repeatable field update; supports dot paths (e.g. layoutOverrides.<k>=<v|null>, front.text=...).",
                "--move <idx>:<mode>      Reorder component, block, or question to 1-based <idx> using 'shift' or 'exchange'.",
                "--to <compAddr>[:<idx>]  Move a block or question into another component in the same session.",
                "--at <idx>               Optional 1-based target index when combined with --to.",
                "--order <id1,id2,...>    Exact comma-separated permutation of IDs or #refs on a session, component, or quiz.",
                "--md                     Treat input file/'-' as a Markdown fragment (smart-wrapped for the target type).",
                "--subtype <key>          Optional subtype hint when using --md (e.g. clinical, multi, blur).",
                "--image <path>           Optional attached image path when using --md."
            ],
            Examples:
            [
                "mby patch Session_2026...#c2.q1 --set text=\"Updated prompt\" --set layoutOverrides.optionColumns=2 --json",
                "mby patch Session_2026...#c1.b2 --move 1:shift --json",
                "mby patch Session_2026...#c1.b2 --to Session_2026...#c2:1 --json",
                "mby patch Session_2026...#c1 --order #b2,#b1 --json"
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

            int? moveToIdx = null;
            var rawAt = context.GetOptionValue("at");
            if (!string.IsNullOrWhiteSpace(rawAt))
            {
                if (!int.TryParse(rawAt, out int parsedAt))
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Invalid --at value '{rawAt}'. Pass a 1-based integer.", context.GlobalOptions.Json);
                moveToIdx = parsedAt;
            }

            IReadOnlyList<string>? orderIds = null;
            var rawOrder = context.GetOptionValue("order");
            if (!string.IsNullOrWhiteSpace(rawOrder))
            {
                orderIds = rawOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            var cmd = new PatchEntityCommand(
                Address: addr,
                JsonFilePath: filePath,
                InlineJson: inlineJson,
                SetPairs: context.GetOptionValues("set"),
                MoveSpec: context.GetOptionValue("move"),
                MoveToTargetAddress: context.GetOptionValue("to"),
                MoveToIndex: moveToIdx,
                ExactOrderIds: orderIds,
                IsMarkdown: context.HasFlag("md"),
                MarkdownSubtype: context.GetOptionValue("subtype"),
                AttachedImagePath: context.GetOptionValue("image"));

            return await CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(cmd, context.CancellationToken),
                v => $"Patched {v.EntityKind} {v.TargetId}");
        }
    }
}

public sealed class CloneCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "to", "at", "ref" };

    public CloneCliCommand()
        : base(new CommandDescriptor(
            Verb: "clone",
            Usage: "clone <addr> [--to <compAddr>] [--at <idx>] [--ref <slug>]",
            Description: "Duplicates an existing component, article block, or question with fresh IDs and non-colliding refs.",
            HandlerType: typeof(Handler),
            AllowedValueOptions: AllowedValues,
            Prerequisites: "Target must resolve to a component, articleBlock, or question.",
            OptionsHelp:
            [
                "--to <compAddr>  Optional target container in the same workspace.",
                "--at <idx>       Optional 1-based insertion index.",
                "--ref <slug>     Optional custom ref slug for the cloned entity."
            ],
            Examples:
            [
                "mby clone Session_2026...#c1.b1 --json",
                "mby clone Session_2026...#q1 --to Session_2026...#quiz2 --at 1 --ref q1-copy --json"
            ]))
    {
    }

    public sealed class Handler(CloneEntityHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {new CloneCliCommand().Descriptor.Usage}", context.GlobalOptions.Json);

            int? atIdx = null;
            var rawAt = context.GetOptionValue("at");
            if (!string.IsNullOrWhiteSpace(rawAt))
            {
                if (!int.TryParse(rawAt, out int parsedAt))
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Invalid --at value '{rawAt}'. Pass a 1-based integer.", context.GlobalOptions.Json);
                atIdx = parsedAt;
            }

            var cmd = new CloneEntityCommand(
                context.PositionalArgs[0],
                context.GetOptionValue("to"),
                atIdx,
                context.GetOptionValue("ref"));

            return await CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(cmd, context.CancellationToken),
                v => $"Cloned {v.EntityKind} -> {v.NewId}");
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
