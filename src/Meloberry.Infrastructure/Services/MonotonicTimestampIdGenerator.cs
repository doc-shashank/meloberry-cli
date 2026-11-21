using System.Globalization;
using Meloberry.Application.Abstractions;

namespace Meloberry.Infrastructure.Services;

public sealed class MonotonicTimestampIdGenerator(MeloberryStorageOptions? options = null) : IIdGenerator
{
    private readonly Lock _gate = new();
    private long _lastEpochMs;

    public string NewSessionId() => $"Session_{NextTimestampStamp()}";
    public string NewComponentId() => $"Component_{NextTimestampStamp()}";
    public string NewArticleBlockId() => $"ArtComp_{NextTimestampStamp()}";
    public string NewQuizId() => $"Set_{NextTimestampStamp()}";
    public string NewQuestionId() => $"Q_{NextTimestampStamp()}";

    private string NextTimestampStamp()
    {
        long nextMs;
        lock (_gate)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string? appDataDir = options?.AppDataDirectory;
            nextMs = !string.IsNullOrWhiteSpace(appDataDir)
                ? ReserveCrossProcessTimestamp(appDataDir, nowMs, _lastEpochMs)
                : (nowMs <= _lastEpochMs ? _lastEpochMs + 1 : nowMs);
            _lastEpochMs = nextMs;
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(nextMs).ToString("yyyyMMdd_HHmmss_fff");
    }

    private static long ReserveCrossProcessTimestamp(string appDataDir, long nowMs, long inProcessLast)
    {
        try
        {
            Directory.CreateDirectory(appDataDir);
            string lockPath = Path.Combine(appDataDir, ".mby_id_clock.lock");
            string clockPath = Path.Combine(appDataDir, ".mby_id_clock");

            using var lockStream = AcquireFileLockWithRetry(lockPath);
            long persisted = 0;
            if (File.Exists(clockPath))
                long.TryParse(File.ReadAllText(clockPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out persisted);

            long baseline = Math.Max(inProcessLast, persisted);
            long nextMs = nowMs <= baseline ? baseline + 1 : nowMs;
            File.WriteAllText(clockPath, nextMs.ToString(CultureInfo.InvariantCulture));
            return nextMs;
        }
        catch { return nowMs <= inProcessLast ? inProcessLast + 1 : nowMs; }
    }

    private static FileStream AcquireFileLockWithRetry(string lockPath)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 199) { Thread.Sleep(5); }
        }
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
