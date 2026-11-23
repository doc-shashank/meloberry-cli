using System.Text;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Quizzes;
using Meloberry.Cli.Abstractions;
using Meloberry.Domain.Common;

namespace Meloberry.Cli.Commands;

public sealed class GetCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase) { "view" };

    public GetCliCommand()
        : base(new CommandDescriptor(
            Verb: "get",
            Usage: "get <addr> [--view outline|full|ids]",
            Description: "Inspects an entity by ID, path (#c1.b2), or #ref (defaults to compact outline).",
            HandlerType: typeof(Handler),
            AllowedValueOptions: AllowedValues,
            Prerequisites: "Target entity must exist (direct ID, <wsId>#c<n>[.b<m>|.q<m>], <setId>#q<m>, or <wsId>#<ref>).",
            OptionsHelp:
            [
                "--view <mode>   Output verbosity: 'outline' (default, compact snippets), 'full' (complete content), or 'ids' (minimal ID/path map)."
            ],
            Examples:
            [
                "mby get Session_2026... --json",
                "mby get Session_2026...#c1 --view full --json",
                "mby get Session_2026... --view ids --json"
            ]))
    {
    }

    public sealed class Handler(InspectEntityHandler useCase) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: get <addr> [--view outline|full|ids]", context.GlobalOptions.Json));

            var rawView = context.GetOptionValue("view") ?? "outline";
            InspectViewMode view;
            switch (rawView.Trim().ToLowerInvariant())
            {
                case "outline": view = InspectViewMode.Outline; break;
                case "full": view = InspectViewMode.Full; break;
                case "ids": view = InspectViewMode.Ids; break;
                default:
                    return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Invalid --view '{rawView}'. Use 'outline', 'full', or 'ids'.", context.GlobalOptions.Json));
            }

            return CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(new InspectEntityQuery(context.PositionalArgs[0], view), context.CancellationToken),
                FormatHuman);
        }

        private static string FormatHuman(InspectEntityResponse r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{r.EntityKind}] {r.Id} ({r.View})");
            if (!string.IsNullOrWhiteSpace(r.Name)) sb.AppendLine($"  Name: {r.Name}");
            if (r.Components is not null)
            {
                foreach (var c in r.Components)
                    AppendComponent(sb, c, "  ");
            }
            else if (r.Component is not null)
            {
                AppendComponent(sb, r.Component, "  ");
            }
            else if (r.ArticleBlock is not null)
            {
                AppendBlock(sb, r.ArticleBlock, "  ");
            }
            else if (r.Questions is not null)
            {
                foreach (var q in r.Questions)
                    AppendQuestion(sb, q, "  ");
            }
            else if (r.Question is not null)
            {
                AppendQuestion(sb, r.Question, "  ");
            }
            return sb.ToString().TrimEnd();
        }

        private static void AppendComponent(StringBuilder sb, InspectComponentDto c, string indent)
        {
            var refPart = c.Ref is null ? string.Empty : $" #{c.Ref}";
            var titlePart = string.IsNullOrWhiteSpace(c.Title) ? string.Empty : $" \"{c.Title}\"";
            sb.AppendLine($"{indent}#{c.Path} {c.Id} [{c.Kind}{(c.QuizType is null ? "" : ":" + c.QuizType)}]{refPart}{titlePart}");
            if (c.ArticleBlocks is not null)
                foreach (var b in c.ArticleBlocks) AppendBlock(sb, b, indent + "  ");
            if (c.Questions is not null)
                foreach (var q in c.Questions) AppendQuestion(sb, q, indent + "  ");
        }

        private static void AppendBlock(StringBuilder sb, InspectArticleBlockDto b, string indent)
        {
            var refPart = b.Ref is null ? string.Empty : $" #{b.Ref}";
            var preview = b.Snippet ?? b.TextContent ?? b.ImagePath ?? string.Empty;
            sb.AppendLine($"{indent}#{b.Path} {b.Id} [{b.Kind}]{refPart} {preview}".TrimEnd());
        }

        private static void AppendQuestion(StringBuilder sb, InspectQuestionDto q, string indent)
        {
            var refPart = q.Ref is null ? string.Empty : $" #{q.Ref}";
            var preview = q.Snippet ?? q.Text ?? q.Front?.Text ?? string.Empty;
            sb.AppendLine($"{indent}#{q.Path} {q.Id} [{q.Type}]{refPart} {preview}".TrimEnd());
        }
    }
}

