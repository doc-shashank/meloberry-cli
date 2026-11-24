using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Xunit;

namespace Meloberry.CliTests;

[Collection("CwdTests")]
public sealed class Phase2CliTests : IDisposable
{
    private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAUAAAADICAYAAACZBDirAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAKoSURBVHhe7dShAQAgDMCw/X8tEjc8LzQipgd0zt0FKJo/AFQYIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBA1gNPI9UTnmi3rAAAAABJRU5ErkJggg==");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_phase2c_cli_{Guid.NewGuid():N}");
    private readonly string _appDataDir;

    public Phase2CliTests()
    {
        Directory.CreateDirectory(_tempDir);
        _appDataDir = Path.Combine(_tempDir, "appdata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // 1. SPEC TESTS FIRST: Verify CLI prime, spec --help, spec relabel, and spec @questions
    [Fact]
    public async Task Phase2_SpecAndPrimeCli_ExposeRelabelAndGroupCatalog_WithinPrimeBudget()
    {
        var primeC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["prime"], primeC, _appDataDir));
        Assert.True(primeC.StandardOutput.Length < 1550, $"Expected prime < 1550 chars, got {primeC.StandardOutput.Length}");
        Assert.Contains("session|article|quiz|text|image|mcq|flashcard|layouts", primeC.StandardOutput);
        Assert.Contains("diff|mechanism|relabel", primeC.StandardOutput);

        var helpC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "--help"], helpC, _appDataDir));
        Assert.Contains("diff", helpC.StandardOutput);
        Assert.Contains("mechanism", helpC.StandardOutput);
        Assert.Contains("relabel", helpC.StandardOutput);

        var specJsonC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "diff", "mechanism", "relabel", "--json"], specJsonC, _appDataDir));
        using (var doc = JsonDocument.Parse(specJsonC.StandardOutput))
        {
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            Assert.Equal(3, schemas.GetArrayLength());
            Assert.Equal("relabel", schemas[2].GetProperty("topic").GetString());
            Assert.Equal("content", schemas[2].GetProperty("scope").GetString());
            Assert.Equal("question", schemas[2].GetProperty("category").GetString());
        }

        var groupC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "@questions", "--json"], groupC, _appDataDir));
        using (var doc = JsonDocument.Parse(groupC.StandardOutput))
        {
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            Assert.Equal(8, schemas.GetArrayLength());
            var topics = schemas.EnumerateArray().Select(s => s.GetProperty("topic").GetString()).ToList();
            Assert.Contains("relabel", topics);
        }

        var crossC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "quiz", "markdown", "patch"], crossC, _appDataDir));
        Assert.Contains("relabel", crossC.StandardOutput);
        Assert.Contains(":::relabel", crossC.StandardOutput);
    }

    // 2. Full Phase 2 E2E CLI workflow
    [Fact]
    public async Task Phase2_EndToEndCliWorkflow_ApplyCheckGetPatchBuildPkgAndSpec()
    {
        var modDir = Path.Combine(_tempDir, "chapter2c");
        var assetsDir = Path.Combine(modDir, "assets");
        Directory.CreateDirectory(assetsDir);

        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "diagram.png"), ValidPngBytes);
        await File.WriteAllTextAsync(Path.Combine(assetsDir, "diagram.masks.json"), """
            {
              "boxes": [
                { "label": "Glomerulus", "rect": [0.15, 0.20, 0.25, 0.14] },
                { "label": "Bowman capsule", "rect": [0.55, 0.25, 0.25, 0.14] }
              ]
            }
            """);
        // Root-level *.masks.json sidecar should be ignored by directory loader
        await File.WriteAllTextAsync(Path.Combine(modDir, "root.masks.json"), """{"boxes":[{"label":"Ignored","rect":[0.1,0.1,0.2,0.2]}]}""");

        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), """
            ---
            kind: session
            name: Phase 2 Complete Session
            ---
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "01-interactive.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Full Phase 2 Drills
            ref: p2-full
            ---

            :::diff{ref="diff-1" mode="spot-wrong" title="Gram Comparison"}
            | Property | Gram+ | Gram- |
            | :--- | :--- | :--- |
            | Outer membrane | ~~Present~~ -> Absent | Present |
            :::

            :::mechanism{ref="mech-1" title="RAAS Cascade"}
            1. Renin release
            2. Angiotensin I formation
            ! ANP activates renin
            :::

            :::relabel{ref="rel-1" image="assets/diagram.png" masks="assets/diagram.masks.json" mode="bank" style="blur"}
            prompt: Label the renal corpuscle.
            :::
            """);

        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_tempDir);

            var helpC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "--help"], helpC, _appDataDir));
            Assert.Contains("relabel", helpC.StandardOutput);

            var specC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "diff", "mechanism", "relabel", "--json"], specC, _appDataDir));

            var applyC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "./chapter2c", "--json"], applyC, _appDataDir));
            var wsId = JsonDocument.Parse(applyC.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            var checkC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["check", wsId, "--json"], checkC, _appDataDir));

            var getHuman = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--view", "full"], getHuman, _appDataDir));
            Assert.Contains("Gram Comparison", getHuman.StandardOutput);
            Assert.Contains("RAAS Cascade", getHuman.StandardOutput);
            Assert.Contains("Label the renal corpuscle.", getHuman.StandardOutput);

            var getJson = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--view", "full", "--json"], getJson, _appDataDir));
            Assert.Contains("\"type\":\"relabel\"", getJson.StandardOutput);

            var patchC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["patch", $"{wsId}#rel-1", "--set", "style=whiteout", "--json"], patchC, _appDataDir));

            var buildC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["build", wsId, "-o", "./web2c", "--verify", "--static-only", "--json"], buildC, _appDataDir));

            var expC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["pkg", "export", wsId, "android", "./out.mbypkg", "--json"], expC, _appDataDir));

            var impC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["pkg", "import", "./out.mbypkg", "./reimp", "--json"], impC, _appDataDir));
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }

    // 3. CLI lint failure across diff + mechanism + relabel exits 2 with structured report
    [Fact]
    public async Task Phase2_CliLintFailure_ExitsValidationCodeWithStructuredReport()
    {
        var assetsDir = Path.Combine(_tempDir, "bad_assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "img.png"), ValidPngBytes);

        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_tempDir);
            var invalidSpec = """
                {
                  "kind": "quiz",
                  "quizType": "mixed",
                  "name": "Broken Phase 2 Quiz",
                  "questions": [
                    { "type": "diff", "mode": "fill", "headers": ["A", "B"], "rows": [["NoBlank1", "NoBlank2"]] },
                    { "type": "mechanism", "steps": [{ "orderGroup": 1, "text": "Only one step" }, { "isDistractor": true, "text": "Decoy" }] },
                    { "type": "relabel", "imagePath": "bad_assets/img.png", "mode": "bank", "style": "solid", "boxes": [{ "label": "Out", "x": 0.85, "y": 0.2, "w": 0.3, "h": 0.1 }] }
                  ]
                }
                """;

            var applyC = new TestConsole(invalidSpec);
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["apply", "-", "-o", Path.Combine(_tempDir, "ws_bad"), "--json"], applyC, _appDataDir));
            var wsId = JsonDocument.Parse(applyC.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            var checkC = new TestConsole();
            int exit = await CliApplicationRunner.ExecuteAsync(["check", wsId, "--json"], checkC, _appDataDir);
            Assert.Equal(ExitCodes.ValidationError, exit);
            using var errDoc = JsonDocument.Parse(checkC.StandardError);
            Assert.Equal("Lint.Failed", errDoc.RootElement.GetProperty("error").GetProperty("code").GetString());
            var issuesJson = errDoc.RootElement.GetProperty("data").GetProperty("issues").GetRawText();
            Assert.Contains("Lint.DiffMissingBlanks", issuesJson);
            Assert.Contains("Lint.InsufficientMechanismSteps", issuesJson);
            Assert.Contains("Lint.InvalidRelabelBoxBounds", issuesJson);
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }
}
