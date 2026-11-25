using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Xunit;

namespace Meloberry.CliTests;

public sealed class SpecAuditRegressionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_audit_reg_{Guid.NewGuid():N}");
    private readonly string _appDataDir;
    private readonly string _workDir;

    public SpecAuditRegressionTests()
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
    public async Task Mcq_AutoExpandsMaxOptions_UpTo26_AndRejects27OrExplicitMismatch()
    {
        var opts5 = string.Join(",", Enumerable.Range(1, 5).Select(i =>
            $"{{\"text\":\"Opt {i}\",\"isCorrect\":{(i == 1 ? "true" : "false")}}}"));
        var spec5 = $"{{\"kind\":\"quiz\",\"name\":\"Q5\",\"questions\":[{{\"type\":\"mcq\",\"text\":\"5 opts?\",\"options\":[{opts5}]}}]}}";

        var c5 = new TestConsole(spec5);
        int e5 = await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _workDir, "--json"], c5, _appDataDir);
        Assert.Equal(ExitCodes.Success, e5);

        using var doc5 = JsonDocument.Parse(c5.StandardOutput);
        var id5 = doc5.RootElement.GetProperty("data").GetProperty("targetId").GetString()!;
        Assert.True(doc5.RootElement.GetProperty("data").GetProperty("lint").GetProperty("isClean").GetBoolean());

        var g5 = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", id5, "--view", "full", "--json"], g5, _appDataDir));
        using var g5Doc = JsonDocument.Parse(g5.StandardOutput);
        Assert.Equal(5, g5Doc.RootElement.GetProperty("data").GetProperty("questions")[0].GetProperty("maxOptions").GetInt32());

        // Patching 26 options without maxOptions succeeds and sets maxOptions=26
        var opts26 = string.Join(",", Enumerable.Range(1, 26).Select(i =>
            $"{{\"text\":\"Opt {i}\",\"isCorrect\":{(i == 26 ? "true" : "false")}}}"));
        var p26 = new TestConsole($"{{\"options\":[{opts26}]}}");
        int ep26 = await CliApplicationRunner.ExecuteAsync(["patch", $"{id5}#q1", "-", "--json"], p26, _appDataDir);
        Assert.Equal(ExitCodes.Success, ep26);

        var g26 = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", id5, "--view", "full", "--json"], g26, _appDataDir));
        using var g26Doc = JsonDocument.Parse(g26.StandardOutput);
        Assert.Equal(26, g26Doc.RootElement.GetProperty("data").GetProperty("questions")[0].GetProperty("maxOptions").GetInt32());

        // 27 options fails with Question.InvalidMaxOptions
        var opts27 = string.Join(",", Enumerable.Range(1, 27).Select(i =>
            $"{{\"text\":\"Opt {i}\",\"isCorrect\":{(i == 1 ? "true" : "false")}}}"));
        var spec27 = $"{{\"kind\":\"quiz\",\"name\":\"Q27\",\"questions\":[{{\"type\":\"mcq\",\"text\":\"27?\",\"options\":[{opts27}]}}]}}";
        var c27 = new TestConsole(spec27);
        int e27 = await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _workDir, "--json"], c27, _appDataDir);
        Assert.Equal(ExitCodes.ValidationError, e27);
        Assert.Contains("\"code\":\"Question.InvalidMaxOptions\"", c27.StandardError);

        // Explicit maxOptions:4 with 5 options fails clearly without Index.OutOfIndexBounds
        var specMismatch = $"{{\"kind\":\"quiz\",\"name\":\"Qm\",\"questions\":[{{\"type\":\"mcq\",\"text\":\"5?\",\"maxOptions\":4,\"options\":[{opts5}]}}]}}";
        var cm = new TestConsole(specMismatch);
        int em = await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _workDir, "--json"], cm, _appDataDir);
        Assert.Equal(ExitCodes.ValidationError, em);
        Assert.Contains("\"code\":\"Question.OptionIndexExceedsMaxOptions\"", cm.StandardError);
    }

    [Fact]
    public async Task Apply_AlwaysSurfacesLintSummary_OnNonDryRun_WhileCheckAndBuildGateExit2()
    {
        var badSpec = """{"kind":"quiz","name":"Bad","questions":[{"type":"mcq","text":"?","options":[{"text":"A","isCorrect":false},{"text":"B","isCorrect":false}]}]}""";
        var cApply = new TestConsole(badSpec);
        int exitApply = await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _workDir, "--json"], cApply, _appDataDir);
        Assert.Equal(ExitCodes.Success, exitApply);

        using var doc = JsonDocument.Parse(cApply.StandardOutput);
        var data = doc.RootElement.GetProperty("data");
        var id = data.GetProperty("targetId").GetString()!;
        var lint = data.GetProperty("lint");
        Assert.False(lint.GetProperty("isClean").GetBoolean());
        Assert.Equal(1, lint.GetProperty("errors").GetInt32());
        Assert.Equal(1, lint.GetProperty("warnings").GetInt32());

        var cCheck = new TestConsole();
        int exitCheck = await CliApplicationRunner.ExecuteAsync(["check", id, "--json"], cCheck, _appDataDir);
        Assert.Equal(ExitCodes.ValidationError, exitCheck);
        Assert.Contains("\"code\":\"Lint.Failed\"", cCheck.StandardError);
        Assert.Contains("\"code\":\"Lint.NoCorrectOption\"", cCheck.StandardError);
    }

    [Fact]
    public async Task Patch_RejectsInsufficientMcqOptions_EmptyObject_AndNullOnNonNullableStrings()
    {
        var imgFile = Path.Combine(_tempDir, "fig.png");
        await File.WriteAllBytesAsync(imgFile, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var specPath = Path.Combine(_tempDir, "session.json");
        await File.WriteAllTextAsync(specPath, """
        {
          "kind": "session",
          "name": "Patch Rules",
          "components": [
            {
              "kind": "article",
              "ref": "art",
              "blocks": [
                { "kind": "text", "ref": "blk-txt", "text": "Original text" },
                { "kind": "image", "ref": "blk-img", "imagePath": "fig.png", "caption": "Cap", "alt": "Alt" },
                { "kind": "callout", "ref": "blk-call", "tone": "pearl", "title": "Title", "text": "Body" },
                { "kind": "table", "ref": "blk-tbl", "headers": ["A", "B"], "hideableCols": [2], "rows": [["1", "2"]] }
              ]
            },
            {
              "kind": "quiz",
              "ref": "qz",
              "questions": [
                { "type": "mcq", "ref": "q-one", "text": "Q?", "options": [{"text":"A","isCorrect":true},{"text":"B","isCorrect":false}] }
              ]
            }
          ]
        }
        """);

        var cApply = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", specPath, "-o", _workDir, "--json"], cApply, _appDataDir));
        using var applyDoc = JsonDocument.Parse(cApply.StandardOutput);
        var wsId = applyDoc.RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

        // Empty {} patch fails with Patch.Empty
        var cEmpty = new TestConsole("{}");
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-one", "-", "--json"], cEmpty, _appDataDir));
        Assert.Contains("\"code\":\"Patch.Empty\"", cEmpty.StandardError);

        // {} with --move still succeeds
        var cEmptyMove = new TestConsole("{}");
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#blk-img", "-", "--move", "1:shift", "--json"], cEmptyMove, _appDataDir));

        // MCQ options: [] or 1 option fails on patch
        var cZeroOpts = new TestConsole("""{"options":[]}""");
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-one", "-", "--json"], cZeroOpts, _appDataDir));
        Assert.Contains("\"code\":\"Patch.InvalidValue\"", cZeroOpts.StandardError);

        var cOneOpt = new TestConsole("""{"options":[{"text":"Only","isCorrect":true}]}""");
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-one", "-", "--json"], cOneOpt, _appDataDir));
        Assert.Contains("\"code\":\"Patch.InvalidValue\"", cOneOpt.StandardError);

        // --set text=null on text block fails with Patch.InvalidValue instead of storing "null"
        var cNullText = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#blk-txt", "--set", "text=null", "--json"], cNullText, _appDataDir));
        Assert.Contains("\"code\":\"Patch.InvalidValue\"", cNullText.StandardError);

        var cNullName = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", wsId, "--set", "name=null", "--json"], cNullName, _appDataDir));
        Assert.Contains("\"code\":\"Patch.InvalidValue\"", cNullName.StandardError);

        // Nullable fields still clear cleanly via --set ...=null
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#blk-img", "--set", "caption=null", "--set", "alt=null", "--json"], new TestConsole(), _appDataDir));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#blk-call", "--set", "title=null", "--json"], new TestConsole(), _appDataDir));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#blk-tbl", "--set", "hideableCols=null", "--json"], new TestConsole(), _appDataDir));

        var gTxt = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", $"{wsId}#blk-txt", "--view", "full", "--json"], gTxt, _appDataDir));
        Assert.Contains("\"textContent\":\"Original text\"", gTxt.StandardOutput);
    }

    [Fact]
    public async Task InvalidRefMessage_PkgTrailingSlashDir_And_PrimeSpecConsistency_Work()
    {
        var spec = """{"kind":"quiz","name":"PkgTest","questions":[{"type":"mcq","ref":"q1-ok","text":"2+2?","options":[{"text":"4","isCorrect":true},{"text":"3","isCorrect":false}]}]}""";
        var cApply = new TestConsole(spec);
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _workDir, "--json"], cApply, _appDataDir));
        using var applyDoc = JsonDocument.Parse(cApply.StandardOutput);
        var wsId = applyDoc.RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

        // Address.InvalidRef distinguishes character errors vs length overflow
        var cBadChars = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q1-ok", "--set", "ref=BAD REF", "--json"], cBadChars, _appDataDir));
        Assert.Contains("\"code\":\"Address.InvalidRef\"", cBadChars.StandardError);
        Assert.DoesNotContain("max 32)", cBadChars.StandardError);
        Assert.Contains("disallowed characters", cBadChars.StandardError);

        var cTooLong = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q1-ok", "--set", "ref=abcdefghijklmnopqrstuvwxyz0123456", "--json"], cTooLong, _appDataDir));
        Assert.Contains("exceeds max length 32", cTooLong.StandardError);

        // pkg export with non-existent trailing-slash directory creates dir/<wsId>.mbypkg
        var trailingDir = Path.Combine(_tempDir, "new_pkg_dir") + Path.DirectorySeparatorChar;
        var cPkgDir = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "export", wsId, "desktop", trailingDir, "--json"], cPkgDir, _appDataDir));
        var expectedPkg = Path.Combine(_tempDir, "new_pkg_dir", $"{wsId}.mbypkg");
        Assert.True(File.Exists(expectedPkg));
        Assert.DoesNotContain(".mbypkg.mbypkg", cPkgDir.StandardOutput);

        // pkg export with non-existent file stem appends .mbypkg
        var stemPath = Path.Combine(_tempDir, "custom_name");
        var cPkgStem = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "export", wsId, "desktop", stemPath, "--json"], cPkgStem, _appDataDir));
        Assert.True(File.Exists(stemPath + ".mbypkg"));

        // Built-in prime & spec accuracy
        var cPrime = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["prime"], cPrime, _appDataDir));
        Assert.True(cPrime.StandardOutput.Length < 1600, $"prime exceeded 1600 chars: {cPrime.StandardOutput.Length}");
        Assert.Contains("<setId>#q<m>", cPrime.StandardOutput);
        Assert.Contains("data.lint", cPrime.StandardOutput);

        var cSpec = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "markdown", "table", "assertion-reason", "spot-error"], cSpec, _appDataDir));
        Assert.Contains("allowNoError?=false", cSpec.StandardOutput);
        Assert.Contains("frontmatter-only _session.md", cSpec.StandardOutput);
        Assert.Contains("<stem>_<hash8>.<ext>", cSpec.StandardOutput);
        Assert.Contains("1-based", cSpec.StandardOutput);
        Assert.Contains("5opt for both-false", cSpec.StandardOutput);
    }
}
