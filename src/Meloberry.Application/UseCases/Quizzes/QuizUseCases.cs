using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;

namespace Meloberry.Application.UseCases.Quizzes;

public sealed record ListWorkspacesQuery(string? DirectoryFilter = null, bool IncludeAll = false);

public sealed class ListWorkspacesHandler(IWorkspaceRepository repository)
{
    public async Task<Result<IReadOnlyList<WorkspaceListItem>>> HandleAsync(ListWorkspacesQuery query, CancellationToken cancellationToken = default) =>
        Result<IReadOnlyList<WorkspaceListItem>>.Success(await repository.ListWorkspacesAsync(query.DirectoryFilter, query.IncludeAll, cancellationToken));
}
