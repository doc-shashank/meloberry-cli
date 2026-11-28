using Meloberry.Application.UseCases.Json;
using Meloberry.Cli.Abstractions;

namespace Meloberry.Cli.Commands;

public sealed class ScaffoldCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "apply", "placeholders" };
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "format", "out" };

    public ScaffoldCliCommand()
        : base(new CommandDescriptor(
            Verb: "scaffold",
            Usage: "scaffold <blueprint.json|preset.mbypreset|-> [--format md|json] [-o outDir|file] [--apply] [--placeholders]",
            Description: "Generates lint-clean JSON or Modular Markdown scaffolds from a .mbypreset or PromptDraft blueprint.",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            AllowedValueOptions: AllowedValues,
            Prerequisites: "Input must be a valid .mbypreset v1 or PromptDraft JSON file or stdin ('-').",
            OptionsHelp:
            [
                "--format <md|json>  Output scaffold format (default 'md').",
                "-o, --out <path>    Write generated files (and referenced/placeholder assets) to disk.",
                "--apply             Immediately materialize the scaffold into a SQLite workspace.",
                "--placeholders      Emit valid placeholders for any specs without content."
            ],
            Examples:
            [
                "mby scaffold ./template.mbypreset --format md -o ./draft_md --placeholders --json",
                "mby scaffold - --format json --placeholders --apply --json"
            ]))
    {
    }

    public sealed class Handler(ScaffoldHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {new ScaffoldCliCommand().Descriptor.Usage}", context.GlobalOptions.Json);

            var (filePath, inlineJson, errExit) = await CliCommandHelpers.ReadJsonInputAsync(context, context.PositionalArgs[0]);
            if (errExit is not null) return errExit.Value;

            var cmd = new ScaffoldCommand(
                InputFilePath: filePath,
                InlineJson: inlineJson,
                Format: context.GetOptionValue("format") ?? "md",
                OutputPath: context.GetOptionValue("out"),
                Apply: context.HasFlag("apply"),
                AllowPlaceholders: context.HasFlag("placeholders"));

            return await CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(cmd, context.CancellationToken),
                r => r.Applied is not null
                    ? $"Scaffolded ({r.Format}) and applied {r.Applied.EntityKind} {r.Applied.TargetId}"
                    : $"Scaffolded ({r.Format}) {r.Slug}");
        }
    }
}
