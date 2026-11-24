using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Analysis;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class Phase2DiffAndMechanismTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_phase2b_unit_{Guid.NewGuid():N}");

    public Phase2DiffAndMechanismTests() => Directory.CreateDirectory(_tempRoot);

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
    public async Task PrimeAndSpecSchemas_ExposeDiffAndMechanism_WithinPrimeCharBudget_AndGroupCatalog()
    {
        using var sp = CreateProvider("appdata_specs");
        using var scope = sp.CreateScope();
        var prime = scope.ServiceProvider.GetRequiredService<GetPrimeGuideHandler>();
        var schema = scope.ServiceProvider.GetRequiredService<GetSchemaHandler>();

        var primeRes = await prime.HandleAsync();
        Assert.True(primeRes.IsSuccess);
        Assert.Contains("diff", primeRes.Value.SpecTopics);
        Assert.Contains("mechanism", primeRes.Value.SpecTopics);
        Assert.Contains("session|article|quiz|text|image|mcq|flashcard|layouts", primeRes.Value.Guide);
        var crlfGuide = primeRes.Value.Guide.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.True(crlfGuide.Length < 1550, $"Expected CRLF guide < 1550 chars, got {crlfGuide.Length}");

        var specRes = await schema.HandleAsync(new GetSchemaQuery(["diff", "mechanism", "quiz", "markdown", "patch"]));
        Assert.True(specRes.IsSuccess, specRes.Error?.Description);
        Assert.Equal(5, specRes.Value.Schemas.Count);

        var diffSchema = specRes.Value.Schemas.Single(s => s.Topic == "diff");
        Assert.Equal("content", diffSchema.Scope);
        Assert.Equal("question", diffSchema.Category);
        Assert.Contains(@"\|", diffSchema.CompactSignature);
        Assert.Contains("{{", diffSchema.CompactSignature);

        var mechSchema = specRes.Value.Schemas.Single(s => s.Topic == "mechanism");
        Assert.Equal("content", mechSchema.Scope);
        Assert.Equal("question", mechSchema.Category);
        Assert.Contains("orderGroup", mechSchema.CompactSignature);

        Assert.Contains("Differentiation", specRes.Value.Schemas.Single(s => s.Topic == "quiz").CompactSignature);
        var mdSig = specRes.Value.Schemas.Single(s => s.Topic == "markdown").CompactSignature;
        Assert.Contains(":::diff", mdSig);
        Assert.Contains(":::mechanism", mdSig);
        var patchSig = specRes.Value.Schemas.Single(s => s.Topic == "patch").CompactSignature;
        Assert.Contains("diff:", patchSig);
        Assert.Contains("mechanism:", patchSig);
    }

    [Fact]
    public async Task Differentiation_AllThreeModes_MarkdownAndJson_RoundTripPatchLintAndWebApp()
    {
        using var sp = CreateProvider("appdata_diff");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var loader = scope.ServiceProvider.GetRequiredService<IDeclarativeSpecLoader>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var wsDir = Path.Combine(_tempRoot, "ws_diff");

        var md = """
            ---
            kind: quiz
            quizType: mixed
            title: Differentiation Suite
            ---

            :::diff{ref="diff-spot" mode="spot-wrong" title="Gram Wall Comparison"}
            | Property | Gram-Positive | Gram-Negative |
            | :--- | :--- | :--- |
            | Peptidoglycan | Thick | Thin |
            | Outer membrane | ~~Present~~ -> Absent \| None | Present |
            | Endotoxin | ~~Present~~ -> {{Absent | Zero}} | Present |
            :::explanation
            Gram-positive bacteria lack an outer lipid membrane and LPS.
            :::

            :::diff{ref="diff-noerr" mode="spot-wrong" allowNoError=true}
            | Parameter | Acidosis | Alkalosis |
            | :--- | :--- | :--- |
            | Arterial pH | < 7.35 | > 7.45 |
            :::

            :::diff{ref="diff-fill" mode="fill" bank=true fuzzy=true title="$\text{PaCO}_2$ vs $\text{HCO}_3^-$"}
            | Disorder | Primary Change | Compensation |
            | :--- | :--- | :--- |
            | Respiratory Acidosis | {{Increased PaCO2 | High PaCO2}} | Renal $\text{HCO}_3^-$ retention |
            | Metabolic Acidosis | {{Decreased $\text{HCO}_3^-$ | Low HCO3-}} | {{Hyperventilation | Respiratory}} |
            :::

            :::diff{ref="diff-rearr" mode="rearrange" title="Type 1 vs Type 2 DM"}
            | Feature | Type 1 DM | Type 2 DM |
            | :--- | :--- | :--- |
            | Pathogenesis | Autoimmune $\beta$-cell loss | Insulin resistance |
            | Typical onset | Childhood | Adulthood |
            | Chronic hyperglycemia | Yes | Yes |
            :::
            """;

        var mdRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: md, WorkingDirectory: wsDir));
        Assert.True(mdRes.IsSuccess, mdRes.Error?.Description);
        var quizId = mdRes.Value.TargetId;

        var lintRes = await lint.HandleAsync(new LintCommand(quizId));
        Assert.True(lintRes.IsSuccess && lintRes.Value.IsClean);
        Assert.Equal(0, lintRes.Value.ErrorCount);
        Assert.Equal(0, lintRes.Value.WarningCount);

        var loaded = await repo.FindQuizContainerAsync(quizId);
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Quiz.Questions.Count);

        var spotPq = Assert.IsType<PayloadQuestion>(loaded.Quiz.Questions[0]);
        Assert.Equal(QuestionType.Differentiation, spotPq.Type);
        Assert.True(Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(spotPq.PayloadJson, out var spotP));
        Assert.Equal("spot-wrong", spotP!.Mode);
        Assert.Equal(2, spotP.SpecialCells.Count);
        Assert.Equal(["Absent", "None"], spotP.SpecialCells[0].Corrections);
        Assert.Equal(["Absent", "Zero"], spotP.SpecialCells[1].Corrections);

        var fillPq = Assert.IsType<PayloadQuestion>(loaded.Quiz.Questions[2]);
        Assert.True(Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(fillPq.PayloadJson, out var fillP));
        Assert.Equal("fill", fillP!.Mode);
        Assert.True(fillP.Bank);
        Assert.Equal(3, fillP.SpecialCells.Count);

        // Outline & Full inspect
        var outline = await inspect.HandleAsync(new InspectEntityQuery($"{quizId}#diff-noerr", InspectViewMode.Outline));
        Assert.True(outline.IsSuccess);
        Assert.Contains("[diff:spot-wrong] Acidosis vs Alkalosis", outline.Value.Question!.Snippet);

        var full = await inspect.HandleAsync(new InspectEntityQuery($"{quizId}#diff-fill", InspectViewMode.Full));
        Assert.True(full.IsSuccess);
        Assert.NotNull(full.Value.Question!.PayloadJson);

        // Patch: title, bank=false, and rows recomputing SpecialCells; malformed {{unclosed fails fast
        var pOk = await patch.HandleAsync(new PatchEntityCommand(
            $"{quizId}#diff-fill",
            InlineJson: """{"title":"Updated Diff","bank":false,"rows":[["Resp","{{High PaCO2}}","Renal"]]}"""));
        Assert.True(pOk.IsSuccess, pOk.Error?.Description);
        var reloadedFill = (await repo.FindQuizContainerAsync(quizId))!.Quiz.Questions[2];
        Assert.True(Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(reloadedFill.PayloadJson, out var patchedFill));
        Assert.Equal("Updated Diff", patchedFill!.Title);
        Assert.False(patchedFill.Bank);
        Assert.Single(patchedFill.SpecialCells);

        var pBadBlank = await patch.HandleAsync(new PatchEntityCommand(
            $"{quizId}#diff-fill",
            InlineJson: """{"rows":[["Resp","{{unclosed","Renal"]]}"""));
        Assert.False(pBadBlank.IsSuccess);
        Assert.Equal("Diff.MalformedBlankCell", pBadBlank.Error!.Code);

        var pBadWidth = await patch.HandleAsync(new PatchEntityCommand(
            $"{quizId}#diff-fill",
            InlineJson: """{"rows":[["OnlyOneCol"]]}"""));
        Assert.False(pBadWidth.IsSuccess);
        Assert.Equal("Patch.InvalidValue", pBadWidth.Error!.Code);

        // .mbypkg v2.0 export & import round-trip + corrupted diff .mbypkg rejection
        var pkgPath = Path.Combine(_tempRoot, "diff.mbypkg");
        Assert.True((await export.HandleAsync(new ExportPackageCommand(quizId, pkgPath, "android"))).IsSuccess);
        Assert.True((await import.HandleAsync(new ImportPackageCommand(pkgPath, Path.Combine(_tempRoot, "diff_reimp")))).IsSuccess);

        var badDiffPkg = Path.Combine(_tempRoot, "bad_diff.mbypkg");
        File.Copy(pkgPath, badDiffPkg);
        using (var zip = ZipFile.Open(badDiffPkg, ZipArchiveMode.Update))
        {
            var mEntry = zip.GetEntry("manifest.json")!;
            string mText;
            using (var sr = new StreamReader(mEntry.Open(), Encoding.UTF8)) mText = await sr.ReadToEndAsync();
            mText = mText.Replace(quizId, "Set_20261124_200000_001", StringComparison.Ordinal);
            mEntry.Delete();
            await using (var s = zip.CreateEntry("manifest.json").Open()) await s.WriteAsync(Encoding.UTF8.GetBytes(mText));

            var wEntry = zip.GetEntry("workspace.json")!;
            wEntry.Delete();
            await using (var s = zip.CreateEntry("workspace.json").Open())
                await s.WriteAsync(Encoding.UTF8.GetBytes("""{"id":"Set_20261124_200000_001","quizType":"Mixed","questions":[{"id":"Q_20261124_200000_001","index":1,"questionType":"Differentiation","payloadJson":"{\"mode\":\"fill\",\"headers\":[\"A\",\"B\"],\"rows\":null}"}]}"""));
        }
        var badDiffImp = await import.HandleAsync(new ImportPackageCommand(badDiffPkg, Path.Combine(_tempRoot, "bad_diff_imp")));
        Assert.False(badDiffImp.IsSuccess);
        Assert.Equal("Package.InvalidWorkspace", badDiffImp.Error!.Code);

        // WebApp build + KaTeX bundling + SEARCH_INDEX
        var webDir = Path.Combine(_tempRoot, "diff_web");
        var buildRes = await build.HandleAsync(new BuildWebAppCommand(quizId, webDir, StaticVerifyOnly: true));
        Assert.True(buildRes.IsSuccess && buildRes.Value.Verify?.Passed == true);
        Assert.True(File.Exists(Path.Combine(webDir, "katex", "katex.min.js")));
        var css = await File.ReadAllTextAsync(Path.Combine(webDir, "styles.css"));
        var js = await File.ReadAllTextAsync(Path.Combine(webDir, "app.js"));
        Assert.Contains(".mby-diff-table", css);
        Assert.Contains("buildDiffCard", js);
        Assert.Contains("deterministicShuffle", js);
        Assert.Contains("Peptidoglycan", js);

        // Strict JSON authoring with shared fields (title, fuzzy, caseSensitive, allowNoError)
        var jsonSpec = """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "questions": [
                {
                  "type": "diff",
                  "ref": "json-diff",
                  "mode": "fill",
                  "title": "JSON Diff",
                  "bank": true,
                  "fuzzy": true,
                  "caseSensitive": false,
                  "allowNoError": false,
                  "headers": ["Feature", "A", "B"],
                  "rows": [["Site", "{{Loop}}", "Distal"]]
                }
              ]
            }
            """;
        var jsonRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: jsonSpec, WorkingDirectory: wsDir));
        Assert.True(jsonRes.IsSuccess, jsonRes.Error?.Description);
        Assert.True((await lint.HandleAsync(new LintCommand(jsonRes.Value.TargetId))).Value.IsClean);

        // Markdown parse-time fail-fast checks
        Assert.Equal("Markdown.InvalidDirectiveAttr", (await loader.LoadFromTextAsync("---\nkind: quiz\n---\n:::diff{mode=\"bogus\"}\n| A | B |\n|---|---|\n| 1 | 2 |\n:::")).Error!.Code);
        Assert.Equal("Markdown.InvalidDirectiveAttr", (await loader.LoadFromTextAsync("---\nkind: quiz\n---\n:::diff{mode=\"fill\"} extra\n| A | B |\n|---|---|\n| {{1}} | 2 |\n:::")).Error!.Code);
        Assert.Equal("Markdown.UnclosedDirective", (await loader.LoadFromTextAsync("---\nkind: quiz\n---\n:::mcq\nStem?\n- [x] A\n- [ ] B\n:::diff{mode=\"fill\"}\n| A | B |\n|---|---|\n| {{1}} | 2 |\n:::")).Error!.Code);
        Assert.Equal("Markdown.InvalidDiff", (await loader.LoadFromTextAsync("---\nkind: quiz\n---\n:::diff{mode=\"fill\"}\n| A | B |\n|---|---|\n| 1 | 2 |\n:::")).Error!.Code);

        // JSON lint-time checks
        var badJson = """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "questions": [
                { "type": "diff", "mode": "bogus", "headers": ["A", "B"], "rows": [["1", "2"]] },
                { "type": "diff", "mode": "fill", "headers": ["A", "B"], "rows": [["1", "2"]] },
                { "type": "diff", "mode": "spot-wrong", "allowNoError": false, "headers": ["A", "B"], "rows": [["1", "2"]] },
                { "type": "diff", "mode": "rearrange", "headers": ["Feature", "OnlyOneEntity"], "rows": [["X", "Y"]] }
              ]
            }
            """;
        var badApplied = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badJson, WorkingDirectory: wsDir));
        Assert.True(badApplied.IsSuccess, badApplied.Error?.Description);
        var badLint = await lint.HandleAsync(new LintCommand(badApplied.Value.TargetId));
        Assert.False(badLint.Value.IsClean);
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.InvalidDiffMode");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.DiffMissingBlanks");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.DiffMissingFaults");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.InvalidDiffTable");
    }

    [Fact]
    public async Task Mechanism_OrderedParallelAndDistractors_RoundTripsPatchLintAndWebApp()
    {
        using var sp = CreateProvider("appdata_mech");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var loader = scope.ServiceProvider.GetRequiredService<IDeclarativeSpecLoader>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var wsDir = Path.Combine(_tempRoot, "ws_mech");

        var md = """
            ---
            kind: quiz
            quizType: mixed
            title: Mechanism Quiz
            ---

            :::mechanism{ref="mech-raas" title="RAAS Cascade"}
            1. Decreased renal perfusion triggers juxtaglomerular renin release
            2. Renin cleaves angiotensinogen into Angiotensin I
            3. ACE converts Angiotensin I to Angiotensin II
            4a. Angiotensin II causes efferent arteriolar vasoconstriction
            4b. Angiotensin II stimulates adrenal $\text{Aldosterone}$ secretion
            ! ANP directly converts Angiotensin I into Angiotensin II
            :::explanation
            ANP inhibits renin and aldosterone release.
            :::layout
            cardMaxWidthPx: 880
            :::
            """;

        var mdRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: md, WorkingDirectory: wsDir));
        Assert.True(mdRes.IsSuccess, mdRes.Error?.Description);
        var quizId = mdRes.Value.TargetId;

        var lintRes = await lint.HandleAsync(new LintCommand(quizId));
        Assert.True(lintRes.IsSuccess && lintRes.Value.IsClean);

        var loaded = await repo.FindQuizContainerAsync(quizId);
        var pq = Assert.IsType<PayloadQuestion>(Assert.Single(loaded!.Quiz.Questions));
        Assert.Equal(QuestionType.Mechanism, pq.Type);
        Assert.Equal("880", pq.LayoutOverrides["cardMaxWidthPx"]);
        Assert.True(Phase1PayloadCodec.TryDeserialize<MechanismPayload>(pq.PayloadJson, out var mp));
        Assert.Equal(6, mp!.Steps.Count);
        Assert.Equal([1, 2, 3, 4, 4], mp.Steps.Where(s => !s.IsDistractor).Select(s => s.OrderGroup));
        Assert.Single(mp.Steps, s => s.IsDistractor);

        // Outline inspect & SessionAnalysisReport formatting
        var outline = await inspect.HandleAsync(new InspectEntityQuery($"{quizId}#mech-raas", InspectViewMode.Outline));
        Assert.Contains("[mechanism] RAAS Cascade (5 steps)", outline.Value.Question!.Snippet);

        var report = new SessionAnalysisReport(
            "1.0", quizId, "Mechanism Quiz", "2026-11-24T00:00:00Z", "2026-11-24T00:01:00Z", 60000,
            new SessionAnalysisSummary(2, 2, 2, 2, 100.0, 0, 0, 0, 0),
            [
                new SlideAnalysisEntry(1, "Q_1", "diff", 10000, 1, 1, null, null, true, true, null, null),
                new SlideAnalysisEntry(2, pq.Id, "mechanism", 12000, 1, 1, null, null, true, true, null, null)
            ],
            []);
        var mdReport = report.ToMarkdownReport();
        Assert.Contains("| `Q_1` | diff |", mdReport);
        Assert.Contains("Correct: Yes, Checks: 1", mdReport);

        // Patch title via --set and steps via JSON
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{quizId}#mech-raas", SetPairs: ["title=Updated RAAS"]))).IsSuccess);
        var pSteps = await patch.HandleAsync(new PatchEntityCommand(
            $"{quizId}#mech-raas",
            InlineJson: """{"steps":[{"orderGroup":1,"text":"Step A"},{"orderGroup":2,"text":"Step B"},{"isDistractor":true,"text":"Decoy C"}]}"""));
        Assert.True(pSteps.IsSuccess, pSteps.Error?.Description);

        // .mbypkg round-trip & WebApp build
        var pkgPath = Path.Combine(_tempRoot, "mech.mbypkg");
        Assert.True((await export.HandleAsync(new ExportPackageCommand(quizId, pkgPath, "desktop"))).IsSuccess);
        Assert.True((await import.HandleAsync(new ImportPackageCommand(pkgPath, Path.Combine(_tempRoot, "mech_reimp")))).IsSuccess);

        var webDir = Path.Combine(_tempRoot, "mech_web");
        var buildRes = await build.HandleAsync(new BuildWebAppCommand(quizId, webDir, StaticVerifyOnly: true));
        Assert.True(buildRes.IsSuccess && buildRes.Value.Verify?.Passed == true);
        var css = await File.ReadAllTextAsync(Path.Combine(webDir, "styles.css"));
        var js = await File.ReadAllTextAsync(Path.Combine(webDir, "app.js"));
        Assert.Contains(".mby-mech-bank", css);
        Assert.Contains(".mby-mech-seq", css);
        Assert.Contains("buildMechanismCard", js);

        // Strict JSON authoring
        var jsonMech = """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "questions": [
                {
                  "type": "mechanism",
                  "title": "Coagulation",
                  "steps": [
                    { "stepId": 1, "orderGroup": 1, "text": "Tissue factor exposure" },
                    { "stepId": 2, "orderGroup": 2, "text": "Thrombin burst" },
                    { "stepId": 3, "isDistractor": true, "text": "Plasminogen activation" }
                  ]
                }
              ]
            }
            """;
        var jsonApplied = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: jsonMech, WorkingDirectory: wsDir));
        Assert.True(jsonApplied.IsSuccess, jsonApplied.Error?.Description);
        Assert.True((await lint.HandleAsync(new LintCommand(jsonApplied.Value.TargetId))).Value.IsClean);

        // Markdown parse-time duplicate bare step number
        var dupBare = await loader.LoadFromTextAsync("---\nkind: quiz\n---\n:::mechanism\n1. First\n2. Second\n2. Duplicate bare two\n:::");
        Assert.False(dupBare.IsSuccess);
        Assert.Equal("Markdown.InvalidMechanism", dupBare.Error!.Code);

        // JSON lint-time errors + warning on excessive distractors
        var badMechJson = """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "questions": [
                { "type": "mechanism", "steps": [{ "orderGroup": 1, "text": "Only one" }, { "isDistractor": true, "text": "Decoy" }] },
                { "type": "mechanism", "steps": [{ "orderGroup": 1, "text": "Same" }, { "orderGroup": 2, "text": "same" }] },
                { "type": "mechanism", "steps": [{ "orderGroup": 1, "text": "Valid" }, { "orderGroup": 2, "text": "   " }] }
              ]
            }
            """;
        var badMechApp = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badMechJson, WorkingDirectory: wsDir));
        var badMechLint = await lint.HandleAsync(new LintCommand(badMechApp.Value.TargetId));
        Assert.False(badMechLint.Value.IsClean);
        Assert.Contains(badMechLint.Value.Issues, i => i.Code == "Lint.InsufficientMechanismSteps");
        Assert.Contains(badMechLint.Value.Issues, i => i.Code == "Lint.DuplicateMechanismStep");
        Assert.Contains(badMechLint.Value.Issues, i => i.Code == "Lint.EmptyMechanismStep");

        var warnMechJson = """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "questions": [
                {
                  "type": "mechanism",
                  "steps": [
                    { "orderGroup": 1, "text": "Ordered 1" },
                    { "orderGroup": 2, "text": "Ordered 2" },
                    { "isDistractor": true, "text": "Decoy 1" },
                    { "isDistractor": true, "text": "Decoy 2" },
                    { "isDistractor": true, "text": "Decoy 3" }
                  ]
                }
              ]
            }
            """;
        var warnApp = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: warnMechJson, WorkingDirectory: wsDir));
        var warnLint = await lint.HandleAsync(new LintCommand(warnApp.Value.TargetId));
        Assert.True(warnLint.Value.IsClean);
        Assert.Equal(0, warnLint.Value.ErrorCount);
        Assert.Equal(1, warnLint.Value.WarningCount);
        Assert.Contains(warnLint.Value.Issues, i => i.Code == "Lint.ExcessiveMechanismDistractors" && i.Severity == "Warning");
    }

    [Fact]
    public async Task DiffAndMechanism_HeadlessCdpVerification_PassesZeroErrorsOrOverflow()
    {
        using var sp = CreateProvider("appdata_cdp");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var verifier = scope.ServiceProvider.GetRequiredService<IHeadlessWebAppVerifier>();
        var wsDir = Path.Combine(_tempRoot, "ws_cdp");

        var modDir = Path.Combine(_tempRoot, "cdp_session");
        Directory.CreateDirectory(modDir);
        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), "---\nkind: session\nname: Phase 2B CDP Verification\n---\n");
        await File.WriteAllTextAsync(Path.Combine(modDir, "01-drills.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Interactive Drills
            ---

            :::diff{mode="spot-wrong" allowNoError=true title="Gram $\text{Wall}$"}
            | Property | Gram+ | Gram- |
            | :--- | :--- | :--- |
            | Outer membrane | ~~Present~~ -> $\text{Absent}$ | Present |
            :::

            :::diff{mode="fill" bank=true title="Acid-Base"}
            | Disorder | Primary |
            | :--- | :--- |
            | Respiratory Acidosis | {{$\text{PaCO}_2$ rise | High PaCO2}} |
            :::

            :::diff{mode="rearrange" title="Diabetes Comparison"}
            | Feature | Type 1 | Type 2 |
            | :--- | :--- | :--- |
            | Onset | Childhood | Adulthood |
            | Hyperglycemia | Present | Present |
            :::

            :::mechanism{title="RAAS Pathway"}
            1. Renin release
            2. Angiotensin I formation
            3a. Efferent vasoconstriction
            3b. Aldosterone secretion
            ! ANP stimulates renin
            :::
            """);

        var applied = await apply.HandleAsync(new ApplySpecCommand(null, modDir, WorkingDirectory: wsDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);

        var webDir = Path.Combine(_tempRoot, "cdp_web");
        var built = await build.HandleAsync(new BuildWebAppCommand(applied.Value.TargetId, webDir, StaticVerifyOnly: true));
        Assert.True(built.IsSuccess && built.Value.Verify?.Passed == true);

        var cdpRes = await verifier.VerifyAsync(webDir, staticOnly: false, timeoutSeconds: 30);
        if (cdpRes.IsSuccess || cdpRes.Error?.Code != "Verify.BrowserNotFound")
        {
            Assert.True(cdpRes.IsSuccess, cdpRes.Error?.Description);
            Assert.True(cdpRes.Value.Passed,
                $"CDP failed: console=[{string.Join("; ", cdpRes.Value.ConsoleErrors)}], overflow=[{string.Join("; ", cdpRes.Value.OverflowIssues)}], broken=[{string.Join("; ", cdpRes.Value.BrokenAssets)}], katex=[{string.Join("; ", cdpRes.Value.KatexErrors)}]");
            Assert.Empty(cdpRes.Value.ConsoleErrors);
            Assert.Empty(cdpRes.Value.OverflowIssues);
            Assert.Empty(cdpRes.Value.KatexErrors);
        }
    }
}
