using Meloberry.Application.Abstractions;
using Meloberry.Cli.Abstractions;

namespace Meloberry.Cli.Commands;

public sealed class VersionCliCommand : CliCommand
{
    public VersionCliCommand()
        : base(new CommandDescriptor(
            Verb: "version",
            Usage: "version",
            Description: "Displays the Meloberry CLI version, product name, runtime details, and capabilities (in --json).",
            HandlerType: typeof(Handler),
            Prerequisites: "Stateless; also accessible via '-v' or '--version'.",
            OptionsHelp:
            [
                "(No command-specific options; supports global --json, which includes data.capabilities.)"
            ],
            Examples:
            [
                "mby version",
                "mby -v --json"
            ]))
    {
    }

    public sealed class Handler(ICliToolMetadataProvider metadataProvider) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            var meta = metadataProvider.GetMetadata();
            if (meta.Capabilities is not { Count: > 0 })
                meta = meta with { Capabilities = CliCapabilities.All };
            var text = $"{meta.ProductName} ({meta.ExecutableName}) v{meta.Version} | {meta.Runtime}";
            CliCommandHelpers.WriteOutput(context, meta, text);
            return Task.FromResult(ExitCodes.Success);
        }
    }
}
