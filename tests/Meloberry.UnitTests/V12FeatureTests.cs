using System.IO.Compression;
using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Common;
using Meloberry.Domain.Questions;
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

    [Fact]
    public async Task StandaloneArticle_LayoutOverridesAndRef_ApplyCleanly_And_RootValidationStillEnforced()
    {
        using var sp = CreateProvider("appdata_layout_ov");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        // 1. Standalone Markdown article with indented layoutOverrides + ref
        var mdPath = Path.Combine(_tempRoot, "standalone_override.md");
        await File.WriteAllTextAsync(mdPath, """
            ---
            title: Styled Article
            ref: styled-art
            layoutOverrides:
              cardMaxWidthPx: 960
              bodyFontSizePx: 18
            ---

            # Styled Article

            Content with layout overrides.
            """);

        var wsDir = Path.Combine(_tempRoot, "ws_md_ov");
        var mdRes = await apply.HandleAsync(new ApplySpecCommand(null, mdPath, WorkingDirectory: wsDir));
        Assert.True(mdRes.IsSuccess, mdRes.Error?.Description);
        var loadedMd = await repo.FindSessionAsync(mdRes.Value.TargetId);
        Assert.NotNull(loadedMd);
        var comp = Assert.Single(loadedMd!.Session.Components);
        Assert.Equal("styled-art", comp.RefKey);
        Assert.Equal("960", comp.LayoutOverrides["cardMaxWidthPx"]);
        Assert.Equal("18", comp.LayoutOverrides["bodyFontSizePx"]);

        // 2. Standalone Markdown article with inline layoutOverrides
        var inlineMd = """
            ---
            title: Inline Override
            layoutOverrides: { cardMaxWidthPx: 880 }
            ---

            Hello inline override.
            """;
        var inlineRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: inlineMd, WorkingDirectory: wsDir));
        Assert.True(inlineRes.IsSuccess, inlineRes.Error?.Description);
        var loadedInline = await repo.FindSessionAsync(inlineRes.Value.TargetId);
        Assert.Equal("880", loadedInline!.Session.Components[0].LayoutOverrides["cardMaxWidthPx"]);

        // 3. Malformed frontmatter layoutOverrides and order fail explicitly
        var badFmOv = "---\nlayoutOverrides: invalid_scalar\n---\nBody\n";
        var badFmOvRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badFmOv, WorkingDirectory: wsDir));
        Assert.False(badFmOvRes.IsSuccess);
        Assert.Equal("Markdown.InvalidFrontmatter", badFmOvRes.Error!.Code);

        var badFmOrder = "---\norder: not-a-list\n---\nBody\n";
        var badFmOrderRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badFmOrder, WorkingDirectory: wsDir));
        Assert.False(badFmOrderRes.IsSuccess);
        Assert.Equal("Markdown.InvalidFrontmatter", badFmOrderRes.Error!.Code);

        // 4. Standalone article JSON with layoutOverrides + ref succeeds
        var artJson = """
            {
              "kind": "article",
              "title": "JSON Article",
              "ref": "json-art",
              "layoutOverrides": { "cardMaxWidthPx": "900" },
              "blocks": [{ "kind": "text", "text": "Hello JSON article." }]
            }
            """;
        var artJsonRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: artJson, WorkingDirectory: wsDir));
        Assert.True(artJsonRes.IsSuccess, artJsonRes.Error?.Description);
        var loadedArtJson = await repo.FindSessionAsync(artJsonRes.Value.TargetId);
        Assert.Equal("json-art", loadedArtJson!.Session.Components[0].RefKey);
        Assert.Equal("900", loadedArtJson.Session.Components[0].LayoutOverrides["cardMaxWidthPx"]);

        // 5. Session root and standalone quiz root still reject top-level layoutOverrides
        var badSessionJson = """
            {
              "kind": "session",
              "layoutOverrides": { "cardMaxWidthPx": "900" },
              "components": [{ "kind": "article", "blocks": [{ "kind": "text", "text": "Hi" }] }]
            }
            """;
        var badSessionRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badSessionJson, WorkingDirectory: wsDir));
        Assert.False(badSessionRes.IsSuccess);
        Assert.Equal("Json.InvalidFieldForType", badSessionRes.Error!.Code);

        var badQuizJson = """
            {
              "kind": "quiz",
              "layoutOverrides": { "optionColumns": "2" },
              "questions": [{ "type": "flashcard", "front": { "text": "Q" }, "back": { "text": "A" } }]
            }
            """;
        var badQuizRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badQuizJson, WorkingDirectory: wsDir));
        Assert.False(badQuizRes.IsSuccess);
        Assert.Equal("Json.InvalidFieldForType", badQuizRes.Error!.Code);

        // 6. Article JSON mixing components[] is rejected instead of silently overwritten
        var mixedArtJson = """
            {
              "kind": "article",
              "blocks": [{ "kind": "text", "text": "Hi" }],
              "components": [{ "kind": "article", "blocks": [{ "kind": "text", "text": "Other" }] }]
            }
            """;
        var mixedArtRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: mixedArtJson, WorkingDirectory: wsDir));
        Assert.False(mixedArtRes.IsSuccess);
        Assert.Equal("Json.InvalidFieldForType", mixedArtRes.Error!.Code);

        // 7. _session.md rejecting layoutOverrides, and directory component JSON preserving ref
        var dirWithBadManifest = Path.Combine(_tempRoot, "dir_bad_manifest");
        Directory.CreateDirectory(dirWithBadManifest);
        await File.WriteAllTextAsync(Path.Combine(dirWithBadManifest, "_session.md"), """
            ---
            kind: session
            name: Bad Manifest
            layoutOverrides:
              cardMaxWidthPx: 900
            ---
            """);
        await File.WriteAllTextAsync(Path.Combine(dirWithBadManifest, "01-note.md"), "# Note\nHello");
        var badManifestRes = await apply.HandleAsync(new ApplySpecCommand(null, dirWithBadManifest, WorkingDirectory: wsDir));
        Assert.False(badManifestRes.IsSuccess);
        Assert.Equal("Json.InvalidFieldForType", badManifestRes.Error!.Code);

        var dirWithRefJson = Path.Combine(_tempRoot, "dir_ref_json");
        Directory.CreateDirectory(dirWithRefJson);
        await File.WriteAllTextAsync(Path.Combine(dirWithRefJson, "01-quiz.json"), """
            {
              "kind": "quiz",
              "ref": "dir-quiz-ref",
              "title": "Dir Quiz",
              "questions": [{ "type": "flashcard", "front": { "text": "Q" }, "back": { "text": "A" } }]
            }
            """);
        var dirRefRes = await apply.HandleAsync(new ApplySpecCommand(null, dirWithRefJson, WorkingDirectory: wsDir));
        Assert.True(dirRefRes.IsSuccess, dirRefRes.Error?.Description);
        var loadedDirRef = await repo.FindSessionAsync(dirRefRes.Value.TargetId);
        Assert.Equal("dir-quiz-ref", loadedDirRef!.Session.Components[0].RefKey);
    }

    [Fact]
    public async Task CdpHeadlessVerifier_PassesCleanBundledQuizAndCatchesQ2Overflow()
    {
        using var sp = CreateProvider("appdata_cdp_verify");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var verifier = scope.ServiceProvider.GetRequiredService<IHeadlessWebAppVerifier>();

        var wsDir = Path.Combine(_tempRoot, "ws_cdp");
        var webDir = Path.Combine(_tempRoot, "web_cdp");
        var specJson = """
            {
              "kind": "session",
              "name": "CDP Quiz Scope Check",
              "components": [
                {
                  "kind": "article",
                  "title": "Overview",
                  "blocks": [
                    {
                      "kind": "text",
                      "text": "# Overview\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\nNormal paragraph with $x^2 + y^2 = z^2$."
                    }
                  ]
                },
                {
                  "kind": "quiz",
                  "title": "Mixed Quiz",
                  "quizType": "mixed",
                  "questions": [
                    {
                      "type": "mcq",
                      "text": "Question 1 prompt?",
                      "explanation": "Q1 explanation text.",
                      "options": [
                        { "text": "Correct 1", "isCorrect": true },
                        { "text": "Wrong 1", "isCorrect": false }
                      ]
                    },
                    {
                      "type": "mcq",
                      "text": "Question 2 prompt?",
                      "explanation": "Q2 explanation text.",
                      "options": [
                        { "text": "Correct 2", "isCorrect": true },
                        { "text": "Wrong 2", "isCorrect": false }
                      ]
                    },
                    {
                      "type": "flashcard",
                      "front": { "text": "Front term" },
                      "back": { "text": "Back definition" }
                    }
                  ]
                }
              ]
            }
            """;

        var applied = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: specJson, WorkingDirectory: wsDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);
        var built = await build.HandleAsync(new BuildWebAppCommand(applied.Value.TargetId, webDir));
        Assert.True(built.IsSuccess, built.Error?.Description);

        var cleanRes = await verifier.VerifyAsync(webDir, staticOnly: false, timeoutSeconds: 30);
        if (!cleanRes.IsSuccess && cleanRes.Error!.Code == "Verify.BrowserNotFound")
            return; // Browser not installed in test environment

        Assert.True(cleanRes.IsSuccess, cleanRes.Error?.Description);
        Assert.True(cleanRes.Value.Passed, $"Expected clean pass, got console=[{string.Join("; ", cleanRes.Value.ConsoleErrors)}], overflow=[{string.Join("; ", cleanRes.Value.OverflowIssues)}], broken=[{string.Join("; ", cleanRes.Value.BrokenAssets)}], katex=[{string.Join("; ", cleanRes.Value.KatexErrors)}]");
        Assert.Equal(2, cleanRes.Value.SlidesChecked);

        // Inject an unbreakable overflowing string specifically into Q2 (leaving Q1 clean)
        var appJsPath = Path.Combine(webDir, "app.js");
        var appJs = await File.ReadAllTextAsync(appJsPath);
        var longToken = new string('W', 180);
        var mutatedJs = appJs.Replace("Question 2 prompt?", longToken, StringComparison.Ordinal);
        Assert.NotEqual(appJs, mutatedJs);
        await File.WriteAllTextAsync(appJsPath, mutatedJs);

        var overflowRes = await verifier.VerifyAsync(webDir, staticOnly: false, timeoutSeconds: 30);
        Assert.True(overflowRes.IsSuccess, overflowRes.Error?.Description);
        Assert.False(overflowRes.Value.Passed);
        Assert.Contains(overflowRes.Value.OverflowIssues, msg => msg.Contains("Q2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RegressionSuite_OrderInSessionJson_MarkdownEdgeCases_PatchCoercion_McqRules_And_MbypkgLayoutBounds()
    {
        using var sp = CreateProvider("appdata_reg_all");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var resolver = scope.ServiceProvider.GetRequiredService<EntityAddressResolver>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var batch = scope.ServiceProvider.GetRequiredService<BatchOperationsHandler>();
        var importer = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();

        var wsDir = Path.Combine(_tempRoot, "ws_reg_all");
        Directory.CreateDirectory(wsDir);

        // 1. Design Choice A + Bug 8: session.json supports 'order', rejects _session.md + session.json conflict
        var orderedDir = Path.Combine(_tempRoot, "ordered_dir");
        var spacedAssets = Path.Combine(orderedDir, "assets");
        Directory.CreateDirectory(spacedAssets);
        await File.WriteAllBytesAsync(Path.Combine(spacedAssets, "fig 1 (a).png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        await File.WriteAllTextAsync(Path.Combine(orderedDir, "session.json"), """
            {
              "kind": "session",
              "name": "Ordered Session",
              "order": ["second.md", "first.md"]
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(orderedDir, "first.md"), "Plain intro paragraph without heading.\n\n```bash\n# Not a title\n```\n");
        await File.WriteAllTextAsync(Path.Combine(orderedDir, "second.md"), "# Second Chapter\n\n![Spaced](assets/fig 1 (a).png \"Fig 1\")\n");

        var orderedRes = await apply.HandleAsync(new ApplySpecCommand(null, orderedDir, WorkingDirectory: wsDir));
        Assert.True(orderedRes.IsSuccess, orderedRes.Error?.Description);
        var loadedOrdered = (await repo.FindSessionAsync(orderedRes.Value.TargetId))!.Session;
        Assert.Equal("Second Chapter", loadedOrdered.Components[0].Title);
        Assert.Equal("First", loadedOrdered.Components[1].Title); // # Not a title inside code fence was ignored!
        Assert.Equal("assets/fig 1 (a)_" + loadedOrdered.Components[0].ArticleBlocks[1].ImagePath!.Split('_')[^1], loadedOrdered.Components[0].ArticleBlocks[1].ImagePath);

        // Both _session.md and session.json fails explicitly
        await File.WriteAllTextAsync(Path.Combine(orderedDir, "_session.md"), "---\nname: Conflict\n---\n");
        var conflictDirRes = await apply.HandleAsync(new ApplySpecCommand(null, orderedDir, WorkingDirectory: wsDir));
        Assert.False(conflictDirRes.IsSuccess);
        Assert.Equal("Json.InvalidSpec", conflictDirRes.Error!.Code);

        // 2. Design Choice E.3 + Bug 11: Markdown empty body rejected, unknown :::tip rejected, plain paragraph stdin accepted
        var emptyMdRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: "---\ntitle: Empty\n---\n   \n", WorkingDirectory: wsDir));
        Assert.False(emptyMdRes.IsSuccess);
        Assert.Equal("Markdown.EmptyBody", emptyMdRes.Error!.Code);

        var unknownFenceRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: ":::tip\nHi\n:::\n", WorkingDirectory: wsDir));
        Assert.False(unknownFenceRes.IsSuccess);
        Assert.Equal("Markdown.UnknownFence", unknownFenceRes.Error!.Code);

        var plainParaRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: "Just a plain markdown paragraph.", WorkingDirectory: wsDir));
        Assert.True(plainParaRes.IsSuccess, plainParaRes.Error?.Description);

        // 3. Design Choice B + Bug 1 & 2: schema-aware --set coercion, case-insensitive merge, safe invalid JSON types
        var quizSpec = """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "name": "Patch Quiz",
              "questions": [
                {
                  "type": "mcq",
                  "ref": "q-mcq",
                  "text": "Original?",
                  "isMultiCorrect": true,
                  "maxOptions": 4,
                  "options": [
                    { "index": 1, "text": "A", "isCorrect": true },
                    { "index": 2, "text": "B", "isCorrect": true },
                    { "index": 3, "text": "C", "isCorrect": false },
                    { "index": 4, "text": "D", "isCorrect": false }
                  ]
                },
                {
                  "type": "flashcard",
                  "ref": "q-fc",
                  "front": { "text": "Front" },
                  "back": { "text": "Back" }
                }
              ]
            }
            """;
        var qRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: quizSpec, WorkingDirectory: wsDir));
        Assert.True(qRes.IsSuccess, qRes.Error?.Description);
        var qWsId = qRes.Value.TargetId;

        // Setting numeric/boolean strings via --set succeeds instead of Patch.InvalidValue
        var fcSetRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{qWsId}#q-fc",
            InlineJson: """{"Front":{"Text":"OldFront"}}""",
            SetPairs: ["front.text=true", "back.text=42", "front.position=aboveImage"]));
        Assert.True(fcSetRes.IsSuccess, fcSetRes.Error?.Description);
        var fcAfter = (FlashcardQuestion)(await resolver.ResolveAsync($"{qWsId}#q-fc")).Value.Question!;
        Assert.Equal("true", fcAfter.Front.Text);
        Assert.Equal("42", fcAfter.Back.Text);
        Assert.Equal(MediaPosition.TextAboveImage, fcAfter.Front.Position);

        // Non-string flashcard position & array maxOptions return Patch.InvalidValue (no InvalidOperationException crash)
        var badPosRes = await patch.HandleAsync(new PatchEntityCommand($"{qWsId}#q-fc", InlineJson: """{"front":{"position":42}}"""));
        Assert.False(badPosRes.IsSuccess);
        Assert.Equal("Patch.InvalidValue", badPosRes.Error!.Code);

        var badMaxRes = await patch.HandleAsync(new PatchEntityCommand($"{qWsId}#q-mcq", InlineJson: """{"maxOptions":[]}"""));
        Assert.False(badMaxRes.IsSuccess);
        Assert.Equal("Patch.InvalidValue", badMaxRes.Error!.Code);

        // 4. Design Choice E.1 + Bugs 4, 5, 6: combined MCQ shrink + single-select + options works; duplicate index & multi-correct in single rejected; quizType mismatch rejected
        var combinedMcqRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{qWsId}#q-mcq",
            InlineJson: """{"maxOptions":2,"isMultiCorrect":false,"options":[{"text":"Yes","isCorrect":true},{"text":"No","isCorrect":false}]}"""));
        Assert.True(combinedMcqRes.IsSuccess, combinedMcqRes.Error?.Description);
        var mcqAfter = (McqQuestion)(await resolver.ResolveAsync($"{qWsId}#q-mcq")).Value.Question!;
        Assert.Equal(2, mcqAfter.MaxOptions);
        Assert.False(mcqAfter.IsMultiCorrect);
        Assert.Equal(2, mcqAfter.Options.Count);

        var dupIdxRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{qWsId}#q-mcq",
            InlineJson: """{"options":[{"index":2,"text":"A","isCorrect":true},{"text":"B","isCorrect":false}]}"""));
        Assert.False(dupIdxRes.IsSuccess);
        Assert.Equal("Json.DuplicateOptionIndex", dupIdxRes.Error!.Code);

        var multiInSingleRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{qWsId}#q-mcq",
            InlineJson: """{"options":[{"text":"A","isCorrect":true},{"text":"B","isCorrect":true}]}"""));
        Assert.False(multiInSingleRes.IsSuccess);
        Assert.Equal("Question.MultipleCorrectInSingle", multiInSingleRes.Error!.Code);

        var mismatchQuizRes = await apply.HandleAsync(new ApplySpecCommand(
            null, null,
            InlineJson: """{"kind":"quiz","quizType":"mcq","questions":[{"type":"flashcard","front":{"text":"F"},"back":{"text":"B"}}]}""",
            WorkingDirectory: wsDir));
        Assert.False(mismatchQuizRes.IsSuccess);
        Assert.Equal("Quiz.QuestionTypeMismatch", mismatchQuizRes.Error!.Code);

        // 5. Bug 9: Reserved positional slugs (c1, q1) rejected by RefKeyValidator and auto-slug avoids them
        Assert.False(RefKeyValidator.ValidateAndNormalize("c1").IsSuccess);
        Assert.False(RefKeyValidator.ValidateAndNormalize("q12").IsSuccess);
        Assert.Equal("art-c1", RefKeyValidator.SuggestSlug("C1", "art"));

        // 6. Bug 7: Unvalidated global layouts.json in .mbypkg is rejected
        var badLayoutPkg = Path.Combine(_tempRoot, "bad_layouts.mbypkg");
        using (var za = ZipFile.Open(badLayoutPkg, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(za.CreateEntry("manifest.json").Open()))
                await w.WriteAsync("""{"packageVersion":"1.0","kind":"quiz","id":"Set_20261120_120000_001","exportedAtUtc":"2026-01-01T00:00:00Z","assets":[]}""");
            using (var w = new StreamWriter(za.CreateEntry("workspace.json").Open()))
                await w.WriteAsync("""{"id":"Set_20261120_120000_001","quizType":"Mcq","questions":[]}""");
            using (var w = new StreamWriter(za.CreateEntry("layouts.json").Open()))
                await w.WriteAsync("""{"mcq":{"cardMaxWidthPx":"999999"}}""");
        }
        var badLayoutRes = await importer.HandleAsync(new ImportPackageCommand(badLayoutPkg, wsDir));
        Assert.False(badLayoutRes.IsSuccess);
        Assert.Equal("Layout.OutOfBounds", badLayoutRes.Error!.Code);

        // 7. Design Choice E.4 + Bug 3 & 13: batch rolls back newly created build/pkg outputs, rejects non-string set items, and $prev.id after rm points to workspace
        var batchBuildOut = Path.Combine(_tempRoot, "batch_rolled_back_web");
        var batchPkgOut = Path.Combine(_tempRoot, "batch_rolled_back.mbypkg");
        var rollbackBatchJson = $$"""
            [
              { "op": "apply", "out": "{{wsDir.Replace("\\", "\\\\")}}", "spec": { "kind": "quiz", "quizType": "mcq", "questions": [{ "type": "mcq", "text": "Q?", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] }] } },
              { "op": "build", "addr": "$0.id", "out": "{{batchBuildOut.Replace("\\", "\\\\")}}" },
              { "op": "pkg", "addr": "$0.id", "platform": "desktop", "out": "{{batchPkgOut.Replace("\\", "\\\\")}}" },
              { "op": "patch", "addr": "$0.id", "set": [123] }
            ]
            """;
        var rollbackBatchRes = await batch.HandleAsync(new BatchOperationsCommand(InlineJson: rollbackBatchJson));
        Assert.False(rollbackBatchRes.IsSuccess);
        Assert.Equal("Batch.InvalidOp", rollbackBatchRes.Error!.Code);
        Assert.False(Directory.Exists(batchBuildOut));
        Assert.False(File.Exists(batchPkgOut));

        var rmPrevBatchJson = $$"""
            [
              { "op": "apply", "out": "{{wsDir.Replace("\\", "\\\\")}}", "spec": { "kind": "quiz", "quizType": "mcq", "questions": [
                { "type": "mcq", "text": "Q1?", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] },
                { "type": "mcq", "text": "Q2?", "options": [{ "text": "C", "isCorrect": true }, { "text": "D" }] }
              ] } },
              { "op": "rm", "addr": "$0.id#q2" },
              { "op": "check", "addr": "$prev.id" }
            ]
            """;
        var rmPrevRes = await batch.HandleAsync(new BatchOperationsCommand(InlineJson: rmPrevBatchJson));
        Assert.True(rmPrevRes.IsSuccess, rmPrevRes.Error?.Description);

        // 8. Bug 10: failed append cleans up newly copied image asset
        var extraPng = Path.Combine(_tempRoot, "orphan_check.png");
        await File.WriteAllBytesAsync(extraPng, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xAA, 0xBB]);
        var failAppendJsonPath = Path.Combine(_tempRoot, "fail_append.json");
        await File.WriteAllTextAsync(failAppendJsonPath, """
            {
              "questions": [
                { "type": "mcq", "text": "Good Q", "imagePath": "orphan_check.png", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] },
                { "type": "mcq", "ref": "q-mcq", "text": "Dup Ref", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] }
              ]
            }
            """);
        var failAppendRes = await apply.HandleAsync(new ApplySpecCommand(qWsId, failAppendJsonPath));
        Assert.False(failAppendRes.IsSuccess);
        Assert.Equal("Address.DuplicateRef", failAppendRes.Error!.Code);
        var assetsPath = Path.Combine(qRes.Value.StorageDirectory, "assets");
        Assert.True(!Directory.Exists(assetsPath) || Directory.GetFiles(assetsPath, "orphan_check_*").Length == 0);
    }
}
