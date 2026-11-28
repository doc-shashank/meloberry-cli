using System.Text.RegularExpressions;

namespace Meloberry.Domain.Scaffolding;

/// <summary>
/// Pure data-driven smart-wrapping and relative asset path helpers shared by scaffold generation
/// and single-fragment Markdown patching.
/// </summary>
public static partial class MarkdownSmartWrapper
{
    private static readonly HashSet<string> ImageCapableDirectiveTopics = new(StringComparer.OrdinalIgnoreCase)
        { "relabel", "diagram-explore", "mcq", "flashcard", "spot-error", "numeric", "case", "link" };

    [GeneratedRegex(@"!\[[^\]]*\]\((?:<([^>\r\n]+)>|([^)\s""]+))(?:\s+""[^""]*"")?\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"(?:image|imagePath|masks|masksPath)\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectiveAssetAttrRegex();

    [GeneratedRegex(@"^\s*(?:image|imagePath|masks|masksPath)\s*:\s*""?([^""\r\n]+)""?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex YamlAssetLineRegex();

    [GeneratedRegex(@"^\s*(?:image|imagePath)\s*:\s*""?([^""\r\n]+)""?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex YamlImageLineRegex();

    public static string FormatMarkdownImageTarget(string relPath) =>
        relPath.Any(c => char.IsWhiteSpace(c) || c is '(' or ')') ? $"<{relPath}>" : relPath;

    public static string SmartWrapSingleFragment(
        string topic,
        string? subtype,
        string rawBody,
        string? imagePath,
        string? caption,
        string? alt,
        out bool requiresPlaceholderImage,
        string? linkId = null,
        string? linkTitle = null)
    {
        requiresPlaceholderImage = false;
        var t = topic.Trim().ToLowerInvariant();
        var content = (rawBody ?? string.Empty).TrimStart('\uFEFF').Trim();
        var relImg = string.IsNullOrWhiteSpace(imagePath) ? null : imagePath.Trim().Replace('\\', '/');

        if (!string.IsNullOrWhiteSpace(relImg))
        {
            return WrapWithAttachedImage(t, subtype, content, relImg, caption, alt, out requiresPlaceholderImage, linkId, linkTitle);
        }

        if ((content.StartsWith(":::", StringComparison.Ordinal) && !IsInnerSubDirective(content)) ||
            content.StartsWith("{", StringComparison.Ordinal) ||
            (t == "image" && content.StartsWith("![", StringComparison.Ordinal)))
        {
            return content;
        }

        if (t == "text")
            return content;

        if (t == "image")
        {
            return !content.Contains('\n') && !string.IsNullOrWhiteSpace(content)
                ? $"![]({FormatMarkdownImageTarget(content)})"
                : content;
        }

        if (t == "link")
        {
            var id = string.IsNullOrWhiteSpace(linkId) ? "card-1" : linkId;
            var title = string.IsNullOrWhiteSpace(linkTitle) ? "Reference" : linkTitle;
            return $":::link{{id=\"{EscapeAttr(id)}\" title=\"{EscapeAttr(title)}\"}}\n{content}\n:::";
        }

        string? explicitImage = null;
        if (ImageCapableDirectiveTopics.Contains(t))
        {
            var m = YamlImageLineRegex().Match(content);
            if (m.Success)
            {
                explicitImage = m.Groups[1].Value.Trim().Replace('\\', '/');
                content = YamlImageLineRegex().Replace(content, string.Empty, 1).Trim();
            }
        }

        if (t == "flashcard" && !string.IsNullOrWhiteSpace(explicitImage))
        {
            var imgLine = $"![]({FormatMarkdownImageTarget(explicitImage)})";
            var wrappedBody = IsInnerSubDirective(content)
                ? InjectImageIntoFlashcardFront(content, imgLine)
                : $":::front\n{imgLine}\n{content}\n:::back\nAnswer";
            return $":::flashcard\n{wrappedBody}\n:::";
        }

        var attrs = BuildDefaultDirectiveAttributes(t, subtype, explicitImage, ref requiresPlaceholderImage);
        var header = attrs.Count == 0
            ? $":::{t}"
            : $":::{t}{{{string.Join(" ", attrs)}}}";
        return $"{header}\n{content}\n:::";
    }

    private static string WrapWithAttachedImage(
        string topic,
        string? subtype,
        string body,
        string relImg,
        string? caption,
        string? alt,
        out bool requiresPlaceholderImage,
        string? linkId,
        string? linkTitle)
    {
        requiresPlaceholderImage = false;
        if (topic == "image")
        {
            var safeAlt = (alt ?? string.Empty).Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal)
                .Replace('\r', ' ').Replace('\n', ' ').Trim();
            var safeCap = (caption ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace('\r', ' ').Replace('\n', ' ').Trim();
            var target = FormatMarkdownImageTarget(relImg);
            return string.IsNullOrWhiteSpace(safeCap)
                ? $"![{safeAlt}]({target})"
                : $"![{safeAlt}]({target} \"{safeCap}\")";
        }

        if (topic == "flashcard")
        {
            var target = FormatMarkdownImageTarget(relImg);
            var imgLine = $"![]({target})";
            if (string.IsNullOrWhiteSpace(body))
                return $":::flashcard\n:::front\n{imgLine}\n:::back\nAnswer\n:::";

            if (StartsWithOuterDirective(body, "flashcard"))
                return InjectImageIntoFlashcardFront(body, imgLine);

            if (IsInnerSubDirective(body))
                return $":::flashcard\n{InjectImageIntoFlashcardFront(body, imgLine)}\n:::";

            return $":::flashcard\n:::front\n{imgLine}\n{body}\n:::back\nAnswer\n:::";
        }

        if (topic == "link")
        {
            if (StartsWithOuterDirective(body, "link"))
                return InjectImageIntoPrefencedDirective(body, relImg);
            var id = string.IsNullOrWhiteSpace(linkId) ? "card-1" : linkId;
            var title = string.IsNullOrWhiteSpace(linkTitle) ? "Reference" : linkTitle;
            return string.IsNullOrWhiteSpace(body)
                ? $":::link{{id=\"{EscapeAttr(id)}\" title=\"{EscapeAttr(title)}\" image=\"{EscapeAttr(relImg)}\"}}\n:::"
                : $":::link{{id=\"{EscapeAttr(id)}\" title=\"{EscapeAttr(title)}\" image=\"{EscapeAttr(relImg)}\"}}\n{body}\n:::";
        }

        if (StartsWithOuterDirective(body, topic))
            return InjectImageIntoPrefencedDirective(body, relImg);

        var attrs = BuildDefaultDirectiveAttributes(topic, subtype, relImg, ref requiresPlaceholderImage);
        var header = attrs.Count == 0 ? $":::{topic}" : $":::{topic}{{{string.Join(" ", attrs)}}}";
        return string.IsNullOrWhiteSpace(body) ? $"{header}\n:::" : $"{header}\n{body}\n:::";
    }

    public static List<string> BuildDefaultDirectiveAttributes(
        string topic,
        string? subtype,
        string? explicitImage,
        ref bool requiresPlaceholderImg)
    {
        var attrs = new List<string>();
        switch (topic)
        {
            case "callout":
                attrs.Add($"tone=\"{(subtype is "pearl" or "clinical" or "warning" ? subtype : "pearl")}\"");
                break;
            case "table":
                if (string.Equals(subtype, "stickyFirstCol", StringComparison.OrdinalIgnoreCase)) attrs.Add("stickyFirstCol=true");
                if (string.Equals(subtype, "hideableCols", StringComparison.OrdinalIgnoreCase)) attrs.Add("hideableCols=\"2\"");
                break;
            case "reveal":
                if (string.Equals(subtype, "showFirst", StringComparison.OrdinalIgnoreCase)) attrs.Add("showFirst=true");
                break;
            case "mcq":
                if (string.Equals(subtype, "multi", StringComparison.OrdinalIgnoreCase)) attrs.Add("multi=true");
                break;
            case "fill-blank":
                if (string.Equals(subtype, "fuzzy", StringComparison.OrdinalIgnoreCase)) attrs.Add("fuzzy=true");
                if (string.Equals(subtype, "caseSensitive", StringComparison.OrdinalIgnoreCase)) attrs.Add("caseSensitive=true");
                break;
            case "assertion-reason":
                attrs.Add($"answer=\"{(string.Equals(subtype, "both-false", StringComparison.OrdinalIgnoreCase) ? "both-false" : "both-explain")}\"");
                if (subtype is "4opt" or "5opt") attrs.Add($"scheme=\"{subtype}\"");
                break;
            case "spot-error":
                if (string.Equals(subtype, "requireFix", StringComparison.OrdinalIgnoreCase)) attrs.Add("requireFix=true");
                if (string.Equals(subtype, "allowNoError", StringComparison.OrdinalIgnoreCase)) attrs.Add("allowNoError=true");
                break;
            case "diff":
                var diffMode = subtype is "fill" or "spot-wrong" or "rearrange" ? subtype : "fill";
                attrs.Add($"mode=\"{diffMode}\"");
                if (string.Equals(subtype, "bank", StringComparison.OrdinalIgnoreCase)) attrs.Add("bank=true");
                break;
            case "relabel":
                if (string.IsNullOrWhiteSpace(explicitImage)) requiresPlaceholderImg = true;
                attrs.Add($"image=\"{EscapeAttr(string.IsNullOrWhiteSpace(explicitImage) ? "assets/placeholder.png" : explicitImage)}\"");
                attrs.Add($"mode=\"{(subtype is "reveal" or "bank" or "recall" ? subtype : "bank")}\"");
                attrs.Add($"style=\"{(subtype is "solid" or "blur" or "pixelate" or "whiteout" ? subtype : "blur")}\"");
                break;
            case "diagram-explore":
                if (string.IsNullOrWhiteSpace(explicitImage)) requiresPlaceholderImg = true;
                attrs.Add($"image=\"{EscapeAttr(string.IsNullOrWhiteSpace(explicitImage) ? "assets/placeholder.png" : explicitImage)}\"");
                break;
        }

        if (!string.IsNullOrWhiteSpace(explicitImage) && topic is not ("relabel" or "diagram-explore" or "flashcard"))
        {
            attrs.Add($"image=\"{EscapeAttr(explicitImage)}\"");
        }

        return attrs;
    }

    public static void CollectReferencedAssets(string markdown, ISet<string> collected)
    {
        foreach (Match m in MarkdownImageRegex().Matches(markdown))
        {
            var val = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            TryAddRelativeAsset(val, collected);
        }

        foreach (Match m in DirectiveAssetAttrRegex().Matches(markdown))
            TryAddRelativeAsset(m.Groups[1].Value, collected);

        foreach (Match m in YamlAssetLineRegex().Matches(markdown))
            TryAddRelativeAsset(m.Groups[1].Value, collected);
    }

    public static void TryAddRelativeAsset(string raw, ISet<string> collected)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("mby://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("assets/placeholder.png", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(trimmed))
        {
            return;
        }

        var normalized = trimmed.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (!string.IsNullOrWhiteSpace(normalized))
            collected.Add(normalized);
    }

    private static bool StartsWithOuterDirective(string body, string topic)
    {
        if (!body.StartsWith(":::", StringComparison.Ordinal))
            return false;

        var nlIdx = body.IndexOf('\n');
        var firstLine = (nlIdx >= 0 ? body[..nlIdx] : body).TrimEnd('\r').Trim();
        var afterColons = firstLine.TrimStart(':');
        if (!afterColons.StartsWith(topic, StringComparison.OrdinalIgnoreCase))
            return false;

        if (afterColons.Length == topic.Length)
            return true;

        var boundary = afterColons[topic.Length];
        return boundary is '{' or ' ' or '\t';
    }

    private static bool IsInnerSubDirective(string content)
    {
        var nlIdx = content.IndexOf('\n');
        var firstLine = (nlIdx >= 0 ? content[..nlIdx] : content).TrimEnd('\r').Trim();
        return firstLine.Equals(":::front", StringComparison.OrdinalIgnoreCase) ||
               firstLine.Equals(":::back", StringComparison.OrdinalIgnoreCase);
    }

    private static string InjectImageIntoFlashcardFront(string content, string imgLine)
    {
        if (content.Contains(imgLine, StringComparison.OrdinalIgnoreCase))
            return content;

        const string frontMarker = ":::front";
        var idx = content.IndexOf(frontMarker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return content;

        var insertPos = content.IndexOf('\n', idx);
        return insertPos < 0 ? $"{content}\n{imgLine}" : content.Insert(insertPos + 1, $"{imgLine}\n");
    }

    private static string InjectImageIntoPrefencedDirective(string body, string relImg)
    {
        var nlIdx = body.IndexOf('\n');
        var firstLine = nlIdx >= 0 ? body[..nlIdx] : body;
        var rest = nlIdx >= 0 ? body[nlIdx..] : string.Empty;
        var cleanFirst = firstLine.TrimEnd('\r');
        if (cleanFirst.Contains("image=", StringComparison.OrdinalIgnoreCase))
            return body;

        var braceClose = cleanFirst.LastIndexOf('}');
        string updatedFirst;
        if (braceClose > 0)
        {
            var inner = cleanFirst[..braceClose].TrimEnd();
            var sep = inner.EndsWith('{') ? string.Empty : " ";
            updatedFirst = $"{inner}{sep}image=\"{EscapeAttr(relImg)}\"}}";
        }
        else
        {
            updatedFirst = $"{cleanFirst.TrimEnd()}{{image=\"{EscapeAttr(relImg)}\"}}";
        }

        var suffix = firstLine.EndsWith('\r') ? "\r" : string.Empty;
        return $"{updatedFirst}{suffix}{rest}";
    }

    private static string EscapeAttr(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
             .Replace('\r', ' ')
             .Replace('\n', ' ')
             .Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);
}
