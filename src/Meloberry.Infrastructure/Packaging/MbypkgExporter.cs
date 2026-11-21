using System.IO.Compression;
using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Layouts;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;
using Meloberry.Infrastructure.Persistence;
using Meloberry.Infrastructure.Services;

namespace Meloberry.Infrastructure.Packaging;

public sealed class MbypkgExporter(
    IWorkspaceRepository workspaceRepository,
    ILayoutRepository layoutRepository,
    ChainTransactionManager? transactionManager = null) : IPackageExporter
{
    private const long MaxEntryBytes = 50L * 1024 * 1024;
    private const long MaxTotalBytes = 250L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task<Result<string>> ExportSessionAsync(
        StudySession session, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputPath, string targetPlatform = "desktop", bool includeLayouts = false, CancellationToken cancellationToken = default) =>
        WritePackageAsync(session.Id, "session", storageDirectory, MapSessionToDto(session, includeLayouts), layouts,
            CollectSessionAssets(session), outputPath, targetPlatform, includeLayouts, cancellationToken);

    public Task<Result<string>> ExportQuizAsync(
        Quiz quiz, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputPath, string targetPlatform = "desktop", bool includeLayouts = false, CancellationToken cancellationToken = default) =>
        WritePackageAsync(quiz.Id, "quiz", storageDirectory, MapQuizToDto(quiz, includeLayouts), layouts,
            CollectQuizAssets(quiz), outputPath, targetPlatform, includeLayouts, cancellationToken);

    public async Task<Result<WorkspaceListItem>> ImportPackageAsync(
        string packagePath, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            return new Error("Package.NotFound", $"Package '{packagePath}' was not found.", ErrorType.NotFound);

        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = archive.GetEntry("manifest.json");
        var workspaceEntry = archive.GetEntry("workspace.json");
        if (manifestEntry is null || workspaceEntry is null)
            return new Error("Package.Invalid", "Invalid .mbypkg archive: missing manifest.json or workspace.json.", ErrorType.Validation);

        long totalBytesRead = 0;

        var manifestRes = await ReadBoundedJsonAsync<PackageManifest>(manifestEntry, "manifest.json", "Package.InvalidManifest", totalBytesRead, cancellationToken);
        if (!manifestRes.IsSuccess) return manifestRes.Error!;
        totalBytesRead += manifestRes.Value.BytesRead;
        var manifest = manifestRes.Value.Data;

        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Kind))
            return new Error("Package.InvalidManifest", "Package manifest.json is corrupted.", ErrorType.Validation);
        if (!IsSafeSingleSegmentId(manifest.Id))
            return new Error("Package.InvalidManifest", $"Unsafe workspace ID in manifest.json: '{manifest.Id}'.", ErrorType.Validation);

        if (!PathConfinement.TryResolveInside(destinationDirectory, manifest.Id, out var targetStorageDir))
            return new Error("Package.InvalidManifest", $"Workspace ID '{manifest.Id}' escapes destination directory.", ErrorType.Validation);

        var destRootFull = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.GetParent(targetStorageDir)?.FullName.Equals(destRootFull, StringComparison.OrdinalIgnoreCase) != true)
            return new Error("Package.InvalidManifest", $"Workspace ID '{manifest.Id}' escapes destination directory.", ErrorType.Validation);
        if (Directory.Exists(targetStorageDir))
            return new Error("Package.TargetExists", $"Destination workspace '{targetStorageDir}' already exists.", ErrorType.Conflict);

        Dictionary<string, Dictionary<string, string>>? layoutMap = null;
        var layoutsEntry = archive.GetEntry("layouts.json");
        if (layoutsEntry is not null)
        {
            var lRes = await ReadBoundedJsonAsync<Dictionary<string, Dictionary<string, string>>>(layoutsEntry, "layouts.json", "Package.InvalidLayouts", totalBytesRead, cancellationToken);
            if (!lRes.IsSuccess) return lRes.Error!;
            totalBytesRead += lRes.Value.BytesRead;
            layoutMap = lRes.Value.Data;
        }

        StudySession? importedSession = null;
        Quiz? importedQuiz = null;
        if (string.Equals(manifest.Kind, "session", StringComparison.OrdinalIgnoreCase))
        {
            var sRes = await ReadBoundedJsonAsync<SessionPackageDto>(workspaceEntry, "workspace.json", "Package.InvalidWorkspace", totalBytesRead, cancellationToken);
            if (!sRes.IsSuccess) return sRes.Error!;
            totalBytesRead += sRes.Value.BytesRead;
            var sDto = sRes.Value.Data;
            if (!IsSafeSingleSegmentId(sDto.Id) || !string.Equals(sDto.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
                return new Error("Package.InvalidWorkspace", "Failed to deserialize session workspace.json or workspace ID is invalid.", ErrorType.Validation);

            var sessionRes = RehydrateSessionSafe(sDto, targetStorageDir);
            if (!sessionRes.IsSuccess) return sessionRes.Error!;
            importedSession = sessionRes.Value;
        }
        else if (string.Equals(manifest.Kind, "quiz", StringComparison.OrdinalIgnoreCase))
        {
            var qRes = await ReadBoundedJsonAsync<QuizPackageDto>(workspaceEntry, "workspace.json", "Package.InvalidWorkspace", totalBytesRead, cancellationToken);
            if (!qRes.IsSuccess) return qRes.Error!;
            totalBytesRead += qRes.Value.BytesRead;
            var qDto = qRes.Value.Data;
            if (!IsSafeSingleSegmentId(qDto.Id) || !string.Equals(qDto.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
                return new Error("Package.InvalidWorkspace", "Failed to deserialize quiz workspace.json or workspace ID is invalid.", ErrorType.Validation);

            var quizRes = RehydrateQuizSafe(qDto, targetStorageDir);
            if (!quizRes.IsSuccess) return quizRes.Error!;
            importedQuiz = quizRes.Value;
        }
        else
        {
            return new Error("Package.InvalidManifest", $"Unsupported package kind '{manifest.Kind}'.", ErrorType.Validation);
        }

        var stagingDir = Path.Combine(destRootFull, $".import_staging_{Guid.NewGuid():N}");
        var stagingAssetsDir = Path.Combine(stagingDir, "assets");
        Directory.CreateDirectory(stagingAssetsDir);
        bool movedToTarget = false;

        try
        {
            foreach (var entry in archive.Entries)
            {
                var normName = entry.FullName.Replace('\\', '/');
                if (normName.StartsWith('/') || normName.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s is "." or ".."))
                    return new Error("Package.Invalid", $"Unsafe path in archive: '{entry.FullName}'.", ErrorType.Validation);
                if (!normName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(entry.Name))
                    continue;

                var assetFileName = Path.GetFileName(normName);
                if (!PathConfinement.TryResolveInside(stagingAssetsDir, assetFileName, out var outFile))
                    return new Error("Package.Invalid", $"Asset path escapes target directory: '{entry.FullName}'.", ErrorType.Validation);

                var bytesRes = await ReadEntryBytesBoundedAsync(entry, totalBytesRead, cancellationToken);
                if (!bytesRes.IsSuccess) return bytesRes.Error!;
                totalBytesRead += bytesRes.Value.Length;

                if (!FileContentInspector.TryDetectImageExtension(bytesRes.Value, out _))
                    return new Error("Package.InvalidAsset", $"Archive asset '{entry.FullName}' failed image verification.", ErrorType.Validation);

                await File.WriteAllBytesAsync(outFile, bytesRes.Value, cancellationToken);
            }

            if (Directory.Exists(targetStorageDir))
                return new Error("Package.TargetExists", $"Destination workspace '{targetStorageDir}' already exists.", ErrorType.Conflict);

            transactionManager?.NotifyWorkspaceMutating(targetStorageDir);
            Directory.Move(stagingDir, targetStorageDir);
            movedToTarget = true;

            if (layoutMap is not null)
            {
                foreach (var (targetType, values) in layoutMap)
                    if (LayoutBoundsCatalog.IsValidTargetType(targetType))
                        await layoutRepository.SaveProfileAsync(LayoutProfile.Rehydrate(targetType, values), cancellationToken);
            }

            if (importedSession is not null)
            {
                await workspaceRepository.CreateSessionAsync(importedSession, cancellationToken);
                return new WorkspaceListItem(importedSession.Id, "session", "session", targetStorageDir, importedSession.Components.Count);
            }

            await workspaceRepository.CreateQuizAsync(importedQuiz!, cancellationToken);
            return new WorkspaceListItem(importedQuiz!.Id, "quiz", importedQuiz.Type.ToString().ToLowerInvariant(), targetStorageDir, importedQuiz.Questions.Count);
        }
        catch
        {
            if (movedToTarget && Directory.Exists(targetStorageDir))
                try { Directory.Delete(targetStorageDir, recursive: true); } catch { }
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }

    private static async Task<Result<(T Data, long BytesRead)>> ReadBoundedJsonAsync<T>(
        ZipArchiveEntry entry, string entryLabel, string errorCode, long currentTotalBytes, CancellationToken ct)
    {
        var bytesRes = await ReadEntryBytesBoundedAsync(entry, currentTotalBytes, ct);
        if (!bytesRes.IsSuccess) return bytesRes.Error!;
        try
        {
            var data = JsonSerializer.Deserialize<T>(bytesRes.Value, JsonOptions);
            return data is null
                ? new Error(errorCode, $"Package {entryLabel} is empty or corrupted.", ErrorType.Validation)
                : (data, (long)bytesRes.Value.Length);
        }
        catch (JsonException ex)
        {
            return new Error(errorCode, $"Package {entryLabel} is invalid JSON: {ex.Message}", ErrorType.Validation);
        }
    }

    private static async Task<Result<byte[]>> ReadEntryBytesBoundedAsync(ZipArchiveEntry entry, long currentTotalBytes, CancellationToken ct)
    {
        if (entry.Length > MaxEntryBytes)
            return new Error("Package.EntryTooLarge", $"Archive entry '{entry.FullName}' exceeds {MaxEntryBytes / (1024 * 1024)} MB limit.", ErrorType.Validation);

        await using var stream = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long entryBytes = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            entryBytes += read;
            if (entryBytes > MaxEntryBytes)
                return new Error("Package.EntryTooLarge", $"Archive entry '{entry.FullName}' exceeds {MaxEntryBytes / (1024 * 1024)} MB limit.", ErrorType.Validation);
            if (currentTotalBytes + entryBytes > MaxTotalBytes)
                return new Error("Package.ArchiveTooLarge", $"Archive exceeds {MaxTotalBytes / (1024 * 1024)} MB decompressed limit.", ErrorType.Validation);
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static bool IsSafeSingleSegmentId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id is not ("." or "..") &&
        !Path.IsPathRooted(id) &&
        !id.Contains('/') && !id.Contains('\\') &&
        !id.Contains("..", StringComparison.Ordinal) &&
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsSafeRelativeAssetPath(string? path, string storageDir) =>
        string.IsNullOrWhiteSpace(path) ||
        (path.Replace('\\', '/').StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
         PathConfinement.TryResolveInside(storageDir, path, out _));

    private static Result<Dictionary<string, string>?> ValidateOverrides(IReadOnlyList<string> targets, Dictionary<string, string>? raw)
    {
        if (raw is null || raw.Count == 0) return (Dictionary<string, string>?)null;
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in raw)
        {
            var res = LayoutBoundsCatalog.ValidateAndResolve(targets, k, v);
            if (!res.IsSuccess) return res.Error!;
            clean[res.Value.CanonicalName] = res.Value.Value;
        }
        return clean;
    }

    private static async Task<Result<string>> WritePackageAsync<TWorkspace>(
        string id, string kind, string storageDirectory, TWorkspace workspaceDto,
        IReadOnlyDictionary<string, LayoutProfile> layouts, IReadOnlyList<string> assetRelativePaths,
        string? outputPath, string targetPlatform, bool includeLayouts, CancellationToken cancellationToken)
    {
        var normalizedPlatform = string.Equals(targetPlatform, "android", StringComparison.OrdinalIgnoreCase) ? "android" : "desktop";
        var resolvedOut = ResolvePackageOutputPath(id, storageDirectory, outputPath);
        var outDir = Path.GetDirectoryName(resolvedOut);
        if (!string.IsNullOrWhiteSpace(outDir)) Directory.CreateDirectory(outDir);

        var distinctAssets = assetRelativePaths.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var resolvedAssets = new List<string>(distinctAssets.Count);

        foreach (var relAsset in distinctAssets)
        {
            if (!PathConfinement.TryResolveInside(storageDirectory, relAsset, out var src))
                return new Error("Asset.InvalidPath", $"Asset path escapes or is invalid for workspace: '{relAsset}'.", ErrorType.Validation);
            if (!File.Exists(src))
                return new Error("Export.MissingAsset", $"Referenced asset '{src}' is missing.", ErrorType.Validation);
            resolvedAssets.Add(src);
        }

        var manifest = new PackageManifest("1.0", kind, id, DateTimeOffset.UtcNow.ToString("O"), distinctAssets, normalizedPlatform);
        var tempOut = Path.Combine(outDir ?? Directory.GetCurrentDirectory(), $".{Path.GetFileName(resolvedOut)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var fileStream = new FileStream(tempOut, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteJsonEntryAsync(archive, "manifest.json", manifest, cancellationToken);
                await WriteJsonEntryAsync(archive, "workspace.json", workspaceDto, cancellationToken);
                if (includeLayouts)
                {
                    var layoutDict = layouts.ToDictionary(kv => kv.Key, kv => kv.Value.Values.ToDictionary(x => x.Key, x => x.Value), StringComparer.OrdinalIgnoreCase);
                    await WriteJsonEntryAsync(archive, "layouts.json", layoutDict, cancellationToken);
                }
                foreach (var src in resolvedAssets)
                {
                    var entry = archive.CreateEntry($"assets/{Path.GetFileName(src)}", CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await using var srcStream = File.OpenRead(src);
                    await srcStream.CopyToAsync(entryStream, cancellationToken);
                }
            }

            File.Move(tempOut, resolvedOut, overwrite: true);
            return resolvedOut;
        }
        finally
        {
            if (File.Exists(tempOut))
                try { File.Delete(tempOut); } catch { }
        }
    }

    private static string ResolvePackageOutputPath(string id, string storageDirectory, string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return Path.Combine(Directory.GetParent(storageDirectory)?.FullName ?? Directory.GetCurrentDirectory(), $"{id}.mbypkg");

        var full = Path.GetFullPath(outputPath);
        if (Directory.Exists(full)) return Path.Combine(full, $"{id}.mbypkg");
        return full.EndsWith(".mbypkg", StringComparison.OrdinalIgnoreCase) ? full : $"{full}.mbypkg";
    }

    private static async Task WriteJsonEntryAsync<T>(ZipArchive archive, string entryName, T payload, CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, ct);
    }

    internal static List<string> CollectSessionAssets(StudySession session)
    {
        var assets = new List<string>();
        foreach (var comp in session.Components)
        {
            if (comp.Kind == ComponentKind.Article)
                assets.AddRange(comp.ArticleBlocks.Where(b => b.Kind == ArticleBlockKind.Image && !string.IsNullOrWhiteSpace(b.ImagePath)).Select(b => b.ImagePath!));
            else if (comp.Quiz is not null)
                assets.AddRange(CollectQuizAssets(comp.Quiz));
        }
        return assets;
    }

    internal static List<string> CollectQuizAssets(Quiz quiz)
    {
        var assets = new List<string>();
        foreach (var q in quiz.Questions)
        {
            if (q is McqQuestion mcq)
            {
                if (!string.IsNullOrWhiteSpace(mcq.ImagePath)) assets.Add(mcq.ImagePath);
                assets.AddRange(mcq.Options.Where(o => !string.IsNullOrWhiteSpace(o.ImagePath)).Select(o => o.ImagePath!));
            }
            else if (q is FlashcardQuestion card)
            {
                if (!string.IsNullOrWhiteSpace(card.Front.ImagePath)) assets.Add(card.Front.ImagePath);
                if (!string.IsNullOrWhiteSpace(card.Back.ImagePath)) assets.Add(card.Back.ImagePath);
            }
        }
        return assets;
    }

    private static SessionPackageDto MapSessionToDto(StudySession session, bool includeLayouts) =>
        new(session.Id, session.ActiveComponentId, session.Components.Select(c => new ComponentPackageDto(
            c.Id, c.Index, c.Kind.ToString(), c.Quiz is null ? null : MapQuizToDto(c.Quiz, includeLayouts),
            c.ArticleBlocks.Select(b => new ArticleBlockPackageDto(b.Id, b.Index, b.Kind.ToString(), b.TextContent, b.ImagePath)).ToList(),
            includeLayouts ? c.LayoutOverrides.ToDictionary(x => x.Key, x => x.Value) : null,
            c.Title)).ToList(),
            session.Name, session.Description);

    private static QuizPackageDto MapQuizToDto(Quiz quiz, bool includeLayouts) =>
        new(quiz.Id, quiz.Type.ToString(), quiz.Questions.Select(q => q switch
        {
            McqQuestion m => new QuestionPackageDto(m.Id, m.Index, "Mcq", m.Text, m.MaxOptions, m.IsMultiCorrect,
                m.Options.Select(o => new OptionPackageDto(o.Index, o.Text, o.ImagePath, o.IsCorrect)).ToList(),
                null, null, includeLayouts ? m.LayoutOverrides.ToDictionary(x => x.Key, x => x.Value) : null,
                m.ImagePath, m.Explanation),
            FlashcardQuestion f => new QuestionPackageDto(f.Id, f.Index, "Flashcard", null, 0, false, [],
                new SidePackageDto(f.Front.Text, f.Front.ImagePath, f.Front.Position.ToString()),
                new SidePackageDto(f.Back.Text, f.Back.ImagePath, f.Back.Position.ToString()),
                includeLayouts ? f.LayoutOverrides.ToDictionary(x => x.Key, x => x.Value) : null),
            _ => throw new InvalidOperationException("Unsupported question type.")
        }).ToList(),
        string.IsNullOrWhiteSpace(quiz.Name) ? null : quiz.Name,
        string.IsNullOrWhiteSpace(quiz.Description) ? null : quiz.Description);

    private static Result<StudySession> RehydrateSessionSafe(SessionPackageDto dto, string storageDir)
    {
        if (dto.Components is null)
            return new Error("Package.InvalidWorkspace", "Session workspace.json is missing components.", ErrorType.Validation);

        var comps = new List<SessionComponent>(dto.Components.Count);
        foreach (var c in dto.Components)
        {
            if (!IsSafeSingleSegmentId(c.Id) || c.Index < 1)
                return new Error("Package.InvalidWorkspace", $"Invalid component ID or index in workspace.json: '{c.Id}'.", ErrorType.Validation);
            if (!Enum.TryParse<ComponentKind>(c.Kind, true, out var kind))
                return new Error("Package.InvalidWorkspace", $"Unsupported component kind '{c.Kind}'.", ErrorType.Validation);

            Quiz? quiz = null;
            if (c.Quiz is not null)
            {
                if (!IsSafeSingleSegmentId(c.Quiz.Id))
                    return new Error("Package.InvalidWorkspace", $"Invalid component quiz ID '{c.Quiz.Id}'.", ErrorType.Validation);
                var qRes = RehydrateQuizSafe(c.Quiz, storageDir);
                if (!qRes.IsSuccess) return qRes.Error!;
                quiz = qRes.Value;
            }

            var blocks = new List<ArticleBlock>((c.ArticleBlocks ?? []).Count);
            foreach (var b in c.ArticleBlocks ?? [])
            {
                if (!IsSafeSingleSegmentId(b.Id) || b.Index < 1)
                    return new Error("Package.InvalidWorkspace", $"Invalid article block ID or index: '{b.Id}'.", ErrorType.Validation);
                if (!Enum.TryParse<ArticleBlockKind>(b.Kind, true, out var bKind))
                    return new Error("Package.InvalidWorkspace", $"Unsupported article block kind '{b.Kind}'.", ErrorType.Validation);
                if (!IsSafeRelativeAssetPath(b.ImagePath, storageDir))
                    return new Error("Package.InvalidWorkspace", $"Unsafe article imagePath '{b.ImagePath}'.", ErrorType.Validation);

                blocks.Add(ArticleBlock.Rehydrate(b.Id, c.Id, b.Index, bKind, b.TextContent, b.ImagePath));
            }

            IReadOnlyList<string> compTargets = kind == ComponentKind.Article
                ? ["article"]
                : quiz?.Type switch { QuizType.Flashcard => ["flashcard"], QuizType.Mixed => ["mcq", "flashcard"], _ => ["mcq"] };
            var ovRes = ValidateOverrides(compTargets, c.LayoutOverrides);
            if (!ovRes.IsSuccess) return ovRes.Error!;

            comps.Add(SessionComponent.Rehydrate(c.Id, dto.Id, c.Index, kind, quiz, blocks, ovRes.Value, c.Title));
        }

        return StudySession.Rehydrate(dto.Id, storageDir, dto.ActiveComponentId, comps, dto.Name, dto.Description);
    }

    private static Result<Quiz> RehydrateQuizSafe(QuizPackageDto dto, string storageDir)
    {
        if (!Enum.TryParse<QuizType>(dto.QuizType, true, out var qType))
            return new Error("Package.InvalidWorkspace", $"Unsupported quiz type '{dto.QuizType}'.", ErrorType.Validation);

        var questions = new List<Question>((dto.Questions ?? []).Count);
        foreach (var q in dto.Questions ?? [])
        {
            if (!IsSafeSingleSegmentId(q.Id) || q.Index < 1)
                return new Error("Package.InvalidWorkspace", $"Invalid question ID or index: '{q.Id}'.", ErrorType.Validation);

            if (string.Equals(q.QuestionType, "Mcq", StringComparison.OrdinalIgnoreCase))
            {
                var ovRes = ValidateOverrides(["mcq"], q.LayoutOverrides);
                if (!ovRes.IsSuccess) return ovRes.Error!;
                if (!IsSafeRelativeAssetPath(q.PromptImagePath, storageDir))
                    return new Error("Package.InvalidWorkspace", $"Unsafe MCQ prompt imagePath '{q.PromptImagePath}'.", ErrorType.Validation);

                var opts = new List<McqOption>();
                foreach (var o in q.Options ?? [])
                {
                    if (o.Index < 1)
                        return new Error("Package.InvalidWorkspace", $"Invalid MCQ option index {o.Index} on '{q.Id}'.", ErrorType.Validation);
                    if (!IsSafeRelativeAssetPath(o.ImagePath, storageDir))
                        return new Error("Package.InvalidWorkspace", $"Unsafe option imagePath '{o.ImagePath}'.", ErrorType.Validation);
                    opts.Add(McqOption.Create(o.Index, o.Text, o.ImagePath, o.IsCorrect));
                }

                questions.Add(McqQuestion.Rehydrate(q.Id, dto.Id, q.Index, q.PromptText ?? string.Empty,
                    q.MaxOptions is >= 2 and <= 26 ? q.MaxOptions : McqQuestion.DefaultMaxOptions, q.IsMultiCorrect, opts, ovRes.Value,
                    q.PromptImagePath, q.Explanation));
            }
            else if (string.Equals(q.QuestionType, "Flashcard", StringComparison.OrdinalIgnoreCase))
            {
                var ovRes = ValidateOverrides(["flashcard"], q.LayoutOverrides);
                if (!ovRes.IsSuccess) return ovRes.Error!;

                var frontRes = RehydrateSideSafe(q.Front, storageDir);
                if (!frontRes.IsSuccess) return frontRes.Error!;
                var backRes = RehydrateSideSafe(q.Back, storageDir);
                if (!backRes.IsSuccess) return backRes.Error!;

                questions.Add(FlashcardQuestion.Rehydrate(q.Id, dto.Id, q.Index, frontRes.Value, backRes.Value, ovRes.Value));
            }
            else
            {
                return new Error("Package.InvalidWorkspace", $"Unsupported question type '{q.QuestionType}'.", ErrorType.Validation);
            }
        }

        return Quiz.Rehydrate(dto.Id, qType, storageDir, questions, dto.Name, dto.Description);
    }

    private static Result<FlashcardSide> RehydrateSideSafe(SidePackageDto? side, string storageDir)
    {
        if (side is null) return FlashcardSide.Empty();
        if (!Enum.TryParse<MediaPosition>(side.Position, true, out var pos))
            return new Error("Package.InvalidWorkspace", $"Unsupported media position '{side.Position}'.", ErrorType.Validation);
        if (!IsSafeRelativeAssetPath(side.ImagePath, storageDir))
            return new Error("Package.InvalidWorkspace", $"Unsafe flashcard imagePath '{side.ImagePath}'.", ErrorType.Validation);
        return FlashcardSide.Rehydrate(side.Text, side.ImagePath, pos);
    }

    private sealed record PackageManifest(string PackageVersion, string Kind, string Id, string ExportedAtUtc, List<string> Assets, string TargetPlatform = "desktop");
    private sealed record SessionPackageDto(string Id, string? ActiveComponentId, List<ComponentPackageDto> Components, string? Name = null, string? Description = null);
    private sealed record ComponentPackageDto(string Id, int Index, string Kind, QuizPackageDto? Quiz, List<ArticleBlockPackageDto> ArticleBlocks, Dictionary<string, string>? LayoutOverrides, string? Title = null);
    private sealed record ArticleBlockPackageDto(string Id, int Index, string Kind, string? TextContent, string? ImagePath);
    private sealed record QuizPackageDto(string Id, string QuizType, List<QuestionPackageDto> Questions, string? Name = null, string? Description = null);
    private sealed record QuestionPackageDto(string Id, int Index, string QuestionType, string? PromptText, int MaxOptions, bool IsMultiCorrect, List<OptionPackageDto> Options, SidePackageDto? Front, SidePackageDto? Back, Dictionary<string, string>? LayoutOverrides, string? PromptImagePath = null, string? Explanation = null);
    private sealed record OptionPackageDto(int Index, string? Text, string? ImagePath, bool IsCorrect);
    private sealed record SidePackageDto(string? Text, string? ImagePath, string Position);
}