public sealed class LsCliCommand : CliCommand
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase) { "all" };

    public LsCliCommand()
        : base(new CommandDescriptor(
            Verb: "ls",
            Usage: "ls [directory] [--all]",
            Description: "Lists discovered study session and standalone quiz workspaces in CWD + .meloberry/workspaces (or globally with --all).",
            HandlerType: typeof(Handler),
            AllowedFlagOptions: AllowedFlags,
            Prerequisites: "Scans [directory] (or CWD + .meloberry/workspaces when omitted); pass --all to query all globally registered workspaces in app_settings.db.",
            OptionsHelp:
            [
                "--all   List all workspaces registered globally in app_settings.db across all directories."
            ],
            Examples:
            [
                "mby ls --json",
                "mby ls ./custom_dir --json",
                "mby ls --all --json"
            ]))
    {
    }

    public sealed class Handler(ListWorkspacesHandler useCase) : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count > 1)
                return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: ls [directory] [--all]", context.GlobalOptions.Json));

            var dir = context.PositionalArgs.Count == 1 ? context.PositionalArgs[0] : null;
            bool includeAll = context.HasFlag("all");
            return CliCommandHelpers.FinishAsync(
                context,
                useCase.HandleAsync(new ListWorkspacesQuery(dir, includeAll), context.CancellationToken),
                items => items.Count == 0
                    ? "No workspaces found."
                    : string.Join(Environment.NewLine, items.Select(x => $"{x.Id} | {x.Kind} ({x.SubType}) | items:{x.ItemCount} | {x.StorageDirectory}")));
        }
    }
}

public sealed class CheckCliCommand : CliCommand
{
    public CheckCliCommand()
        : base(new CommandDescriptor(
            Verb: "check",
            Usage: "check <addr> [--quiet] [--verbose]",
            Description: "Runs mandatory lint validation on a workspace or sub-entity.",
            HandlerType: typeof(Handler),
            Prerequisites: "Target workspace or sub-entity must exist. Exits 2 (Lint.Failed) if any Error-severity lint issues are found.",
            OptionsHelp:
            [
                "--quiet    Omit empty issues array when lint is clean.",
                "--verbose  Include full issue details."
            ],
            Examples: ["mby check Session_2026... --quiet --json"]))
    {
    }

    public sealed class Handler(LintHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: check <addr>", context.GlobalOptions.Json);

            var result = await useCase.HandleAsync(
                new LintCommand(context.PositionalArgs[0], context.GlobalOptions.Quiet, context.GlobalOptions.Verbose),
                context.CancellationToken);
            if (!result.IsSuccess) return result.ToExitCode(context.Console, context.GlobalOptions.Json);

            var report = result.Value;
            var lines = new List<string>
            {
                $"Check {report.TargetId}: {report.ErrorCount} error(s), {report.WarningCount} warning(s)."
            };
            lines.AddRange(report.Issues.Select(i => $"  [{i.Severity}] {i.Code} ({i.TargetId}): {i.Message}"));

            if (report.ErrorCount > 0)
            {
                if (context.GlobalOptions.Json)
                {
                    return Result.Failure(new Error(
                        "Lint.Failed",
                        $"Check {report.TargetId}: {report.ErrorCount} error(s), {report.WarningCount} warning(s).",
                        ErrorType.Validation,
                        Data: report)).ToExitCode(context.Console, json: true);
                }
                foreach (var line in lines) context.Console.Error.WriteLine(line);
                return ExitCodes.ValidationError;
            }

            CliCommandHelpers.WriteOutput(context, report, string.Join(Environment.NewLine, lines));
            return ExitCodes.Success;
        }
    }
}
