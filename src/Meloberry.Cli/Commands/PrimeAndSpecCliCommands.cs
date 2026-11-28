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
            HandlerType: typeof(Handler),
            Prerequisites: "Stateless; requires no existing workspace. Also invoked when running 'mby', 'mby -h', or 'mby --help'.",
            OptionsHelp:
            [
                "(No command-specific options; supports global --json, --app-data.)"
            ],
            Examples:
            [
                "mby prime",
                "mby prime --json"
            ]))
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
            Usage: "spec [type|@group...] [--icons] [--nested]",
            Description: "Prints compact schemas for implemented specs (Session & Quiz Content Specs vs. Other Specs) and layout bounds.",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "icons", "nested" },
            Prerequisites: $"Stateless; pass topic names ({string.Join(", ", GetSchemaHandler.SupportedTopics)}) or group selectors ({string.Join(", ", GetSchemaHandler.SupportedGroups)}), or omit to print all.",
            OptionsHelp:
            [
                "--icons    Include embedded SVG spec/subtype icons in JSON output.",
                "--nested   Include machine-readable schemas for nested item types (e.g., mcqOption, relabelBox, linkCard)."
            ],
            Examples:
            [
                "mby spec @questions --json",
                "mby spec --icons --json",
                "mby spec relabel --nested --json",
                "mby spec @content @other --json",
                "mby spec mcq flashcard layouts --json"
            ]))
    {
    }

    public sealed class Handler(GetSchemaHandler useCase) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context) =>
            CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(
                    new GetSchemaQuery(context.PositionalArgs, context.HasFlag("icons"), context.HasFlag("nested")),
                    context.CancellationToken),
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

            if (res.NestedTypes is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("// nestedTypes");
                foreach (var (typeName, fields) in res.NestedTypes)
                {
                    var sig = string.Join(", ", fields.Select(f => $"{f.Name}{(f.Required ? "" : "?")}:{f.Type}"));
                    sb.AppendLine($"{typeName}: {{ {sig} }}");
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
