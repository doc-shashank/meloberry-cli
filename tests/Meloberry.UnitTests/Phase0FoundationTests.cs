using System.IO.Compression;
using System.Text.Json;
using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;
using Meloberry.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class Phase0FoundationTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_phase0_unit_{Guid.NewGuid():N}");

    public Phase0FoundationTests() => Directory.CreateDirectory(_tempRoot);

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

    private static byte[] CreateMinimalPng() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    [Fact]
    public async Task Sqlite_PayloadJson_MigratesLegacyDb_And_RoundTripsArticleBlockPayload()
    {
        var wsRoot = Path.Combine(_tempRoot, "legacy_ws");
        var sessionId = "Session_20261120_100000_001";
        var sessionDir = Path.Combine(wsRoot, sessionId);
        Directory.CreateDirectory(Path.Combine(sessionDir, "assets"));
        var dbPath = Path.Combine(sessionDir, $"{sessionId}.db");

        // Build a legacy v1.3 SQLite DB intentionally WITHOUT PayloadJson columns
        await using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE WorkspaceMeta (Id TEXT PRIMARY KEY, Kind TEXT NOT NULL, SubType TEXT NOT NULL, ActiveComponentId TEXT NULL, Name TEXT NULL, Description TEXT NULL);
                CREATE TABLE Components (Id TEXT PRIMARY KEY, SortIndex INTEGER NOT NULL, Kind TEXT NOT NULL, QuizType TEXT NULL, LayoutOverridesJson TEXT NULL, Title TEXT NULL, RefKey TEXT NULL);
                CREATE TABLE ArticleBlocks (Id TEXT PRIMARY KEY, ComponentId TEXT NOT NULL, SortIndex INTEGER NOT NULL, BlockKind TEXT NOT NULL, TextContent TEXT NULL, ImagePath TEXT NULL, RefKey TEXT NULL, Caption TEXT NULL, Alt TEXT NULL, CalloutTone TEXT NULL, CalloutTitle TEXT NULL);
                CREATE TABLE Questions (Id TEXT PRIMARY KEY, ContainerId TEXT NOT NULL, SortIndex INTEGER NOT NULL, QuestionType TEXT NOT NULL, PromptText TEXT NULL, MaxOptions INTEGER NOT NULL, IsMultiCorrect INTEGER NOT NULL, FrontText TEXT NULL, FrontImagePath TEXT NULL, FrontPosition TEXT NULL, BackText TEXT NULL, BackImagePath TEXT NULL, BackPosition TEXT NULL, LayoutOverridesJson TEXT NULL, PromptImagePath TEXT NULL, Explanation TEXT NULL, RefKey TEXT NULL);
                CREATE TABLE McqOptions (QuestionId TEXT NOT NULL, OptionIndex INTEGER NOT NULL, OptionText TEXT NULL, ImagePath TEXT NULL, IsCorrect INTEGER NOT NULL, PRIMARY KEY (QuestionId, OptionIndex));

                INSERT INTO WorkspaceMeta VALUES ('Session_20261120_100000_001', 'session', 'session', 'Component_20261120_100000_002', 'Legacy Session', 'Pre-v2 DB');
                INSERT INTO Components VALUES ('Component_20261120_100000_002', 1, 'Article', NULL, '{}', 'Legacy Notes', 'legacy-notes');
                INSERT INTO ArticleBlocks VALUES ('ArtComp_20261120_100000_003', 'Component_20261120_100000_002', 1, 'Text', 'Legacy text content', NULL, 'legacy-blk', NULL, NULL, NULL, NULL);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        using var sp = CreateProvider("appdata_legacy");
        using var scope = sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();

        // Listing discovers the legacy workspace and triggers indexing
        var listed = await repo.ListWorkspacesAsync(wsRoot);
        Assert.Single(listed);
        Assert.Equal(sessionId, listed[0].Id);

        // Loading migrates PayloadJson columns non-destructively
        var found = await repo.FindSessionAsync(sessionId);
        Assert.NotNull(found);
        var block = found!.Session.Components[0].ArticleBlocks[0];
        Assert.Equal("Legacy text content", block.TextContent);
        Assert.Null(block.PayloadJson);

        // Mutate PayloadJson via domain method, save, reload, and inspect Full view
        const string samplePayload = """{"version":2,"stickyFirstCol":true}""";
        Assert.True(block.SetPayloadJson(samplePayload).IsSuccess);
        await repo.SaveSessionAsync(found.Session);

        var reloaded = await repo.FindSessionAsync(sessionId);
        Assert.NotNull(reloaded);
        Assert.Equal(samplePayload, reloaded!.Session.Components[0].ArticleBlocks[0].PayloadJson);

        var inspected = await inspect.HandleAsync(new InspectEntityQuery($"{sessionId}#legacy-blk", InspectViewMode.Full));
        Assert.True(inspected.IsSuccess, inspected.Error?.Description);
        Assert.Equal(samplePayload, inspected.Value.ArticleBlock?.PayloadJson);
    }

    [Fact]
    public async Task Mbypkg_ExportsVersion2_And_ImportsBothV1AndV2_And_RejectsUnsupportedVersion()
    {
        using var sp = CreateProvider("appdata_pkg");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();

        var wsDir = Path.Combine(_tempRoot, "ws_pkg");
        var specJson = """
            {
              "kind": "session",
              "name": "Packaging v2 Test",
              "components": [
                {
                  "kind": "article",
                  "title": "Intro",
                  "blocks": [{ "kind": "text", "text": "Hello v2" }]
                },
                {
                  "kind": "quiz",
                  "title": "Check",
                  "quizType": "mcq",
                  "questions": [
                    {
                      "type": "mcq",
                      "text": "2 + 2?",
                      "options": [
                        { "text": "4", "isCorrect": true },
                        { "text": "5", "isCorrect": false }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        var applied = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: specJson, WorkingDirectory: wsDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);

        // 1. Export writes packageVersion "2.0" and omits null payloadJson
        var pkgV2Path = Path.Combine(_tempRoot, "v2_out.mbypkg");
        var expRes = await export.HandleAsync(new ExportPackageCommand(applied.Value.TargetId, pkgV2Path, "android"));
        Assert.True(expRes.IsSuccess, expRes.Error?.Description);

        using (var zip = ZipFile.OpenRead(pkgV2Path))
        {
            using var manifestReader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
            using var manifestDoc = JsonDocument.Parse(await manifestReader.ReadToEndAsync());
            Assert.Equal("2.0", manifestDoc.RootElement.GetProperty("packageVersion").GetString());

            using var wsReader = new StreamReader(zip.GetEntry("workspace.json")!.Open());
            var wsJson = await wsReader.ReadToEndAsync();
            Assert.DoesNotContain("payloadJson", wsJson, StringComparison.OrdinalIgnoreCase);
        }

        // Re-import v2.0 archive cleanly
        var importV2Dir = Path.Combine(_tempRoot, "import_v2");
        var impV2Res = await import.HandleAsync(new ImportPackageCommand(pkgV2Path, importV2Dir));
        Assert.True(impV2Res.IsSuccess, impV2Res.Error?.Description);

        // 2. Import legacy v1.0 archive (without payloadJson fields) succeeds
        var pkgV1Path = Path.Combine(_tempRoot, "v1_legacy.mbypkg");
        using (var za = ZipFile.Open(pkgV1Path, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(za.CreateEntry("manifest.json").Open()))
                await w.WriteAsync("""{"packageVersion":"1.0","kind":"quiz","id":"Set_20261120_100100_001","exportedAtUtc":"2026-01-01T00:00:00Z","assets":[]}""");
            using (var w = new StreamWriter(za.CreateEntry("workspace.json").Open()))
                await w.WriteAsync("""{"id":"Set_20261120_100100_001","quizType":"Flashcard","name":"Legacy v1 Quiz","questions":[{"id":"Q_20261120_100100_002","index":1,"questionType":"Flashcard","maxOptions":0,"isMultiCorrect":false,"options":[],"front":{"text":"Front","position":"TextBelowImage"},"back":{"text":"Back","position":"TextBelowImage"}}]}""");
        }
        var importV1Dir = Path.Combine(_tempRoot, "import_v1");
        var impV1Res = await import.HandleAsync(new ImportPackageCommand(pkgV1Path, importV1Dir));
        Assert.True(impV1Res.IsSuccess, impV1Res.Error?.Description);
        Assert.Equal("Set_20261120_100100_001", impV1Res.Value.ImportedId);

        // 3. Import unsupported v3.0 archive fails with Package.UnsupportedVersion
        var pkgV3Path = Path.Combine(_tempRoot, "v3_future.mbypkg");
        using (var za = ZipFile.Open(pkgV3Path, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(za.CreateEntry("manifest.json").Open()))
                await w.WriteAsync("""{"packageVersion":"3.0","kind":"quiz","id":"Set_20261120_100200_001","exportedAtUtc":"2026-01-01T00:00:00Z","assets":[]}""");
            using (var w = new StreamWriter(za.CreateEntry("workspace.json").Open()))
                await w.WriteAsync("""{"id":"Set_20261120_100200_001","quizType":"Mcq","questions":[]}""");
        }
        var impV3Res = await import.HandleAsync(new ImportPackageCommand(pkgV3Path, Path.Combine(_tempRoot, "import_v3")));
        Assert.False(impV3Res.IsSuccess);
        Assert.Equal("Package.UnsupportedVersion", impV3Res.Error!.Code);
    }

    [Fact]
    public async Task MarkdownQuiz_StandaloneAndDirectory_ParsesMcqAndFlashcardWithImagesExplanationsAndLayout()
    {
        var sessionDir = Path.Combine(_tempRoot, "md_session");
        var assetsDir = Path.Combine(sessionDir, "assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "prompt.png"), CreateMinimalPng());
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "opt_c.png"), CreateMinimalPng());
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "q_front.png"), CreateMinimalPng());
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "q_back.png"), CreateMinimalPng());

        await File.WriteAllTextAsync(Path.Combine(sessionDir, "_session.md"), """
            ---
            kind: session
            name: Renal Pharmacology
            description: Modular article + Markdown quiz
            ---
            """);

        await File.WriteAllTextAsync(Path.Combine(sessionDir, "01-notes.md"), """
            ---
            title: Diuretics Overview
            ref: diuretics-notes
            ---

            # Diuretics

            Loop diuretics inhibit $\text{Na}^+/\text{K}^+/2\text{Cl}^-$.
            """);

        var quizMdPath = Path.Combine(sessionDir, "02-review.md");
        await File.WriteAllTextAsync(quizMdPath, """
            ---
            kind: quiz
            quizType: mixed
            title: Diuretics Review
            ref: diuretics-quiz
            ---

            # Diuretics Review

            :::mcq{ref="q-loop" multi=true image="assets/prompt.png"}
            Which statements are true about **loop diuretics**?

            ```text
            :::not-a-fence inside code block
            - [ ] not an option inside code block
            ```

            - [x] Act on thick ascending limb
            - [x] Decrease serum $\text{Ca}^{2+}$
            - [ ] Sparing of potassium ![Option C](assets/opt_c.png)
            :::explanation
            Loops inhibit NKCC2 and reduce paracellular $\text{Ca}^{2+}$ reabsorption.
            :::layout
            optionColumns: 2
            :::

            :::flashcard{ref="fc-nkcc2" frontPosition="aboveImage" backPosition="belowImage"}
            :::front
            ![Masked](assets/q_front.png)
            Identify the highlighted transporter.
            :::back
            ![Unmasked](assets/q_back.png)
            NKCC2 cotransporter
            :::layout
            flipDurationMs: 600
            :::

            :::flashcard{ref="fc-implicit"}
            Implicit front side without explicit front fence
            :::back
            Back side text
            :::
            """);

        using var sp = CreateProvider("appdata_md_quiz");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();

        // 1. Directory mode combining 01-notes.md + 02-review.md
        var wsDir = Path.Combine(_tempRoot, "ws_md_dir");
        var dirRes = await apply.HandleAsync(new ApplySpecCommand(null, sessionDir, WorkingDirectory: wsDir));
        Assert.True(dirRes.IsSuccess, dirRes.Error?.Description);
        var loaded = await repo.FindSessionAsync(dirRes.Value.TargetId);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Session.Components.Count);
        Assert.Equal(ComponentKind.Article, loaded.Session.Components[0].Kind);
        Assert.Equal(ComponentKind.Quiz, loaded.Session.Components[1].Kind);
        Assert.Equal("diuretics-quiz", loaded.Session.Components[1].RefKey);

        var quiz = loaded.Session.Components[1].Quiz!;
        Assert.Equal(QuizType.Mixed, quiz.Type);
        Assert.Equal(3, quiz.Questions.Count);

        var mcq = Assert.IsType<McqQuestion>(quiz.Questions[0]);
        Assert.Equal("q-loop", mcq.RefKey);
        Assert.True(mcq.IsMultiCorrect);
        Assert.Equal(3, mcq.MaxOptions); // auto-sized to option count to avoid Lint.UnfilledOptionSlot
        Assert.NotNull(mcq.ImagePath);
        Assert.Contains(":::not-a-fence inside code block", mcq.Text);
        Assert.Equal(3, mcq.Options.Count);
        Assert.True(mcq.Options[0].IsCorrect);
        Assert.True(mcq.Options[1].IsCorrect);
        Assert.False(mcq.Options[2].IsCorrect);
        Assert.Equal("Sparing of potassium", mcq.Options[2].Text);
        Assert.NotNull(mcq.Options[2].ImagePath);
        Assert.Contains("NKCC2", mcq.Explanation);
        Assert.Equal("2", mcq.LayoutOverrides["optionColumns"]);

        var fc1 = Assert.IsType<FlashcardQuestion>(quiz.Questions[1]);
        Assert.Equal("fc-nkcc2", fc1.RefKey);
        Assert.Equal(MediaPosition.TextAboveImage, fc1.Front.Position);
        Assert.Equal(MediaPosition.TextBelowImage, fc1.Back.Position);
        Assert.NotNull(fc1.Front.ImagePath);
        Assert.NotNull(fc1.Back.ImagePath);
        Assert.Equal("Identify the highlighted transporter.", fc1.Front.Text);
        Assert.Equal("NKCC2 cotransporter", fc1.Back.Text);
        Assert.Equal("600", fc1.LayoutOverrides["flipDurationMs"]);

        var fc2 = Assert.IsType<FlashcardQuestion>(quiz.Questions[2]);
        Assert.Equal("fc-implicit", fc2.RefKey);
        Assert.Equal("Implicit front side without explicit front fence", fc2.Front.Text);
        Assert.Equal("Back side text", fc2.Back.Text);

        // Lint must be 100% clean (0 errors, 0 warnings)
        var lintRes = await lint.HandleAsync(new LintCommand(dirRes.Value.TargetId));
        Assert.True(lintRes.IsSuccess, lintRes.Error?.Description);
        Assert.True(lintRes.Value.IsClean);
        Assert.Equal(0, lintRes.Value.ErrorCount);
        Assert.Equal(0, lintRes.Value.WarningCount);

        // Build with --static-only + pkg export/import round-trip
        var webDir = Path.Combine(_tempRoot, "md_quiz_web");
        var buildRes = await build.HandleAsync(new BuildWebAppCommand(dirRes.Value.TargetId, webDir, StaticVerifyOnly: true));
        Assert.True(buildRes.IsSuccess, buildRes.Error?.Description);
        Assert.True(buildRes.Value.Verify?.Passed);

        var pkgPath = Path.Combine(_tempRoot, "md_quiz.mbypkg");
        var expRes = await export.HandleAsync(new ExportPackageCommand(dirRes.Value.TargetId, pkgPath, "desktop"));
        Assert.True(expRes.IsSuccess, expRes.Error?.Description);
        var impRes = await import.HandleAsync(new ImportPackageCommand(pkgPath, Path.Combine(_tempRoot, "md_quiz_reimp")));
        Assert.True(impRes.IsSuccess, impRes.Error?.Description);

        // 2. Standalone auto-detected Markdown quiz (no frontmatter, H1 heading + :::mcq)
        var standaloneQuizMd = """
            # Quick Auto Quiz

            :::mcq
            SI unit of force?
            - [x] Newton
            - [ ] Joule
            :::
            """;
        var standaloneRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: standaloneQuizMd, WorkingDirectory: wsDir));
        Assert.True(standaloneRes.IsSuccess, standaloneRes.Error?.Description);
        Assert.Equal("quiz", standaloneRes.Value.EntityKind);
        var loadedStandalone = await repo.FindQuizContainerAsync(standaloneRes.Value.TargetId);
        Assert.NotNull(loadedStandalone);
        Assert.Equal("Quick Auto Quiz", loadedStandalone!.Quiz.Name);
        Assert.Single(loadedStandalone.Quiz.Questions);
    }

    [Fact]
    public async Task MarkdownQuiz_StrictValidationErrors_CatchTyposAndInvalidMixing()
    {
        using var sp = CreateProvider("appdata_md_errors");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var wsDir = Path.Combine(_tempRoot, "ws_md_err");

        // 1. Unknown attribute typo on :::mcq gives hint
        var badAttr = """
            :::mcq{multy=true}
            Prompt?
            - [x] A
            - [ ] B
            :::
            """;
        var badAttrRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badAttr, WorkingDirectory: wsDir));
        Assert.False(badAttrRes.IsSuccess);
        Assert.Equal("Markdown.InvalidDirectiveAttr", badAttrRes.Error!.Code);
        Assert.Contains("multi", badAttrRes.Error.Hint);

        // 2. Unknown sub-section typo :::explain inside :::mcq
        var badSub = """
            :::mcq
            Prompt?
            - [x] A
            - [ ] B
            :::explain
            Wrong section name
            :::
            """;
        var badSubRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: badSub, WorkingDirectory: wsDir));
        Assert.False(badSubRes.IsSuccess);
        Assert.Equal("Markdown.UnknownSubSection", badSubRes.Error!.Code);

        // 3. Mixing article prose and quiz directives fails
        var mixed = """
            Regular article paragraph.

            :::mcq
            Prompt?
            - [x] A
            - [ ] B
            :::
            """;
        var mixedRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: mixed, WorkingDirectory: wsDir));
        Assert.False(mixedRes.IsSuccess);
        Assert.Equal("Markdown.MixedContentNotAllowed", mixedRes.Error!.Code);

        // 4. :::mcq inside explicit kind: article fails
        var mcqInArticle = """
            ---
            kind: article
            title: Pure Article
            ---

            :::mcq
            Prompt?
            - [x] A
            - [ ] B
            :::
            """;
        var mcqInArtRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: mcqInArticle, WorkingDirectory: wsDir));
        Assert.False(mcqInArtRes.IsSuccess);
        Assert.Equal("Markdown.InvalidDirectiveForKind", mcqInArtRes.Error!.Code);

        // 5. Stray prose in explicit kind: quiz fails
        var proseInQuiz = """
            ---
            kind: quiz
            title: Pure Quiz
            ---

            Stray prose paragraph.

            :::mcq
            Prompt?
            - [x] A
            - [ ] B
            :::
            """;
        var proseInQuizRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: proseInQuiz, WorkingDirectory: wsDir));
        Assert.False(proseInQuizRes.IsSuccess);
        Assert.Equal("Markdown.UnexpectedContentInQuiz", proseInQuizRes.Error!.Code);

        // 6. Unclosed :::mcq returns Markdown.UnclosedDirective; unclosed :::callout returns Markdown.UnclosedCallout
        var unclosedMcq = ":::mcq\nPrompt?\n- [x] A\n- [ ] B\n";
        var unclosedMcqRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: unclosedMcq, WorkingDirectory: wsDir));
        Assert.False(unclosedMcqRes.IsSuccess);
        Assert.Equal("Markdown.UnclosedDirective", unclosedMcqRes.Error!.Code);

        var unclosedCallout = ":::pearl\nMissing close\n";
        var unclosedCalloutRes = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: unclosedCallout, WorkingDirectory: wsDir));
        Assert.False(unclosedCalloutRes.IsSuccess);
        Assert.Equal("Markdown.UnclosedCallout", unclosedCalloutRes.Error!.Code);
    }
}
