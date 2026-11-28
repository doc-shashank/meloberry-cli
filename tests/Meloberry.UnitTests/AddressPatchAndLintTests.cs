using System.Text.Json;
using System.Text.Json.Nodes;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Common;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;
using Meloberry.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class AddressPatchAndLintTests : IDisposable
{
    private readonly WorkspaceTestFixture _fx = new("mby_addr_lint");

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Addressing_Patching_Move_ImageGuards_And_BatchRollback_Work()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var resolver = scope.ServiceProvider.GetRequiredService<EntityAddressResolver>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var batch = scope.ServiceProvider.GetRequiredService<BatchOperationsHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var srcDir = _fx.SubDir("patch_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig1.png"));
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig2.png"), [0x01]);
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "orphan.png"), [0xAA, 0xBB]);

        var specPath = Path.Combine(srcDir, "session.json");
        await File.WriteAllTextAsync(specPath, """
        {
          "kind": "session",
          "name": "Initial",
          "components": [
            {
              "kind": "article",
              "ref": "intro",
              "title": "Section 1",
              "blocks": [
                { "kind": "text", "ref": "b-one", "text": "First block" },
                { "kind": "image", "ref": "b-img", "imagePath": "fig1.png" },
                { "kind": "table", "ref": "tbl1", "headers": ["A", "B", "C"], "rows": [["1", "2", "3"]] },
                { "kind": "diagram-explore", "ref": "diag1", "imagePath": "fig1.png", "title": "Diag", "hotspots": [{ "label": "N1", "note": "Note", "rect": [0.1, 0.1, 0.2, 0.2] }] },
                { "kind": "reveal", "ref": "rev1", "title": "Rev", "showFirst": false, "steps": [{ "heading": "H1", "body": "B1" }] }
              ]
            },
            {
              "kind": "quiz",
              "ref": "check",
              "quizType": "mixed",
              "questions": [
                { "type": "mcq", "ref": "q-force", "text": "Old prompt?", "isMultiCorrect": true, "maxOptions": 4, "options": [{ "index": 1, "text": "A", "isCorrect": true }, { "index": 2, "text": "B", "isCorrect": true }, { "index": 3, "text": "C", "isCorrect": false }, { "index": 4, "text": "D", "isCorrect": false }] },
                { "type": "flashcard", "ref": "q-fc", "front": { "text": "F1", "imagePath": "fig1.png" }, "back": { "text": "B1" } },
                { "type": "fill-blank", "ref": "q-fb", "text": "Value is {{42}}." },
                { "type": "assertion-reason", "ref": "q-ar", "assertion": "A", "reason": "R", "answer": "both-no-explain" },
                { "type": "spot-error", "ref": "q-se", "noError": true, "allowNoError": true, "text": "In [acidosis], pH is [low]." },
                { "type": "diff", "ref": "q-diff", "mode": "fill", "headers": ["H1", "H2", "H3"], "rows": [["R", "{{X}}", "Z"]] },
                { "type": "mechanism", "ref": "q-mech", "title": "Mech", "steps": [{ "orderGroup": 1, "text": "A" }, { "orderGroup": 2, "text": "B" }] },
                { "type": "relabel", "ref": "q-rel", "imagePath": "fig1.png", "mode": "recall", "style": "blur", "boxes": [{ "label": "Node", "rect": [0.1, 0.1, 0.2, 0.2] }] },
                { "type": "numeric", "ref": "q-num", "text": "Val?", "answer": 100, "tolerance": "5%" },
                { "type": "case", "ref": "q-case", "title": "Case 1", "stem": "Patient stem.", "questions": [{ "type": "mcq", "text": "S1", "options": [{ "text": "X", "isCorrect": true }, { "text": "Y" }] }, { "type": "numeric", "text": "S2", "answer": 5, "tolerance": 1 }] }
              ]
            }
          ]
        }
        """);

        var applied = (await apply.HandleAsync(new ApplySpecCommand(null, specPath, WorkingDirectory: _fx.WorkDir))).Value;
        var wsId = applied.TargetId;

        // Positional paths & #ref resolution + duplicate ref rejection
        Assert.Equal((await resolver.ResolveAsync($"{wsId}#c1")).Value.EntityId, (await resolver.ResolveAsync($"{wsId}#intro")).Value.EntityId);
        Assert.Equal((await resolver.ResolveAsync($"{wsId}#c1.b1")).Value.EntityId, (await resolver.ResolveAsync($"{wsId}#b-one")).Value.EntityId);
        var q1Id = (await resolver.ResolveAsync($"{wsId}#c2.q1")).Value.EntityId;
        Assert.Equal(q1Id, (await resolver.ResolveAsync($"{wsId}#q-force")).Value.EntityId);

        Assert.Equal("Address.DuplicateRef", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#c1.b2", SetPairs: ["ref=b-one"]))).Error!.Code);

        // Unknown field typo hint + dotted --set validation
        var unknownRes = await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-force", InlineJson: """{"explanaton":"typo"}"""));
        Assert.Equal("Patch.UnknownField", unknownRes.Error!.Code);
        Assert.Equal("Did you mean 'explanation'?", unknownRes.Error.Hint);
        Assert.Equal("Patch.InvalidSet", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-se", SetPairs: [".foo=bar"]))).Error!.Code);
        Assert.Equal("Patch.InvalidSet", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-se", SetPairs: ["foo.=bar"]))).Error!.Code);

        // Combined MCQ shrink + single-select + move 1 -> 2, duplicate option index, multi-correct in single
        var moveRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{wsId}#q-force",
            InlineJson: """{"text":"Updated?","maxOptions":2,"isMultiCorrect":false,"layoutOverrides":{"optionColumns":"2"},"options":[{"text":"Yes","isCorrect":true},{"text":"No","isCorrect":false}]}""",
            MoveSpec: "2:shift"));
        Assert.True(moveRes.IsSuccess);
        Assert.Equal(2, moveRes.Value.NewIndex);
        Assert.Equal(q1Id, (await resolver.ResolveAsync($"{wsId}#c2.q2")).Value.EntityId);
        Assert.Equal("Json.DuplicateOptionIndex", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-force", InlineJson: """{"options":[{"index":2,"text":"A","isCorrect":true},{"text":"B"}]}"""))).Error!.Code);
        Assert.Equal("Question.MultipleCorrectInSingle", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-force", InlineJson: """{"options":[{"text":"A","isCorrect":true},{"text":"B","isCorrect":true}]}"""))).Error!.Code);
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-force", InlineJson: """{"maxOptions":[]}"""))).Error!.Code);

        // Flashcard coercion + image clearing + invalid position type
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-fc", SetPairs: ["front.text=true", "back.text=42", "front.position=aboveImage"]))).IsSuccess);
        var fcAfter = (FlashcardQuestion)(await resolver.ResolveAsync($"{wsId}#q-fc")).Value.Question!;
        Assert.Equal("true", fcAfter.Front.Text);
        Assert.Equal("42", fcAfter.Back.Text);
        Assert.Equal(MediaPosition.TextAboveImage, fcAfter.Front.Position);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-fc", InlineJson: """{"front":{"imagePath":"   "}}"""))).IsSuccess);
        Assert.Null(((FlashcardQuestion)(await resolver.ResolveAsync($"{wsId}#q-fc")).Value.Question!).Front.ImagePath);
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-fc", InlineJson: """{"front":{"position":42}}"""))).Error!.Code);

        // Image path rejection on articleBlock:image & relabel
        foreach (var bad in new[] { """{"imagePath":""}""", """{"imagePath":"   "}""", """{"imagePath":null}""" })
            Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#b-img", InlineJson: bad))).Error!.Code);
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-rel", SetPairs: ["imagePath=null"]))).Error!.Code);

        // Valid patches across all component types
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#tbl1", SetPairs: ["title=Updated Tbl", "stickyFirstCol=false", "hideableCols=2", "footnote=Note."]))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#diag1", SetPairs: ["title=Updated Diag"]))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#rev1", SetPairs: ["showFirst=true"]))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-fb", SetPairs: ["fuzzy=false", "text=Pacemaker is {{PFK-1}}."]))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-ar", SetPairs: ["answer=both-explain"]))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-diff", InlineJson: """{"title":"Updated Diff","bank":false,"placeholder":"Type gas change","rows":[["Resp","{{High PaCO2}}","Renal"]]}"""))).IsSuccess);
        var patchedDiff = (PayloadQuestion)(await resolver.ResolveAsync($"{wsId}#q-diff")).Value.Question!;
        Assert.Contains("\"placeholder\":\"Type gas change\"", patchedDiff.PayloadJson);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-mech", InlineJson: """{"title":"Upd Mech","steps":[{"orderGroup":1,"text":"A"},{"orderGroup":2,"text":"B"},{"isDistractor":true,"text":"C"}]}"""))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-rel", SetPairs: ["mode=reveal", "style=pixelate", "prompt=Updated"]))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-rel", JsonFilePath: await WriteTempJsonAsync(srcDir, "p_img.json", """{"imagePath":"fig2.png"}""")))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-num", SetPairs: ["answer=110", "tolerance=10%"]))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-case", SetPairs: ["title=Updated Case"]))).IsSuccess);

        // Invalid payload patch guards
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-se", SetPairs: ["allowNoError=false"]))).Error!.Code);
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-se", InlineJson: """{"noError":true,"allowNoError":false}"""))).Error!.Code);
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#tbl1", InlineJson: """{"hideableCols":[5]}"""))).Error!.Code);
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#tbl1", InlineJson: """{"headers":[" ",""]}"""))).Error!.Code);
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#tbl1", InlineJson: """{"rows":[]}"""))).Error!.Code);
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#tbl1", InlineJson: """{"rows":[["only1"]]}"""))).Error!.Code);
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#tbl1", InlineJson: """{"alignments":["left"]}"""))).Error!.Code);
        Assert.Equal("Diff.MalformedBlankCell", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-diff", InlineJson: """{"rows":[["Resp","{{unclosed","Renal"]]}"""))).Error!.Code);
        Assert.Equal("Patch.InvalidValue", (await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-diff", InlineJson: """{"rows":[["OnlyOneCol"]]}"""))).Error!.Code);

        // Failed append cleans up newly copied image asset
        var failAppendPath = await WriteTempJsonAsync(srcDir, "fail_append.json", """
        {
          "questions": [
            { "type": "mcq", "text": "Good Q", "imagePath": "orphan.png", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] },
            { "type": "mcq", "ref": "q-force", "text": "Dup Ref", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] }
          ]
        }
        """);
        Assert.Equal("Address.DuplicateRef", (await apply.HandleAsync(new ApplySpecCommand(wsId, failAppendPath))).Error!.Code);
        Assert.Empty(Directory.GetFiles(applied.StorageDirectory + "/assets", "orphan_*"));

        // Batch guards: conflicts, rollback of build/pkg outputs, $prev.id after rm
        Assert.Equal("Batch.InvalidOp", (await batch.HandleAsync(new BatchOperationsCommand(InlineJson: """[{"op":"apply","file":"a.json","spec":{"kind":"quiz","questions":[]}}]"""))).Error!.Code);
        Assert.Equal("Batch.InvalidOp", (await batch.HandleAsync(new BatchOperationsCommand(InlineJson: $$$"""[{"op":"patch","addr":"{{{wsId}}}","file":"p.json","data":{"name":"New"}}]"""))).Error!.Code);

        var batchBuildOut = Path.Combine(_fx.TempRoot, "batch_rb_web");
        var batchPkgOut = Path.Combine(_fx.TempRoot, "batch_rb.mbypkg");
        var rbBatch = $$"""
            [
              { "op": "apply", "out": "{{_fx.WorkDir.Replace("\\", "\\\\")}}", "spec": { "kind": "quiz", "quizType": "mcq", "questions": [{ "type": "mcq", "text": "Q?", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] }] } },
              { "op": "build", "addr": "$0.id", "out": "{{batchBuildOut.Replace("\\", "\\\\")}}" },
              { "op": "pkg", "addr": "$0.id", "platform": "desktop", "out": "{{batchPkgOut.Replace("\\", "\\\\")}}" },
              { "op": "patch", "addr": "$0.id", "set": [123] }
            ]
            """;
        Assert.Equal("Batch.InvalidOp", (await batch.HandleAsync(new BatchOperationsCommand(InlineJson: rbBatch))).Error!.Code);
        Assert.False(Directory.Exists(batchBuildOut));
        Assert.False(File.Exists(batchPkgOut));

        var rmPrevBatch = $$"""
            [
              { "op": "apply", "out": "{{_fx.WorkDir.Replace("\\", "\\\\")}}", "spec": { "kind": "quiz", "quizType": "mcq", "questions": [
                { "type": "mcq", "text": "Q1?", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] },
                { "type": "mcq", "text": "Q2?", "options": [{ "text": "C", "isCorrect": true }, { "text": "D" }] }
              ] } },
              { "op": "rm", "addr": "$0.id#q2" },
              { "op": "check", "addr": "$prev.id" }
            ]
            """;
        Assert.True((await batch.HandleAsync(new BatchOperationsCommand(InlineJson: rmPrevBatch))).IsSuccess);

        // Batch rollback restores pre-existing assets, deletes newly added assets, and undoes full workspace rm
        var preExistingAssets = Directory.GetFiles(Path.Combine(applied.StorageDirectory, "assets"))
            .Select(Path.GetFileName)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rollbackAppendSpec = await WriteTempJsonAsync(srcDir, "rollback_append.json", """{"blocks":[{"kind":"image","imagePath":"orphan.png"}]}""");
        var rollbackAssetsAndRmBatch = $$"""
            [
              { "op": "patch", "addr": "{{wsId}}#b-one", "set": ["text=Mutated inside failing batch"] },
              { "op": "apply", "addr": "{{wsId}}#c1", "file": "{{rollbackAppendSpec.Replace("\\", "\\\\")}}" },
              { "op": "rm", "addr": "{{wsId}}" },
              { "op": "patch", "addr": "{{wsId}}", "set": [123] }
            ]
            """;
        Assert.Equal("Batch.InvalidOp", (await batch.HandleAsync(new BatchOperationsCommand(InlineJson: rollbackAssetsAndRmBatch))).Error!.Code);
        Assert.True(Directory.Exists(applied.StorageDirectory));
        Assert.Equal("First block", (await resolver.ResolveAsync($"{wsId}#b-one")).Value.ArticleBlock!.TextContent);
        var postRollbackAssets = Directory.GetFiles(Path.Combine(applied.StorageDirectory, "assets"))
            .Select(Path.GetFileName)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(preExistingAssets, postRollbackAssets);

        // Domain UpdateImage / SetSideImage blank-path validation
        var imgBlock = ArticleBlock.CreateImage("ArtComp_1", "Component_1", "assets/fig1.png");
        Assert.Equal("ArticleBlock.EmptyImagePath", imgBlock.UpdateImage("  ").Error!.Code);
        var card = FlashcardQuestion.CreateNew("Question_1", "Quiz_1", 1);
        Assert.Equal("Question.EmptyImagePath", card.SetSideImage(FlashcardFace.Front, "").Error!.Code);
        _ = repo;
    }

    [Fact]
    public async Task LintMatrix_CoversAllBlockAndQuestionTypes_Links_Warnings_And_ExportBuildGuards()
    {
        using var sp = _fx.CreateProvider("appdata_lint");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var srcDir = _fx.SubDir("lint_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "img.png"));

        // 1. Export/build blocked by Lint.Failed, succeed once fixed
        var mcqApply = (await apply.HandleAsync(new ApplySpecCommand(null, null,
            """{"kind":"quiz","quizType":"mcq","questions":[{"type":"mcq","text":"temp","options":[{"text":"3"},{"text":"4"}]}]}""", _fx.WorkDir))).Value;
        Assert.Equal("Lint.Failed", (await export.HandleAsync(new ExportPackageCommand(mcqApply.TargetId))).Error!.Code);
        Assert.Equal("Lint.Failed", (await build.HandleAsync(new BuildWebAppCommand(mcqApply.TargetId))).Error!.Code);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{mcqApply.TargetId}#q1",
            InlineJson: """{"text":"2+2?","options":[{"text":"3","isCorrect":false},{"text":"4","isCorrect":true}]}"""))).IsSuccess);
        var fixedLint = (await lint.HandleAsync(new LintCommand(mcqApply.TargetId))).Value;
        Assert.True(fixedLint.IsClean && fixedLint.WarningCount == 1);

        // 2. Comprehensive invalid session exercising every lint code across Phase 1-3 + native links
        var badSpecPath = await WriteTempJsonAsync(srcDir, "broken_all.json", """
        {
          "kind": "session",
          "name": "Broken All",
          "components": [
            {
              "kind": "article",
              "title": "Bad Article",
              "blocks": [
                { "kind": "text", "text": "Bad [c](mby://card/missing), [a](mby://article/nope), [s](mby://session/Session_Future_001)." },
                { "kind": "table", "headers": ["  ", ""], "alignments": ["diagonal"], "hideableCols": [1, 1, 5], "rows": [["a", "b"]] },
                { "kind": "diagram-explore", "imagePath": "img.png", "hotspots": [{ "label": "Out", "note": "", "rect": [0.85, 0.2, 0.3, 0.1] }] },
                { "kind": "reveal", "steps": [] }
              ]
            },
            {
              "kind": "quiz",
              "quizType": "mixed",
              "title": "Bad Quiz",
              "questions": [
                { "type": "fill-blank", "text": "No blanks." },
                { "type": "fill-blank", "text": "Unbalanced {{blank" },
                { "type": "fill-blank", "text": "Empty {{ | }} blank." },
                { "type": "assertion-reason", "assertion": "A", "reason": "", "answer": "both-explain" },
                { "type": "assertion-reason", "assertion": "A", "reason": "R", "answer": "maybe" },
                { "type": "assertion-reason", "assertion": "A", "reason": "R", "answer": "both-false", "scheme": "4opt" },
                { "type": "spot-error", "text": "Only [~~one~~ -> two]." },
                { "type": "spot-error", "text": "Two [clean] [spans]." },
                { "type": "spot-error", "noError": true, "allowNoError": false, "text": "In [valid] and [~~bad~~ -> good]." },
                { "type": "spot-error", "text": "Empty [valid] and [~~bad~~ -> ]." },
                { "type": "diff", "mode": "bogus", "headers": ["A", "B"], "rows": [["1", "2"]] },
                { "type": "diff", "mode": "fill", "headers": ["A", "B"], "rows": [["1", "2"]] },
                { "type": "diff", "mode": "spot-wrong", "allowNoError": false, "headers": ["A", "B"], "rows": [["1", "2"]] },
                { "type": "diff", "mode": "rearrange", "headers": ["Feature", "OnlyOne"], "rows": [["X", "Y"]] },
                { "type": "mechanism", "steps": [{ "orderGroup": 1, "text": "Only one" }, { "isDistractor": true, "text": "Decoy" }] },
                { "type": "mechanism", "steps": [{ "orderGroup": 1, "text": "Same" }, { "orderGroup": 2, "text": "same" }] },
                { "type": "mechanism", "steps": [{ "orderGroup": 1, "text": "Valid" }, { "orderGroup": 2, "text": "   " }] },
                { "type": "mechanism", "steps": [{ "orderGroup": 1, "text": "O1" }, { "orderGroup": 2, "text": "O2" }, { "isDistractor": true, "text": "D1" }, { "isDistractor": true, "text": "D2" }, { "isDistractor": true, "text": "D3" }] },
                { "type": "relabel", "imagePath": "img.png", "mode": "bad", "style": "neon", "boxes": [{ "label": "Out", "x": 0.8, "y": 0.2, "w": 0.5, "h": 0.1 }, { "label": "   ", "x": 0.1, "y": 0.1, "w": 0.2, "h": 0.1 }] },
                { "type": "relabel", "imagePath": "img.png", "mode": "bank", "style": "solid", "boxes": [] },
                { "type": "classify", "buckets": ["OnlyOne"], "items": [{ "bucket": "OnlyOne", "text": "Solo" }] },
                { "type": "classify", "buckets": ["B1", "B2"], "items": [{ "bucket": "B1", "text": "Dup" }, { "bucket": "B2", "text": "Dup" }] },
                { "type": "match", "pairs": [{ "left": "Only1", "right": "R1" }], "distractors": ["D1", "D2"] },
                { "type": "numeric", "text": "", "answer": 10, "tolerance": -1 },
                { "type": "case", "stem": "Short", "questions": [{ "type": "numeric", "text": "", "answer": 5, "tolerance": 1 }] }
              ]
            }
          ]
        }
        """);

        var badApplied = (await apply.HandleAsync(new ApplySpecCommand(null, badSpecPath, WorkingDirectory: _fx.WorkDir))).Value;
        var report = (await lint.HandleAsync(new LintCommand(badApplied.TargetId))).Value;
        Assert.False(report.IsClean);

        foreach (var code in new[]
        {
            "Lint.MissingLinkCard", "Lint.UnresolvedArticleLink", "Lint.UnresolvedSessionLink",
            "Lint.InvalidTablePayload", "Lint.InvalidTableHideableCol",
            "Lint.InvalidDiagramHotspotBounds", "Lint.EmptyDiagramHotspotNote", "Lint.EmptyRevealSteps",
            "Lint.NoFillBlanks", "Lint.UnbalancedBlankDelimiters", "Lint.EmptyBlankAnswer",
            "Lint.EmptyAssertionOrReason", "Lint.InvalidAssertionReasonAnswer", "Lint.InvalidAssertionReasonScheme",
            "Lint.InsufficientSpotErrorSpans", "Lint.SpotErrorMissingFault", "Lint.SpotErrorConflictingNoError", "Lint.SpotErrorEmptyCorrection",
            "Lint.InvalidDiffMode", "Lint.DiffMissingBlanks", "Lint.DiffMissingFaults", "Lint.InvalidDiffTable",
            "Lint.InsufficientMechanismSteps", "Lint.DuplicateMechanismStep", "Lint.EmptyMechanismStep", "Lint.ExcessiveMechanismDistractors",
            "Lint.InvalidRelabelMode", "Lint.InvalidRelabelStyle", "Lint.InvalidRelabelBoxBounds", "Lint.EmptyRelabelLabel", "Lint.EmptyRelabelBoxes",
            "Lint.InsufficientClassifyBuckets", "Lint.DuplicateClassifyItem",
            "Lint.InsufficientMatchPairs", "Lint.ExcessiveMatchDistractors",
            "Lint.InvalidNumericTolerance", "Lint.InsufficientCaseQuestions"
        })
        {
            Assert.Contains(report.Issues, i => i.Code == code);
        }
        Assert.Contains(report.Issues, i => i.TargetId.Contains(":sub-0"));

        // 3. Broken image file on disk + null Quiz component + corrupt in-DB FillBlank payload
        foreach (var file in Directory.GetFiles(Path.Combine(badApplied.StorageDirectory, "assets")))
            File.Delete(file);
        var brokenImgReport = (await lint.HandleAsync(new LintCommand(badApplied.TargetId))).Value;
        Assert.Contains(brokenImgReport.Issues, i => i.Code == "Lint.BrokenImageFile");

        var nullQuizStorage = _fx.SubDir("work", "Session_20261124_000001_001");
        Directory.CreateDirectory(Path.Combine(nullQuizStorage, "assets"));
        var nullComp = SessionComponent.Rehydrate("Component_20261124_000001_001", "Session_20261124_000001_001", 1, ComponentKind.Quiz, quiz: null, articleBlocks: []);
        await repo.CreateSessionAsync(StudySession.Rehydrate("Session_20261124_000001_001", nullQuizStorage, nullComp.Id, [nullComp], "Null Quiz"));
        Assert.Contains((await lint.HandleAsync(new LintCommand("Session_20261124_000001_001"))).Value.Issues, i => i.Code == "Lint.EmptyComponent");

        var corruptStorage = _fx.SubDir("work", "Set_20261124_120000_993");
        Directory.CreateDirectory(Path.Combine(corruptStorage, "assets"));
        await repo.CreateQuizAsync(Quiz.Rehydrate("Set_20261124_120000_993", QuizType.Mixed, corruptStorage,
            [PayloadQuestion.Rehydrate("Q_20261124_120000_993", "Set_20261124_120000_993", 1, QuestionType.FillBlank, """{"template":"Enzyme is {{PFK-1}}."}""")], "Corrupt"));
        Assert.Contains((await lint.HandleAsync(new LintCommand("Set_20261124_120000_993"))).Value.Issues, i => i.Code == "Lint.InvalidQuestionPayload");

        // 4. Phase 3 Max* lint warnings when density thresholds are exceeded
        var denseHotspots = string.Join(",", Enumerable.Range(1, Phase3PayloadRules.MaxHotspots + 1).Select(idx => $"{{\"label\":\"H{idx}\",\"note\":\"Note {idx}\",\"rect\":[0.1,0.1,0.1,0.1]}}"));
        var denseSteps = string.Join(",", Enumerable.Range(1, Phase3PayloadRules.MaxRevealSteps + 1).Select(idx => $"{{\"heading\":\"S{idx}\",\"body\":\"B{idx}\"}}"));
        var denseBuckets = string.Join(",", Enumerable.Range(1, Phase3PayloadRules.MaxClassifyBuckets + 1).Select(idx => $"\"B{idx}\""));
        var denseItems = string.Join(",", Enumerable.Range(1, Phase3PayloadRules.MaxClassifyItems + 1).Select(idx => $"{{\"bucket\":\"B{(idx % (Phase3PayloadRules.MaxClassifyBuckets + 1)) + 1}\",\"text\":\"Item {idx}\"}}"));
        var densePairs = string.Join(",", Enumerable.Range(1, Phase3PayloadRules.MaxMatchPairs + 1).Select(idx => $"{{\"left\":\"L{idx}\",\"right\":\"R{idx}\"}}"));
        var denseSubQs = string.Join(",", Enumerable.Range(1, Phase3PayloadRules.MaxCaseQuestions + 1).Select(idx => $"{{\"type\":\"numeric\",\"text\":\"Q{idx}\",\"answer\":{idx}}}"));
        var denseSpecPath = await WriteTempJsonAsync(srcDir, "dense_warnings.json", $$"""
        {
          "kind": "session",
          "name": "Dense Warnings",
          "components": [
            {
              "kind": "article",
              "title": "Dense Article",
              "blocks": [
                { "kind": "diagram-explore", "imagePath": "img.png", "hotspots": [{{denseHotspots}}] },
                { "kind": "reveal", "steps": [{{denseSteps}}] }
              ]
            },
            {
              "kind": "quiz",
              "quizType": "mixed",
              "title": "Dense Quiz",
              "questions": [
                { "type": "classify", "buckets": [{{denseBuckets}}], "items": [{{denseItems}}] },
                { "type": "match", "pairs": [{{densePairs}}] },
                { "type": "case", "stem": "Dense stem.", "questions": [{{denseSubQs}}] }
              ]
            }
          ]
        }
        """);
        var denseApplied = (await apply.HandleAsync(new ApplySpecCommand(null, denseSpecPath, WorkingDirectory: _fx.WorkDir))).Value;
        var denseReport = (await lint.HandleAsync(new LintCommand(denseApplied.TargetId))).Value;
        Assert.True(denseReport.IsClean);
        foreach (var warnCode in new[]
        {
            "Lint.ExcessiveDiagramHotspots",
            "Lint.ExcessiveRevealSteps",
            "Lint.ExcessiveClassifyBuckets",
            "Lint.ExcessiveClassifyItems",
            "Lint.ExcessiveMatchPairs",
            "Lint.ExcessiveCaseQuestions"
        })
        {
            Assert.Single(denseReport.Issues, i => i.Severity == "Warning" && i.Code == warnCode);
        }
    }

    [Fact]
    public async Task GetFull_IncludesArticleLinkCards()
    {
        using var sp = _fx.CreateProvider("appdata_links_full");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();

        var srcDir = _fx.SubDir("links_full_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig.png"));
        var mdPath = Path.Combine(srcDir, "article.md");
        await File.WriteAllTextAsync(mdPath, """
            ---
            kind: article
            title: "Link Cards"
            ---

            See [the card](mby://card/c1) for details.

            :::link{id="c1" title="Card" image="fig.png"}
            Hover card body text.
            :::
            """);

        var applied = (await apply.HandleAsync(new ApplySpecCommand(null, mdPath, WorkingDirectory: _fx.WorkDir))).Value;

        var compFull = await inspect.HandleAsync(new InspectEntityQuery($"{applied.TargetId}#c1", InspectViewMode.Full));
        Assert.True(compFull.IsSuccess);
        Assert.NotNull(compFull.Value.Component?.Links);
        var card = Assert.Single(compFull.Value.Component!.Links!);
        Assert.Equal("c1", card.Id);
        Assert.Equal("Card", card.Title);
        Assert.Equal("Hover card body text.", card.Text);
        Assert.NotNull(card.ImagePath);
        Assert.StartsWith("assets/fig_", card.ImagePath!, StringComparison.Ordinal);

        var sessionFull = await inspect.HandleAsync(new InspectEntityQuery(applied.TargetId, InspectViewMode.Full));
        Assert.True(sessionFull.IsSuccess);
        Assert.NotNull(sessionFull.Value.Components?[0].Links);
        Assert.Single(sessionFull.Value.Components![0].Links!);

        var compOutline = await inspect.HandleAsync(new InspectEntityQuery($"{applied.TargetId}#c1", InspectViewMode.Outline));
        Assert.True(compOutline.IsSuccess);
        Assert.Null(compOutline.Value.Component?.Links);
    }

    [Fact]
    public async Task AddressResolver_ResolvesExternalWorkspaceFolderPath()
    {
        var externalParent = _fx.SubDir("external_unregistered_parent");
        string storageDir;
        string sessionId;

        using (var sp1 = _fx.CreateProvider("appdata_creator"))
        using (var scope1 = sp1.CreateScope())
        {
            var apply = scope1.ServiceProvider.GetRequiredService<ApplySpecHandler>();
            var applied = await apply.HandleAsync(new ApplySpecCommand(
                null,
                null,
                """{"kind":"session","name":"External","components":[{"kind":"article","ref":"intro","title":"Intro","blocks":[{"kind":"text","ref":"b1","text":"Hello external"}]}]}""",
                WorkingDirectory: externalParent));
            Assert.True(applied.IsSuccess);
            storageDir = applied.Value.StorageDirectory;
            sessionId = applied.Value.TargetId;
        }

        // Resolve using a completely fresh app_settings.db that has never run mby ls
        using var sp2 = _fx.CreateProvider("appdata_fresh_resolver");
        using var scope2 = sp2.CreateScope();
        var resolver = scope2.ServiceProvider.GetRequiredService<EntityAddressResolver>();

        var directRes = await resolver.ResolveAsync(storageDir);
        Assert.True(directRes.IsSuccess, directRes.Error?.Description);
        Assert.Equal("session", directRes.Value.EntityKind);
        Assert.Equal(sessionId, directRes.Value.WorkspaceId);

        var subRes = await resolver.ResolveAsync($"{storageDir}#c1.b1");
        Assert.True(subRes.IsSuccess, subRes.Error?.Description);
        Assert.Equal("articleBlock", subRes.Value.EntityKind);
        Assert.Equal("Hello external", subRes.Value.ArticleBlock?.TextContent);

        var refRes = await resolver.ResolveAsync($"{storageDir}#b1");
        Assert.True(refRes.IsSuccess, refRes.Error?.Description);
        Assert.Equal("articleBlock", refRes.Value.EntityKind);

        // Also resolve bare unregistered folder name + #subPath directly from CWD
        using var sp3 = _fx.CreateProvider("appdata_fresh_cwd_bare", services =>
        {
            services.AddSingleton<IWorkingDirectoryProvider>(new FixedWorkingDirectoryProvider(externalParent));
        });
        using var scope3 = sp3.CreateScope();
        var cwdResolver = scope3.ServiceProvider.GetRequiredService<EntityAddressResolver>();
        var bareSubRes = await cwdResolver.ResolveAsync($"{sessionId}#c1.b1");
        Assert.True(bareSubRes.IsSuccess, bareSubRes.Error?.Description);
        Assert.Equal("articleBlock", bareSubRes.Value.EntityKind);
        Assert.Equal("Hello external", bareSubRes.Value.ArticleBlock?.TextContent);
    }

    [Fact]
    public async Task GetSpecView_RoundTripsAll6BlocksLinksAnd12QuestionTypes_ThroughStrictApply()
    {
        using var sp = _fx.CreateProvider("appdata_spec_rt");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();

        var srcDir = _fx.SubDir("spec_rt_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig1.png"));
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig2.png"), [0x01, 0x02]);

        var masterSpecPath = await WriteTempJsonAsync(srcDir, "master.json", """
        {
          "kind": "session",
          "name": "Master Round-Trip",
          "description": "All 6 blocks, links, overrides, and 12 question types.",
          "components": [
            {
              "kind": "article",
              "ref": "art-all",
              "title": "All Blocks Article",
              "layoutOverrides": { "blockSpacingPx": "24" },
              "links": [
                { "id": "card1", "title": "Card One", "text": "Hover note.", "imagePath": "fig1.png" }
              ],
              "blocks": [
                { "kind": "text", "ref": "blk-txt", "text": "Intro with [card](mby://card/card1)." },
                { "kind": "image", "ref": "blk-img", "imagePath": "fig1.png", "caption": "Fig 1", "alt": "Figure 1" },
                { "kind": "callout", "ref": "blk-cal", "tone": "clinical", "title": "Clinical Pearl", "text": "Key detail." },
                { "kind": "table", "ref": "blk-tbl", "title": "Comparison", "caption": "Cap", "stickyFirstCol": true, "hideableCols": [2], "headers": ["Feature", "A", "B"], "alignments": ["left", "center", "right"], "rows": [["R1", "V1", "V2"]], "footnote": "Foot" },
                { "kind": "diagram-explore", "ref": "blk-diag", "imagePath": "fig2.png", "title": "Diagram", "caption": "Diag Cap", "alt": "Diag Alt", "hotspots": [{ "label": "Zone A", "note": "Zone A note", "rect": [0.1, 0.2, 0.3, 0.15] }] },
                { "kind": "reveal", "ref": "blk-rev", "title": "Steps", "showFirst": true, "steps": [{ "heading": "S1", "body": "Body 1" }, { "heading": "S2", "body": "Body 2" }] }
              ]
            },
            {
              "kind": "quiz",
              "ref": "quiz-all",
              "title": "All 12 Questions",
              "quizType": "mixed",
              "layoutOverrides": { "optionColumns": "2" },
              "questions": [
                { "type": "mcq", "ref": "q-mcq", "text": "Select primes:", "imagePath": "fig1.png", "explanation": "2 and 3.", "isMultiCorrect": true, "maxOptions": 4, "layoutOverrides": { "optionColumns": "1" }, "options": [{ "index": 1, "text": "2", "isCorrect": true }, { "index": 2, "text": "3", "imagePath": "fig2.png", "isCorrect": true }, { "index": 3, "text": "4", "isCorrect": false }] },
                { "type": "flashcard", "ref": "q-fc", "front": { "text": "Front?", "imagePath": "fig1.png", "position": "aboveImage" }, "back": { "text": "Back!", "position": "belowImage" }, "layoutOverrides": { "cardMaxWidthPx": "720" } },
                { "type": "fill-blank", "ref": "q-fb", "text": "Enzyme: {{PFK-1 | phosphofructokinase-1}}.", "fuzzy": true, "caseSensitive": false, "placeholder": "Type...", "explanation": "Committed step." },
                { "type": "assertion-reason", "ref": "q-ar", "assertion": "A stmt", "reason": "R stmt", "answer": "both-no-explain", "scheme": "4opt", "explanation": "AR exp." },
                { "type": "spot-error", "ref": "q-se", "text": "In [alkalosis], pH is [~~low~~ -> high | elevated].", "imagePath": "fig1.png", "requireFix": true, "allowNoError": true, "noError": false, "explanation": "SE exp." },
                { "type": "diff", "ref": "q-diff", "mode": "fill", "title": "Compare", "bank": true, "fuzzy": true, "caseSensitive": false, "allowNoError": false, "placeholder": "...", "headers": ["Prop", "X", "Y"], "alignments": ["left", "center", "right"], "rows": [["Row1", "{{Ans | Alt}}", "Other"]], "explanation": "Diff exp." },
                { "type": "mechanism", "ref": "q-mech", "title": "Pathway", "steps": [{ "orderGroup": 1, "text": "Step 1" }, { "orderGroup": 2, "text": "Step 2A" }, { "orderGroup": 2, "text": "Step 2B" }, { "isDistractor": true, "text": "Decoy" }], "explanation": "Mech exp." },
                { "type": "relabel", "ref": "q-rel", "imagePath": "fig2.png", "prompt": "Label parts", "mode": "bank", "style": "blur", "boxes": [{ "label": "Part A", "aliases": ["PA"], "rect": [0.1, 0.2, 0.2, 0.1] }], "distractors": ["Decoy"], "fuzzy": true, "caseSensitive": false, "explanation": "Rel exp." },
                { "type": "classify", "ref": "q-cls", "title": "Sort", "buckets": ["Loop", "Thiazide"], "items": [{ "bucket": "Loop", "text": "Furosemide" }, { "bucket": "Thiazide", "text": "HCTZ" }], "explanation": "Cls exp." },
                { "type": "match", "ref": "q-mat", "title": "Match", "pairs": [{ "left": "A1", "right": "Gq" }, { "left": "B2", "right": "Gs" }], "distractors": ["Gi"], "explanation": "Mat exp." },
                { "type": "numeric", "ref": "q-num", "prompt": "MAP?", "answer": 93.3, "tolerance": 5, "toleranceMode": "percent", "unit": "mmHg", "placeholder": "0", "imagePath": "fig1.png", "explanation": "Num exp." },
                {
                  "type": "case",
                  "ref": "q-case",
                  "title": "Clinical Vignette",
                  "stem": "Patient presents with HTN.",
                  "imagePath": "fig1.png",
                  "caption": "Vignette fig",
                  "explanation": "Case exp.",
                  "questions": [
                    { "type": "mcq", "text": "Sub MCQ?", "imagePath": "fig2.png", "options": [{ "text": "Yes", "isCorrect": true }, { "text": "No", "isCorrect": false }] },
                    { "type": "flashcard", "front": { "text": "Sub FC front" }, "back": { "text": "Sub FC back" } },
                    { "type": "numeric", "prompt": "Sub numeric?", "answer": 42, "tolerance": 2, "toleranceMode": "absolute", "unit": "U" }
                  ]
                }
              ]
            }
          ]
        }
        """);

        var applied = (await apply.HandleAsync(new ApplySpecCommand(null, masterSpecPath, WorkingDirectory: _fx.WorkDir))).Value;
        var specRes = await inspect.HandleAsync(new InspectEntityQuery(applied.TargetId, InspectViewMode.Spec));
        Assert.True(specRes.IsSuccess, specRes.Error?.Description);
        Assert.Equal("spec", specRes.Value.View);
        Assert.NotNull(specRes.Value.Spec);
        Assert.NotNull(specRes.Value.IdMap);

        var specJson = specRes.Value.Spec!.ToJsonString();
        Assert.DoesNotContain("payloadJson", specJson, StringComparison.OrdinalIgnoreCase);

        // Verify idMap mirrors spec 1:1
        var idMap = specRes.Value.IdMap!;
        Assert.Equal(applied.TargetId, idMap.Id);
        Assert.Equal(2, idMap.Components!.Count);
        Assert.Equal("c1", idMap.Components[0].Path);
        Assert.Equal("art-all", idMap.Components[0].Ref);
        Assert.Equal(6, idMap.Components[0].Blocks!.Count);
        Assert.Equal("c1.b1", idMap.Components[0].Blocks![0].Path);
        Assert.Equal("blk-txt", idMap.Components[0].Blocks![0].Ref);
        Assert.Equal("c2", idMap.Components[1].Path);
        Assert.Equal(12, idMap.Components[1].Questions!.Count);
        Assert.Equal("c2.q12", idMap.Components[1].Questions![11].Path);
        Assert.Equal("q-case", idMap.Components[1].Questions![11].Ref);

        // Round-trip directly into strict ApplySpecHandler (JsonUnmappedMemberHandling.Disallow)
        var rtSpecPath = await WriteTempJsonAsync(applied.StorageDirectory, "roundtrip_spec.json", specJson);
        var rtWorkDir = _fx.SubDir("spec_rt_out");
        var rtApplied = await apply.HandleAsync(new ApplySpecCommand(null, rtSpecPath, WorkingDirectory: rtWorkDir));
        Assert.True(rtApplied.IsSuccess, rtApplied.Error?.Description);

        var rtLint = await lint.HandleAsync(new LintCommand(rtApplied.Value.TargetId));
        Assert.True(rtLint.IsSuccess);
        Assert.True(rtLint.Value.IsClean, string.Join("; ", rtLint.Value.Issues.Select(i => $"{i.Code}: {i.Message}")));
        Assert.Equal(0, rtLint.Value.ErrorCount);
    }

    [Fact]
    public async Task GetSpecView_WorksForSubEntities()
    {
        using var sp = _fx.CreateProvider("appdata_spec_sub");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();

        var srcDir = _fx.SubDir("spec_sub_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "img.png"));

        var sessionSpec = await WriteTempJsonAsync(srcDir, "sub.json", """
        {
          "kind": "session",
          "name": "Sub Test",
          "components": [
            {
              "kind": "article",
              "ref": "a1",
              "title": "Art",
              "blocks": [
                { "kind": "text", "text": "T1" },
                { "kind": "image", "imagePath": "img.png" },
                { "kind": "table", "ref": "tbl", "title": "Tbl", "headers": ["H1", "H2"], "rows": [["A", "B"]] }
              ]
            },
            {
              "kind": "quiz",
              "ref": "qz",
              "quizType": "mixed",
              "questions": [
                { "type": "mcq", "text": "Q1", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] },
                { "type": "mcq", "text": "Q2", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] },
                { "type": "mcq", "text": "Q3", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] },
                { "type": "mcq", "text": "Q4", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] },
                { "type": "relabel", "ref": "rel5", "imagePath": "img.png", "mode": "bank", "style": "solid", "boxes": [{ "label": "L1", "rect": [0.1, 0.1, 0.2, 0.2] }] }
              ]
            }
          ]
        }
        """);
        var ws = (await apply.HandleAsync(new ApplySpecCommand(null, sessionSpec, WorkingDirectory: _fx.WorkDir))).Value;

        var c1 = await inspect.HandleAsync(new InspectEntityQuery($"{ws.TargetId}#c1", InspectViewMode.Spec));
        Assert.True(c1.IsSuccess);
        Assert.Equal("article", c1.Value.Spec!["kind"]!.GetValue<string>());
        Assert.Equal("c1", c1.Value.IdMap!.Path);
        Assert.Equal(3, c1.Value.IdMap.Blocks!.Count);

        var b3 = await inspect.HandleAsync(new InspectEntityQuery($"{ws.TargetId}#c1.b3", InspectViewMode.Spec));
        Assert.True(b3.IsSuccess);
        Assert.Equal("table", b3.Value.Spec!["kind"]!.GetValue<string>());
        Assert.Equal("c1.b3", b3.Value.IdMap!.Path);
        Assert.DoesNotContain("payloadJson", b3.Value.Spec.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var q5 = await inspect.HandleAsync(new InspectEntityQuery($"{ws.TargetId}#c2.q5", InspectViewMode.Spec));
        Assert.True(q5.IsSuccess);
        Assert.Equal("relabel", q5.Value.Spec!["type"]!.GetValue<string>());
        Assert.Equal("c2.q5", q5.Value.IdMap!.Path);
        Assert.DoesNotContain("payloadJson", q5.Value.Spec.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        // Standalone Set_*
        var quizWs = (await apply.HandleAsync(new ApplySpecCommand(
            null,
            null,
            """{"kind":"quiz","quizType":"mcq","name":"Standalone","questions":[{"type":"mcq","ref":"sq1","text":"Q?","options":[{"text":"1","isCorrect":true},{"text":"2"}]}]}""",
            WorkingDirectory: _fx.WorkDir))).Value;

        var setSpec = await inspect.HandleAsync(new InspectEntityQuery(quizWs.TargetId, InspectViewMode.Spec));
        Assert.True(setSpec.IsSuccess);
        Assert.Equal("quiz", setSpec.Value.Spec!["kind"]!.GetValue<string>());
        Assert.Single(setSpec.Value.IdMap!.Questions!);
        Assert.Equal("q1", setSpec.Value.IdMap.Questions![0].Path);
    }

    [Fact]
    public async Task PatchComponent_UpdatesReplacesAndClearsLinks_AndRejectsLinksOnQuiz()
    {
        using var sp = _fx.CreateProvider("appdata_patch_links");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();

        var srcDir = _fx.SubDir("patch_links_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig.png"));
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig2.png"), [0x09]);

        var specPath = await WriteTempJsonAsync(srcDir, "session.json", """
        {
          "kind": "session",
          "name": "Links Patch Session",
          "components": [
            {
              "kind": "article",
              "ref": "art1",
              "title": "Art",
              "blocks": [{ "kind": "text", "text": "See [card](mby://card/c1)." }],
              "links": [{ "id": "old", "title": "Old", "text": "Old body" }]
            },
            {
              "kind": "quiz",
              "ref": "qz1",
              "quizType": "mcq",
              "questions": [{ "type": "mcq", "text": "Q?", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] }]
            }
          ]
        }
        """);

        var ws = (await apply.HandleAsync(new ApplySpecCommand(null, specPath, WorkingDirectory: _fx.WorkDir))).Value;

        // Replace links with a new card referencing fig.png
        var patchFile = await WriteTempJsonAsync(srcDir, "patch_links.json", """
        {
          "links": [
            { "id": "c1", "title": "Updated", "text": "Body", "imagePath": "fig.png" }
          ]
        }
        """);
        var replaceRes = await patch.HandleAsync(new PatchEntityCommand($"{ws.TargetId}#c1", JsonFilePath: patchFile));
        Assert.True(replaceRes.IsSuccess, replaceRes.Error?.Description);

        var afterReplace = await inspect.HandleAsync(new InspectEntityQuery($"{ws.TargetId}#c1", InspectViewMode.Full));
        Assert.True(afterReplace.IsSuccess);
        var links = Assert.Single(afterReplace.Value.Component!.Links!);
        Assert.Equal("c1", links.Id);
        Assert.Equal("Updated", links.Title);
        Assert.Equal("Body", links.Text);
        Assert.StartsWith("assets/fig_", links.ImagePath);

        // Duplicate link IDs are rejected
        var dupRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{ws.TargetId}#c1",
            InlineJson: """{"links":[{"id":"c1","text":"A"},{"id":"C1","text":"B"}]}"""));
        Assert.False(dupRes.IsSuccess);

        // Clear all link cards with []
        var clearRes = await patch.HandleAsync(new PatchEntityCommand($"{ws.TargetId}#c1", InlineJson: """{"links":[]}"""));
        Assert.True(clearRes.IsSuccess, clearRes.Error?.Description);

        var afterClear = await inspect.HandleAsync(new InspectEntityQuery($"{ws.TargetId}#c1", InspectViewMode.Full));
        Assert.True(afterClear.IsSuccess);
        Assert.Null(afterClear.Value.Component!.Links);

        // Reject links on quiz component
        var quizRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{ws.TargetId}#c2",
            InlineJson: """{"links":[{"id":"c1","text":"Not allowed"}]}"""));
        Assert.False(quizRes.IsSuccess);
        Assert.Contains(quizRes.Error!.Code, new[] { "Patch.InvalidValue", "Component.WrongKind" });
    }

    [Fact]
    public async Task PatchCaseQuestion_AcceptsDeclarativeSubQuestions_AndCopiesImages()
    {
        using var sp = _fx.CreateProvider("appdata_patch_case");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();

        var srcDir = _fx.SubDir("patch_case_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig1.png"));
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig2.png"), [0x11, 0x22]);

        var specPath = await WriteTempJsonAsync(srcDir, "case_session.json", """
        {
          "kind": "quiz",
          "quizType": "mixed",
          "name": "Case Patch Test",
          "questions": [
            {
              "type": "case",
              "ref": "q-case",
              "title": "Case",
              "stem": "Initial stem.",
              "questions": [
                { "type": "mcq", "text": "Old1?", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] },
                { "type": "mcq", "text": "Old2?", "options": [{ "text": "C", "isCorrect": true }, { "text": "D" }] }
              ]
            }
          ]
        }
        """);
        var ws = (await apply.HandleAsync(new ApplySpecCommand(null, specPath, WorkingDirectory: _fx.WorkDir))).Value;

        var patchPath = await WriteTempJsonAsync(srcDir, "patch_case.json", """
        {
          "questions": [
            {
              "type": "mcq",
              "text": "New?",
              "imagePath": "fig2.png",
              "options": [
                { "text": "Opt 1", "isCorrect": true },
                { "text": "Opt 2", "isCorrect": false }
              ]
            },
            {
              "type": "numeric",
              "text": "Val?",
              "answer": 42,
              "tolerance": "5%"
            }
          ]
        }
        """);

        var patchRes = await patch.HandleAsync(new PatchEntityCommand($"{ws.TargetId}#q-case", JsonFilePath: patchPath));
        Assert.True(patchRes.IsSuccess, patchRes.Error?.Description);

        var specView = await inspect.HandleAsync(new InspectEntityQuery($"{ws.TargetId}#q-case", InspectViewMode.Spec));
        Assert.True(specView.IsSuccess);
        var qs = specView.Value.Spec!["questions"]!.AsArray();
        Assert.Equal(2, qs.Count);
        Assert.Equal("mcq", qs[0]!["type"]!.GetValue<string>());
        Assert.Equal("New?", qs[0]!["text"]!.GetValue<string>());
        var copiedImg = qs[0]!["imagePath"]!.GetValue<string>();
        Assert.StartsWith("assets/fig2_", copiedImg);
        Assert.True(File.Exists(Path.Combine(ws.StorageDirectory, copiedImg.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Equal("numeric", qs[1]!["type"]!.GetValue<string>());
        Assert.Equal("Val?", qs[1]!["prompt"]!.GetValue<string>());
        Assert.Equal(42, qs[1]!["answer"]!.GetValue<double>());
        Assert.Equal(5, qs[1]!["tolerance"]!.GetValue<double>());
        Assert.Equal("percent", qs[1]!["toleranceMode"]!.GetValue<string>());

        var lintRes = await lint.HandleAsync(new LintCommand(ws.TargetId));
        Assert.True(lintRes.IsSuccess);
        Assert.True(lintRes.Value.IsClean, string.Join("; ", lintRes.Value.Issues.Select(i => $"{i.Code}: {i.Message}")));

        // Nested case must be rejected with Case.NestedCaseNotAllowed
        var nestedRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{ws.TargetId}#q-case",
            InlineJson: """
            {
              "questions": [
                { "type": "case", "stem": "Nested", "questions": [{ "type": "numeric", "text": "A", "answer": 1 }, { "type": "numeric", "text": "B", "answer": 2 }] },
                { "type": "numeric", "text": "Valid", "answer": 3 }
              ]
            }
            """));
        Assert.False(nestedRes.IsSuccess);
        Assert.Equal("Case.NestedCaseNotAllowed", nestedRes.Error!.Code);
        Assert.Equal("questions[0]", nestedRes.Error.Path);

        // Unmapped field inside a sub-question should strip synthetic wrapper prefix from error path
        var badSubFieldRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{ws.TargetId}#q-case",
            InlineJson: """
            {
              "questions": [
                { "type": "numeric", "text": "A", "answer": 1, "unknownSubProp": 123 },
                { "type": "numeric", "text": "B", "answer": 2 }
              ]
            }
            """));
        Assert.False(badSubFieldRes.IsSuccess);
        Assert.Equal("Json.ParseError", badSubFieldRes.Error!.Code);
        Assert.DoesNotContain("questions[0].questions", badSubFieldRes.Error.Path ?? string.Empty, StringComparison.Ordinal);
        Assert.StartsWith("$.questions[0]", badSubFieldRes.Error.Path ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patch_PreservesExistingWorkspaceAssetPath_FromDifferentCwd_WhileBlockingTraversal()
    {
        var unrelatedCwd = _fx.SubDir("unrelated_cwd");
        var outsideDir = _fx.SubDir("outside_dir");
        await TestAssets.WritePngAsync(Path.Combine(outsideDir, "outside.png"), [0x44]);

        using var sp = _fx.CreateProvider("appdata_patch_asset_cwd", services =>
        {
            services.AddSingleton<IWorkingDirectoryProvider>(new FixedWorkingDirectoryProvider(unrelatedCwd));
        });
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();

        var srcDir = _fx.SubDir("asset_cwd_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig.png"), [0x77, 0x88]);
        var specPath = await WriteTempJsonAsync(srcDir, "s.json", """
        {
          "kind": "session",
          "name": "Asset CWD Test",
          "components": [
            {
              "kind": "article",
              "blocks": [
                { "kind": "text", "text": "Intro" },
                { "kind": "image", "imagePath": "fig.png", "caption": "Orig" }
              ]
            }
          ]
        }
        """);

        var externalWorkDir = _fx.SubDir("ext_ws_root");
        var ws = (await apply.HandleAsync(new ApplySpecCommand(null, specPath, WorkingDirectory: externalWorkDir))).Value;

        var origBlock = (await inspect.HandleAsync(new InspectEntityQuery($"{ws.TargetId}#c1.b2", InspectViewMode.Full))).Value.ArticleBlock!;
        var storedAssetPath = origBlock.ImagePath!;
        Assert.StartsWith("assets/fig_", storedAssetPath);

        var assetFilesBefore = Directory.GetFiles(Path.Combine(ws.StorageDirectory, "assets"));

        // Patching with existing workspace asset path from unrelated CWD succeeds without recopying
        var keepRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{ws.TargetId}#c1.b2",
            SetPairs: [$"imagePath={storedAssetPath}", "caption=Updated"]));
        Assert.True(keepRes.IsSuccess, keepRes.Error?.Description);

        var updatedBlock = (await inspect.HandleAsync(new InspectEntityQuery($"{ws.TargetId}#c1.b2", InspectViewMode.Full))).Value.ArticleBlock!;
        Assert.Equal(storedAssetPath, updatedBlock.ImagePath);
        Assert.Equal("Updated", updatedBlock.Caption);
        Assert.Equal(assetFilesBefore.Length, Directory.GetFiles(Path.Combine(ws.StorageDirectory, "assets")).Length);

        // Path traversal must still be blocked with Asset.InvalidPath
        var traversalRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{ws.TargetId}#c1.b2",
            SetPairs: ["imagePath=../outside.png"]));
        Assert.False(traversalRes.IsSuccess);
        Assert.Equal("Asset.InvalidPath", traversalRes.Error!.Code);

        var assetsEscapeRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{ws.TargetId}#c1.b2",
            SetPairs: [$"imagePath=assets/../{ws.TargetId}.db"]));
        Assert.False(assetsEscapeRes.IsSuccess);
        Assert.Equal("Asset.InvalidPath", assetsEscapeRes.Error!.Code);
    }

    [Fact]
    public async Task Batch_ResolvesRelativeImagePaths_AgainstBatchFileDirectory_AndRollsBackOnError()
    {
        var unrelatedCwd = _fx.SubDir("batch_unrelated_cwd");
        using var sp = _fx.CreateProvider("appdata_batch_basedir", services =>
        {
            services.AddSingleton<IWorkingDirectoryProvider>(new FixedWorkingDirectoryProvider(unrelatedCwd));
        });
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var batch = scope.ServiceProvider.GetRequiredService<BatchOperationsHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();

        var ws = (await apply.HandleAsync(new ApplySpecCommand(
            null,
            null,
            """
            {
              "kind": "session",
              "name": "Batch BaseDir Test",
              "components": [
                {
                  "kind": "quiz",
                  "ref": "qz",
                  "quizType": "mcq",
                  "questions": [
                    { "type": "mcq", "ref": "q-orig", "text": "Orig?", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] }
                  ]
                }
              ]
            }
            """,
            WorkingDirectory: _fx.WorkDir))).Value;

        var stagingDir = _fx.SubDir("batch_staging");
        await TestAssets.WritePngAsync(Path.Combine(stagingDir, "new_fig.png"), [0x55, 0x66]);

        // First test atomic rollback when step 3 fails
        var failingBatchPath = await WriteTempJsonAsync(stagingDir, "batch_fail.json", $$"""
        [
          {
            "op": "apply",
            "addr": "{{ws.TargetId}}",
            "spec": {
              "kind": "article",
              "ref": "new-art",
              "title": "Added Article",
              "blocks": [
                { "kind": "image", "imagePath": "new_fig.png", "caption": "Staged" }
              ]
            }
          },
          {
            "op": "patch",
            "addr": "{{ws.TargetId}}#q-orig",
            "data": { "imagePath": "new_fig.png", "text": "Updated?" }
          },
          {
            "op": "patch",
            "addr": "{{ws.TargetId}}#nonexistent-ref",
            "data": { "text": "Boom" }
          }
        ]
        """);

        var failRes = await batch.HandleAsync(new BatchOperationsCommand(JsonFilePath: failingBatchPath));
        Assert.False(failRes.IsSuccess);

        var afterRollback = (await inspect.HandleAsync(new InspectEntityQuery(ws.TargetId, InspectViewMode.Full))).Value;
        Assert.Single(afterRollback.Components!);
        Assert.Equal("Orig?", afterRollback.Components![0].Questions![0].Text);
        Assert.Null(afterRollback.Components[0].Questions![0].ImagePath);
        var assetsDir = Path.Combine(ws.StorageDirectory, "assets");
        Assert.True(!Directory.Exists(assetsDir) || Directory.GetFiles(assetsDir).Length == 0);

        // Now run the valid 2-step batch from the staging directory
        var okBatchPath = await WriteTempJsonAsync(stagingDir, "batch_ok.json", $$"""
        [
          {
            "op": "apply",
            "addr": "{{ws.TargetId}}",
            "spec": {
              "kind": "article",
              "ref": "new-art",
              "title": "Added Article",
              "blocks": [
                { "kind": "image", "imagePath": "new_fig.png", "caption": "Staged" }
              ]
            }
          },
          {
            "op": "patch",
            "addr": "{{ws.TargetId}}#q-orig",
            "data": { "imagePath": "new_fig.png", "text": "Updated?" }
          }
        ]
        """);

        var okRes = await batch.HandleAsync(new BatchOperationsCommand(JsonFilePath: okBatchPath));
        Assert.True(okRes.IsSuccess, okRes.Error?.Description);

        var afterOk = (await inspect.HandleAsync(new InspectEntityQuery(ws.TargetId, InspectViewMode.Full))).Value;
        Assert.Equal(2, afterOk.Components!.Count);
        Assert.StartsWith("assets/new_fig_", afterOk.Components[1].ArticleBlocks![0].ImagePath);
        Assert.Equal("Updated?", afterOk.Components[0].Questions![0].Text);
        Assert.StartsWith("assets/new_fig_", afterOk.Components[0].Questions![0].ImagePath);

        var lintRes = await lint.HandleAsync(new LintCommand(ws.TargetId));
        Assert.True(lintRes.IsSuccess);
        Assert.True(lintRes.Value.IsClean, string.Join("; ", lintRes.Value.Issues.Select(i => $"{i.Code}: {i.Message}")));
    }

    private sealed class FixedWorkingDirectoryProvider(string currentDir) : IWorkingDirectoryProvider
    {
        public string GetCurrentDirectory() => currentDir;
        public string NormalizeDirectoryPath(string? explicitPath) =>
            string.IsNullOrWhiteSpace(explicitPath) ? currentDir : Path.GetFullPath(explicitPath);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool FileExists(string path) => File.Exists(path);
    }

    private static async Task<string> WriteTempJsonAsync(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }
}
