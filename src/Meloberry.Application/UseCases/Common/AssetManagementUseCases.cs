using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;

namespace Meloberry.Application.UseCases.Common;

public sealed record AddAssetCommand(
    string? WorkspaceAddress,
    string SourceFilePath,
    string? TargetDirectory = null);

public sealed record AddAssetResponse(
    string WorkspaceId,
    string RelativeAssetPath,
    string FullPath,
    bool AlreadyExisted);

public sealed record WorkspaceAssetItem(
    string RelativePath,
    string FullPath,
    long SizeBytes,
    bool IsReferenced,
    bool IsOrphan);

public sealed record ListAssetsQuery(string WorkspaceAddress);

public sealed record ListAssetsResponse(
    string WorkspaceId,
    IReadOnlyList<WorkspaceAssetItem> Assets);

public sealed record PruneAssetsCommand(string WorkspaceAddress, bool DryRun = false);

public sealed record PruneAssetsResponse(
    string WorkspaceId,
    IReadOnlyList<string> RemovedAssets,
    long BytesFreed,
    bool DryRun);

public sealed class AddAssetHandler(
    EntityAddressResolver addressResolver,
    IContentInspector contentInspector,
    IWorkingDirectoryProvider workingDirectoryProvider)
{
    public async Task<Result<AddAssetResponse>> HandleAsync(AddAssetCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.SourceFilePath))
            return new Error("Cli.InvalidArgs", "Asset source file path is required.", ErrorType.Validation);

        string storageDir;
        string workspaceId;
        if (!string.IsNullOrWhiteSpace(command.TargetDirectory))
        {
            storageDir = workingDirectoryProvider.NormalizeDirectoryPath(command.TargetDirectory);
            workspaceId = Path.GetFileName(storageDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        else if (!string.IsNullOrWhiteSpace(command.WorkspaceAddress))
        {
            var resolved = await addressResolver.ResolveAsync(command.WorkspaceAddress, cancellationToken);
            if (!resolved.IsSuccess) return resolved.Error!;
            if (resolved.Value.EntityKind is not ("session" or "quiz"))
                return new Error("Asset.InvalidWorkspace", $"Address '{command.WorkspaceAddress}' must target a Session_* or Set_* workspace.", ErrorType.Validation);
            storageDir = resolved.Value.StorageDirectory;
            workspaceId = resolved.Value.WorkspaceId;
        }
        else
        {
            return new Error("Cli.InvalidArgs", "Provide either a workspace address or '--dir <targetDir>'.", ErrorType.Validation);
        }

        var sourceFull = Path.GetFullPath(command.SourceFilePath);
        var copyRes = await contentInspector.CopyAndVerifyImageWithStatusAsync(sourceFull, storageDir, cancellationToken);
        if (!copyRes.IsSuccess) return copyRes.Error!;

        var fullAssetPath = Path.GetFullPath(Path.Combine(storageDir, copyRes.Value.RelativeAssetPath.Replace('/', Path.DirectorySeparatorChar)));
        return new AddAssetResponse(workspaceId, copyRes.Value.RelativeAssetPath, fullAssetPath, copyRes.Value.AlreadyExisted);
    }
}

public sealed class ListAssetsHandler(EntityAddressResolver addressResolver)
{
    public async Task<Result<ListAssetsResponse>> HandleAsync(ListAssetsQuery query, CancellationToken cancellationToken = default)
    {
        var resolved = await addressResolver.ResolveAsync(query.WorkspaceAddress, cancellationToken);
        if (!resolved.IsSuccess) return resolved.Error!;
        var entity = resolved.Value;
        if (entity.EntityKind is not ("session" or "quiz"))
            return new Error("Asset.InvalidWorkspace", $"Address '{query.WorkspaceAddress}' must target a Session_* or Set_* workspace.", ErrorType.Validation);

        var referenced = CollectNormalizedReferencedSet(entity);
        var assetsDir = Path.Combine(entity.StorageDirectory, "assets");
        var items = new List<WorkspaceAssetItem>();
        if (Directory.Exists(assetsDir))
        {
            foreach (var file in Directory.EnumerateFiles(assetsDir, "*", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var rel = $"assets/{Path.GetFileName(file)}";
                var info = new FileInfo(file);
                bool isRef = referenced.Contains(rel);
                items.Add(new WorkspaceAssetItem(rel, info.FullName, info.Length, isRef, !isRef));
            }
        }

        return new ListAssetsResponse(entity.WorkspaceId, items);
    }

    internal static HashSet<string> CollectNormalizedReferencedSet(ResolvedEntity entity)
    {
        var raw = entity.Session is not null
            ? WorkspaceAssetCollector.CollectSessionAssets(entity.Session)
            : WorkspaceAssetCollector.CollectQuizAssets(entity.Quiz!);
        return raw.Select(p => p.Replace('\\', '/').Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class PruneAssetsHandler(
    EntityAddressResolver addressResolver,
    IChainTransactionManager? transactionManager = null)
{
    public async Task<Result<PruneAssetsResponse>> HandleAsync(PruneAssetsCommand command, CancellationToken cancellationToken = default)
    {
        var resolved = await addressResolver.ResolveAsync(command.WorkspaceAddress, cancellationToken);
        if (!resolved.IsSuccess) return resolved.Error!;
        var entity = resolved.Value;
        if (entity.EntityKind is not ("session" or "quiz"))
            return new Error("Asset.InvalidWorkspace", $"Address '{command.WorkspaceAddress}' must target a Session_* or Set_* workspace.", ErrorType.Validation);

        var referenced = ListAssetsHandler.CollectNormalizedReferencedSet(entity);
        var assetsDir = Path.Combine(entity.StorageDirectory, "assets");
        var removed = new List<string>();
        long bytesFreed = 0;

        if (!Directory.Exists(assetsDir))
            return new PruneAssetsResponse(entity.WorkspaceId, removed, 0, command.DryRun);

        if (!command.DryRun)
            transactionManager?.NotifyWorkspaceMutating(entity.StorageDirectory);

        foreach (var file in Directory.EnumerateFiles(assetsDir, "*", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var rel = $"assets/{Path.GetFileName(file)}";
            if (referenced.Contains(rel))
                continue;

            var size = new FileInfo(file).Length;
            if (!command.DryRun)
            {
                transactionManager?.BackupFileForRollback(file);
                File.Delete(file);
            }
            removed.Add(rel);
            bytesFreed += size;
        }

        return new PruneAssetsResponse(entity.WorkspaceId, removed, bytesFreed, command.DryRun);
    }
}
