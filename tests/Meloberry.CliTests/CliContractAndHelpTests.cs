using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Commands;
using Meloberry.Cli.Hosting;
using Meloberry.Cli.Parsing;
using Meloberry.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.CliTests;

[Collection("CwdTests")]
public sealed class CliContractAndHelpTests : IDisposable
{
    private readonly WorkspaceTestFixture _fx = new("mby_cli_contract");

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Prime_And_SpecCatalog_ExposeAll28TopicsAndGroups_WithinCrlfBudget()
    {
        var cEmpty = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync([], cEmpty, _fx.AppDataDir));
        var crlfPrime = cEmpty.StandardOutput.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.True(crlfPrime.Length < 1550, $"Expected CRLF prime < 1550 chars, got {crlfPrime.Length}");
        Assert.Contains("session|article|quiz|text|image|mcq|flashcard|layouts", crlfPrime);
        Assert.Contains("diff|mechanism|relabel", crlfPrime);
        Assert.Contains("diagram-explore|reveal|case|classify|match|numeric", crlfPrime);
        Assert.Contains("<setId>#q<m>", crlfPrime);
        Assert.Contains("data.lint", crlfPrime);
        Assert.DoesNotContain("minigame", crlfPrime, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chart", crlfPrime, StringComparison.OrdinalIgnoreCase);

        var cHelp = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--help"], cHelp, _fx.AppDataDir));
        Assert.Equal(cEmpty.StandardOutput, cHelp.StandardOutput);

        var specHelp = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "--help"], specHelp, _fx.AppDataDir));
        foreach (var topic in new[] { "table", "fill-blank", "assertion-reason", "spot-error", "diff", "mechanism", "relabel", "diagram-explore", "reveal", "case", "classify", "match", "numeric" })
            Assert.Contains(topic, specHelp.StandardOutput);

        var cMcq = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "mcq"], cMcq, _fx.AppDataDir));
        Assert.True(cMcq.StandardOutput.Length < 650, $"spec mcq should be < 650 chars, got {cMcq.StandardOutput.Length}");
        Assert.Contains("isMultiCorrect", cMcq.StandardOutput);

        var cBlocks = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "@blocks", "--json"], cBlocks, _fx.AppDataDir));
        using (var doc = JsonDocument.Parse(cBlocks.StandardOutput))
        {
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            Assert.Equal(6, schemas.GetArrayLength());
            var topics = schemas.EnumerateArray().Select(s => s.GetProperty("topic").GetString()).ToList();
            Assert.Contains("diagram-explore", topics);
            Assert.Contains("reveal", topics);
        }

        var cQuestions = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "@questions", "--json"], cQuestions, _fx.AppDataDir));
        using (var doc = JsonDocument.Parse(cQuestions.StandardOutput))
        {
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            Assert.Equal(12, schemas.GetArrayLength());
            Assert.All(schemas.EnumerateArray(), s =>
            {
                Assert.Equal("content", s.GetProperty("scope").GetString());
                Assert.Equal("question", s.GetProperty("category").GetString());
            });
        }

        var cOther = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "@other", "--json"], cOther, _fx.AppDataDir));
        using (var doc = JsonDocument.Parse(cOther.StandardOutput))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(6, data.GetProperty("schemas").GetArrayLength());
            Assert.True(data.TryGetProperty("layoutProperties", out var lp) && lp.GetArrayLength() > 0);
        }

        var cTextSpecs = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "markdown", "table", "assertion-reason", "spot-error", "diff", "mechanism", "relabel", "patch", "batch"], cTextSpecs, _fx.AppDataDir));
        Assert.Contains("allowNoError?=false", cTextSpecs.StandardOutput);
        Assert.Contains("frontmatter-only _session.md", cTextSpecs.StandardOutput);
        Assert.Contains("<stem>_<hash8>.<ext>", cTextSpecs.StandardOutput);
        Assert.Contains("1-based", cTextSpecs.StandardOutput);
        Assert.Contains("5opt for both-false", cTextSpecs.StandardOutput);
        Assert.Contains(":::relabel", cTextSpecs.StandardOutput);
        Assert.Contains("$prev.id", cTextSpecs.StandardOutput);

        foreach (var unknown in new[] { "chart", "@bogus" })
        {
            var cErr = new TestConsole();
            Assert.Equal(ExitCodes.NotFound, await CliApplicationRunner.ExecuteAsync(["spec", unknown, "--json"], cErr, _fx.AppDataDir));
            Assert.Contains("\"code\":\"Schema.TopicNotFound\"", cErr.StandardError);
            Assert.DoesNotContain('\n', cErr.StandardError.Trim());
        }
    }

    [Theory]
    [InlineData("prime")]
    [InlineData("spec")]
    [InlineData("apply")]
    [InlineData("patch")]
    [InlineData("get")]
    [InlineData("ls")]
    [InlineData("rm")]
    [InlineData("check")]
    [InlineData("build")]
    [InlineData("batch")]
    [InlineData("pkg")]
    [InlineData("layout")]
    [InlineData("analyze")]
    [InlineData("version")]
    public async Task SubcommandHelp_RendersCommandSpecificHelpInTextAndJson(string verb)
    {
        var textC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync([verb, "--help"], textC, _fx.AppDataDir));
        Assert.Contains($"mby {verb}", textC.StandardOutput);
        Assert.Contains("Prerequisites:", textC.StandardOutput);
        Assert.Contains("Options:", textC.StandardOutput);
        Assert.Contains("Examples:", textC.StandardOutput);
        Assert.DoesNotContain("Exit: 0=ok", textC.StandardOutput);

        var jsonC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync([verb, "--help", "--json"], jsonC, _fx.AppDataDir));
        using var doc = JsonDocument.Parse(jsonC.StandardOutput);
        Assert.Equal(verb, doc.RootElement.GetProperty("data").GetProperty("verb").GetString());
    }

    [Fact]
    public async Task Flags_ExitCodes_DispatcherEnvelope_Timeout_Bom_And_BatchContracts_Work()
    {
        // help <verb> & version
        var helpAnalyze = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["help", "analyze"], helpAnalyze, _fx.AppDataDir));
        Assert.Contains("--headless", helpAnalyze.StandardOutput);

        var verConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["-v", "--json"], verConsole, _fx.AppDataDir));
        Assert.Contains("\"executableName\":\"mby\"", verConsole.StandardOutput);

        // Unknown long & short flags -> exit 2 Cli.ParseError
        foreach (var args in new[]
        {
            new[] { "ls", "--bogus" },
            new[] { "ls", "-q", "--json" },
            new[] { "--json", "ls", "-x" }
        })
        {
            var badFlag = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(args, badFlag, _fx.AppDataDir));
            Assert.Contains("Cli.ParseError", badFlag.StandardError);
        }

        // --timeout bounds on apply, build, analyze
        foreach (var args in new[]
        {
            new[] { "apply", "-", "--timeout", "999", "--json" },
            new[] { "build", "-", "--timeout", "999", "--json" },
            new[] { "analyze", "./nonexistent", "--headless", "--timeout", "999", "--json" }
        })
        {
            var c = new TestConsole("{}");
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(args, c, _fx.AppDataDir));
            Assert.Contains("\"code\":\"Cli.InvalidArgs\"", c.StandardError);
        }

        // apply --verify without --build fails validation
        var badVerify = new TestConsole("{}");
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["apply", "-", "--verify", "--json"], badVerify, _fx.AppDataDir));

        // Dispatcher unhandled exception -> exit 1 Cli.UnexpectedError envelope
        var dispatcher = new CommandDispatcher([new ThrowingTestCommand()], new ThrowingServiceScopeFactory());
        var crashConsole = new TestConsole();
        Assert.Equal(ExitCodes.GeneralError, await dispatcher.ExecuteAsync(["boom", "--json"], crashConsole));
        using (var errDoc = JsonDocument.Parse(crashConsole.StandardError))
        {
            Assert.False(errDoc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Cli.UnexpectedError", errDoc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        // BOM stdin + --dry-run leaves WorkDir empty
        var bomStdin = "\uFEFF" + """{"kind":"quiz","quizType":"mcq","questions":[{"type":"mcq","text":"BOM ok?","options":[{"text":"Yes","isCorrect":true},{"text":"No"}]}]}""";
        var bomConsole = new TestConsole(bomStdin);
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _fx.WorkDir, "--dry-run", "--json"], bomConsole, _fx.AppDataDir));
        Assert.Contains("\"dryRun\":true", bomConsole.StandardOutput);
        Assert.Empty(Directory.GetDirectories(_fx.WorkDir));

        // Batch: invalid timeout rejected, rollback on step 2 leaves WorkDir empty, valid batch with $0.id succeeds
        var badTimeoutBatch = new TestConsole("""[{"op":"build","addr":"Session_20260101_000000_000","timeout":999}]""");
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["batch", "-", "--json"], badTimeoutBatch, _fx.AppDataDir));
        Assert.Contains("Batch.InvalidOp", badTimeoutBatch.StandardError);

        var badBatch = $$"""
        [
          { "op": "apply", "out": "{{_fx.WorkDir.Replace("\\", "\\\\")}}", "spec": { "kind": "quiz", "quizType": "mcq", "name": "TempQuiz" } },
          { "op": "patch", "addr": "$0.id", "data": { "unknownField": 123 } }
        ]
        """;
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["batch", "-", "--json"], new TestConsole(badBatch), _fx.AppDataDir));
        Assert.Empty(Directory.GetDirectories(_fx.WorkDir));

        var goodBatch = $$"""
        [
          { "op": "apply", "out": "{{_fx.WorkDir.Replace("\\", "\\\\")}}", "spec": { "kind": "quiz", "quizType": "mcq", "name": "Initial" } },
          { "op": "patch", "addr": "$0.id", "data": { "name": "Renamed Quiz" } },
          { "op": "apply", "addr": "$0.id", "spec": { "questions": [{ "type": "mcq", "text": "2+2?", "options": [{ "text": "4", "isCorrect": true }, { "text": "3" }] }] } },
          { "op": "check", "addr": "$0.id" }
        ]
        """;
        var goodBatchC = new TestConsole(goodBatch);
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["batch", "-", "--json"], goodBatchC, _fx.AppDataDir));
        Assert.Contains("\"stepCount\":4", goodBatchC.StandardOutput);
    }

    [Fact]
    public async Task McqLimits_PatchNullGuards_PkgPathResolution_LsScoping_And_EphemeralBuild_Work()
    {
        // MCQ auto-expands maxOptions up to 26, rejects 27 & explicit mismatch
        var opts5 = string.Join(",", Enumerable.Range(1, 5).Select(i => $"{{\"text\":\"Opt {i}\",\"isCorrect\":{(i == 1 ? "true" : "false")}}}"));
        var c5 = new TestConsole($"{{\"kind\":\"quiz\",\"name\":\"Q5\",\"questions\":[{{\"type\":\"mcq\",\"text\":\"5 opts?\",\"options\":[{opts5}]}}]}}");
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _fx.WorkDir, "--json"], c5, _fx.AppDataDir));
        var id5 = JsonDocument.Parse(c5.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

        var opts26 = string.Join(",", Enumerable.Range(1, 26).Select(i => $"{{\"text\":\"Opt {i}\",\"isCorrect\":{(i == 26 ? "true" : "false")}}}"));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{id5}#q1", "-", "--json"], new TestConsole($"{{\"options\":[{opts26}]}}"), _fx.AppDataDir));
        var g26 = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", id5, "--view", "full", "--json"], g26, _fx.AppDataDir));
        Assert.Equal(26, JsonDocument.Parse(g26.StandardOutput).RootElement.GetProperty("data").GetProperty("questions")[0].GetProperty("maxOptions").GetInt32());

        var opts27 = string.Join(",", Enumerable.Range(1, 27).Select(i => $"{{\"text\":\"Opt {i}\",\"isCorrect\":{(i == 1 ? "true" : "false")}}}"));
        var c27 = new TestConsole($"{{\"kind\":\"quiz\",\"name\":\"Q27\",\"questions\":[{{\"type\":\"mcq\",\"text\":\"27?\",\"options\":[{opts27}]}}]}}");
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _fx.WorkDir, "--json"], c27, _fx.AppDataDir));
        Assert.Contains("\"code\":\"Question.InvalidMaxOptions\"", c27.StandardError);

        var cm = new TestConsole($"{{\"kind\":\"quiz\",\"name\":\"Qm\",\"questions\":[{{\"type\":\"mcq\",\"text\":\"5?\",\"maxOptions\":4,\"options\":[{opts5}]}}]}}");
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _fx.WorkDir, "--json"], cm, _fx.AppDataDir));
        Assert.Contains("\"code\":\"Question.OptionIndexExceedsMaxOptions\"", cm.StandardError);

        // Patch guards: empty {}, empty {} + --move, <2 options, null on non-nullable text/name, null on nullable fields, InvalidRef reasons
        var pngPath = Path.Combine(_fx.TempRoot, "fig.png");
        await TestAssets.WritePngAsync(pngPath);
        var patchSpecPath = Path.Combine(_fx.TempRoot, "patch_rules.json");
        await File.WriteAllTextAsync(patchSpecPath, """
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
                { "type": "mcq", "ref": "q-one", "text": "Q?", "layoutOverrides": {"optionColumns":"2"}, "options": [{"text":"A","isCorrect":true},{"text":"B","isCorrect":false}] }
              ]
            }
          ]
        }
        """);
        var cApply = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", patchSpecPath, "-o", _fx.WorkDir, "--json"], cApply, _fx.AppDataDir));
        var wsId = JsonDocument.Parse(cApply.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

        var cEmpty = new TestConsole("{}");
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-one", "-", "--json"], cEmpty, _fx.AppDataDir));
        Assert.Contains("\"code\":\"Patch.Empty\"", cEmpty.StandardError);
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#blk-img", "-", "--move", "1:shift", "--json"], new TestConsole("{}"), _fx.AppDataDir));

        foreach (var badOpts in new[] { """{"options":[]}""", """{"options":[{"text":"Only","isCorrect":true}]}""" })
        {
            var cBad = new TestConsole(badOpts);
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-one", "-", "--json"], cBad, _fx.AppDataDir));
            Assert.Contains("\"code\":\"Patch.InvalidValue\"", cBad.StandardError);
        }

        var cNullText = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#blk-txt", "--set", "text=null", "--json"], cNullText, _fx.AppDataDir));
        Assert.Contains("\"code\":\"Patch.InvalidValue\"", cNullText.StandardError);
        var cNullName = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", wsId, "--set", "name=null", "--json"], cNullName, _fx.AppDataDir));
        Assert.Contains("\"code\":\"Patch.InvalidValue\"", cNullName.StandardError);

        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#blk-img", "--set", "caption=null", "--set", "alt=null", "--json"], new TestConsole(), _fx.AppDataDir));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#blk-call", "--set", "title=null", "--json"], new TestConsole(), _fx.AppDataDir));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#blk-tbl", "--set", "hideableCols=null", "--json"], new TestConsole(), _fx.AppDataDir));

        var cBadRef = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-one", "--set", "ref=BAD REF", "--json"], cBadRef, _fx.AppDataDir));
        Assert.Contains("disallowed characters", cBadRef.StandardError);
        var cLongRef = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsId}#q-one", "--set", "ref=abcdefghijklmnopqrstuvwxyz0123456", "--json"], cLongRef, _fx.AppDataDir));
        Assert.Contains("exceeds max length 32", cLongRef.StandardError);

        // pkg export: non-interactive warnings, trailing-slash dir, stem auto-extension, --include-layout
        var trailingDir = Path.Combine(_fx.TempRoot, "new_pkg_dir") + Path.DirectorySeparatorChar;
        var cPkgDir = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "export", wsId, "desktop", trailingDir, "--json"], cPkgDir, _fx.AppDataDir));
        Assert.True(File.Exists(Path.Combine(_fx.TempRoot, "new_pkg_dir", $"{wsId}.mbypkg")));
        Assert.Contains("\"warnings\":", cPkgDir.StandardOutput);
        Assert.DoesNotContain(".mbypkg.mbypkg", cPkgDir.StandardOutput);

        var stemPath = Path.Combine(_fx.TempRoot, "custom_name");
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["pkg", "export", wsId, "android", stemPath, "--include-layout", "--json"], new TestConsole(), _fx.AppDataDir));
        Assert.True(File.Exists(stemPath + ".mbypkg"));

        // File starting with "Session_" + --ephemeral + ls scoping + external DB folder build + MBY_APP_DATA isolation
        var dirA = _fx.SubDir("projA");
        var dirB = _fx.SubDir("projB");
        var sessionFile = Path.Combine(dirA, "Session_01.md");
        await File.WriteAllTextAsync(sessionFile, "# Session Zero One\n\nBody content.\n");

        var prevEnvAppData = Environment.GetEnvironmentVariable("MBY_APP_DATA");
        try
        {
            Environment.SetEnvironmentVariable("MBY_APP_DATA", Path.Combine(_fx.TempRoot, "ignored_env_appdata"));
            using (_fx.PushCwd(dirA))
            {
                var ephemOut = Path.Combine(dirA, "ephem_web");
                Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["build", "Session_01.md", "-o", ephemOut, "--ephemeral", "--static-only", "--json"], new TestConsole(), _fx.AppDataDir));
                Assert.True(File.Exists(Path.Combine(ephemOut, "index.html")));
                Assert.False(Directory.Exists(Path.Combine(dirA, ".meloberry", "workspaces")));

                var typoC = new TestConsole();
                Assert.Equal(ExitCodes.NotFound, await CliApplicationRunner.ExecuteAsync(["build", "./sesson.json", "--json"], typoC, _fx.AppDataDir));
                Assert.Contains("\"code\":\"Json.FileNotFound\"", typoC.StandardError);
            }

            using (_fx.PushCwd(dirB))
            {
                var lsLocalC = new TestConsole();
                Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["ls", "--json"], lsLocalC, _fx.AppDataDir));
                Assert.DoesNotContain(wsId, lsLocalC.StandardOutput);

                var lsAllC = new TestConsole();
                Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["ls", "--all", "--json"], lsAllC, _fx.AppDataDir));
                Assert.Contains(wsId, lsAllC.StandardOutput);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MBY_APP_DATA", prevEnvAppData);
        }

        // Build unregistered external workspace DB folder under a brand-new --app-data directory
        var storageDir = Path.Combine(_fx.WorkDir, wsId);
        var freshAppData = Path.Combine(_fx.TempRoot, "appdata_fresh");
        var extWeb = Path.Combine(_fx.TempRoot, "ext_web");
        var extBuildC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["build", storageDir, "-o", extWeb, "--static-only", "--json"], extBuildC, freshAppData));
        Assert.True(JsonDocument.Parse(extBuildC.StandardOutput).RootElement.GetProperty("data").GetProperty("verify").GetProperty("passed").GetBoolean());
    }

    private sealed class ThrowingTestCommand() : CliCommand(new CommandDescriptor("boom", "boom", "Test throwing command", typeof(ThrowingHandler)))
    {
        public sealed class ThrowingHandler : ICommandHandler
        {
            public Task<int> InvokeAsync(CliInvocationContext context) => throw new InvalidOperationException("Simulated unhandled crash");
        }
    }

    private sealed class ThrowingServiceScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ThrowingScope();

        private sealed class ThrowingScope : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) => serviceType == typeof(ThrowingTestCommand.ThrowingHandler) ? new ThrowingTestCommand.ThrowingHandler() : null;
            public void Dispose() { }
        }
    }
}
