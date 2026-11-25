using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meloberry.Domain.Common;

namespace Meloberry.Domain.Payloads;

public sealed record TableBlockPayload(
    string? Title,
    string? Caption,
    bool StickyFirstCol,
    IReadOnlyList<int> HideableCols,
    IReadOnlyList<string> Headers,
    IReadOnlyList<string> Alignments,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? Footnote);

public sealed record FillBlankSlot(
    int Index,
    IReadOnlyList<string> Answers);

public sealed record FillBlankPayload(
    string Template,
    IReadOnlyList<FillBlankSlot> Blanks,
    bool Fuzzy,
    bool CaseSensitive,
    string? Explanation,
    string? Placeholder = null);

public sealed record AssertionReasonPayload(
    string Assertion,
    string Reason,
    string Answer,
    string Scheme,
    string? Explanation);

public sealed record SpotErrorSegment(
    int? SpanIndex,
    string Text,
    bool IsCandidate,
    bool IsError,
    IReadOnlyList<string>? Corrections);

public sealed record SpotErrorPayload(
    string Template,
    IReadOnlyList<SpotErrorSegment> Segments,
    bool RequireFix,
    bool AllowNoError,
    bool NoError,
    string? Explanation,
    string? ImagePath = null);

public sealed record ArticleLinkCard(
    string Id,
    string? Title,
    string? Text,
    string? ImagePath = null);

public sealed record ParsedArticleLinkTarget(
    string Kind,
    string TargetId,
    string? SubTargetId = null,
    string? ExplicitHref = null);

