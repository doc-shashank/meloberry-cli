using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Application.UseCases.Quizzes;
using Meloberry.Domain.Sessions;
using Meloberry.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class PersistenceAndPackagingTests : IDisposable
{
    private readonly WorkspaceTestFixture _fx = new("mby_persist_pkg");

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Sqlite_MigratesLegacyV13Db_And_ListWorkspacesIgnoresCorruptSubdirDb()
    {
        var wsRoot = _fx.SubDir("legacy_ws");
        var sessionId = "Session_20261120_100000_001";
        var sessionDir = Path.Combine(wsRoot, sessionId);
        Directory.CreateDirectory(Path.Combine(sessionDir, "assets"));
        var dbPath = Path.Combine(sessionDir, $"{sessionId}.db");

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

        // Corrupt extra .db inside storage subdir must not crash workspace listing
        var corruptDir = Path.Combine(wsRoot, "Set_corrupt_fake");
        Directory.CreateDirectory(corruptDir);
        await File.WriteAllTextAsync(Path.Combine(corruptDir, "Set_corrupt_fake.db"), "not-sqlite");

        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var list = scope.ServiceProvider.GetRequiredService<ListWorkspacesHandler>();

        var listed = await repo.ListWorkspacesAsync(wsRoot);
        Assert.Contains(listed, w => w.Id == sessionId);
        Assert.True((await list.HandleAsync(new ListWorkspacesQuery(wsRoot))).IsSuccess);

        var found = await repo.FindSessionAsync(sessionId);
        Assert.NotNull(found);
        var block = found!.Session.Components[0].ArticleBlocks[0];
        Assert.Equal("Legacy text content", block.TextContent);
        Assert.Null(block.PayloadJson);

        const string samplePayload = """{"version":2,"stickyFirstCol":true}""";
        Assert.True(block.SetPayloadJson(samplePayload).IsSuccess);
        await repo.SaveSessionAsync(found.Session);

        var reloaded = await repo.FindSessionAsync(sessionId);
        Assert.Equal(samplePayload, reloaded!.Session.Components[0].ArticleBlocks[0].PayloadJson);
        var inspected = await inspect.HandleAsync(new InspectEntityQuery($"{sessionId}#legacy-blk", InspectViewMode.Full));
        Assert.Equal(samplePayload, inspected.Value.ArticleBlock?.PayloadJson);

        // Verify PRAGMA user_version was upgraded to 2 and ls refreshes when external .db mtime changes
        await using (var verConn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await verConn.OpenAsync();
            await using var verCmd = verConn.CreateCommand();
            verCmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(2, Convert.ToInt32(await verCmd.ExecuteScalarAsync()));
        }

        var listedBefore = await repo.ListWorkspacesAsync(wsRoot);
        Assert.Equal(1, Assert.Single(listedBefore, w => w.Id == sessionId).ItemCount);

        await using (var mutConn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await mutConn.OpenAsync();
            await using var insCmd = mutConn.CreateCommand();
            insCmd.CommandText = "INSERT INTO Components VALUES ('Component_20261120_100000_009', 2, 'Article', NULL, '{}', 'Second', 'second-notes', NULL);";
            await insCmd.ExecuteNonQueryAsync();
        }
        File.SetLastWriteTimeUtc(dbPath, DateTime.UtcNow.AddMinutes(2));

        var listedAfter = await repo.ListWorkspacesAsync(wsRoot);
        Assert.Equal(2, Assert.Single(listedAfter, w => w.Id == sessionId).ItemCount);

        // Verify IX_EntityIndex_WorkspaceId secondary index exists in app_settings.db
        await using (var appConn = new SqliteConnection($"Data Source={Path.Combine(_fx.AppDataDir, "app_settings.db")};Pooling=False"))
        {
            await appConn.OpenAsync();
            await using var idxCmd = appConn.CreateCommand();
            idxCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'IX_EntityIndex_WorkspaceId';";
            Assert.Equal("IX_EntityIndex_WorkspaceId", (await idxCmd.ExecuteScalarAsync())?.ToString());
        }
    }

    [Fact]
    public async Task Mbypkg_V2RoundTripsAllComponentsAndAssets_And_ImportsV1AndBomArchives()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var srcDir = _fx.SubDir("pkg_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "img.png"));
        await File.WriteAllTextAsync(Path.Combine(srcDir, "_session.md"), "---\nkind: session\nname: All Components Pack\n---\n");
        await File.WriteAllTextAsync(Path.Combine(srcDir, "01-art.md"), """
            ---
            title: Article
            ---

            ![Diag](img.png "Fig. 1 $\Delta G$")

            :::pearl Pearl Title
            Pearl text.
            :::

            :::table{title="Tbl" hideableCols="2"}
            | A | B |
            |---|---|
            | 1 | 2 |
            :::

            :::diagram-explore{image="img.png" title="Expl"}
            boxes:
              - label: "Node"
                rect: [0.1, 0.1, 0.2, 0.2]
                note: "Important"
            :::

            :::reveal{title="Rev"}
            > Step 1
            Body
            :::

            :::link{id="c1" title="Card" image="img.png"}
            Card body
            :::

            See [Card](mby://card/c1).
            """);
        await File.WriteAllTextAsync(Path.Combine(srcDir, "02-quiz.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Quiz
            ---

            :::mcq
            Q?
            - [x] A
            - [ ] B
            :::

            :::flashcard
            F
            :::back
            B
            :::

            :::fill-blank
            Ans {{42}}.
            :::

            :::assertion-reason{answer="both-explain"}
            A: A
            R: R
            :::

            :::spot-error{image="img.png"}
            In [a], [~~b~~ -> c].
            :::

            :::diff{mode="spot-wrong"}
            | A | B |
            |---|---|
            | ~~1~~ -> 2 | 3 |
            :::

            :::mechanism
            1. Step 1
            2. Step 2
            :::

            :::relabel{image="img.png" mode="bank" style="blur"}
            boxes:
              - label: "Node"
                rect: [0.1, 0.1, 0.2, 0.2]
            :::

            :::classify
            [B1]
            - I1
            [B2]
            - I2
            :::

            :::match
            - L1 :: R1
            - L2 :: R2
            :::

            :::numeric{answer=10 tolerance=1}
            Num?
            :::

            :::case
            Stem.
            ::::mcq
            S1?
            - [x] X
            - [ ] Y
            ::::
            ::::numeric{answer=5 tolerance=1}
            S2?
            ::::
            :::
            """);

        var applied = (await apply.HandleAsync(new ApplySpecCommand(null, srcDir, WorkingDirectory: _fx.WorkDir))).Value;
        var pkgV2 = Path.Combine(_fx.TempRoot, "all_v2.mbypkg");
        Assert.True((await export.HandleAsync(new ExportPackageCommand(applied.TargetId, pkgV2, "android"))).IsSuccess);

        using (var zip = ZipFile.OpenRead(pkgV2))
        {
            using var manifestDoc = JsonDocument.Parse(await WorkspaceTestFixture.ReadZipEntryTextAsync(zip, "manifest.json"));
            Assert.Equal("2.0", manifestDoc.RootElement.GetProperty("packageVersion").GetString());
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("assets/img_", StringComparison.OrdinalIgnoreCase));
            var wsJson = await WorkspaceTestFixture.ReadZipEntryTextAsync(zip, "workspace.json");
            Assert.Contains("Pearl Title", wsJson);
        }

        // Re-import v2.0 + verify persisted fields
        var impV2Dir = _fx.SubDir("imp_v2");
        Assert.True((await import.HandleAsync(new ImportPackageCommand(pkgV2, impV2Dir))).IsSuccess);
        var loaded = (await repo.FindSessionAsync(applied.TargetId))!.Session;
        var callout = Assert.Single(loaded.Components[0].ArticleBlocks, b => b.Kind == ArticleBlockKind.Callout);
        Assert.Equal(CalloutTone.Pearl, callout.CalloutTone);
        Assert.Equal("Pearl Title", callout.CalloutTitle);
        Assert.Equal(12, loaded.Components[1].Quiz!.Questions.Count);

        // Export simple quiz for v2 null-omission + BOM import check
        var simple = (await apply.HandleAsync(new ApplySpecCommand(null, null,
            """{"kind":"quiz","quizType":"mcq","questions":[{"type":"mcq","text":"2+2?","options":[{"text":"4","isCorrect":true},{"text":"5"}]}]}""", _fx.WorkDir))).Value;
        var simplePkg = Path.Combine(_fx.TempRoot, "simple.mbypkg");
        Assert.True((await export.HandleAsync(new ExportPackageCommand(simple.TargetId, simplePkg, "android"))).IsSuccess);
        using (var zip = ZipFile.OpenRead(simplePkg))
        {
            var wsJson = await WorkspaceTestFixture.ReadZipEntryTextAsync(zip, "workspace.json");
            Assert.DoesNotContain("payloadJson", wsJson, StringComparison.OrdinalIgnoreCase);
        }

        var bomPkg = Path.Combine(_fx.TempRoot, "bom.mbypkg");
        var bomId = "Set_20261124_120000_991";
        await WorkspaceTestFixture.MutateZipAsync(simplePkg, bomPkg, async zip =>
        {
            var m = (await WorkspaceTestFixture.ReadZipEntryTextAsync(zip, "manifest.json")).Replace(simple.TargetId, bomId, StringComparison.Ordinal);
            var w = (await WorkspaceTestFixture.ReadZipEntryTextAsync(zip, "workspace.json")).Replace(simple.TargetId, bomId, StringComparison.Ordinal);
            await WorkspaceTestFixture.ReplaceZipEntryTextAsync(zip, "manifest.json", m, prependUtf8Bom: true);
            await WorkspaceTestFixture.ReplaceZipEntryTextAsync(zip, "workspace.json", w, prependUtf8Bom: true);
        });
        Assert.True((await import.HandleAsync(new ImportPackageCommand(bomPkg, _fx.SubDir("imp_bom")))).IsSuccess);

        // Legacy v1.0 import succeeds
        var pkgV1 = await WorkspaceTestFixture.WritePackageAsync(
            Path.Combine(_fx.TempRoot, "v1.mbypkg"), "quiz", "Set_20261120_100100_001",
            """{"id":"Set_20261120_100100_001","quizType":"Flashcard","name":"Legacy v1","questions":[{"id":"Q_20261120_100100_002","index":1,"questionType":"Flashcard","maxOptions":0,"isMultiCorrect":false,"options":[],"front":{"text":"Front","position":"TextBelowImage"},"back":{"text":"Back","position":"TextBelowImage"}}]}""",
            packageVersion: "1.0");
        var impV1 = await import.HandleAsync(new ImportPackageCommand(pkgV1, _fx.SubDir("imp_v1")));
        Assert.True(impV1.IsSuccess);
        Assert.Equal("Set_20261120_100100_001", impV1.Value.ImportedId);
    }

    [Fact]
    public async Task ImportPackage_ReturnsTargetPlatformAndIncludedLayout()
    {
        using var sp = _fx.CreateProvider("appdata_pkg_meta");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();

        var applied = (await apply.HandleAsync(new ApplySpecCommand(
            null,
            null,
            """{"kind":"quiz","quizType":"mcq","name":"Meta Quiz","questions":[{"type":"mcq","text":"1+1?","options":[{"text":"2","isCorrect":true},{"text":"3"}]}]}""",
            _fx.WorkDir))).Value;

        var androidPkg = Path.Combine(_fx.TempRoot, "android_with_layout.mbypkg");
        var desktopPkg = Path.Combine(_fx.TempRoot, "desktop_no_layout.mbypkg");
        Assert.True((await export.HandleAsync(new ExportPackageCommand(applied.TargetId, androidPkg, "android", IncludeLayout: true))).IsSuccess);
        Assert.True((await export.HandleAsync(new ExportPackageCommand(applied.TargetId, desktopPkg, "desktop", IncludeLayout: false))).IsSuccess);

        using var spImp1 = _fx.CreateProvider("appdata_pkg_meta_imp1");
        using var scopeImp1 = spImp1.CreateScope();
        var imp1 = await scopeImp1.ServiceProvider.GetRequiredService<ImportPackageHandler>()
            .HandleAsync(new ImportPackageCommand(androidPkg, _fx.SubDir("imp_android_meta")));
        Assert.True(imp1.IsSuccess, imp1.Error?.Description);
        Assert.Equal("android", imp1.Value.TargetPlatform);
        Assert.True(imp1.Value.IncludedLayout);

        using var spImp2 = _fx.CreateProvider("appdata_pkg_meta_imp2");
        using var scopeImp2 = spImp2.CreateScope();
        var imp2 = await scopeImp2.ServiceProvider.GetRequiredService<ImportPackageHandler>()
            .HandleAsync(new ImportPackageCommand(desktopPkg, _fx.SubDir("imp_desktop_meta")));
        Assert.True(imp2.IsSuccess, imp2.Error?.Description);
        Assert.Equal("desktop", imp2.Value.TargetPlatform);
        Assert.False(imp2.Value.IncludedLayout);
    }
    [Theory]
    [MemberData(nameof(InvalidPackageCases))]
    public async Task Mbypkg_Import_RejectsCorruptTamperedAndInvalidArchives(
        string fileName, string version, string kind, string id, string workspaceJson, string? layoutsJson, string? extraEntry, string expectedCode)
    {
        using var sp = _fx.CreateProvider("appdata_bad_pkg");
        using var scope = sp.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();

        var extra = extraEntry is null ? null : new[] { (extraEntry, "bad") };
        var pkgPath = await WorkspaceTestFixture.WritePackageAsync(
            Path.Combine(_fx.TempRoot, fileName), kind, id, workspaceJson, version, layoutsJson, extra);

        var res = await importer.HandleAsync(new ImportPackageCommand(pkgPath, _fx.SubDir("dest_" + Path.GetFileNameWithoutExtension(fileName))));
        Assert.False(res.IsSuccess);
        Assert.Equal(expectedCode, res.Error!.Code);
    }

    public static IEnumerable<object?[]> InvalidPackageCases()
    {
        yield return ["v3.mbypkg", "3.0", "quiz", "Set_20261120_100200_001", """{"id":"Set_20261120_100200_001","quizType":"Mcq","questions":[]}""", null, null, "Package.UnsupportedVersion"];
        yield return ["evil_id.mbypkg", "1.0", "quiz", "../escaped", """{"id":"../escaped","quizType":"Mcq","questions":[]}""", null, null, "Package.InvalidManifest"];
        yield return ["zipslip.mbypkg", "1.0", "quiz", "Set_20261120_000000_001", """{"id":"Set_20261120_000000_001","quizType":"Mcq","questions":[]}""", null, "../../pwn.txt", "Package.Invalid"];
        yield return ["bad_layouts.mbypkg", "1.0", "quiz", "Set_20261120_120000_001", """{"id":"Set_20261120_120000_001","quizType":"Mcq","questions":[]}""", """{"mcq":{"cardMaxWidthPx":"999999"}}""", null, "Layout.OutOfBounds"];
        yield return ["bad_table.mbypkg", "2.0", "session", "Session_20261124_010000_001", """{"id":"Session_20261124_010000_001","components":[{"id":"Component_20261124_010000_001","index":1,"kind":"Article","articleBlocks":[{"id":"ArtComp_20261124_010000_001","index":1,"kind":"Table","payloadJson":"{bad"}]}]}""", null, null, "Package.InvalidWorkspace"];
        yield return ["null_quiz.mbypkg", "2.0", "session", "Session_20261124_010000_002", """{"id":"Session_20261124_010000_002","components":[{"id":"Component_20261124_010000_002","index":1,"kind":"Quiz","quiz":null,"articleBlocks":[]}]}""", null, null, "Package.InvalidWorkspace"];
        yield return ["mismatch_quiz.mbypkg", "2.0", "session", "Session_20261124_010000_003", """{"id":"Session_20261124_010000_003","components":[{"id":"Component_20261124_010000_003","index":1,"kind":"Quiz","quiz":{"id":"Component_OTHER","quizType":"Mcq","questions":[]},"articleBlocks":[]}]}""", null, null, "Package.InvalidWorkspace"];
        yield return ["art_with_quiz.mbypkg", "2.0", "session", "Session_20261124_010000_004", """{"id":"Session_20261124_010000_004","components":[{"id":"Component_20261124_010000_004","index":1,"kind":"Article","quiz":{"id":"Component_20261124_010000_004","quizType":"Mcq","questions":[]},"articleBlocks":[]}]}""", null, null, "Package.InvalidWorkspace"];
        yield return ["bad_enum.mbypkg", "2.0", "quiz", "Set_20261124_010000_005", """{"id":"Set_20261124_010000_005","quizType":"999","questions":[]}""", null, null, "Package.InvalidWorkspace"];
        yield return ["bad_side_enum.mbypkg", "2.0", "quiz", "Set_20261124_010000_006", """{"id":"Set_20261124_010000_006","quizType":"Flashcard","questions":[{"id":"Q_20261124_010000_006","index":1,"questionType":"Flashcard","front":{"text":"F","position":"999"},"back":{"text":"B","position":"TextBelowImage"}}]}""", null, null, "Package.InvalidWorkspace"];
        yield return ["bad_diff.mbypkg", "2.0", "quiz", "Set_20261124_200000_001", """{"id":"Set_20261124_200000_001","quizType":"Mixed","questions":[{"id":"Q_20261124_200000_001","index":1,"questionType":"Differentiation","payloadJson":"{\"mode\":\"fill\",\"headers\":[\"A\",\"B\"],\"rows\":null}"}]}""", null, null, "Package.InvalidWorkspace"];
        yield return ["null_blanks.mbypkg", "2.0", "quiz", "Set_20261124_120000_992", """{"id":"Set_20261124_120000_992","quizType":"Mixed","questions":[{"id":"Q_20261124_120000_992","index":1,"questionType":"FillBlank","payloadJson":"{\"template\":\"Enzyme is {{PFK-1}}.\"}"}]}""", null, null, "Package.InvalidWorkspace"];
    }
}
