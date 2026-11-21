using System.Text;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Cli.Abstractions;

namespace Meloberry.Cli.Commands;

public sealed class PrimeCliCommand : CliCommand
{
    public PrimeCliCommand()
        : base(new CommandDescriptor(
            Verb: "prime",
            Usage: "prime",
            Description: "Prints the ultra-dense AI-agent grammar card.",
            HandlerType: typeof(Handler)))
    {
    }

    public sealed class Handler(GetPrimeGuideHandler useCase) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context) =>
            CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(context.CancellationToken),
                r => r.Guide);
    }
}

public sealed class SpecCliCommand : CliCommand
{
    public SpecCliCommand()
        : base(new CommandDescriptor(
            Verb: "spec",
            Usage: "spec [type...]",
            Description: "Prints compact schemas for implemented JSON specs and layout bounds.",
            HandlerType: typeof(Handler)))
    {
    }

    public sealed class Handler(GetSchemaHandler useCase) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context) =>
            CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(new GetSchemaQuery(context.PositionalArgs), context.CancellationToken),
                FormatSpec);

        private static string FormatSpec(GetSchemaResponse res)
        {
            var sb = new StringBuilder();
            foreach (var s in res.Schemas)
            {
                sb.AppendLine(s.CompactSignature.TrimEnd());
                sb.AppendLine();
            }

            if (res.LayoutProperties is { Count: > 0 })
            {
                sb.AppendLine("// layouts (LayoutBoundsCatalog)");
                foreach (var p in res.LayoutProperties)
                {
                    var bounds = p.AllowedValues is { Count: > 0 } ? string.Join('|', p.AllowedValues) : $"{p.MinValue}..{p.MaxValue}";
                    sb.AppendLine($"{p.Name} ({p.ValueType}, def={p.DefaultValue}, bounds={bounds}, targets={string.Join(',', p.ApplicableTargets)})");
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
