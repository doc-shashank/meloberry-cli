using System.Reflection;
using System.Runtime.InteropServices;

namespace Meloberry.Application.Abstractions;

/// <summary>
/// Resolves unified CLI tool metadata from compiled MSBuild assembly attributes.
/// Safe under single-file publishing and test runners (does not rely on entry assembly or Assembly.Location).
/// </summary>
public sealed class AssemblyCliToolMetadataProvider(
    Assembly assembly,
    string executableName = "mby") : ICliToolMetadataProvider
{
    private readonly CliToolMetadata _cached = ReadFromAssembly(assembly, executableName);

    public CliToolMetadata GetMetadata() => _cached;

    private static CliToolMetadata ReadFromAssembly(Assembly assembly, string executableName)
    {
        var rawInfoVer = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var cleanVer = rawInfoVer?.Split('+', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(cleanVer))
        {
            var asmVer = assembly.GetName().Version;
            cleanVer = asmVer is not null ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "1.0.0";
        }

        var productName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        if (string.IsNullOrWhiteSpace(productName))
            productName = "Meloberry CLI";

        return new CliToolMetadata(
            ProductName: productName,
            ExecutableName: executableName,
            Version: cleanVer,
            Runtime: RuntimeInformation.FrameworkDescription);
    }
}
