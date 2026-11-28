using System.Text;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Cli.Abstractions;

namespace Meloberry.Cli.Commands;

public sealed class PrimeCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "topics", "container", "format", "cli-prefix", "authoring-format", "workspace", "out"
    };

    public PrimeCliCommand()
        : base(new CommandDescriptor(
            Verb: "prime",
            Usage: "prime [--topics t1,t2|@grp] [--container session|article|quiz] [--format card|agents-md] [--cli-prefix cmd] [--authoring-format fmt] [--workspace dir] [-o file]",
            Description: "Prints the ultra-dense AI-agent grammar card or generates a targeted AGENTS.md playbook.",
            HandlerType: typeof(Handler),
            AllowedValueOptions: AllowedValues,
            Prerequisites: "Stateless; requires no existing workspace. Also invoked when running 'mby', 'mby -h', or 'mby --help'.",
            OptionsHelp:
            [
                "--topics <list|@grp>      Comma-separated topics or @group selector for --format agents-md (default: @content).",
                "--container <kind>        Target container: 'session' (default), 'article', or 'quiz'.",
                "--format <mode>           Output format: 'card' (default) or 'agents-md'.",
                "--cli-prefix <cmd>        CLI command prefix embedded in AGENTS.md (default: 'mby').",
                "--authoring-format <fmt>  'markdownDirectory' (default), 'singleMarkdown', or 'json'.",
                "--workspace <dir>         Optional workspace path recorded in generated AGENTS.md.",
                "-o, --out <file>          Optional output file path for generated AGENTS.md."
            ],
            Examples:
            [
                "mby prime",
                "mby prime --json",
                "mby prime --topics mcq,flashcard,link --container article --format agents-md -o ./AGENTS.md --json"
            ]))
    {
    }

    public sealed class Handler(GetPrimeGuideHandler useCase) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            var topics = context.GetOptionValues("topics");
            var query = new GetPrimeGuideQuery(
                Topics: topics.Count > 0 ? topics : null,
                Container: context.GetOptionValue("container"),
                Format: context.GetOptionValue("format") ?? "card",
                CliPrefix: context.GetOptionValue("cli-prefix") ?? "mby",
                AuthoringFormat: context.GetOptionValue("authoring-format") ?? "markdownDirectory",
                WorkspacePath: context.GetOptionValue("workspace"),
                OutputFilePath: context.GetOptionValue("out"));

            return CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(query, context.CancellationToken),
                r => r.Guide);
        }
    }
}

public sealed class SpecCliCommand : CliCommand
{
    public SpecCliCommand()
        : base(new CommandDescriptor(
            Verb: "spec",
            Usage: "spec [type|@group...] [--icons] [--nested] [--templates]",
            Description: "Prints compact schemas for implemented specs (Session & Quiz Content Specs vs. Other Specs) and layout bounds.",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "icons", "nested", "templates" },
            Prerequisites: $"Stateless; pass topic names ({string.Join(", ", GetSchemaHandler.SupportedTopics)}) or group selectors ({string.Join(", ", GetSchemaHandler.SupportedGroups)}), or omit to print all.",
            OptionsHelp:
            [
                "--icons      Include embedded SVG spec/subtype icons in JSON output.",
                "--nested     Include machine-readable schemas for nested item types (e.g., mcqOption, relabelBox, linkCard).",
                "--templates  Include markdownHint and markdownStarter authoring templates on content topics."
            ],
            Examples:
            [
                "mby spec @questions --json",
                "mby spec --icons --templates --json",
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
                    new GetSchemaQuery(
                        context.PositionalArgs,
                        context.HasFlag("icons"),
                        context.HasFlag("nested"),
                        context.HasFlag("templates")),
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
