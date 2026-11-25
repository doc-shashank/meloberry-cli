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
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#q-diff", InlineJson: """{"title":"Updated Diff","bank":false,"rows":[["Resp","{{High PaCO2}}","Renal"]]}"""))).IsSuccess);
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
    }

    private static async Task<string> WriteTempJsonAsync(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }
}
