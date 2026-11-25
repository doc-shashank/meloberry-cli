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
        ["session", "article", "quiz", "text", "image", "mcq", "flashcard", "layouts", "callout", "markdown", "analyze", "pkg", "build", "batch", "patch", "table", "fill-blank", "assertion-reason", "spot-error", "diff", "mechanism", "relabel", "diagram-explore", "reveal", "case", "classify", "match", "numeric"];

    private static readonly string[] V2Commands =
        ["prime", "spec", "apply", "patch", "get", "ls", "rm", "check", "build", "batch", "pkg", "layout", "analyze", "version"];

    public Task<Result<GetPrimeGuideResponse>> HandleAsync(CancellationToken cancellationToken = default)
    {
        var meta = metadataProvider.GetMetadata();
        var topics = string.Join('|', ImplementedSpecTopics);
        var guide = $$"""
            {{meta.ExecutableName}} v{{meta.Version}} - Study WebApp Compiler (agent:content; mby:layout/render)
            Flags:--json|--app-data <dir>|--quiet|--verbose | Env:MBY_AGENT=1, MBY_APP_DATA
            Stdin:'-' | Addr:<id> | <wsId>#<ref|c<n>[.b<m>|.q<m>]> | <setId>#q<m> | Help:mby <cmd> -h
            Cmds:
             prime | This card
             spec [t|@grp..] {{topics}}
             apply [ws|comp] <src|-> [-o dir] [--build dir] [--verify] [--static-only] [--timeout n] [--export plat[:path]] [--dry-run]
             patch <addr> [f|-] [--set k=v] [--move idx:shift|exchange]
             get <addr> [--view outline|full|ids] (def:outline)
             ls [dir] [--all] | List (.meloberry/workspaces; --all)
             rm <addr> | Delete entity
             check <addr> | Mandatory lint
             build <target|-> [-o dir] [--verify] [--static-only] [--ephemeral] [--timeout n] [--preview] [--no-wait]
             batch <file|-> | Atomic JSON ops
             pkg export <id> <desktop|android> [out] [--include-layout] | import <file> [dir]
             layout get [t] | set <t> <p> <v> | reset [t] [k...] | edit [target] [--no-wait]
             analyze <dir|file> [--headless] [--static-only] [--timeout n] [--all] [--live] [--no-wait]
             version | Version/runtime
            <src>=file|dir; <target>=addr|spec|dir. apply emits data.lint (check/build/pkg exit 2). DB:.meloberry/workspaces.
            Exit:0=ok 1=err 2=lint/val 3=notFound 4=conflict
            """;

        return Task.FromResult(Result<GetPrimeGuideResponse>.Success(
            new GetPrimeGuideResponse(meta.Version, guide, ImplementedSpecTopics, V2Commands)));
    }
}
