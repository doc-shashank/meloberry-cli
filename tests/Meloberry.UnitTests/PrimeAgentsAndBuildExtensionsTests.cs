using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Application.UseCases.Quizzes;
using Meloberry.Domain.Common;
using Meloberry.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class PrimeAgentsAndBuildExtensionsTests : IDisposable
{
    private readonly WorkspaceTestFixture _fx = new("mby_prime_build");

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void TokenEstimator_ProducesPositiveDeterministicBpeEstimates()
    {
        Assert.Equal(0, TokenEstimator.EstimateTokens(string.Empty));
        Assert.Equal(0, TokenEstimator.EstimateTokens("   \r\n  "));

        var shortEstimate = TokenEstimator.EstimateTokens("mby apply ./study --json");
        var longEstimate = TokenEstimator.EstimateTokens(string.Join("\n", Enumerable.Repeat("## Section\nContent block with `table` and `mcq` schema fields.", 12)));

        Assert.True(shortEstimate > 0);
        Assert.True(longEstimate > shortEstimate * 5);
    }

    [Fact]
    public async Task PrimeHandler_CardFormat_StaysWithinCrlfBudget_And_AgentsMdFormat_GeneratesTargetedPlaybook()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetPrimeGuideHandler>();

        var cardResult = await handler.HandleAsync(new GetPrimeGuideQuery());
        Assert.True(cardResult.IsSuccess, cardResult.Error?.Description);
        var crlfCard = cardResult.Value.Guide.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.True(crlfCard.Length < 1550, $"Default prime card CRLF length was {crlfCard.Length} (>= 1550).");
        Assert.Contains("link", cardResult.Value.SpecTopics);
        Assert.Contains("clone", cardResult.Value.Commands);
        Assert.Contains("asset", cardResult.Value.Commands);
        Assert.Contains("scaffold", cardResult.Value.Commands);
        Assert.True(cardResult.Value.ApproximateTokens > 0);

        var outFile = Path.Combine(_fx.WorkDir, "AGENTS.generated.md");
        var agentsResult = await handler.HandleAsync(new GetPrimeGuideQuery(
            Topics: ["mcq", "link", "markdown"],
            Container: "article",
            Format: "agents-md",
            CliPrefix: @".\mby-pc.bat mby",
            AuthoringFormat: "markdownDirectory",
            WorkspacePath: _fx.WorkDir,
            OutputFilePath: outFile));

        Assert.True(agentsResult.IsSuccess, agentsResult.Error?.Description);
        Assert.Equal(outFile, agentsResult.Value.WrittenPath);
        Assert.True(File.Exists(outFile));
        Assert.NotNull(agentsResult.Value.IncludedTopics);
        Assert.Contains("mcq", agentsResult.Value.IncludedTopics);
        Assert.Contains("link", agentsResult.Value.IncludedTopics);
        Assert.Contains("markdown", agentsResult.Value.IncludedTopics);
        Assert.True(agentsResult.Value.ApproximateTokens > 20);
        Assert.Equal(agentsResult.Value.Guide.Length, agentsResult.Value.CharacterCount);

        var text = await File.ReadAllTextAsync(outFile);
        Assert.Equal(agentsResult.Value.Guide, text);
        Assert.Contains(@".\mby-pc.bat mby", text);
        Assert.Contains(_fx.WorkDir, text);
        Assert.Contains("Standalone Article", text);
        Assert.Contains("### `mcq`", text);
        Assert.Contains("### `link`", text);
        Assert.Contains("### `markdown`", text);
        Assert.DoesNotContain("### `flashcard`", text);

        // Fix #5: layouts topic and @other group are included in agents-md output
        var layoutsOnly = await handler.HandleAsync(new GetPrimeGuideQuery(
            Topics: ["layouts"],
            Format: "agents-md"));
        Assert.True(layoutsOnly.IsSuccess, layoutsOnly.Error?.Description);
        Assert.NotNull(layoutsOnly.Value.IncludedTopics);
        Assert.Contains("layouts", layoutsOnly.Value.IncludedTopics);
        Assert.Contains("### `layouts`", layoutsOnly.Value.Guide);
        Assert.Contains("cardMaxWidthPx", layoutsOnly.Value.Guide);
        Assert.Contains("themeMode", layoutsOnly.Value.Guide);

        var otherGroup = await handler.HandleAsync(new GetPrimeGuideQuery(
            Topics: ["@other"],
            Format: "agents-md"));
        Assert.True(otherGroup.IsSuccess, otherGroup.Error?.Description);
        Assert.NotNull(otherGroup.Value.IncludedTopics);
        Assert.Contains("layouts", otherGroup.Value.IncludedTopics);
        Assert.Contains("cardMaxWidthPx", otherGroup.Value.Guide);
        Assert.Contains("themeMode", otherGroup.Value.Guide);
    }

    [Fact]
    public async Task Build_AllThemes_And_Signal_EmitFourThemesBridge_And_AtomicSignalFile()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var batch = scope.ServiceProvider.GetRequiredService<BatchOperationsHandler>();

        var specJson = """
        {
          "kind": "session",
          "name": "Theme Signal Study",
          "components": [
            {
              "kind": "article",
              "title": "Intro",
              "blocks": [{ "kind": "text", "text": "Hello Meloberry" }]
            }
          ]
        }
        """;

        var applied = await apply.HandleAsync(new ApplySpecCommand(null, null, specJson, _fx.WorkDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);
        var wsId = applied.Value.TargetId;

        var outWeb = _fx.SubDir("all_themes_web");
        var signalDir = _fx.SubDir("signal_root");
        var built = await build.HandleAsync(new BuildWebAppCommand(
            wsId,
            outWeb,
            IncludeAllThemes: true,
            EmitSignal: true,
            SignalDirectory: signalDir));

        Assert.True(built.IsSuccess, built.Error?.Description);
        var css = await File.ReadAllTextAsync(Path.Combine(outWeb, "styles.css"));
        var js = await File.ReadAllTextAsync(Path.Combine(outWeb, "app.js"));
        Assert.Contains(":root{", css);
        Assert.Contains("#fdf6f9", css);
        Assert.Contains("light", css);
        Assert.Contains("dark", css);
        Assert.Contains("paper", css);
        Assert.Contains("mby-theme-override", js);
        Assert.Contains("mby-theme-applied", js);
        Assert.Contains("127.0.0.1", js);
        Assert.Contains("localhost", js);

        var signalFile = Path.Combine(signalDir, ".mby-signal.txt");
        Assert.True(File.Exists(signalFile));
        Assert.False(File.Exists(signalFile + ".tmp"));
        var signalText = await File.ReadAllTextAsync(signalFile);
        Assert.Contains("version=1", signalText);
        Assert.Contains("event=webapp_built", signalText);
        Assert.Contains($"workspace_id={wsId}", signalText);
        Assert.Contains($"path={Path.GetFullPath(outWeb)}", signalText);
        Assert.Contains("title=Theme Signal Study", signalText);
        Assert.Contains("producer=mby/", signalText);

        var batchWeb = _fx.SubDir("batch_all_themes_web");
        var batchSignalDir = _fx.SubDir("batch_signal_root");
        var batchJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                op = "build",
                addr = wsId,
                @out = batchWeb,
                allThemes = true,
                signal = true,
                signalDir = batchSignalDir
            }
        });
        var batchRes = await batch.HandleAsync(new BatchOperationsCommand(InlineJson: batchJson));
        Assert.True(batchRes.IsSuccess, batchRes.Error?.Description);
        Assert.Contains("mby-theme-override", await File.ReadAllTextAsync(Path.Combine(batchWeb, "app.js")));
        Assert.True(File.Exists(Path.Combine(batchSignalDir, ".mby-signal.txt")));
    }

    [Fact]
    public async Task ListWorkspaces_WebDiscovery_And_AnalyzeIngest_RoundTripReports()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var list = scope.ServiceProvider.GetRequiredService<ListWorkspacesHandler>();
        var analyze = scope.ServiceProvider.GetRequiredService<AnalyzeWebAppHandler>();

        var scanDir = _fx.SubDir("web_scan");
        var webA = Path.Combine(scanDir, "alpha_web");
        var specJson = """
        {
          "kind": "session",
          "name": "Discoverable Session",
          "components": [
            { "kind": "article", "blocks": [{ "kind": "text", "text": "Slide 1" }] }
          ]
        }
        """;
        var builtRes = await apply.HandleAsync(new ApplySpecCommand(null, null, specJson, _fx.WorkDir, BuildOutDir: webA));
        Assert.True(builtRes.IsSuccess, builtRes.Error?.Description);
        var wsId = builtRes.Value.TargetId;

        var webB = Path.Combine(scanDir, "beta_web");
        Directory.CreateDirectory(webB);
        await File.WriteAllTextAsync(Path.Combine(webB, "index.html"), "<html></html>");
        await File.WriteAllTextAsync(Path.Combine(webB, "app.js"), "console.log('ok');");

        // Dot directories and incomplete dirs must be ignored
        var dotWeb = Path.Combine(scanDir, ".hidden_web");
        Directory.CreateDirectory(dotWeb);
        await File.WriteAllTextAsync(Path.Combine(dotWeb, "index.html"), "<html></html>");
        await File.WriteAllTextAsync(Path.Combine(dotWeb, "app.js"), "console.log('hidden');");
        var partialDir = Path.Combine(scanDir, "not_a_webapp");
        Directory.CreateDirectory(partialDir);
        await File.WriteAllTextAsync(Path.Combine(partialDir, "index.html"), "<html></html>");

        var rawReport = $$"""
        {
          "schemaVersion": "1.0",
          "workspaceId": "{{wsId}}",
          "title": "Discoverable Session",
          "startedAtUtc": "2026-11-28T07:00:00Z",
          "completedAtUtc": "2026-11-28T07:05:00Z",
          "totalDurationMs": 12000,
          "summary": {
            "totalSlides": 1,
            "mcqTotal": 0,
            "mcqCorrectFirstTry": 0,
            "mcqCorrectFinal": 0,
            "mcqAccuracyPercent": 0,
            "flashcardTotal": 0,
            "flashcardKnown": 0,
            "flashcardNeedsReview": 0,
            "articleSlidesViewed": 1
          },
          "slides": [],
          "weakItemIds": []
        }
        """;
        var wrappedPayload = $"{{\"report\":{rawReport},\"markdown\":\"# Custom Ingested MD\"}}";

        var ingestWrapped = await analyze.IngestAsync(new IngestAnalysisCommand(webB, InlineJson: wrappedPayload));
        Assert.True(ingestWrapped.IsSuccess, ingestWrapped.Error?.Description);
        Assert.True(File.Exists(ingestWrapped.Value.JsonPath));
        Assert.True(File.Exists(ingestWrapped.Value.MarkdownPath));
        Assert.Equal("# Custom Ingested MD", await File.ReadAllTextAsync(ingestWrapped.Value.MarkdownPath));

        var ingestRaw = await analyze.IngestAsync(new IngestAnalysisCommand(webA, InlineJson: rawReport));
        Assert.True(ingestRaw.IsSuccess, ingestRaw.Error?.Description);

        var loaded = await analyze.HandleAsync(new AnalyzeWebAppQuery(webB));
        Assert.True(loaded.IsSuccess, loaded.Error?.Description);
        Assert.Equal(wsId, loaded.Value.LatestReport.WorkspaceId);

        var badJson = await analyze.IngestAsync(new IngestAnalysisCommand(webB, InlineJson: "{not json"));
        Assert.False(badJson.IsSuccess);
        var missingDir = await analyze.IngestAsync(new IngestAnalysisCommand(Path.Combine(scanDir, "missing_web"), InlineJson: rawReport));
        Assert.False(missingDir.IsSuccess);

        var discovered = await list.DiscoverWebAppsAsync(scanDir);
        Assert.True(discovered.IsSuccess, discovered.Error?.Description);
        Assert.Equal(2, discovered.Value.Count);
        var beta = Assert.Single(discovered.Value, w => w.Name == "beta_web");
        Assert.Equal(1, beta.ReportCount);
        Assert.Equal("2026-11-28T07:05:00Z", beta.LatestCompletedAtUtc);
    }
}
