using System.IO.Compression;
using System.Text.Json;
using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Analysis;
using Meloberry.Domain.Common;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class Phase2RelabelAndIntegrationTests : IDisposable
{
    // Real browser-decodable 320x200 AliceBlue PNG (passes both FileContentInspector and Chromium img.decode)
    private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAUAAAADICAYAAACZBDirAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAKoSURBVHhe7dShAQAgDMCw/X8tEjc8LzQipgd0zt0FKJo/AFQYIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBA1gNPI9UTnmi3rAAAAABJRU5ErkJggg==");

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_phase2c_unit_{Guid.NewGuid():N}");

    public Phase2RelabelAndIntegrationTests() => Directory.CreateDirectory(_tempRoot);

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

    // 1. SPEC TESTS FIRST: Verify prime & spec catalog expose diff, mechanism, and relabel
    [Fact]
    public async Task PrimeAndSchema_ExposeAllThreePhase2Topics()
    {
        using var sp = CreateProvider("appdata_specs");
        using var scope = sp.CreateScope();
        var prime = scope.ServiceProvider.GetRequiredService<GetPrimeGuideHandler>();
        var schema = scope.ServiceProvider.GetRequiredService<GetSchemaHandler>();

        var primeRes = await prime.HandleAsync();
        Assert.True(primeRes.IsSuccess);
        Assert.Contains("diff", primeRes.Value.SpecTopics);
        Assert.Contains("mechanism", primeRes.Value.SpecTopics);
        Assert.Contains("relabel", primeRes.Value.SpecTopics);
        Assert.Contains("session|article|quiz|text|image|mcq|flashcard|layouts", primeRes.Value.Guide);
        Assert.Contains("diff|mechanism|relabel", primeRes.Value.Guide);
        var crlfGuide = primeRes.Value.Guide.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.True(crlfGuide.Length < 1550, $"Expected CRLF guide < 1550 chars, got {crlfGuide.Length}");

        var specRes = await schema.HandleAsync(new GetSchemaQuery(["diff", "mechanism", "relabel", "quiz", "markdown", "patch"]));
        Assert.True(specRes.IsSuccess, specRes.Error?.Description);
        Assert.Equal(6, specRes.Value.Schemas.Count);

        var relabelSchema = specRes.Value.Schemas.Single(s => s.Topic == "relabel");
        Assert.Equal("content", relabelSchema.Scope);
        Assert.Equal("question", relabelSchema.Category);
        Assert.Contains("reveal", relabelSchema.CompactSignature);
        Assert.Contains("bank", relabelSchema.CompactSignature);
        Assert.Contains("recall", relabelSchema.CompactSignature);
        Assert.Contains("whiteout", relabelSchema.CompactSignature);
        var allRelabelText = relabelSchema.CompactSignature + " " + relabelSchema.Summary + " " + string.Join(" ", relabelSchema.Fields.Select(f => f.Description));
        Assert.Contains("--no-masks", allRelabelText);
        Assert.Contains(".meta.json", allRelabelText);

        Assert.Contains("RelabelImage", specRes.Value.Schemas.Single(s => s.Topic == "quiz").CompactSignature);
        Assert.Contains(":::relabel", specRes.Value.Schemas.Single(s => s.Topic == "markdown").CompactSignature);
        Assert.Contains("relabel:", specRes.Value.Schemas.Single(s => s.Topic == "patch").CompactSignature);

        var qGroup = await schema.HandleAsync(new GetSchemaQuery(["@questions"]));
        Assert.True(qGroup.IsSuccess);
        Assert.Contains(qGroup.Value.Schemas, s => s.Topic == "relabel" && s.Scope == "content" && s.Category == "question");
    }

    // 2. Inline boxes Markdown + JSON round-trip, assets, .mbypkg, patch, lint & WebApp
    [Fact]
    public async Task RelabelImage_InlineBoxes_RoundTripsAssetsMbypkgPatchLintAndWebApp()
    {
        using var sp = CreateProvider("appdata_inline");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();

        var srcDir = Path.Combine(_tempRoot, "inline_src");
        var assetsDir = Path.Combine(srcDir, "assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "nephron.png"), ValidPngBytes);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "nephron_v2.png"), [.. ValidPngBytes, 0x00]);

        var mdPath = Path.Combine(srcDir, "relabel_quiz.md");
        await File.WriteAllTextAsync(mdPath, """
            ---
            kind: quiz
            quizType: mixed
            title: Renal Anatomy Relabel
            ---

            :::relabel{ref="rel-nephron" image="assets/nephron.png" mode="recall" style="blur"}
            prompt: Identify the nephron segments.
            boxes:
              - label: "Proximal convoluted tubule"
                aliases: ["PCT"]
                rect: [0.10, 0.20, 0.25, 0.12]
              - label: "Glomerulus"
                rect: [0.55, 0.15, 0.20, 0.10]
            :::explanation
            The proximal convoluted tubule reabsorbs ~65% of filtered sodium and water.
            :::
            """);

        var wsDir = Path.Combine(_tempRoot, "ws_inline");
        var appRes = await apply.HandleAsync(new ApplySpecCommand(null, mdPath, WorkingDirectory: wsDir));
        Assert.True(appRes.IsSuccess, appRes.Error?.Description);
        var quizId = appRes.Value.TargetId;

        // Also append a strict JSON type="relabel" question with mode="bank" and distractors
        var jsonPath = Path.Combine(srcDir, "bank_q.json");
        await File.WriteAllTextAsync(jsonPath, """
            {
              "questions": [
                {
                  "type": "relabel",
                  "ref": "rel-bank",
                  "imagePath": "assets/nephron.png",
                  "prompt": "Assign labels from bank.",
                  "mode": "bank",
                  "style": "solid",
                  "distractors": ["Distal tubule"],
                  "boxes": [
                    { "label": "Glomerulus", "rect": [0.40, 0.20, 0.22, 0.10] },
                    { "label": "Loop of Henle", "x": 0.20, "y": 0.60, "w": 0.25, "h": 0.12 }
                  ]
                }
              ]
            }
            """);
        var appendRes = await apply.HandleAsync(new ApplySpecCommand(quizId, jsonPath));
        Assert.True(appendRes.IsSuccess, appendRes.Error?.Description);

        // Verify SQLite reload, confined asset path & normalized boxes
        var loaded = (await repo.FindQuizContainerAsync(quizId))?.Quiz;
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Questions.Count);
        var pq1 = Assert.IsType<PayloadQuestion>(loaded.Questions[0]);
        Assert.Equal(QuestionType.RelabelImage, pq1.Type);
        Assert.True(Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(pq1.PayloadJson, out var p1) && p1 is not null);
        Assert.StartsWith("assets/", p1!.ImagePath);
        Assert.Null(p1.SourceMasksPath);
        Assert.Null(p1.IncludeLabels);
        Assert.Equal(2, p1.Boxes.Count);
        Assert.Equal(0.10, p1.Boxes[0].X, 4);
        Assert.Contains("PCT", p1.Boxes[0].Aliases);

        // Verify outline inspect snippet
        var outline = await inspect.HandleAsync(new InspectEntityQuery($"{quizId}#rel-nephron", InspectViewMode.Outline));
        Assert.True(outline.IsSuccess);
        Assert.Contains("[relabel:recall/blur]", outline.Value.Question!.Snippet);

        // Verify clean lint
        var lintRes = await lint.HandleAsync(new LintCommand(quizId));
        Assert.True(lintRes.IsSuccess && lintRes.Value.IsClean, string.Join("; ", lintRes.Value.Issues.Select(i => i.Message)));

        // Verify .mbypkg v2.0 export & import bundle image and preserve boxes
        var pkgPath = Path.Combine(_tempRoot, "relabel.mbypkg");
        var expRes = await export.HandleAsync(new ExportPackageCommand(quizId, pkgPath, "android"));
        Assert.True(expRes.IsSuccess, expRes.Error?.Description);
        using (var zip = ZipFile.OpenRead(pkgPath))
        {
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("assets/nephron", StringComparison.OrdinalIgnoreCase));
        }

        var reimpDir = Path.Combine(_tempRoot, "reimp_inline");
        var impRes = await import.HandleAsync(new ImportPackageCommand(pkgPath, reimpDir));
        Assert.True(impRes.IsSuccess, impRes.Error?.Description);

        // Verify PatchEntityHandler updates mode, style, prompt, imagePath, and rejects imagePath=null
        var patchOk = await patch.HandleAsync(new PatchEntityCommand(
            $"{quizId}#rel-nephron",
            SetPairs: ["mode=reveal", "style=pixelate", "prompt=Updated prompt"]));
        Assert.True(patchOk.IsSuccess, patchOk.Error?.Description);

        var imgPatchFile = Path.Combine(srcDir, "patch_img.json");
        await File.WriteAllTextAsync(imgPatchFile, """{"imagePath":"assets/nephron_v2.png"}""");
        var patchImgOk = await patch.HandleAsync(new PatchEntityCommand($"{quizId}#rel-nephron", JsonFilePath: imgPatchFile));
        Assert.True(patchImgOk.IsSuccess, patchImgOk.Error?.Description);

        var reloadedPatched = (await repo.FindQuizContainerAsync(quizId))?.Quiz;
        Assert.True(Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(reloadedPatched!.Questions[0].PayloadJson, out var patchedPayload) && patchedPayload is not null);
        Assert.Equal("reveal", patchedPayload!.Mode);
        Assert.Equal("pixelate", patchedPayload.Style);
        Assert.Equal("Updated prompt", patchedPayload.Prompt);
        Assert.NotEqual(p1.ImagePath, patchedPayload.ImagePath);

        var patchNullImg = await patch.HandleAsync(new PatchEntityCommand($"{quizId}#rel-nephron", SetPairs: ["imagePath=null"]));
        Assert.False(patchNullImg.IsSuccess);
        Assert.Equal("Patch.InvalidValue", patchNullImg.Error!.Code);

        // Verify BuildWebAppHandler bundles image & emits relabel runtime/CSS + passes static-only check
        var webDir = Path.Combine(_tempRoot, "web_inline");
        var buildRes = await build.HandleAsync(new BuildWebAppCommand(quizId, webDir, StaticVerifyOnly: true));
        Assert.True(buildRes.IsSuccess, buildRes.Error?.Description);
        Assert.NotNull(buildRes.Value.Verify);
        Assert.True(buildRes.Value.Verify!.Passed);
        Assert.True(buildRes.Value.Verify.ImagesChecked >= 1);

        var appJs = await File.ReadAllTextAsync(Path.Combine(webDir, "app.js"));
        var stylesCss = await File.ReadAllTextAsync(Path.Combine(webDir, "styles.css"));
        Assert.Contains("buildRelabelCard", appJs);
        Assert.Contains(".mby-relabel-stage", stylesCss);
        Assert.Contains(".mby-mask-blur", stylesCss);
        Assert.Contains(".mby-mask-pixelate", stylesCss);
        Assert.True(Directory.GetFiles(Path.Combine(webDir, "assets"), "nephron*.png").Length >= 1);

        // Verify SessionAnalysisReport markdown formatting includes relabel
        var report = new SessionAnalysisReport(
            "1.0", quizId, "Relabel", "2026-01-01T00:00:00Z", "2026-01-01T00:01:00Z", 60000,
            new SessionAnalysisSummary(1, 1, 1, 1, 100.0, 0, 0, 0, 0),
            [new SlideAnalysisEntry(1, pq1.Id, "relabel", 15000, 1, 1, null, null, true, true, null, null)],
            []);
        Assert.Contains("Correct: Yes, Checks: 1", report.ToMarkdownReport());
    }

    // 3. Normalized .masks.json + melomg --meta .meta.json + nested relative confinement + guardrails
    [Fact]
    public async Task RelabelImage_SidecarJson_And_MelomgMetaJson_LoadFilterAndEnforceConfinement()
    {
        using var sp = CreateProvider("appdata_sidecar");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var modDir = Path.Combine(_tempRoot, "sidecar_mod");
        var assetsDir = Path.Combine(modDir, "assets");
        var nestedDir = Path.Combine(modDir, "quizzes");
        Directory.CreateDirectory(assetsDir);
        Directory.CreateDirectory(nestedDir);

        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "diagram.png"), ValidPngBytes);
        await File.WriteAllTextAsync(Path.Combine(assetsDir, "diagram.masks.json"), """
            {
              "boxes": [
                { "label": "Proximal tubule", "aliases": ["PCT"], "rect": [0.10, 0.10, 0.20, 0.08] },
                { "label": "Loop of Henle", "rect": [0.35, 0.50, 0.25, 0.10] },
                { "label": "Collecting duct", "rect": [0.70, 0.25, 0.20, 0.10] }
              ]
            }
            """);

        // melomg --meta format (camelCase FigureSidecarMetadata with nullable text)
        await File.WriteAllTextAsync(Path.Combine(assetsDir, "diagram.meta.json"), """
            {
              "figureId": "Fig_1",
              "pixelWidth": 1000,
              "pixelHeight": 500,
              "appliedMasks": [
                { "index": 1, "pixelX": 100, "pixelY": 100, "pixelWidth": 200, "pixelHeight": 50, "text": "Glomerulus", "style": "solid", "source": "ocr" },
                { "index": 2, "pixelX": 400, "pixelY": 250, "pixelWidth": 150, "pixelHeight": 50, "text": null, "style": "solid", "source": "manual" }
              ]
            }
            """);

        // 3a. Normalized .masks.json with include filtering (3 -> 2)
        var md1 = Path.Combine(modDir, "q1.md");
        await File.WriteAllTextAsync(md1, """
            ---
            kind: quiz
            quizType: mixed
            title: Sidecar Filtered
            ---

            :::relabel{ref="rel-sidecar" image="assets/diagram.png" masks="assets/diagram.masks.json" mode="bank" style="whiteout"}
            prompt: Label selected parts.
            include: ["Proximal tubule", "Collecting duct"]
            :::
            """);
        var r1 = await apply.HandleAsync(new ApplySpecCommand(null, md1, WorkingDirectory: Path.Combine(_tempRoot, "ws_s1")));
        Assert.True(r1.IsSuccess, r1.Error?.Description);
        var q1 = (await repo.FindQuizContainerAsync(r1.Value.TargetId))?.Quiz;
        var rel1 = JsonSerializer.Deserialize<RelabelImagePayload>(q1!.Questions[0].PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(2, rel1.Boxes.Count);
        Assert.Equal("Proximal tubule", rel1.Boxes[0].Label);
        Assert.Equal("Collecting duct", rel1.Boxes[1].Label);
        Assert.Null(rel1.SourceMasksPath);

        // 3b. melomg .meta.json normalization & graceful null text handling
        var md2 = Path.Combine(modDir, "q2.md");
        await File.WriteAllTextAsync(md2, """
            ---
            kind: quiz
            quizType: mixed
            title: Melomg Meta
            ---

            :::relabel{ref="rel-meta" image="assets/diagram.png" masks="assets/diagram.meta.json" mode="reveal" style="solid"}
            prompt: Reveal labels.
            :::
            """);
        var r2 = await apply.HandleAsync(new ApplySpecCommand(null, md2, WorkingDirectory: Path.Combine(_tempRoot, "ws_s2")));
        Assert.True(r2.IsSuccess, r2.Error?.Description);
        var q2 = (await repo.FindQuizContainerAsync(r2.Value.TargetId))?.Quiz;
        var rel2 = JsonSerializer.Deserialize<RelabelImagePayload>(q2!.Questions[0].PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(2, rel2.Boxes.Count);
        Assert.Equal("Glomerulus", rel2.Boxes[0].Label);
        Assert.Equal(0.1, rel2.Boxes[0].X, 4);
        Assert.Equal(0.2, rel2.Boxes[0].Y, 4);
        Assert.Equal(0.2, rel2.Boxes[0].W, 4);
        Assert.Equal(0.1, rel2.Boxes[0].H, 4);
        Assert.Equal(string.Empty, rel2.Boxes[1].Label); // null text mapped to "" without crash

        // 3c. Nested component ../assets/diagram.masks.json confined inside specRootDirectory (via public ApplySpecHandler + StubSpecLoader)
        var nestedServices = new ServiceCollection();
        nestedServices.AddApplication();
        nestedServices.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(_tempRoot, "appdata_nested"));
        var stubLoader = new StubSpecLoader();
        nestedServices.AddSingleton<IDeclarativeSpecLoader>(stubLoader);
        using var nestedSp = nestedServices.BuildServiceProvider();
        var nestedApply = nestedSp.GetRequiredService<ApplySpecHandler>();
        var nestedPayload = Phase1PayloadCodec.Serialize(new RelabelImagePayload(
            "../assets/diagram.png", "Nested sidecar", "bank", "solid", [], [], true, false, null,
            SourceMasksPath: "../assets/diagram.masks.json"));
        stubLoader.NextSpec = new DeclarativeApplySpec(
            Kind: "session", Name: "Nested Relabel", Description: null, Title: null, QuizType: null,
            Components:
            [
                new DeclarativeComponentSpec(
                    "quiz", "Nested Quiz", "mixed", null,
                    [new JsonQuestionSpec("relabel", null, null, false, [], null, null, null, null, null, "rel-nested", nestedPayload)],
                    null, SourceDirectory: nestedDir)
            ],
            Blocks: null, Questions: null, LayoutOverrides: null, AssetBaseDirectory: modDir);
        var nestedRes = await nestedApply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: "stub", WorkingDirectory: Path.Combine(_tempRoot, "ws_nested")));
        Assert.True(nestedRes.IsSuccess, nestedRes.Error?.Description);

        // 3d. Reject specifying BOTH masks sidecar AND inline boxes (Markdown & JSON)
        var bothMd = Path.Combine(modDir, "both.md");
        await File.WriteAllTextAsync(bothMd, """
            ---
            kind: quiz
            quizType: mixed
            ---

            :::relabel{image="assets/diagram.png" masks="assets/diagram.masks.json"}
            boxes:
              - label: "A"
                rect: [0.1, 0.1, 0.2, 0.2]
            :::
            """);
        var bothMdRes = await apply.HandleAsync(new ApplySpecCommand(null, bothMd, WorkingDirectory: Path.Combine(_tempRoot, "ws_both_md")));
        Assert.False(bothMdRes.IsSuccess);
        Assert.Equal("Markdown.InvalidRelabel", bothMdRes.Error!.Code);

        var bothJson = Path.Combine(modDir, "both.json");
        await File.WriteAllTextAsync(bothJson, """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "questions": [
                {
                  "type": "relabel",
                  "imagePath": "assets/diagram.png",
                  "masksPath": "assets/diagram.masks.json",
                  "boxes": [{ "label": "A", "rect": [0.1, 0.1, 0.2, 0.2] }]
                }
              ]
            }
            """);
        var bothJsonRes = await apply.HandleAsync(new ApplySpecCommand(null, bothJson, WorkingDirectory: Path.Combine(_tempRoot, "ws_both_json")));
        Assert.False(bothJsonRes.IsSuccess);
        Assert.Equal("Json.InvalidQuestion", bothJsonRes.Error!.Code);

        // 3e. include filter matching 0 boxes fails fast with Relabel.IncludeMatchedNoBoxes
        var noMatchMd = Path.Combine(modDir, "nomatch.md");
        await File.WriteAllTextAsync(noMatchMd, """
            ---
            kind: quiz
            quizType: mixed
            ---

            :::relabel{image="assets/diagram.png" masks="assets/diagram.masks.json"}
            include: ["NonExistentStructure"]
            :::
            """);
        var noMatchRes = await apply.HandleAsync(new ApplySpecCommand(null, noMatchMd, WorkingDirectory: Path.Combine(_tempRoot, "ws_nomatch")));
        Assert.False(noMatchRes.IsSuccess);
        Assert.Equal("Relabel.IncludeMatchedNoBoxes", noMatchRes.Error!.Code);

        // 3f. Path traversal escape on masks="../secret.json" and image="../secret.png" fails with Asset.InvalidPath
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "secret.json"), """{"boxes":[{"label":"X","rect":[0.1,0.1,0.2,0.2]}]}""");
        await File.WriteAllBytesAsync(Path.Combine(_tempRoot, "secret.png"), ValidPngBytes);

        var escMaskMd = Path.Combine(modDir, "esc_mask.md");
        await File.WriteAllTextAsync(escMaskMd, """
            ---
            kind: quiz
            quizType: mixed
            ---

            :::relabel{image="assets/diagram.png" masks="../secret.json"}
            prompt: Escape mask test.
            :::
            """);
        var escMaskRes = await apply.HandleAsync(new ApplySpecCommand(null, escMaskMd, WorkingDirectory: Path.Combine(_tempRoot, "ws_escm")));
        Assert.False(escMaskRes.IsSuccess);
        Assert.Equal("Asset.InvalidPath", escMaskRes.Error!.Code);

        var escImgMd = Path.Combine(modDir, "esc_img.md");
        await File.WriteAllTextAsync(escImgMd, """
            ---
            kind: quiz
            quizType: mixed
            ---

            :::relabel{image="../secret.png" masks="assets/diagram.masks.json"}
            prompt: Escape image test.
            :::
            """);
        var escImgRes = await apply.HandleAsync(new ApplySpecCommand(null, escImgMd, WorkingDirectory: Path.Combine(_tempRoot, "ws_esci")));
        Assert.False(escImgRes.IsSuccess);
        Assert.Equal("Asset.InvalidPath", escImgRes.Error!.Code);
    }

    // 4. Lint validation catches out-of-bounds boxes, empty boxes, empty label, invalid mode/style & broken image
    [Fact]
    public async Task RelabelImage_LintValidation_CatchesOutOfBoundsBoxesEmptyLabelsAndBrokenImage()
    {
        using var sp = CreateProvider("appdata_lint");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var srcDir = Path.Combine(_tempRoot, "lint_src");
        Directory.CreateDirectory(srcDir);
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "img.png"), ValidPngBytes);

        var badSpecPath = Path.Combine(srcDir, "bad_relabel.json");
        await File.WriteAllTextAsync(badSpecPath, """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "name": "Bad Relabels",
              "questions": [
                {
                  "type": "relabel",
                  "imagePath": "img.png",
                  "mode": "invalid-mode",
                  "style": "neon",
                  "boxes": [
                    { "label": "OverflowBox", "x": 0.8, "y": 0.2, "w": 0.5, "h": 0.1 },
                    { "label": "   ", "x": 0.1, "y": 0.1, "w": 0.2, "h": 0.1 }
                  ]
                },
                {
                  "type": "relabel",
                  "imagePath": "img.png",
                  "mode": "bank",
                  "style": "solid",
                  "boxes": []
                }
              ]
            }
            """);

        var wsDir = Path.Combine(_tempRoot, "ws_lint");
        var applied = await apply.HandleAsync(new ApplySpecCommand(null, badSpecPath, WorkingDirectory: wsDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);
        var quizId = applied.Value.TargetId;

        var report1 = await lint.HandleAsync(new LintCommand(quizId));
        Assert.True(report1.IsSuccess);
        Assert.False(report1.Value.IsClean);
        Assert.Contains(report1.Value.Issues, i => i.Code == "Lint.InvalidRelabelMode");
        Assert.Contains(report1.Value.Issues, i => i.Code == "Lint.InvalidRelabelStyle");
        Assert.Contains(report1.Value.Issues, i => i.Code == "Lint.InvalidRelabelBoxBounds");
        Assert.Contains(report1.Value.Issues, i => i.Code == "Lint.EmptyRelabelLabel");
        Assert.Contains(report1.Value.Issues, i => i.Code == "Lint.EmptyRelabelBoxes");

        // Delete stored image file on disk -> Lint.BrokenImageFile
        var quiz = (await repo.FindQuizContainerAsync(quizId))?.Quiz;
        foreach (var file in Directory.GetFiles(Path.Combine(quiz!.WorkingDirectory, "assets")))
            File.Delete(file);

        var report2 = await lint.HandleAsync(new LintCommand(quizId));
        Assert.True(report2.IsSuccess);
        Assert.Contains(report2.Value.Issues, i => i.Code == "Lint.BrokenImageFile");
    }

    // 5. Mixed Phase 2 session (diff + mechanism + relabel) passes static-only and real headless CDP
    [Fact]
    public async Task MixedPhase2Session_HeadlessCdpVerification_PassesZeroErrorsOverflowOrBrokenImages()
    {
        using var sp = CreateProvider("appdata_cdp");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var verifier = scope.ServiceProvider.GetRequiredService<IHeadlessWebAppVerifier>();

        var modDir = Path.Combine(_tempRoot, "mixed_p2");
        var assetsDir = Path.Combine(modDir, "assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "nephron.png"), ValidPngBytes);

        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), "---\nkind: session\nname: Mixed Phase 2 CDP Suite\n---\n");
        await File.WriteAllTextAsync(Path.Combine(modDir, "01-mixed-quiz.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: All Phase 2 Interactive Drills
            ---

            :::diff{mode="spot-wrong" title="Gram Wall"}
            | Property | Gram+ | Gram- |
            | :--- | :--- | :--- |
            | Outer membrane | ~~Present~~ -> Absent | Present |
            :::

            :::diff{mode="fill" bank=true title="Acid-Base"}
            | Disorder | Primary |
            | :--- | :--- |
            | Respiratory Acidosis | {{High PaCO2 | Increased PaCO2}} |
            :::

            :::diff{mode="rearrange" title="Diabetes"}
            | Feature | Type 1 | Type 2 |
            | :--- | :--- | :--- |
            | Onset | Childhood | Adulthood |
            :::

            :::mechanism{title="RAAS Activation"}
            1. Renin release
            2. Angiotensin I formed
            3a. Efferent arteriole constriction
            3b. Aldosterone release
            ! ANP stimulates renin
            :::

            :::relabel{ref="rel-bank" image="assets/nephron.png" mode="bank" style="blur"}
            prompt: Match the nephron labels ($\text{Na}^+$ transport).
            distractors: ["Collecting duct"]
            boxes:
              - label: "Proximal tubule"
                rect: [0.10, 0.15, 0.30, 0.18]
              - label: "Glomerulus"
                rect: [0.55, 0.45, 0.30, 0.18]
            :::explanation
            Proximal tubule reabsorbs $\text{Na}^+$ iso-osmotically.
            :::

            :::relabel{ref="rel-reveal" image="assets/nephron.png" mode="reveal" style="whiteout"}
            prompt: Tap masks to reveal structures.
            boxes:
              - label: "Loop of Henle"
                rect: [0.20, 0.30, 0.28, 0.18]
            :::
            """);

        var wsDir = Path.Combine(_tempRoot, "ws_cdp");
        var applied = await apply.HandleAsync(new ApplySpecCommand(null, modDir, WorkingDirectory: wsDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);

        var webDir = Path.Combine(_tempRoot, "web_cdp");
        var built = await build.HandleAsync(new BuildWebAppCommand(applied.Value.TargetId, webDir, StaticVerifyOnly: true));
        Assert.True(built.IsSuccess, built.Error?.Description);

        var staticRes = await verifier.VerifyAsync(webDir, staticOnly: true);
        Assert.True(staticRes.IsSuccess, staticRes.Error?.Description);
        Assert.True(staticRes.Value.Passed);
        Assert.True(staticRes.Value.ImagesChecked >= 1);

        var cdpRes = await verifier.VerifyAsync(webDir, staticOnly: false, timeoutSeconds: 30);
        if (cdpRes.IsSuccess || cdpRes.Error?.Code != "Verify.BrowserNotFound")
        {
            Assert.True(cdpRes.IsSuccess, cdpRes.Error?.Description);
            Assert.True(cdpRes.Value.Passed,
                $"CDP failed: console=[{string.Join("; ", cdpRes.Value.ConsoleErrors)}], overflow=[{string.Join("; ", cdpRes.Value.OverflowIssues)}], broken=[{string.Join("; ", cdpRes.Value.BrokenAssets)}], katex=[{string.Join("; ", cdpRes.Value.KatexErrors)}]");
            Assert.Empty(cdpRes.Value.ConsoleErrors);
            Assert.Empty(cdpRes.Value.OverflowIssues);
            Assert.Empty(cdpRes.Value.BrokenAssets);
            Assert.Empty(cdpRes.Value.KatexErrors);
        }
    }

    private sealed class StubSpecLoader : IDeclarativeSpecLoader
    {
        public DeclarativeApplySpec? NextSpec { get; set; }

        public Task<Result<DeclarativeApplySpec>> LoadAsync(string specPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<DeclarativeApplySpec>.Success(NextSpec!));

        public Task<Result<DeclarativeApplySpec>> LoadFromTextAsync(string rawText, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<DeclarativeApplySpec>.Success(NextSpec!));
    }
}
