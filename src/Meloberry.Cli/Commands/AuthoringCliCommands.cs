using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Json;
using Meloberry.Cli.Abstractions;

namespace Meloberry.Cli.Commands;

public sealed class ApplyCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "dry-run" };
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "out", "build", "export" };

    public ApplyCliCommand()
        : base(new CommandDescriptor(
            Verb: "apply",
            Usage: "apply [addr] <file|-> [-o dir] [--build dir] [--export plat:path] [--dry-run]",
            Description: "Creates or appends to a session/quiz/component from JSON file or stdin ('-').",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            AllowedValueOptions: AllowedValues))
    {
    }

    public sealed class Handler(ApplySpecHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count is < 1 or > 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {new ApplyCliCommand().Descriptor.Usage}", context.GlobalOptions.Json);

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
                ExportSpec: context.GetOptionValue("export"));

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
            AllowedValueOptions: AllowedValues))
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
            HandlerType: typeof(Handler)))
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
            HandlerType: typeof(Handler)))
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
