using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Analysis;
using Meloberry.Domain.Common;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;
using Meloberry.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class Phase2HardeningTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_phase2a_unit_{Guid.NewGuid():N}");

    public Phase2HardeningTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private ServiceProvider CreateProvider(string subDir = "appdata")
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(_tempRoot, subDir));
        return services.BuildServiceProvider();
    }

    private static Task WriteDummyPngAsync(string path) =>
        File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    [Fact]
    public void Phase1PayloadParsers_HandleKatexDelimitersPipesAndBackslashes()
    {
        // Fill-blank: KaTeX braces (}}) and pipes ($P(A|B)$) + escaped \| inside blanks
        var fbRes = Phase1PayloadRules.ParseFillBlankTemplate(
            @"The value of $\frac{1}{\sqrt{2}}$ and $\text{P}_{\text{O}_2}$ is {{0.707 | $P(A|B)$ | a\|b}}.");
        Assert.True(fbRes.IsSuccess, fbRes.Error?.Description);
        var slot = Assert.Single(fbRes.Value);
        Assert.Equal(["0.707", "$P(A|B)$", "a|b"], slot.Answers);

        // Fill-blank: unmatched prose }} outside math still fails
        var fbBad = Phase1PayloadRules.ParseFillBlankTemplate("Prose stray }} and {{ok}}.");
        Assert.False(fbBad.IsSuccess);
        Assert.Equal("FillBlank.UnbalancedDelimiters", fbBad.Error!.Code);

        // Spot-error: KaTeX brackets outside and inside candidate spans + KaTeX pipe and \| in corrections
        var seRes = Phase1PayloadRules.ParseSpotErrorTemplate(
            @"In [acidosis], $[\text{Ca}^{2+}]$ and [$[\text{H}^+]$] give [~~$\sqrt[3]{8}$~~ -> $\sqrt[2]{4}$ | $P(A|B)$ | a\|b].");
        Assert.True(seRes.IsSuccess, seRes.Error?.Description);
        var candidates = seRes.Value.Where(s => s.IsCandidate).ToList();
        Assert.Equal(3, candidates.Count);
        Assert.Equal(@"$[\text{H}^+]$", candidates[1].Text);
        var errSeg = Assert.Single(candidates, s => s.IsError);
        Assert.Equal(@"$\sqrt[3]{8}$", errSeg.Text);
        Assert.Equal([@"$\sqrt[2]{4}$", "$P(A|B)$", "a|b"], errSeg.Corrections);

        // Spot-error: unmatched prose ] outside math still fails
        var seBad = Phase1PayloadRules.ParseSpotErrorTemplate("Stray ] then [one] and [~~two~~ -> three].");
        Assert.False(seBad.IsSuccess);
        Assert.Equal("SpotError.UnbalancedBrackets", seBad.Error!.Code);

        // Markdown table SplitPipeRow: preserves | inside {{...}}, $...$, `...`, and handles \\| trailing pipe
        var mdTable = """
            | Metric | Formula | Blank | EscapedSlash |
            | :--- | :---: | :--- | ---: |
            | Cond | $P(A|B)$ | {{alpha | beta}} | `a | b` \\|
            """;
        var tblRes = Phase1PayloadRules.ParseMarkdownTable(mdTable);
        Assert.True(tblRes.IsSuccess, tblRes.Error?.Description);
        Assert.Equal(4, tblRes.Value.Headers.Count);
        var row = Assert.Single(tblRes.Value.Rows);
        Assert.Equal(4, row.Count);
        Assert.Equal("$P(A|B)$", row[1]);
        Assert.Equal("{{alpha | beta}}", row[2]);
        Assert.Equal(@"`a | b` \\", row[3]);
    }

    [Fact]
    public async Task MarkdownParser_HandlesKatexAttrsUnclosedFencesImagesCalloutRefAndSpotError()
    {
        using var sp = CreateProvider("appdata_md");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var loader = scope.ServiceProvider.GetRequiredService<IDeclarativeSpecLoader>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var schema = scope.ServiceProvider.GetRequiredService<GetSchemaHandler>();
        var wsDir = Path.Combine(_tempRoot, "ws_md");

        // 1. Quoted KaTeX braces in directive attrs + callout ref (full and shorthand) + standalone image guards
        var validArticleMd = """
            ---
            title: Ion Channels
            ---

            ![ECG](ecg.png) showing ST elevation (lead II)

            ![a](a.png) ![b](b.png)

            :::callout{tone="pearl" ref="co-pump" title="$\text{Na}^+$ Pump"}
            Maintains resting membrane potential.
            :::

            :::pearl{ref="co-short" title="$\text{K}^+$ Efflux"}
            Drives repolarization.
            :::

            :::table{ref="tbl-ca" title="Serum $\text{Ca}^{2+}$"}
            | Ion | Normal |
            | --- | --- |
            | $\text{Ca}^{2+}$ | 8.5–10.5 mg/dL |
            :::
            """;

        var artRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: validArticleMd, WorkingDirectory: wsDir));
        Assert.True(artRes.IsSuccess, artRes.Error?.Description);
        var loaded = await repo.FindSessionAsync(artRes.Value.TargetId);
        Assert.NotNull(loaded);
        var blocks = loaded!.Session.Components[0].ArticleBlocks;

        // Prose lines with inline images were NOT misparsed as image blocks
        Assert.DoesNotContain(blocks, b => b.Kind == ArticleBlockKind.Image);
        Assert.Contains(blocks, b => b.Kind == ArticleBlockKind.Text && b.TextContent!.Contains("showing ST elevation"));
        Assert.Contains(blocks, b => b.Kind == ArticleBlockKind.Text && b.TextContent!.Contains("![a](a.png) ![b](b.png)"));

        // Callouts preserved ref and KaTeX title
        var coPump = Assert.Single(blocks, b => b.RefKey == "co-pump");
        Assert.Equal(@"$\text{Na}^+$ Pump", coPump.CalloutTitle);
        var coShort = Assert.Single(blocks, b => b.RefKey == "co-short");
        Assert.Equal(@"$\text{K}^+$ Efflux", coShort.CalloutTitle);

        // Table preserved KaTeX title
        var tbl = Assert.Single(blocks, b => b.RefKey == "tbl-ca");
        Assert.True(Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(tbl.PayloadJson, out var tblPayload));
        Assert.Equal(@"Serum $\text{Ca}^{2+}$", tblPayload!.Title);

        // Standalone image with parenthesized filename still parses as image block
        var spacedImgSpec = await loader.LoadFromTextAsync("""
            ---
            title: Spaced Image
            ---

            ![Spaced](assets/fig 1 (a).png "Fig 1")
            """);
        Assert.True(spacedImgSpec.IsSuccess, spacedImgSpec.Error?.Description);
        var imgBlk = Assert.Single(spacedImgSpec.Value.Blocks!);
        Assert.Equal("image", imgBlk.Kind);
        Assert.Equal("assets/fig 1 (a).png", imgBlk.ImagePath);
        Assert.Equal("Fig 1", imgBlk.Caption);
        Assert.Equal("Spaced", imgBlk.Alt);

        // Trailing garbage after closing } on directive header fails
        var trailingRes = await loader.LoadFromTextAsync("""
            :::table{title="x"} extra
            | A | B |
            | --- | --- |
            | 1 | 2 |
            :::
            """);
        Assert.False(trailingRes.IsSuccess);
        Assert.Equal("Markdown.InvalidDirectiveAttr", trailingRes.Error!.Code);

        // Unclosed directive followed by another opening directive fails fast with Markdown.UnclosedDirective
        var unclosedRes = await loader.LoadFromTextAsync("""
            ---
            kind: quiz
            ---

            :::mcq
            First question?
            - [x] Yes
            - [ ] No

            :::fill-blank{ref="fb-1"}
            Answer is {{42}}.
            :::
            """);
        Assert.False(unclosedRes.IsSuccess);
        Assert.Equal("Markdown.UnclosedDirective", unclosedRes.Error!.Code);

        // Assertion-Reason: ratio continuation line 'a:b ratio is 2:1' stays in A:, duplicate A: fails
        var arRatioRes = await loader.LoadFromTextAsync("""
            ---
            kind: quiz
            ---

            :::assertion-reason{answer="both-explain"}
            A: Ventilation matches perfusion:
            a:b ratio is 2:1 in model.
            R: Shunt lowers PaO2.
            :::
            """);
        Assert.True(arRatioRes.IsSuccess, arRatioRes.Error?.Description);
        Assert.True(Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(arRatioRes.Value.Questions![0].PayloadJson, out var arPayload));
        Assert.Contains("a:b ratio is 2:1 in model.", arPayload!.Assertion);

        var arDupRes = await loader.LoadFromTextAsync("""
            ---
            kind: quiz
            ---

            :::assertion-reason{answer="both-explain"}
            A: First assertion.
            A: Duplicate assertion.
            R: Reason.
            :::
            """);
        Assert.False(arDupRes.IsSuccess);
        Assert.Equal("Markdown.InvalidAssertionReason", arDupRes.Error!.Code);

        // SplitRespectingQuotes handles quoted item ending in escaped backslash
        var escapedQuoteRes = await loader.LoadFromTextAsync("""
            ---
            kind: session
            order: ["01-a\\", "02-b"]
            ---
            """);
        Assert.True(escapedQuoteRes.IsSuccess, escapedQuoteRes.Error?.Description);
        Assert.Equal(2, escapedQuoteRes.Value.Order!.Count);
        Assert.Equal(@"01-a\\", escapedQuoteRes.Value.Order[0]);
        Assert.Equal("02-b", escapedQuoteRes.Value.Order[1]);

        // Spot-error rejects explicit noError=true allowNoError=false
        var spotConflict = await loader.LoadFromTextAsync("""
            ---
            kind: quiz
            ---

            :::spot-error{noError=true allowNoError=false}
            In [acidosis], pH is [low].
            :::
            """);
        Assert.False(spotConflict.IsSuccess);
        Assert.Equal("Markdown.InvalidSpotError", spotConflict.Error!.Code);

        // GetSchemaUseCase documents ref on callout in markdown schema
        var schemaRes = await schema.HandleAsync(new GetSchemaQuery(["markdown"]));
        Assert.True(schemaRes.IsSuccess);
        var mdSig = schemaRes.Value.Schemas.Single(s => s.Topic == "markdown").CompactSignature;
        Assert.Contains("ref?", mdSig);
        Assert.Contains(":::callout{", mdSig);
        var calloutLine = mdSig.Split('\n').First(l => l.Contains(":::callout{"));
        Assert.Contains("ref", calloutLine);
    }

    [Fact]
    public async Task PatchLintAndBatch_EnforceStrictValidationAndNestedImageConfinement()
    {
        using var sp = CreateProvider("appdata_plb");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var batch = scope.ServiceProvider.GetRequiredService<BatchOperationsHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var wsDir = Path.Combine(_tempRoot, "ws_plb");

        // Create session with table and quiz with noError spot-error
        var baseSpec = """
            {
              "kind": "session",
              "name": "Validation Target",
              "components": [
                {
                  "kind": "article",
                  "ref": "art1",
                  "blocks": [
                    {
                      "kind": "table",
                      "ref": "tbl1",
                      "headers": ["Col1", "Col2"],
                      "rows": [["A", "B"]]
                    }
                  ]
                },
                {
                  "kind": "quiz",
                  "ref": "qz1",
                  "quizType": "mixed",
                  "questions": [
                    {
                      "type": "spot-error",
                      "ref": "se1",
                      "noError": true,
                      "allowNoError": true,
                      "text": "In [acidosis], pH is [low]."
                    }
                  ]
                }
              ]
            }
            """;
        var created = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: baseSpec, WorkingDirectory: wsDir));
        Assert.True(created.IsSuccess, created.Error?.Description);
        var sid = created.Value.TargetId;

        // Patch: allowNoError=false on existing noError=true spot-error rejected
        var pConflict1 = await patch.HandleAsync(new PatchEntityCommand($"{sid}#se1", SetPairs: ["allowNoError=false"]));
        Assert.False(pConflict1.IsSuccess);
        Assert.Equal("Patch.InvalidValue", pConflict1.Error!.Code);

        // Patch: setting both noError=true and allowNoError=false in one patch rejected
        var pConflict2 = await patch.HandleAsync(new PatchEntityCommand($"{sid}#se1", InlineJson: """{"noError":true,"allowNoError":false}"""));
        Assert.False(pConflict2.IsSuccess);
        Assert.Equal("Patch.InvalidValue", pConflict2.Error!.Code);

        // Patch: empty dotted --set segments rejected
        var pDot1 = await patch.HandleAsync(new PatchEntityCommand($"{sid}#se1", SetPairs: [".foo=bar"]));
        Assert.False(pDot1.IsSuccess);
        Assert.Equal("Patch.InvalidSet", pDot1.Error!.Code);

        var pDot2 = await patch.HandleAsync(new PatchEntityCommand($"{sid}#se1", SetPairs: ["foo.=bar"]));
        Assert.False(pDot2.IsSuccess);
        Assert.Equal("Patch.InvalidSet", pDot2.Error!.Code);

        // Patch: invalid table patches rejected
        var pTblCols = await patch.HandleAsync(new PatchEntityCommand($"{sid}#tbl1", InlineJson: """{"hideableCols":[5]}"""));
        Assert.False(pTblCols.IsSuccess);
        Assert.Equal("Patch.InvalidValue", pTblCols.Error!.Code);

        var pTblBlankHdr = await patch.HandleAsync(new PatchEntityCommand($"{sid}#tbl1", InlineJson: """{"headers":[" ",""]}"""));
        Assert.False(pTblBlankHdr.IsSuccess);
        Assert.Equal("Patch.InvalidValue", pTblBlankHdr.Error!.Code);

        var pTblEmptyRows = await patch.HandleAsync(new PatchEntityCommand($"{sid}#tbl1", InlineJson: """{"rows":[]}"""));
        Assert.False(pTblEmptyRows.IsSuccess);
        Assert.Equal("Patch.InvalidValue", pTblEmptyRows.Error!.Code);

        var pTblAlign = await patch.HandleAsync(new PatchEntityCommand($"{sid}#tbl1", InlineJson: """{"alignments":["left"]}"""));
        Assert.False(pTblAlign.IsSuccess);
        Assert.Equal("Patch.InvalidValue", pTblAlign.Error!.Code);

        // Lint: JSON spot-error with noError=true, allowNoError=false + bad table alignments/whitespace headers/dup hideableCols
        var brokenJson = """
            {
              "kind": "session",
              "name": "Lint Check Session",
              "components": [
                {
                  "kind": "article",
                  "blocks": [
                    {
                      "kind": "table",
                      "headers": ["  ", ""],
                      "alignments": ["diagonal"],
                      "hideableCols": [1, 1],
                      "rows": [["a", "b"]]
                    }
                  ]
                },
                {
                  "kind": "quiz",
                  "quizType": "mixed",
                  "questions": [
                    {
                      "type": "spot-error",
                      "noError": true,
                      "allowNoError": false,
                      "text": "In [acidosis], pH is [low]."
                    }
                  ]
                }
              ]
            }
            """;
        var brokenApplied = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: brokenJson, WorkingDirectory: wsDir));
        Assert.True(brokenApplied.IsSuccess, brokenApplied.Error?.Description);
        var brokenLint = await lint.HandleAsync(new LintCommand(brokenApplied.Value.TargetId));
        Assert.True(brokenLint.IsSuccess);
        Assert.False(brokenLint.Value.IsClean);
        Assert.Contains(brokenLint.Value.Issues, i => i.Code == "Lint.SpotErrorConflictingNoError");
        Assert.Contains(brokenLint.Value.Issues, i => i.Code == "Lint.InvalidTablePayload" && i.Message.Contains("alignment", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(brokenLint.Value.Issues, i => i.Code == "Lint.InvalidTablePayload" && i.Message.Contains("header", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(brokenLint.Value.Issues, i => i.Code == "Lint.InvalidTableHideableCol" && i.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));

        // Lint: Quiz component with null Quiz flagged as Lint.EmptyComponent
        var nullQuizStorage = Path.Combine(wsDir, "Session_20261124_000001_001");
        Directory.CreateDirectory(Path.Combine(nullQuizStorage, "assets"));
        var nullQuizComp = SessionComponent.Rehydrate("Component_20261124_000001_001", "Session_20261124_000001_001", 1, ComponentKind.Quiz, quiz: null, articleBlocks: []);
        var nullQuizSession = StudySession.Rehydrate("Session_20261124_000001_001", nullQuizStorage, nullQuizComp.Id, [nullQuizComp], "Null Quiz Session");
        await repo.CreateSessionAsync(nullQuizSession);
        var nullQuizLint = await lint.HandleAsync(new LintCommand(nullQuizSession.Id));
        Assert.True(nullQuizLint.IsSuccess);
        Assert.False(nullQuizLint.Value.IsClean);
        Assert.Contains(nullQuizLint.Value.Issues, i => i.Code == "Lint.EmptyComponent" && i.TargetId == nullQuizComp.Id);

        // Nested ../assets/diagram.png confinement inside specRoot succeeds; escaping outside specRoot fails
        var specRoot = Path.Combine(_tempRoot, "nested_spec_root");
        var nestedSub = Path.Combine(specRoot, "articles");
        var rootAssets = Path.Combine(specRoot, "assets");
        Directory.CreateDirectory(nestedSub);
        Directory.CreateDirectory(rootAssets);
        await WriteDummyPngAsync(Path.Combine(rootAssets, "diagram.png"));
        await WriteDummyPngAsync(Path.Combine(_tempRoot, "outside_root.png"));

        var nestedServices = new ServiceCollection();
        nestedServices.AddApplication();
        nestedServices.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(_tempRoot, "appdata_nested"));
        var stubLoader = new StubSpecLoader();
        nestedServices.AddSingleton<IDeclarativeSpecLoader>(stubLoader);
        using var nestedSp = nestedServices.BuildServiceProvider();
        var nestedApply = nestedSp.GetRequiredService<ApplySpecHandler>();

        stubLoader.NextSpec = new DeclarativeApplySpec(
            Kind: "session", Name: "Nested Assets", Description: null, Title: null, QuizType: null,
            Components:
            [
                new DeclarativeComponentSpec(
                    "article", "Intro", null,
                    [new DeclarativeArticleBlockSpec("image", null, "../assets/diagram.png")],
                    null, null, SourceDirectory: nestedSub)
            ],
            Blocks: null, Questions: null, LayoutOverrides: null, AssetBaseDirectory: specRoot);
        var okNested = await nestedApply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: "stub", WorkingDirectory: wsDir));
        Assert.True(okNested.IsSuccess, okNested.Error?.Description);

        stubLoader.NextSpec = new DeclarativeApplySpec(
            Kind: "session", Name: "Escape Assets", Description: null, Title: null, QuizType: null,
            Components:
            [
                new DeclarativeComponentSpec(
                    "article", "Escape", null,
                    [new DeclarativeArticleBlockSpec("image", null, "../../outside_root.png")],
                    null, null, SourceDirectory: nestedSub)
            ],
            Blocks: null, Questions: null, LayoutOverrides: null, AssetBaseDirectory: specRoot);
        var badEscape = await nestedApply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: "stub", WorkingDirectory: wsDir));
        Assert.False(badEscape.IsSuccess);
        Assert.Equal("Asset.InvalidPath", badEscape.Error!.Code);

        // Batch: reject conflicting file + spec on apply, and file + data on patch
        var batchApplyConflict = await batch.HandleAsync(new BatchOperationsCommand(InlineJson: """
            [
              { "op": "apply", "file": "a.json", "spec": { "kind": "quiz", "questions": [] } }
            ]
            """));
        Assert.False(batchApplyConflict.IsSuccess);
        Assert.Equal("Batch.InvalidOp", batchApplyConflict.Error!.Code);

        var batchPatchConflict = await batch.HandleAsync(new BatchOperationsCommand(InlineJson: $$"""
            [
              { "op": "patch", "addr": "{{sid}}", "file": "p.json", "data": { "name": "New" } }
            ]
            """));
        Assert.False(batchPatchConflict.IsSuccess);
        Assert.Equal("Batch.InvalidOp", batchPatchConflict.Error!.Code);
    }

    [Fact]
    public async Task MbypkgAndDirectoryLoader_RejectMalformedArchivesAndIgnoreSidecars()
    {
        using var sp = CreateProvider("appdata_pkg_dir");
        using var scope = sp.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var loader = scope.ServiceProvider.GetRequiredService<IDeclarativeSpecLoader>();
        var destDir = Path.Combine(_tempRoot, "pkg_dest");
        Directory.CreateDirectory(destDir);

        async Task<string> WritePkgAsync(string fileName, string kind, string id, string workspaceJson)
        {
            var path = Path.Combine(_tempRoot, fileName);
            using var za = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var w = new StreamWriter(za.CreateEntry("manifest.json").Open()))
                await w.WriteAsync($$"""{"packageVersion":"2.0","kind":"{{kind}}","id":"{{id}}","exportedAtUtc":"2026-01-01T00:00:00Z","assets":[]}""");
            using (var w = new StreamWriter(za.CreateEntry("workspace.json").Open()))
                await w.WriteAsync(workspaceJson);
            return path;
        }

        // 1. Corrupted TableBlockPayload JSON on import
        var badTablePkg = await WritePkgAsync("bad_table.mbypkg", "session", "Session_20261124_010000_001", """
            {
              "id": "Session_20261124_010000_001",
              "components": [
                {
                  "id": "Component_20261124_010000_001",
                  "index": 1,
                  "kind": "Article",
                  "articleBlocks": [
                    { "id": "ArticleBlock_20261124_010000_001", "index": 1, "kind": "Table", "payloadJson": "{not-valid-json" }
                  ]
                }
              ]
            }
            """);
        var badTableRes = await importer.HandleAsync(new ImportPackageCommand(badTablePkg, destDir));
        Assert.False(badTableRes.IsSuccess);
        Assert.Equal("Package.InvalidWorkspace", badTableRes.Error!.Code);

        // 2. Quiz component with null quiz
        var nullQuizPkg = await WritePkgAsync("null_quiz.mbypkg", "session", "Session_20261124_010000_002", """
            {
              "id": "Session_20261124_010000_002",
              "components": [
                { "id": "Component_20261124_010000_002", "index": 1, "kind": "Quiz", "quiz": null, "articleBlocks": [] }
              ]
            }
            """);
        var nullQuizRes = await importer.HandleAsync(new ImportPackageCommand(nullQuizPkg, destDir));
        Assert.False(nullQuizRes.IsSuccess);
        Assert.Equal("Package.InvalidWorkspace", nullQuizRes.Error!.Code);

        // 3. Quiz component with mismatched quiz.id != component.id
        var mismatchQuizPkg = await WritePkgAsync("mismatch_quiz.mbypkg", "session", "Session_20261124_010000_003", """
            {
              "id": "Session_20261124_010000_003",
              "components": [
                {
                  "id": "Component_20261124_010000_003",
                  "index": 1,
                  "kind": "Quiz",
                  "quiz": { "id": "Component_OTHER", "quizType": "Mcq", "questions": [] },
                  "articleBlocks": []
                }
              ]
            }
            """);
        var mismatchRes = await importer.HandleAsync(new ImportPackageCommand(mismatchQuizPkg, destDir));
        Assert.False(mismatchRes.IsSuccess);
        Assert.Equal("Package.InvalidWorkspace", mismatchRes.Error!.Code);

        // 4. Non-quiz component with non-null quiz
        var artWithQuizPkg = await WritePkgAsync("art_quiz.mbypkg", "session", "Session_20261124_010000_004", """
            {
              "id": "Session_20261124_010000_004",
              "components": [
                {
                  "id": "Component_20261124_010000_004",
                  "index": 1,
                  "kind": "Article",
                  "quiz": { "id": "Component_20261124_010000_004", "quizType": "Mcq", "questions": [] },
                  "articleBlocks": []
                }
              ]
            }
            """);
        var artWithQuizRes = await importer.HandleAsync(new ImportPackageCommand(artWithQuizPkg, destDir));
        Assert.False(artWithQuizRes.IsSuccess);
        Assert.Equal("Package.InvalidWorkspace", artWithQuizRes.Error!.Code);

        // 5. Undefined numeric enum string "999" in kind / quizType / position
        var badEnumPkg = await WritePkgAsync("bad_enum.mbypkg", "quiz", "Set_20261124_010000_005", """
            {
              "id": "Set_20261124_010000_005",
              "quizType": "999",
              "questions": []
            }
            """);
        var badEnumRes = await importer.HandleAsync(new ImportPackageCommand(badEnumPkg, destDir));
        Assert.False(badEnumRes.IsSuccess);
        Assert.Equal("Package.InvalidWorkspace", badEnumRes.Error!.Code);

        var badSideEnumPkg = await WritePkgAsync("bad_side_enum.mbypkg", "quiz", "Set_20261124_010000_006", """
            {
              "id": "Set_20261124_010000_006",
              "quizType": "Flashcard",
              "questions": [
                {
                  "id": "Question_20261124_010000_006",
                  "index": 1,
                  "questionType": "Flashcard",
                  "front": { "text": "F", "position": "999" },
                  "back": { "text": "B", "position": "TextBelowImage" }
                }
              ]
            }
            """);
        var badSideRes = await importer.HandleAsync(new ImportPackageCommand(badSideEnumPkg, destDir));
        Assert.False(badSideRes.IsSuccess);
        Assert.Equal("Package.InvalidWorkspace", badSideRes.Error!.Code);

        // 6. Directory loader ignores *.masks.json and *.meta.json sidecars
        var sidecarDir = Path.Combine(_tempRoot, "dir_sidecars");
        Directory.CreateDirectory(sidecarDir);
        await File.WriteAllTextAsync(Path.Combine(sidecarDir, "_session.md"), "---\nkind: session\nname: Sidecar Dir\n---\n");
        await File.WriteAllTextAsync(Path.Combine(sidecarDir, "heart.masks.json"), """{"boxes":[{"label":"Aorta","x":0.1,"y":0.1,"w":0.2,"h":0.1}]}""");
        await File.WriteAllTextAsync(Path.Combine(sidecarDir, "heart.meta.json"), """{"image_size":[800,600],"regions":[]}""");
        await File.WriteAllTextAsync(Path.Combine(sidecarDir, "01-intro.md"), "# Anatomy\n\nBody text.\n");
        var sidecarRes = await loader.LoadAsync(sidecarDir);
        Assert.True(sidecarRes.IsSuccess, sidecarRes.Error?.Description);
        Assert.Single(sidecarRes.Value.Components!);

        // 7. Directory session.json rejects invalid kind or root blocks/questions
        var badSessionKindDir = Path.Combine(_tempRoot, "dir_bad_session_kind");
        Directory.CreateDirectory(badSessionKindDir);
        await File.WriteAllTextAsync(Path.Combine(badSessionKindDir, "session.json"), """{"kind":"article","name":"Wrong Kind"}""");
        await File.WriteAllTextAsync(Path.Combine(badSessionKindDir, "01.md"), "# One\n");
        var badSessionKindRes = await loader.LoadAsync(badSessionKindDir);
        Assert.False(badSessionKindRes.IsSuccess);

        var badSessionRootBlocksDir = Path.Combine(_tempRoot, "dir_bad_session_blocks");
        Directory.CreateDirectory(badSessionRootBlocksDir);
        await File.WriteAllTextAsync(Path.Combine(badSessionRootBlocksDir, "session.json"), """{"kind":"session","blocks":[{"kind":"text","text":"nope"}]}""");
        await File.WriteAllTextAsync(Path.Combine(badSessionRootBlocksDir, "01.md"), "# One\n");
        var badSessionBlocksRes = await loader.LoadAsync(badSessionRootBlocksDir);
        Assert.False(badSessionBlocksRes.IsSuccess);
        Assert.Equal("Json.InvalidFieldForType", badSessionBlocksRes.Error!.Code);

        // 8. Directory component JSON rejects mixing blocks[] and questions[]
        var mixedCompDir = Path.Combine(_tempRoot, "dir_mixed_comp");
        Directory.CreateDirectory(mixedCompDir);
        await File.WriteAllTextAsync(Path.Combine(mixedCompDir, "01-mixed.json"), """
            {
              "kind": "article",
              "blocks": [{"kind": "text", "text": "Hello"}],
              "questions": [{"type": "mcq", "text": "Q?", "options": [{"text":"A","isCorrect":true},{"text":"B"}]}]
            }
            """);
        var mixedCompRes = await loader.LoadAsync(mixedCompDir);
        Assert.False(mixedCompRes.IsSuccess);
        Assert.Equal("Json.InvalidFieldForType", mixedCompRes.Error!.Code);

        // 9. JSON table rejects null cells in headers / alignments / rows
        var nullCellRes = await loader.LoadFromTextAsync("""
            {
              "kind": "article",
              "blocks": [
                {
                  "kind": "table",
                  "headers": ["A", null],
                  "rows": [["1", "2"]]
                }
              ]
            }
            """);
        Assert.False(nullCellRes.IsSuccess);
        Assert.Equal("Json.InvalidSpec", nullCellRes.Error!.Code);

        var nullRowCellRes = await loader.LoadFromTextAsync("""
            {
              "kind": "article",
              "blocks": [
                {
                  "kind": "table",
                  "headers": ["A", "B"],
                  "rows": [["1", null]]
                }
              ]
            }
            """);
        Assert.False(nullRowCellRes.IsSuccess);
        Assert.Equal("Json.InvalidSpec", nullRowCellRes.Error!.Code);
    }

    [Fact]
    public async Task WebAppAndReports_RenderKatexTitlesGreekNormalizationAndGradedLabels()
    {
        using var sp = CreateProvider("appdata_web");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var reportStore = scope.ServiceProvider.GetRequiredService<IAnalysisReportStore>();
        var analyze = scope.ServiceProvider.GetRequiredService<AnalyzeWebAppHandler>();
        var wsDir = Path.Combine(_tempRoot, "ws_web");

        // 1. Session where math appears ONLY in a callout title -> still bundles KaTeX!
        var titleOnlyMathMd = """
            ---
            title: Title Math Only
            ---

            :::callout{tone="pearl" title="$\text{Na}^+$ Channel"}
            Plain body without math.
            :::
            """;
        var titleOnlyApplied = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: titleOnlyMathMd, WorkingDirectory: wsDir));
        Assert.True(titleOnlyApplied.IsSuccess, titleOnlyApplied.Error?.Description);
        var titleOnlyWeb = Path.Combine(_tempRoot, "web_title_math");
        var titleOnlyBuild = await build.HandleAsync(new BuildWebAppCommand(titleOnlyApplied.Value.TargetId, titleOnlyWeb, StaticVerifyOnly: true));
        Assert.True(titleOnlyBuild.IsSuccess && titleOnlyBuild.Value.Verify?.Passed == true);
        Assert.True(File.Exists(Path.Combine(titleOnlyWeb, "katex", "katex.min.js")));

        // 2. Session with KaTeX table title + hideable KaTeX header + KaTeX fill-blank
        var dir = Path.Combine(_tempRoot, "web_rich_dir");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "_session.md"), "---\nkind: session\nname: Phase 2A Web Check\n---\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "01-art.md"), """
            # Electrochemistry

            :::table{title="Serum $\text{Ca}^{2+}$ Balance" hideableCols="2"}
            | Disorder | Serum $\text{K}^+$ |
            | --- | --- |
            | Loop diuretic | Decreased |
            :::
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "02-qz.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Recall Check
            ---

            :::fill-blank{fuzzy=true}
            Formula $\frac{1}{\sqrt{2}}$ evaluates to {{0.707 | $\frac{\sqrt{2}}{2}$}}.
            :::
            """);

        var richApplied = await apply.HandleAsync(new ApplySpecCommand(null, dir, WorkingDirectory: wsDir));
        Assert.True(richApplied.IsSuccess, richApplied.Error?.Description);
        var richWeb = Path.Combine(_tempRoot, "web_rich");
        var richBuild = await build.HandleAsync(new BuildWebAppCommand(richApplied.Value.TargetId, richWeb, StaticVerifyOnly: true));
        Assert.True(richBuild.IsSuccess && richBuild.Value.Verify?.Passed == true);

        var appJs = await File.ReadAllTextAsync(Path.Combine(richWeb, "app.js"));
        // Parse embedded DATA payload to verify decoded HTML & math preservation
        var dataMarker = "const DATA = ";
        var dataStart = appJs.IndexOf(dataMarker, StringComparison.Ordinal) + dataMarker.Length;
        var dataEnd = appJs.IndexOf(";\n", dataStart, StringComparison.Ordinal);
        if (dataEnd < 0) dataEnd = appJs.IndexOf(";\r\n", dataStart, StringComparison.Ordinal);
        using (var dataDoc = JsonDocument.Parse(appJs[dataStart..dataEnd]))
        {
            var slides = dataDoc.RootElement.GetProperty("slides");
            var tblTitleHtml = slides[0].GetProperty("articleBlocks")[1].GetProperty("table").GetProperty("titleHtml").GetString();
            Assert.Contains("class=\"math\"", tblTitleHtml);
            var fbPromptHtml = slides[1].GetProperty("questions")[0].GetProperty("fillBlank").GetProperty("promptHtml").GetString();
            Assert.Contains("MBYBLANKSLOT0END", fbPromptHtml);
            Assert.DoesNotContain("MBYBLANKSLOT1END", fbPromptHtml);
            Assert.Contains(@"\frac{1}{\sqrt{2}}", fbPromptHtml);
        }
        // KaTeX titleHtml emitted and rendered via innerHTML + renderMathIn
        Assert.Contains("titleHtml", appJs);
        Assert.Contains("t.titleHtml", appJs);
        Assert.Contains("b.titleHtml", appJs);
        // Table hide/show toggle uses headersHtml + renderMathIn
        Assert.Contains("renderMathIn(btn)", appJs);
        // Guarded expectedEls[idx]
        Assert.Contains("if (expectedEls[idx])", appJs);
        // Greek/micro/final-sigma/math-variants + uppercase Greek normalization
        Assert.Contains("\u00B5", appJs);
        Assert.Contains("\u03C2", appJs);
        Assert.Contains("\u03D5", appJs);
        Assert.Contains("\u03D1", appJs);
        Assert.Contains("\u03F5", appJs);
        // Finish screen uses Graded Score labels
        Assert.Contains("'Graded Score'", appJs);
        Assert.Contains("All graded questions & flashcards look solid", appJs);

        // Live headless CDP check (verifies KaTeX titles, toggle buttons, and no-explanation Check Answer)
        var verifier = scope.ServiceProvider.GetRequiredService<IHeadlessWebAppVerifier>();
        var cdpRes = await verifier.VerifyAsync(richWeb, staticOnly: false, timeoutSeconds: 30);
        if (cdpRes.IsSuccess || cdpRes.Error?.Code != "Verify.BrowserNotFound")
        {
            Assert.True(cdpRes.IsSuccess, cdpRes.Error?.Description);
            Assert.True(cdpRes.Value.Passed,
                $"CDP failed: console=[{string.Join("; ", cdpRes.Value.ConsoleErrors)}], overflow=[{string.Join("; ", cdpRes.Value.OverflowIssues)}], broken=[{string.Join("; ", cdpRes.Value.BrokenAssets)}], katex=[{string.Join("; ", cdpRes.Value.KatexErrors)}]");
        }

        // 3. Domain & Aggregate reports use Graded labels
        var sampleReport = new SessionAnalysisReport(
            "1.0", richApplied.Value.TargetId, "Phase 2A Web Check",
            "2026-11-24T00:00:00Z", "2026-11-24T00:01:00Z", 60000,
            new SessionAnalysisSummary(2, 1, 1, 1, 100.0, 0, 0, 0, 1),
            [new SlideAnalysisEntry(1, "Question_1", "fill-blank", 15000, 1, 1, null, null, true, true, null, null)],
            []);
        var mdReport = sampleReport.ToMarkdownReport();
        Assert.Contains("- **Graded Questions:**", mdReport);
        Assert.DoesNotContain("- **MCQs:**", mdReport);

        var reportsWeb = Path.Combine(_tempRoot, "reports_web");
        Directory.CreateDirectory(reportsWeb);
        var json1 = JsonSerializer.Serialize(sampleReport, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var json2 = JsonSerializer.Serialize(sampleReport with { CompletedAtUtc = "2026-11-24T00:02:00Z" }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.True((await reportStore.SaveReportToWebAppDirectoryAsync(reportsWeb, json1)).IsSuccess);
        await Task.Delay(15);
        Assert.True((await reportStore.SaveReportToWebAppDirectoryAsync(reportsWeb, json2)).IsSuccess);

        var aggRes = await analyze.HandleAsync(new AnalyzeWebAppQuery(reportsWeb, IncludeAllRuns: true));
        Assert.True(aggRes.IsSuccess, aggRes.Error?.Description);
        Assert.Contains("- **Average Graded Accuracy:**", aggRes.Value.MarkdownSummary);
        Assert.DoesNotContain("- **Average MCQ Accuracy:**", aggRes.Value.MarkdownSummary);
    }

    [Fact]
    public async Task Phase2A1_AuditRemediations_VerifyNullGuardsEscapesKatexBadgesPatchBomSessionMdAndClassifier()
    {
        using var sp = CreateProvider("appdata_2a1");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var pkg = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var imp = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var loader = scope.ServiceProvider.GetRequiredService<IDeclarativeSpecLoader>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var schema = scope.ServiceProvider.GetRequiredService<GetSchemaHandler>();
        var batch = scope.ServiceProvider.GetRequiredService<BatchOperationsHandler>();
        var webGen = scope.ServiceProvider.GetRequiredService<IWebAppGenerator>();
        var wsDir = Path.Combine(_tempRoot, "ws_2a1");

        // F-07: Inline backtick code spans & double-backslash before [span]
        var fbCode = Phase1PayloadRules.ParseFillBlankTemplate("Avoid literal `{{x}}` and enter {{`a | b` | fallback}}.");
        Assert.True(fbCode.IsSuccess, fbCode.Error?.Description);
        var fbSlot = Assert.Single(fbCode.Value);
        Assert.Equal(["`a | b`", "fallback"], fbSlot.Answers);

        var seCode = Phase1PayloadRules.ParseSpotErrorTemplate(@"Check `arr[0]` and \\[item] plus [~~bad~~ -> `x | y`].");
        Assert.True(seCode.IsSuccess, seCode.Error?.Description);
        var seCandidates = seCode.Value.Where(s => s.IsCandidate).ToList();
        Assert.Equal(2, seCandidates.Count);
        Assert.Equal("item", seCandidates[0].Text);
        Assert.Equal("bad", seCandidates[1].Text);
        Assert.Equal(["`x | y`"], seCandidates[1].Corrections);
        Assert.Contains("`arr[0]`", seCode.Value[0].Text);

        // F-04: Escaped quotes in directive headers while keeping KaTeX \text intact
        var escapedAttrSpec = await loader.LoadFromTextAsync("""
            ---
            title: Diuretics
            ---

            :::table{title="Loop (\"High-Ceiling\") Diuretics $\text{Na}^+$"}
            | Drug | Site |
            | --- | --- |
            | Furosemide | TAL |
            :::
            """);
        Assert.True(escapedAttrSpec.IsSuccess, escapedAttrSpec.Error?.Description);
        var tblSpec = Assert.Single(escapedAttrSpec.Value.Blocks!);
        Assert.True(Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(tblSpec.PayloadJson, out var tblParsed));
        Assert.Equal(@"Loop (""High-Ceiling"") Diuretics $\text{Na}^+$", tblParsed!.Title);

        // F-08a: _session.md rejects invalid kind and stray body content
        var badKindDir = Path.Combine(_tempRoot, "bad_session_kind");
        Directory.CreateDirectory(badKindDir);
        await File.WriteAllTextAsync(Path.Combine(badKindDir, "_session.md"), "---\nkind: quiz\nname: Wrong\n---\n");
        await File.WriteAllTextAsync(Path.Combine(badKindDir, "01-note.md"), "# Note\nBody");
        var badKindRes = await loader.LoadAsync(badKindDir);
        Assert.False(badKindRes.IsSuccess);
        Assert.Equal("Markdown.InvalidFrontmatter", badKindRes.Error!.Code);

        var badBodyDir = Path.Combine(_tempRoot, "bad_session_body");
        Directory.CreateDirectory(badBodyDir);
        await File.WriteAllTextAsync(Path.Combine(badBodyDir, "_session.md"), "---\nkind: session\nname: BodyTest\n---\n\nUnexpected body text.\n");
        await File.WriteAllTextAsync(Path.Combine(badBodyDir, "01-note.md"), "# Note\nBody");
        var badBodyRes = await loader.LoadAsync(badBodyDir);
        Assert.False(badBodyRes.IsSuccess);
        Assert.Equal("Json.InvalidFieldForType", badBodyRes.Error!.Code);

        // F-05 & F-09b: KaTeX solely inside blank answer & spot-error correction bundles KaTeX + renders primaryAnswerHtml/primaryCorrectionHtml + Enter key on .mby-fix-input
        var katexAnswerSpec = """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "name": "Badge Math Quiz",
              "questions": [
                {
                  "type": "fill-blank",
                  "text": "Compute value: {{$\\sqrt{4}$ | 2}}."
                },
                {
                  "type": "spot-error",
                  "requireFix": true,
                  "text": "Given [x is real], result is [~~2~~ -> $\\sqrt{4}$]."
                }
              ]
            }
            """;
        var katexApplied = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: katexAnswerSpec, WorkingDirectory: wsDir));
        Assert.True(katexApplied.IsSuccess, katexApplied.Error?.Description);
        var katexWebOut = Path.Combine(_tempRoot, "katex_badges_web");
        var katexBuild = await build.HandleAsync(new BuildWebAppCommand(katexApplied.Value.TargetId, katexWebOut));
        Assert.True(katexBuild.IsSuccess, katexBuild.Error?.Description);
        Assert.True(File.Exists(Path.Combine(katexWebOut, "katex", "katex.min.js")));
        var katexAppJs = await File.ReadAllTextAsync(Path.Combine(katexWebOut, "app.js"));
        Assert.Contains("primaryAnswerHtml", katexAppJs);
        Assert.Contains("primaryCorrectionHtml", katexAppJs);
        Assert.Contains("expectedEls[idx].innerHTML = 'Answer: ' + (slot?.primaryAnswerHtml || '')", katexAppJs);
        Assert.Contains("badge.innerHTML = '→ ' + (seg.primaryCorrectionHtml || '')", katexAppJs);
        Assert.Contains("inp.addEventListener('keydown', e => { if (e.key === 'Enter')", katexAppJs);

        // F-06: Table patch rejects wrong-width rows immediately
        var tblApplied = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: """
            {
              "kind": "session",
              "name": "Table Patch Width",
              "components": [
                {
                  "kind": "article",
                  "ref": "art",
                  "blocks": [
                    { "kind": "table", "ref": "t1", "headers": ["A", "B", "C"], "rows": [["1", "2", "3"]] }
                  ]
                }
              ]
            }
            """, WorkingDirectory: wsDir));
        Assert.True(tblApplied.IsSuccess, tblApplied.Error?.Description);
        var badPatchRes = await patch.HandleAsync(new PatchEntityCommand($"{tblApplied.Value.TargetId}#t1", InlineJson: """{"rows":[["only1"]]}"""));
        Assert.False(badPatchRes.IsSuccess);
        Assert.Equal("Patch.InvalidValue", badPatchRes.Error!.Code);

        // F-09a: Batch validates timeout bounds
        var badBatchTimeout = await batch.HandleAsync(new BatchOperationsCommand(InlineJson: """[{"op":"build","addr":"Session_20260101_000000_000","timeout":999}]"""));
        Assert.False(badBatchTimeout.IsSuccess);
        Assert.Equal("Batch.InvalidOp", badBatchTimeout.Error!.Code);

        // F-01 & F-08b: Crafted .mbypkg omitting Blanks fails import; BOM-prefixed .mbypkg imports cleanly; in-DB null Blanks caught by check and safe in WebAppGenerator
        var validPkgPath = Path.Combine(_tempRoot, "valid.mbypkg");
        var expOk = await pkg.HandleAsync(new ExportPackageCommand(katexApplied.Value.TargetId, validPkgPath, "desktop"));
        Assert.True(expOk.IsSuccess, expOk.Error?.Description);

        // Create BOM-prefixed valid package
        var bomPkgPath = Path.Combine(_tempRoot, "bom_valid.mbypkg");
        File.Copy(validPkgPath, bomPkgPath);
        using (var zip = ZipFile.Open(bomPkgPath, ZipArchiveMode.Update))
        {
            var mEntry = zip.GetEntry("manifest.json")!;
            string mText;
            using (var sr = new StreamReader(mEntry.Open(), Encoding.UTF8))
                mText = await sr.ReadToEndAsync();
            var newId = "Set_20261124_120000_991";
            mText = mText.Replace(katexApplied.Value.TargetId, newId, StringComparison.Ordinal);
            mEntry.Delete();
            await using (var s = zip.CreateEntry("manifest.json").Open())
            {
                await s.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF });
                await s.WriteAsync(Encoding.UTF8.GetBytes(mText));
            }

            var wEntry = zip.GetEntry("workspace.json")!;
            string wText;
            using (var sr = new StreamReader(wEntry.Open(), Encoding.UTF8))
                wText = await sr.ReadToEndAsync();
            wText = wText.Replace(katexApplied.Value.TargetId, newId, StringComparison.Ordinal);
            wEntry.Delete();
            await using (var s = zip.CreateEntry("workspace.json").Open())
            {
                await s.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF });
                await s.WriteAsync(Encoding.UTF8.GetBytes(wText));
            }
        }
        var bomImp = await imp.HandleAsync(new ImportPackageCommand(bomPkgPath, Path.Combine(_tempRoot, "bom_imp")));
        Assert.True(bomImp.IsSuccess, bomImp.Error?.Description);

        // Crafted package omitting blanks in FillBlank payloadJson is rejected cleanly on import
        var badPkgPath = Path.Combine(_tempRoot, "null_blanks.mbypkg");
        File.Copy(validPkgPath, badPkgPath);
        using (var zip = ZipFile.Open(badPkgPath, ZipArchiveMode.Update))
        {
            var wEntry = zip.GetEntry("workspace.json")!;
            string wText;
            using (var sr = new StreamReader(wEntry.Open(), Encoding.UTF8))
                wText = await sr.ReadToEndAsync();
            using var doc = JsonDocument.Parse(wText);
            var qs = doc.RootElement.GetProperty("questions").EnumerateArray().ToList();
            var rewritten = JsonSerializer.Serialize(new
            {
                id = "Set_20261124_120000_992",
                quizType = doc.RootElement.GetProperty("quizType").GetString(),
                name = doc.RootElement.GetProperty("name").GetString(),
                questions = new object[]
                {
                    new
                    {
                        id = qs[0].GetProperty("id").GetString(),
                        index = 1,
                        questionType = "FillBlank",
                        payloadJson = """{"template":"Enzyme is {{PFK-1}}."}"""
                    }
                }
            });
            wEntry.Delete();
            await using (var s = zip.CreateEntry("workspace.json").Open())
                await s.WriteAsync(Encoding.UTF8.GetBytes(rewritten));

            var mEntry = zip.GetEntry("manifest.json")!;
            string mText;
            using (var sr = new StreamReader(mEntry.Open(), Encoding.UTF8))
                mText = await sr.ReadToEndAsync();
            mText = mText.Replace(katexApplied.Value.TargetId, "Set_20261124_120000_992", StringComparison.Ordinal);
            mEntry.Delete();
            await using (var s = zip.CreateEntry("manifest.json").Open())
                await s.WriteAsync(Encoding.UTF8.GetBytes(mText));
        }
        var badImp = await imp.HandleAsync(new ImportPackageCommand(badPkgPath, Path.Combine(_tempRoot, "bad_imp")));
        Assert.False(badImp.IsSuccess);
        Assert.Equal("Package.InvalidWorkspace", badImp.Error!.Code);

        // Corrupted in-DB FillBlank payloadJson without blanks fails lint and does NOT crash WebAppGenerator
        var corruptStorage = Path.Combine(_tempRoot, "corrupt_quiz_ws");
        Directory.CreateDirectory(Path.Combine(corruptStorage, "assets"));
        var corruptQuiz = Quiz.Rehydrate(
            "Set_20261124_120000_993",
            QuizType.Mixed,
            corruptStorage,
            [PayloadQuestion.Rehydrate("Q_20261124_120000_993", "Set_20261124_120000_993", 1, QuestionType.FillBlank, """{"template":"Enzyme is {{PFK-1}}."}""")],
            "Corrupt Quiz");
        await repo.CreateQuizAsync(corruptQuiz);
        var corruptLint = await lint.HandleAsync(new LintCommand("Set_20261124_120000_993"));
        Assert.True(corruptLint.IsSuccess);
        Assert.False(corruptLint.Value.IsClean);
        Assert.Contains(corruptLint.Value.Issues, i => i.Code == "Lint.InvalidQuestionPayload");

        var safeWeb = await webGen.GenerateForQuizAsync(
            corruptQuiz,
            corruptStorage,
            new Dictionary<string, Meloberry.Domain.Layouts.LayoutProfile>(),
            Path.Combine(_tempRoot, "corrupt_safe_web"));
        Assert.True(safeWeb.IsSuccess, safeWeb.Error?.Description);

        // S-01: Two-tier Style A spec classifier (@content vs @other + group selectors)
        var contentSchemas = await schema.HandleAsync(new GetSchemaQuery(["@content"]));
        Assert.True(contentSchemas.IsSuccess);
        Assert.Equal(21, contentSchemas.Value.Schemas.Count);
        Assert.All(contentSchemas.Value.Schemas, s => Assert.Equal("content", s.Scope));

        var questionSchemas = await schema.HandleAsync(new GetSchemaQuery(["@questions"]));
        Assert.True(questionSchemas.IsSuccess);
        Assert.Equal(["mcq", "flashcard", "fill-blank", "assertion-reason", "spot-error", "diff", "mechanism", "relabel", "case", "classify", "match", "numeric"], questionSchemas.Value.Schemas.Select(s => s.Topic));
        Assert.All(questionSchemas.Value.Schemas, s => Assert.Equal("question", s.Category));

        var otherSchemas = await schema.HandleAsync(new GetSchemaQuery(["@other"]));
        Assert.True(otherSchemas.IsSuccess);
        Assert.Equal(["markdown", "layouts", "patch", "batch", "pkg", "build", "analyze"], otherSchemas.Value.RequestedTopics);
        Assert.NotNull(otherSchemas.Value.LayoutProperties);
        Assert.All(otherSchemas.Value.Schemas, s => Assert.Equal("other", s.Scope));

        var bogusGroup = await schema.HandleAsync(new GetSchemaQuery(["@bogus"]));
        Assert.False(bogusGroup.IsSuccess);
        Assert.Equal("Schema.TopicNotFound", bogusGroup.Error!.Code);
    }

    private sealed class StubSpecLoader : IDeclarativeSpecLoader
    {
        public DeclarativeApplySpec? NextSpec { get; set; }

        public Task<Result<DeclarativeApplySpec>> LoadAsync(string specPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<DeclarativeApplySpec>.Success(NextSpec!));

        public Task<Result<DeclarativeApplySpec>> LoadFromTextAsync(string rawText, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<DeclarativeApplySpec>.Success(NextSpec!));
    }
}
