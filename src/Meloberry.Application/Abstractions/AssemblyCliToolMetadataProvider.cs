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

        var metadataAttrs = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        var appName = metadataAttrs
            .FirstOrDefault(a => string.Equals(a.Key, "AppName", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(appName))
            appName = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        if (string.IsNullOrWhiteSpace(appName))
            appName = "Meloberry";

        var repoUrl = metadataAttrs
            .FirstOrDefault(a => string.Equals(a.Key, "RepositoryUrl", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(repoUrl))
            repoUrl = "https://github.com/doc-shashank/meloberry-cli/";

        return new CliToolMetadata(
            ProductName: productName,
            ExecutableName: executableName,
            Version: cleanVer,
            Runtime: RuntimeInformation.FrameworkDescription,
            AppName: appName.Trim(),
            RepositoryUrl: repoUrl.Trim());
    }
}
