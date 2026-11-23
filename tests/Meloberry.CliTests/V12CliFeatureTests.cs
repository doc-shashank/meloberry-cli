using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Xunit;

namespace Meloberry.CliTests;

[Collection("CwdTests")]
public sealed class V12CliFeatureTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_v12_cli_{Guid.NewGuid():N}");
    private readonly string _appDataDir;

    public V12CliFeatureTests()
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
    public async Task PerSubcommandHelp_And_EnhancedSpecTopics_WorkInTextAndJson()
    {
        // 1. Subcommand --help renders command-specific help instead of top-level prime card
        foreach (var verb in new[] { "prime", "spec", "apply", "patch", "get", "ls", "rm", "check", "build", "batch", "pkg", "layout", "analyze", "version" })
        {
            var textC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync([verb, "--help"], textC, _appDataDir));
            Assert.Contains($"mby {verb}", textC.StandardOutput);
            Assert.Contains("Prerequisites:", textC.StandardOutput);
            Assert.Contains("Options:", textC.StandardOutput);
            Assert.Contains("Examples:", textC.StandardOutput);
            Assert.DoesNotContain("Exit: 0=ok", textC.StandardOutput);

            var jsonC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync([verb, "--help", "--json"], jsonC, _appDataDir));
            using var doc = JsonDocument.Parse(jsonC.StandardOutput);
            Assert.Equal(verb, doc.RootElement.GetProperty("data").GetProperty("verb").GetString());
        }

        // help <verb> also shows subcommand help
        var helpAnalyze = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["help", "analyze"], helpAnalyze, _appDataDir));
        Assert.Contains("--headless", helpAnalyze.StandardOutput);

        // 2. Enhanced spec topics include exact constraints & new topics (including batch and patch)
        var specC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["spec", "image", "callout", "markdown", "analyze", "batch", "patch", "--json"], specC, _appDataDir));
        using (var doc = JsonDocument.Parse(specC.StandardOutput))
        {
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            Assert.Equal(6, schemas.GetArrayLength());
            Assert.Contains("caption", specC.StandardOutput);
            Assert.Contains("warning", specC.StandardOutput);
            Assert.Contains("--headless", specC.StandardOutput);
            Assert.Contains("$prev.id", specC.StandardOutput);
            Assert.Contains("--move", specC.StandardOutput);
        }

        // 3. Unknown short flags before '--' fail with Cli.ParseError while bare '-' (stdin) remains valid
        var badShort = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["ls", "-q", "--json"], badShort, _appDataDir));
        Assert.Contains("Cli.ParseError", badShort.StandardError);

        var badShortJsonFirst = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["--json", "ls", "-x"], badShortJsonFirst, _appDataDir));
        Assert.Contains("\"code\":\"Cli.ParseError\"", badShortJsonFirst.StandardError);
    }

    [Fact]
    public async Task ModularDirectoryBuild_DefaultsWorkspacesUnderMeloberry_And_StaticVerifyCatchesBrokenAssets()
    {
        var articlesDir = Path.Combine(_tempDir, "articles");
        var assetsDir = Path.Combine(_tempDir, "assets");
        var webDir = Path.Combine(_tempDir, "study_web");
        Directory.CreateDirectory(articlesDir);
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "fig_2.1.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        await File.WriteAllTextAsync(Path.Combine(articlesDir, "_session.md"), """
            ---
            kind: session
            name: Modular Ch 2
            ---
            """);
        await File.WriteAllTextAsync(Path.Combine(articlesDir, "01-intro.md"), """
            # Introduction to Cell Injury

            ![Overview](assets/fig_2.1.png "Fig. 2.1 Responses to stress")

            :::warning Exam Trap
            Irreversible mitochondrial damage marks the point of no return.
            :::
            """);

        var prevCwd = Directory.GetCurrentDirectory();
        string wsId;
        try
        {
            Directory.SetCurrentDirectory(_tempDir);

            // One-shot build ./articles/ -o ./study_web --verify --static-only
            var buildC = new TestConsole();
            int buildExit = await CliApplicationRunner.ExecuteAsync(
                ["build", "./articles/", "-o", "./study_web", "--verify", "--static-only", "--json"],
                buildC,
                _appDataDir);
            Assert.Equal(ExitCodes.Success, buildExit);

            using (var doc = JsonDocument.Parse(buildC.StandardOutput))
            {
                var data = doc.RootElement.GetProperty("data");
                wsId = data.GetProperty("targetId").GetString()!;
                var storageDir = data.GetProperty("storageDirectory").GetString()!;
                Assert.Contains(Path.Combine(".meloberry", "workspaces"), storageDir, StringComparison.OrdinalIgnoreCase);
                Assert.True(data.GetProperty("verify").GetProperty("passed").GetBoolean());
                Assert.Equal(1, data.GetProperty("verify").GetProperty("imagesChecked").GetInt32());
            }

            // ls discovers workspaces tucked under .meloberry/workspaces automatically
            var lsC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["ls", "--json"], lsC, _appDataDir));
            Assert.Contains(wsId, lsC.StandardOutput);

            // analyze <webDir> --static-only --json passes cleanly
            var anOk = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["analyze", webDir, "--static-only", "--json"], anOk, _appDataDir));
            Assert.True(JsonDocument.Parse(anOk.StandardOutput).RootElement.GetProperty("data").GetProperty("passed").GetBoolean());

            // Corrupt all bundled image assets and confirm analyze --static-only exits 2 with unified failure envelope on stderr
            foreach (var assetFile in Directory.GetFiles(Path.Combine(webDir, "assets")))
                File.Delete(assetFile);
            var anFail = new TestConsole();
            int failExit = await CliApplicationRunner.ExecuteAsync(
                ["analyze", webDir, "--static-only", "--json"], anFail, _appDataDir);
            Assert.Equal(ExitCodes.ValidationError, failExit);
            Assert.True(string.IsNullOrWhiteSpace(anFail.StandardOutput));
            using (var errDoc = JsonDocument.Parse(anFail.StandardError))
            {
                Assert.False(errDoc.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("Verify.Failed", errDoc.RootElement.GetProperty("error").GetProperty("code").GetString());
                Assert.False(errDoc.RootElement.GetProperty("data").GetProperty("passed").GetBoolean());
                Assert.True(errDoc.RootElement.GetProperty("data").GetProperty("brokenAssets").GetArrayLength() > 0);
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }

    [Fact]
    public async Task BuildTargetDisambiguation_EphemeralBuild_CheckStderrEnvelope_LsScoping_And_EnvAppDataIsolation()
    {
        var dirA = Path.Combine(_tempDir, "projA");
        var dirB = Path.Combine(_tempDir, "projB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        // File starting with "Session_" must be treated as a file, not a workspace ID
        var sessionFile = Path.Combine(dirA, "Session_01.md");
        await File.WriteAllTextAsync(sessionFile, "# Session Zero One\n\nBody content.\n");

        var prevCwd = Directory.GetCurrentDirectory();
        var prevEnvAppData = Environment.GetEnvironmentVariable("MBY_APP_DATA");
        try
        {
            Directory.SetCurrentDirectory(dirA);

            // Setting MBY_APP_DATA must not override explicit programmatic appDataDirectory
            Environment.SetEnvironmentVariable("MBY_APP_DATA", Path.Combine(_tempDir, "ignored_env_appdata"));

            // 1. One-shot build of Session_01.md with --ephemeral succeeds and leaves no workspace behind
            var ephemOut = Path.Combine(dirA, "ephem_web");
            var ephemC = new TestConsole();
            int ephemExit = await CliApplicationRunner.ExecuteAsync(
                ["build", "Session_01.md", "-o", ephemOut, "--ephemeral", "--static-only", "--json"],
                ephemC,
                _appDataDir);
            Assert.Equal(ExitCodes.Success, ephemExit);
            Assert.True(File.Exists(Path.Combine(ephemOut, "index.html")));
            Assert.False(Directory.Exists(Path.Combine(dirA, ".meloberry", "workspaces")));

            // 2. Misspelled file path returns Json.FileNotFound (exit 3) instead of BuildWeb.TargetNotFound
            var typoC = new TestConsole();
            int typoExit = await CliApplicationRunner.ExecuteAsync(
                ["build", "./sesson.json", "--json"], typoC, _appDataDir);
            Assert.Equal(ExitCodes.NotFound, typoExit);
            Assert.Contains("\"code\":\"Json.FileNotFound\"", typoC.StandardError);

            // 3. apply --verify without --build fails validation (exit 2)
            var badApplyC = new TestConsole();
            int badApplyExit = await CliApplicationRunner.ExecuteAsync(
                ["apply", "Session_01.md", "--verify", "--json"], badApplyC, _appDataDir);
            Assert.Equal(ExitCodes.ValidationError, badApplyExit);

            // 4. check --json on invalid workspace writes {"success":false,"error":...,"data":...} to stderr (exit 2)
            var invalidQuizC = new TestConsole("""{"kind":"quiz","quizType":"mcq","questions":[{"type":"mcq"}]}""");
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", "-", "--json"], invalidQuizC, _appDataDir));
            var badWsId = JsonDocument.Parse(invalidQuizC.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            var checkFailC = new TestConsole();
            int checkExit = await CliApplicationRunner.ExecuteAsync(["check", badWsId, "--json"], checkFailC, _appDataDir);
            Assert.Equal(ExitCodes.ValidationError, checkExit);
            Assert.True(string.IsNullOrWhiteSpace(checkFailC.StandardOutput));
            using (var errDoc = JsonDocument.Parse(checkFailC.StandardError))
            {
                Assert.False(errDoc.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("Lint.Failed", errDoc.RootElement.GetProperty("error").GetProperty("code").GetString());
                Assert.True(errDoc.RootElement.GetProperty("data").GetProperty("errorCount").GetInt32() > 0);
            }

            // 5. Default `mby ls` scopes to CWD + .meloberry/workspaces, while `--all` lists globally
            Directory.SetCurrentDirectory(dirB);
            var lsLocalC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["ls", "--json"], lsLocalC, _appDataDir));
            Assert.DoesNotContain(badWsId, lsLocalC.StandardOutput);

            var lsAllC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["ls", "--all", "--json"], lsAllC, _appDataDir));
            Assert.Contains(badWsId, lsAllC.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MBY_APP_DATA", prevEnvAppData);
            Directory.SetCurrentDirectory(prevCwd);
        }
    }
}
