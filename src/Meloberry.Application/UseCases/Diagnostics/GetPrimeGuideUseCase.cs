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
        ["session", "article", "quiz", "text", "image", "mcq", "flashcard", "layouts", "callout", "markdown", "analyze", "pkg", "build"];

    private static readonly string[] V2Commands =
        ["prime", "spec", "apply", "patch", "get", "ls", "rm", "check", "build", "batch", "pkg", "layout", "analyze", "version"];

    public Task<Result<GetPrimeGuideResponse>> HandleAsync(CancellationToken cancellationToken = default)
    {
        var meta = metadataProvider.GetMetadata();
        var topics = string.Join('|', ImplementedSpecTopics);
        var guide = $$"""
            {{meta.ExecutableName}} v{{meta.Version}} (stateless) - Study WebApp Compiler (agent: content/order; mby: UI/theme/layout)
            Flags: --json | --app-data <dir> | -o <dir> | --quiet | --verbose | Env: MBY_AGENT=1, MBY_APP_DATA
            Stdin: '-' | Addr: <wsId>[#c<n>[.b<m>|.q<m>]|#q<m>|#<ref>] | <id> | Subcmd help: mby <cmd> --help

            Commands:
              prime                                      This card
              spec [type...]                             Specs: {{topics}}
              apply [addr] <file|dir|-> [-o dir] [--build dir] [--export plat:path] [--dry-run]
              patch <addr> [file|-] [--set k=v] [--move idx:shift|exchange]
              get <addr> [--view outline|full|ids]       Default: outline
              ls [dir]                                   List workspaces (CWD + .meloberry/workspaces)
              rm <addr>                                  Delete entity
              check <addr>                               Run mandatory lint
              build <addr|spec|dir|-> [-o dir] [--preview] [--verify] [--no-wait]
              batch <file|->                             Atomic JSON ops array
              pkg export <wsId> <desktop|android> [out] [--include-layout] | pkg import <file> [dir]
              layout get [type] | layout set <type> <prop> <val> | layout edit [target] [--no-wait]
              analyze <webDir> [--headless] [--static-only] [--all] [--live] [--no-wait]
              version                                    CLI version/runtime

            Inputs: JSON, Markdown (.md + frontmatter), or directory of .md/.json. Default DB dir: .meloberry/workspaces.
            Exit: 0=ok 1=err 2=validation/lint 3=notFound 4=conflict
            """;

        return Task.FromResult(Result<GetPrimeGuideResponse>.Success(
            new GetPrimeGuideResponse(meta.Version, guide, ImplementedSpecTopics, V2Commands)));
    }
}
