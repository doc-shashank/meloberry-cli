using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;

namespace Meloberry.Application.UseCases.Diagnostics;

public sealed record GetPrimeGuideResponse(
    string Version,
    string Guide,
    IReadOnlyList<string> SpecTopics,
    IReadOnlyList<string> Commands);

public sealed class GetPrimeGuideHandler(ICliToolMetadataProvider metadataProvider)
{
    private static readonly string[] ImplementedSpecTopics =
        ["session", "article", "quiz", "text", "image", "mcq", "flashcard", "layouts"];

    private static readonly string[] V2Commands =
        ["prime", "spec", "apply", "patch", "get", "ls", "rm", "check", "build", "batch", "pkg", "layout", "analyze", "version"];

    public Task<Result<GetPrimeGuideResponse>> HandleAsync(CancellationToken cancellationToken = default)
    {
        var meta = metadataProvider.GetMetadata();
        var topics = string.Join('|', ImplementedSpecTopics);
        var guide = $$"""
            {{meta.ExecutableName}} v{{meta.Version}} (stateless) - Deterministic Study WebApp Compiler (agent: content/order; mby: UI/theme/layout)
            Flags: --json (minified) | --app-data <dir> | -o <dir> | Env: MBY_AGENT=1, MBY_APP_DATA
            Stdin: '-' for JSON file | Addr: <wsId>[#c<n>[.b<m>|.q<m>]|#q<m>|#<ref>] | <entityId>

            Commands:
              prime                                      This card
              spec [type...]                             Micro-schema: {{topics}}
              apply [addr] <file|-> [-o dir] [--build dir] [--export plat:path] [--dry-run]
              patch <addr> [file|-] [--set k=v] [--move idx:shift|exchange]
              get <addr> [--view outline|full|ids]       Default: outline
              ls [dir]                                   List workspaces
              rm <addr>                                  Delete entity
              check <addr>                               Run mandatory lint
              build <addr|spec|-> [-o dir] [--preview] [--no-wait]
              batch <file|->                             Atomic JSON ops array
              pkg export <wsId> <desktop|android> [out] [--include-layout] | pkg import <file> [dir]
              layout get [type] | layout set <type> <prop> <val> | layout edit [target] [--no-wait]
              analyze <webDir> [--all] [--live] [--no-wait]
              version                                    CLI version/runtime

            Root JSON: Session({kind:"session",name,description?,components:[Article|Quiz]}) or Quiz({kind:"quiz",quizType,name,questions:[...]})
            Strict JSON: unknown keys fail (exit 2). Assets: relative to JSON/CWD, magic-byte checked.
            Exit: 0=ok 1=err 2=validation/lint 3=notFound 4=conflict
            """;

        return Task.FromResult(Result<GetPrimeGuideResponse>.Success(
            new GetPrimeGuideResponse(meta.Version, guide, ImplementedSpecTopics, V2Commands)));
    }
}
