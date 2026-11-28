using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Layouts;
using Meloberry.Domain.Payloads;
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
    internal const string CurrentPackageVersion = "2.0";
    private static readonly HashSet<string> SupportedImportPackageVersions = new(StringComparer.Ordinal) { "1.0", "2.0" };

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

    public async Task<Result<PackageImportResult>> ImportPackageAsync(
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

        if (string.IsNullOrWhiteSpace(manifest.PackageVersion) || !SupportedImportPackageVersions.Contains(manifest.PackageVersion.Trim()))
            return new Error(
                "Package.UnsupportedVersion",
                $"Unsupported .mbypkg packageVersion '{manifest.PackageVersion ?? "<missing>"}'. Supported versions: {string.Join(", ", SupportedImportPackageVersions)}.",
                ErrorType.Validation,
                Hint: "Import a v1.0 or v2.0 .mbypkg archive, or update mby.");

        bool isLegacyV1 = string.Equals(manifest.PackageVersion.Trim(), "1.0", StringComparison.Ordinal);

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

        Dictionary<string, LayoutProfile>? validatedProfileMap = null;
        var layoutsEntry = archive.GetEntry("layouts.json");
        if (layoutsEntry is not null)
        {
            var lRes = await ReadBoundedJsonAsync<Dictionary<string, Dictionary<string, string>>>(layoutsEntry, "layouts.json", "Package.InvalidLayouts", totalBytesRead, cancellationToken);
            if (!lRes.IsSuccess) return lRes.Error!;
            totalBytesRead += lRes.Value.BytesRead;
            var layoutMap = lRes.Value.Data;
            validatedProfileMap = new Dictionary<string, LayoutProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (targetType, values) in layoutMap)
            {
                if (!LayoutBoundsCatalog.IsValidTargetType(targetType))
                    return new Error("Package.InvalidLayouts", $"Unsupported layout target '{targetType}' in layouts.json.", ErrorType.Validation);
                var profileRes = LayoutProfile.CreateDefault(targetType);
                if (!profileRes.IsSuccess) return profileRes.Error!;
                var profile = profileRes.Value;
                if (values is not null)
                {
                    foreach (var (prop, val) in values)
                    {
                        var setRes = profile.SetProperty(prop, val);
                        if (!setRes.IsSuccess) return setRes.Error!;
                    }
                }
                validatedProfileMap[profile.TargetType] = profile;
            }
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

            var sessionRes = RehydrateSessionSafe(sDto, targetStorageDir, isLegacyV1);
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

            var quizRes = RehydrateQuizSafe(qDto, targetStorageDir, isLegacyV1);
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

            if (validatedProfileMap is { Count: > 0 })
            {
                var existingProfiles = new Dictionary<string, LayoutProfile>(
                    await layoutRepository.GetAllProfilesAsync(cancellationToken),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var (target, importedProfile) in validatedProfileMap)
                    existingProfiles[target] = importedProfile;

                // Synchronize deck-global keys consistently across all 3 profiles using the canonical resolver
                var resolvedTheme = LayoutBoundsCatalog.ResolveDeckGlobalValue(validatedProfileMap, "themeMode", "berry");
                var resolvedSlideMs = LayoutBoundsCatalog.ResolveDeckGlobalValue(validatedProfileMap, "slideTransitionMs", "380");
                foreach (var target in LayoutBoundsCatalog.SupportedTargets)
                {
                    if (!existingProfiles.TryGetValue(target, out var p))
                    {
                        p = LayoutProfile.CreateDefault(target).Value;
                        existingProfiles[target] = p;
                    }
                    p.SetProperty("themeMode", resolvedTheme);
                    p.SetProperty("slideTransitionMs", resolvedSlideMs);
                }

                await layoutRepository.SaveProfilesAsync(existingProfiles.Values.ToList(), cancellationToken);
            }

            var targetPlatform = string.IsNullOrWhiteSpace(manifest.TargetPlatform)
                ? "desktop"
                : manifest.TargetPlatform.Trim().ToLowerInvariant();
            bool includedLayout = layoutsEntry is not null;

            if (importedSession is not null)
            {
                await workspaceRepository.CreateSessionAsync(importedSession, cancellationToken);
                return new PackageImportResult(
                    new WorkspaceListItem(importedSession.Id, "session", "session", targetStorageDir, importedSession.Components.Count),
                    targetPlatform,
                    includedLayout);
            }

            await workspaceRepository.CreateQuizAsync(importedQuiz!, cancellationToken);
            return new PackageImportResult(
                new WorkspaceListItem(importedQuiz!.Id, "quiz", importedQuiz.Type.ToString().ToLowerInvariant(), targetStorageDir, importedQuiz.Questions.Count),
                targetPlatform,
                includedLayout);
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
            ReadOnlySpan<byte> span = bytesRes.Value;
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                span = span[3..];
            var data = JsonSerializer.Deserialize<T>(span, JsonOptions);
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

    private static Result<Dictionary<string, string>?> ValidateOverrides(
        IReadOnlyList<string> targets, Dictionary<string, string>? raw, bool allowStripLegacyGlobals)
    {
        if (raw is null || raw.Count == 0) return (Dictionary<string, string>?)null;
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in raw)
        {
            if (allowStripLegacyGlobals && LayoutBoundsCatalog.IsGlobalOnlyProperty(k))
            {
                var legacyCheck = LayoutBoundsCatalog.ValidateAndResolve(targets, k, v);
                if (!legacyCheck.IsSuccess) return legacyCheck.Error!;
                continue;
            }
            var res = LayoutBoundsCatalog.ValidateAndResolveForEntityOverride(targets, k, v);
            if (!res.IsSuccess) return res.Error!;
            clean[res.Value.CanonicalName] = res.Value.Value;
        }
        return clean.Count > 0 ? clean : null;
    }

    private async Task<Result<string>> WritePackageAsync<TWorkspace>(
        string id, string kind, string storageDirectory, TWorkspace workspaceDto,
        IReadOnlyDictionary<string, LayoutProfile> layouts, IReadOnlyList<string> assetRelativePaths,
        string? outputPath, string targetPlatform, bool includeLayouts, CancellationToken cancellationToken)
    {
        var normalizedPlatform = string.Equals(targetPlatform, "android", StringComparison.OrdinalIgnoreCase) ? "android" : "desktop";
        var resolvedOut = ResolvePackageOutputPath(id, storageDirectory, outputPath);
        transactionManager?.NotifyOutputFileMutating(resolvedOut);
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

        var manifest = new PackageManifest(CurrentPackageVersion, kind, id, DateTimeOffset.UtcNow.ToString("O"), distinctAssets, normalizedPlatform);
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
            return Path.Combine(ResolveDefaultOutputBaseDirectory(storageDirectory), $"{id}.mbypkg");

        var trimmed = outputPath.Trim();
        bool endsWithDirSep = trimmed.EndsWith(Path.DirectorySeparatorChar) || trimmed.EndsWith(Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(trimmed);
        if (endsWithDirSep || Directory.Exists(full))
            return Path.Combine(full, $"{id}.mbypkg");

        return full.EndsWith(".mbypkg", StringComparison.OrdinalIgnoreCase) ? full : $"{full}.mbypkg";
    }

    internal static string ResolveDefaultOutputBaseDirectory(string storageDirectory)
    {
        var parent = Directory.GetParent(storageDirectory);
        if (parent is not null &&
            string.Equals(parent.Name, "workspaces", StringComparison.OrdinalIgnoreCase) &&
            parent.Parent is not null &&
            string.Equals(parent.Parent.Name, ".meloberry", StringComparison.OrdinalIgnoreCase) &&
            parent.Parent.Parent is not null)
        {
            return parent.Parent.Parent.FullName;
        }
        return parent?.FullName ?? Directory.GetCurrentDirectory();
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
            {
                List<string>? diagPayloadPaths = null;
                foreach (var b in comp.ArticleBlocks)
                {
                    if (b.Kind is ArticleBlockKind.Image or ArticleBlockKind.DiagramExplore && !string.IsNullOrWhiteSpace(b.ImagePath))
                        assets.Add(b.ImagePath);
                    if (b.Kind == ArticleBlockKind.DiagramExplore &&
                        Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(b.PayloadJson, out var dp) &&
                        !string.IsNullOrWhiteSpace(dp?.ImagePath))
                    {
                        (diagPayloadPaths ??= []).Add(dp.ImagePath);
                    }
                }
                if (diagPayloadPaths is not null)
                    assets.AddRange(diagPayloadPaths);
                foreach (var l in comp.LinkCards)
                {
                    if (!string.IsNullOrWhiteSpace(l.ImagePath))
                        assets.Add(l.ImagePath);
                }
            }
            else if (comp.Quiz is not null)
            {
                assets.AddRange(CollectQuizAssets(comp.Quiz));
            }
        }
        return assets;
    }

    internal static List<string> CollectQuizAssets(Quiz quiz)
    {
        var assets = new List<string>();
        foreach (var q in quiz.Questions)
            CollectQuestionAssets(q, assets);
        return assets;
    }

    private static void CollectQuestionAssets(Question q, List<string> assets)
    {
        if (q is McqQuestion mcq)
        {
            if (!string.IsNullOrWhiteSpace(mcq.ImagePath)) assets.Add(mcq.ImagePath);
            assets.AddRange(mcq.Options.Where(o => !string.IsNullOrWhiteSpace(o.ImagePath)).Select(o => o.ImagePath!));
            return;
        }
        if (q is FlashcardQuestion card)
        {
            if (!string.IsNullOrWhiteSpace(card.Front.ImagePath)) assets.Add(card.Front.ImagePath);
            if (!string.IsNullOrWhiteSpace(card.Back.ImagePath)) assets.Add(card.Back.ImagePath);
            return;
        }
        if (q is not PayloadQuestion pq)
            return;

        switch (pq.Type)
        {
            case QuestionType.RelabelImage when Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(pq.PayloadJson, out var rel) && !string.IsNullOrWhiteSpace(rel?.ImagePath):
                assets.Add(rel.ImagePath);
                break;
            case QuestionType.SpotError when Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(pq.PayloadJson, out var se) && !string.IsNullOrWhiteSpace(se?.ImagePath):
                assets.Add(se.ImagePath);
                break;
            case QuestionType.Numeric when Phase1PayloadCodec.TryDeserialize<NumericPayload>(pq.PayloadJson, out var numP) && !string.IsNullOrWhiteSpace(numP?.ImagePath):
                assets.Add(numP.ImagePath);
                break;
            case QuestionType.CaseGroup when Phase1PayloadCodec.TryDeserialize<CaseGroupPayload>(pq.PayloadJson, out var caseP) && caseP is not null:
                if (!string.IsNullOrWhiteSpace(caseP.ImagePath))
                    assets.Add(caseP.ImagePath);
                if (caseP.Questions is not null)
                {
                    foreach (var sub in caseP.Questions)
                    {
                        if (sub is not null && Quiz.TryParseQuestionType(sub.Type, out var subType) && subType != QuestionType.CaseGroup)
                            CollectQuestionAssets(sub.RehydrateToDomainQuestion(subType, $"{q.Id}:sub-{sub.Index}", q.ContainerId), assets);
                    }
                }
                break;
        }
    }

    private static SessionPackageDto MapSessionToDto(StudySession session, bool includeLayouts) =>
        new(session.Id, session.ActiveComponentId, session.Components.Select(c => new ComponentPackageDto(
            c.Id, c.Index, c.Kind.ToString(), c.Quiz is null ? null : MapQuizToDto(c.Quiz, includeLayouts),
            c.ArticleBlocks.Select(b => new ArticleBlockPackageDto(
                b.Id, b.Index, b.Kind.ToString(), b.TextContent, b.ImagePath,
                b.Caption, b.Alt,
                b.CalloutTone.HasValue ? ArticleBlock.CalloutToneToString(b.CalloutTone.Value) : null,
                b.CalloutTitle,
                b.RefKey,
                b.PayloadJson)).ToList(),
            includeLayouts ? c.LayoutOverrides.ToDictionary(x => x.Key, x => x.Value) : null,
            c.Title,
            c.RefKey,
            c.LinkCards.Count > 0 ? c.LinkCards.ToList() : null)).ToList(),
            session.Name, session.Description);

    private static QuizPackageDto MapQuizToDto(Quiz quiz, bool includeLayouts) =>
        new(quiz.Id, quiz.Type.ToString(), quiz.Questions.Select(q => q switch
        {
            McqQuestion m => new QuestionPackageDto(m.Id, m.Index, "Mcq", m.Text, m.MaxOptions, m.IsMultiCorrect,
                m.Options.Select(o => new OptionPackageDto(o.Index, o.Text, o.ImagePath, o.IsCorrect)).ToList(),
                null, null, includeLayouts ? m.LayoutOverrides.ToDictionary(x => x.Key, x => x.Value) : null,
                m.ImagePath, m.Explanation, m.RefKey, m.PayloadJson),
            FlashcardQuestion f => new QuestionPackageDto(f.Id, f.Index, "Flashcard", null, 0, false, [],
                new SidePackageDto(f.Front.Text, f.Front.ImagePath, f.Front.Position.ToString(), f.Front.IsPositionExplicit),
                new SidePackageDto(f.Back.Text, f.Back.ImagePath, f.Back.Position.ToString(), f.Back.IsPositionExplicit),
                includeLayouts ? f.LayoutOverrides.ToDictionary(x => x.Key, x => x.Value) : null,
                null, null, f.RefKey, f.PayloadJson),
            PayloadQuestion p => new QuestionPackageDto(p.Id, p.Index, p.Type.ToString(), null, 0, false, [],
                null, null, includeLayouts ? p.LayoutOverrides.ToDictionary(x => x.Key, x => x.Value) : null,
                null, null, p.RefKey, p.PayloadJson),
            _ => throw new InvalidOperationException("Unsupported question type.")
        }).ToList(),
        string.IsNullOrWhiteSpace(quiz.Name) ? null : quiz.Name,
        string.IsNullOrWhiteSpace(quiz.Description) ? null : quiz.Description);

    private static Result<string?> ValidateAndRegisterRef(string? rawRef, HashSet<string> usedRefs)
    {
        var normRes = RefKeyValidator.ValidateAndNormalize(rawRef);
        if (!normRes.IsSuccess) return normRes.Error!;
        if (normRes.Value is not null && !usedRefs.Add(normRes.Value))
            return AddressErrors.DuplicateRef(normRes.Value);
        return normRes.Value;
    }

    private static Result<StudySession> RehydrateSessionSafe(SessionPackageDto dto, string storageDir, bool isLegacyV1)
    {
        if (dto.Components is null)
            return new Error("Package.InvalidWorkspace", "Session workspace.json is missing components.", ErrorType.Validation);

        var usedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var comps = new List<SessionComponent>(dto.Components.Count);
        foreach (var c in dto.Components)
        {
            if (!IsSafeSingleSegmentId(c.Id) || c.Index < 1)
                return new Error("Package.InvalidWorkspace", $"Invalid component ID or index in workspace.json: '{c.Id}'.", ErrorType.Validation);
            if (!Enum.TryParse<ComponentKind>(c.Kind, true, out var kind) || !Enum.IsDefined(kind))
                return new Error("Package.InvalidWorkspace", $"Unsupported component kind '{c.Kind}'.", ErrorType.Validation);

            var compRefRes = ValidateAndRegisterRef(c.RefKey, usedRefs);
            if (!compRefRes.IsSuccess) return compRefRes.Error!;

            Quiz? quiz = null;
            if (kind == ComponentKind.Quiz)
            {
                if (c.Quiz is null)
                    return new Error("Package.InvalidWorkspace", $"Quiz component '{c.Id}' is missing its quiz payload.", ErrorType.Validation);
                if (!IsSafeSingleSegmentId(c.Quiz.Id) || !string.Equals(c.Quiz.Id, c.Id, StringComparison.Ordinal))
                    return new Error("Package.InvalidWorkspace", $"Component quiz ID '{c.Quiz.Id}' must match component ID '{c.Id}'.", ErrorType.Validation);
                var qRes = RehydrateQuizSafe(c.Quiz, storageDir, isLegacyV1, usedRefs);
                if (!qRes.IsSuccess) return qRes.Error!;
                quiz = qRes.Value;
            }
            else if (c.Quiz is not null)
            {
                return new Error("Package.InvalidWorkspace", $"Non-quiz component '{c.Id}' ({kind}) cannot contain a quiz payload.", ErrorType.Validation);
            }

            var blocks = new List<ArticleBlock>((c.ArticleBlocks ?? []).Count);
            foreach (var b in c.ArticleBlocks ?? [])
            {
                if (!IsSafeSingleSegmentId(b.Id) || b.Index < 1)
                    return new Error("Package.InvalidWorkspace", $"Invalid article block ID or index: '{b.Id}'.", ErrorType.Validation);
                if (!Enum.TryParse<ArticleBlockKind>(b.Kind, true, out var bKind) || !Enum.IsDefined(bKind))
                    return new Error("Package.InvalidWorkspace", $"Unsupported article block kind '{b.Kind}'.", ErrorType.Validation);
                if (!IsSafeRelativeAssetPath(b.ImagePath, storageDir))
                    return new Error("Package.InvalidWorkspace", $"Unsafe article imagePath '{b.ImagePath}'.", ErrorType.Validation);

                var blockRefRes = ValidateAndRegisterRef(b.RefKey, usedRefs);
                if (!blockRefRes.IsSuccess) return blockRefRes.Error!;

                CalloutTone? calloutTone = null;
                if (!string.IsNullOrWhiteSpace(b.CalloutTone) && ArticleBlock.TryParseCalloutTone(b.CalloutTone, out var parsedTone))
                    calloutTone = parsedTone;
                else if (bKind == ArticleBlockKind.Callout)
                    calloutTone = CalloutTone.Pearl;

                string? blockPayloadJson = b.PayloadJson;
                if (bKind == ArticleBlockKind.Table)
                {
                    if (!Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(b.PayloadJson, out var parsedTable) ||
                        parsedTable is null ||
                        parsedTable.Headers is not { Count: > 0 } ||
                        parsedTable.Rows is not { Count: > 0 } ||
                        parsedTable.Rows.Any(r => r is null))
                    {
                        return new Error("Package.InvalidWorkspace", $"Invalid or missing payloadJson for table block '{b.Id}'.", ErrorType.Validation);
                    }

                    var canonicalAlignments = parsedTable.Alignments is { Count: > 0 }
                        ? parsedTable.Alignments
                        : Enumerable.Repeat("left", parsedTable.Headers.Count).ToList();
                    var canonicalTable = new TableBlockPayload(
                        parsedTable.Title,
                        parsedTable.Caption,
                        parsedTable.StickyFirstCol,
                        parsedTable.HideableCols ?? [],
                        parsedTable.Headers,
                        canonicalAlignments,
                        parsedTable.Rows,
                        parsedTable.Footnote);
                    blockPayloadJson = Phase1PayloadCodec.Serialize(canonicalTable);
                }
                else if (bKind == ArticleBlockKind.DiagramExplore)
                {
                    if (!Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(b.PayloadJson, out var parsedDiag) ||
                        parsedDiag is null ||
                        string.IsNullOrWhiteSpace(parsedDiag.ImagePath) ||
                        !IsSafeRelativeAssetPath(parsedDiag.ImagePath, storageDir) ||
                        parsedDiag.Hotspots is not { Count: > 0 } ||
                        parsedDiag.Hotspots.Any(h => h is null || string.IsNullOrWhiteSpace(h.Label) || string.IsNullOrWhiteSpace(h.Note) || !Phase3PayloadRules.HasValidHotspotBounds(h)))
                    {
                        return new Error("Package.InvalidWorkspace", $"Invalid or missing payloadJson for diagram-explore block '{b.Id}'.", ErrorType.Validation);
                    }
                }
                else if (bKind == ArticleBlockKind.Reveal)
                {
                    if (!Phase1PayloadCodec.TryDeserialize<RevealBlockPayload>(b.PayloadJson, out var parsedRev) ||
                        parsedRev is null ||
                        parsedRev.Steps is not { Count: > 0 } ||
                        parsedRev.Steps.Any(s => s is null || string.IsNullOrWhiteSpace(s.Heading) || string.IsNullOrWhiteSpace(s.Body)))
                    {
                        return new Error("Package.InvalidWorkspace", $"Invalid or missing payloadJson for reveal block '{b.Id}'.", ErrorType.Validation);
                    }
                }

                blocks.Add(ArticleBlock.Rehydrate(b.Id, c.Id, b.Index, bKind, b.TextContent, b.ImagePath,
                    refKey: blockRefRes.Value, caption: b.Caption, alt: b.Alt, calloutTone: calloutTone, calloutTitle: b.CalloutTitle, payloadJson: blockPayloadJson));
            }

            IReadOnlyList<string> compTargets = kind == ComponentKind.Article
                ? ["article"]
                : quiz?.Type switch { QuizType.Flashcard => ["flashcard"], QuizType.Mixed => ["mcq", "flashcard"], _ => ["mcq"] };
            var ovRes = ValidateOverrides(compTargets, c.LayoutOverrides, isLegacyV1);
            if (!ovRes.IsSuccess) return ovRes.Error!;

            List<ArticleLinkCard>? linkCards = null;
            if (c.Links is { Count: > 0 })
            {
                if (kind != ComponentKind.Article)
                    return new Error("Package.InvalidWorkspace", $"Non-article component '{c.Id}' cannot contain article links.", ErrorType.Validation);
                linkCards = new List<ArticleLinkCard>(c.Links.Count);
                foreach (var lc in c.Links)
                {
                    if (lc is null || string.IsNullOrWhiteSpace(lc.Id))
                        return new Error("Package.InvalidWorkspace", $"Component '{c.Id}' contains a link card with empty id.", ErrorType.Validation);
                    var idRes = ArticleLinkRules.ValidateAndNormalizeCardId(lc.Id);
                    if (!idRes.IsSuccess || string.IsNullOrWhiteSpace(idRes.Value))
                        return idRes.Error!;
                    if (!IsSafeRelativeAssetPath(lc.ImagePath, storageDir))
                        return new Error("Package.InvalidWorkspace", $"Unsafe link card imagePath '{lc.ImagePath}'.", ErrorType.Validation);
                    linkCards.Add(new ArticleLinkCard(idRes.Value, lc.Title, lc.Text, lc.ImagePath));
                }
            }

            comps.Add(SessionComponent.Rehydrate(c.Id, dto.Id, c.Index, kind, quiz, blocks, ovRes.Value, c.Title, compRefRes.Value, linkCards));
        }

        return StudySession.Rehydrate(dto.Id, storageDir, dto.ActiveComponentId, comps, dto.Name, dto.Description);
    }

    private static Result<Quiz> RehydrateQuizSafe(QuizPackageDto dto, string storageDir, bool isLegacyV1, HashSet<string>? sharedUsedRefs = null)
    {
        if (!Enum.TryParse<QuizType>(dto.QuizType, true, out var qType) || !Enum.IsDefined(qType))
            return new Error("Package.InvalidWorkspace", $"Unsupported quiz type '{dto.QuizType}'.", ErrorType.Validation);

        var usedRefs = sharedUsedRefs ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var questions = new List<Question>((dto.Questions ?? []).Count);
        foreach (var q in dto.Questions ?? [])
        {
            if (!IsSafeSingleSegmentId(q.Id) || q.Index < 1)
                return new Error("Package.InvalidWorkspace", $"Invalid question ID or index: '{q.Id}'.", ErrorType.Validation);

            var qRefRes = ValidateAndRegisterRef(q.RefKey, usedRefs);
            if (!qRefRes.IsSuccess) return qRefRes.Error!;

            if (string.Equals(q.QuestionType, "Mcq", StringComparison.OrdinalIgnoreCase))
            {
                var ovRes = ValidateOverrides(["mcq"], q.LayoutOverrides, isLegacyV1);
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
                    q.PromptImagePath, q.Explanation, qRefRes.Value));
            }
            else if (string.Equals(q.QuestionType, "Flashcard", StringComparison.OrdinalIgnoreCase))
            {
                var ovRes = ValidateOverrides(["flashcard"], q.LayoutOverrides, isLegacyV1);
                if (!ovRes.IsSuccess) return ovRes.Error!;

                var frontRes = RehydrateSideSafe(q.Front, storageDir);
                if (!frontRes.IsSuccess) return frontRes.Error!;
                var backRes = RehydrateSideSafe(q.Back, storageDir);
                if (!backRes.IsSuccess) return backRes.Error!;

                questions.Add(FlashcardQuestion.Rehydrate(q.Id, dto.Id, q.Index, frontRes.Value, backRes.Value, ovRes.Value, qRefRes.Value));
            }
            else if (Quiz.TryParseQuestionType(q.QuestionType, out var parsedQType) &&
                     parsedQType is QuestionType.FillBlank or QuestionType.AssertionReason or QuestionType.SpotError
                         or QuestionType.Differentiation or QuestionType.Mechanism or QuestionType.RelabelImage
                         or QuestionType.CaseGroup or QuestionType.Classify or QuestionType.Match or QuestionType.Numeric)
            {
                var ovRes = ValidateOverrides(["mcq"], q.LayoutOverrides, isLegacyV1);
                if (!ovRes.IsSuccess) return ovRes.Error!;
                bool validPayload = IsValidPackagePayloadQuestion(parsedQType, q.PayloadJson, storageDir, allowCaseGroup: true);
                if (!validPayload)
                    return new Error("Package.InvalidWorkspace", $"Invalid or missing payloadJson for {parsedQType} question '{q.Id}'.", ErrorType.Validation);

                questions.Add(PayloadQuestion.Rehydrate(q.Id, dto.Id, q.Index, parsedQType, q.PayloadJson!, ovRes.Value, qRefRes.Value));
            }
            else
            {
                return new Error("Package.InvalidWorkspace", $"Unsupported question type '{q.QuestionType}'.", ErrorType.Validation);
            }
        }

        return Quiz.Rehydrate(dto.Id, qType, storageDir, questions, dto.Name, dto.Description);
    }

    private static bool IsValidPackagePayloadQuestion(QuestionType parsedQType, string? payloadJson, string storageDir, bool allowCaseGroup)
    {
        return parsedQType switch
        {
            QuestionType.FillBlank => Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(payloadJson, out var fb) &&
                fb is not null &&
                !string.IsNullOrWhiteSpace(fb.Template) &&
                fb.Blanks is { Count: > 0 } &&
                fb.Blanks.All(b => b is not null && b.Answers is { Count: > 0 }),
            QuestionType.AssertionReason => Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(payloadJson, out var ar) &&
                ar is not null &&
                ar.Assertion is not null &&
                ar.Reason is not null &&
                ar.Answer is not null &&
                ar.Scheme is not null,
            QuestionType.SpotError => Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(payloadJson, out var se) &&
                se is not null &&
                !string.IsNullOrWhiteSpace(se.Template) &&
                se.Segments is { Count: > 0 } &&
                se.Segments.All(s => s is not null && s.Text is not null) &&
                IsSafeRelativeAssetPath(se.ImagePath, storageDir),
            QuestionType.Differentiation => Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(payloadJson, out var dp) &&
                dp is not null &&
                Phase2PayloadRules.IsValidDiffMode(dp.Mode) &&
                dp.Headers is { Count: >= 2 } &&
                dp.Headers.All(h => h is not null) &&
                dp.Rows is { Count: >= 1 } &&
                dp.Rows.All(r => r is not null && r.Count == dp.Headers.Count && r.All(c => c is not null)) &&
                dp.SpecialCells is not null &&
                dp.SpecialCells.All(sc => sc is not null),
            QuestionType.Mechanism => Phase1PayloadCodec.TryDeserialize<MechanismPayload>(payloadJson, out var mp) &&
                mp is not null &&
                mp.Steps is { Count: >= 2 } &&
                mp.Steps.All(s => s is not null && !string.IsNullOrWhiteSpace(s.Text)),
            QuestionType.RelabelImage => Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(payloadJson, out var rel) &&
                rel is not null &&
                !string.IsNullOrWhiteSpace(rel.ImagePath) &&
                IsSafeRelativeAssetPath(rel.ImagePath, storageDir) &&
                Phase2PayloadRules.IsValidRelabelMode(rel.Mode) &&
                Phase2PayloadRules.IsValidRelabelStyle(rel.Style) &&
                rel.Boxes is { Count: >= 1 } &&
                rel.Boxes.All(b => b is not null && Phase2PayloadRules.ValidateRelabelBox(b)),
            QuestionType.Classify => Phase1PayloadCodec.TryDeserialize<ClassifyPayload>(payloadJson, out var cl) &&
                cl is not null &&
                cl.Buckets is { Count: >= 2 } &&
                cl.Buckets.All(b => b is not null && !string.IsNullOrWhiteSpace(b.Name)) &&
                cl.Items is { Count: >= 2 } &&
                cl.Items.All(it => it is not null && !string.IsNullOrWhiteSpace(it.Text)),
            QuestionType.Match => Phase1PayloadCodec.TryDeserialize<MatchPayload>(payloadJson, out var mt) &&
                mt is not null &&
                mt.Pairs is { Count: >= 2 } &&
                mt.Pairs.All(p => p is not null && !string.IsNullOrWhiteSpace(p.Left) && !string.IsNullOrWhiteSpace(p.Right)) &&
                (mt.Distractors is null || mt.Distractors.All(d => !string.IsNullOrWhiteSpace(d))),
            QuestionType.Numeric => Phase1PayloadCodec.TryDeserialize<NumericPayload>(payloadJson, out var nm) &&
                nm is not null &&
                !string.IsNullOrWhiteSpace(nm.Prompt) &&
                double.IsFinite(nm.Answer) &&
                double.IsFinite(nm.Tolerance) &&
                nm.Tolerance >= 0 &&
                Phase3PayloadRules.IsValidToleranceMode(nm.ToleranceMode) &&
                IsSafeRelativeAssetPath(nm.ImagePath, storageDir),
            QuestionType.CaseGroup when allowCaseGroup => Phase1PayloadCodec.TryDeserialize<CaseGroupPayload>(payloadJson, out var cs) &&
                cs is not null &&
                !string.IsNullOrWhiteSpace(cs.Stem) &&
                IsSafeRelativeAssetPath(cs.ImagePath, storageDir) &&
                cs.Questions is { Count: >= 2 } &&
                cs.Questions.All(sub => IsValidCaseSubQuestion(sub, storageDir)),
            _ => false
        };
    }

    private static bool IsValidCaseSubQuestion(CaseSubQuestion? sub, string storageDir)
    {
        if (sub is null || !Quiz.TryParseQuestionType(sub.Type, out var subType) || subType == QuestionType.CaseGroup)
            return false;

        if (subType == QuestionType.Mcq)
            return sub.Mcq is not null
                   && IsSafeRelativeAssetPath(sub.Mcq.ImagePath, storageDir)
                   && (sub.Mcq.Options ?? []).All(o => o is not null && o.Index >= 1 && IsSafeRelativeAssetPath(o.ImagePath, storageDir));

        if (subType == QuestionType.Flashcard)
            return sub.Flashcard is not null
                   && IsSafeRelativeAssetPath(sub.Flashcard.Front.ImagePath, storageDir)
                   && IsSafeRelativeAssetPath(sub.Flashcard.Back.ImagePath, storageDir);

        return IsValidPackagePayloadQuestion(subType, sub.PayloadJson, storageDir, allowCaseGroup: false);
    }

    private static Result<FlashcardSide> RehydrateSideSafe(SidePackageDto? side, string storageDir)
    {
        if (side is null) return FlashcardSide.Empty();
        if (!Enum.TryParse<MediaPosition>(side.Position, true, out var pos) || !Enum.IsDefined(pos))
            return new Error("Package.InvalidWorkspace", $"Unsupported media position '{side.Position}'.", ErrorType.Validation);
        if (!IsSafeRelativeAssetPath(side.ImagePath, storageDir))
            return new Error("Package.InvalidWorkspace", $"Unsafe flashcard imagePath '{side.ImagePath}'.", ErrorType.Validation);
        return FlashcardSide.Rehydrate(side.Text, side.ImagePath, pos, side.IsPositionExplicit);
    }

    private sealed record PackageManifest(string PackageVersion, string Kind, string Id, string ExportedAtUtc, List<string> Assets, string TargetPlatform = "desktop");
    private sealed record SessionPackageDto(string Id, string? ActiveComponentId, List<ComponentPackageDto> Components, string? Name = null, string? Description = null);
    private sealed record ComponentPackageDto(
        string Id,
        int Index,
        string Kind,
        QuizPackageDto? Quiz,
        List<ArticleBlockPackageDto> ArticleBlocks,
        Dictionary<string, string>? LayoutOverrides,
        string? Title = null,
        string? RefKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<ArticleLinkCard>? Links = null);
    private sealed record ArticleBlockPackageDto(
        string Id,
        int Index,
        string Kind,
        string? TextContent,
        string? ImagePath,
        string? Caption = null,
        string? Alt = null,
        string? CalloutTone = null,
        string? CalloutTitle = null,
        string? RefKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PayloadJson = null);
    private sealed record QuizPackageDto(string Id, string QuizType, List<QuestionPackageDto> Questions, string? Name = null, string? Description = null);
    private sealed record QuestionPackageDto(
        string Id,
        int Index,
        string QuestionType,
        string? PromptText,
        int MaxOptions,
        bool IsMultiCorrect,
        List<OptionPackageDto> Options,
        SidePackageDto? Front,
        SidePackageDto? Back,
        Dictionary<string, string>? LayoutOverrides,
        string? PromptImagePath = null,
        string? Explanation = null,
        string? RefKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PayloadJson = null);
    private sealed record OptionPackageDto(int Index, string? Text, string? ImagePath, bool IsCorrect);
    private sealed record SidePackageDto(string? Text, string? ImagePath, string Position, bool IsPositionExplicit = false);
}
