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
        ["session", "article", "quiz", "text", "image", "mcq", "flashcard", "layouts", "callout", "markdown", "analyze", "pkg", "build", "batch", "patch", "table", "fill-blank", "assertion-reason", "spot-error", "diff", "mechanism", "relabel"];

    private static readonly string[] V2Commands =
        ["prime", "spec", "apply", "patch", "get", "ls", "rm", "check", "build", "batch", "pkg", "layout", "analyze", "version"];

    public Task<Result<GetPrimeGuideResponse>> HandleAsync(CancellationToken cancellationToken = default)
    {
        var meta = metadataProvider.GetMetadata();
        var topics = string.Join('|', ImplementedSpecTopics);
        var guide = $$"""
            {{meta.ExecutableName}} v{{meta.Version}} (stateless) - Study WebApp Compiler (agent: content; mby: UI/theme/layout)
            Flags: --json | --app-data <dir> | --quiet | --verbose | Env: MBY_AGENT=1, MBY_APP_DATA
            Stdin: '-' | Addr: <wsId>[#c<n>[.b<m>|.q<m>]|#q<m>|#<ref>] | <id> | Help: mby <cmd> -h

            Commands:
              prime                This card
              spec [type|@grp...]  {{topics}}
              apply [addr] <src|-> [-o dir] [--build dir] [--verify] [--static-only] [--timeout n] [--export plat[:path]] [--dry-run]
              patch <addr> [f|-]   [--set k=v] [--move idx:shift|exchange]
              get <addr>           [--view outline|full|ids] (def:outline)
              ls [dir] [--all]     List workspaces (CWD + .meloberry/workspaces; --all)
              rm <addr>            Delete entity
              check <addr>         Run mandatory lint
              build <target|->     [-o dir] [--verify] [--static-only] [--ephemeral] [--timeout n] [--preview] [--no-wait]
              batch <file|->       Atomic JSON ops array
              pkg                  export <id> <desktop|android> [out] [--include-layout] | import <file> [dir]
              layout               get [type] | set <type> <prop> <val> | edit [target] [--no-wait]
              analyze <dir|file>   [--headless] [--static-only] [--timeout n] [--all] [--live] [--no-wait]
              version              CLI version/runtime

            Inputs: <src>=file|dir, <target>=addr|spec|dir. DB: .meloberry/workspaces.
            Exit: 0=ok 1=err 2=validation/lint 3=notFound 4=conflict
            """;

        return Task.FromResult(Result<GetPrimeGuideResponse>.Success(
            new GetPrimeGuideResponse(meta.Version, guide, ImplementedSpecTopics, V2Commands)));
    }
}
