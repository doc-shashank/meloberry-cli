using Meloberry.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace Meloberry.Infrastructure.Persistence;

public sealed class ChainTransactionManager(MeloberryStorageOptions options) : IChainTransactionManager
{
    private readonly AsyncLocal<ActiveChainTransaction?> _current = new();

    public IChainTransactionScope Begin()
    {
        var txRoot = Path.Combine(Path.GetTempPath(), $"mby_tx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(txRoot);

        bool appSettingsExisted = File.Exists(options.AppSettingsDbPath);
        string? appSettingsBackup = null;
        if (appSettingsExisted)
        {
            SqliteConnection.ClearAllPools();
            appSettingsBackup = Path.Combine(txRoot, "app_settings.db.bak");
            File.Copy(options.AppSettingsDbPath, appSettingsBackup, overwrite: true);
        }

        var active = new ActiveChainTransaction(txRoot, appSettingsExisted, appSettingsBackup);
        var previous = _current.Value;
        _current.Value = active;
        return new ScopeHandle(this, active, previous);
    }

    internal void NotifyWorkspaceMutating(string storageDirectory)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
            return;

        _current.Value?.TrackDirectory(storageDirectory);
    }

    internal void NotifyOutputDirectoryMutating(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return;

        _current.Value?.TrackDirectory(outputDirectory);
    }

    internal void NotifyOutputFileMutating(string outputFilePath)
    {
        if (string.IsNullOrWhiteSpace(outputFilePath))
            return;

        _current.Value?.TrackFile(outputFilePath);
    }

    private void Commit(ActiveChainTransaction active, ActiveChainTransaction? previous)
    {
        if (active.Committed || active.RolledBack)
            return;

        active.Committed = true;
        _current.Value = previous;
        TryDeleteDirectory(active.TxRoot);
    }

    private void Rollback(ActiveChainTransaction active, ActiveChainTransaction? previous)
    {
        if (active.Committed || active.RolledBack)
            return;

        active.RolledBack = true;
        _current.Value = previous;

        SqliteConnection.ClearAllPools();

        foreach (var file in active.GetTrackedFilesSnapshot())
        {
            if (!file.Existed)
            {
                TryDeleteFile(file.FullPath);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(file.BackupPath) && File.Exists(file.BackupPath))
            {
                var parentDir = Path.GetDirectoryName(file.FullPath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                    Directory.CreateDirectory(parentDir);
                File.Copy(file.BackupPath, file.FullPath, overwrite: true);
            }
        }

        foreach (var ws in active.GetTrackedDirectoriesSnapshot())
        {
            if (!ws.Existed)
            {
                TryDeleteDirectory(ws.FullDirectory);
                continue;
            }

            Directory.CreateDirectory(ws.FullDirectory);
            ClearDirectoryContents(ws.FullDirectory);
            if (!string.IsNullOrWhiteSpace(ws.BackupDirectory) && Directory.Exists(ws.BackupDirectory))
                CopyDirectoryContents(ws.BackupDirectory, ws.FullDirectory);
        }

        DeleteSqliteSidecars(options.AppSettingsDbPath);
        if (active.AppSettingsExisted && !string.IsNullOrWhiteSpace(active.AppSettingsBackupPath) && File.Exists(active.AppSettingsBackupPath))
        {
            var appDir = Path.GetDirectoryName(options.AppSettingsDbPath);
            if (!string.IsNullOrWhiteSpace(appDir))
                Directory.CreateDirectory(appDir);
            File.Copy(active.AppSettingsBackupPath, options.AppSettingsDbPath, overwrite: true);
        }
        else if (!active.AppSettingsExisted && File.Exists(options.AppSettingsDbPath))
        {
            File.Delete(options.AppSettingsDbPath);
        }

        TryDeleteDirectory(active.TxRoot);
    }

    private static void DeleteSqliteSidecars(string dbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = dbPath + suffix;
            if (File.Exists(sidecar))
                try { File.Delete(sidecar); } catch { }
        }
    }

    private static void ClearDirectoryContents(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
            File.Delete(file);
        foreach (var subDir in Directory.EnumerateDirectories(directory))
            Directory.Delete(subDir, recursive: true);
    }

    private static void CopyDirectoryContents(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectoryContents(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private sealed record TrackedDirectory(string FullDirectory, bool Existed, string? BackupDirectory);
    private sealed record TrackedFile(string FullPath, bool Existed, string? BackupPath);

    private sealed class ActiveChainTransaction(string txRoot, bool appSettingsExisted, string? appSettingsBackupPath)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TrackedDirectory> _trackedDirs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TrackedFile> _trackedFiles = new(StringComparer.OrdinalIgnoreCase);

        public string TxRoot { get; } = txRoot;
        public bool AppSettingsExisted { get; } = appSettingsExisted;
        public string? AppSettingsBackupPath { get; } = appSettingsBackupPath;
        public bool Committed { get; set; }
        public bool RolledBack { get; set; }

        public void TrackDirectory(string directory)
        {
            var fullDir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            lock (_gate)
            {
                if (_trackedDirs.ContainsKey(fullDir))
                    return;

                if (Directory.Exists(fullDir))
                {
                    SqliteConnection.ClearAllPools();
                    var backupDir = Path.Combine(TxRoot, "dirs", _trackedDirs.Count.ToString());
                    CopyDirectoryContents(fullDir, backupDir);
                    _trackedDirs[fullDir] = new TrackedDirectory(fullDir, Existed: true, backupDir);
                }
                else
                {
                    _trackedDirs[fullDir] = new TrackedDirectory(fullDir, Existed: false, BackupDirectory: null);
                }
            }
        }

        public void TrackFile(string filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            lock (_gate)
            {
                if (_trackedFiles.ContainsKey(fullPath))
                    return;

                if (File.Exists(fullPath))
                {
                    var backupRoot = Path.Combine(TxRoot, "files");
                    Directory.CreateDirectory(backupRoot);
                    var backupPath = Path.Combine(backupRoot, $"{_trackedFiles.Count}.bak");
                    File.Copy(fullPath, backupPath, overwrite: true);
                    _trackedFiles[fullPath] = new TrackedFile(fullPath, Existed: true, backupPath);
                }
                else
                {
                    _trackedFiles[fullPath] = new TrackedFile(fullPath, Existed: false, BackupPath: null);
                }
            }
        }

        public List<TrackedDirectory> GetTrackedDirectoriesSnapshot()
        {
            lock (_gate)
            {
                return [.. _trackedDirs.Values];
            }
        }

        public List<TrackedFile> GetTrackedFilesSnapshot()
        {
            lock (_gate)
            {
                return [.. _trackedFiles.Values];
            }
        }
    }

    private sealed class ScopeHandle(ChainTransactionManager owner, ActiveChainTransaction active, ActiveChainTransaction? previous) : IChainTransactionScope
    {
        private bool _disposed;

        public void Commit() => owner.Commit(active, previous);

        public void Rollback() => owner.Rollback(active, previous);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (!active.Committed && !active.RolledBack)
                owner.Rollback(active, previous);
            else if (ReferenceEquals(owner._current.Value, active))
                owner._current.Value = previous;
        }
    }
}
