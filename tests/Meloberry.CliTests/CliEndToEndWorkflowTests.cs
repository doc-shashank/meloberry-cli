using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Meloberry.TestSupport;
using Xunit;

namespace Meloberry.CliTests;

[Collection("CwdTests")]
public sealed class CliEndToEndWorkflowTests : IDisposable
{
    private readonly WorkspaceTestFixture _fx = new("mby_cli_e2e");

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task ModularSession_AllComponents_E2E_ApplyCheckGetPatchBuildAnalyzePkgAndRm()
    {
        var cwdDir = _fx.SubDir("e2e_cwd");
        using var _ = _fx.PushCwd(cwdDir);

        // Reference Session B for cross-session mby://session/... link verification
        var dirB = Path.Combine(cwdDir, "session_b");
        Directory.CreateDirectory(dirB);
        await File.WriteAllTextAsync(Path.Combine(dirB, "_session.md"), "---\nkind: session\nname: Cardiology Reference\ndescription: ECG basics\n---\n");
        await File.WriteAllTextAsync(Path.Combine(dirB, "01-ecg.md"), "---\ntitle: ECG Basics\nref: ecg-basics\n---\n\nThe P wave reflects atrial depolarization.\n");

        var applyB = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "./session_b", "--json"], applyB, _fx.AppDataDir));
        var sessionBId = JsonDocument.Parse(applyB.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["build", sessionBId, "-o", $"./web/{sessionBId}_web", "--verify", "--static-only", "--json"], new TestConsole(), _fx.AppDataDir));
        Assert.True(File.Exists(Path.Combine(cwdDir, "web", $"{sessionBId}_web", "preview-index.js")));
        Assert.True(File.Exists(Path.Combine(cwdDir, "web", $"{sessionBId}_web", "preview-index.json")));

        // Golden modular Session A exercising all 6 article blocks + all 12 question types + native links
        var modDir = Path.Combine(cwdDir, "chapter_master");
        Directory.CreateDirectory(modDir);
        var assetsDir = Path.Combine(modDir, "assets");
        await TestAssets.WritePngAsync(Path.Combine(assetsDir, "diagram.png"));
        await TestAssets.WritePngAsync(Path.Combine(assetsDir, "opt1.png"));
        await TestAssets.WritePngAsync(Path.Combine(assetsDir, "spot.png"));
        await TestAssets.WritePngAsync(Path.Combine(assetsDir, "card.png"));
        await TestAssets.WriteMasksSidecarAsync(Path.Combine(assetsDir, "diagram.masks.json"), """
            {
              "boxes": [
                { "label": "Glomerulus", "rect": [0.15, 0.20, 0.25, 0.14] },
                { "label": "Proximal tubule", "rect": [0.55, 0.25, 0.25, 0.14] }
              ]
            }
            """);
        // Root-level *.masks.json sidecar must be ignored by directory loader
        await File.WriteAllTextAsync(Path.Combine(modDir, "root.masks.json"), """{"boxes":[{"label":"Ignored","rect":[0.1,0.1,0.2,0.2]}]}""");

        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), """
            ---
            kind: session
            name: Master Study Session
            description: Full CLI pipeline verification
            ---
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "01-article.md"), $$"""
            ---
            title: Nephron Overview
            ref: art-main
            ---

            # Introduction

            The [Loop of Henle](mby://card/loop-card) links to [Diuretic Table](mby://article/art-two), [ECG](mby://session/{{sessionBId}}/ecg-basics), and [Future Neuro](mby://session/Session_Future_999/intro).

            ![Overview](assets/diagram.png "Fig. 1 Overview")

            :::warning Exam Trap
            Irreversible mitochondrial damage marks the point of no return.
            :::

            :::link{id="loop-card" title="Loop of Henle" image="assets/card.png"}
            Thick ascending limb reabsorbs $\text{Na}^+/\text{K}^+/2\text{Cl}^-$.
            :::

            :::table{ref="tbl-main" title="Loop vs Thiazide" stickyFirstCol=true hideableCols="2,3"}
            | Feature | Loop | Thiazide |
            | :--- | :--- | :--- |
            | Calcium | Decreased | Increased |
            :::footnote
            Key renal comparison.
            :::

            :::diagram-explore{ref="diag-1" image="assets/diagram.png" masks="assets/diagram.masks.json" title="Nephron"}
            - Glomerulus: Filters blood plasma.
            - Proximal tubule: Reabsorbs sodium and water.
            :::

            :::reveal{ref="rev-1" title="Clinical Pearl" showFirst=false}
            > Question: Why is glucose absent in urine?
            SGLT2 reabsorbs filtered glucose in the PCT.
            :::
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "02-ref.md"), """
            ---
            title: Diuretic Summary
            ref: art-two
            ---

            Loop diuretics inhibit NKCC2 in the thick ascending limb.
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "03-quiz.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: All Question Types
            ref: quiz-main
            ---

            :::mcq{ref="q-mcq"}
            Which option shows the diagram?
            - [x] ![Opt1](assets/opt1.png) Loop diuretic
            - [ ] Thiazide diuretic
            :::

            :::flashcard{ref="q-fc"}
            Front prompt
            :::back
            Back answer
            :::

            :::fill-blank{ref="q-fb" fuzzy=true}
            Committed enzyme of glycolysis: {{phosphofructokinase-1 | PFK-1}}.
            :::

            :::assertion-reason{ref="q-ar" answer="both-no-explain" scheme="4opt"}
            A: Aspirin is avoided in viral illness in children.
            R: Aspirin acetylates COX enzymes irreversibly.
            :::

            :::spot-error{ref="q-spot" image="assets/spot.png" requireFix=false allowNoError=true}
            In [primary hyperparathyroidism], calcium is [high] and phosphate is [~~high~~ -> low].
            :::

            :::diff{ref="q-diff" mode="spot-wrong" title="Gram Comparison"}
            | Property | Gram+ | Gram- |
            | :--- | :--- | :--- |
            | Outer membrane | ~~Present~~ -> Absent \| None | Present |
            :::

            :::mechanism{ref="q-mech" title="RAAS Cascade"}
            1. Renin release
            2. Angiotensin I formation
            ! ANP activates renin
            :::

            :::relabel{ref="q-rel" image="assets/diagram.png" masks="assets/diagram.masks.json" mode="bank" style="blur"}
            prompt: Label the renal corpuscle.
            :::

            :::classify{ref="q-cls" title="Nephron Segments"}
            [Cortex]
            - Glomerulus
            - Proximal tubule

            [Medulla]
            - Collecting duct
            :::

            :::match{ref="q-mth" title="Hormones"}
            - ADH :: Increases AQP2 channels
            - Aldosterone :: Increases ENaC synthesis
            ! Renin :: Directly constricts bronchi
            :::

            :::numeric{ref="q-num" answer=125 tolerance="5%" unit="mL/min"}
            Normal adult GFR:
            :::

            :::case{ref="q-case" title="Acute Kidney Injury"}
            Patient has BUN:Cr ratio > 20:1 and FeNa < 1%.

            ::::mcq{explanation="Prerenal azotemia preserves tubular Na reabsorption."}
            Most likely category?
            - [x] Prerenal AKI
            - [ ] Intrinsic ATN
            ::::

            ::::numeric{answer=1 tolerance=0.2 unit="%"}
            FeNa threshold (%) below which prerenal AKI is favored:
            ::::
            :::
            """);

        // 1. apply (defaults storage under .meloberry/workspaces)
        var applyC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "./chapter_master", "--json"], applyC, _fx.AppDataDir));
        string wsId;
        string storageDir;
        using (var doc = JsonDocument.Parse(applyC.StandardOutput))
        {
            var data = doc.RootElement.GetProperty("data");
            wsId = data.GetProperty("targetId").GetString()!;
            storageDir = data.GetProperty("storageDirectory").GetString()!;
            Assert.Contains(Path.Combine(".meloberry", "workspaces"), storageDir, StringComparison.OrdinalIgnoreCase);
        }

        // 2. ls discovers .meloberry/workspaces automatically
        var lsC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["ls", "--json"], lsC, _fx.AppDataDir));
        Assert.Contains(wsId, lsC.StandardOutput);

        // 3. check passes (clean with warning for future session link)
        var checkC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["check", wsId, "--json"], checkC, _fx.AppDataDir));
        using (var doc = JsonDocument.Parse(checkC.StandardOutput))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("isClean").GetBoolean());
            Assert.Equal(0, data.GetProperty("errorCount").GetInt32());
            Assert.Contains("Lint.UnresolvedSessionLink", data.GetProperty("issues").GetRawText());
        }

        // 4. get outline / ids / full (JSON + human)
        var getOutline = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--json"], getOutline, _fx.AppDataDir));
        Assert.DoesNotContain('\n', getOutline.StandardOutput.Trim());
        Assert.Contains("\"path\":\"c1.b1\"", getOutline.StandardOutput);

        var getIds = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--view", "ids", "--json"], getIds, _fx.AppDataDir));
        Assert.DoesNotContain("snippet", getIds.StandardOutput);
        Assert.Contains("\"path\":\"c3.q1\"", getIds.StandardOutput);

        var getFullHuman = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--view", "full"], getFullHuman, _fx.AppDataDir));
        Assert.Contains("Loop vs Thiazide", getFullHuman.StandardOutput);
        Assert.Contains("Gram Comparison", getFullHuman.StandardOutput);
        Assert.Contains("RAAS Cascade", getFullHuman.StandardOutput);
        Assert.Contains("Label the renal corpuscle.", getFullHuman.StandardOutput);

        var getFullJson = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--view", "full", "--json"], getFullJson, _fx.AppDataDir));
        foreach (var marker in new[] { "diagram-explore", "reveal", "fill-blank", "assertion-reason", "spot-error", "relabel", "classify", "match", "numeric", "case" })
            Assert.Contains(marker, getFullJson.StandardOutput);

        // 5. patch across components
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-fb", "--set", "placeholder=Patched", "--set", "fuzzy=false", "--json"], new TestConsole(), _fx.AppDataDir));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-spot", "--set", "imagePath=null", "--json"], new TestConsole(), _fx.AppDataDir));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-diff", "--set", "title=Updated Gram", "--json"], new TestConsole(), _fx.AppDataDir));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-rel", "--set", "style=whiteout", "--json"], new TestConsole(), _fx.AppDataDir));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-num", "--set", "answer=120", "--json"], new TestConsole(), _fx.AppDataDir));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#rev-1", "--set", "showFirst=true", "--json"], new TestConsole(), _fx.AppDataDir));

        // 6. build --verify --static-only + analyze --static-only
        var webOut = Path.Combine(cwdDir, "web", $"{wsId}_web");
        var buildC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["build", wsId, "-o", webOut, "--verify", "--static-only", "--json"], buildC, _fx.AppDataDir));
        Assert.True(JsonDocument.Parse(buildC.StandardOutput).RootElement.GetProperty("data").GetProperty("verify").GetProperty("passed").GetBoolean());
        Assert.True(File.Exists(Path.Combine(webOut, "preview-index.js")));
        Assert.True(File.Exists(Path.Combine(webOut, "preview-index.json")));

        var js = await File.ReadAllTextAsync(Path.Combine(webOut, "app.js"));
        Assert.Contains($"../{sessionBId}_web/index.html#article=ecg-basics", js);
        Assert.Contains("../Session_Future_999_web/index.html#article=intro", js);
        Assert.Contains("https://github.com/doc-shashank/meloberry-cli/", js);

        var analyzeOk = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["analyze", webOut, "--static-only", "--json"], analyzeOk, _fx.AppDataDir));
        Assert.True(JsonDocument.Parse(analyzeOk.StandardOutput).RootElement.GetProperty("data").GetProperty("passed").GetBoolean());

        // 7. pkg export & import round-trip (import outside cwdDir so subsequent rm+get tests deleted state cleanly)
        var expC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "export", wsId, "android", "./master.mbypkg", "--json"], expC, _fx.AppDataDir));
        var reimpRoot = _fx.SubDir("reimported_outside_cwd");
        var impC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "import", "./master.mbypkg", reimpRoot, "--json"], impC, _fx.AppDataDir));
        Assert.True(File.Exists(Path.Combine(reimpRoot, wsId, $"{wsId}.db")));

        // 8. rm deletes block then session B cleanly (session B has only one copy on disk)
        var rmBlock = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["rm", $"{wsId}#c1.b2", "--json"], rmBlock, _fx.AppDataDir));
        Assert.Contains("\"deletedId\":\"ArtComp_", rmBlock.StandardOutput);

        var rmSession = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["rm", sessionBId, "--json"], rmSession, _fx.AppDataDir));
        Assert.Contains("\"entityKind\":\"session\"", rmSession.StandardOutput);
        Assert.Equal(ExitCodes.NotFound, await CliApplicationRunner.ExecuteAsync(["get", sessionBId, "--json"], new TestConsole(), _fx.AppDataDir));
    }

    [Fact]
    public async Task CliLintFailure_And_BrokenWebAssetAnalysis_EmitStructuredExit2Envelopes()
    {
        using var _ = _fx.PushCwd(_fx.TempRoot);
        var assetsDir = _fx.SubDir("bad_assets");
        await TestAssets.WritePngAsync(Path.Combine(assetsDir, "img.png"));

        var invalidSpec = """
            {
              "kind": "session",
              "name": "Broken All-Phases Session",
              "components": [
                {
                  "kind": "article",
                  "title": "Bad Article",
                  "blocks": [
                    { "kind": "text", "text": "Broken [card](mby://card/missing) and [art](mby://article/missing-art)." },
                    { "kind": "diagram-explore", "imagePath": "bad_assets/img.png", "hotspots": [] },
                    { "kind": "reveal", "steps": [{ "heading": "", "body": "" }] }
                  ]
                },
                {
                  "kind": "quiz",
                  "quizType": "mixed",
                  "title": "Bad Quiz",
                  "questions": [
                    { "type": "fill-blank", "text": "Missing any blank markers." },
                    { "type": "spot-error", "noError": true, "text": "Conflicting [valid] and [~~wrong~~ -> right]." },
                    { "type": "diff", "mode": "fill", "headers": ["A", "B"], "rows": [["NoBlank1", "NoBlank2"]] },
                    { "type": "mechanism", "steps": [{ "orderGroup": 1, "text": "Only one" }, { "isDistractor": true, "text": "Decoy" }] },
                    { "type": "relabel", "imagePath": "bad_assets/img.png", "mode": "bank", "style": "solid", "boxes": [{ "label": "Out", "x": 0.85, "y": 0.2, "w": 0.3, "h": 0.1 }] },
                    { "type": "classify", "buckets": ["B1", "B2"], "items": [{ "bucket": "B1", "text": "OnlyOne" }] },
                    { "type": "match", "pairs": [{ "left": "A", "right": "A" }], "distractors": ["D1"] },
                    { "type": "numeric", "text": "Val", "answer": 5, "tolerance": -2 },
                    { "type": "case", "stem": "", "questions": [{ "type": "mcq", "text": "Q1", "options": [{ "text": "X", "isCorrect": true }, { "text": "Y", "isCorrect": false }] }] }
                  ]
                }
              ]
            }
            """;

        var applyC = new TestConsole(invalidSpec);
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _fx.WorkDir, "--json"], applyC, _fx.AppDataDir));
        var applyDoc = JsonDocument.Parse(applyC.StandardOutput);
        Assert.False(applyDoc.RootElement.GetProperty("data").GetProperty("lint").GetProperty("isClean").GetBoolean());
        var wsId = applyDoc.RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

        var checkC = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["check", wsId, "--json"], checkC, _fx.AppDataDir));
        Assert.True(string.IsNullOrWhiteSpace(checkC.StandardOutput));
        using (var errDoc = JsonDocument.Parse(checkC.StandardError))
        {
            Assert.False(errDoc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Lint.Failed", errDoc.RootElement.GetProperty("error").GetProperty("code").GetString());
            var issuesJson = errDoc.RootElement.GetProperty("data").GetProperty("issues").GetRawText();
            foreach (var code in new[]
            {
                "Lint.MissingLinkCard", "Lint.UnresolvedArticleLink",
                "Lint.EmptyDiagramHotspots", "Lint.EmptyRevealStep",
                "Lint.NoFillBlanks", "Lint.SpotErrorConflictingNoError",
                "Lint.DiffMissingBlanks", "Lint.InsufficientMechanismSteps", "Lint.InvalidRelabelBoxBounds",
                "Lint.EmptyClassifyBucket", "Lint.InsufficientMatchPairs", "Lint.InvalidNumericTolerance",
                "Lint.EmptyCaseStem", "Lint.InsufficientCaseQuestions"
            })
            {
                Assert.Contains(code, issuesJson);
            }
        }

        // Valid article-only build + corrupted asset triggers Verify.Failed exit 2
        var artDir = _fx.SubDir("art_only");
        await File.WriteAllTextAsync(Path.Combine(artDir, "_session.md"), "---\nkind: session\nname: Art Only\n---\n");
        await File.WriteAllTextAsync(Path.Combine(artDir, "01-read.md"), "---\ntitle: Pure Reading\n---\n\n![Pic](bad_assets/img.png)\n");
        await TestAssets.WritePngAsync(Path.Combine(artDir, "bad_assets", "img.png"));

        var webDir = Path.Combine(_fx.TempRoot, "art_web");
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["build", "./art_only", "-o", webDir, "--verify", "--static-only", "--json"], new TestConsole(), _fx.AppDataDir));

        foreach (var assetFile in Directory.GetFiles(Path.Combine(webDir, "assets")))
            File.Delete(assetFile);

        var anFail = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["analyze", webDir, "--static-only", "--json"], anFail, _fx.AppDataDir));
        Assert.True(string.IsNullOrWhiteSpace(anFail.StandardOutput));
        using var verifyErr = JsonDocument.Parse(anFail.StandardError);
        Assert.False(verifyErr.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Verify.Failed", verifyErr.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(verifyErr.RootElement.GetProperty("data").GetProperty("brokenAssets").GetArrayLength() > 0);
    }
}
