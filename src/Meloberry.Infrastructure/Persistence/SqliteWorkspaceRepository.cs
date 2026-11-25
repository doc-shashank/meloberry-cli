using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;
using Microsoft.Data.Sqlite;

namespace Meloberry.Infrastructure.Persistence;

public sealed class SqliteWorkspaceRepository(
    SqliteLayoutRepository appRegistry,
    IWorkingDirectoryProvider workingDirectoryProvider,
    ChainTransactionManager? transactionManager = null) : IWorkspaceRepository
{
    private sealed record RegistryEntry(string WorkspaceId, string Kind, string SubType, string DbPath, string StorageDirectory);

    public async Task CreateSessionAsync(StudySession session, CancellationToken cancellationToken = default) =>
        await SaveSessionAsync(session, cancellationToken);

    public async Task SaveSessionAsync(StudySession session, CancellationToken cancellationToken = default)
    {
        var ownedIds = new List<string> { session.Id };
        await SaveWorkspaceCoreAsync(session.WorkingDirectory, session.Id, "session", "session", session.ActiveComponentId, session.Name, session.Description, ownedIds,
            async (conn, tx) =>
            {
                foreach (var comp in session.Components)
                {
                    ownedIds.Add(comp.Id);
                    var linksJson = comp.LinkCards.Count > 0 ? Phase1PayloadCodec.Serialize(comp.LinkCards) : null;
                    await ExecAsync(conn, tx, "INSERT INTO Components (Id, SortIndex, Kind, QuizType, LayoutOverridesJson, Title, RefKey, LinksJson) VALUES ($id, $idx, $kind, $qt, $lj, $title, $ref, $links);",
                        cancellationToken, ("$id", comp.Id), ("$idx", comp.Index), ("$kind", comp.Kind.ToString()),
                        ("$qt", comp.Quiz?.Type.ToString()), ("$lj", JsonSerializer.Serialize(comp.LayoutOverrides)), ("$title", comp.Title), ("$ref", comp.RefKey), ("$links", linksJson));

                    if (comp.Kind == ComponentKind.Article)
                    {
                        foreach (var b in comp.ArticleBlocks)
                        {
                            ownedIds.Add(b.Id);
                            string? toneStr = b.CalloutTone.HasValue ? ArticleBlock.CalloutToneToString(b.CalloutTone.Value) : null;
                            await ExecAsync(conn, tx,
                                "INSERT INTO ArticleBlocks (Id, ComponentId, SortIndex, BlockKind, TextContent, ImagePath, RefKey, Caption, Alt, CalloutTone, CalloutTitle, PayloadJson) VALUES ($id, $cid, $idx, $kind, $txt, $img, $ref, $cap, $alt, $tone, $ctitle, $pj);",
                                cancellationToken,
                                ("$id", b.Id), ("$cid", b.ComponentId), ("$idx", b.Index), ("$kind", b.Kind.ToString()),
                                ("$txt", b.TextContent), ("$img", b.ImagePath), ("$ref", b.RefKey),
                                ("$cap", b.Caption), ("$alt", b.Alt), ("$tone", toneStr), ("$ctitle", b.CalloutTitle), ("$pj", b.PayloadJson));
                        }
                    }
                    else if (comp.Quiz is not null)
                    {
                        foreach (var q in comp.Quiz.Questions)
                        {
                            ownedIds.Add(q.Id);
                            await InsertQuestionAsync(conn, tx, q, cancellationToken);
                        }
                    }
                }
            }, cancellationToken);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var lookup = await FindSessionAsync(sessionId, cancellationToken);
        if (lookup is not null) await RemoveWorkspaceFilesAndIndexAsync(sessionId, lookup.StorageDirectory, cancellationToken);
    }

    public async Task CreateQuizAsync(Quiz quiz, CancellationToken cancellationToken = default) =>
        await SaveQuizAsync(quiz, cancellationToken);

    public async Task SaveQuizAsync(Quiz quiz, CancellationToken cancellationToken = default)
    {
        var ownedIds = new List<string> { quiz.Id };
        await SaveWorkspaceCoreAsync(quiz.WorkingDirectory, quiz.Id, "quiz", quiz.Type.ToString().ToLowerInvariant(), null,
            string.IsNullOrWhiteSpace(quiz.Name) ? null : quiz.Name,
            string.IsNullOrWhiteSpace(quiz.Description) ? null : quiz.Description,
            ownedIds,
            async (conn, tx) =>
            {
                foreach (var q in quiz.Questions)
                {
                    ownedIds.Add(q.Id);
                    await InsertQuestionAsync(conn, tx, q, cancellationToken);
                }
            }, cancellationToken);
    }

    public async Task DeleteQuizAsync(string quizId, CancellationToken cancellationToken = default)
    {
        var lookup = await FindQuizContainerAsync(quizId, cancellationToken);
        if (lookup is not null && lookup.Session is null)
            await RemoveWorkspaceFilesAndIndexAsync(quizId, lookup.StorageDirectory, cancellationToken);
    }

    public Task SaveOwnerAsync(QuizOwnerContext owner, CancellationToken cancellationToken = default) =>
        owner.Session is not null ? SaveSessionAsync(owner.Session, cancellationToken) : SaveQuizAsync(owner.Quiz, cancellationToken);

    public async Task<SessionLookupContext?> FindSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryAsync(sessionId, byEntity: false, cancellationToken);
        if (entry is null || !string.Equals(entry.Kind, "session", StringComparison.OrdinalIgnoreCase)) return null;
        var session = await LoadSessionFromDbAsync(entry.DbPath, entry.StorageDirectory, cancellationToken);
        return session is null ? null : new SessionLookupContext(session, entry.StorageDirectory);
    }

    public async Task<ComponentLookupContext?> FindComponentAsync(string componentId, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryAsync(componentId, byEntity: true, cancellationToken);
        if (entry is null || !string.Equals(entry.Kind, "session", StringComparison.OrdinalIgnoreCase)) return null;
        var session = await LoadSessionFromDbAsync(entry.DbPath, entry.StorageDirectory, cancellationToken);
        var comp = session?.FindComponent(componentId);
        return session is null || comp is null ? null : new ComponentLookupContext(session, comp, entry.StorageDirectory);
    }

    public async Task<ArticleBlockLookupContext?> FindArticleBlockAsync(string artCompId, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryAsync(artCompId, byEntity: true, cancellationToken);
        if (entry is null || !string.Equals(entry.Kind, "session", StringComparison.OrdinalIgnoreCase)) return null;
        var session = await LoadSessionFromDbAsync(entry.DbPath, entry.StorageDirectory, cancellationToken);
        if (session is null) return null;

        foreach (var comp in session.Components.Where(c => c.Kind == ComponentKind.Article))
        {
            var block = comp.FindBlock(artCompId);
            if (block is not null) return new ArticleBlockLookupContext(session, comp, block, entry.StorageDirectory);
        }
        return null;
    }

    public async Task<QuizOwnerContext?> FindQuizContainerAsync(string quizOrComponentId, CancellationToken cancellationToken = default)
    {
        if (quizOrComponentId.StartsWith("Session_", StringComparison.Ordinal))
        {
            var s = await FindSessionAsync(quizOrComponentId, cancellationToken);
            var active = s?.Session.GetActiveComponent();
            return s is not null && active?.Kind == ComponentKind.Quiz && active.Quiz is not null
                ? new QuizOwnerContext(s.Session, active, active.Quiz, s.StorageDirectory) : null;
        }
        if (quizOrComponentId.StartsWith("Component_", StringComparison.Ordinal))
        {
            var c = await FindComponentAsync(quizOrComponentId, cancellationToken);
            return c is not null && c.Component.Kind == ComponentKind.Quiz && c.Component.Quiz is not null
                ? new QuizOwnerContext(c.Session, c.Component, c.Component.Quiz, c.StorageDirectory) : null;
        }

        var entry = await ResolveEntryAsync(quizOrComponentId, byEntity: false, cancellationToken);
        if (entry is null || !string.Equals(entry.Kind, "quiz", StringComparison.OrdinalIgnoreCase)) return null;
        var quiz = await LoadStandaloneQuizFromDbAsync(entry.DbPath, entry.StorageDirectory, cancellationToken);
        return quiz is null ? null : new QuizOwnerContext(null, null, quiz, entry.StorageDirectory);
    }

    public async Task<QuestionOwnerContext?> FindQuestionOwnerAsync(string questionId, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryAsync(questionId, byEntity: true, cancellationToken);
        if (entry is null) return null;

        if (string.Equals(entry.Kind, "session", StringComparison.OrdinalIgnoreCase))
        {
            var session = await LoadSessionFromDbAsync(entry.DbPath, entry.StorageDirectory, cancellationToken);
            if (session is null) return null;
            foreach (var comp in session.Components.Where(c => c.Kind == ComponentKind.Quiz && c.Quiz is not null))
            {
                var q = comp.Quiz!.FindQuestion(questionId);
                if (q is not null) return new QuestionOwnerContext(session, comp, comp.Quiz, q, entry.StorageDirectory);
            }
            return null;
        }

        var quiz = await LoadStandaloneQuizFromDbAsync(entry.DbPath, entry.StorageDirectory, cancellationToken);
        var foundQ = quiz?.FindQuestion(questionId);
        return quiz is not null && foundQ is not null ? new QuestionOwnerContext(null, null, quiz, foundQ, entry.StorageDirectory) : null;
    }

    public async Task<IReadOnlyList<WorkspaceListItem>> ListWorkspacesAsync(string? directoryFilter = null, bool includeAll = false, CancellationToken cancellationToken = default)
    {
        var scanDir = string.IsNullOrWhiteSpace(directoryFilter)
            ? workingDirectoryProvider.GetCurrentDirectory()
            : workingDirectoryProvider.NormalizeDirectoryPath(directoryFilter);

        await ScanDirectoryForWorkspacesAsync(scanDir, cancellationToken);
        await appRegistry.EnsureInitializedAsync(cancellationToken);

        var rows = new List<RegistryEntry>();
        var staleIds = new List<string>();
        await using (var regConn = appRegistry.OpenAppConnection())
        {
            await regConn.OpenAsync(cancellationToken);
            await using var cmd = regConn.CreateCommand();
            cmd.CommandText = "SELECT WorkspaceId, Kind, SubType, DbPath, StorageDirectory FROM WorkspaceRegistry ORDER BY WorkspaceId DESC;";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var e = new RegistryEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
                if (!File.Exists(e.DbPath)) staleIds.Add(e.WorkspaceId);
                else rows.Add(e);
            }
        }

        if (staleIds.Count > 0)
            await PruneStaleRegistryEntriesAsync(staleIds, cancellationToken);

        bool filterByScanDir = !string.IsNullOrWhiteSpace(directoryFilter) || !includeAll;
        var normScan = scanDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var items = new List<WorkspaceListItem>();
        foreach (var e in rows)
        {
            if (filterByScanDir)
            {
                var normStorage = Path.GetFullPath(e.StorageDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(normStorage, normScan, StringComparison.OrdinalIgnoreCase) &&
                    !normStorage.StartsWith(normScan + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            try { items.Add(new WorkspaceListItem(e.WorkspaceId, e.Kind, e.SubType, e.StorageDirectory, await CountWorkspaceItemsAsync(e.DbPath, e.Kind, cancellationToken))); }
            catch { }
        }
        return items;
    }

    private async Task PruneStaleRegistryEntriesAsync(List<string> staleIds, CancellationToken ct)
    {
        await using var conn = appRegistry.OpenAppConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        foreach (var id in staleIds)
        {
            await ExecAsync(conn, tx, "DELETE FROM EntityIndex WHERE WorkspaceId = $id;", ct, ("$id", id));
            await ExecAsync(conn, tx, "DELETE FROM WorkspaceRegistry WHERE WorkspaceId = $id;", ct, ("$id", id));
        }
        await tx.CommitAsync(ct);
    }

    private async Task SaveWorkspaceCoreAsync(
        string storageDir, string id, string kind, string subType, string? activeCompId, string? name, string? desc,
        List<string> ownedIds, Func<SqliteConnection, SqliteTransaction, Task> writeChildren, CancellationToken ct)
    {
        transactionManager?.NotifyWorkspaceMutating(storageDir);
        Directory.CreateDirectory(storageDir);
        Directory.CreateDirectory(Path.Combine(storageDir, "assets"));
        var dbPath = Path.Combine(storageDir, $"{id}.db");

        await using var conn = OpenWorkspaceConnection(dbPath);
        await conn.OpenAsync(ct);
        await EnsureWorkspaceSchemaAsync(conn, ct);

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await ExecAsync(conn, tx, "DELETE FROM McqOptions; DELETE FROM Questions; DELETE FROM ArticleBlocks; DELETE FROM Components; DELETE FROM WorkspaceMeta;", ct);
        await ExecAsync(conn, tx, "INSERT INTO WorkspaceMeta (Id, Kind, SubType, ActiveComponentId, Name, Description) VALUES ($id, $k, $st, $act, $n, $d);",
            ct, ("$id", id), ("$k", kind), ("$st", subType), ("$act", activeCompId), ("$n", name), ("$d", desc));

        await writeChildren(conn, tx);
        await tx.CommitAsync(ct);
        await RegisterWorkspaceAsync(id, kind, subType, dbPath, storageDir, ownedIds, ct);
    }

    private async Task<RegistryEntry?> ResolveEntryAsync(string id, bool byEntity, CancellationToken ct)
    {
        await appRegistry.EnsureInitializedAsync(ct);
        var found = await QueryRegistryAsync(id, byEntity, ct);
        if (found is not null && File.Exists(found.DbPath)) return found;

        await ScanDirectoryForWorkspacesAsync(workingDirectoryProvider.GetCurrentDirectory(), ct);
        found = await QueryRegistryAsync(id, byEntity, ct);
        return found is not null && File.Exists(found.DbPath) ? found : null;
    }

    private async Task<RegistryEntry?> QueryRegistryAsync(string id, bool byEntity, CancellationToken ct)
    {
        await using var conn = appRegistry.OpenAppConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = byEntity
            ? "SELECT w.WorkspaceId, w.Kind, w.SubType, w.DbPath, w.StorageDirectory FROM EntityIndex e INNER JOIN WorkspaceRegistry w ON w.WorkspaceId = e.WorkspaceId WHERE e.EntityId = $id;"
            : "SELECT WorkspaceId, Kind, SubType, DbPath, StorageDirectory FROM WorkspaceRegistry WHERE WorkspaceId = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new RegistryEntry(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)) : null;
    }

    private async Task ScanDirectoryForWorkspacesAsync(string rootDir, CancellationToken ct)
    {
        var dirsToScan = new List<string> { rootDir };
        var meloberryWorkspaces = Path.Combine(rootDir, ".meloberry", "workspaces");
        if (Directory.Exists(meloberryWorkspaces))
            dirsToScan.Add(meloberryWorkspaces);

        foreach (var scanDir in dirsToScan)
        {
            if (!Directory.Exists(scanDir)) continue;
            foreach (var subDir in Directory.EnumerateDirectories(scanDir))
            {
                var dirName = Path.GetFileName(subDir);
                if (!dirName.StartsWith("Session_", StringComparison.Ordinal) && !dirName.StartsWith("Set_", StringComparison.Ordinal))
                    continue;
                var candidateDb = Path.Combine(subDir, $"{dirName}.db");
                if (!File.Exists(candidateDb)) continue;
                try { await IndexExternalDatabaseAsync(candidateDb, subDir, ct); } catch { }
            }
        }
    }

    private async Task IndexExternalDatabaseAsync(string dbPath, string storageDir, CancellationToken ct)
    {
        await using var conn = OpenWorkspaceConnection(dbPath);
        await conn.OpenAsync(ct);

        string id, kind, subType;
        await using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText = "SELECT Id, Kind, SubType FROM WorkspaceMeta LIMIT 1;";
            await using var r = await metaCmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return;
            (id, kind, subType) = (r.GetString(0), r.GetString(1), r.GetString(2));
        }

        var entityIds = new List<string> { id };
        foreach (var table in new[] { "Components", "ArticleBlocks", "Questions" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Id FROM {table};";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) entityIds.Add(r.GetString(0));
        }
        await RegisterWorkspaceAsync(id, kind, subType, dbPath, storageDir, entityIds, ct);
    }

    private async Task RegisterWorkspaceAsync(
        string workspaceId, string kind, string subType, string dbPath, string storageDir, IEnumerable<string> entityIds, CancellationToken ct)
    {
        await appRegistry.EnsureInitializedAsync(ct);
        await using var conn = appRegistry.OpenAppConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await ExecAsync(conn, tx, """
            INSERT INTO WorkspaceRegistry (WorkspaceId, Kind, SubType, DbPath, StorageDirectory, UpdatedAtUtc)
            VALUES ($id, $k, $st, $db, $sd, $ts)
            ON CONFLICT(WorkspaceId) DO UPDATE SET Kind = excluded.Kind, SubType = excluded.SubType,
                DbPath = excluded.DbPath, StorageDirectory = excluded.StorageDirectory, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """, ct, ("$id", workspaceId), ("$k", kind), ("$st", subType), ("$db", dbPath), ("$sd", storageDir), ("$ts", DateTimeOffset.UtcNow.ToString("O")));

        await ExecAsync(conn, tx, "DELETE FROM EntityIndex WHERE WorkspaceId = $id;", ct, ("$id", workspaceId));
        foreach (var eid in entityIds.Distinct(StringComparer.Ordinal))
            await ExecAsync(conn, tx, "INSERT OR REPLACE INTO EntityIndex (EntityId, WorkspaceId) VALUES ($eid, $wid);", ct, ("$eid", eid), ("$wid", workspaceId));

        await tx.CommitAsync(ct);
    }

    private async Task RemoveWorkspaceFilesAndIndexAsync(string workspaceId, string storageDirectory, CancellationToken ct)
    {
        transactionManager?.NotifyWorkspaceMutating(storageDirectory);
        await appRegistry.EnsureInitializedAsync(ct);
        await using (var conn = appRegistry.OpenAppConnection())
        {
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await ExecAsync(conn, tx, "DELETE FROM EntityIndex WHERE WorkspaceId = $id; DELETE FROM WorkspaceRegistry WHERE WorkspaceId = $id;", ct, ("$id", workspaceId));
            await tx.CommitAsync(ct);
        }

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(storageDirectory)) Directory.Delete(storageDirectory, recursive: true);
    }

    private static SqliteConnection OpenWorkspaceConnection(string dbPath) => new($"Data Source={dbPath};Pooling=False");

    private static async Task EnsureWorkspaceSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        await ExecAsync(conn, null, """
            CREATE TABLE IF NOT EXISTS WorkspaceMeta (Id TEXT PRIMARY KEY, Kind TEXT NOT NULL, SubType TEXT NOT NULL, ActiveComponentId TEXT NULL, Name TEXT NULL, Description TEXT NULL);
            CREATE TABLE IF NOT EXISTS Components (Id TEXT PRIMARY KEY, SortIndex INTEGER NOT NULL, Kind TEXT NOT NULL, QuizType TEXT NULL, LayoutOverridesJson TEXT NULL, Title TEXT NULL, RefKey TEXT NULL, LinksJson TEXT NULL);
            CREATE TABLE IF NOT EXISTS ArticleBlocks (Id TEXT PRIMARY KEY, ComponentId TEXT NOT NULL, SortIndex INTEGER NOT NULL, BlockKind TEXT NOT NULL, TextContent TEXT NULL, ImagePath TEXT NULL, RefKey TEXT NULL, Caption TEXT NULL, Alt TEXT NULL, CalloutTone TEXT NULL, CalloutTitle TEXT NULL, PayloadJson TEXT NULL);
            CREATE TABLE IF NOT EXISTS Questions (Id TEXT PRIMARY KEY, ContainerId TEXT NOT NULL, SortIndex INTEGER NOT NULL, QuestionType TEXT NOT NULL, PromptText TEXT NULL, MaxOptions INTEGER NOT NULL, IsMultiCorrect INTEGER NOT NULL, FrontText TEXT NULL, FrontImagePath TEXT NULL, FrontPosition TEXT NULL, BackText TEXT NULL, BackImagePath TEXT NULL, BackPosition TEXT NULL, LayoutOverridesJson TEXT NULL, PromptImagePath TEXT NULL, Explanation TEXT NULL, RefKey TEXT NULL, PayloadJson TEXT NULL);
            CREATE TABLE IF NOT EXISTS McqOptions (QuestionId TEXT NOT NULL, OptionIndex INTEGER NOT NULL, OptionText TEXT NULL, ImagePath TEXT NULL, IsCorrect INTEGER NOT NULL, PRIMARY KEY (QuestionId, OptionIndex));
            """, ct);
        await EnsureTableColumnsAsync(conn, ct, "WorkspaceMeta", "Name", "Description");
        await EnsureTableColumnsAsync(conn, ct, "Components", "Title", "RefKey", "LinksJson");
        await EnsureTableColumnsAsync(conn, ct, "ArticleBlocks", "RefKey", "Caption", "Alt", "CalloutTone", "CalloutTitle", "PayloadJson");
        await EnsureTableColumnsAsync(conn, ct, "Questions", "PromptImagePath", "Explanation", "RefKey", "PayloadJson");
    }

    private static async Task EnsureTableColumnsAsync(SqliteConnection conn, CancellationToken ct, string tableName, params string[] columns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({tableName});";
            await using var r = await pragma.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) existing.Add(r.GetString(1));
        }
        foreach (var col in columns.Where(c => !existing.Contains(c)))
            await ExecAsync(conn, null, $"ALTER TABLE {tableName} ADD COLUMN {col} TEXT NULL;", ct);
    }

    private static async Task InsertQuestionAsync(SqliteConnection conn, SqliteTransaction tx, Question question, CancellationToken ct)
    {
        var mcq = question as McqQuestion;
        var card = question as FlashcardQuestion;
        await ExecAsync(conn, tx, """
            INSERT INTO Questions (Id, ContainerId, SortIndex, QuestionType, PromptText, MaxOptions, IsMultiCorrect,
                FrontText, FrontImagePath, FrontPosition, BackText, BackImagePath, BackPosition, LayoutOverridesJson, PromptImagePath, Explanation, RefKey, PayloadJson)
            VALUES ($id, $cid, $idx, $qt, $pt, $mo, $im, $ft, $fi, $fp, $bt, $bi, $bp, $lj, $pimg, $exp, $ref, $pj);
            """, ct,
            ("$id", question.Id), ("$cid", question.ContainerId), ("$idx", question.Index), ("$qt", question.Type.ToString()),
            ("$pt", mcq?.Text), ("$mo", mcq?.MaxOptions ?? 0), ("$im", mcq?.IsMultiCorrect == true ? 1 : 0),
            ("$ft", card?.Front.Text), ("$fi", card?.Front.ImagePath), ("$fp", card is null ? null : FormatSidePosition(card.Front)),
            ("$bt", card?.Back.Text), ("$bi", card?.Back.ImagePath), ("$bp", card is null ? null : FormatSidePosition(card.Back)),
            ("$lj", JsonSerializer.Serialize(question.LayoutOverrides)),
            ("$pimg", mcq?.ImagePath), ("$exp", mcq?.Explanation), ("$ref", question.RefKey), ("$pj", question.PayloadJson));

        if (mcq is not null)
        {
            foreach (var opt in mcq.Options)
                await ExecAsync(conn, tx, "INSERT INTO McqOptions (QuestionId, OptionIndex, OptionText, ImagePath, IsCorrect) VALUES ($qid, $idx, $txt, $img, $cor);",
                    ct, ("$qid", mcq.Id), ("$idx", opt.Index), ("$txt", opt.Text), ("$img", opt.ImagePath), ("$cor", opt.IsCorrect ? 1 : 0));
        }
    }

    private static string FormatSidePosition(FlashcardSide side) =>
        side.IsPositionExplicit ? $"Explicit:{side.Position}" : side.Position.ToString();

    private static (MediaPosition Pos, bool Explicit) ParseSidePosition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (MediaPosition.TextBelowImage, false);
        bool isExplicit = raw.StartsWith("Explicit:", StringComparison.OrdinalIgnoreCase);
        var val = isExplicit ? raw["Explicit:".Length..] : raw;
        return Enum.TryParse<MediaPosition>(val, true, out var pos) ? (pos, isExplicit) : (MediaPosition.TextBelowImage, false);
    }

    private static async Task<StudySession?> LoadSessionFromDbAsync(string dbPath, string storageDirectory, CancellationToken ct)
    {
        if (!File.Exists(dbPath)) return null;
        await using var conn = OpenWorkspaceConnection(dbPath);
        await conn.OpenAsync(ct);
        await EnsureTableColumnsAsync(conn, ct, "WorkspaceMeta", "Name", "Description");
        await EnsureTableColumnsAsync(conn, ct, "Components", "Title", "RefKey", "LinksJson");
        await EnsureTableColumnsAsync(conn, ct, "ArticleBlocks", "RefKey", "Caption", "Alt", "CalloutTone", "CalloutTitle", "PayloadJson");
        await EnsureTableColumnsAsync(conn, ct, "Questions", "PromptImagePath", "Explanation", "RefKey", "PayloadJson");

        string sessionId; string? activeId, name, desc;
        await using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText = "SELECT Id, ActiveComponentId, Name, Description FROM WorkspaceMeta LIMIT 1;";
            await using var r = await metaCmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            sessionId = r.GetString(0);
            activeId = r.IsDBNull(1) ? null : r.GetString(1);
            name = r.IsDBNull(2) ? null : r.GetString(2);
            desc = r.IsDBNull(3) ? null : r.GetString(3);
        }

        var blocksByComp = await LoadArticleBlocksAsync(conn, ct);
        var questionsByContainer = await LoadQuestionsAsync(conn, ct);
        var components = new List<SessionComponent>();

        await using (var compCmd = conn.CreateCommand())
        {
            compCmd.CommandText = "SELECT Id, SortIndex, Kind, QuizType, LayoutOverridesJson, Title, RefKey, LinksJson FROM Components ORDER BY SortIndex;";
            await using var r = await compCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var cid = r.GetString(0);
                var idx = Math.Max(1, r.GetInt32(1));
                var kind = Enum.TryParse<ComponentKind>(r.GetString(2), true, out var k) ? k : ComponentKind.Quiz;
                var qt = r.IsDBNull(3) ? null : r.GetString(3);
                var ov = DeserializeOverrides(r.IsDBNull(4) ? null : r.GetString(4));
                var title = r.IsDBNull(5) ? null : r.GetString(5);
                var refKey = r.IsDBNull(6) ? null : r.GetString(6);
                var rawLinks = r.IsDBNull(7) ? null : r.GetString(7);
                List<ArticleLinkCard>? linkCards = null;
                if (!string.IsNullOrWhiteSpace(rawLinks) && Phase1PayloadCodec.TryDeserialize<List<ArticleLinkCard>>(rawLinks, out var parsedLinks))
                    linkCards = parsedLinks;

                Quiz? quiz = null;
                if (kind == ComponentKind.Quiz)
                {
                    var qType = Enum.TryParse<QuizType>(qt, true, out var parsed) ? parsed : QuizType.Mixed;
                    quiz = Quiz.Rehydrate(cid, qType, storageDirectory, questionsByContainer.TryGetValue(cid, out var ql) ? ql : []);
                }
                components.Add(SessionComponent.Rehydrate(cid, sessionId, idx, kind, quiz, blocksByComp.TryGetValue(cid, out var bl) ? bl : [], ov, title, refKey, linkCards));
            }
        }
        return StudySession.Rehydrate(sessionId, storageDirectory, activeId, components, name, desc);
    }

    private static async Task<Quiz?> LoadStandaloneQuizFromDbAsync(string dbPath, string storageDirectory, CancellationToken ct)
    {
        if (!File.Exists(dbPath)) return null;
        await using var conn = OpenWorkspaceConnection(dbPath);
        await conn.OpenAsync(ct);
        await EnsureTableColumnsAsync(conn, ct, "WorkspaceMeta", "Name", "Description");
        await EnsureTableColumnsAsync(conn, ct, "Questions", "PromptImagePath", "Explanation", "RefKey", "PayloadJson");

        string quizId; QuizType quizType; string? name, desc;
        await using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText = "SELECT Id, SubType, Name, Description FROM WorkspaceMeta LIMIT 1;";
            await using var r = await metaCmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            quizId = r.GetString(0);
            quizType = Quiz.TryParseQuizType(r.GetString(1), out var parsed) ? parsed : QuizType.Mixed;
            name = r.IsDBNull(2) ? null : r.GetString(2);
            desc = r.IsDBNull(3) ? null : r.GetString(3);
        }
        var questionsByContainer = await LoadQuestionsAsync(conn, ct);
        return Quiz.Rehydrate(quizId, quizType, storageDirectory, questionsByContainer.TryGetValue(quizId, out var list) ? list : [], name, desc);
    }

    private static async Task<Dictionary<string, List<ArticleBlock>>> LoadArticleBlocksAsync(SqliteConnection conn, CancellationToken ct)
    {
        var map = new Dictionary<string, List<ArticleBlock>>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, ComponentId, SortIndex, BlockKind, TextContent, ImagePath, RefKey, Caption, Alt, CalloutTone, CalloutTitle, PayloadJson FROM ArticleBlocks ORDER BY SortIndex;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var compId = r.GetString(1);
            var kind = Enum.TryParse<ArticleBlockKind>(r.GetString(3), true, out var bk) ? bk : ArticleBlockKind.Text;
            CalloutTone? calloutTone = null;
            if (!r.IsDBNull(9) && ArticleBlock.TryParseCalloutTone(r.GetString(9), out var parsedTone))
                calloutTone = parsedTone;
            else if (kind == ArticleBlockKind.Callout)
                calloutTone = CalloutTone.Pearl;

            var block = ArticleBlock.Rehydrate(
                r.GetString(0), compId, Math.Max(1, r.GetInt32(2)), kind,
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                calloutTone,
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11));
            (map.TryGetValue(compId, out var list) ? list : map[compId] = []).Add(block);
        }
        return map;
    }

    private static async Task<Dictionary<string, List<Question>>> LoadQuestionsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var optionsByQ = new Dictionary<string, List<McqOption>>(StringComparer.Ordinal);
        await using (var optCmd = conn.CreateCommand())
        {
            optCmd.CommandText = "SELECT QuestionId, OptionIndex, OptionText, ImagePath, IsCorrect FROM McqOptions ORDER BY OptionIndex;";
            await using var r = await optCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var qid = r.GetString(0);
                var opt = McqOption.Create(Math.Max(1, r.GetInt32(1)), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4) == 1);
                (optionsByQ.TryGetValue(qid, out var list) ? list : optionsByQ[qid] = []).Add(opt);
            }
        }

        var map = new Dictionary<string, List<Question>>(StringComparer.Ordinal);
        await using (var qCmd = conn.CreateCommand())
        {
            qCmd.CommandText = "SELECT Id, ContainerId, SortIndex, QuestionType, PromptText, MaxOptions, IsMultiCorrect, FrontText, FrontImagePath, FrontPosition, BackText, BackImagePath, BackPosition, LayoutOverridesJson, PromptImagePath, Explanation, RefKey, PayloadJson FROM Questions ORDER BY SortIndex;";
            await using var r = await qCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var qid = r.GetString(0); var cid = r.GetString(1); var idx = Math.Max(1, r.GetInt32(2));
                var qType = Enum.TryParse<QuestionType>(r.GetString(3), true, out var qt) ? qt : QuestionType.Mcq;
                var ov = DeserializeOverrides(r.IsDBNull(13) ? null : r.GetString(13));
                var refKey = r.IsDBNull(16) ? null : r.GetString(16);
                var payloadJson = r.IsDBNull(17) ? null : r.GetString(17);
                Question q = qType switch
                {
                    QuestionType.Mcq => McqQuestion.Rehydrate(qid, cid, idx, r.IsDBNull(4) ? string.Empty : r.GetString(4),
                        r.GetInt32(5), r.GetInt32(6) == 1, optionsByQ.TryGetValue(qid, out var list) ? list : [], ov,
                        r.IsDBNull(14) ? null : r.GetString(14),
                        r.IsDBNull(15) ? null : r.GetString(15),
                        refKey),
                    QuestionType.Flashcard => RehydrateFlashcardRow(qid, cid, idx, r, ov, refKey),
                    QuestionType.FillBlank or QuestionType.AssertionReason or QuestionType.SpotError or QuestionType.Differentiation or QuestionType.Mechanism or QuestionType.RelabelImage or QuestionType.CaseGroup or QuestionType.Classify or QuestionType.Match or QuestionType.Numeric =>
                        PayloadQuestion.Rehydrate(qid, cid, idx, qType, payloadJson ?? "{}", ov, refKey),
                    _ => throw new InvalidOperationException($"Unsupported persisted question type '{qType}' (payload present: {payloadJson is not null}).")
                };
                (map.TryGetValue(cid, out var qList) ? qList : map[cid] = []).Add(q);
            }
        }
        return map;
    }

    private static FlashcardQuestion RehydrateFlashcardRow(
        string qid, string cid, int idx, SqliteDataReader r, Dictionary<string, string>? ov, string? refKey)
    {
        var (fp, fExp) = ParseSidePosition(r.IsDBNull(9) ? null : r.GetString(9));
        var (bp, bExp) = ParseSidePosition(r.IsDBNull(12) ? null : r.GetString(12));
        return FlashcardQuestion.Rehydrate(qid, cid, idx,
            FlashcardSide.Rehydrate(r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), fp, fExp),
            FlashcardSide.Rehydrate(r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), bp, bExp),
            ov, refKey);
    }

    private static Dictionary<string, string>? DeserializeOverrides(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return parsed is null ? null : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<int> CountWorkspaceItemsAsync(string dbPath, string kind, CancellationToken ct)
    {
        await using var conn = OpenWorkspaceConnection(dbPath);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = string.Equals(kind, "session", StringComparison.OrdinalIgnoreCase) ? "SELECT COUNT(*) FROM Components;" : "SELECT COUNT(*) FROM Questions;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ExecAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, val) in parameters) cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
