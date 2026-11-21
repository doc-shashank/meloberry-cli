using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Xunit;

namespace Meloberry.CliTests;

public sealed class AgentCliV2Tests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_v2_tests_{Guid.NewGuid():N}");
    private readonly string _appDataDir;
    private readonly string _workDir;

    public AgentCliV2Tests()
    {
        Directory.CreateDirectory(_tempDir);
        _appDataDir = Path.Combine(_tempDir, "appdata");
        _workDir = Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task NoArgs_And_Help_EmitPrimeCardStatelessly_WithOnlyImplementedSpecs()
    {
        var c1 = new TestConsole();
        int exit1 = await CliApplicationRunner.ExecuteAsync([], c1, _appDataDir);
        Assert.Equal(ExitCodes.Success, exit1);
        Assert.True(c1.StandardOutput.Length < 1600, $"prime card should be < 1600 chars, got {c1.StandardOutput.Length}");
        Assert.Contains("session|article|quiz|text|image|mcq|flashcard|layouts", c1.StandardOutput);
        Assert.DoesNotContain("minigame", c1.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chart", c1.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cloze", c1.StandardOutput, StringComparison.OrdinalIgnoreCase);

        var c2 = new TestConsole();
        int exit2 = await CliApplicationRunner.ExecuteAsync(["--help"], c2, _appDataDir);
        Assert.Equal(ExitCodes.Success, exit2);
        Assert.Equal(c1.StandardOutput, c2.StandardOutput);
    }

    [Fact]
    public async Task Spec_ReturnsCompactLazyMicroSchemas_And_RejectsUnimplementedTopics()
    {
        var cMcq = new TestConsole();
        int exitMcq = await CliApplicationRunner.ExecuteAsync(["spec", "mcq"], cMcq, _appDataDir);
        Assert.Equal(ExitCodes.Success, exitMcq);
        Assert.True(cMcq.StandardOutput.Length < 650, $"spec mcq should be < 650 chars, got {cMcq.StandardOutput.Length}");
        Assert.Contains("isMultiCorrect", cMcq.StandardOutput);

        var cUnknown = new TestConsole();
        int exitUnknown = await CliApplicationRunner.ExecuteAsync(["spec", "chart", "--json"], cUnknown, _appDataDir);
        Assert.Equal(ExitCodes.NotFound, exitUnknown);
        Assert.Contains("\"code\":\"Schema.TopicNotFound\"", cUnknown.StandardError);
        Assert.DoesNotContain('\n', cUnknown.StandardError.Trim());
    }

    [Fact]
    public async Task OneShotBuildFromStdin_GetOutline_Patch_Rm_And_MinifiedJson_WorkEndToEnd()
    {
        var pngPath = Path.Combine(_workDir, "diagram.png");
        await File.WriteAllBytesAsync(pngPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02]);

        var specJson = $$"""
        {
          "kind": "session",
          "name": "Physics Review",
          "description": "Kinematics",
          "components": [
            {
              "kind": "article",
              "ref": "notes",
              "title": "Equations",
              "blocks": [
                { "kind": "text", "ref": "intro-text", "text": "# Kinematics\n\n$$v = u + at$$ is a fundamental formula for constant acceleration motion across long explanations." },
                { "kind": "image", "imagePath": "work/diagram.png" }
              ]
            },
            {
              "kind": "quiz",
              "ref": "check",
              "quizType": "mixed",
              "questions": [
                {
                  "type": "mcq",
                  "ref": "q-force",
                  "text": "SI unit of force?",
                  "explanation": "Force is measured in Newtons.",
                  "options": [
                    { "text": "Newton", "isCorrect": true },
                    { "text": "Joule", "isCorrect": false }
                  ]
                },
                {
                  "type": "flashcard",
                  "front": { "text": "Formula for force?" },
                  "back": { "text": "$F = ma$" }
                }
              ]
            }
          ]
        }
        """;

        // One-shot build from stdin ('-') with relative asset resolution from CWD
        var prevCwd = Directory.GetCurrentDirectory();
        var webOut = Path.Combine(_tempDir, "dist_web");
        string wsId;
        try
        {
            Directory.SetCurrentDirectory(_tempDir);
            var buildConsole = new TestConsole(specJson);
            int buildExit = await CliApplicationRunner.ExecuteAsync(["build", "-", "-o", webOut, "--json"], buildConsole, _appDataDir);
            Assert.Equal(ExitCodes.Success, buildExit);
            Assert.DoesNotContain('\n', buildConsole.StandardOutput.Trim());
            Assert.True(File.Exists(Path.Combine(webOut, "index.html")));
            Assert.True(File.Exists(Path.Combine(webOut, "styles.css")));
            Assert.True(File.Exists(Path.Combine(webOut, "app.js")));

            using var doc = JsonDocument.Parse(buildConsole.StandardOutput);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            wsId = doc.RootElement.GetProperty("data").GetProperty("targetId").GetString()!;
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }

        // get defaults to outline (omits full textContent, provides compact snippet + path + ref)
        var getOutline = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--json"], getOutline, _appDataDir));
        Assert.DoesNotContain('\n', getOutline.StandardOutput.Trim());
        Assert.DoesNotContain("fundamental formula for constant acceleration", getOutline.StandardOutput);
        Assert.Contains("\"path\":\"c1.b1\"", getOutline.StandardOutput);
        Assert.Contains("\"ref\":\"intro-text\"", getOutline.StandardOutput);

        // get --view full returns complete text
        var getFull = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--view", "full", "--json"], getFull, _appDataDir));
        Assert.Contains("fundamental formula for constant acceleration", getFull.StandardOutput);

        // get --view ids returns minimal ID map
        var getIds = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--view", "ids", "--json"], getIds, _appDataDir));
        Assert.DoesNotContain("snippet", getIds.StandardOutput);
        Assert.Contains("\"path\":\"c2.q1\"", getIds.StandardOutput);

        // patch via --set using #ref addressing
        var patchConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["patch", $"{wsId}#q-force", "--set", "text=SI unit of Force (N)?", "--set", "layoutOverrides.optionColumns=2", "--json"],
            patchConsole, _appDataDir));
        Assert.DoesNotContain('\n', patchConsole.StandardOutput.Trim());

        // rm deletes immediately without any -y flag
        var rmBlock = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["rm", $"{wsId}#c1.b2", "--json"], rmBlock, _appDataDir));
        Assert.Contains("\"deletedId\":\"ArtComp_", rmBlock.StandardOutput);

        var rmSession = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["rm", wsId, "--json"], rmSession, _appDataDir));
        Assert.Contains("\"entityKind\":\"session\"", rmSession.StandardOutput);

        var getMissing = new TestConsole();
        Assert.Equal(ExitCodes.NotFound, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--json"], getMissing, _appDataDir));
    }

    [Fact]
    public async Task ApplyDryRun_DoesNotPersist_And_BatchRollsBackOnFailure()
    {
        var dryJson = """{"kind":"quiz","quizType":"mcq","questions":[{"type":"mcq","text":"Q?","options":[{"text":"A","isCorrect":true},{"text":"B"}]}]}""";
        var dryConsole = new TestConsole(dryJson);
        int dryExit = await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _workDir, "--dry-run", "--json"], dryConsole, _appDataDir);
        Assert.Equal(ExitCodes.Success, dryExit);
        Assert.Contains("\"dryRun\":true", dryConsole.StandardOutput);
        Assert.Empty(Directory.GetDirectories(_workDir));

        // Batch with failing step 2 rolls back step 1 completely
        var badBatch = $$"""
        [
          { "op": "apply", "out": "{{_workDir.Replace("\\", "\\\\")}}", "spec": { "kind": "quiz", "quizType": "mcq", "name": "TempQuiz" } },
          { "op": "patch", "addr": "$0.id", "data": { "unknownField": 123 } }
        ]
        """;
        var badBatchConsole = new TestConsole(badBatch);
        int badExit = await CliApplicationRunner.ExecuteAsync(["batch", "-", "--json"], badBatchConsole, _appDataDir);
        Assert.Equal(ExitCodes.ValidationError, badExit);
        Assert.Empty(Directory.GetDirectories(_workDir));

        // Valid batch with $0.id substitution commits cleanly
        var goodBatch = $$"""
        [
          { "op": "apply", "out": "{{_workDir.Replace("\\", "\\\\")}}", "spec": { "kind": "quiz", "quizType": "mcq", "name": "Initial" } },
          { "op": "patch", "addr": "$0.id", "data": { "name": "Renamed Quiz" } },
          { "op": "apply", "addr": "$0.id", "spec": { "questions": [{ "type": "mcq", "text": "2+2?", "options": [{ "text": "4", "isCorrect": true }, { "text": "3" }] }] } },
          { "op": "check", "addr": "$0.id" }
        ]
        """;
        var goodBatchConsole = new TestConsole(goodBatch);
        int goodExit = await CliApplicationRunner.ExecuteAsync(["batch", "-", "--json"], goodBatchConsole, _appDataDir);
        Assert.Equal(ExitCodes.Success, goodExit);
        Assert.Contains("\"stepCount\":4", goodBatchConsole.StandardOutput);
    }

    [Fact]
    public async Task PkgExport_NeverPrompts_EmitsWarnings_And_RoundTrips()
    {
        var spec = """{"kind":"quiz","quizType":"mcq","name":"PkgQuiz","questions":[{"type":"mcq","text":"2+2?","layoutOverrides":{"optionColumns":"2"},"options":[{"text":"4","isCorrect":true},{"text":"3"}]}]}""";
        var applyConsole = new TestConsole(spec);
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _workDir, "--json"], applyConsole, _appDataDir));
        using var doc = JsonDocument.Parse(applyConsole.StandardOutput);
        var quizId = doc.RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

        // Export content-only with overrides present: succeeds non-interactively with warnings
        var pkg1 = Path.Combine(_tempDir, "content_only.mbypkg");
        var expConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "export", quizId, "desktop", pkg1, "--json"], expConsole, _appDataDir));
        Assert.True(File.Exists(pkg1));
        Assert.Contains("\"warnings\":", expConsole.StandardOutput);

        // Export with --include-layout and import into new directory
        var pkg2 = Path.Combine(_tempDir, "with_layout.mbypkg");
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "export", quizId, "android", pkg2, "--include-layout", "--json"], new TestConsole(), _appDataDir));

        var importDir = Path.Combine(_tempDir, "imported");
        var impConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "import", pkg2, importDir, "--json"], impConsole, _appDataDir));
        Assert.True(File.Exists(Path.Combine(importDir, quizId, $"{quizId}.db")));
    }

    [Fact]
    public async Task Layout_Ls_Version_UnknownFlag_And_MbyAgentEnv_WorkCleanly()
    {
        var layoutSet = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["layout", "set", "mcq", "optionColumns", "2", "--json"], layoutSet, _appDataDir));
        Assert.Contains("\"normalizedValue\":\"2\"", layoutSet.StandardOutput);

        var layoutGet = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["layout", "get", "mcq", "--json"], layoutGet, _appDataDir));
        Assert.Contains("\"property\":\"optionColumns\"", layoutGet.StandardOutput);

        var lsConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["ls", _workDir, "--json"], lsConsole, _appDataDir));

        var verConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["-v", "--json"], verConsole, _appDataDir));
        Assert.Contains("\"executableName\":\"mby\"", verConsole.StandardOutput);

        // Unknown flags fail fast with exit 2
        var badFlag = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["ls", "--bogus"], badFlag, _appDataDir));
        Assert.Contains("Cli.ParseError", badFlag.StandardError);

        // Leading UTF-8 BOM from Windows PowerShell stdin pipes is stripped cleanly
        var bomStdin = "\uFEFF" + """{"kind":"quiz","quizType":"mcq","questions":[{"type":"mcq","text":"BOM ok?","options":[{"text":"Yes","isCorrect":true},{"text":"No"}]}]}""";
        var bomConsole = new TestConsole(bomStdin);
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _workDir, "--dry-run", "--json"], bomConsole, _appDataDir));
    }
}
