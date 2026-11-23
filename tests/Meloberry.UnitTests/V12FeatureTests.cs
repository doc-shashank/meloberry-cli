using System.IO.Compression;
using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Common;
using Meloberry.Domain.Sessions;
using Meloberry.Infrastructure;
using Meloberry.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class V12FeatureTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_v12_unit_{Guid.NewGuid():N}");

    public V12FeatureTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
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
    public void Domain_ArticleBlockCaptionAltCallout_And_RefSlugGeneration_WorkCleanly()
    {
        var img = ArticleBlock.CreateImage("ArtComp_1", "Component_1", "assets/fig1.png", caption: "Fig. 2.1 Overview", alt: "Overview diagram");
        Assert.Equal("Fig. 2.1 Overview", img.Caption);
        Assert.Equal("Overview diagram", img.Alt);
        Assert.True(img.SetImageCaption("Fig. 2.1 Revised").IsSuccess);
        Assert.True(img.SetImageAlt("Revised alt").IsSuccess);
        Assert.Equal("Fig. 2.1 Revised", img.Caption);
        Assert.Equal("Revised alt", img.Alt);

        Assert.True(ArticleBlock.TryParseCalloutTone("clinical", out var clinical));
        Assert.Equal(CalloutTone.Clinical, clinical);
        Assert.False(ArticleBlock.TryParseCalloutTone("invalid_tone", out _));

        var calloutRes = ArticleBlock.CreateCallout("ArtComp_2", "Component_1", CalloutTone.Warning, "Never administer IV bolus.", "Boxed Warning");
        Assert.True(calloutRes.IsSuccess);
        Assert.Equal(ArticleBlockKind.Callout, calloutRes.Value.Kind);
        Assert.Equal(CalloutTone.Warning, calloutRes.Value.CalloutTone);
        Assert.Equal("Boxed Warning", calloutRes.Value.CalloutTitle);

        // RefKeyValidator: invalid explicit ref returns length, max 32, pattern & suggested slug
        var invalidRes = RefKeyValidator.ValidateAndNormalize("art-07-clinicopathologic-features-extremely-long-slug");
        Assert.False(invalidRes.IsSuccess);
        Assert.Equal("Address.InvalidRef", invalidRes.Error!.Code);
        Assert.Contains("32", invalidRes.Error.Description);
        Assert.NotNull(invalidRes.Error.Hint);

        // Deterministic auto-slug <= 32 chars + collision dedup
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slug1 = RefKeyValidator.GenerateSlug("Clinicopathologic Features of Cellular Aging and Accumulation", "art", used);
        var slug2 = RefKeyValidator.GenerateSlug("Clinicopathologic Features of Cellular Aging and Accumulation", "art", used);
        Assert.Equal(slug1, RefKeyValidator.SuggestSlug("Clinicopathologic Features of Cellular Aging and Accumulation", "art"));
        Assert.NotEqual(slug1, slug2);
        Assert.InRange(slug1.Length, 1, 32);
        Assert.InRange(slug2.Length, 1, 32);
        Assert.True(RefKeyValidator.ValidateAndNormalize(slug1).IsSuccess);
        Assert.True(RefKeyValidator.ValidateAndNormalize(slug2).IsSuccess);
    }

    [Fact]
    public async Task MarkdownSpecParser_And_DirectoryLoader_SupportFrontmatterImagesCalloutsTablesAndNaturalSort()
    {
        var articlesDir = Path.Combine(_tempRoot, "articles");
        var assetsDir = Path.Combine(articlesDir, "assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "fig_2.1.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        await File.WriteAllTextAsync(Path.Combine(articlesDir, "_session.md"), """
            ---
            kind: session
            name: Chapter 2 Pathology
            description: Cellular injury and death
            ---
            """);

        await File.WriteAllTextAsync(Path.Combine(articlesDir, "2-injury.md"), """
            ---
            title: Cellular Injury Mechanisms
            ref: cell-injury
            ---

            # Cellular Injury

            | Feature | Necrosis | Apoptosis |
            | --- | --- | --- |
            | Energy | Passive | ATP-dependent |

            ![Necrosis electron micrograph](assets/fig_2.1.png "Fig. 2.1 Stages with $E=mc^2$")

            :::clinical Clinical Correlation
            Loss of phospholipid asymmetry exposes **phosphatidylserine**.
            :::

            ```markdown
            ![ignored inside code fence](assets/not_an_image.png)
            :::pearl
            ignored inside code fence
            :::
            ```
            """);

        await File.WriteAllTextAsync(Path.Combine(articlesDir, "10-aging.md"), """
            # Cellular Aging

            Telomere shortening limits replicative lifespan.
            """);

        await File.WriteAllTextAsync(Path.Combine(articlesDir, "11-quiz.json"), """
            {
              "kind": "quiz",
              "quizType": "mcq",
              "title": "Chapter Review",
              "questions": [
                {
                  "type": "mcq",
                  "text": "Which process requires caspases?",
                  "options": [
                    { "text": "Apoptosis", "isCorrect": true },
                    { "text": "Liquefactive necrosis", "isCorrect": false }
                  ]
                }
              ]
            }
            """);

        var loader = new DeclarativeSpecLoader();
        // Trailing slash normalization check
        var loadRes = await loader.LoadAsync(articlesDir + Path.DirectorySeparatorChar);
        Assert.True(loadRes.IsSuccess, loadRes.Error?.Description);
        var spec = loadRes.Value;
        Assert.Equal("session", spec.Kind);
        Assert.Equal("Chapter 2 Pathology", spec.Name);
        Assert.Equal(3, spec.Components!.Count);
        // Natural numeric order: 2-injury.md, 10-aging.md, 11-quiz.json
        Assert.Equal("Cellular Injury Mechanisms", spec.Components[0].Title);
        Assert.Equal("cell-injury", spec.Components[0].Ref);
        Assert.Equal("Cellular Aging", spec.Components[1].Title);
        Assert.Equal("Chapter Review", spec.Components[2].Title);

        var blocks = spec.Components[0].Blocks!;
        Assert.Equal(4, blocks.Count);
        Assert.Equal("text", blocks[0].Kind);
        Assert.Contains("| Feature | Necrosis | Apoptosis |", blocks[0].Text);
        Assert.Equal("image", blocks[1].Kind);
        Assert.Equal("Fig. 2.1 Stages with $E=mc^2$", blocks[1].Caption);
        Assert.Equal("Necrosis electron micrograph", blocks[1].Alt);
        Assert.Equal("callout", blocks[2].Kind);
        Assert.Equal("clinical", blocks[2].Tone);
        Assert.Equal("Clinical Correlation", blocks[2].Title);
        Assert.Equal("text", blocks[3].Kind);
        Assert.Contains("ignored inside code fence", blocks[3].Text);

        // Path traversal escape check
        var badMd = Path.Combine(_tempRoot, "escape.md");
        await File.WriteAllTextAsync(badMd, "![escape](../secret.png)\n");
        using var sp = CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var errRes = await apply.HandleAsync(new ApplySpecCommand(null, badMd, WorkingDirectory: Path.Combine(_tempRoot, "ws_bad")));
        Assert.False(errRes.IsSuccess);
        Assert.Equal("Asset.InvalidPath", errRes.Error!.Code);
    }

    [Fact]
    public async Task Sqlite_Mbypkg_And_WebAppGenerator_RoundTripNewBlockFieldsAndSearchIndex()
    {
        var mdPath = Path.Combine(_tempRoot, "lesson.md");
        var assetsDir = Path.Combine(_tempRoot, "assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "diagram.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        await File.WriteAllTextAsync(mdPath, """
            ---
            title: Necroptosis Pathway
            ---

            # Overview

            Regulated necrotic cell death via RIPK3 and MLKL.

            ![RIPK3 pathway](assets/diagram.png "Fig. 2.5 Necroptosis $\Delta G$")

            :::pearl High-Yield Pearl
            Caspase-8 inhibition switches apoptosis toward **necroptosis**.
            :::
            """);

        using var sp = CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var pkg = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var imp = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();

        var wsDir = Path.Combine(_tempRoot, "ws");
        var applied = await apply.HandleAsync(new ApplySpecCommand(null, mdPath, WorkingDirectory: wsDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);
        var sessionId = applied.Value.TargetId;

        // Verify Sqlite round-trip preserves Caption, Alt, CalloutTone, CalloutTitle, and auto-generated component ref
        var loaded = await repo.FindSessionAsync(sessionId);
        Assert.NotNull(loaded);
        var comp = loaded!.Session.Components[0];
        Assert.Equal("necroptosis-pathway", comp.RefKey);
        Assert.Equal("Fig. 2.5 Necroptosis $\\Delta G$", comp.ArticleBlocks[1].Caption);
        Assert.Equal("RIPK3 pathway", comp.ArticleBlocks[1].Alt);
        Assert.Equal(CalloutTone.Pearl, comp.ArticleBlocks[2].CalloutTone);
        Assert.Equal("High-Yield Pearl", comp.ArticleBlocks[2].CalloutTitle);

        // Verify .mbypkg export + import round-trip + WebApp output HTML/CSS/JS + KaTeX bundling
        var pkgPath = Path.Combine(_tempRoot, "out.mbypkg");
        var expRes = await pkg.HandleAsync(new ExportPackageCommand(sessionId, pkgPath, "android"));
        Assert.True(expRes.IsSuccess, expRes.Error?.Description);

        using (var zip = ZipFile.OpenRead(pkgPath))
        {
            using var reader = new StreamReader(zip.GetEntry("workspace.json")!.Open());
            var wsJson = await reader.ReadToEndAsync();
            Assert.Contains("Fig. 2.5 Necroptosis", wsJson);
            Assert.Contains("High-Yield Pearl", wsJson);
        }

        var importDir = Path.Combine(_tempRoot, "reimported");
        var impRes = await imp.HandleAsync(new ImportPackageCommand(pkgPath, importDir));
        Assert.True(impRes.IsSuccess);

        var webOut = Path.Combine(_tempRoot, "web_out");
        var webRes = await build.HandleAsync(new BuildWebAppCommand(sessionId, webOut));
        Assert.True(webRes.IsSuccess);

        var html = await File.ReadAllTextAsync(webRes.Value.IndexHtmlPath);
        var css = await File.ReadAllTextAsync(Path.Combine(webOut, "styles.css"));
        var js = await File.ReadAllTextAsync(Path.Combine(webOut, "app.js"));
        Assert.Contains("searchModal", html);
        Assert.Contains("katex.min.css", html);
        Assert.True(File.Exists(Path.Combine(webOut, "katex", "katex.min.js")));
        Assert.Contains(".mby-figure", css);
        Assert.Contains(".mby-callout-pearl", css);
        Assert.Contains("SEARCH_INDEX", js);
        Assert.Contains("mby-figcaption", js);
        Assert.Contains("mby-callout", js);
    }
}
