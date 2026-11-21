using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Infrastructure.Persistence;

namespace Meloberry.Infrastructure.Services;

public sealed class FileContentInspector(ChainTransactionManager? transactionManager = null) : IContentInspector
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Regex DangerousSvgRegex = new(
        @"<\s*(?:script|foreignObject)\b|\bon[a-z]+\s*=|(?:javascript|vbscript)\s*:|data\s*:\s*text/html",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        byte[] bytes = await File.ReadAllBytesAsync(valueOrPath, cancellationToken);
        if (bytes.Length == 0)
            return new Error("File.Empty", $"File '{valueOrPath}' is empty.", ErrorType.Validation);

        if (TryDetectImageExtension(bytes, out var imageExt))
        {
            var relativeAsset = await CopyImageBytesToAssetsAsync(bytes, valueOrPath, imageExt, workspaceStorageDirectory, cancellationToken);
            return new ResolvedContent(ResolvedContentKind.Image, null, relativeAsset);
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

    public async Task<Result> VerifyStoredAssetAsync(string workspaceStorageDirectory, string relativeAssetPath, CancellationToken cancellationToken = default)
    {
        if (!PathConfinement.TryResolveInside(workspaceStorageDirectory, relativeAssetPath, out var fullPath))
            return new Error("Asset.InvalidPath", $"Invalid or escaping asset path: '{relativeAssetPath}'.", ErrorType.Validation);

        if (!File.Exists(fullPath))
            return new Error("Asset.Missing", $"Image file does not exist: '{fullPath}'.", ErrorType.Validation);

        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        return bytes.Length == 0 || !TryDetectImageExtension(bytes, out _)
            ? new Error("Asset.CorruptOrInvalid", $"Image file '{fullPath}' failed magic-byte image verification.", ErrorType.Validation)
            : Result.Success();
    }

    private async Task<string> CopyImageBytesToAssetsAsync(
        byte[] bytes, string originalPath, string canonicalExtension, string workspaceStorageDirectory, CancellationToken cancellationToken)
    {
        transactionManager?.NotifyWorkspaceMutating(workspaceStorageDirectory);
        var assetsDir = Path.Combine(workspaceStorageDirectory, "assets");
        Directory.CreateDirectory(assetsDir);

        var safeName = Path.GetFileNameWithoutExtension(originalPath);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "image";
        foreach (char invalid in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(invalid, '_');
        while (safeName.Contains("..", StringComparison.Ordinal)) safeName = safeName.Replace("..", "_", StringComparison.Ordinal);
        safeName = safeName.Trim('.');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "image";

        var hash8 = Convert.ToHexStringLower(SHA256.HashData(bytes))[..8];
        var fileName = $"{safeName}_{hash8}{canonicalExtension}";
        var destinationPath = Path.Combine(assetsDir, fileName);
        if (!File.Exists(destinationPath))
            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);

        return Path.Combine("assets", fileName).Replace('\\', '/');
    }

    internal static bool TryDetectImageExtension(ReadOnlySpan<byte> bytes, out string extension)
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

        if (bytes.Length >= 4 && !bytes.Contains((byte)0))
        {
            try
            {
                var fullText = StrictUtf8.GetString(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                var probe = fullText.Length > 1024 ? fullText[..1024] : fullText;
                if ((probe.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
                     (probe.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && probe.Contains("<svg", StringComparison.OrdinalIgnoreCase))) &&
                    !DangerousSvgRegex.IsMatch(fullText))
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
