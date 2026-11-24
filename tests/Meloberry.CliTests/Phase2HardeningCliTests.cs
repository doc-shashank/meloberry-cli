using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Commands;
using Meloberry.Cli.Hosting;
using Meloberry.Cli.Parsing;
using Xunit;

namespace Meloberry.CliTests;

[Collection("CwdTests")]
public sealed class Phase2HardeningCliTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_phase2a_cli_{Guid.NewGuid():N}");
    private readonly string _appDataDir;

    public Phase2HardeningCliTests()
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
    public async Task Phase2A_CliHardening_HelpSyncHumanGetPayloadPreviewAndRootSidecarIgnore()
    {
        // 1. mby spec --help lists Phase 1 topics dynamically
        var helpC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "--help"], helpC, _appDataDir));
        Assert.Contains("table", helpC.StandardOutput);
        Assert.Contains("fill-blank", helpC.StandardOutput);
        Assert.Contains("assertion-reason", helpC.StandardOutput);
        Assert.Contains("spot-error", helpC.StandardOutput);

        // 2. Apply directory containing root sidecar (*.masks.json) + table + fill-blank
        var modDir = Path.Combine(_tempDir, "module");
        Directory.CreateDirectory(modDir);
        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), """
            ---
            kind: session
            name: CLI Hardening Session
            ---
            """);
        await File.WriteAllTextAsync(Path.Combine(modDir, "root.masks.json"), """
            {"boxes":[{"label":"Right Atrium","x":0.1,"y":0.2,"w":0.3,"h":0.1}]}
            """);
        await File.WriteAllTextAsync(Path.Combine(modDir, "01-table.md"), """
            ---
            title: Renal Table
            ---

            :::table{ref="tbl-renal" title="Electrolytes"}
            | Ion | Level |
            | --- | --- |
            | Sodium | 140 |
            :::
            """);
        await File.WriteAllTextAsync(Path.Combine(modDir, "02-quiz.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Quick Recall
            ---

            :::fill-blank{ref="fb-na"}
            Normal serum sodium is {{140 | 135-145}} mEq/L.
            :::
            """);

        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_tempDir);

            var applyC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["apply", "./module", "--json"], applyC, _appDataDir));
            string wsId;
            using (var doc = JsonDocument.Parse(applyC.StandardOutput))
                wsId = doc.RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

            // 3. Human `mby get <wsId> --view full` displays payload previews for table and fill-blank
            var getC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["get", wsId, "--view", "full"], getC, _appDataDir));
            Assert.Contains("Electrolytes", getC.StandardOutput);
            Assert.Contains("140", getC.StandardOutput);
            Assert.Contains("Normal serum sodium", getC.StandardOutput);
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }

    [Fact]
    public async Task Phase2A_CliEdgeCases_TimeoutUpperBoundAndExternalWorkspaceFolderBuild()
    {
        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_tempDir);

            // 1. --timeout 999 fails with ExitCodes.ValidationError (Cli.InvalidArgs) on apply, build, and analyze
            foreach (var args in new[]
            {
                new[] { "apply", "-", "--timeout", "999", "--json" },
                new[] { "build", "-", "--timeout", "999", "--json" },
                new[] { "analyze", "./nonexistent", "--headless", "--timeout", "999", "--json" }
            })
            {
                var c = new TestConsole("{}");
                int code = await CliApplicationRunner.ExecuteAsync(args, c, _appDataDir);
                Assert.Equal(ExitCodes.ValidationError, code);
                using var errDoc = JsonDocument.Parse(c.StandardError);
                Assert.Equal("Cli.InvalidArgs", errDoc.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            // 2. Build an unregistered external workspace DB folder using a brand-new --app-data directory
            var externalBase = Path.Combine(_tempDir, "external_vault");
            Directory.CreateDirectory(externalBase);
            var createAppData = Path.Combine(_tempDir, "appdata_create");
            var freshAppData = Path.Combine(_tempDir, "appdata_fresh");

            var specJson = """
                {
                  "kind": "session",
                  "name": "External Vault Session",
                  "components": [
                    {
                      "kind": "article",
                      "title": "Overview",
                      "blocks": [{ "kind": "text", "text": "# External\n\nStill buildable." }]
                    }
                  ]
                }
                """;
            var applyC = new TestConsole(specJson);
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["apply", "-", "-o", externalBase, "--json"], applyC, createAppData));
            string storageDir;
            using (var doc = JsonDocument.Parse(applyC.StandardOutput))
                storageDir = doc.RootElement.GetProperty("data").GetProperty("storageDirectory").GetString()!;

            var webOut = Path.Combine(_tempDir, "ext_web");
            var buildC = new TestConsole();
            int buildExit = await CliApplicationRunner.ExecuteAsync(
                ["build", storageDir, "-o", webOut, "--static-only", "--json"], buildC, freshAppData);
            Assert.Equal(ExitCodes.Success, buildExit);
            using (var doc = JsonDocument.Parse(buildC.StandardOutput))
                Assert.True(doc.RootElement.GetProperty("data").GetProperty("verify").GetProperty("passed").GetBoolean());
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }

    [Fact]
    public async Task Phase2A1_CliDispatcherEnvelope_SpecGroups_And_PatchBatchGuards()
    {
        // 1. Top-level CommandDispatcher catches unhandled exceptions and emits JSON error envelope
        var throwingCmd = new ThrowingTestCommand();
        var dispatcher = new CommandDispatcher([throwingCmd], new ThrowingServiceScopeFactory());
        var crashConsole = new TestConsole();
        int crashExit = await dispatcher.ExecuteAsync(["boom", "--json"], crashConsole);
        Assert.Equal(ExitCodes.GeneralError, crashExit);
        using (var errDoc = JsonDocument.Parse(crashConsole.StandardError))
        {
            Assert.False(errDoc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Cli.UnexpectedError", errDoc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        // 2. Two-tier spec groups via CLI (`mby spec @questions --json`, `mby spec @other --json`, `mby spec @bogus --json`)
        var qSpecConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "@questions", "--json"], qSpecConsole, _appDataDir));
        using (var doc = JsonDocument.Parse(qSpecConsole.StandardOutput))
        {
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            Assert.Equal(8, schemas.GetArrayLength());
            var topics = schemas.EnumerateArray().Select(s => s.GetProperty("topic").GetString()).ToList();
            Assert.Contains("diff", topics);
            Assert.Contains("mechanism", topics);
            Assert.Contains("relabel", topics);
            Assert.All(schemas.EnumerateArray(), s =>
            {
                Assert.Equal("content", s.GetProperty("scope").GetString());
                Assert.Equal("question", s.GetProperty("category").GetString());
            });
        }

        var otherSpecConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "@other", "--json"], otherSpecConsole, _appDataDir));
        using (var doc = JsonDocument.Parse(otherSpecConsole.StandardOutput))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(6, data.GetProperty("schemas").GetArrayLength());
            Assert.True(data.TryGetProperty("layoutProperties", out var lp) && lp.GetArrayLength() > 0);
        }

        var bogusSpecConsole = new TestConsole();
        Assert.Equal(ExitCodes.NotFound, await CliApplicationRunner.ExecuteAsync(["spec", "@bogus", "--json"], bogusSpecConsole, _appDataDir));
        Assert.Contains("Schema.TopicNotFound", bogusSpecConsole.StandardError);

        // 3. Batch timeout bounds via CLI
        var batchConsole = new TestConsole("""[{"op":"build","addr":"Session_20260101_000000_000","timeout":999}]""");
        int batchExit = await CliApplicationRunner.ExecuteAsync(["batch", "-", "--json"], batchConsole, _appDataDir);
        Assert.Equal(ExitCodes.ValidationError, batchExit);
        Assert.Contains("Batch.InvalidOp", batchConsole.StandardError);
    }

    private sealed class ThrowingTestCommand() : CliCommand(new CommandDescriptor("boom", "boom", "Test throwing command", typeof(ThrowingHandler)))
    {
        public sealed class ThrowingHandler : ICommandHandler
        {
            public Task<int> InvokeAsync(CliInvocationContext context) => throw new InvalidOperationException("Simulated unhandled crash");
        }
    }

    private sealed class ThrowingServiceScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new ThrowingScope();

        private sealed class ThrowingScope : Microsoft.Extensions.DependencyInjection.IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) => serviceType == typeof(ThrowingTestCommand.ThrowingHandler) ? new ThrowingTestCommand.ThrowingHandler() : null;
            public void Dispose() { }
        }
    }
}
