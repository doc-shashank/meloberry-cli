using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Layouts;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Analysis;
using Meloberry.Domain.Layouts;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class LayoutAndWebAppTests : IDisposable
{
    private readonly WorkspaceTestFixture _fx = new("mby_layout_webapp");

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task LayoutProfiles_And_EditorBuilder_EnforceAtomicityBoundsGlobalsAndParity()
    {
        using var sp = _fx.CreateProvider();
        var saveHandler = sp.GetRequiredService<SaveLayoutProfilesHandler>();
        var setHandler = sp.GetRequiredService<SetLayoutPropertyHandler>();
        var resetHandler = sp.GetRequiredService<ResetLayoutProfileHandler>();
        var getBounds = sp.GetRequiredService<GetLayoutBoundsHandler>();

        // Atomic failure on mixed valid + out-of-bounds leaves DB unchanged
        var failSave = await saveHandler.HandleAsync(new SaveLayoutProfilesCommand(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["mcq"] = new Dictionary<string, string> { ["optionColumns"] = "2", ["cardMaxWidthPx"] = "99999" }
        }));
        Assert.True(failSave.IsSuccess && !failSave.Value.Saved);
        Assert.Equal("1", (await getBounds.HandleAsync(new GetLayoutBoundsQuery("mcq"))).Value.Properties.First(p => p.Property == "optionColumns").CurrentValue);

        // Valid batch saves atomically and syncs globals across all profiles
        var okSave = await saveHandler.HandleAsync(new SaveLayoutProfilesCommand(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["mcq"] = new Dictionary<string, string> { ["optionColumns"] = "2", ["themeMode"] = "dark", ["slideTransitionMs"] = "375" },
            ["article"] = new Dictionary<string, string> { ["blockSpacingPx"] = "28" }
        }));
        Assert.True(okSave.IsSuccess && okSave.Value.Saved);
        var allProps = (await getBounds.HandleAsync(new GetLayoutBoundsQuery(null))).Value.Properties;
        Assert.Equal("2", allProps.First(p => p.TargetType == "mcq" && p.Property == "optionColumns").CurrentValue);
        Assert.Equal("28", allProps.First(p => p.TargetType == "article" && p.Property == "blockSpacingPx").CurrentValue);
        Assert.All(allProps.Where(p => p.Property == "themeMode"), p => Assert.Equal("dark", p.CurrentValue));
        Assert.All(allProps.Where(p => p.Property == "slideTransitionMs"), p => Assert.Equal("375", p.CurrentValue));

        // Reset guards + scoped reset preserving globals + full reset
        Assert.Equal("Layout.EmptyResetKeys", (await resetHandler.HandleAsync(new ResetLayoutProfileCommand("mcq", []))).Error!.Code);
        Assert.Equal("Layout.UnknownProperty", (await resetHandler.HandleAsync(new ResetLayoutProfileCommand("mcq", ["notAProperty"]))).Error!.Code);
        Assert.True((await resetHandler.HandleAsync(new ResetLayoutProfileCommand("mcq", ["optionColumns"]))).IsSuccess);
        var afterScoped = (await getBounds.HandleAsync(new GetLayoutBoundsQuery("mcq"))).Value.Properties;
        Assert.Equal("1", afterScoped.First(p => p.Property == "optionColumns").CurrentValue);
        Assert.Equal("dark", afterScoped.First(p => p.Property == "themeMode").CurrentValue);

        Assert.True((await resetHandler.HandleAsync(new ResetLayoutProfileCommand("mcq", null))).IsSuccess);
        Assert.Equal("berry", (await getBounds.HandleAsync(new GetLayoutBoundsQuery("article"))).Value.Properties.First(p => p.Property == "themeMode").CurrentValue);

        // LayoutBoundsCatalog checks
        Assert.Equal("Layout.GlobalPropertyOnEntity", LayoutBoundsCatalog.ValidateAndResolveForEntityOverride("mcq", "themeMode", "dark").Error!.Code);
        Assert.Equal("Layout.GlobalPropertyOnEntity", LayoutBoundsCatalog.ValidateAndResolveForEntityOverride(["mcq", "flashcard"], "slideTransitionMs", "500").Error!.Code);
        Assert.Equal("Layout.OutOfBounds", LayoutBoundsCatalog.ValidateAndResolve(["mcq", "flashcard"], "flipDurationMs", "9999").Error!.Code);

        // Tool metadata & LayoutEditorPageBuilder HTML checks
        var domainMeta = new AssemblyCliToolMetadataProvider(typeof(Meloberry.Domain.Common.Result).Assembly, "mby").GetMetadata();
        var appMeta = new AssemblyCliToolMetadataProvider(typeof(Meloberry.Application.DependencyInjection).Assembly, "mby").GetMetadata();
        var infraMeta = sp.GetRequiredService<ICliToolMetadataProvider>().GetMetadata();
        Assert.Equal("2.2.0-beta-1", domainMeta.Version);
        Assert.Equal("2.2.0-beta-1", appMeta.Version);
        Assert.Equal("2.2.0-beta-1", infraMeta.Version);
        Assert.Equal("Meloberry", infraMeta.AppName);
        Assert.Equal("Meloberry CLI", infraMeta.ProductName);
        Assert.Equal("mby", infraMeta.ExecutableName);
        Assert.Equal("https://github.com/doc-shashank/meloberry-cli/", infraMeta.RepositoryUrl);

        var editorHtml = sp.GetRequiredService<ILayoutEditorPageBuilder>().BuildHtml("relabel", infraMeta, "csrf-sample-123");
        Assert.Contains("<meta name=\"generator\" content=\"Meloberry CLI (mby) 2.2.0-beta-1\"", editorHtml);
        Assert.Contains("EDITOR_CSRF_TOKEN = \"csrf-sample-123\"", editorHtml);
        Assert.Contains("X-Mby-Editor-Token", editorHtml);
        Assert.Contains("mby v2.2.0-beta-1", editorHtml);
        Assert.Contains("data-theme=\"paper\"", editorHtml);
        foreach (var targetId in new[] { "article-text", "callout", "table", "image", "mcq-question", "mcq-options", "fill-blank", "assertion-reason", "spot-error", "diff", "mechanism", "relabel", "flashcard" })
            Assert.Contains($"id: '{targetId}'", editorHtml);
        _ = setHandler;
    }

    [Fact]
    public async Task WebAppGenerator_SanitizesXss_BundlesKatexForNestedMath_ExcludesUnusedAssets_And_FormatsReports()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var setLayout = scope.ServiceProvider.GetRequiredService<SetLayoutPropertyHandler>();
        var reportStore = scope.ServiceProvider.GetRequiredService<IAnalysisReportStore>();
        var analyze = scope.ServiceProvider.GetRequiredService<AnalyzeWebAppHandler>();
        var webGen = scope.ServiceProvider.GetRequiredService<IWebAppGenerator>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        // 1. Math only in callout title still bundles KaTeX
        var titleMathApplied = (await apply.HandleAsync(new ApplySpecCommand(null, null,
            "---\ntitle: Title Math\n---\n\n:::callout{tone=\"pearl\" title=\"$\\text{Na}^+$ Channel\"}\nPlain body.\n:::\n", _fx.WorkDir))).Value;
        var titleMathWeb = Path.Combine(_fx.TempRoot, "web_title_math");
        Assert.True((await build.HandleAsync(new BuildWebAppCommand(titleMathApplied.TargetId, titleMathWeb, StaticVerifyOnly: true))).IsSuccess);
        Assert.True(File.Exists(Path.Combine(titleMathWeb, "katex", "katex.min.js")));

        // 2. XSS stripping, double-dot filename encoding, defaultTextPosition, KaTeX inside fill-blank/spot-error, unused asset exclusion
        var srcDir = _fx.SubDir("web_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "diagram..final v2.png"));
        var specPath = Path.Combine(srcDir, "spec.json");
        await File.WriteAllTextAsync(specPath, """
        {
          "kind": "quiz",
          "quizType": "mixed",
          "name": "Web Security & Math",
          "questions": [
            { "type": "flashcard", "front": { "text": "<script>alert(1)</script> [x](javascript:alert(2))", "imagePath": "diagram..final v2.png" }, "back": { "text": "Back **safe**" } },
            { "type": "fill-blank", "text": "Compute value: {{$\\sqrt{4}$ | 2}}." },
            { "type": "spot-error", "requireFix": true, "text": "Given [x is real], result is [~~2~~ -> $\\sqrt{4}$]." }
          ]
        }
        """);
        Assert.True((await setLayout.HandleAsync(new SetLayoutPropertyCommand("flashcard", "defaultTextPosition", "aboveImage"))).IsSuccess);
        var applied = (await apply.HandleAsync(new ApplySpecCommand(null, specPath, WorkingDirectory: _fx.WorkDir))).Value;
        await TestAssets.WritePngAsync(Path.Combine(applied.StorageDirectory, "assets", "unused.png"));

        var webOut = Path.Combine(_fx.TempRoot, "web_sec");
        var built = await build.HandleAsync(new BuildWebAppCommand(applied.TargetId, webOut, StaticVerifyOnly: true));
        Assert.True(built.IsSuccess && built.Value.Verify?.Passed == true);
        Assert.False(File.Exists(Path.Combine(webOut, "assets", "unused.png")));
        Assert.True(File.Exists(Path.Combine(webOut, "katex", "katex.min.js")));

        var html = await File.ReadAllTextAsync(built.Value.IndexHtmlPath);
        var js = await File.ReadAllTextAsync(Path.Combine(webOut, "app.js"));
        Assert.Contains("<meta name=\"generator\" content=\"Meloberry CLI (mby) 2.2.0-beta-1\"", html);
        Assert.DoesNotContain("<script>alert(1)</script>", js);
        Assert.DoesNotContain("javascript:alert(2)", js);
        Assert.Contains("aboveImage", js);
        Assert.Contains("diagram_final%20v2", js);
        Assert.Contains("primaryAnswerHtml", js);
        Assert.Contains("primaryCorrectionHtml", js);

        // 3. Corrupted in-DB FillBlank payload does not crash WebAppGenerator
        var corruptStorage = _fx.SubDir("work", "Set_20261124_120000_993");
        Directory.CreateDirectory(Path.Combine(corruptStorage, "assets"));
        var corruptQuiz = Quiz.Rehydrate("Set_20261124_120000_993", QuizType.Mixed, corruptStorage,
            [PayloadQuestion.Rehydrate("Q_20261124_120000_993", "Set_20261124_120000_993", 1, QuestionType.FillBlank, """{"template":"Enzyme is {{PFK-1}}."}""")], "Corrupt");
        await repo.CreateQuizAsync(corruptQuiz);
        Assert.True((await webGen.GenerateForQuizAsync(corruptQuiz, corruptStorage, new Dictionary<string, LayoutProfile>(), Path.Combine(_fx.TempRoot, "corrupt_web"))).IsSuccess);

        // 4. SessionAnalysisReport & AnalyzeWebAppHandler multi-run aggregation + Graded labels
        var sampleReport = new SessionAnalysisReport(
            "1.0", applied.TargetId, "Web Check",
            "2026-11-24T00:00:00Z", "2026-11-24T00:01:00Z", 60000,
            new SessionAnalysisSummary(2, 1, 1, 1, 100.0, 0, 0, 0, 1),
            [new SlideAnalysisEntry(1, "Question_1", "fill-blank", 15000, 1, 1, null, null, true, true, null, null)],
            []);
        var mdReport = sampleReport.ToMarkdownReport();
        Assert.Contains("- **Graded Questions:**", mdReport);
        Assert.DoesNotContain("- **MCQs:**", mdReport);

        var reportsWeb = _fx.SubDir("reports_web");
        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Assert.True((await reportStore.SaveReportToWebAppDirectoryAsync(reportsWeb, JsonSerializer.Serialize(sampleReport, jsonOpts))).IsSuccess);
        await Task.Delay(15);
        Assert.True((await reportStore.SaveReportToWebAppDirectoryAsync(reportsWeb, JsonSerializer.Serialize(sampleReport with { CompletedAtUtc = "2026-11-24T00:02:00Z" }, jsonOpts))).IsSuccess);

        var aggRes = await analyze.HandleAsync(new AnalyzeWebAppQuery(reportsWeb, IncludeAllRuns: true));
        Assert.True(aggRes.IsSuccess);
        Assert.Equal(2, aggRes.Value.TotalRunsFound);
        Assert.Contains("- **Average Graded Accuracy:**", aggRes.Value.MarkdownSummary);
        Assert.DoesNotContain("- **Average MCQ Accuracy:**", aggRes.Value.MarkdownSummary);
    }

    [Fact]
    public async Task HeadlessCdpVerifier_PassesMasterShowcaseAll18Components_And_CatchesQ2Overflow()
    {
        using var sp = _fx.CreateProvider("appdata_cdp");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var verifier = scope.ServiceProvider.GetRequiredService<IHeadlessWebAppVerifier>();

        var modDir = _fx.SubDir("cdp_master");
        await TestAssets.WritePngAsync(Path.Combine(modDir, "assets", "heart.png"));
        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), "---\nkind: session\nname: Master CDP Showcase\n---\n");

        await File.WriteAllTextAsync(Path.Combine(modDir, "01-article.md"), """
            ---
            title: Master Article Blocks
            ref: art-master
            ---

            # Overview

            Normal paragraph with $x^2 + y^2 = z^2$ and [Pearl Card](mby://card/card-1).

            ![Heart Diagram](assets/heart.png "Fig. 1 Cardiac chambers")

            :::callout{tone="pearl" title="Serum $\text{Na}^+$ Note"}
            Hyponatremia shifts water intracellularly.
            :::

            :::link{id="card-1" title="Pearl Card"}
            Quick reference card with $\Delta G$.
            :::

            :::table{title="Serum $\text{Ca}^{2+}$ Balance" stickyFirstCol=true hideableCols="2,3" caption="Key changes"}
            | Condition | Serum $\text{K}^+$ | Serum $\text{Ca}^{2+}$ |
            | :--- | :---: | :---: |
            | Loop diuretics | Decreased | Decreased |
            | Thiazides | Decreased | Increased |
            :::footnote
            Monitor ECG for U waves.
            :::

            :::diagram-explore{image="assets/heart.png" title="Chambers"}
            boxes:
              - label: "Right Atrium"
                rect: [0.15, 0.20, 0.22, 0.18]
                note: "Receives venous return ($\text{SVC}/\text{IVC}$)."
              - label: "Left Ventricle"
                rect: [0.55, 0.45, 0.25, 0.22]
                note: "Pumps oxygenated blood into the aorta."
            :::

            :::reveal{title="Frank-Starling Check" showFirst=false}
            > Step 1: Preload Increase
            Increased end-diastolic volume stretches sarcomeres.

            > Step 2: Stroke Volume
            $\Delta SV$ increases proportionally.
            :::
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "02-quiz.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Master Interactive Quiz
            ref: quiz-master
            ---

            :::mcq
            Question 1 prompt?
            - [x] Correct 1
            - [ ] Wrong 1
            :::explanation
            Q1 explanation text.
            :::

            :::mcq
            Question 2 prompt?
            - [x] Correct 2
            - [ ] Wrong 2
            :::explanation
            Q2 explanation text.
            :::

            :::flashcard
            :::front
            Front term
            :::back
            Back definition
            :::

            :::fill-blank{fuzzy=true}
            Formula $\frac{1}{\sqrt{2}}$ evaluates to {{0.707 | $\frac{\sqrt{2}}{2}$}}.
            :::

            :::assertion-reason{answer="both-no-explain"}
            A: Aspirin is avoided in children with viral illness.
            R: Aspirin acetylates COX-1 irreversibly.
            :::

            :::spot-error{requireFix=true allowNoError=true}
            In [primary hyperparathyroidism], calcium is [elevated] and phosphate is [~~elevated~~ -> decreased].
            :::

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

            :::diff{mode="rearrange" title="Diabetes"}
            | Feature | Type 1 | Type 2 |
            | :--- | :--- | :--- |
            | Onset | Childhood | Adulthood |
            :::

            :::mechanism{title="RAAS Pathway"}
            1. Renin release
            2. Angiotensin I formation
            3a. Efferent vasoconstriction
            3b. Aldosterone secretion
            ! ANP stimulates renin
            :::

            :::relabel{image="assets/heart.png" mode="bank" style="blur"}
            prompt: Match the labels ($\text{Na}^+$).
            distractors: ["Collecting duct"]
            boxes:
              - label: "Proximal tubule"
                rect: [0.10, 0.15, 0.30, 0.18]
              - label: "Glomerulus"
                rect: [0.55, 0.45, 0.30, 0.18]
            :::explanation
            Proximal tubule reabsorbs $\text{Na}^+$.
            :::

            :::relabel{image="assets/heart.png" mode="reveal" style="whiteout"}
            prompt: Tap masks to reveal.
            boxes:
              - label: "Loop of Henle"
                rect: [0.20, 0.30, 0.28, 0.18]
            :::

            :::classify{title="Circuit Classification"}
            [Pulmonary]
            - Right Ventricle
            - Pulmonary Trunk

            [Systemic]
            - Left Ventricle
            :::explanation
            Right ventricle pumps to pulmonary trunk.
            :::

            :::match{title="Valves"}
            - Mitral :: Left atrium to left ventricle
            - Tricuspid :: Right atrium to right ventricle
            ! Aortic valve
            :::explanation
            Mitral is bicuspid.
            :::

            :::numeric{answer=70 tolerance="10%" unit="mL"}
            Estimate resting stroke volume ($\text{SV}$):
            :::explanation
            Normal SV is ~70 mL.
            :::

            :::case{title="Valvular Murmur Case"}
            A 72-year-old presents with systolic murmur radiating to the carotids.

            ::::mcq{explanation="Classic presentation of aortic stenosis."}
            Which lesion is most likely?
            - [x] Aortic stenosis
            - [ ] Mitral regurgitation
            ::::

            ::::numeric{answer=1 tolerance=0.5 unit="cm2" explanation="Severe AS area is < 1.0 cm2."}
            Upper valve area threshold ($\text{cm}^2$) for severe AS:
            ::::
            :::
            """);

        var applied = await apply.HandleAsync(new ApplySpecCommand(null, modDir, WorkingDirectory: _fx.WorkDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);

        var webDir = Path.Combine(_fx.TempRoot, "web_master");
        var built = await build.HandleAsync(new BuildWebAppCommand(applied.Value.TargetId, webDir, StaticVerifyOnly: true));
        Assert.True(built.IsSuccess && built.Value.Verify?.Passed == true);

        // Verify componentIndex lives in app.js (not index.html) and Phase 1 widget improvements emit properly
        var appJs = await File.ReadAllTextAsync(Path.Combine(webDir, "app.js"));
        Assert.Contains("\"componentIndex\":2", appJs);
        var classifyJs = await File.ReadAllTextAsync(Path.Combine(webDir, "components", "questions", "classify.js"));
        var matchJs = await File.ReadAllTextAsync(Path.Combine(webDir, "components", "questions", "match.js"));
        var numericJs = await File.ReadAllTextAsync(Path.Combine(webDir, "components", "questions", "numeric.js"));
        Assert.Contains("deterministicShuffle", classifyJs);
        Assert.DoesNotContain("Math.random()", classifyJs);
        Assert.Contains("deterministicShuffle", matchJs);
        Assert.DoesNotContain("Math.random()", matchJs);
        Assert.Contains("toPrecision(6)", numericJs);
        Assert.Contains("off by ", numericJs);

        // Run 1: Live headless CDP check across all 6 blocks + all 12 question types
        var cdpRes = await verifier.VerifyAsync(webDir, staticOnly: false, timeoutSeconds: 45);
        if (!cdpRes.IsSuccess && cdpRes.Error?.Code == "Verify.BrowserNotFound")
            return;

        Assert.True(cdpRes.IsSuccess, cdpRes.Error?.Description);
        Assert.True(cdpRes.Value.Passed,
            $"CDP failed: console=[{string.Join("; ", cdpRes.Value.ConsoleErrors)}], overflow=[{string.Join("; ", cdpRes.Value.OverflowIssues)}], broken=[{string.Join("; ", cdpRes.Value.BrokenAssets)}], katex=[{string.Join("; ", cdpRes.Value.KatexErrors)}]");
        Assert.Equal(2, cdpRes.Value.SlidesChecked);
        Assert.Empty(cdpRes.Value.ConsoleErrors);
        Assert.Empty(cdpRes.Value.OverflowIssues);
        Assert.Empty(cdpRes.Value.BrokenAssets);
        Assert.Empty(cdpRes.Value.KatexErrors);

        // Run 2: Negative test injecting unbreakable overflow specifically into Q2
        var appJsPath = Path.Combine(webDir, "app.js");
        var mutatedJs = appJs.Replace("Question 2 prompt?", new string('W', 180), StringComparison.Ordinal);
        Assert.NotEqual(appJs, mutatedJs);
        await File.WriteAllTextAsync(appJsPath, mutatedJs);

        var overflowRes = await verifier.VerifyAsync(webDir, staticOnly: false, timeoutSeconds: 45);
        Assert.True(overflowRes.IsSuccess, overflowRes.Error?.Description);
        Assert.False(overflowRes.Value.Passed);
        Assert.Contains(overflowRes.Value.OverflowIssues, msg => msg.Contains("Q2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WebAppGenerator_TreeShakesComponentsThemesNullsAndStaleFiles()
    {
        using var sp = _fx.CreateProvider("appdata_treeshake");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var setLayout = scope.ServiceProvider.GetRequiredService<SetLayoutPropertyHandler>();

        // 1. Text-only article session (berry theme)
        Assert.True((await setLayout.HandleAsync(new SetLayoutPropertyCommand("article", "themeMode", "berry"))).IsSuccess);
        var textApplied = (await apply.HandleAsync(new ApplySpecCommand(null, null,
            "---\ntitle: Plain Notes\n---\n\nJust plain prose without math, images, or links.\n", _fx.WorkDir))).Value;
        var textWeb = Path.Combine(_fx.TempRoot, "web_text_only");
        var textBuilt = await build.HandleAsync(new BuildWebAppCommand(textApplied.TargetId, textWeb, StaticVerifyOnly: true));
        Assert.True(textBuilt.IsSuccess && textBuilt.Value.Verify?.Passed == true);

        Assert.True(File.Exists(Path.Combine(textWeb, "components", "blocks", "article.css")));
        Assert.False(Directory.Exists(Path.Combine(textWeb, "components", "questions")));
        Assert.False(Directory.Exists(Path.Combine(textWeb, "components", "aux")));
        Assert.False(Directory.Exists(Path.Combine(textWeb, "katex")));
        Assert.True(File.Exists(Path.Combine(textWeb, "preview-index.json")));
        Assert.True(File.Exists(Path.Combine(textWeb, "preview-index.js")));

        var textCss = await File.ReadAllTextAsync(Path.Combine(textWeb, "styles.css"));
        Assert.DoesNotContain("data-theme=\"dark\"", textCss);
        Assert.DoesNotContain("data-theme=\"light\"", textCss);
        Assert.DoesNotContain("data-theme=\"paper\"", textCss);

        var textHtml = await File.ReadAllTextAsync(textBuilt.Value.IndexHtmlPath);
        Assert.DoesNotContain("imageZoomModal", textHtml);
        Assert.DoesNotContain("linkPreviewPopover", textHtml);
        Assert.Contains("id=\"mby-ico-save\"", textHtml);

        var textJs = await File.ReadAllTextAsync(Path.Combine(textWeb, "app.js"));
        Assert.DoesNotContain("\"relabel\":null", textJs);
        Assert.DoesNotContain("\"flashcard\":null", textJs);
        Assert.DoesNotContain("\"layout\":", textJs);
        Assert.DoesNotContain("\"layoutOverrides\":", textJs);
        Assert.Contains("\"profiles\":", textJs);
        Assert.Contains("getSearchIndex", textJs);
        Assert.Contains("ensureSlideRendered", textJs);

        // 1b. Root DATA.profiles deduplicates global profile overrides, static spec fields, & CaseGroup target inheritance
        Assert.True((await setLayout.HandleAsync(new SetLayoutPropertyCommand("mcq", "optionColumns", "2"))).IsSuccess);
        var dedupRes = await apply.HandleAsync(new ApplySpecCommand(null, null, """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "name": "Dedup Check",
              "questions": [
                {
                  "type": "mcq",
                  "text": "Q1?",
                  "options": [
                    { "text": "A", "isCorrect": true },
                    { "text": "B", "isCorrect": false }
                  ]
                },
                {
                  "type": "mcq",
                  "text": "Q2?",
                  "layoutOverrides": { "cardMaxWidthPx": "720" },
                  "options": [
                    { "text": "C", "isCorrect": true },
                    { "text": "D", "isCorrect": false }
                  ]
                },
                {
                  "type": "assertion-reason",
                  "assertion": "Statement one.",
                  "reason": "Statement two.",
                  "answer": "both-explain"
                },
                {
                  "type": "diff",
                  "mode": "spot-wrong",
                  "title": "Plain Title",
                  "headers": ["Col A", "Col B"],
                  "rows": [["Same", "~~Bad~~ -> Good"]]
                },
                {
                  "type": "case",
                  "title": "Case Inheritance",
                  "stem": "Clinical scenario.",
                  "layoutOverrides": { "cardMaxWidthPx": "760" },
                  "questions": [
                    {
                      "type": "flashcard",
                      "front": { "text": "Sub front" },
                      "back": { "text": "Sub back" }
                    },
                    {
                      "type": "mcq",
                      "text": "Sub MCQ?",
                      "options": [
                        { "text": "Yes", "isCorrect": true },
                        { "text": "No", "isCorrect": false }
                      ]
                    }
                  ]
                }
              ]
            }
            """, _fx.WorkDir));
        Assert.True(dedupRes.IsSuccess, dedupRes.Error?.Description);
        var dedupApplied = dedupRes.Value;
        var dedupWeb = Path.Combine(_fx.TempRoot, "web_dedup");
        var dedupBuilt = await build.HandleAsync(new BuildWebAppCommand(dedupApplied.TargetId, dedupWeb, StaticVerifyOnly: true));
        Assert.True(dedupBuilt.IsSuccess && dedupBuilt.Value.Verify?.Passed == true);
        var dedupJs = await File.ReadAllTextAsync(Path.Combine(dedupWeb, "app.js"));
        Assert.Contains("\"optionColumns\":\"2\"", dedupJs);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(dedupJs, "\"optionColumns\":\"2\""));
        Assert.Contains("\"layoutOverrides\":{\"cardMaxWidthPx\":\"720\"}", dedupJs);
        Assert.DoesNotContain("\"options\":[{\"code\":", dedupJs);
        Assert.DoesNotContain("\"rowsRaw\":", dedupJs);
        Assert.DoesNotContain("\"alignments\":", dedupJs);
        Assert.DoesNotContain("\"titleHtml\":", dedupJs);
        Assert.True((await setLayout.HandleAsync(new SetLayoutPropertyCommand("mcq", "optionColumns", "1"))).IsSuccess);

        // 2. Standalone imageless MCQ quiz
        var mcqApplied = (await apply.HandleAsync(new ApplySpecCommand(null, null,
            "---\nkind: quiz\nquizType: mcq\nname: Solo MCQ\n---\n\n:::mcq\n2 + 2 = ?\n- [x] 4\n- [ ] 5\n:::\n", _fx.WorkDir))).Value;
        var mcqWeb = Path.Combine(_fx.TempRoot, "web_solo_mcq");
        var mcqBuilt = await build.HandleAsync(new BuildWebAppCommand(mcqApplied.TargetId, mcqWeb, StaticVerifyOnly: true));
        Assert.True(mcqBuilt.IsSuccess && mcqBuilt.Value.Verify?.Passed == true);

        Assert.True(File.Exists(Path.Combine(mcqWeb, "components", "aux", "graded-core.css")));
        Assert.True(File.Exists(Path.Combine(mcqWeb, "components", "aux", "graded-core.js")));
        Assert.True(File.Exists(Path.Combine(mcqWeb, "components", "questions", "choice-grid.css")));
        Assert.True(File.Exists(Path.Combine(mcqWeb, "components", "questions", "mcq.js")));
        Assert.False(Directory.Exists(Path.Combine(mcqWeb, "components", "blocks")));
        Assert.False(File.Exists(Path.Combine(mcqWeb, "components", "aux", "media-zoom.js")));
        Assert.False(File.Exists(Path.Combine(mcqWeb, "components", "aux", "quiz-bundle.js")));
        Assert.False(File.Exists(Path.Combine(mcqWeb, "components", "questions", "flashcard.js")));
        Assert.False(File.Exists(Path.Combine(mcqWeb, "preview-index.json")));
        Assert.False(File.Exists(Path.Combine(mcqWeb, "preview-index.js")));

        // 3. Transitive case dependencies (Set3 case hosting Set1 mcq + Set2 fill-blank)
        var caseApplied = (await apply.HandleAsync(new ApplySpecCommand(null, null, """
            ---
            kind: quiz
            quizType: mixed
            name: Case Transitive Check
            ---

            :::case{title="Clinical Vignette"}
            A patient presents with acute dyspnea.

            ::::mcq
            First-line bedside imaging?
            - [x] Chest radiograph
            - [ ] MRI brain
            ::::

            ::::fill-blank
            Normal arterial pH is approximately {{7.40}}.
            ::::
            :::
            """, _fx.WorkDir))).Value;
        var caseWeb = Path.Combine(_fx.TempRoot, "web_case_transitive");
        var caseBuilt = await build.HandleAsync(new BuildWebAppCommand(caseApplied.TargetId, caseWeb, StaticVerifyOnly: true));
        Assert.True(caseBuilt.IsSuccess && caseBuilt.Value.Verify?.Passed == true);

        Assert.True(File.Exists(Path.Combine(caseWeb, "components", "questions", "case.css")));
        Assert.True(File.Exists(Path.Combine(caseWeb, "components", "questions", "case.js")));
        Assert.True(File.Exists(Path.Combine(caseWeb, "components", "questions", "mcq.js")));
        Assert.True(File.Exists(Path.Combine(caseWeb, "components", "questions", "choice-grid.css")));
        Assert.True(File.Exists(Path.Combine(caseWeb, "components", "questions", "fill-blank.css")));
        Assert.True(File.Exists(Path.Combine(caseWeb, "components", "questions", "fill-blank.js")));
        Assert.True(File.Exists(Path.Combine(caseWeb, "components", "questions", "recall-utils.js")));
        Assert.True(File.Exists(Path.Combine(caseWeb, "components", "questions", "blank-input.css")));
        Assert.True(File.Exists(Path.Combine(caseWeb, "components", "aux", "graded-core.css")));
        Assert.True(File.Exists(Path.Combine(caseWeb, "components", "aux", "graded-core.js")));

        Assert.False(File.Exists(Path.Combine(caseWeb, "components", "questions", "relabel.js")));
        Assert.False(File.Exists(Path.Combine(caseWeb, "components", "questions", "diff.js")));
        Assert.False(File.Exists(Path.Combine(caseWeb, "components", "questions", "mechanism.js")));
        Assert.False(File.Exists(Path.Combine(caseWeb, "components", "questions", "flashcard.js")));
        Assert.False(File.Exists(Path.Combine(caseWeb, "components", "aux", "media-zoom.js")));

        // 4. Stale file cleanup on rebuild into the same output directory
        var heavyDir = _fx.SubDir("heavy_src");
        await TestAssets.WritePngAsync(Path.Combine(heavyDir, "organ.png"));
        var heavyMdPath = Path.Combine(heavyDir, "heavy.md");
        await File.WriteAllTextAsync(heavyMdPath, """
            ---
            kind: quiz
            quizType: mixed
            name: Heavy Math + Relabel
            ---

            :::relabel{image="organ.png"}
            prompt: Identify $\alpha$ structure:
            boxes:
              - label: "Cortex"
                rect: [0.1, 0.1, 0.3, 0.3]
            :::
            """);
        var heavyApplied = (await apply.HandleAsync(new ApplySpecCommand(null, heavyMdPath, WorkingDirectory: _fx.WorkDir))).Value;
        var reuseWeb = Path.Combine(_fx.TempRoot, "web_reuse");

        var firstBuild = await build.HandleAsync(new BuildWebAppCommand(heavyApplied.TargetId, reuseWeb, StaticVerifyOnly: true));
        Assert.True(firstBuild.IsSuccess && firstBuild.Value.Verify?.Passed == true);
        Assert.True(File.Exists(Path.Combine(reuseWeb, "katex", "katex.min.js")));
        Assert.True(File.Exists(Path.Combine(reuseWeb, "katex", "fonts", "KaTeX_Main-Regular.woff2")));
        Assert.False(File.Exists(Path.Combine(reuseWeb, "katex", "fonts", "KaTeX_Fraktur-Regular.woff2")));
        Assert.False(File.Exists(Path.Combine(reuseWeb, "katex", "fonts", "KaTeX_AMS-Regular.woff2")));
        Assert.True(File.Exists(Path.Combine(reuseWeb, "components", "questions", "relabel.js")));

        // Drop a stale file into reuseWeb/assets and reports/verify.latest.json, then rebuild with text-only session
        await TestAssets.WritePngAsync(Path.Combine(reuseWeb, "assets", "stale_orphan.png"));
        Directory.CreateDirectory(Path.Combine(reuseWeb, "reports"));
        var sentinelReport = Path.Combine(reuseWeb, "reports", "keep_me.json");
        await File.WriteAllTextAsync(sentinelReport, "{}");

        var secondBuild = await build.HandleAsync(new BuildWebAppCommand(textApplied.TargetId, reuseWeb, StaticVerifyOnly: true));
        Assert.True(secondBuild.IsSuccess && secondBuild.Value.Verify?.Passed == true);
        Assert.False(Directory.Exists(Path.Combine(reuseWeb, "katex")));
        Assert.False(Directory.Exists(Path.Combine(reuseWeb, "components", "questions")));
        Assert.False(File.Exists(Path.Combine(reuseWeb, "assets", "stale_orphan.png")));
        Assert.True(File.Exists(sentinelReport));

        // 5. Selective KaTeX font families (\mathfrak + \mathbb) copied only when used, and pruned when removed
        var fancyMathApplied = (await apply.HandleAsync(new ApplySpecCommand(null, null,
            "---\ntitle: Fancy Math\n---\n\nLie algebra $\\mathfrak{g} \\subset \\mathbb{R}^n$.\n", _fx.WorkDir))).Value;
        var fancyWeb = Path.Combine(_fx.TempRoot, "web_fancy_math");
        Assert.True((await build.HandleAsync(new BuildWebAppCommand(fancyMathApplied.TargetId, fancyWeb, StaticVerifyOnly: true))).IsSuccess);
        Assert.True(File.Exists(Path.Combine(fancyWeb, "katex", "fonts", "KaTeX_Fraktur-Regular.woff2")));
        Assert.True(File.Exists(Path.Combine(fancyWeb, "katex", "fonts", "KaTeX_AMS-Regular.woff2")));
        Assert.False(File.Exists(Path.Combine(fancyWeb, "katex", "fonts", "KaTeX_Script-Regular.woff2")));

        // Rebuild plain math into fancyWeb -> Fraktur & AMS fonts pruned
        Assert.True((await build.HandleAsync(new BuildWebAppCommand(heavyApplied.TargetId, fancyWeb, StaticVerifyOnly: true))).IsSuccess);
        Assert.True(File.Exists(Path.Combine(fancyWeb, "katex", "fonts", "KaTeX_Main-Regular.woff2")));
        Assert.False(File.Exists(Path.Combine(fancyWeb, "katex", "fonts", "KaTeX_Fraktur-Regular.woff2")));
        Assert.False(File.Exists(Path.Combine(fancyWeb, "katex", "fonts", "KaTeX_AMS-Regular.woff2")));
    }
}
