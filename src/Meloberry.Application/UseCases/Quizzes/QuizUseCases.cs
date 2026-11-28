using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;

namespace Meloberry.Application.UseCases.Quizzes;

public sealed record ListWorkspacesQuery(string? DirectoryFilter = null, bool IncludeAll = false);

public sealed class ListWorkspacesHandler(
    IWorkspaceRepository repository,
    IAnalysisReportStore? reportStore = null)
{
    public async Task<Result<IReadOnlyList<WorkspaceListItem>>> HandleAsync(ListWorkspacesQuery query, CancellationToken cancellationToken = default) =>
        Result<IReadOnlyList<WorkspaceListItem>>.Success(await repository.ListWorkspacesAsync(query.DirectoryFilter, query.IncludeAll, cancellationToken));

    public async Task<Result<IReadOnlyList<DiscoveredWebAppDto>>> DiscoverWebAppsAsync(
        string? directoryFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (reportStore is null)
        {
            return new Error("WebDiscovery.Unavailable", "WebApp discovery store is not configured.", ErrorType.Validation);
        }

        var dir = string.IsNullOrWhiteSpace(directoryFilter)
            ? Directory.GetCurrentDirectory()
            : directoryFilter;

        return await reportStore.DiscoverWebAppsAsync(dir, cancellationToken);
    }
}
