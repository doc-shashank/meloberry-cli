using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Json;
using Meloberry.Domain.Common;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;
using Meloberry.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class ReorganizationTests : IDisposable
{
    private readonly WorkspaceTestFixture _fx = new("mby_reorg");

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void DomainTransferBlockAndQuestion_EnforceTypeSafetyBoundsAndExactOrder()
    {
        var session = StudySession.Create("Session_1", "C:\\temp\\Session_1").Value;
        var art1 = session.AddComponent("Component_1", "article").Value;
        var art2 = session.AddComponent("Component_2", "article").Value;
        var qzMcq = session.AddComponent("Component_3", "mcq").Value;
        var qzMixed = session.AddComponent("Component_4", "mixed").Value;

        var b1 = art1.AddTextBlock("ArtComp_1", "One").Value;
        var b2 = art1.AddTextBlock("ArtComp_2", "Two").Value;
        var b3 = art2.AddTextBlock("ArtComp_3", "Three").Value;

        var sameRes = session.TransferBlock(b1.Id, art1.Id, art1.Id, art1.ArticleBlocks.Count + 1);
        Assert.True(sameRes.IsSuccess);
        Assert.Equal([b2.Id, b1.Id], art1.ArticleBlocks.Select(b => b.Id).ToArray());

        var xferBlock = session.TransferBlock(b1.Id, art1.Id, art2.Id, 1);
        Assert.True(xferBlock.IsSuccess);
        Assert.Equal(1, xferBlock.Value);
        Assert.Equal([b2.Id], art1.ArticleBlocks.Select(b => b.Id).ToArray());
        Assert.Equal([b1.Id, b3.Id], art2.ArticleBlocks.Select(b => b.Id).ToArray());
        Assert.Equal(art2.Id, b1.ComponentId);

        var fc = qzMixed.Quiz!.AddQuestion("Q_1", QuestionType.Flashcard).Value;
        var mcq = qzMixed.Quiz!.AddQuestion("Q_2", QuestionType.Mcq).Value;
        var mismatch = session.TransferQuestion(fc.Id, qzMixed.Id, qzMcq.Id);
        Assert.False(mismatch.IsSuccess);
        Assert.Equal("Quiz.QuestionTypeMismatch", mismatch.Error!.Code);
        Assert.Equal(2, qzMixed.Quiz.Questions.Count);
        Assert.Empty(qzMcq.Quiz!.Questions);

        var okQ = session.TransferQuestion(mcq.Id, qzMixed.Id, qzMcq.Id, 1);
        Assert.True(okQ.IsSuccess);
        Assert.Equal(qzMcq.Quiz.Id, mcq.ContainerId);
        Assert.Single(qzMcq.Quiz.Questions);
        Assert.Single(qzMixed.Quiz.Questions);

        Assert.Equal("Reorder.CountMismatch", session.ReorderComponentsExact([art1.Id]).Error!.Code);
        Assert.Equal("Reorder.DuplicateId", session.ReorderComponentsExact([art1.Id, art1.Id, qzMcq.Id, qzMixed.Id]).Error!.Code);
        Assert.Equal("Reorder.UnknownId", session.ReorderComponentsExact([art1.Id, art2.Id, qzMcq.Id, "Component_999"]).Error!.Code);
        Assert.True(session.ReorderComponentsExact([qzMixed.Id, art2.Id, art1.Id, qzMcq.Id]).IsSuccess);
        Assert.Equal([1, 2, 3, 4], session.Components.Select(c => c.Index).ToArray());
    }

    [Fact]
    public async Task ApplyAt_PatchTo_PatchOrder_Clone_And_BatchReorg_WorkEndToEnd()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var clone = scope.ServiceProvider.GetRequiredService<CloneEntityHandler>();
        var batch = scope.ServiceProvider.GetRequiredService<BatchOperationsHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var initialJson = """
        {
          "kind": "session",
          "name": "Reorg Lab",
          "components": [
            {
              "kind": "article",
              "ref": "art-a",
              "title": "Article A",
              "blocks": [
                { "kind": "text", "ref": "blk-1", "text": "Block 1" },
                { "kind": "text", "ref": "blk-2", "text": "Block 2" }
              ]
            },
            {
              "kind": "article",
              "ref": "art-b",
              "title": "Article B",
              "blocks": [
                { "kind": "text", "ref": "blk-3", "text": "Block 3" }
              ]
            },
            {
              "kind": "quiz",
              "ref": "quiz-a",
              "quizType": "mixed",
              "questions": [
                { "type": "mcq", "ref": "q-one", "text": "Q1?", "layoutOverrides": { "optionColumns": "2" }, "options": [{ "text": "Y", "isCorrect": true }, { "text": "N" }] }
              ]
            },
            {
              "kind": "quiz",
              "ref": "quiz-b",
              "quizType": "mixed",
              "questions": [
                { "type": "flashcard", "ref": "q-two", "front": { "text": "Front" }, "back": { "text": "Back" } }
              ]
            }
          ]
        }
        """;

        var createRes = await apply.HandleAsync(new ApplySpecCommand(null, null, initialJson, _fx.WorkDir));
        Assert.True(createRes.IsSuccess);
        var wsId = createRes.Value.TargetId;

        var appendBlock = """{"blocks":[{"kind":"text","ref":"blk-inserted","text":"Inserted at 2"}]}""";
        var applyAtRes = await apply.HandleAsync(new ApplySpecCommand($"{wsId}#art-a", null, appendBlock, InsertAtIndex: 2));
        Assert.True(applyAtRes.IsSuccess);

        var moveBlockRes = await patch.HandleAsync(new PatchEntityCommand($"{wsId}#blk-inserted", MoveToTargetAddress: $"{wsId}#art-b:1"));
        Assert.True(moveBlockRes.IsSuccess);
        Assert.Equal(1, moveBlockRes.Value.NewIndex);

        var moveQRes = await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-one", MoveToTargetAddress: $"{wsId}#quiz-b", MoveToIndex: 1));
        Assert.True(moveQRes.IsSuccess);
        Assert.Equal(1, moveQRes.Value.NewIndex);

        var cloneBlockRes = await clone.HandleAsync(new CloneEntityCommand($"{wsId}#blk-inserted", $"{wsId}#art-a", InsertAtIndex: 1));
        Assert.True(cloneBlockRes.IsSuccess);
        Assert.Equal("articleBlock", cloneBlockRes.Value.EntityKind);
        Assert.Equal(1, cloneBlockRes.Value.NewIndex);
        Assert.NotEqual("blk-inserted", cloneBlockRes.Value.NewRef);

        var cloneCompRes = await clone.HandleAsync(new CloneEntityCommand($"{wsId}#art-b", InsertAtIndex: 1, NewRef: "art-b-copy"));
        Assert.True(cloneCompRes.IsSuccess);
        Assert.Equal("component", cloneCompRes.Value.EntityKind);
        Assert.Equal("art-b-copy", cloneCompRes.Value.NewRef);

        var orderRes = await patch.HandleAsync(new PatchEntityCommand($"{wsId}#art-b", ExactOrderIds: ["#blk-3", "#blk-inserted"]));
        Assert.True(orderRes.IsSuccess);

        var loaded = (await repo.FindSessionAsync(wsId))!.Session;
        var artB = loaded.Components.Single(c => c.RefKey == "art-b");
        Assert.Equal(["blk-3", "blk-inserted"], artB.ArticleBlocks.Select(b => b.RefKey!).ToArray());

        var quizB = loaded.Components.Single(c => c.RefKey == "quiz-b");
        Assert.Equal(["q-one", "q-two"], quizB.Quiz!.Questions.Select(q => q.RefKey!).ToArray());

        var batchJson = $$"""
        [
          { "op": "apply", "addr": "{{wsId}}#art-a", "at": 1, "spec": { "blocks": [{ "kind": "text", "ref": "blk-batch", "text": "Batch block" }] } },
          { "op": "move", "addr": "{{wsId}}#blk-batch", "to": "{{wsId}}#art-b", "index": 1 },
          { "op": "clone", "addr": "{{wsId}}#q-one", "ref": "q-one-clone" },
          { "op": "reorder", "addr": "{{wsId}}#art-b", "order": ["#blk-inserted", "#blk-3", "#blk-batch"] }
        ]
        """;
        var batchRes = await batch.HandleAsync(new BatchOperationsCommand(InlineJson: batchJson));
        Assert.True(batchRes.IsSuccess, batchRes.Error?.Description);

        var loadedAfterBatch = (await repo.FindSessionAsync(wsId))!.Session;
        var artBAfter = loadedAfterBatch.Components.Single(c => c.RefKey == "art-b");
        Assert.Equal(["blk-inserted", "blk-3", "blk-batch"], artBAfter.ArticleBlocks.Select(b => b.RefKey!).ToArray());
        Assert.Contains(loadedAfterBatch.Components.Single(c => c.RefKey == "quiz-b").Quiz!.Questions, q => q.RefKey == "q-one-clone");
    }
}
