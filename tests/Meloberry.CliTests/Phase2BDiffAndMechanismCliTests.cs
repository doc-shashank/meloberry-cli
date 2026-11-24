using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Xunit;

namespace Meloberry.CliTests;

[Collection("CwdTests")]
public sealed class Phase2BDiffAndMechanismCliTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_phase2b_cli_{Guid.NewGuid():N}");
    private readonly string _appDataDir;

    public Phase2BDiffAndMechanismCliTests()
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
    public async Task Phase2B_SpecAndPrimeCli_ExposeDiffAndMechanism_WithinPrimeBudget()
    {
        // 1. mby prime stays < 1550 chars (even under Windows CRLF) and lists diff|mechanism
        var primeC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["prime"], primeC, _appDataDir));
        Assert.True(primeC.StandardOutput.Length < 1550, $"Expected prime < 1550 chars (reserving headroom for 2C), got {primeC.StandardOutput.Length}");
        Assert.Contains("session|article|quiz|text|image|mcq|flashcard|layouts", primeC.StandardOutput);
        Assert.Contains("diff|mechanism", primeC.StandardOutput);

        // 2. mby spec --help dynamically lists diff and mechanism
        var helpC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "--help"], helpC, _appDataDir));
        Assert.Contains("diff", helpC.StandardOutput);
        Assert.Contains("mechanism", helpC.StandardOutput);

        // 3. mby spec diff mechanism --json returns content/question schemas
        var specJsonC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "diff", "mechanism", "--json"], specJsonC, _appDataDir));
        using (var doc = JsonDocument.Parse(specJsonC.StandardOutput))
        {
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            Assert.Equal(2, schemas.GetArrayLength());
            Assert.Equal("diff", schemas[0].GetProperty("topic").GetString());
            Assert.Equal("content", schemas[0].GetProperty("scope").GetString());
            Assert.Equal("question", schemas[0].GetProperty("category").GetString());
            Assert.Equal("mechanism", schemas[1].GetProperty("topic").GetString());
            Assert.Equal("content", schemas[1].GetProperty("scope").GetString());
            Assert.Equal("question", schemas[1].GetProperty("category").GetString());
        }

        // 4. Updated container/format/workflow specs reference diff & mechanism
        var crossC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "quiz", "markdown", "patch"], crossC, _appDataDir));
        Assert.Contains("diff", crossC.StandardOutput);
        Assert.Contains("mechanism", crossC.StandardOutput);
        Assert.Contains(":::diff", crossC.StandardOutput);
        Assert.Contains(":::mechanism", crossC.StandardOutput);
    }

    [Fact]
    public async Task Phase2B_EndToEndCliWorkflow_ApplyCheckGetPatchBuildAndPkg()
    {
        var modDir = Path.Combine(_tempDir, "chapter2b");
        Directory.CreateDirectory(modDir);

        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), """
            ---
            kind: session
            name: Phase 2B Interactive Session
            ---
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "01-interactive.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Diff & Mechanism Drills
            ref: p2b-drills
            ---

            :::diff{ref="diff-gram" mode="spot-wrong" title="Gram Comparison"}
            | Property | Gram-Positive | Gram-Negative |
            | :--- | :--- | :--- |
            | Outer membrane | ~~Present~~ -> Absent \| None | Present |
            :::explanation
            Gram-positive bacteria lack an outer lipid membrane.
            :::

            :::mechanism{ref="mech-raas" title="RAAS Cascade"}
            1. Decreased renal perfusion triggers renin release
            2. Renin converts angiotensinogen to Angiotensin I
            3a. Angiotensin II constricts efferent arterioles
            3b. Angiotensin II stimulates aldosterone release
            ! ANP converts Angiotensin I to Angiotensin II
            :::explanation
            ANP antagonizes RAAS.
            :::
            """);

        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_tempDir);

            var applyC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "./chapter2b", "--json"], applyC, _appDataDir));
            string wsId;
            using (var doc = JsonDocument.Parse(applyC.StandardOutput))
                wsId = doc.RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            var checkC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["check", wsId, "--json"], checkC, _appDataDir));

            var getHuman = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--view", "full"], getHuman, _appDataDir));
            Assert.Contains("Gram Comparison", getHuman.StandardOutput);
            Assert.Contains("RAAS Cascade", getHuman.StandardOutput);

            var patchC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["patch", $"{wsId}#diff-gram", "--set", "title=Updated Gram Table", "--json"], patchC, _appDataDir));

            var buildC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["build", wsId, "-o", "./web2b", "--verify", "--static-only", "--json"], buildC, _appDataDir));

            var expC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["pkg", "export", wsId, "android", "./p2b.mbypkg", "--json"], expC, _appDataDir));

            var impC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["pkg", "import", "./p2b.mbypkg", "./p2b_reimp", "--json"], impC, _appDataDir));
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }

    [Fact]
    public async Task Phase2B_CliLintFailure_ExitsValidationCodeWithStructuredReport()
    {
        var invalidSpec = """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "name": "Broken Phase 2B Quiz",
              "questions": [
                { "type": "diff", "mode": "fill", "headers": ["A", "B"], "rows": [["NoBlank1", "NoBlank2"]] },
                { "type": "mechanism", "steps": [{ "orderGroup": 1, "text": "Only one step" }, { "isDistractor": true, "text": "Decoy" }] }
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
        var issuesJson = errDoc.RootElement.GetProperty("data").GetProperty("issues").GetRawText();
        Assert.Contains("Lint.DiffMissingBlanks", issuesJson);
        Assert.Contains("Lint.InsufficientMechanismSteps", issuesJson);
    }
}
