using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Sessions;

namespace Meloberry.Infrastructure.Services;

internal sealed record ParsedMarkdownDocument(
    string? Kind,
    string? Name,
    string? Description,
    string? Title,
    string? Ref,
    Dictionary<string, string>? LayoutOverrides,
    IReadOnlyList<string>? Order,
    IReadOnlyList<DeclarativeArticleBlockSpec> Blocks);

internal static partial class MarkdownSpecParser
{
    private static readonly HashSet<string> AllowedFrontmatterKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "kind", "title", "ref", "name", "description", "layoutOverrides", "order"
    };

    [GeneratedRegex(@"^:::\s*(?<name>[a-zA-Z_][a-zA-Z0-9_-]*)\b", RegexOptions.Compiled)]
    private static partial Regex UnknownDirectiveFenceRegex();

    [GeneratedRegex(@"^:::\s*callout(?:\s*\{(?<attrs>[^}]*)\})?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CalloutAttrsFenceRegex();

    [GeneratedRegex(@"^:::\s*(?<tone>pearl|clinical|warning)(?:\s+(?<title>.+?))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CalloutShorthandFenceRegex();

    [GeneratedRegex(@"(?<k>[a-zA-Z_][a-zA-Z0-9_-]*)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s}]+))", RegexOptions.Compiled)]
    private static partial Regex CalloutAttrPairRegex();

    [GeneratedRegex(@"^#\s+(?<heading>.+?)\s*$", RegexOptions.Compiled)]
    private static partial Regex H1HeadingRegex();

    public static bool LooksLikeMarkdown(string rawText)
    {
        var trimmed = rawText.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.Length == 0)
            return false;
        char first = trimmed[0];
        if (first is '{' or '[' or '"')
            return false;
        if (char.IsDigit(first) || (first == '-' && trimmed.Length > 1 && char.IsDigit(trimmed[1])))
            return false;
        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("null", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static Result<DeclarativeApplySpec> ParseSingleSpec(string rawMarkdown, string sourceLabel, string? filePath = null)
    {
        var parsedRes = ParseDocument(rawMarkdown, sourceLabel, isSessionManifest: false);
        if (!parsedRes.IsSuccess) return parsedRes.Error!;
        var doc = parsedRes.Value;

        var title = !string.IsNullOrWhiteSpace(doc.Title)
            ? doc.Title
            : ExtractFirstH1(doc.Blocks) ?? HumanizeFileStem(filePath);

        if (string.Equals(doc.Kind, "session", StringComparison.OrdinalIgnoreCase))
        {
            var comps = new List<DeclarativeComponentSpec>();
            bool hasBody = doc.Blocks.Count > 0;
            if (hasBody)
            {
                comps.Add(new DeclarativeComponentSpec(
                    "article",
                    title,
                    null,
                    doc.Blocks,
                    null,
                    doc.LayoutOverrides,
                    doc.Ref,
                    filePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(filePath))));
            }
            return new DeclarativeApplySpec(
                "session",
                doc.Name ?? title,
                doc.Description,
                null,
                null,
                comps,
                null,
                null,
                hasBody ? null : doc.LayoutOverrides,
                filePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(filePath)),
                Ref: hasBody ? null : doc.Ref,
                Order: doc.Order);
        }

        if (!string.IsNullOrWhiteSpace(doc.Kind) && !string.Equals(doc.Kind, "article", StringComparison.OrdinalIgnoreCase))
        {
            return new Error("Markdown.InvalidFrontmatter", $"Unsupported Markdown frontmatter kind '{doc.Kind}' in '{sourceLabel}'. Use 'article' or 'session'.", ErrorType.Validation);
        }

        if (doc.Blocks.Count == 0)
            return new Error("Markdown.EmptyBody", $"Markdown article '{sourceLabel}' has no content blocks.", ErrorType.Validation);

        return new DeclarativeApplySpec(
            Kind: "article",
            Name: doc.Name,
            Description: doc.Description,
            Title: title,
            QuizType: null,
            Components: null,
            Blocks: doc.Blocks,
            Questions: null,
            LayoutOverrides: doc.LayoutOverrides,
            AssetBaseDirectory: filePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(filePath)),
            Ref: doc.Ref,
            Order: doc.Order);
    }

    public static Result<DeclarativeComponentSpec> ParseComponent(string rawMarkdown, string filePath)
    {
        var parsedRes = ParseDocument(rawMarkdown, filePath, isSessionManifest: false);
        if (!parsedRes.IsSuccess) return parsedRes.Error!;
        var doc = parsedRes.Value;

        if (!string.IsNullOrWhiteSpace(doc.Kind) && !string.Equals(doc.Kind, "article", StringComparison.OrdinalIgnoreCase))
            return new Error("Markdown.InvalidFrontmatter", $"Component Markdown file '{Path.GetFileName(filePath)}' must have kind 'article' (or omit kind).", ErrorType.Validation);

        if (doc.Blocks.Count == 0)
            return new Error("Markdown.EmptyBody", $"Markdown article '{Path.GetFileName(filePath)}' has no content blocks.", ErrorType.Validation);

        var title = !string.IsNullOrWhiteSpace(doc.Title)
            ? doc.Title
            : ExtractFirstH1(doc.Blocks) ?? HumanizeFileStem(filePath);

        return new DeclarativeComponentSpec(
            Kind: "article",
            Title: title,
            QuizType: null,
            Blocks: doc.Blocks,
            Questions: null,
            LayoutOverrides: doc.LayoutOverrides,
            Ref: doc.Ref,
            SourceDirectory: Path.GetDirectoryName(Path.GetFullPath(filePath)));
    }

    public static Result<ParsedMarkdownDocument> ParseDocument(string rawMarkdown, string sourceLabel, bool isSessionManifest)
    {
        var normalized = rawMarkdown.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        int bodyStartLine = 0;

        string? kind = null;
        string? name = null;
        string? description = null;
        string? title = null;
        string? refKey = null;
        Dictionary<string, string>? layoutOverrides = null;
        List<string>? order = null;

        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            int endIdx = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    endIdx = i;
                    break;
                }
            }
            if (endIdx < 0)
                return new Error("Markdown.InvalidFrontmatter", $"Unclosed YAML frontmatter starting on line 1 of '{sourceLabel}'. Add closing '---'.", ErrorType.Validation, Path: "frontmatter:1");

            var fmRes = ParseFrontmatter(lines, 1, endIdx, sourceLabel,
                out kind, out name, out description, out title, out refKey, out layoutOverrides, out order);
            if (!fmRes.IsSuccess) return fmRes.Error!;
            bodyStartLine = endIdx + 1;
        }

        if (isSessionManifest)
        {
            return new ParsedMarkdownDocument(kind, name, description, title, refKey, layoutOverrides, order, []);
        }

        var blocksRes = ParseBodyBlocks(lines, bodyStartLine, sourceLabel);
        if (!blocksRes.IsSuccess) return blocksRes.Error!;

        return new ParsedMarkdownDocument(kind, name, description, title, refKey, layoutOverrides, order, blocksRes.Value);
    }

    private static Result ParseFrontmatter(
        string[] lines,
        int startLineIdx,
        int endLineIdx,
        string sourceLabel,
        out string? kind,
        out string? name,
        out string? description,
        out string? title,
        out string? refKey,
        out Dictionary<string, string>? layoutOverrides,
        out List<string>? order)
    {
        kind = null; name = null; description = null; title = null; refKey = null;
        layoutOverrides = null; order = null;
        string? currentSection = null;

        for (int i = startLineIdx; i < endLineIdx; i++)
        {
            int lineNum = i + 1;
            var rawLine = lines[i];
            if (string.IsNullOrWhiteSpace(rawLine) || rawLine.TrimStart().StartsWith('#'))
                continue;

            bool indented = rawLine.Length > 0 && (rawLine[0] == ' ' || rawLine[0] == '\t');
            var trimmed = rawLine.Trim();

            if (indented && string.Equals(currentSection, "layoutOverrides", StringComparison.OrdinalIgnoreCase))
            {
                int colon = trimmed.IndexOf(':');
                if (colon <= 0)
                    return new Error("Markdown.InvalidFrontmatter", $"Expected 'key: value' inside layoutOverrides at line {lineNum} of '{sourceLabel}'.", ErrorType.Validation, Path: $"frontmatter:{lineNum}");
                layoutOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                layoutOverrides[trimmed[..colon].Trim()] = UnquoteYamlScalar(trimmed[(colon + 1)..]);
                continue;
            }

            if ((indented || trimmed.StartsWith('-')) && string.Equals(currentSection, "order", StringComparison.OrdinalIgnoreCase) && trimmed.StartsWith('-'))
            {
                order ??= [];
                var item = UnquoteYamlScalar(trimmed[1..]);
                if (!string.IsNullOrWhiteSpace(item)) order.Add(item);
                continue;
            }

            currentSection = null;
            int sep = trimmed.IndexOf(':');
            if (sep <= 0)
                return new Error("Markdown.InvalidFrontmatter", $"Invalid YAML frontmatter syntax at line {lineNum} of '{sourceLabel}'.", ErrorType.Validation, Path: $"frontmatter:{lineNum}");

            var key = trimmed[..sep].Trim();
            var rawVal = trimmed[(sep + 1)..].Trim();

            if (!AllowedFrontmatterKeys.Contains(key))
            {
                var suggestion = DeclarativeSpecLoader.FindClosestKey(key, AllowedFrontmatterKeys);
                return new Error(
                    "Markdown.InvalidFrontmatter",
                    $"Unknown frontmatter key '{key}' at line {lineNum} of '{sourceLabel}'. Allowed keys: kind, title, ref, name, description, layoutOverrides, order.",
                    ErrorType.Validation,
                    Path: $"frontmatter:{lineNum}",
                    Hint: suggestion is null ? null : $"Did you mean '{suggestion}'?");
            }

            if (string.Equals(key, "layoutOverrides", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(rawVal))
                {
                    currentSection = "layoutOverrides";
                    continue;
                }

                if (rawVal.StartsWith('{') && rawVal.EndsWith('}'))
                {
                    var inner = rawVal[1..^1].Trim();
                    if (!string.IsNullOrEmpty(inner))
                    {
                        layoutOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var part in SplitRespectingQuotes(inner, ','))
                        {
                            int colon = part.IndexOf(':');
                            if (colon <= 0)
                                return new Error("Markdown.InvalidFrontmatter", $"Expected 'key: value' inside inline layoutOverrides at line {lineNum} of '{sourceLabel}'.", ErrorType.Validation, Path: $"frontmatter:{lineNum}");
                            layoutOverrides[part[..colon].Trim()] = UnquoteYamlScalar(part[(colon + 1)..]);
                        }
                    }
                    continue;
                }

                return new Error(
                    "Markdown.InvalidFrontmatter",
                    $"Invalid 'layoutOverrides' value at line {lineNum} of '{sourceLabel}'. Use indented 'key: value' lines or inline '{{ key: value }}'.",
                    ErrorType.Validation,
                    Path: $"frontmatter:{lineNum}");
            }

            if (string.Equals(key, "order", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(rawVal))
                {
                    currentSection = "order";
                }
                else if (rawVal.StartsWith('[') && rawVal.EndsWith(']'))
                {
                    order ??= [];
                    foreach (var part in SplitRespectingQuotes(rawVal[1..^1], ','))
                    {
                        var val = UnquoteYamlScalar(part);
                        if (!string.IsNullOrWhiteSpace(val)) order.Add(val);
                    }
                }
                else
                {
                    return new Error(
                        "Markdown.InvalidFrontmatter",
                        $"Invalid 'order' value at line {lineNum} of '{sourceLabel}'. Use indented '- filename' lines or inline '[file1, file2]'.",
                        ErrorType.Validation,
                        Path: $"frontmatter:{lineNum}");
                }
                continue;
            }

            var scalar = UnquoteYamlScalar(rawVal);
            switch (key.ToLowerInvariant())
            {
                case "kind": kind = scalar; break;
                case "title": title = scalar; break;
                case "ref": refKey = scalar; break;
                case "name": name = scalar; break;
                case "description": description = scalar; break;
            }
        }

        return Result.Success();
    }

    private static Result<IReadOnlyList<DeclarativeArticleBlockSpec>> ParseBodyBlocks(string[] lines, int startLineIdx, string sourceLabel)
    {
        var blocks = new List<DeclarativeArticleBlockSpec>();
        var textBuffer = new StringBuilder();
        string? activeCodeFence = null;

        void FlushText()
        {
            if (textBuffer.Length == 0) return;
            var content = textBuffer.ToString().Trim();
            textBuffer.Clear();
            if (!string.IsNullOrWhiteSpace(content))
                blocks.Add(new DeclarativeArticleBlockSpec("text", content, null));
        }

        for (int i = startLineIdx; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (activeCodeFence is null && TryOpenCodeFence(trimmed, out var fenceToken))
            {
                activeCodeFence = fenceToken;
                textBuffer.AppendLine(line);
                continue;
            }

            if (activeCodeFence is not null)
            {
                textBuffer.AppendLine(line);
                if (IsCloseCodeFence(trimmed, activeCodeFence))
                    activeCodeFence = null;
                continue;
            }

            // Callout container :::callout{...} or :::pearl|clinical|warning [Title]
            if (TryMatchCalloutOpen(trimmed, out var rawTone, out var calloutTitle))
            {
                int openLine = i + 1;
                if (!ArticleBlock.TryParseCalloutTone(rawTone, out _))
                    return ArticleErrors.InvalidCalloutTone(rawTone);

                var calloutBody = new StringBuilder();
                bool closed = false;
                string? innerCodeFence = null;

                for (i = i + 1; i < lines.Length; i++)
                {
                    var innerLine = lines[i];
                    var innerTrim = innerLine.Trim();
                    if (innerCodeFence is null && TryOpenCodeFence(innerTrim, out var innerToken))
                    {
                        innerCodeFence = innerToken;
                        calloutBody.AppendLine(innerLine);
                        continue;
                    }
                    if (innerCodeFence is not null)
                    {
                        calloutBody.AppendLine(innerLine);
                        if (IsCloseCodeFence(innerTrim, innerCodeFence))
                            innerCodeFence = null;
                        continue;
                    }
                    if (innerTrim == ":::")
                    {
                        closed = true;
                        break;
                    }
                    calloutBody.AppendLine(innerLine);
                }

                if (!closed)
                {
                    return new Error(
                        "Markdown.UnclosedCallout",
                        $"Unclosed ':::' callout block starting at line {openLine} of '{sourceLabel}'.",
                        ErrorType.Validation,
                        Path: $"markdown:{openLine}");
                }

                var bodyText = calloutBody.ToString().Trim();
                if (string.IsNullOrWhiteSpace(bodyText))
                    return ArticleErrors.EmptyCalloutContent();

                FlushText();
                blocks.Add(new DeclarativeArticleBlockSpec(
                    "callout",
                    bodyText,
                    null,
                    Tone: rawTone.ToLowerInvariant(),
                    Title: string.IsNullOrWhiteSpace(calloutTitle) ? null : calloutTitle));
                continue;
            }

            // Reject unknown :::directive fences rather than silently rendering them as plain text
            var unknownFence = UnknownDirectiveFenceRegex().Match(trimmed);
            if (unknownFence.Success)
            {
                var directive = unknownFence.Groups["name"].Value;
                return new Error(
                    "Markdown.UnknownFence",
                    $"Unsupported fenced directive ':::{directive}' at line {i + 1} of '{sourceLabel}'. Supported fences: ':::callout', ':::pearl', ':::clinical', ':::warning'.",
                    ErrorType.Validation,
                    Path: $"markdown:{i + 1}",
                    Hint: "Use ':::callout{tone=\"pearl\" title=\"...\"}', ':::pearl', ':::clinical', or ':::warning'.");
            }

            // Standalone Markdown image: ![alt](path "caption") or ![alt](<path with spaces> "caption")
            if (TryParseStandaloneImage(trimmed, out var alt, out var path, out var caption))
            {
                FlushText();
                blocks.Add(new DeclarativeArticleBlockSpec(
                    "image",
                    null,
                    path,
                    Caption: string.IsNullOrWhiteSpace(caption) ? null : caption,
                    Alt: string.IsNullOrWhiteSpace(alt) ? null : alt));
                continue;
            }

            textBuffer.AppendLine(line);
        }

        FlushText();
        return blocks;
    }

    private static bool TryMatchCalloutOpen(string trimmedLine, out string tone, out string? title)
    {
        tone = "pearl";
        title = null;

        var attrMatch = CalloutAttrsFenceRegex().Match(trimmedLine);
        if (attrMatch.Success)
        {
            var attrs = attrMatch.Groups["attrs"].Value;
            foreach (Match m in CalloutAttrPairRegex().Matches(attrs))
            {
                var k = m.Groups["k"].Value.Trim();
                var v = m.Groups["dq"].Success ? m.Groups["dq"].Value
                    : (m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["bare"].Value);
                if (string.Equals(k, "tone", StringComparison.OrdinalIgnoreCase))
                    tone = v.Trim();
                else if (string.Equals(k, "title", StringComparison.OrdinalIgnoreCase))
                    title = v.Trim();
            }
            return true;
        }

        var shortMatch = CalloutShorthandFenceRegex().Match(trimmedLine);
        if (shortMatch.Success)
        {
            tone = shortMatch.Groups["tone"].Value.Trim();
            if (shortMatch.Groups["title"].Success)
                title = UnquoteYamlScalar(shortMatch.Groups["title"].Value);
            return true;
        }

        return false;
    }

    private static bool TryOpenCodeFence(string trimmed, out string fenceToken)
    {
        fenceToken = string.Empty;
        if (trimmed.Length < 3) return false;
        char ch = trimmed[0];
        if (ch is not ('`' or '~')) return false;
        int len = 0;
        while (len < trimmed.Length && trimmed[len] == ch) len++;
        if (len < 3) return false;
        fenceToken = new string(ch, len);
        return true;
    }

    private static bool IsCloseCodeFence(string trimmed, string openingToken)
    {
        if (trimmed.Length < openingToken.Length || trimmed[0] != openingToken[0])
            return false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != openingToken[0])
                return string.IsNullOrWhiteSpace(trimmed[i..]) && i >= openingToken.Length;
        }
        return true;
    }

    private static string UnquoteYamlScalar(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\'')))
            return t[1..^1].Trim();
        return t;
    }

    private static bool TryParseStandaloneImage(string trimmedLine, out string alt, out string path, out string? caption)
    {
        alt = string.Empty;
        path = string.Empty;
        caption = null;

        if (!trimmedLine.StartsWith("![", StringComparison.Ordinal) || !trimmedLine.EndsWith(')'))
            return false;

        int closeBracket = trimmedLine.IndexOf("](", 2, StringComparison.Ordinal);
        if (closeBracket < 0)
            return false;

        alt = trimmedLine[2..closeBracket].Trim();
        var inner = trimmedLine[(closeBracket + 2)..^1].Trim();
        if (inner.Length == 0)
            return false;

        string? parsedCaption = null;
        string rawDestination = inner;

        if (inner.StartsWith('<'))
        {
            int closeAngle = inner.IndexOf('>');
            if (closeAngle <= 1) return false;
            rawDestination = inner[1..closeAngle].Trim();
            var rest = inner[(closeAngle + 1)..].Trim();
            if (rest.Length > 0)
            {
                if (!TryParseQuotedCaption(rest, out parsedCaption))
                    return false;
            }
        }
        else if (inner.EndsWith('"') || inner.EndsWith('\''))
        {
            char quote = inner[^1];
            int openQuote = inner.LastIndexOf(quote, inner.Length - 2);
            if (openQuote > 0 && char.IsWhiteSpace(inner[openQuote - 1]))
            {
                rawDestination = inner[..openQuote].Trim();
                parsedCaption = inner[(openQuote + 1)..^1].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(rawDestination))
            return false;

        path = rawDestination;
        caption = parsedCaption ?? (string.IsNullOrWhiteSpace(alt) ? null : alt);
        return true;
    }

    private static bool TryParseQuotedCaption(string raw, out string? caption)
    {
        caption = null;
        if (raw.Length < 2) return false;
        char q = raw[0];
        if ((q == '"' || q == '\'') && raw[^1] == q)
        {
            caption = raw[1..^1].Trim();
            return true;
        }
        return false;
    }

    private static List<string> SplitRespectingQuotes(string input, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char activeQuote = '\0';

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (activeQuote != '\0')
            {
                current.Append(c);
                if (c == activeQuote && (i == 0 || input[i - 1] != '\\'))
                    activeQuote = '\0';
            }
            else if (c == '"' || c == '\'')
            {
                activeQuote = c;
                current.Append(c);
            }
            else if (c == separator)
            {
                var item = current.ToString().Trim();
                if (item.Length > 0) parts.Add(item);
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        var tail = current.ToString().Trim();
        if (tail.Length > 0) parts.Add(tail);
        return parts;
    }

    private static string? ExtractFirstH1(IReadOnlyList<DeclarativeArticleBlockSpec> blocks)
    {
        foreach (var b in blocks)
        {
            if (b.Kind != "text" || string.IsNullOrWhiteSpace(b.Text)) continue;
            string? activeFence = null;
            foreach (var line in b.Text.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (activeFence is null && TryOpenCodeFence(trimmed, out var token))
                {
                    activeFence = token;
                    continue;
                }
                if (activeFence is not null)
                {
                    if (IsCloseCodeFence(trimmed, activeFence))
                        activeFence = null;
                    continue;
                }
                var m = H1HeadingRegex().Match(trimmed);
                if (m.Success) return m.Groups["heading"].Value.Trim();
            }
        }
        return null;
    }

    internal static string? HumanizeFileStem(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var stem = Path.GetFileNameWithoutExtension(filePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(stem)) return null;
        var parts = stem.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && int.TryParse(parts[0], out _))
            parts = parts[1..];
        var joined = string.Join(' ', parts);
        return string.IsNullOrWhiteSpace(joined)
            ? stem
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(joined.ToLowerInvariant());
    }
}
