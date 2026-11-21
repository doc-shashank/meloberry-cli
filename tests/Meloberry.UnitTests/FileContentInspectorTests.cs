using System.Text;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Infrastructure.Services;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class FileContentInspectorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_inspector_{Guid.NewGuid():N}");
    private readonly FileContentInspector _inspector = new();

    public FileContentInspectorTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }, ".jpg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x01, 0x00 }, ".gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, ".webp")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0x20, 0x00 }, ".bmp")]
    public async Task ResolveInputAsync_DetectsImagesByMagicBytes_WithoutFileExtension(byte[] headerBytes, string expectedExt)
    {
        var fileWithoutExt = Path.Combine(_tempDir, $"upload_{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(fileWithoutExt, headerBytes);

        var workspaceDir = Path.Combine(_tempDir, "ws");
        var res = await _inspector.ResolveInputAsync(ContentInputMethod.Path, fileWithoutExt, workspaceDir);

        Assert.True(res.IsSuccess);
        Assert.Equal(ResolvedContentKind.Image, res.Value.Kind);
        Assert.NotNull(res.Value.RelativeAssetPath);
        Assert.EndsWith(expectedExt, res.Value.RelativeAssetPath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(workspaceDir, res.Value.RelativeAssetPath)));
    }

    [Fact]
    public async Task ResolveInputAsync_DetectsSvgAndUtf8MarkdownCorrectly()
    {
        var svgFile = Path.Combine(_tempDir, "vector_no_ext");
        await File.WriteAllTextAsync(svgFile, "<svg xmlns=\"http://www.w3.org/2000/svg\"><circle r=\"10\"/></svg>", Encoding.UTF8);

        var mdFile = Path.Combine(_tempDir, "notes_no_ext");
        await File.WriteAllTextAsync(mdFile, "# Heading\n\nSome **Markdown** text.", Encoding.UTF8);

        var ws = Path.Combine(_tempDir, "ws2");
        var svgRes = await _inspector.ResolveInputAsync(ContentInputMethod.Path, svgFile, ws);
        var mdRes = await _inspector.ResolveInputAsync(ContentInputMethod.Path, mdFile, ws);

        Assert.True(svgRes.IsSuccess);
        Assert.Equal(ResolvedContentKind.Image, svgRes.Value.Kind);
        Assert.EndsWith(".svg", svgRes.Value.RelativeAssetPath);

        Assert.True(mdRes.IsSuccess);
        Assert.Equal(ResolvedContentKind.Text, mdRes.Value.Kind);
        Assert.Contains("**Markdown**", mdRes.Value.TextContent);
    }

    [Fact]
    public async Task ResolveInputAsync_RejectsUnrecognizedBinaryFiles()
    {
        var binFile = Path.Combine(_tempDir, "random.dat");
        await File.WriteAllBytesAsync(binFile, [0x00, 0xFF, 0xFE, 0x7F, 0x01, 0x00]);

        var res = await _inspector.ResolveInputAsync(ContentInputMethod.Path, binFile, Path.Combine(_tempDir, "ws3"));
        Assert.False(res.IsSuccess);
        Assert.Equal(ErrorType.Validation, res.Error!.Type);
        Assert.Equal("File.UnsupportedFormat", res.Error.Code);
    }
}
