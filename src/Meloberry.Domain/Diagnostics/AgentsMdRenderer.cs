using System.Text;
using Meloberry.Domain.Common;

namespace Meloberry.Domain.Diagnostics;

public sealed record AgentsMdTopicField(
    string Name,
    string Type,
    bool Required,
    string Description,
    IReadOnlyList<string>? AllowedValues = null,
    int? MaxLength = null,
    string? Pattern = null,
    int? MinValue = null,
    int? MaxValue = null);

public sealed record AgentsMdTopicSchema(
    string Topic,
    string Category,
    string DisplayName,
    string Summary,
    string Command,
    string CompactSignature,
    IReadOnlyList<AgentsMdTopicField> Fields,
    string? ExampleJson = null,
    string? MarkdownHint = null,
    string? MarkdownStarter = null);

public sealed record AgentsMdRenderInput(
    string ExecutableName,
    string Version,
    string CliCommandPrefix,
    string? WorkspacePath,
    string Container,
    string AuthoringFormat,
    IReadOnlyList<AgentsMdTopicSchema> SelectedSchemas,
    IReadOnlyList<string>? ExtraSections = null);

public static class AgentsMdRenderer
{
    public static string Render(AgentsMdRenderInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cmd = string.IsNullOrWhiteSpace(input.CliCommandPrefix)
            ? input.ExecutableName
            : input.CliCommandPrefix.Trim();

        var sb = new StringBuilder();
        sb.AppendLine("# AGENTS.md — Meloberry Workspace Contract");
        sb.AppendLine();
        sb.AppendLine($"Generated natively by **{input.ExecutableName} v{input.Version}**. Use the exact command prefix and live topic schemas below directly without wasting turns on exploratory `prime` or `spec` calls.");
        sb.AppendLine();

        AppendToolsetSection(sb, input, cmd);
        AppendContractSection(sb, cmd);
        AppendWorkflowSection(sb, input, cmd);
        AppendSchemasSection(sb, input.SelectedSchemas, cmd);

        if (input.ExtraSections is { Count: > 0 })
        {
            foreach (var extra in input.ExtraSections.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                sb.AppendLine(extra.TrimEnd());
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendToolsetSection(StringBuilder sb, AgentsMdRenderInput input, string cmd)
    {
        sb.AppendLine("## 1. Workspace & Toolset");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(input.WorkspacePath))
        {
            sb.AppendLine($"- **Workspace Root**: `{input.WorkspacePath}`");
        }

        sb.AppendLine($"- **CLI Command**: `{cmd}`");
        sb.AppendLine($"- **Included Topics**: {(input.SelectedSchemas.Count > 0 ? string.Join(", ", input.SelectedSchemas.Select(s => $"`{s.Topic}`")) : "_none_")}");
        sb.AppendLine();
    }

    private static void AppendContractSection(StringBuilder sb, string cmd)
    {
        sb.AppendLine("## 2. Execution Contract & Lint Invariants");
        sb.AppendLine();
        sb.AppendLine("- **Always enable JSON output**: set `MBY_AGENT=1` or pass `--json` (`{\"success\":true,\"data\":...}`).");
        sb.AppendLine("- **Exit Codes**: `0` Ok, `1` IO/Error, `2` Lint/Validation, `3` NotFound, `4` Conflict, `5` Unauthorized.");
        sb.AppendLine("- **Pipe via `stdin` (`-`)** for `apply`, `patch`, `build`, `batch`, and `analyze --ingest -`.");
        sb.AppendLine("- **Addressing**: `<wsId>`, `<wsId>#c<n>`, `#c<n>.b<m>`, `#c<n>.q<m>`, `<setId>#q<m>`, `#<ref>` (`^[a-z0-9][a-z0-9_-]{0,31}$`).");
        sb.AppendLine("- **Strict Schemas**: camelCase keys only (`disallowUnmappedProperties`); unknown keys fail with exit `2`. Every `imagePath` must be workspace-relative and pass magic-byte verification (`.png/.jpg/.jpeg/.gif/.webp/.bmp/.svg`).");
        sb.AppendLine($"- **Media & Reorganization**: stage images with `{cmd} asset add <wsId|dir> <file> --json`, reorder/move with `{cmd} patch <addr> --to <target> [--at <idx>] --order <ids>`, duplicate with `{cmd} clone <addr> --json`.");
        sb.AppendLine("- **Lint Invariants**: sessions need >=1 component; articles >=1 non-empty block; `table` column widths & 1-based `hideableCols` must align; MCQs need 2..26 options & >=1 `isCorrect`; flashcards need non-empty front/back text or image; `spot-error` needs >=2 `[span]` tokens.");
        sb.AppendLine("- **Never block on servers**: pass `--no-wait` on `build --preview` or `layout edit`.");
        sb.AppendLine();
    }

    private static void AppendWorkflowSection(StringBuilder sb, AgentsMdRenderInput input, string cmd)
    {
        sb.AppendLine("## 3. Target Container & Build Flow");
        sb.AppendLine();
        sb.AppendLine($"- **Target Container**: {FormatContainerLabel(input.Container)}");
        sb.AppendLine($"- **Authoring Format**: {FormatAuthoringLabel(input.AuthoringFormat)}");

        var sourceTarget = NormalizeAuthoringFormat(input.AuthoringFormat) switch
        {
            "singleMarkdown" => "./study.md",
            "json" => "./spec.json",
            _ => "./study_src/"
        };

        sb.AppendLine($"- **One-Shot Compile & Verify**: `{cmd} build {sourceTarget} -o ./dist_web --verify --all-themes --signal --json`");
        sb.AppendLine($"- **Persistent Workspace & Package Flow**: `{cmd} apply {sourceTarget} --json` -> `{cmd} check <wsId> --json` -> `{cmd} build <wsId> -o ./dist_web --all-themes --signal --json` -> `{cmd} pkg export <wsId> android ./out.mbypkg --json`");
        sb.AppendLine($"- **Headless QA**: `{cmd} analyze ./dist_web --headless --json`");
        sb.AppendLine();
    }

    private static void AppendSchemasSection(
        StringBuilder sb,
        IReadOnlyList<AgentsMdTopicSchema> schemas,
        string cmd)
    {
        sb.AppendLine("## 4. Targeted Live Schemas");
        sb.AppendLine();

        if (schemas.Count == 0)
        {
            sb.AppendLine($"_No schemas selected. Run `{cmd} spec <topic> --templates --json` to inspect available topics._");
            sb.AppendLine();
            return;
        }

        foreach (var spec in schemas)
        {
            sb.AppendLine($"### `{spec.Topic}` ({spec.Category})");
            if (!string.IsNullOrWhiteSpace(spec.Summary))
            {
                sb.AppendLine($"{spec.Summary}  ");
            }

            if (!string.IsNullOrWhiteSpace(spec.Command))
            {
                sb.AppendLine($"Command: `{spec.Command}`");
            }

            if (!string.IsNullOrWhiteSpace(spec.MarkdownHint))
            {
                sb.AppendLine($"Markdown hint: {spec.MarkdownHint}");
            }

            if (!string.IsNullOrWhiteSpace(spec.CompactSignature))
            {
                var sigFence = TokenEstimator.MakeCodeFence(spec.CompactSignature);
                sb.AppendLine($"{sigFence}text");
                sb.AppendLine(spec.CompactSignature.Trim());
                sb.AppendLine(sigFence);
            }

            if (!string.IsNullOrWhiteSpace(spec.MarkdownStarter))
            {
                var mdFence = TokenEstimator.MakeCodeFence(spec.MarkdownStarter);
                sb.AppendLine($"{mdFence}markdown");
                sb.AppendLine(spec.MarkdownStarter.Trim());
                sb.AppendLine(mdFence);
            }

            if (spec.Fields.Count > 0)
            {
                sb.AppendLine("Fields:");
                foreach (var field in spec.Fields)
                {
                    var reqTag = field.Required ? "required" : "optional";
                    var constraints = new List<string>();
                    if (field.AllowedValues is { Count: > 0 })
                    {
                        constraints.Add("values: " + string.Join("|", field.AllowedValues));
                    }

                    if (field.MinValue.HasValue || field.MaxValue.HasValue)
                    {
                        constraints.Add($"range: {field.MinValue?.ToString() ?? ""}..{field.MaxValue?.ToString() ?? ""}");
                    }

                    if (field.MaxLength.HasValue)
                    {
                        constraints.Add($"maxLen: {field.MaxLength.Value}");
                    }

                    if (!string.IsNullOrWhiteSpace(field.Pattern))
                    {
                        constraints.Add($"pattern: `{field.Pattern}`");
                    }

                    var constraintText = constraints.Count > 0 ? $" ({string.Join(", ", constraints)})" : "";
                    sb.AppendLine($"- `{field.Name}` (`{field.Type}`, {reqTag}){constraintText}: {field.Description}");
                }
            }

            if (!string.IsNullOrWhiteSpace(spec.ExampleJson))
            {
                var exFence = TokenEstimator.MakeCodeFence(spec.ExampleJson);
                sb.AppendLine("<details><summary>Example JSON</summary>");
                sb.AppendLine();
                sb.AppendLine($"{exFence}json");
                sb.AppendLine(spec.ExampleJson.Trim());
                sb.AppendLine(exFence);
                sb.AppendLine("</details>");
            }

            sb.AppendLine();
        }
    }

    private static string FormatContainerLabel(string? container)
    {
        return (container ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "article" or "standalonearticle" => "Standalone Article",
            "quiz" or "standalonequiz" => "Standalone Quiz",
            _ => "Study Session (Articles + Quizzes)"
        };
    }

    private static string NormalizeAuthoringFormat(string? authoringFormat)
    {
        return (authoringFormat ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "singlemarkdown" or "single" => "singleMarkdown",
            "json" => "json",
            _ => "markdownDirectory"
        };
    }

    private static string FormatAuthoringLabel(string? authoringFormat)
    {
        return NormalizeAuthoringFormat(authoringFormat) switch
        {
            "singleMarkdown" => "Single Markdown File (`./study.md`)",
            "json" => "JSON (`./spec.json`)",
            _ => "Modular Markdown Directory (`./study_src/`)"
        };
    }
}
