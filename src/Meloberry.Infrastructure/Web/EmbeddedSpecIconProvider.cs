using System.Text;
using Meloberry.Application.Abstractions;

namespace Meloberry.Infrastructure.Web;

/// <summary>Serves embedded SVG spec and subtype icons owned by `mby-cli`.</summary>
public sealed class EmbeddedSpecIconProvider : ISpecIconProvider
{
    private const string Prefix = "Meloberry.Infrastructure.Web.Assets.SpecIcons.";
    private static readonly Lazy<IReadOnlyDictionary<string, string>> IconsLazy = new(LoadEmbeddedIcons);

    public IReadOnlyDictionary<string, string> GetIcons(IEnumerable<string>? topics = null)
    {
        var all = IconsLazy.Value;
        var topicList = topics?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (topicList is null || topicList.Count == 0)
            return all;

        var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, svg) in all)
        {
            if (topicList.Any(t =>
                string.Equals(key, t, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(t + "-", StringComparison.OrdinalIgnoreCase)))
            {
                filtered[key] = svg;
            }
        }

        return filtered;
    }

    private static IReadOnlyDictionary<string, string> LoadEmbeddedIcons()
    {
        var asm = typeof(EmbeddedSpecIconProvider).Assembly;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resName in asm.GetManifestResourceNames())
        {
            if (!resName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
                !resName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = resName[Prefix.Length..^4];
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream is null) continue;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            dict[key] = reader.ReadToEnd().Trim();
        }

        return dict;
    }
}
