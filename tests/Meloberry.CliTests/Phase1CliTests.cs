using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Xunit;

namespace Meloberry.CliTests;

[Collection("CwdTests")]
public sealed class Phase1CliTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_phase1_cli_{Guid.NewGuid():N}");
    private readonly string _appDataDir;

    public Phase1CliTests()
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
    public async Task Phase1_EndToEndCliWorkflow_ApplyCheckGetPatchBuildPkgAndSpec()
    {
        var modDir = Path.Combine(_tempDir, "chapter");
        Directory.CreateDirectory(modDir);

        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), """
            ---
            kind: session
            name: Phase 1 CLI E2E
            ---
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "01-table.md"), """
            ---
            title: Diuretic Summary
            ref: diuretic-summary
            ---

            :::table{ref="tbl-main" title="Loop vs Thiazide" stickyFirstCol=true hideableCols="2,3"}
            | Feature | Loop | Thiazide |
            | :--- | :--- | :--- |
            | Calcium | Decreased | Increased |
            :::footnote
            Key renal comparison.
            :::
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "02-drills.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Phase 1 Drills
            ref: p1-drills
            ---

            :::fill-blank{ref="fb-pfk1" fuzzy=true}
            Committed enzyme of glycolysis: {{phosphofructokinase-1 | PFK-1}}.
            :::explanation
            PFK-1 converts F6P to F1,6BP.
            :::

            :::assertion-reason{ref="ar-cox" answer="both-no-explain" scheme="4opt"}
            A: Aspirin is avoided in viral illness in children.
            R: Aspirin acetylates COX enzymes irreversibly.
            :::explanation
            Due to Reye syndrome.
            :::

            :::spot-error{ref="se-pth" requireFix=false allowNoError=true}
            In [primary hyperparathyroidism], calcium is [high] and phosphate is [~~high~~ -> low].
            :::explanation
            PTH lowers phosphate.
            :::
            """);

        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_tempDir);

            // 1. mby spec for all 4 Phase 1 topics
            var specC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["spec", "table", "fill-blank", "assertion-reason", "spot-error", "--json"], specC, _appDataDir));
            using (var doc = JsonDocument.Parse(specC.StandardOutput))
                Assert.Equal(4, doc.RootElement.GetProperty("data").GetProperty("schemas").GetArrayLength());

            // 2. mby apply ./chapter --json
            var applyC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["apply", "./chapter", "--json"], applyC, _appDataDir));
            string wsId;
            using (var doc = JsonDocument.Parse(applyC.StandardOutput))
                wsId = doc.RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            // 3. mby check <wsId> --json
            var checkC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["check", wsId, "--json"], checkC, _appDataDir));
            using (var doc = JsonDocument.Parse(checkC.StandardOutput))
                Assert.True(doc.RootElement.GetProperty("data").GetProperty("isClean").GetBoolean());

            // 4. mby get <wsId> --view full --json
            var getC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["get", wsId, "--view", "full", "--json"], getC, _appDataDir));
            Assert.Contains("payloadJson", getC.StandardOutput);
            Assert.Contains("fill-blank", getC.StandardOutput);
            Assert.Contains("assertion-reason", getC.StandardOutput);
            Assert.Contains("spot-error", getC.StandardOutput);

            // 5. mby patch <wsId>#fb-pfk1 --set fuzzy=false --json
            var patchC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["patch", $"{wsId}#fb-pfk1", "--set", "fuzzy=false", "--json"], patchC, _appDataDir));

            // 6. mby build <wsId> -o ./web --verify --static-only --json
            var buildC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["build", wsId, "-o", "./web", "--verify", "--static-only", "--json"], buildC, _appDataDir));
            using (var doc = JsonDocument.Parse(buildC.StandardOutput))
                Assert.True(doc.RootElement.GetProperty("data").GetProperty("verify").GetProperty("passed").GetBoolean());

            // 7. mby pkg export & import
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

    [Fact]
    public async Task Phase1_CliLintFailure_ExitsValidationCodeWithStructuredReport()
    {
        var invalidSpec = """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "name": "Broken Phase 1 Quiz",
              "questions": [
                { "type": "fill-blank", "text": "Missing any blank markers." },
                { "type": "spot-error", "noError": true, "text": "Conflicting [valid] and [~~wrong~~ -> right]." }
              ]
            }
            """;

        var applyC = new TestConsole(invalidSpec);
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["apply", "-", "-o", Path.Combine(_tempDir, "ws"), "--json"], applyC, _appDataDir));
        var wsId = JsonDocument.Parse(applyC.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

        var checkC = new TestConsole();
        int exit = await CliApplicationRunner.ExecuteAsync(["check", wsId, "--json"], checkC, _appDataDir);
        Assert.Equal(ExitCodes.ValidationError, exit);
        Assert.True(string.IsNullOrWhiteSpace(checkC.StandardOutput));

        using var errDoc = JsonDocument.Parse(checkC.StandardError);
        Assert.False(errDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Lint.Failed", errDoc.RootElement.GetProperty("error").GetProperty("code").GetString());
        var issuesJson = errDoc.RootElement.GetProperty("data").GetProperty("issues").GetRawText();
        Assert.Contains("Lint.NoFillBlanks", issuesJson);
        Assert.Contains("Lint.SpotErrorConflictingNoError", issuesJson);
    }
}
