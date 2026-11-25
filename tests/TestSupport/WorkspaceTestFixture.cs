using System.IO.Compression;
using System.Text;
using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meloberry.TestSupport;

public sealed class WorkspaceTestFixture : IDisposable
{
    public string TempRoot { get; }
    public string AppDataDir { get; }
    public string WorkDir { get; }

    public WorkspaceTestFixture(string prefix = "mby_test")
    {
        TempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        AppDataDir = Path.Combine(TempRoot, "appdata");
        WorkDir = Path.Combine(TempRoot, "work");
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(WorkDir);
    }

    public ServiceProvider CreateProvider(string subDir = "appdata", Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(TempRoot, subDir));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public IDisposable PushCwd(string dir) => new CwdScope(dir);

    public string SubDir(params string[] parts)
    {
        var path = Path.Combine([TempRoot, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public static async Task<string> WritePackageAsync(
        string pkgPath,
        string kind,
        string id,
        string workspaceJson,
        string packageVersion = "2.0",
        string? layoutsJson = null,
        IEnumerable<(string EntryName, string Content)>? extraEntries = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
        using var za = ZipFile.Open(pkgPath, ZipArchiveMode.Create);
        using (var w = new StreamWriter(za.CreateEntry("manifest.json").Open()))
            await w.WriteAsync($$"""{"packageVersion":"{{packageVersion}}","kind":"{{kind}}","id":"{{id}}","exportedAtUtc":"2026-01-01T00:00:00Z","assets":[]}""");
        using (var w = new StreamWriter(za.CreateEntry("workspace.json").Open()))
            await w.WriteAsync(workspaceJson);
        if (layoutsJson is not null)
        {
            using var w = new StreamWriter(za.CreateEntry("layouts.json").Open());
            await w.WriteAsync(layoutsJson);
        }
        if (extraEntries is not null)
        {
            foreach (var (entryName, content) in extraEntries)
            {
                using var w = new StreamWriter(za.CreateEntry(entryName).Open());
                await w.WriteAsync(content);
            }
        }
        return pkgPath;
    }

    public static async Task MutateZipAsync(
        string srcPkg,
        string dstPkg,
        Func<ZipArchive, Task> mutate)
    {
        File.Copy(srcPkg, dstPkg, overwrite: true);
        using var zip = ZipFile.Open(dstPkg, ZipArchiveMode.Update);
        await mutate(zip);
    }

    public static async Task ReplaceZipEntryTextAsync(ZipArchive zip, string entryName, string content, bool prependUtf8Bom = false)
    {
        zip.GetEntry(entryName)?.Delete();
        await using var s = zip.CreateEntry(entryName).Open();
        if (prependUtf8Bom)
            await s.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF });
        await s.WriteAsync(Encoding.UTF8.GetBytes(content));
    }

    public static async Task<string> ReadZipEntryTextAsync(ZipArchive zip, string entryName)
    {
        using var sr = new StreamReader(zip.GetEntry(entryName)!.Open(), Encoding.UTF8);
        return await sr.ReadToEndAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(TempRoot))
            Directory.Delete(TempRoot, recursive: true);
    }

    private sealed class CwdScope : IDisposable
    {
        private readonly string _previous = Directory.GetCurrentDirectory();

        public CwdScope(string next)
        {
            Directory.CreateDirectory(next);
            Directory.SetCurrentDirectory(next);
        }

        public void Dispose() => Directory.SetCurrentDirectory(_previous);
    }
}

public sealed class StubSpecLoader : IDeclarativeSpecLoader
{
    public DeclarativeApplySpec? NextSpec { get; set; }

    public Task<Result<DeclarativeApplySpec>> LoadAsync(string specPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<DeclarativeApplySpec>.Success(NextSpec!));

    public Task<Result<DeclarativeApplySpec>> LoadFromTextAsync(string rawText, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<DeclarativeApplySpec>.Success(NextSpec!));
}
