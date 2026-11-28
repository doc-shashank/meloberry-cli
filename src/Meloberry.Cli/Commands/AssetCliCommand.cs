using Meloberry.Application.UseCases.Common;
using Meloberry.Cli.Abstractions;

namespace Meloberry.Cli.Commands;

public sealed class AssetCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "dry-run" };
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "dir" };

    public AssetCliCommand()
        : base(new CommandDescriptor(
            Verb: "asset",
            Usage: "asset <add|ls|prune> <wsAddr|--dir dir> [filePath] [--dry-run]",
            Description: "Validates, content-hashes, lists, or prunes workspace assets.",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            AllowedValueOptions: AllowedValues,
            Prerequisites: "Subcommands: 'add <wsAddr> <filePath>' (or 'add --dir <dir> <filePath>'), 'ls <wsAddr>', 'prune <wsAddr> [--dry-run]'.",
            OptionsHelp:
            [
                "--dir <dir>  Target staging directory for 'asset add' before a workspace DB exists.",
                "--dry-run    Report unreferenced assets without deleting them ('asset prune')."
            ],
            Examples:
            [
                "mby asset add Session_2026... ./diagram.png --json",
                "mby asset add --dir ./staging ./diagram.png --json",
                "mby asset ls Session_2026... --json",
                "mby asset prune Session_2026... --dry-run --json"
            ]))
    {
    }

    public sealed class Handler(
        AddAssetHandler addHandler,
        ListAssetsHandler listHandler,
        PruneAssetsHandler pruneHandler) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count < 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {new AssetCliCommand().Descriptor.Usage}", context.GlobalOptions.Json);

            var sub = context.PositionalArgs[0].Trim().ToLowerInvariant();
            switch (sub)
            {
                case "add":
                {
                    var dir = context.GetOptionValue("dir");
                    string? wsAddr = null;
                    string? filePath = null;
                    if (!string.IsNullOrWhiteSpace(dir) && context.PositionalArgs.Count == 2)
                    {
                        filePath = context.PositionalArgs[1];
                    }
                    else if (context.PositionalArgs.Count == 3)
                    {
                        wsAddr = context.PositionalArgs[1];
                        filePath = context.PositionalArgs[2];
                    }
                    else
                    {
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: mby asset add <wsAddr> <filePath> | mby asset add --dir <dir> <filePath>", context.GlobalOptions.Json);
                    }

                    return await CliCommandHelpers.FinishAsync(
                        context,
                        addHandler.HandleAsync(new AddAssetCommand(wsAddr, filePath, dir), context.CancellationToken),
                        r => r.RelativeAssetPath);
                }

                case "ls":
                {
                    if (context.PositionalArgs.Count != 2)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: mby asset ls <wsAddr>", context.GlobalOptions.Json);

                    return await CliCommandHelpers.FinishAsync(
                        context,
                        listHandler.HandleAsync(new ListAssetsQuery(context.PositionalArgs[1]), context.CancellationToken),
                        r => $"{r.Assets.Count} asset(s) ({r.Assets.Count(a => a.IsOrphan)} orphan)");
                }

                case "prune":
                {
                    if (context.PositionalArgs.Count != 2)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: mby asset prune <wsAddr> [--dry-run]", context.GlobalOptions.Json);

                    return await CliCommandHelpers.FinishAsync(
                        context,
                        pruneHandler.HandleAsync(new PruneAssetsCommand(context.PositionalArgs[1], context.HasFlag("dry-run")), context.CancellationToken),
                        r => $"{(r.DryRun ? "Would prune" : "Pruned")} {r.RemovedAssets.Count} asset(s) ({r.BytesFreed} bytes)");
                }

                default:
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Unknown asset subcommand '{sub}'. Use 'add', 'ls', or 'prune'.", context.GlobalOptions.Json);
            }
        }
    }
}
