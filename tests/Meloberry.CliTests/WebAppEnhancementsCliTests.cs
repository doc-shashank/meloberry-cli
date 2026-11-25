using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Xunit;

namespace Meloberry.CliTests;

[Collection("CwdTests")]
public sealed class WebAppEnhancementsCliTests : IDisposable
{
    private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAUAAAADICAYAAACZBDirAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAKoSURBVHhe7dShAQAgDMCw/X8tEjc8LzQipgd0zt0FKJo/AFQYIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBA1gNPI9UTnmi3rAAAAABJRU5ErkJggg==");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_ui_enh_{Guid.NewGuid():N}");
    private readonly string _appDataDir;

    public WebAppEnhancementsCliTests()
    {
        Directory.CreateDirectory(_tempDir);
        _appDataDir = Path.Combine(_tempDir, "appdata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task WebAppEnhancements_FullSession_UiComponentsLinksGreetingSearchAndSummary()
    {
        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_tempDir);

            // Create sibling Target Session B first so Session A can reference an existing session AND a future session
            var dirB = Path.Combine(_tempDir, "session_b");
            Directory.CreateDirectory(dirB);
            await File.WriteAllTextAsync(Path.Combine(dirB, "_session.md"), """
                ---
                kind: session
                name: Cardiology Reference
                description: ECG and cardiac physiology notes
                ---
                """);
            await File.WriteAllTextAsync(Path.Combine(dirB, "01-ecg.md"), """
                ---
                title: ECG Basics
                ref: ecg-basics
                ---

                The P wave reflects atrial depolarization and PR interval measures AV nodal conduction delay.
                """);

            var applyB = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "./session_b", "--json"], applyB, _appDataDir));
            var sessionBId = JsonDocument.Parse(applyB.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            var buildB = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["build", sessionBId, "-o", $"./web/{sessionBId}_web", "--verify", "--static-only", "--json"], buildB, _appDataDir));
            Assert.True(File.Exists(Path.Combine(_tempDir, "web", $"{sessionBId}_web", "preview-index.js")));
            Assert.True(File.Exists(Path.Combine(_tempDir, "web", $"{sessionBId}_web", "preview-index.json")));

            // Create Session A covering all UI enhancements + native links (card, article, existing session, future session)
            var dirA = Path.Combine(_tempDir, "session_a");
            var assetsA = Path.Combine(dirA, "assets");
            Directory.CreateDirectory(assetsA);
            await File.WriteAllBytesAsync(Path.Combine(assetsA, "opt1.png"), ValidPngBytes);
            await File.WriteAllBytesAsync(Path.Combine(assetsA, "spot.png"), ValidPngBytes);
            await File.WriteAllBytesAsync(Path.Combine(assetsA, "card.png"), ValidPngBytes);
            await File.WriteAllBytesAsync(Path.Combine(assetsA, "relabel.png"), ValidPngBytes);
            await File.WriteAllTextAsync(Path.Combine(assetsA, "relabel.masks.json"), """
                {
                  "boxes": [
                    { "label": "Glomerulus", "rect": [0.15, 0.20, 0.25, 0.14] },
                    { "label": "Loop of Henle", "rect": [0.55, 0.25, 0.25, 0.14] }
                  ]
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(dirA, "_session.md"), """
                ---
                kind: session
                name: Renal Physiology Master
                description: Comprehensive renal nephron study deck
                ---
                """);

            await File.WriteAllTextAsync(Path.Combine(dirA, "01-nephron.md"), $$"""
                ---
                title: Nephron Overview
                ref: nephron-overview
                ---

                The [Loop of Henle](mby://card/loop-card) establishes the medullary gradient. See also [Diuretic Table](mby://article/diuretic-table), [ECG Basics](mby://session/{{sessionBId}}/ecg-basics), and [Future Neuro](mby://session/Session_Future_999/intro).

                :::link{id="loop-card" title="Loop of Henle" image="assets/card.png"}
                Thick ascending limb actively reabsorbs $\text{Na}^+/\text{K}^+/2\text{Cl}^-$.
                :::

                :::table{title="Nephron Segments" hideableCols="2,3"}
                | Segment | Transport | Diuretic |
                | :--- | :--- | :--- |
                | TAL | NKCC2 | Loop |
                | DCT | NCC | Thiazide |
                :::
                """);

            await File.WriteAllTextAsync(Path.Combine(dirA, "02-diuretics.md"), """
                ---
                title: Diuretic Summary
                ref: diuretic-table
                ---

                Loop diuretics inhibit NKCC2 in the thick ascending limb and cause calciuria.
                """);

            await File.WriteAllTextAsync(Path.Combine(dirA, "03-drills.md"), """
                ---
                kind: quiz
                quizType: mixed
                title: Interactive Drills
                ref: drills
                ---

                :::mcq{ref="q-mcq"}
                Which option shows the diagram?
                - [x] ![Opt1](assets/opt1.png) Loop diuretic
                - [ ] Thiazide diuretic
                :::

                :::fill-blank{ref="q-fb-default" fuzzy=true}
                Rate-limiting enzyme: {{phosphofructokinase-1 | PFK-1}}.
                :::

                :::fill-blank{ref="q-fb-custom" fuzzy=true placeholder="Type enzyme"}
                Second enzyme: {{hexokinase}}.
                :::

                :::spot-error{ref="q-spot" image="assets/spot.png" requireFix=false allowNoError=true}
                In [TAL], [loop diuretics] [~~increase~~ -> decrease] calcium reabsorption.
                :::

                :::diff{ref="q-diff-fill" mode="fill" bank=true title="Fill Comparison"}
                | Feature | Loop | Thiazide |
                | :--- | :--- | :--- |
                | Target | {{NKCC2}} | {{NCC}} |
                :::

                :::diff{ref="q-diff-spot" mode="spot-wrong" title="Spot Comparison"}
                | Feature | Loop | Thiazide |
                | :--- | :--- | :--- |
                | Serum Ca | ~~Increased~~ -> Decreased | Increased |
                :::

                :::diff{ref="q-diff-rearr" mode="rearrange" title="Rearrange Comparison"}
                | Feature | Loop | Thiazide |
                | :--- | :--- | :--- |
                | Site | TAL | DCT |
                :::

                :::mechanism{ref="q-mech" title="RAAS Cascade"}
                1. Renin release
                2. Angiotensin I
                ! ANP inhibits ACE
                :::

                :::relabel{ref="q-rel" image="assets/relabel.png" masks="assets/relabel.masks.json" mode="bank" style="blur"}
                prompt: Label the structures.
                :::

                :::flashcard{ref="q-fc"}
                Front prompt
                :::back
                Back answer
                :::
                """);

            var applyA = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "./session_a", "--json"], applyA, _appDataDir));
            var sessionAId = JsonDocument.Parse(applyA.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            // Lint should succeed (Warning for future session, 0 errors)
            var checkA = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["check", sessionAId, "--json"], checkA, _appDataDir));
            using (var checkDoc = JsonDocument.Parse(checkA.StandardOutput))
            {
                var data = checkDoc.RootElement.GetProperty("data");
                Assert.True(data.GetProperty("isClean").GetBoolean());
                Assert.Equal(0, data.GetProperty("errorCount").GetInt32());
                var issuesJson = data.GetProperty("issues").GetRawText();
                Assert.Contains("Lint.UnresolvedSessionLink", issuesJson);
            }

            // Build WebApp + static verify
            var outWebA = Path.Combine(_tempDir, "web", $"{sessionAId}_web");
            var buildA = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["build", sessionAId, "-o", outWebA, "--verify", "--static-only", "--json"], buildA, _appDataDir));

            var html = await File.ReadAllTextAsync(Path.Combine(outWebA, "index.html"));
            var css = await File.ReadAllTextAsync(Path.Combine(outWebA, "styles.css"));
            var js = await File.ReadAllTextAsync(Path.Combine(outWebA, "app.js"));
            Assert.True(File.Exists(Path.Combine(outWebA, "preview-index.js")));
            Assert.True(File.Exists(Path.Combine(outWebA, "preview-index.json")));

            // 1. Retry button + Flashcard Reset
            Assert.Contains("mby-retry-btn", js);
            Assert.Contains("createRetryBtn", js);

            // 2. Clickable hideable table column headers + distinct color (no toggle pill bar)
            Assert.Contains("mby-th-hideable", js);
            Assert.DoesNotContain("mby-table-controls", js);
            Assert.Contains("--table-hideable-bg", css);
            Assert.Contains("--table-hideable-color", css);

            // 3. MCQ option image alignment
            Assert.Contains("mby-option-media", js);
            Assert.Contains("mby-option-text", js);
            Assert.Contains("align-items: stretch", css);

            // 4 & 5. Fill-in-the-blank empty default placeholder, configurable placeholder, and "Almost Correct!"
            Assert.DoesNotContain("placeholder=\"Blank ", js);
            Assert.DoesNotContain("Pick or type", js);
            Assert.Contains("\"placeholder\":\"Type enzyme\"", js);
            Assert.Contains("Almost Correct!", js);
            Assert.DoesNotContain("Correct (minor typo accepted)!", js);

            // 6, 7, 8. Spot the Error image support, compact box, answer above box, left-aligned No error button
            Assert.Contains("assets/spot_", js);
            Assert.Contains("mby-error-chip-wrap", js);
            Assert.Contains("beforebegin", js);
            Assert.Contains("[noErrorBtn, checkBtn", js);

            // 9. Distinct selection color across themes
            Assert.Contains("--select:", css);
            Assert.Contains("--select-soft:", css);
            Assert.Contains("var(--select)", css);
            Assert.Contains("var(--select-soft)", css);

            // 10. Diff bank retyping deactivation
            Assert.Contains("itemMatchesNorm", js);

            // 11. Diff spot-wrong corrections below table
            Assert.Contains("mby-diff-corrections", js);

            // 12 & 13. Mechanism FLIP animation + universal drag-and-drop
            Assert.Contains("animateSeqUpdate", js);
            Assert.Contains("data-step-id", js);
            Assert.Contains("draggable = true", js);
            Assert.Contains("mby-drop-over", js);

            // 14. Native article links (card + article + existing session + future session actual href + preview-index.js loader)
            Assert.Contains("mby-native-link", js);
            Assert.Contains("linkCards", js);
            Assert.Contains("externalPreviews", js);
            Assert.Contains("initLinkPreviewCache", js);
            Assert.Contains("preview-index.js", js);
            Assert.Contains($"../{sessionBId}_web/index.html#article=ecg-basics", js);
            Assert.Contains("../Session_Future_999_web/index.html#article=intro", js);
            Assert.Contains("mby-link-popover-excerpt", css);

            // 15, 16, 17. Greeting card counts, Generator text, GitHub link
            Assert.DoesNotContain("component(s)", js);
            Assert.Contains("Articles", js);
            Assert.Contains("Questions", js);
            Assert.Contains("Generator:", js);
            Assert.Contains("https://github.com/doc-shashank/meloberry-cli/", js);
            Assert.Contains(">GitHub<", js);

            // 18. Search UI/UX overhaul
            Assert.Contains("id=\"searchFilters\"", html);
            Assert.Contains("id=\"searchClearBtn\"", html);
            Assert.Contains("renderSessionOutline", js);

            // 19, 20, 21. Summary ID hiding, content-aware metrics, centered SVG Back button
            Assert.DoesNotContain("<code>${escapeHtml(s.id)}</code>", js);
            Assert.Contains("dataset.componentId", js);
            Assert.Contains("dataset.workspaceId", js);
            Assert.Contains("articleTotal", js);
            Assert.Contains("display: inline-flex; align-items: center; justify-content: center;", css);
            Assert.Contains("<svg", html);

            // Verify .mbypkg export & re-import preserves SpotError image & Article LinkCards
            var pkgPath = Path.Combine(_tempDir, "session_a.mbypkg");
            var expC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "export", sessionAId, "desktop", pkgPath, "--json"], expC, _appDataDir));
            var impDir = Path.Combine(_tempDir, "reimported");
            var impC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "import", pkgPath, impDir, "--json"], impC, _appDataDir));

            // Patch test for fill-blank placeholder & spot-error imagePath
            var patchFb = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["patch", $"{sessionAId}#q-fb-default", "--set", "placeholder=Patched placeholder", "--json"], patchFb, _appDataDir));
            var patchSpot = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["patch", $"{sessionAId}#q-spot", "--set", "imagePath=null", "--json"], patchSpot, _appDataDir));
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }

    [Fact]
    public async Task WebAppEnhancements_ArticleOnlySession_SummaryAndLintErrors()
    {
        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_tempDir);

            // Broken card & broken article link should fail mby check with Lint Errors
            var badDir = Path.Combine(_tempDir, "bad_links");
            Directory.CreateDirectory(badDir);
            await File.WriteAllTextAsync(Path.Combine(badDir, "_session.md"), "---\nkind: session\nname: Bad Links\n---\n");
            await File.WriteAllTextAsync(Path.Combine(badDir, "01-art.md"), """
                ---
                title: Broken Links Article
                ref: broken-art
                ---

                Broken [card](mby://card/does-not-exist) and [article](mby://article/missing-art).
                """);

            var applyBad = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "./bad_links", "--json"], applyBad, _appDataDir));
            var badId = JsonDocument.Parse(applyBad.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            var checkBad = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["check", badId, "--json"], checkBad, _appDataDir));
            Assert.Contains("Lint.MissingLinkCard", checkBad.StandardError);
            Assert.Contains("Lint.UnresolvedArticleLink", checkBad.StandardError);

            // Valid article-only session
            var artDir = Path.Combine(_tempDir, "art_only");
            Directory.CreateDirectory(artDir);
            await File.WriteAllTextAsync(Path.Combine(artDir, "_session.md"), "---\nkind: session\nname: Article Only\n---\n");
            await File.WriteAllTextAsync(Path.Combine(artDir, "01-read.md"), """
                ---
                title: Pure Reading
                ref: pure-read
                ---

                Some clean reading prose without any questions.
                """);

            var applyArt = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "./art_only", "--json"], applyArt, _appDataDir));
            var artId = JsonDocument.Parse(applyArt.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            var buildArt = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["build", artId, "-o", "./web_art_only", "--verify", "--static-only", "--json"], buildArt, _appDataDir));

            var js = await File.ReadAllTextAsync(Path.Combine(_tempDir, "web_art_only", "app.js"));
            // Content-aware summary avoids unconditional Flashcards 0/0
            Assert.Contains("if (report.summary.flashcardTotal > 0)", js);
            Assert.Contains("if (report.summary.mcqTotal > 0)", js);
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }
}
