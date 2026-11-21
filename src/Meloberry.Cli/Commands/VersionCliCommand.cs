using Meloberry.Application.Abstractions;
using Meloberry.Cli.Abstractions;

namespace Meloberry.Cli.Commands;

public sealed class VersionCliCommand : CliCommand
{
    public VersionCliCommand()
        : base(new CommandDescriptor(
            Verb: "version",
            Usage: "version",
            Description: "Displays the Meloberry CLI version, product name, and runtime details.",
            HandlerType: typeof(Handler)))
    {
    }

    public sealed class Handler(ICliToolMetadataProvider metadataProvider) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            var meta = metadataProvider.GetMetadata();
            var text = $"{meta.ProductName} ({meta.ExecutableName}) v{meta.Version} | {meta.Runtime}";
            CliCommandHelpers.WriteOutput(context, meta, text);
            return Task.FromResult(ExitCodes.Success);
        }
    }
}
