using System.IO.Compression;
using System.Text.Json;
using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Layouts;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class LintAndExportGuardTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_lint_unit_{Guid.NewGuid():N}");

    public LintAndExportGuardTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task ExportAndWebBuild_CannotBypassLint_AndSucceedOnceValid()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(_tempRoot, "appdata"));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var exportPkg = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var importPkg = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var buildWeb = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();

        var quizDir = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(quizDir);
        var created = (await apply.HandleAsync(new ApplySpecCommand(
            null,
            null,
            """{"kind":"quiz","quizType":"mcq","questions":[{"type":"mcq","text":"temp","options":[{"text":"3"},{"text":"4"}]}]}""",
            quizDir))).Value;

        // Break MCQ to empty text -> Lint fails -> Export & BuildWeb are blocked
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var quizOwner = (await repo.FindQuizContainerAsync(created.TargetId))!;
        var qId = quizOwner.Quiz.Questions[0].Id;

        var emptyMcq = (await apply.HandleAsync(new ApplySpecCommand(
            null,
            null,
            """{"kind":"quiz","quizType":"mcq","questions":[{"type":"mcq"}]}""",
            quizDir))).Value;

        var badLint = (await lint.HandleAsync(new LintCommand(emptyMcq.TargetId))).Value;
        Assert.False(badLint.IsClean);
        Assert.True(badLint.ErrorCount >= 3);

        var blockedExport = await exportPkg.HandleAsync(new ExportPackageCommand(emptyMcq.TargetId));
        Assert.False(blockedExport.IsSuccess);
        Assert.Equal("Lint.Failed", blockedExport.Error!.Code);

        var blockedWeb = await buildWeb.HandleAsync(new BuildWebAppCommand(emptyMcq.TargetId));
        Assert.False(blockedWeb.IsSuccess);
        Assert.Equal("Lint.Failed", blockedWeb.Error!.Code);

        // Fix prompt & mark correct via patch -> Lint passes (with UnfilledOptionSlot warning)
        Assert.True((await patch.HandleAsync(new PatchEntityCommand(
            $"{created.TargetId}#q1",
            InlineJson: """{"text":"2 + 2 = ?","options":[{"text":"3","isCorrect":false},{"text":"4","isCorrect":true}]}"""))).IsSuccess);

        var goodLint = (await lint.HandleAsync(new LintCommand(created.TargetId))).Value;
        Assert.True(goodLint.IsClean);
        Assert.Equal(0, goodLint.ErrorCount);
        Assert.Equal(1, goodLint.WarningCount); // 2 of 4 slots filled

        // Now export & buildweb succeed
        var pkgPath = Path.Combine(_tempRoot, "out.mbypkg");
        var exportOk = await exportPkg.HandleAsync(new ExportPackageCommand(created.TargetId, pkgPath));
        Assert.True(exportOk.IsSuccess);
        Assert.True(File.Exists(exportOk.Value.PackagePath));

        var webOut = Path.Combine(_tempRoot, "web_out");
        var webOk = await buildWeb.HandleAsync(new BuildWebAppCommand(created.TargetId, webOut));
        Assert.True(webOk.IsSuccess);
        Assert.True(File.Exists(webOk.Value.IndexHtmlPath));
        var js = await File.ReadAllTextAsync(Path.Combine(webOut, "app.js"));
        Assert.Contains("const DATA = ", js);
        var jsonStart = js.IndexOf('{');
        var trackMarker = js.IndexOf("const track", StringComparison.Ordinal);
        Assert.True(trackMarker > jsonStart);
        var jsonSlice = js[jsonStart..trackMarker].Trim().TrimEnd(';');
        var dataPayload = JsonDocument.Parse(jsonSlice);
        var promptHtml = dataPayload.RootElement.GetProperty("slides")[0].GetProperty("mcq").GetProperty("promptHtml").GetString();
        Assert.Contains("2 + 2 = ?", promptHtml);

        // Verify telemetry & dual analysis hooks exist in generated WebApp
        Assert.Contains("buildAnalysisReport", js);
        Assert.Contains("buildAnalysisMarkdown", js);
        Assert.Contains("/api/analysis", js);
        Assert.Contains("needsReview", js);
        Assert.Contains("MAGNIFIER_ICON_SVG", js);
        Assert.Contains("isAtFinalSaveStep", js);
        Assert.Contains("'Previous '", js);
        Assert.Contains("scrollDownBtn", js);
        Assert.Contains("scrollBy", js);
        Assert.Contains("scrollSlideToTop", js);
        Assert.Contains("measureFaceNaturalHeight", js);
        var html = await File.ReadAllTextAsync(webOk.Value.IndexHtmlPath);
        Assert.Contains("finishBtn", html);
        Assert.Contains("prevCompMenu", html);
        Assert.Contains("prevCompMenuItem", html);
        Assert.Contains("scrollDownBtn", html);
        Assert.Contains("<meta name=\"generator\" content=\"Meloberry CLI (mby) 1.3.0\"", html);
        Assert.Contains("id=\"greetingGeneratorFooter\"", html);

        var genEl = dataPayload.RootElement.GetProperty("generator");
        Assert.Equal("Meloberry CLI", genEl.GetProperty("toolName").GetString());
        Assert.Equal("mby", genEl.GetProperty("executable").GetString());
        Assert.Equal("1.3.0", genEl.GetProperty("version").GetString());

        Assert.Contains("--option-columns", js);
        Assert.Contains("bindLongPress", js);
        Assert.Contains("populateGeneratorFooter", js);
        Assert.DoesNotContain("--flip-ms", js);
        Assert.DoesNotContain("inner.classList.toggle('flipped'", js);

        var css = await File.ReadAllTextAsync(Path.Combine(webOut, "styles.css"));
        Assert.Contains(".mby-quiz-track", css);
        Assert.Contains(".mby-q-pill", css);
        Assert.Contains("border-radius: 10px;", css);
        Assert.Contains("stroke: #ffffff;", css);
        Assert.Contains("code {", css);
        Assert.Contains("pre {", css);
        Assert.Contains("box-shadow: none;", css);
        Assert.Contains("align-self: flex-start;", css);
        Assert.Contains(".mby-flashcard-scene.is-flipping", css);
        Assert.Contains(".mby-scroll-down-btn", css);
        Assert.Contains(".mby-generator-footer", css);
        Assert.Contains("repeat(var(--option-columns, 1)", css);

        // Verify IAnalysisReportStore + AnalyzeWebAppHandler round-trip
        var reportStore = scope.ServiceProvider.GetRequiredService<IAnalysisReportStore>();
        var analyzeHandler = scope.ServiceProvider.GetRequiredService<AnalyzeWebAppHandler>();
        var sampleJson = $$"""
        {
          "schemaVersion": "1.0",
          "workspaceId": "{{created.TargetId}}",
          "title": "Meloberry Quiz",
          "startedAtUtc": "2026-11-19T15:00:00.000Z",
          "completedAtUtc": "2026-11-19T15:01:30.000Z",
          "totalDurationMs": 90000,
          "summary": {
            "totalSlides": 1,
            "mcqTotal": 1,
            "mcqCorrectFirstTry": 0,
            "mcqCorrectFinal": 1,
            "mcqAccuracyPercent": 100.0,
            "flashcardTotal": 0,
            "flashcardKnown": 0,
            "flashcardNeedsReview": 0,
            "articleSlidesViewed": 0
          },
          "slides": [
            {
              "slideIndex": 1,
              "id": "{{qId}}",
              "kind": "mcq",
              "timeSpentMs": 12500,
              "visitedCount": 1,
              "checkAttempts": 2,
              "selectedOptionIndices": [2],
              "correctOptionIndices": [2],
              "firstTryCorrect": false,
              "isCorrect": true,
              "flipCount": null,
              "flashcardRating": null
            }
          ],
          "weakItemIds": []
        }
        """;
        var savedRes = await reportStore.SaveReportToWebAppDirectoryAsync(webOut, sampleJson);
        Assert.True(savedRes.IsSuccess);
        Assert.True(File.Exists(savedRes.Value.JsonPath));
        Assert.True(File.Exists(savedRes.Value.MarkdownPath));

        var analyzed = await analyzeHandler.HandleAsync(new AnalyzeWebAppQuery(webOut));
        Assert.True(analyzed.IsSuccess);
        Assert.Equal(1, analyzed.Value.TotalRunsFound);
        Assert.Contains(qId, analyzed.Value.MarkdownSummary);

        // Import .mbypkg into a fresh folder
        var importDest = Path.Combine(_tempRoot, "imported");
        var imported = await importPkg.HandleAsync(new ImportPackageCommand(pkgPath, importDest));
        Assert.True(imported.IsSuccess);
        Assert.Equal(created.TargetId, imported.Value.ImportedId);
        Assert.True(File.Exists(Path.Combine(importDest, created.TargetId, $"{created.TargetId}.db")));
    }

    [Fact]
    public void UnifiedToolMetadata_AndComponentLayoutEditorBuilder_MatchVersion100AndSharedStyles()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(_tempRoot, "appdata_meta"));
        using var provider = services.BuildServiceProvider();

        var domainMeta = new AssemblyCliToolMetadataProvider(typeof(Meloberry.Domain.Common.Result).Assembly, "mby").GetMetadata();
        var appMeta = new AssemblyCliToolMetadataProvider(typeof(Meloberry.Application.DependencyInjection).Assembly, "mby").GetMetadata();
        var infraMeta = provider.GetRequiredService<ICliToolMetadataProvider>().GetMetadata();

        Assert.Equal("1.3.0", domainMeta.Version);
        Assert.Equal("1.3.0", appMeta.Version);
        Assert.Equal("1.3.0", infraMeta.Version);
        Assert.Equal("Meloberry CLI", infraMeta.ProductName);
        Assert.Equal("mby", infraMeta.ExecutableName);

        var editorBuilder = provider.GetRequiredService<ILayoutEditorPageBuilder>();
        var html = editorBuilder.BuildHtml("mcq-options", infraMeta);
        Assert.Contains("<meta name=\"generator\" content=\"Meloberry CLI (mby) 1.3.0\"", html);
        Assert.Contains("mby v1.3.0", html);
        Assert.Contains("Article Text Block", html);
        Assert.Contains("Image Block", html);
        Assert.Contains("MCQ Question", html);
        Assert.Contains("MCQ Options", html);
        Assert.Contains("Flashcard", html);
        Assert.Contains("mby-flashcard-scene", html);
        Assert.Contains("repeat(var(--option-columns, 1)", html);
        Assert.Contains("/api/layout/reset", html);
        Assert.DoesNotContain("spec.allowedValues", html);
        Assert.Contains("spec.allowedBounds", html);
        Assert.Contains("id=\"saveAllBtn\" type=\"button\" class=\"mby-nav-btn primary\"", html);
        Assert.Contains("const keys = (comp.fields || []).map(k => k.key)", html);
        Assert.Contains("data-theme=\"paper\"", html);
    }

    private ServiceProvider CreateProvider(string subDir)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(_tempRoot, subDir));
        return services.BuildServiceProvider();
    }

    private static Task WriteDummyPngAsync(string path) =>
        File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    [Fact]
    public async Task SecurityAndIntegrityFixes_CrossProcessId_ThemeSync_ScopedReset_MarkdownXss_JsonNulls_And_PackageGuards_Work()
    {
        var appData = Path.Combine(_tempRoot, "appdata_fixes");
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = appData);
        using var provider = services.BuildServiceProvider();

        // Two separate ServiceProvider/IIdGenerator instances sharing the same appData never collide
        var services2 = new ServiceCollection();
        services2.AddApplication();
        services2.AddInfrastructure(opts => opts.AppDataDirectory = appData);
        using var provider2 = services2.BuildServiceProvider();
        var gen1 = provider.GetRequiredService<IIdGenerator>();
        var gen2 = provider2.GetRequiredService<IIdGenerator>();
        var ids = Enumerable.Range(0, 10)
            .Select(i => (i % 2 == 0 ? gen1 : gen2).NewQuestionId())
            .ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.True(File.Exists(Path.Combine(appData, ".mby_id_clock")));

        // Global themeMode syncs across all profiles, and scoped reset preserves global keys
        var setLayout = provider.GetRequiredService<SetLayoutPropertyHandler>();
        var resetLayout = provider.GetRequiredService<ResetLayoutProfileHandler>();
        var getBounds = provider.GetRequiredService<GetLayoutBoundsHandler>();

        Assert.True((await setLayout.HandleAsync(new SetLayoutPropertyCommand("article", "themeMode", "dark"))).IsSuccess);
        Assert.True((await setLayout.HandleAsync(new SetLayoutPropertyCommand("mcq", "optionColumns", "2"))).IsSuccess);
        var mcqBounds = (await getBounds.HandleAsync(new GetLayoutBoundsQuery("mcq"))).Value;
        Assert.Equal("dark", mcqBounds.Properties.First(p => p.Property == "themeMode").CurrentValue);

        Assert.True((await resetLayout.HandleAsync(new ResetLayoutProfileCommand("mcq", ["optionColumns"]))).IsSuccess);
        var afterScopedReset = (await getBounds.HandleAsync(new GetLayoutBoundsQuery("mcq"))).Value;
        Assert.Equal("1", afterScopedReset.Properties.First(p => p.Property == "optionColumns").CurrentValue);
        Assert.Equal("dark", afterScopedReset.Properties.First(p => p.Property == "themeMode").CurrentValue);

        // Mixed flashcard question accepts canonical & case-insensitive flipDurationMs + asset filename with `..`
        var workDir = Path.Combine(_tempRoot, "work_fixes");
        Directory.CreateDirectory(workDir);
        var dottedPng = Path.Combine(_tempRoot, "diagram..final v2.png");
        await WriteDummyPngAsync(dottedPng);

        var apply = provider.GetRequiredService<ApplySpecHandler>();
        var patch = provider.GetRequiredService<PatchEntityHandler>();
        var setEntityLayout = provider.GetRequiredService<SetEntityLayoutHandler>();

        var quizJson = Path.Combine(_tempRoot, "card_spec.json");
        await File.WriteAllTextAsync(quizJson, $$"""
        {
          "kind": "quiz",
          "quizType": "mixed",
          "questions": [
            {
              "type": "flashcard",
              "front": {
                "text": "<script>alert(1)</script> [x](javascript:alert(2))",
                "imagePath": "diagram..final v2.png"
              },
              "back": { "text": "Back **safe**" }
            }
          ]
        }
        """);

        var appliedQuiz = (await apply.HandleAsync(new ApplySpecCommand(null, quizJson, WorkingDirectory: workDir))).Value;
        var cardId = appliedQuiz.AddedQuestionIds![0];
        var overrideRes = await setEntityLayout.HandleAsync(new SetEntityLayoutCommand(cardId, "FLIPDURATIONMS", "350"));
        Assert.True(overrideRes.IsSuccess);
        Assert.Equal("flipDurationMs", overrideRes.Value.Property);

        // WebApp disables raw HTML/XSS, escapes openFinishScreen id, applies defaultTextPosition & URL encoding
        Assert.True((await setLayout.HandleAsync(new SetLayoutPropertyCommand("flashcard", "defaultTextPosition", "aboveImage"))).IsSuccess);
        var buildWeb = provider.GetRequiredService<BuildWebAppHandler>();
        var webRes = await buildWeb.HandleAsync(new BuildWebAppCommand(appliedQuiz.TargetId, Path.Combine(_tempRoot, "web_out")));
        Assert.True(webRes.IsSuccess);
        var appJs = await File.ReadAllTextAsync(Path.Combine(webRes.Value.OutputDirectory, "app.js"));
        Assert.DoesNotContain("<script>alert(1)</script>", appJs);
        Assert.DoesNotContain("javascript:alert(2)", appJs);
        Assert.Contains("escapeHtml(s.id)", appJs);
        Assert.Contains("aboveImage", appJs);
        Assert.Contains("diagram_final%20v2", appJs);

        // JSON loader rejects null element inside options without NullReferenceException
        var badJson = Path.Combine(_tempRoot, "null_opt.json");
        await File.WriteAllTextAsync(badJson, """{ "questions": [ { "type": "mcq", "text": "Q?", "options": [null] } ] }""");
        var badJsonRes = await apply.HandleAsync(new ApplySpecCommand(appliedQuiz.TargetId, badJson));
        Assert.False(badJsonRes.IsSuccess);
        Assert.Equal("Json.InvalidQuestion", badJsonRes.Error!.Code);

        // Unmapped typo produces self-healing hint
        var typoJson = Path.Combine(_tempRoot, "typo.json");
        await File.WriteAllTextAsync(typoJson, """{ "kind": "quiz", "questions": [{ "type": "mcq", "text": "Q?", "options": [{ "text": "A", "correct": true }] }] }""");
        var typoRes = await apply.HandleAsync(new ApplySpecCommand(null, typoJson, WorkingDirectory: workDir));
        Assert.False(typoRes.IsSuccess);
        Assert.Equal("Json.ParseError", typoRes.Error!.Code);
        Assert.Equal("Did you mean 'isCorrect'?", typoRes.Error.Hint);

        // Import rejects manifest.json traversal IDs without touching destination
        var evilPkg = Path.Combine(_tempRoot, "evil_manifest.mbypkg");
        using (var za = ZipFile.Open(evilPkg, ZipArchiveMode.Create))
        {
            var mEntry = za.CreateEntry("manifest.json");
            using (var w = new StreamWriter(mEntry.Open()))
                await w.WriteAsync("""{"packageVersion":"1.0","kind":"quiz","id":"../escaped","exportedAtUtc":"2026-01-01T00:00:00Z","assets":[]}""");
            var wEntry = za.CreateEntry("workspace.json");
            using (var w = new StreamWriter(wEntry.Open()))
                await w.WriteAsync("""{"id":"../escaped","quizType":"Mcq","questions":[]}""");
        }
        var importer = provider.GetRequiredService<ImportPackageHandler>();
        var evilRes = await importer.HandleAsync(new ImportPackageCommand(evilPkg, workDir));
        Assert.False(evilRes.IsSuccess);
        Assert.Equal("Package.InvalidManifest", evilRes.Error!.Code);

        // Corrupt extra .db inside storage subdir does not crash workspace listing
        var corruptDir = Path.Combine(workDir, "Set_corrupt_fake");
        Directory.CreateDirectory(corruptDir);
        await File.WriteAllTextAsync(Path.Combine(corruptDir, "Set_corrupt_fake.db"), "not-sqlite");
        var listWorkspaces = provider.GetRequiredService<Meloberry.Application.UseCases.Quizzes.ListWorkspacesHandler>();
        var listRes = await listWorkspaces.HandleAsync(new Meloberry.Application.UseCases.Quizzes.ListWorkspacesQuery());
        Assert.True(listRes.IsSuccess);
    }

    [Fact]
    public async Task RegressionGuards_TraversalSvgXssJsonPositionUnreferencedAssetsAndZipSlip_Work()
    {
        using var provider = CreateProvider("appdata_reg");
        var workDir = Path.Combine(_tempRoot, "work_reg");
        Directory.CreateDirectory(workDir);

        // SEC-01: JSON loader blocks escaping imagePath traversal outside JSON directory
        var outsidePng = Path.Combine(_tempRoot, "outside.png");
        await WriteDummyPngAsync(outsidePng);
        var jsonDir = Path.Combine(workDir, "json_sub");
        Directory.CreateDirectory(jsonDir);
        var apply = provider.GetRequiredService<ApplySpecHandler>();
        var traversalJson = Path.Combine(jsonDir, "evil.json");
        await File.WriteAllTextAsync(traversalJson, """{ "kind": "quiz", "quizType": "mixed", "questions": [{ "type": "mcq", "text": "Q?", "options": [{"text":"A","imagePath":"../outside.png","isCorrect":true},{"text":"B"}] }] }""");
        var travRes = await apply.HandleAsync(new ApplySpecCommand(null, traversalJson, WorkingDirectory: workDir));
        Assert.False(travRes.IsSuccess);
        Assert.Equal("Asset.InvalidPath", travRes.Error!.Code);

        // SEC-04: SVG with script or event handler is rejected
        var inspector = provider.GetRequiredService<IContentInspector>();
        var evilSvg = Path.Combine(workDir, "evil.svg");
        await File.WriteAllTextAsync(evilSvg, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var wsAssetsDir = Path.Combine(workDir, "svg_test");
        var svgRes = await inspector.CopyAndVerifyImageAsync(evilSvg, wsAssetsDir);
        Assert.False(svgRes.IsSuccess);

        // Valid flashcard JSON position ("aboveImage") + omitted MCQ option index succeed
        var validJson = Path.Combine(jsonDir, "valid.json");
        await File.WriteAllTextAsync(validJson, """
        {
          "kind": "quiz",
          "quizType": "mixed",
          "questions": [
            { "type": "mcq", "text": "M?", "options": [ { "text": "Opt1", "isCorrect": true }, { "text": "Opt2" } ] },
            { "type": "flashcard", "front": { "text": "F", "position": "aboveImage" }, "back": { "text": "B" } }
          ]
        }
        """);
        var batchRes = await apply.HandleAsync(new ApplySpecCommand(null, validJson, WorkingDirectory: workDir));
        Assert.True(batchRes.IsSuccess);
        Assert.Equal(2, batchRes.Value.AddedQuestionIds!.Count);

        // SEC-02: Import rejects Zip Slip entry paths
        var zipSlipPkg = Path.Combine(_tempRoot, "zipslip.mbypkg");
        using (var za = ZipFile.Open(zipSlipPkg, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(za.CreateEntry("manifest.json").Open()))
                await w.WriteAsync("""{"packageVersion":"1.0","kind":"quiz","id":"Set_20261120_000000_001","exportedAtUtc":"2026-01-01T00:00:00Z","assets":[]}""");
            using (var w = new StreamWriter(za.CreateEntry("workspace.json").Open()))
                await w.WriteAsync("""{"id":"Set_20261120_000000_001","quizType":"Mcq","questions":[]}""");
            using (var w = new StreamWriter(za.CreateEntry("../../pwn.txt").Open()))
                await w.WriteAsync("bad");
        }
        var slipRes = await provider.GetRequiredService<ImportPackageHandler>().HandleAsync(new ImportPackageCommand(zipSlipPkg, workDir));
        Assert.False(slipRes.IsSuccess);

        // Paper theme syncs across profiles
        var setLayout = provider.GetRequiredService<SetLayoutPropertyHandler>();
        Assert.True((await setLayout.HandleAsync(new SetLayoutPropertyCommand("flashcard", "themeMode", "paper"))).IsSuccess);
        var artBounds = (await provider.GetRequiredService<GetLayoutBoundsHandler>().HandleAsync(new GetLayoutBoundsQuery("article"))).Value;
        Assert.Equal("paper", artBounds.Properties.First(p => p.Property == "themeMode").CurrentValue);

        // BuildWeb only copies referenced assets and deduplicates finish auto-save
        var unusedAsset = Path.Combine(batchRes.Value.StorageDirectory, "assets", "unused.png");
        Directory.CreateDirectory(Path.GetDirectoryName(unusedAsset)!);
        await WriteDummyPngAsync(unusedAsset);
        var webRes = await provider.GetRequiredService<BuildWebAppHandler>().HandleAsync(new BuildWebAppCommand(batchRes.Value.TargetId, Path.Combine(_tempRoot, "web_reg")));
        Assert.True(webRes.IsSuccess);
        Assert.False(File.Exists(Path.Combine(webRes.Value.OutputDirectory, "assets", "unused.png")));
        var js = await File.ReadAllTextAsync(Path.Combine(webRes.Value.OutputDirectory, "app.js"));
        Assert.Contains("if (!autoSaveSent && triggerSave)", js);
    }
}
