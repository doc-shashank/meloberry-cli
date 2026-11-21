using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Layouts;
using Microsoft.Data.Sqlite;

namespace Meloberry.Infrastructure.Persistence;

public sealed class SqliteLayoutRepository(MeloberryStorageOptions options) : ILayoutRepository
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public async Task<LayoutProfile> GetProfileAsync(string targetType, CancellationToken cancellationToken = default)
    {
        var all = await GetAllProfilesAsync(cancellationToken);
        var normalized = targetType.Trim().ToLowerInvariant();
        return all.TryGetValue(normalized, out var profile) ? profile : LayoutProfile.CreateDefault(normalized).Value;
    }

    public async Task<IReadOnlyDictionary<string, LayoutProfile>> GetAllProfilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var profiles = LayoutBoundsCatalog.SupportedTargets.ToDictionary(
            t => t, t => LayoutProfile.CreateDefault(t).Value, StringComparer.OrdinalIgnoreCase);

        await using var conn = OpenAppConnection();
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TargetType, PropertiesJson FROM LayoutProfiles;";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var targetType = reader.GetString(0);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1));
            if (dict is not null && LayoutBoundsCatalog.IsValidTargetType(targetType))
                profiles[targetType.ToLowerInvariant()] = LayoutProfile.Rehydrate(targetType, dict);
        }
        return profiles;
    }

    public async Task SaveProfileAsync(LayoutProfile profile, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = OpenAppConnection();
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LayoutProfiles (TargetType, PropertiesJson, UpdatedAtUtc)
            VALUES ($targetType, $json, $updatedAt)
            ON CONFLICT(TargetType) DO UPDATE SET PropertiesJson = excluded.PropertiesJson, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        cmd.Parameters.AddWithValue("$targetType", profile.TargetType);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile.Values));
        cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized && File.Exists(options.AppSettingsDbPath)) return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.AppDataDirectory);
            await using var conn = OpenAppConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS LayoutProfiles (TargetType TEXT PRIMARY KEY, PropertiesJson TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS WorkspaceRegistry (WorkspaceId TEXT PRIMARY KEY, Kind TEXT NOT NULL, SubType TEXT NOT NULL, DbPath TEXT NOT NULL, StorageDirectory TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS EntityIndex (EntityId TEXT PRIMARY KEY, WorkspaceId TEXT NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    internal SqliteConnection OpenAppConnection() => new($"Data Source={options.AppSettingsDbPath};Pooling=False");
}
