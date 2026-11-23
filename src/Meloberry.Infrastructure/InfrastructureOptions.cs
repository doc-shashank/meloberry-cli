using Meloberry.Application.Abstractions;

namespace Meloberry.Infrastructure;

public sealed class MeloberryStorageOptions
{
    public string AppDataDirectory { get; set; } = ResolveDefaultAppDataDirectory();
    public string AppSettingsDbPath => Path.Combine(AppDataDirectory, "app_settings.db");

    private static string ResolveDefaultAppDataDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("MBY_APP_DATA");
        if (string.IsNullOrWhiteSpace(overrideDir))
            overrideDir = Environment.GetEnvironmentVariable("MELOBERRY_APPDATA");
        return !string.IsNullOrWhiteSpace(overrideDir)
            ? Path.GetFullPath(overrideDir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meloberry");
    }
}

public sealed class WorkingDirectoryProvider : IWorkingDirectoryProvider
{
    public string GetCurrentDirectory() => Directory.GetCurrentDirectory();
    public string NormalizeDirectoryPath(string? explicitPath) =>
        string.IsNullOrWhiteSpace(explicitPath) ? Directory.GetCurrentDirectory() : Path.GetFullPath(explicitPath.Trim());
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
}
