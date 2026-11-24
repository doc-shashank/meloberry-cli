using System.IO.Compression;
using System.Text.Json;
using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;
using Meloberry.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class Phase1ComponentTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_phase1_unit_{Guid.NewGuid():N}");

    public Phase1ComponentTests() => Directory.CreateDirectory(_tempRoot);

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

    [Fact]
    public async Task NativeTable_MarkdownAndJson_RoundTripsSqliteMbypkgPatchAndWebApp()
    {
        using var sp = CreateProvider("appdata_table");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();

        var wsDir = Path.Combine(_tempRoot, "ws_table");
        var md = """
            ---
            title: Renal Diuretics
            ref: renal-diuretics
            ---

            # Diuretics Overview

            :::table{ref="tbl-diuretics" title="Loop vs Thiazide Diuretics" caption="Renal pharmacology comparison" stickyFirstCol=true hideableCols="2,3"}
            | Feature | Loop Diuretics | Thiazide Diuretics |
            | :--- | :---: | ---: |
            | Primary Site | Thick ascending limb | Early distal tubule |
            | Serum $\text{Ca}^{2+}$ | Decreased | Increased |
            :::footnote
            Both classes can cause hypokalemia and metabolic alkalosis.
            :::
            """;

        var mdRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: md, WorkingDirectory: wsDir));
        Assert.True(mdRes.IsSuccess, mdRes.Error?.Description);
        var sessionId = mdRes.Value.TargetId;

        // Lint clean
        var lintRes = await lint.HandleAsync(new LintCommand(sessionId));
        Assert.True(lintRes.IsSuccess, lintRes.Error?.Description);
        Assert.True(lintRes.Value.IsClean);
        Assert.Equal(0, lintRes.Value.ErrorCount);
        Assert.Equal(0, lintRes.Value.WarningCount);

        // SQLite reload preserves TableBlockPayload
        var loaded = await repo.FindSessionAsync(sessionId);
        Assert.NotNull(loaded);
        var tableBlock = loaded!.Session.Components[0].ArticleBlocks[1];
        Assert.Equal(ArticleBlockKind.Table, tableBlock.Kind);
        Assert.Equal("tbl-diuretics", tableBlock.RefKey);
        Assert.True(Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(tableBlock.PayloadJson, out var payload));
        Assert.NotNull(payload);
        Assert.Equal("Loop vs Thiazide Diuretics", payload!.Title);
        Assert.Equal("Renal pharmacology comparison", payload.Caption);
        Assert.True(payload.StickyFirstCol);
        Assert.Equal([2, 3], payload.HideableCols);
        Assert.Equal(["Feature", "Loop Diuretics", "Thiazide Diuretics"], payload.Headers);
        Assert.Equal(["left", "center", "right"], payload.Alignments);
        Assert.Equal(2, payload.Rows.Count);
        Assert.Contains("hypokalemia", payload.Footnote);

        // Inspect Outline & Full views
        var outInspect = await inspect.HandleAsync(new InspectEntityQuery($"{sessionId}#tbl-diuretics", InspectViewMode.Outline));
        Assert.True(outInspect.IsSuccess, outInspect.Error?.Description);
        Assert.Equal("table", outInspect.Value.ArticleBlock?.Kind);
        Assert.Contains("Loop vs Thiazide Diuretics", outInspect.Value.ArticleBlock?.Snippet);

        var fullInspect = await inspect.HandleAsync(new InspectEntityQuery($"{sessionId}#tbl-diuretics", InspectViewMode.Full));
        Assert.True(fullInspect.IsSuccess, fullInspect.Error?.Description);
        Assert.NotNull(fullInspect.Value.ArticleBlock?.PayloadJson);

        // Patch table block via --set
        var patchRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{sessionId}#tbl-diuretics",
            SetPairs: ["title=Updated Diuretics", "stickyFirstCol=false", "hideableCols=2", "footnote=Updated note."]));
        Assert.True(patchRes.IsSuccess, patchRes.Error?.Description);

        var afterPatch = await repo.FindSessionAsync(sessionId);
        Assert.True(Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(afterPatch!.Session.Components[0].ArticleBlocks[1].PayloadJson, out var patchedPayload));
        Assert.Equal("Updated Diuretics", patchedPayload!.Title);
        Assert.False(patchedPayload.StickyFirstCol);
        Assert.Equal([2], patchedPayload.HideableCols);
        Assert.Equal("Updated note.", patchedPayload.Footnote);

        // .mbypkg v2.0 export & import round-trip
        var pkgPath = Path.Combine(_tempRoot, "table.mbypkg");
        var expRes = await export.HandleAsync(new ExportPackageCommand(sessionId, pkgPath, "android"));
        Assert.True(expRes.IsSuccess, expRes.Error?.Description);
        var impRes = await import.HandleAsync(new ImportPackageCommand(pkgPath, Path.Combine(_tempRoot, "table_reimp")));
        Assert.True(impRes.IsSuccess, impRes.Error?.Description);

        // Build WebApp emits native table classes, KaTeX, and search index entries
        var webDir = Path.Combine(_tempRoot, "table_web");
        var buildRes = await build.HandleAsync(new BuildWebAppCommand(sessionId, webDir, StaticVerifyOnly: true));
        Assert.True(buildRes.IsSuccess, buildRes.Error?.Description);
        Assert.True(buildRes.Value.Verify?.Passed);
        var css = await File.ReadAllTextAsync(Path.Combine(webDir, "styles.css"));
        var js = await File.ReadAllTextAsync(Path.Combine(webDir, "app.js"));
        Assert.Contains(".mby-native-table", css);
        Assert.Contains(".mby-sticky-col", css);
        Assert.Contains("buildNativeTableBlock", js);
        Assert.Contains("Thick ascending limb", js);
        Assert.True(File.Exists(Path.Combine(webDir, "katex", "katex.min.js")));

        // Strict JSON authoring of kind: "table"
        var jsonSpec = """
            {
              "kind": "article",
              "title": "JSON Table Article",
              "blocks": [
                {
                  "kind": "table",
                  "ref": "json-tbl",
                  "title": "Acid-Base",
                  "caption": "Primary changes",
                  "stickyFirstCol": true,
                  "hideableCols": [2],
                  "headers": ["Disorder", "Primary Change"],
                  "rows": [
                    ["Metabolic Acidosis", "Low HCO3-"],
                    ["Respiratory Acidosis", "High PaCO2"]
                  ],
                  "footnote": "Check Winter's formula."
                }
              ]
            }
            """;
        var jsonRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: jsonSpec, WorkingDirectory: wsDir));
        Assert.True(jsonRes.IsSuccess, jsonRes.Error?.Description);
        var jsonLint = await lint.HandleAsync(new LintCommand(jsonRes.Value.TargetId));
        Assert.True(jsonLint.IsSuccess && jsonLint.Value.IsClean);
    }

    [Fact]
    public async Task NativeTable_LintAndParser_RejectMalformedTablesAndOutOfBoundsHideableCols()
    {
        using var sp = CreateProvider("appdata_table_err");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var wsDir = Path.Combine(_tempRoot, "ws_table_err");

        // 1. Markdown table missing data rows
        var headerOnlyMd = """
            :::table{title="Empty Rows"}
            | A | B |
            | --- | --- |
            :::
            """;
        var headerOnlyRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: headerOnlyMd, WorkingDirectory: wsDir));
        Assert.False(headerOnlyRes.IsSuccess);
        Assert.Equal("Markdown.InvalidTable", headerOnlyRes.Error!.Code);

        // 2. Mismatched column count in Markdown
        var unevenMd = """
            :::table
            | A | B |
            | --- | --- |
            | 1 | 2 | 3 |
            :::
            """;
        var unevenRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: unevenMd, WorkingDirectory: wsDir));
        Assert.False(unevenRes.IsSuccess);
        Assert.Equal("Markdown.InvalidTable", unevenRes.Error!.Code);

        // 3. Out-of-bounds hideableCols in Markdown
        var badColsMd = """
            :::table{hideableCols="1,9"}
            | A | B | C |
            | --- | --- | --- |
            | 1 | 2 | 3 |
            :::
            """;
        var badColsRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badColsMd, WorkingDirectory: wsDir));
        Assert.False(badColsRes.IsSuccess);
        Assert.Equal("Markdown.InvalidDirectiveAttr", badColsRes.Error!.Code);

        // 4. Unknown attribute typo hint
        var typoMd = """
            :::table{stikyFirstCol=true}
            | A | B |
            | --- | --- |
            | 1 | 2 |
            :::
            """;
        var typoRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: typoMd, WorkingDirectory: wsDir));
        Assert.False(typoRes.IsSuccess);
        Assert.Equal("Markdown.InvalidDirectiveAttr", typoRes.Error!.Code);
        Assert.Contains("stickyFirstCol", typoRes.Error.Hint);

        // 5. JSON table with out-of-bounds hideableCols fails mandatory lint
        var badJsonTable = """
            {
              "kind": "article",
              "blocks": [
                {
                  "kind": "table",
                  "headers": ["A", "B"],
                  "rows": [["1", "2"]],
                  "hideableCols": [5]
                }
              ]
            }
            """;
        var badJsonApply = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badJsonTable, WorkingDirectory: wsDir));
        Assert.True(badJsonApply.IsSuccess, badJsonApply.Error?.Description);
        var badJsonLint = await lint.HandleAsync(new LintCommand(badJsonApply.Value.TargetId));
        Assert.True(badJsonLint.IsSuccess);
        Assert.False(badJsonLint.Value.IsClean);
        Assert.Contains(badJsonLint.Value.Issues, i => i.Code == "Lint.InvalidTableHideableCol");
    }

    [Fact]
    public async Task FillBlank_MarkdownAndJson_RoundTripsAndValidatesBlanks()
    {
        using var sp = CreateProvider("appdata_fb");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var wsDir = Path.Combine(_tempRoot, "ws_fb");

        var md = """
            ---
            kind: quiz
            quizType: mixed
            title: Glycolysis Free Recall
            ---

            :::fill-blank{ref="fb-pfk1" fuzzy=true caseSensitive=false}
            The rate-limiting enzyme of glycolysis is {{phosphofructokinase-1 | PFK-1 | PFK1}}, which is allosterically activated by {{fructose-2,6-bisphosphate | F-2,6-BP}}.
            :::explanation
            PFK-1 catalyzes the irreversible committed step.
            :::layout
            cardMaxWidthPx: 900
            :::
            """;

        var mdRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: md, WorkingDirectory: wsDir));
        Assert.True(mdRes.IsSuccess, mdRes.Error?.Description);
        var quizId = mdRes.Value.TargetId;

        var lintRes = await lint.HandleAsync(new LintCommand(quizId));
        Assert.True(lintRes.IsSuccess && lintRes.Value.IsClean);

        var loaded = await repo.FindQuizContainerAsync(quizId);
        Assert.NotNull(loaded);
        var pq = Assert.IsType<PayloadQuestion>(Assert.Single(loaded!.Quiz.Questions));
        Assert.Equal(QuestionType.FillBlank, pq.Type);
        Assert.Equal("fb-pfk1", pq.RefKey);
        Assert.Equal("900", pq.LayoutOverrides["cardMaxWidthPx"]);
        Assert.True(Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(pq.PayloadJson, out var p));
        Assert.Equal(2, p!.Blanks.Count);
        Assert.Equal(["phosphofructokinase-1", "PFK-1", "PFK1"], p.Blanks[0].Answers);
        Assert.True(p.Fuzzy);
        Assert.False(p.CaseSensitive);

        // Patch fuzzy + text re-parses blanks
        var patchRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{quizId}#fb-pfk1",
            SetPairs: ["fuzzy=false", "text=Primary pacemaker enzyme is {{PFK-1 | PFK1}}."]));
        Assert.True(patchRes.IsSuccess, patchRes.Error?.Description);
        var afterPatch = await repo.FindQuizContainerAsync(quizId);
        Assert.True(Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(afterPatch!.Quiz.Questions[0].PayloadJson, out var pAfter));
        Assert.False(pAfter!.Fuzzy);
        Assert.Single(pAfter.Blanks);

        // Inspect
        var insp = await inspect.HandleAsync(new InspectEntityQuery($"{quizId}#fb-pfk1", InspectViewMode.Full));
        Assert.True(insp.IsSuccess);
        Assert.Equal("fill-blank", insp.Value.Question?.Type);
        Assert.NotNull(insp.Value.Question?.PayloadJson);

        // Package + WebApp
        var pkgPath = Path.Combine(_tempRoot, "fb.mbypkg");
        Assert.True((await export.HandleAsync(new ExportPackageCommand(quizId, pkgPath, "desktop"))).IsSuccess);
        Assert.True((await import.HandleAsync(new ImportPackageCommand(pkgPath, Path.Combine(_tempRoot, "fb_reimp")))).IsSuccess);

        var webDir = Path.Combine(_tempRoot, "fb_web");
        var buildRes = await build.HandleAsync(new BuildWebAppCommand(quizId, webDir, StaticVerifyOnly: true));
        Assert.True(buildRes.IsSuccess && buildRes.Value.Verify?.Passed == true);
        var js = await File.ReadAllTextAsync(Path.Combine(webDir, "app.js"));
        Assert.Contains("buildFillBlankCard", js);
        Assert.Contains("normalizeRecallAnswer", js);
        Assert.Contains("Override: Mark Correct", js);

        // Invalid JSON cases caught by lint
        var badJson = """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "questions": [
                { "type": "fill-blank", "text": "No blanks here at all." },
                { "type": "fill-blank", "text": "Unbalanced {{blank here" },
                { "type": "fill-blank", "text": "Empty {{ | }} blank." }
              ]
            }
            """;
        var badApply = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badJson, WorkingDirectory: wsDir));
        Assert.True(badApply.IsSuccess, badApply.Error?.Description);
        var badLint = await lint.HandleAsync(new LintCommand(badApply.Value.TargetId));
        Assert.True(badLint.IsSuccess);
        Assert.False(badLint.Value.IsClean);
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.NoFillBlanks");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.UnbalancedBlankDelimiters");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.EmptyBlankAnswer");
    }

    [Fact]
    public async Task AssertionReason_MarkdownAndJson_RoundTripsSchemesAndValidatesEnum()
    {
        using var sp = CreateProvider("appdata_ar");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var wsDir = Path.Combine(_tempRoot, "ws_ar");

        var md = """
            ---
            kind: quiz
            title: Pharmacology Assertion-Reason
            ---

            :::assertion-reason{ref="ar-aspirin" answer="both-no-explain" scheme="4opt"}
            A: Aspirin is avoided in children with viral infections.
            R: Aspirin irreversibly inhibits COX-1 and COX-2 via acetylation.
            :::explanation
            Pediatric avoidance is due to Reye syndrome risk.
            :::

            :::assertion-reason{ref="ar-five" answer="both-false"}
            A: Penicillin inhibits 30S ribosomal subunit.
            R: Aminoglycosides inhibit cell wall cross-linking.
            :::
            """;

        var mdRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: md, WorkingDirectory: wsDir));
        Assert.True(mdRes.IsSuccess, mdRes.Error?.Description);
        var quizId = mdRes.Value.TargetId;

        var lintRes = await lint.HandleAsync(new LintCommand(quizId));
        Assert.True(lintRes.IsSuccess && lintRes.Value.IsClean);

        var loaded = await repo.FindQuizContainerAsync(quizId);
        Assert.Equal(2, loaded!.Quiz.Questions.Count);
        Assert.True(Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(loaded.Quiz.Questions[0].PayloadJson, out var p1));
        Assert.Equal("4opt", p1!.Scheme);
        Assert.Equal("both-no-explain", p1.Answer);
        Assert.True(Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(loaded.Quiz.Questions[1].PayloadJson, out var p2));
        Assert.Equal("5opt", p2!.Scheme); // auto-resolved to 5opt for both-false

        // Patch answer
        var patchRes = await patch.HandleAsync(new PatchEntityCommand($"{quizId}#ar-aspirin", SetPairs: ["answer=both-explain"]));
        Assert.True(patchRes.IsSuccess, patchRes.Error?.Description);

        // Package + WebApp
        var pkgPath = Path.Combine(_tempRoot, "ar.mbypkg");
        Assert.True((await export.HandleAsync(new ExportPackageCommand(quizId, pkgPath, "android"))).IsSuccess);
        Assert.True((await import.HandleAsync(new ImportPackageCommand(pkgPath, Path.Combine(_tempRoot, "ar_reimp")))).IsSuccess);

        var webDir = Path.Combine(_tempRoot, "ar_web");
        var buildRes = await build.HandleAsync(new BuildWebAppCommand(quizId, webDir, StaticVerifyOnly: true));
        Assert.True(buildRes.IsSuccess && buildRes.Value.Verify?.Passed == true);
        var js = await File.ReadAllTextAsync(Path.Combine(webDir, "app.js"));
        Assert.Contains("buildAssertionReasonCard", js);

        // Invalid Markdown: both-false with explicit 4opt fails
        var badSchemeMd = """
            :::assertion-reason{answer="both-false" scheme="4opt"}
            A: One
            R: Two
            :::
            """;
        var badSchemeRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badSchemeMd, WorkingDirectory: wsDir));
        Assert.False(badSchemeRes.IsSuccess);
        Assert.Equal("Markdown.InvalidAssertionReason", badSchemeRes.Error!.Code);

        // Invalid JSON cases caught by lint
        var badJson = """
            {
              "kind": "quiz",
              "questions": [
                { "type": "assertion-reason", "assertion": "Only A", "reason": "", "answer": "both-explain" },
                { "type": "assertion-reason", "assertion": "A", "reason": "R", "answer": "maybe" },
                { "type": "assertion-reason", "assertion": "A", "reason": "R", "answer": "both-false", "scheme": "4opt" }
              ]
            }
            """;
        var badApply = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badJson, WorkingDirectory: wsDir));
        Assert.True(badApply.IsSuccess, badApply.Error?.Description);
        var badLint = await lint.HandleAsync(new LintCommand(badApply.Value.TargetId));
        Assert.True(badLint.IsSuccess);
        Assert.False(badLint.Value.IsClean);
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.EmptyAssertionOrReason");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.InvalidAssertionReasonAnswer");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.InvalidAssertionReasonScheme");
    }

    [Fact]
    public async Task SpotError_MarkdownAndJson_RoundTripsSpansNoErrorAndRequireFix()
    {
        using var sp = CreateProvider("appdata_se");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var wsDir = Path.Combine(_tempRoot, "ws_se");

        var md = """
            ---
            kind: quiz
            title: Endocrine Spot the Error
            ---

            :::spot-error{ref="se-hpth" requireFix=true allowNoError=true}
            In [primary hyperparathyroidism], serum calcium is [elevated], serum phosphate is [~~elevated~~ -> decreased | low], and PTH is [high].
            :::explanation
            PTH inhibits proximal tubular phosphate reabsorption, causing hypophosphatemia.
            :::

            :::spot-error{ref="se-clean" noError=true}
            In [metabolic acidosis], arterial pH is [decreased] and serum bicarbonate is [low].
            :::explanation
            All segments are accurate.
            :::
            """;

        var mdRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: md, WorkingDirectory: wsDir));
        Assert.True(mdRes.IsSuccess, mdRes.Error?.Description);
        var quizId = mdRes.Value.TargetId;

        var lintRes = await lint.HandleAsync(new LintCommand(quizId));
        Assert.True(lintRes.IsSuccess && lintRes.Value.IsClean);

        var loaded = await repo.FindQuizContainerAsync(quizId);
        Assert.Equal(2, loaded!.Quiz.Questions.Count);
        Assert.True(Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(loaded.Quiz.Questions[0].PayloadJson, out var p1));
        Assert.True(p1!.RequireFix);
        Assert.True(p1.AllowNoError);
        Assert.False(p1.NoError);
        Assert.Equal(4, p1.Segments.Count(s => s.IsCandidate));
        var errSeg = Assert.Single(p1.Segments, s => s.IsError);
        Assert.Equal("elevated", errSeg.Text);
        Assert.Equal(["decreased", "low"], errSeg.Corrections);

        Assert.True(Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(loaded.Quiz.Questions[1].PayloadJson, out var p2));
        Assert.True(p2!.NoError);
        Assert.True(p2.AllowNoError); // defaulted true when noError=true

        // Patch requireFix=false
        var patchRes = await patch.HandleAsync(new PatchEntityCommand($"{quizId}#se-hpth", SetPairs: ["requireFix=false"]));
        Assert.True(patchRes.IsSuccess, patchRes.Error?.Description);
        var afterPatch = await repo.FindQuizContainerAsync(quizId);
        Assert.True(Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(afterPatch!.Quiz.Questions[0].PayloadJson, out var p1After));
        Assert.False(p1After!.RequireFix);

        // Package + WebApp
        var pkgPath = Path.Combine(_tempRoot, "se.mbypkg");
        Assert.True((await export.HandleAsync(new ExportPackageCommand(quizId, pkgPath, "desktop"))).IsSuccess);
        Assert.True((await import.HandleAsync(new ImportPackageCommand(pkgPath, Path.Combine(_tempRoot, "se_reimp")))).IsSuccess);

        var webDir = Path.Combine(_tempRoot, "se_web");
        var buildRes = await build.HandleAsync(new BuildWebAppCommand(quizId, webDir, StaticVerifyOnly: true));
        Assert.True(buildRes.IsSuccess && buildRes.Value.Verify?.Passed == true);
        var js = await File.ReadAllTextAsync(Path.Combine(webDir, "app.js"));
        Assert.Contains("buildSpotErrorCard", js);

        // Invalid JSON cases caught by lint
        var badJson = """
            {
              "kind": "quiz",
              "questions": [
                { "type": "spot-error", "text": "Only [~~one~~ -> two] candidate span." },
                { "type": "spot-error", "text": "Has [two] clean [spans] without error." },
                { "type": "spot-error", "noError": true, "text": "Marks noError with [valid] and [~~bad~~ -> good]." },
                { "type": "spot-error", "text": "Empty fix [valid] and [~~bad~~ -> ]." }
              ]
            }
            """;
        var badApply = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badJson, WorkingDirectory: wsDir));
        Assert.True(badApply.IsSuccess, badApply.Error?.Description);
        var badLint = await lint.HandleAsync(new LintCommand(badApply.Value.TargetId));
        Assert.True(badLint.IsSuccess);
        Assert.False(badLint.Value.IsClean);
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.InsufficientSpotErrorSpans");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.SpotErrorMissingFault");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.SpotErrorConflictingNoError");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.SpotErrorEmptyCorrection");
    }

    [Fact]
    public async Task MixedPhase1Session_HeadlessCdpVerification_PassesZeroErrorsOverflowOrKatexIssues()
    {
        var dir = Path.Combine(_tempRoot, "mixed_p1_dir");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "_session.md"), """
            ---
            kind: session
            name: Phase 1 Integrated Session
            description: Native tables and all 3 Phase 1 question widgets
            ---
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "01-article.md"), """
            # Integrated Physiology

            :::table{title="Electrolyte Shifts" stickyFirstCol=true hideableCols="2,3" caption="Key ion changes"}
            | Condition | Serum $\text{K}^+$ | Serum $\text{Ca}^{2+}$ |
            | :--- | :---: | :---: |
            | Loop diuretics | Decreased | Decreased |
            | Thiazides | Decreased | Increased |
            :::footnote
            Monitor ECG for U waves in severe hypokalemia.
            :::
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "02-quiz.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Phase 1 Mixed Quiz
            ---

            :::mcq
            Which transporter is inhibited by furosemide?
            - [x] NKCC2
            - [ ] NCC
            :::explanation
            Furosemide blocks the $\text{Na}^+/\text{K}^+/2\text{Cl}^-$ cotransporter.
            :::

            :::fill-blank{fuzzy=true}
            The rate-limiting enzyme of glycolysis is {{phosphofructokinase-1 | PFK-1}}.
            :::explanation
            Activated by $\text{F-2,6-BP}$.
            :::

            :::assertion-reason{answer="both-no-explain"}
            A: Aspirin is avoided in children with viral illness.
            R: Aspirin acetylates COX-1 irreversibly.
            :::explanation
            Avoided specifically due to Reye syndrome.
            :::

            :::spot-error{requireFix=true allowNoError=true}
            In [primary hyperparathyroidism], serum calcium is [elevated] and phosphate is [~~elevated~~ -> decreased].
            :::explanation
            PTH decreases renal phosphate reabsorption.
            :::

            :::flashcard
            :::front
            Rate-limiting enzyme of TCA cycle?
            :::back
            Isocitrate dehydrogenase
            :::
            """);

        using var sp = CreateProvider("appdata_p1_cdp");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var verifier = scope.ServiceProvider.GetRequiredService<IHeadlessWebAppVerifier>();

        var wsDir = Path.Combine(_tempRoot, "ws_p1_cdp");
        var webDir = Path.Combine(_tempRoot, "web_p1_cdp");
        var applied = await apply.HandleAsync(new ApplySpecCommand(null, dir, WorkingDirectory: wsDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);

        var built = await build.HandleAsync(new BuildWebAppCommand(applied.Value.TargetId, webDir, StaticVerifyOnly: true));
        Assert.True(built.IsSuccess, built.Error?.Description);
        Assert.True(built.Value.Verify?.Passed);

        var cdpRes = await verifier.VerifyAsync(webDir, staticOnly: false, timeoutSeconds: 30);
        if (!cdpRes.IsSuccess && cdpRes.Error!.Code == "Verify.BrowserNotFound")
            return;

        Assert.True(cdpRes.IsSuccess, cdpRes.Error?.Description);
        Assert.True(cdpRes.Value.Passed,
            $"CDP failed: console=[{string.Join("; ", cdpRes.Value.ConsoleErrors)}], overflow=[{string.Join("; ", cdpRes.Value.OverflowIssues)}], broken=[{string.Join("; ", cdpRes.Value.BrokenAssets)}], katex=[{string.Join("; ", cdpRes.Value.KatexErrors)}]");
        Assert.Equal(2, cdpRes.Value.SlidesChecked);
    }

    [Fact]
    public async Task PrimeAndSchema_ExposeAllFourPhase1Topics()
    {
        using var sp = CreateProvider("appdata_p1_schema");
        using var scope = sp.CreateScope();
        var prime = scope.ServiceProvider.GetRequiredService<GetPrimeGuideHandler>();
        var schema = scope.ServiceProvider.GetRequiredService<GetSchemaHandler>();

        var primeRes = await prime.HandleAsync();
        Assert.True(primeRes.IsSuccess);
        foreach (var topic in new[] { "table", "fill-blank", "assertion-reason", "spot-error" })
            Assert.Contains(topic, primeRes.Value.SpecTopics);

        var schemaRes = await schema.HandleAsync(new GetSchemaQuery(["table", "fill-blank", "assertion-reason", "spot-error", "markdown", "patch"]));
        Assert.True(schemaRes.IsSuccess, schemaRes.Error?.Description);
        Assert.Equal(6, schemaRes.Value.Schemas.Count);
        var markdownSig = schemaRes.Value.Schemas.Single(s => s.Topic == "markdown").CompactSignature;
        Assert.Contains(":::table", markdownSig);
        Assert.Contains(":::fill-blank", markdownSig);
        Assert.Contains(":::assertion-reason", markdownSig);
        Assert.Contains(":::spot-error", markdownSig);
    }
}
