using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Infrastructure.Persistence;

namespace Meloberry.Infrastructure.Services;

public sealed partial class FileContentInspector(ChainTransactionManager? transactionManager = null) : IContentInspector
{
    private const int RasterHeaderProbeBytes = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [GeneratedRegex(@"<\s*(?:script|foreignObject)\b|\bon[a-z]+\s*=|(?:javascript|vbscript)\s*:|data\s*:\s*text/html", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DangerousSvgRegex();

    public async Task<Result<ResolvedContent>> ResolveInputAsync(
        ContentInputMethod method, string valueOrPath, string workspaceStorageDirectory,
        bool requireImageOnly = false, CancellationToken cancellationToken = default)
    {
        if (method == ContentInputMethod.String)
        {
            if (requireImageOnly) return new Error("Input.ImagePathRequired", "Expected an image file path.", ErrorType.Validation);
            if (string.IsNullOrWhiteSpace(valueOrPath)) return new Error("Input.EmptyString", "Text value cannot be empty.", ErrorType.Validation);
            return new ResolvedContent(ResolvedContentKind.Text, valueOrPath, null);
        }

        if (string.IsNullOrWhiteSpace(valueOrPath) || !File.Exists(valueOrPath))
            return new Error("File.NotFound", $"File '{valueOrPath}' was not found.", ErrorType.NotFound);

        await using var fs = new FileStream(
            valueOrPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (fs.Length == 0)
            return new Error("File.Empty", $"File '{valueOrPath}' is empty.", ErrorType.Validation);

        var headerBuf = new byte[RasterHeaderProbeBytes];
        int headerLen = await ReadHeaderBytesAsync(fs, headerBuf, cancellationToken);
        if (TryDetectRasterImageExtension(headerBuf.AsSpan(0, headerLen), out var rasterExt))
        {
            var (relativeAsset, alreadyExisted) = await CopyRasterStreamToAssetsAsync(fs, valueOrPath, rasterExt, workspaceStorageDirectory, cancellationToken);
            return new ResolvedContent(ResolvedContentKind.Image, null, relativeAsset, alreadyExisted);
        }

        fs.Position = 0;
        var bytes = new byte[fs.Length];
        await fs.ReadExactlyAsync(bytes, cancellationToken);

        if (TryDetectImageExtension(bytes, out var imageExt))
        {
            var (relativeAsset, alreadyExisted) = await CopyImageBytesToAssetsAsync(bytes, valueOrPath, imageExt, workspaceStorageDirectory, cancellationToken);
            return new ResolvedContent(ResolvedContentKind.Image, null, relativeAsset, alreadyExisted);
        }

        if (requireImageOnly)
            return new Error("File.InvalidImage", $"File '{valueOrPath}' is not a supported or safe image (PNG, JPEG, GIF, WebP, BMP, sanitized SVG).", ErrorType.Validation);

        if (TryDecodeStrictUtf8Text(bytes, out var text))
            return new ResolvedContent(ResolvedContentKind.Text, text, null);

        return new Error("File.UnsupportedFormat", $"File '{valueOrPath}' is neither a recognized image nor valid UTF-8 text.", ErrorType.Validation);
    }

    public async Task<Result<string>> CopyAndVerifyImageAsync(string sourceImagePath, string workspaceStorageDirectory, CancellationToken cancellationToken = default)
    {
        var result = await ResolveInputAsync(ContentInputMethod.Path, sourceImagePath, workspaceStorageDirectory, requireImageOnly: true, cancellationToken);
        return result.IsSuccess ? result.Value.RelativeAssetPath! : result.Error!;
    }

    public async Task<Result<CopiedAssetResult>> CopyAndVerifyImageWithStatusAsync(string sourceImagePath, string workspaceStorageDirectory, CancellationToken cancellationToken = default)
    {
        var result = await ResolveInputAsync(ContentInputMethod.Path, sourceImagePath, workspaceStorageDirectory, requireImageOnly: true, cancellationToken);
        return result.IsSuccess
            ? new CopiedAssetResult(result.Value.RelativeAssetPath!, result.Value.AlreadyExisted)
            : result.Error!;
    }

    public async Task<Result> VerifyStoredAssetAsync(string workspaceStorageDirectory, string relativeAssetPath, CancellationToken cancellationToken = default)
    {
        if (!PathConfinement.TryResolveInside(workspaceStorageDirectory, relativeAssetPath, out var fullPath))
            return new Error("Asset.InvalidPath", $"Invalid or escaping asset path: '{relativeAssetPath}'.", ErrorType.Validation);

        if (!File.Exists(fullPath))
            return new Error("Asset.Missing", $"Image file does not exist: '{fullPath}'.", ErrorType.Validation);

        await using var fs = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (fs.Length == 0)
            return new Error("Asset.CorruptOrInvalid", $"Image file '{fullPath}' failed magic-byte image verification.", ErrorType.Validation);

        var headerBuf = new byte[RasterHeaderProbeBytes];
        int headerLen = await ReadHeaderBytesAsync(fs, headerBuf, cancellationToken);
        var headerSpan = headerBuf.AsSpan(0, headerLen);

        if (TryDetectRasterImageExtension(headerSpan, out _))
            return Result.Success();

        if (CouldBeSvgHeader(headerSpan))
        {
            fs.Position = 0;
            var fullBytes = new byte[fs.Length];
            await fs.ReadExactlyAsync(fullBytes, cancellationToken);
            if (TryDetectImageExtension(fullBytes, out _))
                return Result.Success();
        }

        return new Error("Asset.CorruptOrInvalid", $"Image file '{fullPath}' failed magic-byte image verification.", ErrorType.Validation);
    }

    private static async Task<int> ReadHeaderBytesAsync(FileStream fs, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await fs.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private static string BuildCanonicalAssetFileName(string originalPath, string hash8, string canonicalExtension)
    {
        var safeName = Path.GetFileNameWithoutExtension(originalPath);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "image";
        foreach (char invalid in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(invalid, '_');
        while (safeName.Contains("..", StringComparison.Ordinal)) safeName = safeName.Replace("..", "_", StringComparison.Ordinal);
        safeName = safeName.Trim('.');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "image";
        if (safeName.EndsWith($"_{hash8}", StringComparison.OrdinalIgnoreCase))
            return $"{safeName}{canonicalExtension}";
        return $"{safeName}_{hash8}{canonicalExtension}";
    }

    private async Task<(string RelativePath, bool AlreadyExisted)> CopyRasterStreamToAssetsAsync(
        FileStream sourceStream, string originalPath, string canonicalExtension, string workspaceStorageDirectory, CancellationToken cancellationToken)
    {
        transactionManager?.NotifyWorkspaceMutating(workspaceStorageDirectory);
        var assetsDir = Path.Combine(workspaceStorageDirectory, "assets");
        Directory.CreateDirectory(assetsDir);

        sourceStream.Position = 0;
        var hashBytes = await SHA256.HashDataAsync(sourceStream, cancellationToken);
        var hash8 = Convert.ToHexStringLower(hashBytes)[..8];
        var fileName = BuildCanonicalAssetFileName(originalPath, hash8, canonicalExtension);
        var destinationPath = Path.Combine(assetsDir, fileName);

        bool existed = File.Exists(destinationPath);
        if (!existed)
        {
            sourceStream.Position = 0;
            await using var dst = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await sourceStream.CopyToAsync(dst, 81920, cancellationToken);
        }

        return (Path.Combine("assets", fileName).Replace('\\', '/'), existed);
    }

    private async Task<(string RelativePath, bool AlreadyExisted)> CopyImageBytesToAssetsAsync(
        byte[] bytes, string originalPath, string canonicalExtension, string workspaceStorageDirectory, CancellationToken cancellationToken)
    {
        transactionManager?.NotifyWorkspaceMutating(workspaceStorageDirectory);
        var assetsDir = Path.Combine(workspaceStorageDirectory, "assets");
        Directory.CreateDirectory(assetsDir);

        var hash8 = Convert.ToHexStringLower(SHA256.HashData(bytes))[..8];
        var fileName = BuildCanonicalAssetFileName(originalPath, hash8, canonicalExtension);
        var destinationPath = Path.Combine(assetsDir, fileName);
        bool existed = File.Exists(destinationPath);
        if (!existed)
            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);

        return (Path.Combine("assets", fileName).Replace('\\', '/'), existed);
    }

    private static bool CouldBeSvgHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4 || header.Contains((byte)0))
            return false;

        int i = 0;
        if (header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            i = 3;

        while (i < header.Length && header[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;

        return i >= header.Length || header[i] == (byte)'<';
    }

    internal static bool TryDetectRasterImageExtension(ReadOnlySpan<byte> bytes, out string extension)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        { extension = ".png"; return true; }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        { extension = ".jpg"; return true; }

        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8' &&
            (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a')
        { extension = ".gif"; return true; }

        if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
        { extension = ".webp"; return true; }

        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
        { extension = ".bmp"; return true; }

        extension = string.Empty;
        return false;
    }

    internal static bool TryDetectImageExtension(ReadOnlySpan<byte> bytes, out string extension)
    {
        if (TryDetectRasterImageExtension(bytes, out extension))
            return true;

        if (bytes.Length >= 4 && !bytes.Contains((byte)0))
        {
            try
            {
                var fullText = StrictUtf8.GetString(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                var probe = fullText.Length > 1024 ? fullText[..1024] : fullText;
                if ((probe.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
                     (probe.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && probe.Contains("<svg", StringComparison.OrdinalIgnoreCase))) &&
                    !DangerousSvgRegex().IsMatch(fullText))
                {
                    extension = ".svg";
                    return true;
                }
            }
            catch { }
        }

        extension = string.Empty;
        return false;
    }

    private static bool TryDecodeStrictUtf8Text(byte[] bytes, out string text)
    {
        if (bytes.AsSpan().Contains((byte)0)) { text = string.Empty; return false; }
        try
        {
            ReadOnlySpan<byte> payload = bytes;
            if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF) payload = payload[3..];
            text = StrictUtf8.GetString(payload);
            return true;
        }
        catch (DecoderFallbackException) { text = string.Empty; return false; }
    }
}
