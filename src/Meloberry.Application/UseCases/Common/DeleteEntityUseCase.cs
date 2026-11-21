using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;

namespace Meloberry.Application.UseCases.Common;

public sealed record DeleteEntityCommand(string Address);
public sealed record DeleteEntityResponse(string DeletedId, string EntityKind, string WorkspaceId, int? RemainingSiblings = null);

public sealed class DeleteEntityHandler(EntityAddressResolver addressResolver, IWorkspaceRepository repository)
{
    public async Task<Result<DeleteEntityResponse>> HandleAsync(DeleteEntityCommand command, CancellationToken cancellationToken = default)
    {
        var resolved = await addressResolver.ResolveAsync(command.Address, cancellationToken);
        if (!resolved.IsSuccess) return resolved.Error!;

        var target = resolved.Value;
        switch (target.EntityKind)
        {
            case "session":
                await repository.DeleteSessionAsync(target.EntityId, cancellationToken);
                return new DeleteEntityResponse(target.EntityId, "session", target.WorkspaceId);

            case "quiz":
                await repository.DeleteQuizAsync(target.EntityId, cancellationToken);
                return new DeleteEntityResponse(target.EntityId, "quiz", target.WorkspaceId);

            case "component":
            {
                var res = target.Session!.DeleteComponent(target.EntityId);
                if (!res.IsSuccess) return res.Error!;
                await repository.SaveSessionAsync(target.Session, cancellationToken);
                return new DeleteEntityResponse(target.EntityId, "component", target.WorkspaceId, target.Session.Components.Count);
            }

            case "articleBlock":
            {
                var res = target.Component!.DeleteBlock(target.EntityId);
                if (!res.IsSuccess) return res.Error!;
                await repository.SaveSessionAsync(target.Session!, cancellationToken);
                return new DeleteEntityResponse(target.EntityId, "articleBlock", target.WorkspaceId, target.Component.ArticleBlocks.Count);
            }

            case "question":
            {
                var res = target.Quiz!.DeleteQuestion(target.EntityId);
                if (!res.IsSuccess) return res.Error!;
                if (target.Session is not null)
                    await repository.SaveSessionAsync(target.Session, cancellationToken);
                else
                    await repository.SaveQuizAsync(target.Quiz, cancellationToken);
                return new DeleteEntityResponse(target.EntityId, "question", target.WorkspaceId, target.Quiz.Questions.Count);
            }

            default:
                return new Error("Delete.UnsupportedTarget", $"Cannot delete entity kind '{target.EntityKind}'.", ErrorType.Validation);
        }
    }
}
