using System.Reflection;
using System.Text.Json;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
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
    public async Task Prime_And_SpecCatalog_ExposeAll29TopicsAndGroups_WithinCrlfBudget()
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
            Assert.Equal(7, schemas.GetArrayLength());
            var topics = schemas.EnumerateArray().Select(s => s.GetProperty("topic").GetString()).ToList();
            Assert.Contains("diagram-explore", topics);
            Assert.Contains("reveal", topics);
            Assert.Contains("link", topics);
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
    [InlineData("clone")]
    [InlineData("get")]
    [InlineData("ls")]
    [InlineData("rm")]
    [InlineData("check")]
    [InlineData("asset")]
    [InlineData("scaffold")]
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

    [Fact]
    public async Task SpecCommand_WithIconsFlag_IncludesIconsAndDisplayNames_WhileDefaultOmitsIcons()
    {
        var defaultConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "@content", "--json"], defaultConsole, _fx.AppDataDir));
        using (var defaultDoc = JsonDocument.Parse(defaultConsole.StandardOutput))
        {
            var data = defaultDoc.RootElement.GetProperty("data");
            Assert.False(data.TryGetProperty("icons", out _));
            var schemas = data.GetProperty("schemas");
            Assert.All(schemas.EnumerateArray(), item =>
            {
                Assert.True(item.TryGetProperty("displayName", out var dn));
                Assert.False(string.IsNullOrWhiteSpace(dn.GetString()));
            });
        }

        var iconsConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "--icons", "--json"], iconsConsole, _fx.AppDataDir));
        using (var iconsDoc = JsonDocument.Parse(iconsConsole.StandardOutput))
        {
            var data = iconsDoc.RootElement.GetProperty("data");
            Assert.True(data.TryGetProperty("icons", out var iconsEl));
            foreach (var requiredIcon in new[]
            {
                "text", "image", "callout", "callout-clinical", "table", "link",
                "diagram-explore", "reveal", "mcq", "mcq-multi", "flashcard",
                "fill-blank", "assertion-reason", "spot-error", "diff",
                "mechanism", "relabel", "relabel-blur", "case", "classify", "match", "numeric"
            })
            {
                Assert.True(iconsEl.TryGetProperty(requiredIcon, out var svgEl), $"Missing icon '{requiredIcon}'.");
                Assert.StartsWith("<svg", svgEl.GetString(), StringComparison.OrdinalIgnoreCase);
            }
        }

        var blocksIconsConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "@blocks", "--icons", "--json"], blocksIconsConsole, _fx.AppDataDir));
        using (var blocksDoc = JsonDocument.Parse(blocksIconsConsole.StandardOutput))
        {
            var iconsEl = blocksDoc.RootElement.GetProperty("data").GetProperty("icons");
            Assert.True(iconsEl.TryGetProperty("diagram-explore", out _));
            Assert.True(iconsEl.TryGetProperty("callout-clinical", out _));
            Assert.False(iconsEl.TryGetProperty("mcq", out _));
        }
    }

    [Fact]
    public async Task GetCommand_ViewSpec_AndHelp_WorkWithinPrimeBudget()
    {
        var helpC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", "--help"], helpC, _fx.AppDataDir));
        Assert.Contains("spec", helpC.StandardOutput);

        var applyC = new TestConsole("""{"kind":"session","name":"CLI Spec","components":[{"kind":"article","ref":"a1","blocks":[{"kind":"table","headers":["A","B"],"rows":[["1","2"]]}]}]}""");
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "-", "-o", _fx.WorkDir, "--json"], applyC, _fx.AppDataDir));
        var wsId = JsonDocument.Parse(applyC.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

        var getSpecC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsId, "--view", "spec", "--json"], getSpecC, _fx.AppDataDir));
        Assert.DoesNotContain("payloadJson", getSpecC.StandardOutput, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(getSpecC.StandardOutput);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("spec", data.GetProperty("view").GetString());
        Assert.Equal("session", data.GetProperty("spec").GetProperty("kind").GetString());
        Assert.Equal(wsId, data.GetProperty("idMap").GetProperty("id").GetString());

        var primeC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["prime"], primeC, _fx.AppDataDir));
        var crlfPrime = primeC.StandardOutput.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.True(crlfPrime.Length < 1550, $"Expected CRLF prime < 1550 chars, got {crlfPrime.Length}");
    }

    [Fact]
    public async Task VersionJson_ExposesStableCapabilitiesList()
    {
        var verConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["version", "--json"], verConsole, _fx.AppDataDir));
        using var doc = JsonDocument.Parse(verConsole.StandardOutput);
        var data = doc.RootElement.GetProperty("data");
        Assert.True(data.TryGetProperty("capabilities", out var capsEl), "Expected 'capabilities' array on version --json.");
        var caps = capsEl.EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
        {
            "get.view.spec",
            "get.path.dir",
            "patch.component.links",
            "patch.case.declarative",
            "spec.icons",
            "spec.nested",
            "pkg.import.metadata",
            "batch.atomic",
            "spec.linkTopic",
            "spec.modeGroups",
            "spec.markdownTemplates",
            "reorg.crossComponentMove",
            "reorg.positionalInsert",
            "reorg.clone",
            "reorg.batchReorder",
            "asset.cli",
            "scaffold.cli",
            "preset.mbypreset",
            "markdown.roundTrip",
            "prime.agentsMd",
            "build.allThemes",
            "build.signal",
            "ls.web",
            "analyze.ingest"
        })
        {
            Assert.Contains(required, caps);
        }
        Assert.Equal("2.2.0-beta-2", data.GetProperty("version").GetString());
    }

    [Fact]
    public async Task SpecCommand_WithTemplatesAndLink_ExposesModeGroupsSupportsImageAndStarters()
    {
        var cLink = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "link", "--templates", "--json"], cLink, _fx.AppDataDir));
        using (var linkDoc = JsonDocument.Parse(cLink.StandardOutput))
        {
            var schema = linkDoc.RootElement.GetProperty("data").GetProperty("schemas")[0];
            Assert.Equal("link", schema.GetProperty("topic").GetString());
            Assert.Equal("Hover Link Card", schema.GetProperty("displayName").GetString());
            Assert.True(schema.GetProperty("supportsImage").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(schema.GetProperty("markdownHint").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(schema.GetProperty("markdownStarter").GetString()));
        }

        var cDefault = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "mcq", "--json"], cDefault, _fx.AppDataDir));
        using (var defDoc = JsonDocument.Parse(cDefault.StandardOutput))
        {
            var mcq = defDoc.RootElement.GetProperty("data").GetProperty("schemas")[0];
            Assert.True(mcq.GetProperty("supportsImage").GetBoolean());
            Assert.True(mcq.TryGetProperty("modeGroups", out var mg) && mg.GetArrayLength() == 1);
            Assert.False(mcq.TryGetProperty("markdownStarter", out _));
        }
    }

    [Fact]
    public async Task SpecPatchParity_EveryPatchableFieldIsDocumentedInSpec()
    {
        var field = typeof(PatchEntityHandler).GetField("AllowedKeysByKind", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var allowedByKind = Assert.IsType<Dictionary<string, HashSet<string>>>(field!.GetValue(null));

        var handler = new GetSchemaHandler();
        var schemaRes = await handler.HandleAsync(new GetSchemaQuery());
        Assert.True(schemaRes.IsSuccess);
        var byTopic = schemaRes.Value.Schemas.ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase);

        foreach (var (kind, allowedKeys) in allowedByKind)
        {
            HashSet<string> documentedFields;
            if (kind == "component")
            {
                documentedFields = byTopic["article"].Fields.Concat(byTopic["quiz"].Fields)
                    .Select(f => f.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                var topic = kind.Contains(':') ? kind.Split(':', 2)[1] : kind;
                Assert.True(byTopic.TryGetValue(topic, out var topicDef), $"No spec topic '{topic}' for patch kind '{kind}'.");
                documentedFields = topicDef!.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var key in allowedKeys)
            {
                Assert.True(
                    documentedFields.Contains(key),
                    $"Patchable key '{key}' on '{kind}' is missing from GetSchemaHandler fields.");
            }
        }

        Assert.Contains("links?", byTopic["patch"].CompactSignature);
    }

    [Fact]
    public async Task SpecNestedSchemas_OptionalFlag_DescribesComplexItemTypes()
    {
        var helpC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "--help"], helpC, _fx.AppDataDir));
        Assert.Contains("--nested", helpC.StandardOutput);

        var defaultC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "relabel", "--json"], defaultC, _fx.AppDataDir));
        using (var defaultDoc = JsonDocument.Parse(defaultC.StandardOutput))
        {
            var data = defaultDoc.RootElement.GetProperty("data");
            Assert.False(data.TryGetProperty("nestedTypes", out _));
            Assert.True(data.TryGetProperty("schemaVersion", out var sv));
            Assert.False(string.IsNullOrWhiteSpace(sv.GetString()));
        }

        var relabelNestedC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "relabel", "--nested", "--json"], relabelNestedC, _fx.AppDataDir));
        using (var relabelDoc = JsonDocument.Parse(relabelNestedC.StandardOutput))
        {
            var nestedTypes = relabelDoc.RootElement.GetProperty("data").GetProperty("nestedTypes");
            Assert.True(nestedTypes.TryGetProperty("relabelBox", out var boxFields));
            Assert.False(nestedTypes.TryGetProperty("mcqOption", out _));
            var fieldNames = boxFields.EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
            foreach (var expected in new[] { "label", "aliases", "rect", "x", "y", "w", "h" })
                Assert.Contains(expected, fieldNames);
        }

        var allNestedC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "--nested", "--json"], allNestedC, _fx.AppDataDir));
        using (var allDoc = JsonDocument.Parse(allNestedC.StandardOutput))
        {
            var nestedTypes = allDoc.RootElement.GetProperty("data").GetProperty("nestedTypes");
            foreach (var expectedType in new[]
            {
                "mcqOption", "flashcardSide", "relabelBox", "diagramHotspot",
                "revealStep", "mechanismStep", "bucket", "classifyItem", "matchPair", "linkCard"
            })
            {
                Assert.True(nestedTypes.TryGetProperty(expectedType, out var fields), $"Missing nestedType '{expectedType}'.");
                Assert.True(fields.GetArrayLength() > 0);
            }
        }
    }

    [Fact]
    public async Task V2_2_0_Commands_Flags_Signal_WebDiscovery_And_TelemetryIngest_WorkEndToEnd()
    {
        // 1. prime --format agents-md -o <file>
        var agentsOut = Path.Combine(_fx.WorkDir, "AGENTS.md");
        var primeC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["prime", "--topics", "mcq,link", "--container", "article", "--format", "agents-md", "--cli-prefix", ".\\mby-pc.bat mby", "-o", agentsOut, "--json"],
            primeC, _fx.AppDataDir));
        using (var doc = JsonDocument.Parse(primeC.StandardOutput))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("2.2.0-beta-2", data.GetProperty("version").GetString());
            Assert.True(data.GetProperty("approximateTokens").GetInt32() > 0);
            Assert.Equal(Path.GetFullPath(agentsOut), data.GetProperty("writtenPath").GetString());
        }
        Assert.True(File.Exists(agentsOut));

        // 2. scaffold - -o <dir> --placeholders --json -> apply <dir> -o <WorkDir>
        var blueprintJson = """
        {
          "container": "session",
          "title": "CLI v2.2 Blueprint",
          "components": [
            {
              "id": "c1",
              "kind": "article",
              "title": "Chapter 1",
              "selections": [{ "topic": "text", "enabled": true, "desiredCount": 2 }]
            },
            {
              "id": "c2",
              "kind": "article",
              "title": "Chapter 2",
              "selections": [{ "topic": "callout", "enabled": true, "desiredCount": 1 }]
            }
          ]
        }
        """;
        var scaffoldDir = _fx.SubDir("scaffold_md");
        var scaffoldC = new TestConsole(blueprintJson);
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["scaffold", "-", "-o", scaffoldDir, "--placeholders", "--json"],
            scaffoldC, _fx.AppDataDir));
        using (var sDoc = JsonDocument.Parse(scaffoldC.StandardOutput))
        {
            var data = sDoc.RootElement.GetProperty("data");
            Assert.Equal(scaffoldDir, data.GetProperty("writtenPath").GetString());
        }

        var applyScaffoldC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["apply", scaffoldDir, "-o", _fx.WorkDir, "--json"],
            applyScaffoldC, _fx.AppDataDir));
        string wsId;
        using (var aDoc = JsonDocument.Parse(applyScaffoldC.StandardOutput))
        {
            wsId = aDoc.RootElement.GetProperty("data").GetProperty("targetId").GetString()!;
            Assert.StartsWith("Session_", wsId);
        }
        var wsAddr = Path.Combine(_fx.WorkDir, wsId);

        // 3. asset add / ls / prune
        var pngPath = Path.Combine(_fx.WorkDir, "fig.png");
        await TestAssets.WritePngAsync(pngPath);
        var assetAddC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["asset", "add", wsAddr, pngPath, "--json"], assetAddC, _fx.AppDataDir));
        var assetLsC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["asset", "ls", wsAddr, "--json"], assetLsC, _fx.AppDataDir));
        Assert.Contains("\"isOrphan\":true", assetLsC.StandardOutput);
        var assetPruneC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["asset", "prune", wsAddr, "--json"], assetPruneC, _fx.AppDataDir));

        // 4. clone, patch --md, patch --to --at, apply --at
        var cloneC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["clone", $"{wsAddr}#c1.b1", "--json"], cloneC, _fx.AppDataDir));

        var patchMdC = new TestConsole("Patched markdown body");
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsAddr}#c1.b1", "--md", "-", "--json"], patchMdC, _fx.AppDataDir));

        var moveC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["patch", $"{wsAddr}#c1.b1", "--to", $"{wsAddr}#c2", "--at", "1", "--json"], moveC, _fx.AppDataDir));

        var applyAtFile = Path.Combine(_fx.WorkDir, "inserted_comp.md");
        await File.WriteAllTextAsync(applyAtFile, "---\nkind: article\ntitle: Inserted First\n---\n\nInserted paragraph.\n");
        var applyAtC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", wsAddr, applyAtFile, "--at", "1", "--json"], applyAtC, _fx.AppDataDir));

        // 5. get --view markdown -o <dir>
        var mdExportDir = _fx.SubDir("exported_md");
        var getMdC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", wsAddr, "--view", "markdown", "-o", mdExportDir, "--json"], getMdC, _fx.AppDataDir));
        Assert.True(File.Exists(Path.Combine(mdExportDir, "_session.md")));

        // 6. build --all-themes --signal --signal-dir
        var scanDir = _fx.SubDir("built_scan");
        var webDir = Path.Combine(scanDir, "study_web");
        var sigDir = _fx.SubDir("sig_root");
        var buildC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["build", wsAddr, "-o", webDir, "--all-themes", "--signal", "--signal-dir", sigDir, "--json"],
            buildC, _fx.AppDataDir));
        Assert.True(File.Exists(Path.Combine(sigDir, ".mby-signal.txt")));
        Assert.Contains("mby-theme-override", await File.ReadAllTextAsync(Path.Combine(webDir, "app.js")));

        // 7. analyze --ingest - & ls --web
        var rawReport = $$"""
        {
          "report": {
            "schemaVersion": "1.0",
            "workspaceId": "{{wsId}}",
            "title": "CLI v2.2 Blueprint",
            "startedAtUtc": "2026-11-28T07:00:00Z",
            "completedAtUtc": "2026-11-28T07:02:00Z",
            "totalDurationMs": 5000,
            "summary": {
              "totalSlides": 3,
              "mcqTotal": 0,
              "mcqCorrectFirstTry": 0,
              "mcqCorrectFinal": 0,
              "mcqAccuracyPercent": 0,
              "flashcardTotal": 0,
              "flashcardKnown": 0,
              "flashcardNeedsReview": 0,
              "articleSlidesViewed": 3
            },
            "slides": [],
            "weakItemIds": []
          }
        }
        """;
        var ingestC = new TestConsole(rawReport);
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["analyze", webDir, "--ingest", "-", "--json"], ingestC, _fx.AppDataDir));

        var lsWebC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["ls", scanDir, "--web", "--json"], lsWebC, _fx.AppDataDir));
        using (var lsDoc = JsonDocument.Parse(lsWebC.StandardOutput))
        {
            var arr = lsDoc.RootElement.GetProperty("data");
            Assert.Equal(1, arr.GetArrayLength());
            Assert.Equal(1, arr[0].GetProperty("reportCount").GetInt32());
        }
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
