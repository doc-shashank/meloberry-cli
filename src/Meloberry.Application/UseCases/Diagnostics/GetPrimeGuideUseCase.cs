using System.Text;
using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Diagnostics;

namespace Meloberry.Application.UseCases.Diagnostics;

public sealed record GetPrimeGuideQuery(
    IReadOnlyList<string>? Topics = null,
    string? Container = null,
    string Format = "card",
    string CliPrefix = "mby",
    string AuthoringFormat = "markdownDirectory",
    string? WorkspacePath = null,
    string? OutputFilePath = null);

public sealed record GetPrimeGuideResponse(
    string Version,
    string Guide,
    IReadOnlyList<string> SpecTopics,
    IReadOnlyList<string> Commands,
    int ApproximateTokens = 0,
    int CharacterCount = 0,
    IReadOnlyList<string>? IncludedTopics = null,
    string? WrittenPath = null);

public sealed class GetPrimeGuideHandler(
    ICliToolMetadataProvider metadataProvider,
    GetSchemaHandler? schemaHandler = null)
{
    private static readonly JsonSerializerOptions ExampleJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] ImplementedSpecTopics =
        ["session", "article", "quiz", "text", "image", "mcq", "flashcard", "layouts", "callout", "link", "markdown", "analyze", "pkg", "build", "batch", "patch", "table", "fill-blank", "assertion-reason", "spot-error", "diff", "mechanism", "relabel", "diagram-explore", "reveal", "case", "classify", "match", "numeric"];

    private static readonly string[] V2Commands =
        ["prime", "spec", "apply", "patch", "clone", "get", "ls", "rm", "check", "asset", "scaffold", "build", "batch", "pkg", "layout", "analyze", "version"];

    public async Task<Result<GetPrimeGuideResponse>> HandleAsync(CancellationToken cancellationToken = default) =>
        await HandleAsync(new GetPrimeGuideQuery(), cancellationToken);

    public async Task<Result<GetPrimeGuideResponse>> HandleAsync(
        GetPrimeGuideQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var meta = metadataProvider.GetMetadata();
        var normalizedFormat = (query.Format ?? "card").Trim().ToLowerInvariant();

        if (normalizedFormat is "" or "card")
        {
            var topics = string.Join('|', ImplementedSpecTopics);
            var guide = $$"""
                {{meta.ExecutableName}} v{{meta.Version}} - Study WebApp Compiler (agent:content; mby:layout/render)
                Flags:--json|--app-data <d>|--quiet|--verbose | Env:MBY_AGENT=1, MBY_APP_DATA
                Stdin:'-' | Addr:<id>|<ws>#<ref|c<n>[.b<m>|.q<m>]>|<setId>#q<m> | Help:mby <cmd> -h
                Cmds:
                 prime [--topics..] [--format agents-md]
                 spec [t|@grp..] {{topics}} [--templates]
                 apply [ws|comp] <src|-> [--at n] [-o d] [--build d] [--verify] [--dry-run]
                 patch <addr> [f|-] [--set k=v] [--to <addr> [--at n]] [--order ..] [--md]
                 clone <addr> [--to <addr>] [--at n] | Duplicate entity
                 get <addr> [--view outline|full|ids|spec|markdown] [-o d]
                 ls [dir] [--all|--web] | rm <addr> | check <addr>
                 asset add <ws> <f> [--dir d] | ls <ws> | prune <ws>
                 scaffold <blueprint|-> [-o d] [--apply] [--placeholders]
                 build <target|-> [-o d] [--verify] [--static-only] [--all-themes] [--signal] [--no-wait]
                 batch <file|-> | Atomic JSON ops
                 pkg export <id> <desktop|android> [out] [--include-layout] | import <f> [d]
                 layout get [t]|set <t> <p> <v>|reset [t] [k..]|edit [target] [--no-wait]
                 analyze <dir|f> [--headless] [--static-only] [--ingest f|-] [--no-wait]
                 version | Version/runtime
                <src>=f|d. apply emits data.lint (check/build/pkg exit 2). DB:.meloberry/workspaces.
                Exit:0=ok 1=err 2=lint/val 3=notFound 4=conflict
                """;

            return Result<GetPrimeGuideResponse>.Success(new GetPrimeGuideResponse(
                meta.Version,
                guide,
                ImplementedSpecTopics,
                V2Commands,
                TokenEstimator.EstimateTokens(guide),
                guide.Length));
        }

        if (normalizedFormat is "agents-md" or "agentsmd" or "agents_md")
        {
            var effectiveSchemaHandler = schemaHandler ?? new GetSchemaHandler();
            var requestedTopics = ExpandTopicTokens(query.Topics);
            var schemaQuery = new GetSchemaQuery(
                requestedTopics.Count > 0 ? requestedTopics : ["@content"],
                IncludeIcons: false,
                IncludeNested: false,
                IncludeTemplates: true);

            var schemaResult = await effectiveSchemaHandler.HandleAsync(schemaQuery, cancellationToken);
            if (!schemaResult.IsSuccess)
            {
                return Result<GetPrimeGuideResponse>.Failure(schemaResult.Error!);
            }

            var selectedSchemasList = schemaResult.Value.Schemas
                .Select(s => new AgentsMdTopicSchema(
                    s.Topic,
                    s.Category,
                    s.DisplayName ?? s.Topic,
                    s.Summary,
                    s.Command,
                    s.CompactSignature,
                    s.Fields.Select(f => new AgentsMdTopicField(
                        f.Name,
                        f.Type,
                        f.Required,
                        f.Description,
                        f.AllowedValues,
                        f.MaxLength,
                        f.Pattern,
                        f.MinValue,
                        f.MaxValue)).ToArray(),
                    s.Example is null ? null : JsonSerializer.Serialize(s.Example, ExampleJsonOptions),
                    s.MarkdownHint,
                    s.MarkdownStarter))
                .ToList();

            if (schemaResult.Value.LayoutProperties is { Count: > 0 } layoutProps)
            {
                var compactLines = layoutProps.Select(p =>
                {
                    var bounds = p.AllowedValues is { Count: > 0 }
                        ? string.Join('|', p.AllowedValues)
                        : $"{p.MinValue}..{p.MaxValue}";
                    return $"{p.Name} ({p.ValueType}, def={p.DefaultValue}, bounds={bounds}, targets={string.Join(',', p.ApplicableTargets)})";
                });

                var layoutFields = layoutProps
                    .Select(p => new AgentsMdTopicField(
                        p.Name,
                        p.ValueType,
                        Required: false,
                        $"{p.Description} (default={p.DefaultValue}, targets={string.Join(',', p.ApplicableTargets)})",
                        p.AllowedValues,
                        MaxLength: null,
                        Pattern: null,
                        p.MinValue,
                        p.MaxValue))
                    .ToArray();

                selectedSchemasList.Add(new AgentsMdTopicSchema(
                    Topic: "layouts",
                    Category: "format",
                    DisplayName: "Layouts",
                    Summary: "Global and entity layout profile bounds (`article`, `mcq`, `flashcard`).",
                    Command: "mby layout get|set|reset|edit",
                    CompactSignature: string.Join('\n', compactLines),
                    Fields: layoutFields,
                    ExampleJson: null,
                    MarkdownHint: null,
                    MarkdownStarter: null));
            }

            var selectedSchemas = selectedSchemasList.ToArray();

            var markdown = AgentsMdRenderer.Render(new AgentsMdRenderInput(
                meta.ExecutableName,
                meta.Version,
                string.IsNullOrWhiteSpace(query.CliPrefix) ? meta.ExecutableName : query.CliPrefix.Trim(),
                query.WorkspacePath,
                query.Container ?? "session",
                query.AuthoringFormat,
                selectedSchemas));

            string? writtenPath = null;
            if (!string.IsNullOrWhiteSpace(query.OutputFilePath))
            {
                writtenPath = Path.GetFullPath(query.OutputFilePath);
                var parentDir = Path.GetDirectoryName(writtenPath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                await File.WriteAllTextAsync(writtenPath, markdown, new UTF8Encoding(false), cancellationToken);
            }

            var includedTopics = selectedSchemas.Select(s => s.Topic).ToArray();
            return Result<GetPrimeGuideResponse>.Success(new GetPrimeGuideResponse(
                meta.Version,
                markdown,
                ImplementedSpecTopics,
                V2Commands,
                TokenEstimator.EstimateTokens(markdown),
                markdown.Length,
                includedTopics,
                writtenPath));
        }

        return Result<GetPrimeGuideResponse>.Failure(
            new Error("Prime.InvalidFormat", $"Invalid --format '{query.Format}'. Use 'card' or 'agents-md'.", ErrorType.Validation));
    }

    private static List<string> ExpandTopicTokens(IReadOnlyList<string>? rawTopics)
    {
        var result = new List<string>();
        if (rawTopics is null || rawTopics.Count == 0)
        {
            return result;
        }

        foreach (var item in rawTopics)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            foreach (var part in item.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    result.Add(part);
                }
            }
        }

        return result;
    }
}