public static class Phase1PayloadCodec
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize<T>(T payload) =>
        JsonSerializer.Serialize(payload, SerializeOptions);

    public static bool TryDeserialize<T>(string? payloadJson, out T? payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(payloadJson))
            return false;
        try
        {
            payload = JsonSerializer.Deserialize<T>(payloadJson, DeserializeOptions);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public readonly record struct FillBlankSpan(int OpenIndex, int CloseIndex);

public static partial class Phase1PayloadRules
{
    public static readonly IReadOnlyList<string> AssertionReasonAnswers =
        ["both-explain", "both-no-explain", "a-true-r-false", "a-false-r-true", "both-false"];

    public static readonly IReadOnlyList<string> AssertionReasonSchemes =
        ["4opt", "5opt"];

    [GeneratedRegex(@"^:?-+:?$", RegexOptions.Compiled)]
    private static partial Regex TableSeparatorCellRegex();

    [GeneratedRegex(@"^~~(?<wrong>.*?)~~\s*->\s*(?<fix>.*)$", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex SpotErrorFaultRegex();

    public static bool IsValidAssertionReasonAnswer(string? answer) =>
        !string.IsNullOrWhiteSpace(answer) &&
        AssertionReasonAnswers.Contains(answer.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsValidAssertionReasonScheme(string? scheme) =>
        !string.IsNullOrWhiteSpace(scheme) &&
        AssertionReasonSchemes.Contains(scheme.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string NormalizeAssertionReasonAnswer(string? answer) =>
        string.IsNullOrWhiteSpace(answer) ? string.Empty : answer.Trim().ToLowerInvariant();

    public static string ResolveAssertionReasonScheme(string? rawScheme, string? answer)
    {
        if (!string.IsNullOrWhiteSpace(rawScheme))
            return rawScheme.Trim().ToLowerInvariant();
        return string.Equals(answer?.Trim(), "both-false", StringComparison.OrdinalIgnoreCase)
            ? "5opt"
            : "4opt";
    }

    public static bool IsEscaped(string text, int index)
    {
        int backslashes = 0;
        for (int k = index - 1; k >= 0 && text[k] == '\\'; k--)
            backslashes++;
        return (backslashes & 1) == 1;
    }

    public static bool TrySkipInlineCodeSpan(string text, int index, out int nextIndex)
    {
        nextIndex = index;
        if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length || text[index] != '`' || IsEscaped(text, index))
            return false;

        int runLen = 0;
        while (index + runLen < text.Length && text[index + runLen] == '`')
            runLen++;

        int j = index + runLen;
        while (j < text.Length)
        {
            if (text[j] == '`')
            {
                int closeLen = 0;
                while (j + closeLen < text.Length && text[j + closeLen] == '`')
                    closeLen++;
                if (closeLen == runLen)
                {
                    nextIndex = j + closeLen;
                    return true;
                }
                j += closeLen;
                continue;
            }
            j++;
        }
        return false;
    }

    public static bool TrySkipMathSpan(string text, int index, out int nextIndex)
    {
        nextIndex = index;
        if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length || text[index] != '$' || IsEscaped(text, index))
            return false;

        // Display math: $$...$$
        if (index + 1 < text.Length && text[index + 1] == '$')
        {
            for (int j = index + 2; j + 1 < text.Length; j++)
            {
                if (text[j] == '$' && text[j + 1] == '$' && !IsEscaped(text, j))
                {
                    nextIndex = j + 2;
                    return true;
                }
            }
            return false;
        }

        // Inline math: $...$
        if (index + 1 >= text.Length || char.IsWhiteSpace(text[index + 1]))
            return false;

        for (int j = index + 1; j < text.Length; j++)
        {
            if (text[j] == '\n' && j + 1 < text.Length && text[j + 1] == '\n')
                return false;
            if (text[j] == '$' && !IsEscaped(text, j))
            {
                if (j > index + 1 && !char.IsWhiteSpace(text[j - 1]))
                {
                    nextIndex = j + 1;
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    public static Result<IReadOnlyList<FillBlankSpan>> FindFillBlankSpans(string template)
    {
        var spans = new List<FillBlankSpan>();
        int i = 0;
        while (i < template.Length)
        {
            if (TrySkipInlineCodeSpan(template, i, out int afterCode))
            {
                i = afterCode;
                continue;
            }
            if (TrySkipMathSpan(template, i, out int afterMath))
            {
                i = afterMath;
                continue;
            }

            if (i + 1 < template.Length && template[i] == '}' && template[i + 1] == '}')
            {
                return new Error("FillBlank.UnbalancedDelimiters", "Found unmatched '}}' delimiter in fill-in-the-blank template.", ErrorType.Validation);
            }

            if (i + 1 < template.Length && template[i] == '{' && template[i + 1] == '{')
            {
                int openIdx = i;
                int j = i + 2;
                int closeIdx = -1;
                while (j < template.Length)
                {
                    if (TrySkipInlineCodeSpan(template, j, out int afterInnerCode))
                    {
                        j = afterInnerCode;
                        continue;
                    }
                    if (TrySkipMathSpan(template, j, out int afterInnerMath))
                    {
                        j = afterInnerMath;
                        continue;
                    }
                    if (j + 1 < template.Length && template[j] == '{' && template[j + 1] == '{')
                        return new Error("FillBlank.UnbalancedDelimiters", "Nested '{{' delimiter inside blank in fill-in-the-blank template.", ErrorType.Validation);
                    if (j + 1 < template.Length && template[j] == '}' && template[j + 1] == '}')
                    {
                        closeIdx = j;
                        break;
                    }
                    j++;
                }

                if (closeIdx < 0)
                    return new Error("FillBlank.UnbalancedDelimiters", "Unclosed '{{' delimiter in fill-in-the-blank template.", ErrorType.Validation);

                spans.Add(new FillBlankSpan(openIdx, closeIdx));
                i = closeIdx + 2;
                continue;
            }

            i++;
        }

        return spans;
    }

    public static List<string> SplitPipeAliases(string raw)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int i = 0;
        while (i < raw.Length)
        {
            if (TrySkipInlineCodeSpan(raw, i, out int afterCode))
            {
                current.Append(raw, i, afterCode - i);
                i = afterCode;
                continue;
            }
            if (TrySkipMathSpan(raw, i, out int afterMath))
            {
                current.Append(raw, i, afterMath - i);
                i = afterMath;
                continue;
            }

            if (raw[i] == '|')
            {
                if (IsEscaped(raw, i))
                {
                    if (current.Length > 0 && current[^1] == '\\')
                        current.Length--;
                    current.Append('|');
                }
                else
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                i++;
                continue;
            }

            current.Append(raw[i]);
            i++;
        }
        parts.Add(current.ToString());
        return parts;
    }

    public static Result<IReadOnlyList<FillBlankSlot>> ParseFillBlankTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return new Error("FillBlank.EmptyTemplate", "Fill-in-the-blank prompt text cannot be empty.", ErrorType.Validation);

        var spansRes = FindFillBlankSpans(template);
        if (!spansRes.IsSuccess)
            return spansRes.Error!;

        var slots = new List<FillBlankSlot>(spansRes.Value.Count);
        foreach (var span in spansRes.Value)
        {
            var inner = template[(span.OpenIndex + 2)..span.CloseIndex];
            var parts = SplitPipeAliases(inner);
            var answers = new List<string>(parts.Count);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    return new Error("FillBlank.EmptyAnswer", $"Blank #{slots.Count + 1} contains an empty answer or alias.", ErrorType.Validation);
                answers.Add(trimmed);
            }

            if (answers.Count == 0)
                return new Error("FillBlank.EmptyAnswer", $"Blank #{slots.Count + 1} has no accepted answers.", ErrorType.Validation);

            slots.Add(new FillBlankSlot(slots.Count + 1, answers));
        }

        if (slots.Count == 0)
            return new Error("FillBlank.NoBlanks", "Fill-in-the-blank template must contain at least one '{{answer}}' blank.", ErrorType.Validation);

        return slots;
    }

    public static Result<IReadOnlyList<SpotErrorSegment>> ParseSpotErrorTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return new Error("SpotError.EmptyTemplate", "Spot-the-error statement text cannot be empty.", ErrorType.Validation);

        var segments = new List<SpotErrorSegment>();
        var plain = new StringBuilder();
        int spanIdx = 0;

        for (int i = 0; i < template.Length; i++)
        {
            if (TrySkipInlineCodeSpan(template, i, out int afterCode))
            {
                plain.Append(template, i, afterCode - i);
                i = afterCode - 1;
                continue;
            }
            if (TrySkipMathSpan(template, i, out int afterMath))
            {
                plain.Append(template, i, afterMath - i);
                i = afterMath - 1;
                continue;
            }

            char c = template[i];
            if (c == '\\' && i + 1 < template.Length && template[i + 1] == '\\')
            {
                plain.Append("\\\\");
                i++;
                continue;
            }
            if (c == '\\' && i + 1 < template.Length && (template[i + 1] == '[' || template[i + 1] == ']'))
            {
                plain.Append(template[++i]);
                continue;
            }

            if (c == ']')
                return new Error("SpotError.UnbalancedBrackets", "Found unmatched ']' bracket in spot-the-error statement.", ErrorType.Validation);

            if (c == '[')
            {
                if (plain.Length > 0)
                {
                    segments.Add(new SpotErrorSegment(null, plain.ToString(), IsCandidate: false, IsError: false, Corrections: null));
                    plain.Clear();
                }

                int closeIdx = -1;
                var innerSb = new StringBuilder();
                for (int j = i + 1; j < template.Length; j++)
                {
                    if (TrySkipInlineCodeSpan(template, j, out int afterInnerCode))
                    {
                        innerSb.Append(template, j, afterInnerCode - j);
                        j = afterInnerCode - 1;
                        continue;
                    }
                    if (TrySkipMathSpan(template, j, out int afterInnerMath))
                    {
                        innerSb.Append(template, j, afterInnerMath - j);
                        j = afterInnerMath - 1;
                        continue;
                    }
                    if (template[j] == '\\' && j + 1 < template.Length && template[j + 1] == '\\')
                    {
                        innerSb.Append("\\\\");
                        j++;
                        continue;
                    }
                    if (template[j] == '\\' && j + 1 < template.Length && (template[j + 1] == '[' || template[j + 1] == ']'))
                    {
                        innerSb.Append(template[++j]);
                        continue;
                    }
                    if (template[j] == '[')
                        return new Error("SpotError.UnbalancedBrackets", "Nested '[' bracket inside candidate span in spot-the-error statement.", ErrorType.Validation);
                    if (template[j] == ']')
                    {
                        closeIdx = j;
                        break;
                    }
                    innerSb.Append(template[j]);
                }

                if (closeIdx < 0)
                    return new Error("SpotError.UnbalancedBrackets", "Unclosed '[' candidate span in spot-the-error statement.", ErrorType.Validation);

                var inner = innerSb.ToString().Trim();
                if (string.IsNullOrEmpty(inner))
                    return new Error("SpotError.EmptySpan", "Spot-the-error statement contains an empty '[]' candidate span.", ErrorType.Validation);

                spanIdx++;
                if (inner.Contains("~~", StringComparison.Ordinal))
                {
                    var m = SpotErrorFaultRegex().Match(inner);
                    if (!m.Success)
                        return new Error("SpotError.EmptyCorrection", $"Malformed error span '[{inner}]'. Use '[~~wrong~~ -> correction | alias]'.", ErrorType.Validation);

                    var wrongText = m.Groups["wrong"].Value.Trim();
                    var fixRaw = m.Groups["fix"].Value.Trim();
                    if (string.IsNullOrEmpty(wrongText) || string.IsNullOrEmpty(fixRaw))
                        return new Error("SpotError.EmptyCorrection", $"Error span '[{inner}]' must specify non-empty wrong text and correction.", ErrorType.Validation);

                    var fixParts = SplitPipeAliases(fixRaw);
                    var corrections = new List<string>(fixParts.Count);
                    foreach (var fp in fixParts)
                    {
                        var trimmedFix = fp.Trim();
                        if (string.IsNullOrEmpty(trimmedFix))
                            return new Error("SpotError.EmptyCorrection", $"Error span '[{inner}]' contains an empty correction alias.", ErrorType.Validation);
                        corrections.Add(trimmedFix);
                    }

                    segments.Add(new SpotErrorSegment(spanIdx, wrongText, IsCandidate: true, IsError: true, Corrections: corrections));
                }
                else
                {
                    segments.Add(new SpotErrorSegment(spanIdx, inner, IsCandidate: true, IsError: false, Corrections: null));
                }

                i = closeIdx;
                continue;
            }

            plain.Append(c);
        }

        if (plain.Length > 0)
            segments.Add(new SpotErrorSegment(null, plain.ToString(), IsCandidate: false, IsError: false, Corrections: null));

        return segments;
    }

    public static Result<(IReadOnlyList<string> Headers, IReadOnlyList<string> Alignments, IReadOnlyList<IReadOnlyList<string>> Rows)> ParseMarkdownTable(string? rawMarkdownTable)
    {
        if (string.IsNullOrWhiteSpace(rawMarkdownTable))
            return new Error("Table.InvalidFormat", "Table body cannot be empty.", ErrorType.Validation);

        var lines = rawMarkdownTable
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        if (lines.Count < 3)
            return new Error("Table.InvalidFormat", "Markdown table requires a header row, an alignment separator row, and at least one data row.", ErrorType.Validation);

        if (!lines[0].Contains('|') || !lines[1].Contains('|'))
            return new Error("Table.InvalidFormat", "Markdown table rows must use '|' pipe delimiters.", ErrorType.Validation);

        var headers = SplitPipeRow(lines[0]);
        if (headers.Count == 0 || headers.All(string.IsNullOrWhiteSpace))
            return new Error("Table.InvalidFormat", "Markdown table header row must contain at least one column.", ErrorType.Validation);

        var sepCells = SplitPipeRow(lines[1]);
        if (sepCells.Count != headers.Count)
            return new Error("Table.InvalidFormat", $"Markdown table separator row has {sepCells.Count} column(s), expected {headers.Count}.", ErrorType.Validation);

        var alignments = new List<string>(headers.Count);
        foreach (var cell in sepCells)
        {
            var t = cell.Trim();
            if (!TableSeparatorCellRegex().IsMatch(t))
                return new Error("Table.InvalidFormat", $"Invalid table alignment separator cell '{t}'. Use '---', ':---', ':---:', or '---:'.", ErrorType.Validation);

            bool leftColon = t.StartsWith(':');
            bool rightColon = t.EndsWith(':');
            alignments.Add(leftColon && rightColon ? "center" : (rightColon ? "right" : "left"));
        }

        var rows = new List<IReadOnlyList<string>>(lines.Count - 2);
        for (int i = 2; i < lines.Count; i++)
        {
            if (!lines[i].Contains('|'))
                return new Error("Table.InvalidFormat", $"Table data row {i - 1} must use '|' pipe delimiters.", ErrorType.Validation);

            var cells = SplitPipeRow(lines[i]);
            if (cells.Count != headers.Count)
                return new Error("Table.InvalidFormat", $"Table data row {i - 1} has {cells.Count} column(s), expected {headers.Count}.", ErrorType.Validation);

            rows.Add(cells);
        }

        return (headers, alignments, rows);
    }

    public static List<string> SplitPipeRow(string line)
    {
        var span = line.Trim();
        if (span.StartsWith('|')) span = span[1..];
        if (span.EndsWith('|') && !IsEscaped(span, span.Length - 1)) span = span[..^1];

        var cells = new List<string>();
        var current = new StringBuilder();
        int blankDepth = 0;
        int codeTickLen = 0;

        int i = 0;
        while (i < span.Length)
        {
            if (codeTickLen > 0)
            {
                if (span[i] == '`')
                {
                    int runLen = 0;
                    while (i + runLen < span.Length && span[i + runLen] == '`') runLen++;
                    current.Append(span, i, runLen);
                    i += runLen;
                    if (runLen == codeTickLen)
                        codeTickLen = 0;
                    continue;
                }
                current.Append(span[i]);
                i++;
                continue;
            }

            if (span[i] == '`' && !IsEscaped(span, i))
            {
                int runLen = 0;
                while (i + runLen < span.Length && span[i + runLen] == '`') runLen++;
                if (HasMatchingCodeFenceClose(span, i + runLen, runLen))
                    codeTickLen = runLen;
                current.Append(span, i, runLen);
                i += runLen;
                continue;
            }

            if (TrySkipMathSpan(span, i, out int afterMath))
            {
                current.Append(span, i, afterMath - i);
                i = afterMath;
                continue;
            }

            if (i + 1 < span.Length && span[i] == '{' && span[i + 1] == '{')
            {
                blankDepth++;
                current.Append("{{");
                i += 2;
                continue;
            }

            if (blankDepth > 0 && i + 1 < span.Length && span[i] == '}' && span[i + 1] == '}')
            {
                blankDepth--;
                current.Append("}}");
                i += 2;
                continue;
            }

            if (span[i] == '|')
            {
                if (blankDepth > 0)
                {
                    current.Append('|');
                    i++;
                    continue;
                }

                if (IsEscaped(span, i))
                {
                    if (current.Length > 0 && current[^1] == '\\')
                        current.Length--;
                    current.Append('|');
                    i++;
                    continue;
                }

                cells.Add(current.ToString().Trim());
                current.Clear();
                i++;
                continue;
            }

            current.Append(span[i]);
            i++;
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static bool HasMatchingCodeFenceClose(string span, int startIndex, int tickLen)
    {
        int j = startIndex;
        while (j < span.Length)
        {
            if (span[j] == '`')
            {
                int runLen = 0;
                while (j + runLen < span.Length && span[j + runLen] == '`') runLen++;
                if (runLen == tickLen)
                    return true;
                j += runLen;
                continue;
            }
            j++;
        }
        return false;
    }
}

public static partial class ArticleLinkRules
{
    public static Result<string> ValidateAndNormalizeCardId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new Error("ArticleLink.InvalidId", "Link card id cannot be empty.", ErrorType.Validation);

        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized.Length > 64)
            return new Error("ArticleLink.InvalidId", $"Link card id '{raw}' exceeds 64 characters.", ErrorType.Validation);

        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            bool valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || (i > 0 && c is '-' or '_');
            if (!valid)
                return new Error("ArticleLink.InvalidId", $"Link card id '{raw}' must match ^[a-z0-9][a-z0-9_-]{{0,63}}$.", ErrorType.Validation);
        }

        return normalized;
    }

    public static bool TryParseMbyUri(string? rawUrl, out ParsedArticleLinkTarget target)
    {
        target = default!;
        if (string.IsNullOrWhiteSpace(rawUrl))
            return false;

        var trimmed = rawUrl.Trim();
        if (!trimmed.StartsWith("mby://", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = trimmed["mby://".Length..];
        string? explicitHref = null;
        int qIdx = rest.IndexOf('?');
        if (qIdx >= 0)
        {
            var query = rest[(qIdx + 1)..];
            rest = rest[..qIdx];
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var k = Uri.UnescapeDataString(part[..eq]).Trim();
                if (!string.Equals(k, "href", StringComparison.OrdinalIgnoreCase)) continue;
                var v = Uri.UnescapeDataString(part[(eq + 1)..]).Trim();
                if (IsUnsafeHref(v))
                    return false;
                if (!string.IsNullOrWhiteSpace(v))
                    explicitHref = v;
            }
        }

        int hashIdx = rest.IndexOf('#');
        if (hashIdx >= 0)
            rest = rest[..hashIdx];

        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        var kind = segments[0].Trim().ToLowerInvariant();
        var targetId = Uri.UnescapeDataString(segments[1]).Trim();
        if (string.IsNullOrWhiteSpace(targetId))
            return false;

        if (kind is "card" or "article")
        {
            if (segments.Length != 2)
                return false;
            target = new ParsedArticleLinkTarget(kind, kind == "card" ? targetId.ToLowerInvariant() : targetId, null, explicitHref);
            return true;
        }

        if (kind == "session")
        {
            if (segments.Length > 3)
                return false;
            string? sub = segments.Length == 3 ? Uri.UnescapeDataString(segments[2]).Trim() : null;
            if (segments.Length == 3 && string.IsNullOrWhiteSpace(sub))
                return false;
            target = new ParsedArticleLinkTarget("session", targetId, sub, explicitHref);
            return true;
        }

        return false;
    }

    public static IReadOnlyList<ParsedArticleLinkTarget> ExtractMbyLinksFromMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var results = new List<ParsedArticleLinkTarget>();
        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        string? activeFence = null;
        var proseSb = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (MarkdownFenceHelper.TryUpdateFenceState(trimmed, ref activeFence))
                continue;
            if (activeFence is null)
                proseSb.AppendLine(line);
        }

        var text = proseSb.ToString();
        int i = 0;
        while (i < text.Length)
        {
            if (Phase1PayloadRules.TrySkipInlineCodeSpan(text, i, out int afterCode))
            {
                i = afterCode;
                continue;
            }
            if (Phase1PayloadRules.TrySkipMathSpan(text, i, out int afterMath))
            {
                i = afterMath;
                continue;
            }

            if (text[i] == '[' && (i == 0 || (text[i - 1] != '!' && !Phase1PayloadRules.IsEscaped(text, i))))
            {
                int closeBracket = FindClosingBracket(text, i + 1);
                if (closeBracket > i && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                {
                    int closeParen = FindClosingParen(text, closeBracket + 2);
                    if (closeParen > closeBracket + 2)
                    {
                        var dest = text[(closeBracket + 2)..closeParen].Trim();
                        if (dest.StartsWith('<'))
                        {
                            int angEnd = dest.IndexOf('>');
                            if (angEnd > 1) dest = dest[1..angEnd].Trim();
                        }
                        else
                        {
                            int ws = dest.IndexOfAny([' ', '\t', '\r', '\n']);
                            if (ws > 0) dest = dest[..ws].Trim();
                        }

                        if (TryParseMbyUri(dest, out var parsed))
                            results.Add(parsed);

                        i = closeParen + 1;
                        continue;
                    }
                }
            }
            i++;
        }

        return results;
    }

    public static string ResolveSessionHref(ParsedArticleLinkTarget target)
    {
        var baseHref = !string.IsNullOrWhiteSpace(target.ExplicitHref)
            ? target.ExplicitHref.Trim()
            : $"../{Uri.EscapeDataString(target.TargetId)}_web/index.html";

        if (!string.IsNullOrWhiteSpace(target.SubTargetId) && !baseHref.Contains('#'))
            return $"{baseHref}#article={Uri.EscapeDataString(target.SubTargetId)}";

        return baseHref;
    }

    public static string ResolveSessionPreviewDir(string resolvedHref)
    {
        if (string.IsNullOrWhiteSpace(resolvedHref))
            return ".";

        var clean = resolvedHref.Trim();
        int hash = clean.IndexOf('#');
        if (hash >= 0) clean = clean[..hash];
        int query = clean.IndexOf('?');
        if (query >= 0) clean = clean[..query];

        if (clean.EndsWith('/'))
            return clean.TrimEnd('/') is { Length: > 0 } dir ? dir : ".";

        int lastSlash = clean.LastIndexOf('/');
        if (lastSlash < 0)
            return ".";

        var lastSeg = clean[(lastSlash + 1)..];
        if (lastSeg.Contains('.'))
        {
            var parent = clean[..lastSlash].TrimEnd('/');
            return string.IsNullOrEmpty(parent) ? "." : parent;
        }

        return clean.TrimEnd('/');
    }

    private static bool IsUnsafeHref(string href)
    {
        var compact = new string(href.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
        return compact.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
               compact.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase) ||
               compact.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindClosingBracket(string text, int start)
    {
        int depth = 1;
        for (int j = start; j < text.Length; j++)
        {
            if (Phase1PayloadRules.TrySkipInlineCodeSpan(text, j, out int afterCode))
            {
                j = afterCode - 1;
                continue;
            }
            if (text[j] == '\\') { j++; continue; }
            if (text[j] == '[') depth++;
            else if (text[j] == ']')
            {
                depth--;
                if (depth == 0) return j;
            }
        }
        return -1;
    }

    private static int FindClosingParen(string text, int start)
    {
        int depth = 1;
        for (int j = start; j < text.Length; j++)
        {
            if (text[j] == '\\') { j++; continue; }
            if (text[j] == '(') depth++;
            else if (text[j] == ')')
            {
                depth--;
                if (depth == 0) return j;
            }
            else if (text[j] == '\n')
            {
                return -1;
            }
        }
        return -1;
    }
}
