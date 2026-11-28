using System.Text;
using Meloberry.Application.Abstractions;
using Meloberry.Infrastructure.Packaging;

namespace Meloberry.Infrastructure.Services;

public sealed class FileBuildSignalWriter(ICliToolMetadataProvider metadataProvider) : IBuildSignalWriter
{
    public const string SignalFileName = ".mby-signal.txt";

    public async Task<string> WriteWebAppBuiltSignalAsync(
        BuildSignalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var signalDir = !string.IsNullOrWhiteSpace(request.SignalDirectory)
            ? Path.GetFullPath(request.SignalDirectory.Trim())
            : !string.IsNullOrWhiteSpace(request.StorageDirectory)
                ? MbypkgExporter.ResolveDefaultOutputBaseDirectory(request.StorageDirectory)
                : Directory.GetCurrentDirectory();

        Directory.CreateDirectory(signalDir);

        var targetPath = Path.Combine(signalDir, SignalFileName);
        var tempPath = targetPath + ".tmp";
        var meta = metadataProvider.GetMetadata();

        var sb = new StringBuilder();
        sb.AppendLine("version=1");
        sb.AppendLine("event=webapp_built");
        sb.AppendLine($"id={Guid.NewGuid():N}");
        sb.AppendLine($"timestamp={DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"workspace_id={SanitizeLineValue(request.WorkspaceId)}");
        sb.AppendLine($"path={SanitizeLineValue(Path.GetFullPath(request.WebOutputDirectory))}");
        sb.AppendLine($"title={SanitizeLineValue(request.Title)}");
        sb.AppendLine($"producer={SanitizeLineValue($"{meta.ExecutableName}/{meta.Version}")}");

        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        await using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(sb.ToString());
            await writer.FlushAsync(cancellationToken);
            await fs.FlushAsync(cancellationToken);
        }

        File.Move(tempPath, targetPath, overwrite: true);
        return targetPath;
    }

    private static string SanitizeLineValue(string? value) =>
        (value ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
