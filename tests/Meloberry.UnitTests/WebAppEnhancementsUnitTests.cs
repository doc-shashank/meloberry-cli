using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Sessions;
using Meloberry.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class WebAppEnhancementsUnitTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_ui_enh_unit_{Guid.NewGuid():N}");

    public WebAppEnhancementsUnitTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private ServiceProvider CreateProvider(string subDir = "appdata")
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(_tempRoot, subDir));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AssemblyCliToolMetadataProvider_ReadsManifestAppNameVersionAndRepositoryUrl()
    {
        using var sp = CreateProvider();
        var provider = sp.GetRequiredService<ICliToolMetadataProvider>();
        var meta = provider.GetMetadata();

        Assert.Equal("Meloberry", meta.AppName);
        Assert.Equal("2.0.0", meta.Version);
        Assert.Equal("https://github.com/doc-shashank/meloberry-cli/", meta.RepositoryUrl);
    }

    [Fact]
    public void Payloads_BackwardCompatibility_MissingPlaceholderAndImagePathDefaultToNull()
    {
        const string legacyFillJson = """{"template":"Enzyme is {{PFK-1}}.","blanks":[{"index":1,"answers":["PFK-1"]}],"fuzzy":true,"caseSensitive":false}""";
        Assert.True(Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(legacyFillJson, out var fb));
        Assert.NotNull(fb);
        Assert.Null(fb!.Placeholder);

        const string legacySpotJson = """{"template":"[A] and [~~B~~ -> C].","segments":[{"spanIndex":1,"text":"A","isCandidate":true,"isError":false},{"spanIndex":null,"text":" and ","isCandidate":false,"isError":false},{"spanIndex":2,"text":"B","isCandidate":true,"isError":true,"corrections":["C"]}],"requireFix":false,"allowNoError":false,"noError":false}""";
        Assert.True(Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(legacySpotJson, out var se));
        Assert.NotNull(se);
        Assert.Null(se!.ImagePath);
    }

    [Fact]
    public void ArticleLinkRules_ParsesMbyUris_ExtractsOutsideCode_AndResolvesUrls()
    {
        Assert.True(ArticleLinkRules.TryParseMbyUri("mby://card/Loop-Card", out var card));
        Assert.Equal("card", card.Kind);
        Assert.Equal("loop-card", card.TargetId);

        Assert.True(ArticleLinkRules.TryParseMbyUri("mby://article/renal-pharm", out var art));
        Assert.Equal("article", art.Kind);
        Assert.Equal("renal-pharm", art.TargetId);

        Assert.True(ArticleLinkRules.TryParseMbyUri("mby://session/Session_20260401_120000/ecg-basics", out var sess));
        Assert.Equal("session", sess.Kind);
        Assert.Equal("Session_20260401_120000", sess.TargetId);
        Assert.Equal("ecg-basics", sess.SubTargetId);
        var resolvedHref = ArticleLinkRules.ResolveSessionHref(sess);
        Assert.Equal("../Session_20260401_120000_web/index.html#article=ecg-basics", resolvedHref);
        Assert.Equal("../Session_20260401_120000_web", ArticleLinkRules.ResolveSessionPreviewDir(resolvedHref));

        Assert.True(ArticleLinkRules.TryParseMbyUri("mby://session/Session_123/intro?href=../custom/study.html", out var customSess));
        Assert.Equal("../custom/study.html#article=intro", ArticleLinkRules.ResolveSessionHref(customSess));
        Assert.Equal("../custom", ArticleLinkRules.ResolveSessionPreviewDir("../custom/study.html#article=intro"));

        Assert.False(ArticleLinkRules.TryParseMbyUri("mby://session/Session_123?href=javascript:alert(1)", out _));
        Assert.False(ArticleLinkRules.TryParseMbyUri("mby://unknown/abc", out _));

        var md = """
            Inline `[ignored](mby://card/in-code)` should be skipped.

            ```markdown
            [also-ignored](mby://article/in-fence)
            ```

            Real [Card](mby://card/valid-card) and [Session](mby://session/Session_ABC).
            """;
        var links = ArticleLinkRules.ExtractMbyLinksFromMarkdown(md);
        Assert.Equal(2, links.Count);
        Assert.Equal("valid-card", links[0].TargetId);
        Assert.Equal("Session_ABC", links[1].TargetId);
    }

    [Fact]
    public async Task LintHandler_ValidatesCardArticleAndFutureSessionLinks()
    {
        using var sp = CreateProvider("appdata_lint");
        using var scope = sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();

        var wsDir = Path.Combine(_tempRoot, "ws_links");
        Directory.CreateDirectory(Path.Combine(wsDir, "assets"));

        var sessionRes = StudySession.Create("Session_20261124_200000", wsDir);
        Assert.True(sessionRes.IsSuccess);
        var session = sessionRes.Value;
        var comp = session.AddComponent("Component_20261124_200001", "article").Value;
        comp.SetRefKey("main-art");
        comp.AddTextBlock("ArtComp_20261124_200002",
            "Valid [Card](mby://card/c1), missing [BadCard](mby://card/missing), bad [Art](mby://article/nope), and future [Sess](mby://session/Session_Future_001).");
        Assert.True(comp.AddOrReplaceLinkCard(new ArticleLinkCard("c1", "Title", "Card text")).IsSuccess);
        await repo.CreateSessionAsync(session);

        var reportRes = await lint.HandleAsync(new LintCommand(session.Id));
        Assert.True(reportRes.IsSuccess);
        var report = reportRes.Value;
        Assert.False(report.IsClean);
        Assert.Contains(report.Issues, i => i.Severity == "Error" && i.Code == "Lint.MissingLinkCard");
        Assert.Contains(report.Issues, i => i.Severity == "Error" && i.Code == "Lint.UnresolvedArticleLink");
        Assert.Contains(report.Issues, i => i.Severity == "Warning" && i.Code == "Lint.UnresolvedSessionLink");
    }
}
