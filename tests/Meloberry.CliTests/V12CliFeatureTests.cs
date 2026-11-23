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
        foreach (var verb in new[] { "analyze", "pkg", "build" })
        {
            var textC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync([verb, "--help"], textC, _appDataDir));
            Assert.Contains($"mby {verb}", textC.StandardOutput);
            Assert.Contains("Options:", textC.StandardOutput);
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

        // 2. Enhanced spec topics include exact constraints & new topics
        var specC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["spec", "image", "callout", "markdown", "analyze", "--json"], specC, _appDataDir));
        using (var doc = JsonDocument.Parse(specC.StandardOutput))
        {
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            Assert.Equal(4, schemas.GetArrayLength());
            Assert.Contains("caption", specC.StandardOutput);
            Assert.Contains("warning", specC.StandardOutput);
            Assert.Contains("--headless", specC.StandardOutput);
        }
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

            // Corrupt all bundled image assets and confirm analyze --static-only exits 2 (ValidationError)
            foreach (var assetFile in Directory.GetFiles(Path.Combine(webDir, "assets")))
                File.Delete(assetFile);
            var anFail = new TestConsole();
            int failExit = await CliApplicationRunner.ExecuteAsync(
                ["analyze", webDir, "--static-only", "--json"], anFail, _appDataDir);
            Assert.Equal(ExitCodes.ValidationError, failExit);
            Assert.False(JsonDocument.Parse(anFail.StandardOutput).RootElement.GetProperty("data").GetProperty("passed").GetBoolean());
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }
}
