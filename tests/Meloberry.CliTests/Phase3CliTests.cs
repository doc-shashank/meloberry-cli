using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Xunit;

namespace Meloberry.CliTests;

[Collection("CwdTests")]
public sealed class Phase3CliTests : IDisposable
{
    private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAUAAAADICAYAAACZBDirAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAKoSURBVHhe7dShAQAgDMCw/X8tEjc8LzQipgd0zt0FKJo/AFQYIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBA1gNPI9UTnmi3rAAAAABJRU5ErkJggg==");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_phase3_cli_{Guid.NewGuid():N}");
    private readonly string _appDataDir;

    public Phase3CliTests()
    {
        Directory.CreateDirectory(_tempDir);
        _appDataDir = Path.Combine(_tempDir, "appdata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // 1. Prime budget & spec discovery for Phase 3 blocks and questions
    [Fact]
    public async Task Phase3_PrimeAndSpecCli_ExposeAllSixComponents_WithinPrimeBudget()
    {
        var primeC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["prime"], primeC, _appDataDir));
        Assert.True(primeC.StandardOutput.Length < 1550, $"Expected prime < 1550 chars, got {primeC.StandardOutput.Length}");
        Assert.Contains("diagram-explore|reveal|case|classify|match|numeric", primeC.StandardOutput);

        var helpC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "--help"], helpC, _appDataDir));
        foreach (var topic in new[] { "diagram-explore", "reveal", "case", "classify", "match", "numeric" })
            Assert.Contains(topic, helpC.StandardOutput);

        var blocksC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "@blocks", "--json"], blocksC, _appDataDir));
        using (var doc = JsonDocument.Parse(blocksC.StandardOutput))
        {
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            Assert.Equal(6, schemas.GetArrayLength());
            var topics = schemas.EnumerateArray().Select(s => s.GetProperty("topic").GetString()).ToList();
            Assert.Contains("diagram-explore", topics);
            Assert.Contains("reveal", topics);
        }

        var specAllC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["spec", "diagram-explore", "reveal", "case", "classify", "match", "numeric", "--json"], specAllC, _appDataDir));
        using (var doc = JsonDocument.Parse(specAllC.StandardOutput))
            Assert.Equal(6, doc.RootElement.GetProperty("data").GetProperty("schemas").GetArrayLength());
    }

    // 2. End-to-end modular apply -> check -> get -> patch -> build --static-only -> pkg export -> pkg import
    [Fact]
    public async Task Phase3_ModularMarkdownSession_E2E_ApplyCheckGetPatchBuildAndPackage()
    {
        var modDir = Path.Combine(_tempDir, "chapter3");
        var assetsDir = Path.Combine(modDir, "assets");
        Directory.CreateDirectory(assetsDir);

        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "nephron.png"), ValidPngBytes);
        await File.WriteAllTextAsync(Path.Combine(assetsDir, "nephron.masks.json"), """
            {
              "boxes": [
                { "label": "Glomerulus", "rect": [0.15, 0.20, 0.25, 0.14] },
                { "label": "Proximal tubule", "rect": [0.55, 0.25, 0.25, 0.14] }
              ]
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), """
            ---
            kind: session
            name: Phase 3 Complete Session
            ---
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "01-explore.md"), """
            ---
            kind: article
            title: Renal Anatomy Explorer
            ref: p3-art
            ---

            :::diagram-explore{ref="diag-1" image="assets/nephron.png" masks="assets/nephron.masks.json" title="Nephron"}
            - Glomerulus: Filters blood plasma.
            - Proximal tubule: Reabsorbs sodium and water.
            :::

            :::reveal{ref="rev-1" title="Clinical Pearl" showFirst=false}
            > Question: Why is glucose normally absent in urine?
            Sodium-glucose cotransporters (SGLT2) reabsorb 90% in the PCT.
            :::
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "02-drills.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Phase 3 Drills
            ref: p3-quiz
            ---

            :::classify{ref="cls-1" title="Nephron Segments"}
            [Cortex]
            - Glomerulus
            - Proximal tubule

            [Medulla]
            - Collecting duct
            :::

            :::match{ref="mth-1" title="Hormones"}
            - ADH :: Increases AQP2 channels
            - Aldosterone :: Increases ENaC synthesis
            ! Renin :: Directly constricts bronchi
            :::

            :::numeric{ref="num-1" answer=125 tolerance="5%" unit="mL/min"}
            Normal adult GFR:
            :::

            :::case{ref="cs-1" title="Acute Kidney Injury"}
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

        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_tempDir);

            var applyC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "./chapter3", "--json"], applyC, _appDataDir));
            var wsId = JsonDocument.Parse(applyC.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            var checkC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["check", wsId, "--json"], checkC, _appDataDir));

            var getFull = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--view", "full", "--json"], getFull, _appDataDir));
            Assert.Contains("\"kind\":\"diagram-explore\"", getFull.StandardOutput);
            Assert.Contains("\"kind\":\"reveal\"", getFull.StandardOutput);
            Assert.Contains("\"type\":\"classify\"", getFull.StandardOutput);
            Assert.Contains("\"type\":\"match\"", getFull.StandardOutput);
            Assert.Contains("\"type\":\"numeric\"", getFull.StandardOutput);
            Assert.Contains("\"type\":\"case\"", getFull.StandardOutput);

            var patchNum = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["patch", $"{wsId}#num-1", "--set", "answer=120", "--json"], patchNum, _appDataDir));

            var patchRev = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["patch", $"{wsId}#rev-1", "--set", "showFirst=true", "--json"], patchRev, _appDataDir));

            var buildC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["build", wsId, "-o", "./web3", "--verify", "--static-only", "--json"], buildC, _appDataDir));

            var expC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["pkg", "export", wsId, "android", "./p3.mbypkg", "--json"], expC, _appDataDir));

            var impC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["pkg", "import", "./p3.mbypkg", "./reimp3", "--json"], impC, _appDataDir));
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }

    // 3. CLI lint failures for Phase 3 types exit 2 with structured issue codes
    [Fact]
    public async Task Phase3_CliLintFailure_ExitsValidationCodeWithStructuredReport()
    {
        var assetsDir = Path.Combine(_tempDir, "bad3_assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "img.png"), ValidPngBytes);

        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_tempDir);
            var invalidSpec = """
                {
                  "kind": "session",
                  "name": "Broken Phase 3 Session",
                  "components": [
                    {
                      "kind": "article",
                      "title": "Bad Phase 3 Article",
                      "blocks": [
                        { "kind": "diagram-explore", "imagePath": "bad3_assets/img.png", "hotspots": [] },
                        { "kind": "reveal", "steps": [{ "heading": "", "body": "" }] }
                      ]
                    },
                    {
                      "kind": "quiz",
                      "quizType": "mixed",
                      "title": "Bad Phase 3 Quiz",
                      "questions": [
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
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["apply", "-", "-o", Path.Combine(_tempDir, "ws_bad3"), "--json"], applyC, _appDataDir));
            var wsId = JsonDocument.Parse(applyC.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            var checkC = new TestConsole();
            int exit = await CliApplicationRunner.ExecuteAsync(["check", wsId, "--json"], checkC, _appDataDir);
            Assert.Equal(ExitCodes.ValidationError, exit);
            using var errDoc = JsonDocument.Parse(checkC.StandardError);
            Assert.Equal("Lint.Failed", errDoc.RootElement.GetProperty("error").GetProperty("code").GetString());
            var issuesJson = errDoc.RootElement.GetProperty("data").GetProperty("issues").GetRawText();
            Assert.Contains("Lint.EmptyDiagramHotspots", issuesJson);
            Assert.Contains("Lint.EmptyRevealStep", issuesJson);
            Assert.Contains("Lint.EmptyClassifyBucket", issuesJson);
            Assert.Contains("Lint.InsufficientMatchPairs", issuesJson);
            Assert.Contains("Lint.InvalidNumericTolerance", issuesJson);
            Assert.Contains("Lint.EmptyCaseStem", issuesJson);
            Assert.Contains("Lint.InsufficientCaseQuestions", issuesJson);
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }
}
