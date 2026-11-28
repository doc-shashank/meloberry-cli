using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Json;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;

namespace Meloberry.Application.UseCases.Common;

public sealed record CloneEntityCommand(
    string Address,
    string? TargetContainerAddress = null,
    int? InsertAtIndex = null,
    string? NewRef = null);

public sealed record CloneEntityResponse(
    string NewId,
    string EntityKind,
    string TargetContainerId,
    int NewIndex,
    string? NewRef = null,
    string WorkspaceId = "");

public sealed class CloneEntityHandler(
    EntityAddressResolver addressResolver,
    IWorkspaceRepository repository,
    IIdGenerator idGenerator)
{
    public async Task<Result<CloneEntityResponse>> HandleAsync(CloneEntityCommand command, CancellationToken cancellationToken = default)
    {
        var resolved = await addressResolver.ResolveAsync(command.Address, cancellationToken);
        if (!resolved.IsSuccess) return resolved.Error!;
        var src = resolved.Value;

        if (src.EntityKind is not ("component" or "articleBlock" or "question"))
        {
            return new Error(
                "Clone.UnsupportedTarget",
                $"Cannot clone entity kind '{src.EntityKind}'. Supported targets: component, articleBlock, question.",
                ErrorType.Validation);
        }

        string? requestedRef = null;
        if (!string.IsNullOrWhiteSpace(command.NewRef))
        {
            var refNorm = RefKeyValidator.ValidateAndNormalize(command.NewRef);
            if (!refNorm.IsSuccess) return refNorm.Error!;
            requestedRef = refNorm.Value;
        }

        var usedRefs = src.Session is not null
            ? ApplySpecHandler.CollectSessionRefs(src.Session)
            : src.Quiz!.Questions.Where(q => q.RefKey is not null).Select(q => q.RefKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (requestedRef is not null && !usedRefs.Add(requestedRef))
            return AddressErrors.DuplicateRef(requestedRef);

        if (src.EntityKind == "component")
        {
            if (!string.IsNullOrWhiteSpace(command.TargetContainerAddress))
            {
                var dstRes = await addressResolver.ResolveAsync(command.TargetContainerAddress, cancellationToken);
                if (!dstRes.IsSuccess) return dstRes.Error!;
                if (dstRes.Value.EntityKind != "session" || dstRes.Value.Session?.Id != src.Session!.Id)
                    return new Error("Clone.InvalidTarget", "Cloning a component only supports targeting the same session.", ErrorType.Validation);
            }

            var srcComp = src.Component!;
            string? compRef = requestedRef ?? AllocateCloneRef(srcComp.RefKey, srcComp.Kind == ComponentKind.Article ? "art" : "quiz", usedRefs);
            var newCompId = idGenerator.NewComponentId();

            var clonedComp = srcComp.CloneWithNewId(
                newCompId,
                src.Session!.Id,
                idGenerator.NewArticleBlockId,
                idGenerator.NewQuestionId,
                compRef,
                b => AllocateCloneRef(b.RefKey, "blk", usedRefs),
                q => AllocateCloneRef(q.RefKey, "q", usedRefs));

            int insertIdx = command.InsertAtIndex ?? (srcComp.Index + 1);
            var attachRes = src.Session.AttachComponent(clonedComp, insertIdx);
            if (!attachRes.IsSuccess) return attachRes.Error!;

            await repository.SaveSessionAsync(src.Session, cancellationToken);
            return new CloneEntityResponse(clonedComp.Id, "component", src.Session.Id, attachRes.Value, compRef, src.Session.Id);
        }

        if (src.EntityKind == "articleBlock")
        {
            var targetComp = src.Component!;
            if (!string.IsNullOrWhiteSpace(command.TargetContainerAddress))
            {
                var dstRes = await addressResolver.ResolveAsync(command.TargetContainerAddress, cancellationToken);
                if (!dstRes.IsSuccess) return dstRes.Error!;
                if (dstRes.Value.EntityKind != "component" || dstRes.Value.Component?.Kind != ComponentKind.Article || dstRes.Value.Session?.Id != src.Session!.Id)
                    return new Error("Clone.InvalidTarget", "Target container for cloning an articleBlock must be an article component in the same session.", ErrorType.Validation);
                targetComp = dstRes.Value.Component!;
            }

            int insertIdx = command.InsertAtIndex ?? (targetComp.Id == src.Component!.Id ? src.ArticleBlock!.Index + 1 : targetComp.ArticleBlocks.Count + 1);
            if (insertIdx < 1 || insertIdx > targetComp.ArticleBlocks.Count + 1)
                return IndexErrors.OutOfIndexBounds(insertIdx, 1, targetComp.ArticleBlocks.Count + 1);

            string? blockRef = requestedRef ?? AllocateCloneRef(src.ArticleBlock!.RefKey, "blk", usedRefs);
            var clonedBlock = src.ArticleBlock!.CloneWithNewId(idGenerator.NewArticleBlockId(), targetComp.Id, blockRef);

            // Insert via temporary attach + transfer or direct index insert on same component
            var addRes = src.Session!.FindComponent(targetComp.Id)!;
            var attach = AttachClonedBlock(addRes, clonedBlock, insertIdx);
            if (!attach.IsSuccess) return attach.Error!;

            await repository.SaveSessionAsync(src.Session, cancellationToken);
            return new CloneEntityResponse(clonedBlock.Id, "articleBlock", targetComp.Id, attach.Value, blockRef, src.Session.Id);
        }

        // Question clone
        var targetQuiz = src.Quiz!;
        string targetContainerId = src.Component?.Id ?? targetQuiz.Id;
        if (!string.IsNullOrWhiteSpace(command.TargetContainerAddress))
        {
            var dstRes = await addressResolver.ResolveAsync(command.TargetContainerAddress, cancellationToken);
            if (!dstRes.IsSuccess) return dstRes.Error!;

            var dst = dstRes.Value;
            if (src.Session is not null)
            {
                if (dst.EntityKind != "component" || dst.Component?.Kind != ComponentKind.Quiz || dst.Quiz is null || dst.Session?.Id != src.Session.Id)
                    return new Error("Clone.InvalidTarget", "Target container for cloning a session question must be a quiz component in the same session.", ErrorType.Validation);
                targetQuiz = dst.Quiz;
                targetContainerId = dst.Component.Id;
            }
            else if (dst.EntityKind != "quiz" || dst.Quiz?.Id != src.Quiz!.Id)
            {
                return new Error("Clone.InvalidTarget", "Standalone quiz questions can only be cloned within the same quiz.", ErrorType.Validation);
            }
        }

        int qInsertIdx = command.InsertAtIndex ?? (targetQuiz.Id == src.Quiz!.Id ? src.Question!.Index + 1 : targetQuiz.Questions.Count + 1);
        string? qRef = requestedRef ?? AllocateCloneRef(src.Question!.RefKey, "q", usedRefs);
        var clonedQ = src.Question!.CloneWithNewId(idGenerator.NewQuestionId(), targetQuiz.Id, qRef);

        var insRes = targetQuiz.InsertQuestion(clonedQ, qInsertIdx);
        if (!insRes.IsSuccess) return insRes.Error!;

        if (src.Session is not null)
            await repository.SaveSessionAsync(src.Session, cancellationToken);
        else
            await repository.SaveQuizAsync(targetQuiz, cancellationToken);

        return new CloneEntityResponse(clonedQ.Id, "question", targetContainerId, clonedQ.Index, qRef, src.WorkspaceId);
    }

    private static Result<int> AttachClonedBlock(SessionComponent targetComp, ArticleBlock clonedBlock, int insertIdx)
    {
        // Use reflection-free domain-safe path: clone reuses Rehydrate via CloneWithNewId,
        // and we attach through StudySession? Wait: SessionComponent.AttachBlock is internal to Domain.
        // Instead, StudySession.AttachBlock can be exposed or we add SessionComponent.InsertClonedBlock!
        return targetComp.InsertBlockAt(clonedBlock, insertIdx);
    }

    private static string? AllocateCloneRef(string? sourceRef, string fallback, HashSet<string> usedRefs)
    {
        if (string.IsNullOrWhiteSpace(sourceRef))
            return null;

        var slug = RefKeyValidator.GenerateSlug($"{sourceRef}-copy", fallback, usedRefs);
        usedRefs.Add(slug);
        return slug;
    }
}
