using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Sessions;

namespace Meloberry.Infrastructure.Services;

internal sealed record ParsedMarkdownDocument(
    string? Kind,
    string? Name,
    string? Description,
    string? Title,
    string? Ref,
    string? QuizType,
    Dictionary<string, string>? LayoutOverrides,
    IReadOnlyList<string>? Order,
    IReadOnlyList<DeclarativeArticleBlockSpec> Blocks,
    IReadOnlyList<JsonQuestionSpec> Questions,
    string? ExtractedHeading = null,
    IReadOnlyList<DeclarativeLinkCardSpec>? Links = null);

internal static partial class MarkdownSpecParser
{
    private sealed record ParsedDirectiveBody(
        string MainBody,
        Dictionary<string, string> SubSections);

    private static readonly HashSet<string> AllowedFrontmatterKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "kind", "title", "ref", "name", "description", "quizType", "layoutOverrides", "order"
    };

    private static readonly HashSet<string> QuizKindNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "quiz", "mcq", "flashcard", "mixed"
    };

    private static readonly HashSet<string> AllowedCalloutAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "tone", "title", "ref"
    };

    private static readonly HashSet<string> AllowedCalloutShorthandAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "ref"
    };

    private static readonly HashSet<string> AllowedMcqAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "multi", "isMultiCorrect", "maxOptions", "image", "imagePath", "explanation"
    };

    private static readonly HashSet<string> AllowedFlashcardAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "frontPosition", "backPosition"
    };

    private static readonly HashSet<string> AllowedTableAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "title", "caption", "stickyFirstCol", "hideableCols"
    };

    private static readonly HashSet<string> AllowedFillBlankAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "fuzzy", "caseSensitive", "placeholder", "explanation"
    };

    private static readonly HashSet<string> AllowedAssertionReasonAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "answer", "scheme", "explanation"
    };

    private static readonly HashSet<string> AllowedSpotErrorAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "requireFix", "allowNoError", "noError", "image", "imagePath", "explanation"
    };

    private static readonly HashSet<string> AllowedLinkAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "title", "image", "imagePath"
    };

    private static readonly HashSet<string> AllowedDiffAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "mode", "title", "bank", "fuzzy", "caseSensitive", "allowNoError", "placeholder", "explanation"
    };

    private static readonly HashSet<string> AllowedMechanismAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "title", "explanation"
    };

    private static readonly HashSet<string> AllowedRelabelAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "image", "imagePath", "masks", "masksPath", "mode", "style", "fuzzy", "caseSensitive", "explanation"
    };

    private static readonly HashSet<string> AllowedDiagramExploreAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "image", "imagePath", "masks", "masksPath", "title", "caption", "alt"
    };

    private static readonly HashSet<string> AllowedRevealAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "title", "showFirst"
    };

    private static readonly HashSet<string> AllowedClassifyAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "title", "explanation"
    };

    private static readonly HashSet<string> AllowedMatchAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "title", "explanation"
    };

    private static readonly HashSet<string> AllowedNumericAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "answer", "tolerance", "toleranceMode", "unit", "placeholder", "image", "imagePath", "explanation"
    };

    private static readonly HashSet<string> AllowedCaseAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "title", "image", "imagePath", "caption", "explanation"
    };

    private static readonly HashSet<string> EmptySubSections = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllowedTableSubSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "footnote"
    };

    private static readonly HashSet<string> AllowedMcqSubSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "explanation", "layout"
    };

    private static readonly HashSet<string> AllowedFlashcardSubSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "front", "back", "layout"
    };

    private static readonly HashSet<string> AllowedExplanationLayoutSubSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "explanation", "layout"
    };

    [GeneratedRegex(@"^:::\s*(?<name>[a-zA-Z_][a-zA-Z0-9_-]*)(?<rest>.*)$", RegexOptions.Compiled)]
    private static partial Regex DirectiveOpenFenceRegex();

    [GeneratedRegex(@"^:::\s*(?<section>[a-zA-Z_][a-zA-Z0-9_-]*)\s*$", RegexOptions.Compiled)]
    private static partial Regex DirectiveSubSectionRegex();

    [GeneratedRegex(@"^::::\s*(?<name>[a-zA-Z_][a-zA-Z0-9_-]*)(?<rest>.*)$", RegexOptions.Compiled)]
    private static partial Regex QuadDirectiveOpenRegex();

    [GeneratedRegex(@"^::::\s*(?<section>[a-zA-Z_][a-zA-Z0-9_-]*)\s*$", RegexOptions.Compiled)]
    private static partial Regex QuadSubSectionRegex();

    [GeneratedRegex(@"^-\s*\[(?<mark>[ xX])\]\s*(?<text>.*)$", RegexOptions.Compiled)]
    private static partial Regex McqOptionLineRegex();

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?:<(?<ang>[^>]+)>|(?<path>[^)\s""']+))(?:\s+(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'))?\)", RegexOptions.Compiled)]
    private static partial Regex InlineImageRegex();

    [GeneratedRegex(@"(?<k>[a-zA-Z_][a-zA-Z0-9_-]*)\s*=\s*(?:""(?<dq>(?:\\.|[^""\\])*)""|'(?<sq>(?:\\.|[^'\\])*)'|(?<bare>[^\s}""']+))", RegexOptions.Compiled)]
    private static partial Regex DirectiveAttrPairRegex();

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

    public static string SmartWrapSingleFragment(
        string topic,
        string? subtype,
        string rawBody,
        string? imagePath = null,
        string? caption = null,
        string? alt = null) =>
        Domain.Scaffolding.MarkdownSmartWrapper.SmartWrapSingleFragment(
            topic, subtype, rawBody, imagePath, caption, alt, out _);

    public static Result<DeclarativeApplySpec> ParseSingleFragment(
        string topic,
        string? subtype,
        string rawBody,
        string? imagePath = null,
        string? caption = null,
        string? alt = null)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return new Error("Markdown.InvalidFragment", "Fragment topic cannot be empty.", ErrorType.Validation);

        var t = topic.Trim().ToLowerInvariant();
        var wrapped = SmartWrapSingleFragment(t, subtype, rawBody, imagePath, caption, alt);
        bool isArticleBlock = t is "text" or "image" or "callout" or "table" or "diagram-explore" or "reveal" or "link";
        var docText = isArticleBlock
            ? $"---\nkind: article\ntitle: \"Fragment\"\n---\n\n{wrapped}"
            : $"---\nkind: quiz\nquizType: mixed\ntitle: \"Fragment\"\n---\n\n{wrapped}";
        return ParseSingleSpec(docText, $"<fragment:{t}>");
    }

    public static Result<DeclarativeApplySpec> ParseSingleSpec(string rawMarkdown, string sourceLabel, string? filePath = null)
    {
        var parsedRes = ParseDocument(rawMarkdown, sourceLabel, isSessionManifest: false);
        if (!parsedRes.IsSuccess) return parsedRes.Error!;
        var doc = parsedRes.Value;

        var title = !string.IsNullOrWhiteSpace(doc.Title)
            ? doc.Title
            : doc.ExtractedHeading ?? ExtractFirstH1(doc.Blocks) ?? HumanizeFileStem(filePath);

        var assetBaseDir = filePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(filePath));

        if (string.Equals(doc.Kind, "session", StringComparison.OrdinalIgnoreCase))
        {
            var comps = new List<DeclarativeComponentSpec>();
            bool hasArticleBody = doc.Blocks.Count > 0;
            bool hasQuizBody = doc.Questions.Count > 0;
            if (hasArticleBody && hasQuizBody)
                return new Error("Markdown.MixedContentNotAllowed", $"Markdown file '{sourceLabel}' mixes article blocks and quiz directives. Split them into separate component files.", ErrorType.Validation);

            if (hasArticleBody)
            {
                comps.Add(new DeclarativeComponentSpec("article", title, null, doc.Blocks, null, doc.LayoutOverrides, doc.Ref, assetBaseDir, doc.Links));
            }
            else if (hasQuizBody)
            {
                var qType = ResolveQuizType(doc.Kind, doc.QuizType);
                comps.Add(new DeclarativeComponentSpec("quiz", title, qType, null, doc.Questions, doc.LayoutOverrides, doc.Ref, assetBaseDir));
            }

            bool hasBody = hasArticleBody || hasQuizBody;
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
                assetBaseDir,
                Ref: hasBody ? null : doc.Ref,
                Order: doc.Order);
        }

        bool isExplicitQuiz = !string.IsNullOrWhiteSpace(doc.Kind) && QuizKindNames.Contains(doc.Kind);
        bool isExplicitArticle = string.Equals(doc.Kind, "article", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(doc.Kind) && !isExplicitArticle && !isExplicitQuiz)
        {
            return new Error("Markdown.InvalidFrontmatter", $"Unsupported Markdown frontmatter kind '{doc.Kind}' in '{sourceLabel}'. Use 'article', 'quiz' ('mcq'|'flashcard'|'mixed'), or 'session'.", ErrorType.Validation);
        }

        bool isQuizDoc = isExplicitQuiz || (string.IsNullOrWhiteSpace(doc.Kind) && doc.Questions.Count > 0 && doc.Blocks.Count == 0);
        if (isQuizDoc)
        {
            if (doc.Questions.Count == 0)
                return new Error("Markdown.EmptyBody", $"Markdown quiz '{sourceLabel}' has no question directives (use ':::mcq', ':::flashcard', ':::fill-blank', ':::assertion-reason', ':::spot-error', ':::diff', ':::mechanism', or ':::relabel').", ErrorType.Validation);

            var effectiveQuizType = ResolveQuizType(doc.Kind, doc.QuizType);
            return new DeclarativeApplySpec(
                Kind: "quiz",
                Name: doc.Name ?? title,
                Description: doc.Description,
                Title: title,
                QuizType: effectiveQuizType,
                Components: null,
                Blocks: null,
                Questions: doc.Questions,
                LayoutOverrides: doc.LayoutOverrides,
                AssetBaseDirectory: assetBaseDir,
                Ref: doc.Ref,
                Order: doc.Order);
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
            AssetBaseDirectory: assetBaseDir,
            Ref: doc.Ref,
            Order: doc.Order,
            Links: doc.Links);
    }

    public static Result<DeclarativeComponentSpec> ParseComponent(string rawMarkdown, string filePath)
    {
        var parsedRes = ParseDocument(rawMarkdown, filePath, isSessionManifest: false);
        if (!parsedRes.IsSuccess) return parsedRes.Error!;
        var doc = parsedRes.Value;

        bool isExplicitQuiz = !string.IsNullOrWhiteSpace(doc.Kind) && QuizKindNames.Contains(doc.Kind);
        bool isExplicitArticle = string.Equals(doc.Kind, "article", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(doc.Kind) && !isExplicitArticle && !isExplicitQuiz)
            return new Error("Markdown.InvalidFrontmatter", $"Component Markdown file '{Path.GetFileName(filePath)}' must have kind 'article' or 'quiz' ('mcq'|'flashcard'|'mixed'), or omit kind.", ErrorType.Validation);

        var title = !string.IsNullOrWhiteSpace(doc.Title)
            ? doc.Title
            : doc.ExtractedHeading ?? ExtractFirstH1(doc.Blocks) ?? HumanizeFileStem(filePath);
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(filePath));

        bool isQuizDoc = isExplicitQuiz || (string.IsNullOrWhiteSpace(doc.Kind) && doc.Questions.Count > 0 && doc.Blocks.Count == 0);
        if (isQuizDoc)
        {
            if (doc.Questions.Count == 0)
                return new Error("Markdown.EmptyBody", $"Markdown quiz '{Path.GetFileName(filePath)}' has no question directives.", ErrorType.Validation);

            return new DeclarativeComponentSpec(
                Kind: "quiz",
                Title: title,
                QuizType: ResolveQuizType(doc.Kind, doc.QuizType),
                Blocks: null,
                Questions: doc.Questions,
                LayoutOverrides: doc.LayoutOverrides,
                Ref: doc.Ref,
                SourceDirectory: sourceDir);
        }

        if (doc.Blocks.Count == 0)
            return new Error("Markdown.EmptyBody", $"Markdown article '{Path.GetFileName(filePath)}' has no content blocks.", ErrorType.Validation);

        return new DeclarativeComponentSpec(
            Kind: "article",
            Title: title,
            QuizType: null,
            Blocks: doc.Blocks,
            Questions: null,
            LayoutOverrides: doc.LayoutOverrides,
            Ref: doc.Ref,
            SourceDirectory: sourceDir,
            Links: doc.Links);
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
        string? quizType = null;
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
                out kind, out name, out description, out title, out refKey, out quizType, out layoutOverrides, out order);
            if (!fmRes.IsSuccess) return fmRes.Error!;
            bodyStartLine = endIdx + 1;
        }

        if (isSessionManifest)
        {
            for (int i = bodyStartLine; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    return new Error(
                        "Json.InvalidFieldForType",
                        $"Directory '_session.md' is a frontmatter-only session manifest and cannot contain article or quiz body content (line {i + 1} of '{sourceLabel}').",
                        ErrorType.Validation,
                        Path: $"markdown:{i + 1}");
                }
            }
            return new ParsedMarkdownDocument(kind, name, description, title, refKey, quizType, layoutOverrides, order, [], []);
        }

        var bodyRes = ParseBody(lines, bodyStartLine, sourceLabel, kind);
        if (!bodyRes.IsSuccess) return bodyRes.Error!;

        return new ParsedMarkdownDocument(
            kind, name, description, title, refKey, quizType, layoutOverrides, order,
            bodyRes.Value.Blocks, bodyRes.Value.Questions, bodyRes.Value.ExtractedHeading, bodyRes.Value.Links);
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
        out string? quizType,
        out Dictionary<string, string>? layoutOverrides,
        out List<string>? order)
    {
        kind = null; name = null; description = null; title = null; refKey = null; quizType = null;
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
                var suggestion = TextSimilarity.FindClosest(key, AllowedFrontmatterKeys);
                return new Error(
                    "Markdown.InvalidFrontmatter",
                    $"Unknown frontmatter key '{key}' at line {lineNum} of '{sourceLabel}'. Allowed keys: kind, title, ref, name, description, quizType, layoutOverrides, order.",
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
                case "quiztype": quizType = scalar; break;
            }
        }

        return Result.Success();
    }

    private static Result<(IReadOnlyList<DeclarativeArticleBlockSpec> Blocks, IReadOnlyList<JsonQuestionSpec> Questions, string? ExtractedHeading, IReadOnlyList<DeclarativeLinkCardSpec>? Links)> ParseBody(
        string[] lines,
        int startLineIdx,
        string sourceLabel,
        string? frontmatterKind)
    {
        bool expectQuiz = !string.IsNullOrWhiteSpace(frontmatterKind) && QuizKindNames.Contains(frontmatterKind);
        bool expectArticle = string.Equals(frontmatterKind, "article", StringComparison.OrdinalIgnoreCase);

        var blocks = new List<DeclarativeArticleBlockSpec>();
        var questions = new List<JsonQuestionSpec>();
        var links = new List<DeclarativeLinkCardSpec>();
        var seenLinkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textBuffer = new StringBuilder();
        string? activeCodeFence = null;
        string? extractedHeading = null;

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
                if (expectQuiz)
                    return new Error("Markdown.UnexpectedContentInQuiz", $"Unexpected code fence outside a question directive at line {i + 1} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{i + 1}");

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

            if (trimmed.StartsWith(":::", StringComparison.Ordinal) && trimmed != ":::")
            {
                int openLine = i + 1;
                var headerRes = ParseDirectiveHeader(trimmed, openLine, sourceLabel);
                if (!headerRes.IsSuccess) return headerRes.Error!;
                var (dirName, attrs, posArg) = headerRes.Value;
                var dirLower = dirName.ToLowerInvariant();

                if (dirLower is "callout" or "pearl" or "clinical" or "warning")
                {
                    if (expectQuiz)
                        return new Error("Markdown.InvalidDirectiveForKind", $"Callout directive ':::{dirName}' at line {openLine} cannot be used inside a quiz Markdown file.", ErrorType.Validation, Path: $"markdown:{openLine}");

                    string rawTone;
                    string? calloutTitle = null;
                    string? calloutRef = null;
                    if (dirLower == "callout")
                    {
                        if (!string.IsNullOrWhiteSpace(posArg))
                            return new Error("Markdown.InvalidDirectiveAttr", $"Directive ':::callout' at line {openLine} does not accept positional text. Use ':::callout{{tone=\"...\" title=\"...\"}}'.", ErrorType.Validation, Path: $"markdown:{openLine}");

                        var attrCheck = ValidateDirectiveAttributes(attrs, AllowedCalloutAttrs, dirName, openLine, sourceLabel);
                        if (!attrCheck.IsSuccess) return attrCheck.Error!;

                        rawTone = attrs.TryGetValue("tone", out var tVal) ? tVal : "pearl";
                        calloutTitle = attrs.TryGetValue("title", out var ctVal) ? ctVal : null;
                        attrs.TryGetValue("ref", out calloutRef);
                    }
                    else
                    {
                        if (attrs.Count > 0)
                        {
                            var attrCheck = ValidateDirectiveAttributes(attrs, AllowedCalloutShorthandAttrs, dirName, openLine, sourceLabel);
                            if (!attrCheck.IsSuccess) return attrCheck.Error!;
                            calloutTitle = attrs.TryGetValue("title", out var ctVal) ? ctVal : null;
                            attrs.TryGetValue("ref", out calloutRef);
                        }
                        else if (!string.IsNullOrWhiteSpace(posArg))
                        {
                            calloutTitle = UnquoteYamlScalar(posArg);
                        }
                        rawTone = dirLower;
                    }

                    if (!ArticleBlock.TryParseCalloutTone(rawTone, out _))
                        return ArticleErrors.InvalidCalloutTone(rawTone);

                    var bodyRes = ReadDirectiveBody(lines, ref i, openLine, sourceLabel, dirName, EmptySubSections, "Markdown.UnclosedCallout");
                    if (!bodyRes.IsSuccess) return bodyRes.Error!;

                    var bodyText = bodyRes.Value.MainBody.Trim();
                    if (string.IsNullOrWhiteSpace(bodyText))
                        return ArticleErrors.EmptyCalloutContent();

                    FlushText();
                    blocks.Add(new DeclarativeArticleBlockSpec(
                        "callout",
                        bodyText,
                        null,
                        Ref: string.IsNullOrWhiteSpace(calloutRef) ? null : calloutRef,
                        Tone: rawTone.ToLowerInvariant(),
                        Title: string.IsNullOrWhiteSpace(calloutTitle) ? null : calloutTitle));
                    continue;
                }

                if (TryGetDirectiveRule(dirLower, out var rule))
                {
                    if (rule.IsArticleBlock && expectQuiz)
                        return new Error("Markdown.InvalidDirectiveForKind", $"Article directive ':::{dirName}' at line {openLine} cannot be used inside a quiz Markdown file.", ErrorType.Validation, Path: $"markdown:{openLine}");
                    if (!rule.IsArticleBlock && expectArticle)
                        return new Error("Markdown.InvalidDirectiveForKind", $"Quiz directive ':::{dirName}' at line {openLine} cannot be used inside an article Markdown file.", ErrorType.Validation, Path: $"markdown:{openLine}");
                    if (!string.IsNullOrWhiteSpace(posArg))
                        return new Error("Markdown.InvalidDirectiveAttr", $"Directive ':::{rule.CanonicalName}' at line {openLine} does not accept positional arguments.{rule.PosArgSuffix}", ErrorType.Validation, Path: $"markdown:{openLine}");

                    var attrCheck = ValidateDirectiveAttributes(attrs, rule.AllowedAttrs, rule.CanonicalName, openLine, sourceLabel);
                    if (!attrCheck.IsSuccess) return attrCheck.Error!;

                    var bodyRes = ReadDirectiveBody(lines, ref i, openLine, sourceLabel, rule.CanonicalName, rule.AllowedSubSections);
                    if (!bodyRes.IsSuccess) return bodyRes.Error!;

                    if (rule.ParseArticleBlock is not null)
                    {
                        var blockRes = rule.ParseArticleBlock(openLine, sourceLabel, attrs, bodyRes.Value);
                        if (!blockRes.IsSuccess) return blockRes.Error!;
                        FlushText();
                        blocks.Add(blockRes.Value);
                    }
                    else
                    {
                        var qRes = rule.ParseQuestion!(openLine, sourceLabel, attrs, bodyRes.Value);
                        if (!qRes.IsSuccess) return qRes.Error!;
                        FlushText();
                        questions.Add(qRes.Value);
                    }
                    continue;
                }

                if (dirLower is "case" or "case-group" or "casegroup")
                {
                    if (expectArticle)
                        return new Error("Markdown.InvalidDirectiveForKind", $"Quiz directive ':::{dirName}' at line {openLine} cannot be used inside an article Markdown file.", ErrorType.Validation, Path: $"markdown:{openLine}");
                    if (!string.IsNullOrWhiteSpace(posArg))
                        return new Error("Markdown.InvalidDirectiveAttr", $"Directive ':::case' at line {openLine} does not accept positional arguments.", ErrorType.Validation, Path: $"markdown:{openLine}");

                    var attrCheck = ValidateDirectiveAttributes(attrs, AllowedCaseAttrs, "case", openLine, sourceLabel);
                    if (!attrCheck.IsSuccess) return attrCheck.Error!;

                    var caseBodyRes = ReadCaseDirectiveBody(lines, ref i, openLine, sourceLabel);
                    if (!caseBodyRes.IsSuccess) return caseBodyRes.Error!;

                    var caseRes = ParseCaseDirective(openLine, sourceLabel, attrs, caseBodyRes.Value);
                    if (!caseRes.IsSuccess) return caseRes.Error!;

                    FlushText();
                    questions.Add(caseRes.Value);
                    continue;
                }

                if (dirLower is "link" or "link-card")
                {
                    if (expectQuiz)
                        return new Error("Markdown.InvalidDirectiveForKind", $"Article directive ':::{dirName}' at line {openLine} cannot be used inside a quiz Markdown file.", ErrorType.Validation, Path: $"markdown:{openLine}");
                    if (!string.IsNullOrWhiteSpace(posArg))
                        return new Error("Markdown.InvalidDirectiveAttr", $"Directive ':::link' at line {openLine} does not accept positional arguments. Use ':::link{{id=\"...\" title=\"...\" image=\"...\"}}'.", ErrorType.Validation, Path: $"markdown:{openLine}");

                    var attrCheck = ValidateDirectiveAttributes(attrs, AllowedLinkAttrs, "link", openLine, sourceLabel);
                    if (!attrCheck.IsSuccess) return attrCheck.Error!;

                    var bodyRes = ReadDirectiveBody(lines, ref i, openLine, sourceLabel, dirName, EmptySubSections);
                    if (!bodyRes.IsSuccess) return bodyRes.Error!;

                    var linkRes = ParseLinkDirective(openLine, sourceLabel, attrs, bodyRes.Value);
                    if (!linkRes.IsSuccess) return linkRes.Error!;

                    if (!seenLinkIds.Add(linkRes.Value.Id))
                        return new Error("Markdown.DuplicateLinkId", $"Duplicate link card id '{linkRes.Value.Id}' at line {openLine} of '{sourceLabel}'.", ErrorType.Conflict, Path: $"markdown:{openLine}");

                    FlushText();
                    links.Add(linkRes.Value);
                    continue;
                }

                return new Error(
                    "Markdown.UnknownFence",
                    $"Unsupported fenced directive ':::{dirName}' at line {openLine} of '{sourceLabel}'. Supported fences: ':::callout', ':::pearl', ':::clinical', ':::warning', ':::table', ':::diagram-explore', ':::reveal', ':::link', ':::mcq', ':::flashcard', ':::fill-blank', ':::assertion-reason', ':::spot-error', ':::diff', ':::mechanism', ':::relabel', ':::case', ':::classify', ':::match', ':::numeric'.",
                    ErrorType.Validation,
                    Path: $"markdown:{openLine}",
                    Hint: "Use ':::callout', ':::table', ':::diagram-explore', ':::reveal', ':::link', ':::mcq', ':::flashcard', ':::fill-blank', ':::assertion-reason', ':::spot-error', ':::diff', ':::mechanism', ':::relabel', ':::case', ':::classify', ':::match', or ':::numeric'.");
            }

            if (expectQuiz)
            {
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                if (extractedHeading is null && questions.Count == 0)
                {
                    var h1 = H1HeadingRegex().Match(trimmed);
                    if (h1.Success)
                    {
                        extractedHeading = h1.Groups["heading"].Value.Trim();
                        continue;
                    }
                }

                return new Error(
                    "Markdown.UnexpectedContentInQuiz",
                    $"Unexpected text outside a question directive at line {i + 1} of '{sourceLabel}'. Wrap quiz items in ':::mcq', ':::flashcard', ':::fill-blank', ':::assertion-reason', ':::spot-error', ':::diff', ':::mechanism', ':::relabel', ':::case', ':::classify', ':::match', or ':::numeric'.",
                    ErrorType.Validation,
                    Path: $"markdown:{i + 1}");
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

        if (blocks.Count > 0 && questions.Count > 0)
        {
            // Allow an auto-detected quiz file with a single top-level '# Heading' before directives
            if (blocks.Count == 1 && blocks[0].Kind == "text" && !string.IsNullOrWhiteSpace(blocks[0].Text) && !blocks[0].Text!.Contains('\n'))
            {
                var h1 = H1HeadingRegex().Match(blocks[0].Text!.Trim());
                if (h1.Success)
                {
                    extractedHeading ??= h1.Groups["heading"].Value.Trim();
                    blocks.Clear();
                }
            }

            if (blocks.Count > 0)
            {
                return new Error(
                    "Markdown.MixedContentNotAllowed",
                    $"Markdown file '{sourceLabel}' mixes article content with quiz directives (':::mcq' / ':::flashcard' / ':::fill-blank' / ':::assertion-reason' / ':::spot-error' / ':::diff' / ':::mechanism' / ':::relabel' / ':::case' / ':::classify' / ':::match' / ':::numeric'). Split articles and quizzes into separate files.",
                    ErrorType.Validation);
            }
        }

        return (blocks, questions, extractedHeading, links.Count > 0 ? links : null);
    }

    private static Result<(string Name, Dictionary<string, string> Attrs, string? PositionalArg)> ParseDirectiveHeader(
        string trimmedLine,
        int lineNum,
        string sourceLabel)
    {
        var match = DirectiveOpenFenceRegex().Match(trimmedLine);
        if (!match.Success)
        {
            return new Error(
                "Markdown.InvalidDirectiveAttr",
                $"Malformed directive header at line {lineNum} of '{sourceLabel}': '{trimmedLine}'.",
                ErrorType.Validation,
                Path: $"markdown:{lineNum}");
        }

        var name = match.Groups["name"].Value.Trim();
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawRest = match.Groups["rest"].Value;
        var restTrimmed = rawRest.Trim();
        string? posArg = null;
        string? rawAttrs = null;

        if (restTrimmed.Length > 0)
        {
            if (restTrimmed[0] == '{')
            {
                char quote = '\0';
                int closeBrace = -1;
                for (int idx = 1; idx < restTrimmed.Length; idx++)
                {
                    char ch = restTrimmed[idx];
                    if (quote != '\0')
                    {
                        if (ch == quote && !Phase1PayloadRules.IsEscaped(restTrimmed, idx))
                            quote = '\0';
                    }
                    else if (ch is '"' or '\'')
                    {
                        quote = ch;
                    }
                    else if (ch == '}')
                    {
                        closeBrace = idx;
                        break;
                    }
                }

                if (closeBrace < 0)
                {
                    return new Error(
                        "Markdown.InvalidDirectiveAttr",
                        $"Unclosed attribute braces on directive ':::{name}' at line {lineNum} of '{sourceLabel}'.",
                        ErrorType.Validation,
                        Path: $"markdown:{lineNum}");
                }

                var trailing = restTrimmed[(closeBrace + 1)..];
                if (!string.IsNullOrWhiteSpace(trailing))
                {
                    return new Error(
                        "Markdown.InvalidDirectiveAttr",
                        $"Unexpected trailing token '{trailing.Trim()}' after '}}' on directive ':::{name}' at line {lineNum} of '{sourceLabel}'.",
                        ErrorType.Validation,
                        Path: $"markdown:{lineNum}");
                }

                rawAttrs = restTrimmed[1..closeBrace];
            }
            else
            {
                if (!char.IsWhiteSpace(rawRest[0]))
                {
                    return new Error(
                        "Markdown.InvalidDirectiveAttr",
                        $"Malformed directive header at line {lineNum} of '{sourceLabel}': '{trimmedLine}'.",
                        ErrorType.Validation,
                        Path: $"markdown:{lineNum}");
                }

                posArg = restTrimmed;
                if (posArg.Contains('{') || posArg.Contains('}'))
                {
                    return new Error(
                        "Markdown.InvalidDirectiveAttr",
                        $"Unclosed or malformed attribute braces on directive ':::{name}' at line {lineNum} of '{sourceLabel}'.",
                        ErrorType.Validation,
                        Path: $"markdown:{lineNum}");
                }
            }
        }

        if (rawAttrs is not null)
        {
            int cursor = 0;
            foreach (Match m in DirectiveAttrPairRegex().Matches(rawAttrs))
            {
                var gap = rawAttrs[cursor..m.Index];
                if (!string.IsNullOrWhiteSpace(gap))
                {
                    return new Error(
                        "Markdown.InvalidDirectiveAttr",
                        $"Malformed attribute token '{gap.Trim()}' on directive ':::{name}' at line {lineNum} of '{sourceLabel}'. Expected key=value pairs.",
                        ErrorType.Validation,
                        Path: $"markdown:{lineNum}");
                }
                cursor = m.Index + m.Length;

                var k = m.Groups["k"].Value.Trim();
                var v = m.Groups["dq"].Success ? UnescapeQuotedValue(m.Groups["dq"].Value, '"')
                    : (m.Groups["sq"].Success ? UnescapeQuotedValue(m.Groups["sq"].Value, '\'') : m.Groups["bare"].Value);

                if (attrs.ContainsKey(k))
                {
                    return new Error(
                        "Markdown.InvalidDirectiveAttr",
                        $"Duplicate attribute '{k}' on directive ':::{name}' at line {lineNum} of '{sourceLabel}'.",
                        ErrorType.Validation,
                        Path: $"markdown:{lineNum}");
                }
                attrs[k] = v.Trim();
            }

            if (cursor < rawAttrs.Length && !string.IsNullOrWhiteSpace(rawAttrs[cursor..]))
            {
                return new Error(
                    "Markdown.InvalidDirectiveAttr",
                    $"Malformed attribute token '{rawAttrs[cursor..].Trim()}' on directive ':::{name}' at line {lineNum} of '{sourceLabel}'. Expected key=value pairs.",
                    ErrorType.Validation,
                    Path: $"markdown:{lineNum}");
            }
        }

        return (name, attrs, posArg);
    }

    private static Result ValidateDirectiveAttributes(
        Dictionary<string, string> attrs,
        HashSet<string> allowed,
        string directiveName,
        int lineNum,
        string sourceLabel)
    {
        foreach (var key in attrs.Keys)
        {
            if (!allowed.Contains(key))
            {
                var suggestion = TextSimilarity.FindClosest(key, allowed);
                var allowedList = string.Join(", ", allowed.OrderBy(a => a, StringComparer.Ordinal));
                return new Error(
                    "Markdown.InvalidDirectiveAttr",
                    $"Unknown attribute '{key}' on directive ':::{directiveName}' at line {lineNum} of '{sourceLabel}'. Allowed attributes: {allowedList}.",
                    ErrorType.Validation,
                    Path: $"markdown:{lineNum}",
                    Hint: suggestion is null ? null : $"Did you mean '{suggestion}'?");
            }
        }
        return Result.Success();
    }

    private static Result<ParsedDirectiveBody> ReadDirectiveBody(
        string[] lines,
        ref int lineIdx,
        int openLine,
        string sourceLabel,
        string directiveName,
        HashSet<string> allowedSubSections,
        string unclosedErrorCode = "Markdown.UnclosedDirective")
    {
        var mainBuffer = new StringBuilder();
        var subSections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentBuffer = mainBuffer;
        string? activeSection = null;
        string? innerCodeFence = null;
        bool closed = false;

        for (lineIdx = lineIdx + 1; lineIdx < lines.Length; lineIdx++)
        {
            var innerLine = lines[lineIdx];
            var innerTrim = innerLine.Trim();

            if (innerCodeFence is null && TryOpenCodeFence(innerTrim, out var innerToken))
            {
                innerCodeFence = innerToken;
                currentBuffer.AppendLine(innerLine);
                continue;
            }
            if (innerCodeFence is not null)
            {
                currentBuffer.AppendLine(innerLine);
                if (IsCloseCodeFence(innerTrim, innerCodeFence))
                    innerCodeFence = null;
                continue;
            }

            if (innerTrim == ":::")
            {
                closed = true;
                break;
            }

            if (innerTrim.StartsWith(":::", StringComparison.Ordinal))
            {
                var subMatch = DirectiveSubSectionRegex().Match(innerTrim);
                if (subMatch.Success)
                {
                    var sectionName = subMatch.Groups["section"].Value.Trim().ToLowerInvariant();
                    if (!allowedSubSections.Contains(sectionName))
                    {
                        var suggestion = allowedSubSections.Count > 0
                            ? TextSimilarity.FindClosest(sectionName, allowedSubSections)
                            : null;
                        var allowedDesc = allowedSubSections.Count > 0
                            ? $"Allowed sub-sections: {string.Join(", ", allowedSubSections.OrderBy(s => s, StringComparer.Ordinal).Select(s => $"':::{s}'"))}."
                            : $"Directive ':::{directiveName}' does not support sub-sections.";
                        return new Error(
                            "Markdown.UnknownSubSection",
                            $"Unknown sub-section ':::{sectionName}' inside ':::{directiveName}' at line {lineIdx + 1} of '{sourceLabel}'. {allowedDesc}",
                            ErrorType.Validation,
                            Path: $"markdown:{lineIdx + 1}",
                            Hint: suggestion is null ? null : $"Did you mean ':::{suggestion}'?");
                    }

                    if (string.Equals(activeSection, sectionName, StringComparison.OrdinalIgnoreCase) || subSections.ContainsKey(sectionName))
                    {
                        return new Error(
                            "Markdown.DuplicateSubSection",
                            $"Duplicate sub-section ':::{sectionName}' inside ':::{directiveName}' at line {lineIdx + 1} of '{sourceLabel}'.",
                            ErrorType.Validation,
                            Path: $"markdown:{lineIdx + 1}");
                    }

                    if (activeSection is not null)
                        subSections[activeSection] = currentBuffer.ToString().Trim();

                    activeSection = sectionName;
                    currentBuffer = new StringBuilder();
                    continue;
                }

                var unclosedMsg = string.Equals(unclosedErrorCode, "Markdown.UnclosedCallout", StringComparison.Ordinal)
                    ? $"Unclosed ':::' callout block starting at line {openLine} of '{sourceLabel}' before new fence '{innerTrim}' at line {lineIdx + 1}."
                    : $"Unclosed ':::{directiveName}' directive starting at line {openLine} of '{sourceLabel}' before new fence '{innerTrim}' at line {lineIdx + 1}.";
                return new Error(unclosedErrorCode, unclosedMsg, ErrorType.Validation, Path: $"markdown:{openLine}");
            }

            currentBuffer.AppendLine(innerLine);
        }

        if (!closed)
        {
            var label = string.Equals(unclosedErrorCode, "Markdown.UnclosedCallout", StringComparison.Ordinal)
                ? $"Unclosed ':::' callout block starting at line {openLine} of '{sourceLabel}'."
                : $"Unclosed ':::{directiveName}' directive starting at line {openLine} of '{sourceLabel}'.";
            return new Error(unclosedErrorCode, label, ErrorType.Validation, Path: $"markdown:{openLine}");
        }

        if (activeSection is not null)
            subSections[activeSection] = currentBuffer.ToString().Trim();

        return new ParsedDirectiveBody(mainBuffer.ToString().Trim(), subSections);
    }

    private sealed record DirectiveRule(
        string CanonicalName,
        bool IsArticleBlock,
        string PosArgSuffix,
        HashSet<string> AllowedAttrs,
        HashSet<string> AllowedSubSections,
        Func<int, string, Dictionary<string, string>, ParsedDirectiveBody, Result<DeclarativeArticleBlockSpec>>? ParseArticleBlock = null,
        Func<int, string, Dictionary<string, string>, ParsedDirectiveBody, Result<JsonQuestionSpec>>? ParseQuestion = null);

    private static bool TryGetDirectiveRule(string dirLower, out DirectiveRule rule)
    {
        rule = dirLower switch
        {
            "table" => new("table", true, " Use ':::table{title=\"...\" stickyFirstCol=true hideableCols=\"2,3\"}'.", AllowedTableAttrs, AllowedTableSubSections, ParseArticleBlock: ParseTableDirective),
            "diagram-explore" or "diagramexplore" => new("diagram-explore", true, "", AllowedDiagramExploreAttrs, EmptySubSections, ParseArticleBlock: ParseDiagramExploreDirective),
            "reveal" => new("reveal", true, "", AllowedRevealAttrs, EmptySubSections, ParseArticleBlock: ParseRevealDirective),
            "mcq" => new("mcq", false, " Use ':::mcq{ref=\"...\" multi=false}'.", AllowedMcqAttrs, AllowedMcqSubSections, ParseQuestion: ParseMcqDirective),
            "flashcard" => new("flashcard", false, " Use ':::flashcard{ref=\"...\" frontPosition=\"belowImage\"}'.", AllowedFlashcardAttrs, AllowedFlashcardSubSections, ParseQuestion: ParseFlashcardDirective),
            "fill-blank" or "fillblank" => new("fill-blank", false, " Use ':::fill-blank{fuzzy=true caseSensitive=false}'.", AllowedFillBlankAttrs, AllowedExplanationLayoutSubSections, ParseQuestion: ParseFillBlankDirective),
            "assertion-reason" or "assertionreason" => new("assertion-reason", false, " Use ':::assertion-reason{answer=\"both-explain\" scheme=\"4opt\"}'.", AllowedAssertionReasonAttrs, AllowedExplanationLayoutSubSections, ParseQuestion: ParseAssertionReasonDirective),
            "spot-error" or "spoterror" => new("spot-error", false, " Use ':::spot-error{requireFix=false allowNoError=true}'.", AllowedSpotErrorAttrs, AllowedExplanationLayoutSubSections, ParseQuestion: ParseSpotErrorDirective),
            "diff" or "differentiation" => new("diff", false, " Use ':::diff{mode=\"fill\" title=\"...\"}'.", AllowedDiffAttrs, AllowedExplanationLayoutSubSections, ParseQuestion: ParseDiffDirective),
            "mechanism" => new("mechanism", false, " Use ':::mechanism{title=\"...\"}'.", AllowedMechanismAttrs, AllowedExplanationLayoutSubSections, ParseQuestion: ParseMechanismDirective),
            "relabel" or "relabel-image" => new("relabel", false, " Use ':::relabel{image=\"...\" mode=\"bank\" style=\"blur\"}'.", AllowedRelabelAttrs, AllowedExplanationLayoutSubSections, ParseQuestion: ParseRelabelDirective),
            "classify" or "bucket-sort" => new("classify", false, "", AllowedClassifyAttrs, AllowedExplanationLayoutSubSections, ParseQuestion: ParseClassifyDirective),
            "match" or "match-following" => new("match", false, "", AllowedMatchAttrs, AllowedExplanationLayoutSubSections, ParseQuestion: ParseMatchDirective),
            "numeric" or "numeric-tolerance" => new("numeric", false, "", AllowedNumericAttrs, AllowedExplanationLayoutSubSections, ParseQuestion: ParseNumericDirective),
            _ => null!
        };
        return rule is not null;
    }

    private static Result<bool> TryParseBoolAttr(
        Dictionary<string, string> attrs,
        string key,
        bool defaultVal,
        string directiveTag,
        int openLine,
        string sourceLabel)
    {
        if (!attrs.TryGetValue(key, out var rawVal) || string.IsNullOrWhiteSpace(rawVal))
            return defaultVal;
        if (!bool.TryParse(rawVal, out bool parsed))
            return new Error("Markdown.InvalidDirectiveAttr", $"Invalid boolean '{rawVal}' for '{key}' on '{directiveTag}' at line {openLine} of '{sourceLabel}'. Use 'true' or 'false'.", ErrorType.Validation, Path: $"markdown:{openLine}");
        return parsed;
    }

    private static Result<string?> TryResolvePairAttr(
        Dictionary<string, string> attrs,
        string primaryKey,
        string secondaryKey,
        string directiveTag,
        bool required,
        string? requiredMsg,
        int openLine,
        string sourceLabel)
    {
        bool hasPrimary = attrs.TryGetValue(primaryKey, out var rawPrimary) && !string.IsNullOrWhiteSpace(rawPrimary);
        bool hasSecondary = attrs.TryGetValue(secondaryKey, out var rawSecondary) && !string.IsNullOrWhiteSpace(rawSecondary);
        if (hasPrimary && hasSecondary)
            return new Error("Markdown.InvalidDirectiveAttr", $"Specify either '{primaryKey}' or '{secondaryKey}' on '{directiveTag}' at line {openLine} of '{sourceLabel}', not both.", ErrorType.Validation, Path: $"markdown:{openLine}");
        if (!hasPrimary && !hasSecondary)
            return required
                ? new Error("Markdown.InvalidDirectiveAttr", requiredMsg ?? $"'{directiveTag}' at line {openLine} of '{sourceLabel}' requires '{primaryKey}' or '{secondaryKey}'.", ErrorType.Validation, Path: $"markdown:{openLine}")
                : (string?)null;
        return (hasPrimary ? rawPrimary! : rawSecondary!).Trim();
    }

    private static Result<(string CleanedText, string? ImagePath, string? Caption)> ExtractSingleStandaloneImage(
        string rawBody,
        int openLine,
        string sourceLabel,
        string errorCode,
        string multipleImagesMsg,
        string? initialImagePath,
        bool normalizeNewlines = false)
    {
        var input = normalizeNewlines ? rawBody.Replace("\r\n", "\n") : rawBody;
        var linesOut = new List<string>();
        string? imagePath = initialImagePath;
        string? caption = null;
        string? codeFence = null;

        foreach (var rawLine in input.Split('\n'))
        {
            var trimmedLine = rawLine.Trim();
            if (MarkdownFenceHelper.TryUpdateFenceState(trimmedLine, ref codeFence))
            {
                linesOut.Add(rawLine);
                continue;
            }
            if (codeFence is not null)
            {
                linesOut.Add(rawLine);
                continue;
            }
            if (TryParseStandaloneImage(trimmedLine, out _, out var imgLinePath, out var imgCap))
            {
                if (imagePath is not null)
                    return new Error(errorCode, multipleImagesMsg, ErrorType.Validation, Path: $"markdown:{openLine}");
                imagePath = imgLinePath;
                if (!string.IsNullOrWhiteSpace(imgCap))
                    caption ??= imgCap.Trim();
            }
            else
            {
                linesOut.Add(rawLine);
            }
        }

        return (string.Join('\n', linesOut).Trim(), imagePath, caption);
    }

    private static Result<Dictionary<string, string>?> TryResolveLayoutOverrides(
        ParsedDirectiveBody body,
        int openLine,
        string sourceLabel)
    {
        if (!body.SubSections.TryGetValue("layout", out var rawLayout))
            return Result<Dictionary<string, string>?>.Success(null);
        return ParseLayoutSubSection(rawLayout, openLine, sourceLabel);
    }

    private static Result<(double X, double Y, double W, double H)> ParseYamlRect(
        string raw,
        string errorCode,
        string directiveTag,
        int openLine,
        string sourceLabel)
    {
        var t = raw.Trim();
        if (!t.StartsWith('[') || !t.EndsWith(']'))
            return new Error(errorCode, $"Invalid 'rect' syntax in '{directiveTag}' at line {openLine} of '{sourceLabel}'. Expected '[x, y, w, h]'.", ErrorType.Validation, Path: $"markdown:{openLine}");
        var parts = SplitRespectingQuotes(t[1..^1], ',');
        if (parts.Count != 4 ||
            !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
            !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ||
            !double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
        {
            return new Error(errorCode, $"Invalid 'rect' numbers '{raw}' in '{directiveTag}' at line {openLine} of '{sourceLabel}'. Expected 4 invariant floats [x, y, w, h].", ErrorType.Validation, Path: $"markdown:{openLine}");
        }
        return (x, y, w, h);
    }

    private static Result<Dictionary<string, string>?> ParseLayoutSubSection(string? raw, int openLine, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result<Dictionary<string, string>?>.Success(null);

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in raw.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;
            int colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                return new Error(
                    "Markdown.InvalidDirectiveAttr",
                    $"Invalid ':::layout' entry '{trimmed}' in directive at line {openLine} of '{sourceLabel}'. Expected 'property: value'.",
                    ErrorType.Validation,
                    Path: $"markdown:{openLine}");
            }
            var prop = trimmed[..colon].Trim();
            var val = UnquoteYamlScalar(trimmed[(colon + 1)..]);
            overrides[prop] = val;
        }
        return Result<Dictionary<string, string>?>.Success(overrides.Count > 0 ? overrides : null);
    }

    private static Result<JsonQuestionSpec> ParseMcqDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);

        bool hasMulti = attrs.TryGetValue("multi", out var rawMulti);
        bool hasIsMulti = attrs.TryGetValue("isMultiCorrect", out var rawIsMulti);
        if (hasMulti && hasIsMulti)
            return new Error("Markdown.InvalidDirectiveAttr", $"Specify either 'multi' or 'isMultiCorrect' on ':::mcq' at line {openLine} of '{sourceLabel}', not both.", ErrorType.Validation, Path: $"markdown:{openLine}");

        bool isMultiCorrect = false;
        var multiText = hasMulti ? rawMulti : rawIsMulti;
        if (!string.IsNullOrWhiteSpace(multiText))
        {
            if (!bool.TryParse(multiText, out isMultiCorrect))
                return new Error("Markdown.InvalidDirectiveAttr", $"Invalid boolean '{multiText}' for 'multi' on ':::mcq' at line {openLine} of '{sourceLabel}'. Use 'true' or 'false'.", ErrorType.Validation, Path: $"markdown:{openLine}");
        }

        int? explicitMaxOptions = null;
        if (attrs.TryGetValue("maxOptions", out var rawMax))
        {
            if (!int.TryParse(rawMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMax) || parsedMax < 2 || parsedMax > 26)
                return new Error("Markdown.InvalidDirectiveAttr", $"Invalid 'maxOptions' value '{rawMax}' on ':::mcq' at line {openLine} of '{sourceLabel}'. Must be an integer between 2 and 26.", ErrorType.Validation, Path: $"markdown:{openLine}");
            explicitMaxOptions = parsedMax;
        }

        var imgRes = TryResolvePairAttr(attrs, "image", "imagePath", ":::mcq", required: false, null, openLine, sourceLabel);
        if (!imgRes.IsSuccess) return imgRes.Error!;
        string? promptImagePath = imgRes.Value;

        var promptLines = new List<string>();
        var rawOptions = new List<(bool IsCorrect, StringBuilder Content)>();
        bool inOptions = false;
        string? codeFence = null;

        foreach (var rawLine in body.MainBody.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (codeFence is null && TryOpenCodeFence(trimmed, out var token))
            {
                if (inOptions)
                    return new Error("Markdown.InvalidMcq", $"Code fence inside MCQ options is not supported at directive line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");
                codeFence = token;
                promptLines.Add(rawLine);
                continue;
            }
            if (codeFence is not null)
            {
                promptLines.Add(rawLine);
                if (IsCloseCodeFence(trimmed, codeFence))
                    codeFence = null;
                continue;
            }

            var optMatch = McqOptionLineRegex().Match(trimmed);
            if (optMatch.Success)
            {
                inOptions = true;
                bool isCorrect = optMatch.Groups["mark"].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
                var sb = new StringBuilder(optMatch.Groups["text"].Value.Trim());
                rawOptions.Add((isCorrect, sb));
                continue;
            }

            if (!inOptions)
            {
                if (TryParseStandaloneImage(trimmed, out _, out var imgPath, out _))
                {
                    if (promptImagePath is not null)
                        return new Error("Markdown.InvalidMcq", $"MCQ at line {openLine} of '{sourceLabel}' specifies multiple prompt images. Keep only one prompt image (attribute or standalone image line).", ErrorType.Validation, Path: $"markdown:{openLine}");
                    promptImagePath = imgPath;
                }
                else
                {
                    promptLines.Add(rawLine);
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            bool indented = rawLine.Length > 0 && (rawLine[0] == ' ' || rawLine[0] == '\t');
            if (indented && rawOptions.Count > 0)
            {
                var last = rawOptions[^1].Content;
                if (last.Length > 0) last.Append('\n');
                last.Append(trimmed);
                continue;
            }

            return new Error(
                "Markdown.InvalidMcq",
                $"Unexpected line '{trimmed}' inside ':::mcq' options at directive line {openLine} of '{sourceLabel}'. Prefix options with '- [ ]' or '- [x]', or indent continuation lines.",
                ErrorType.Validation,
                Path: $"markdown:{openLine}");
        }

        var promptText = string.Join('\n', promptLines).Trim();
        var options = new List<JsonMcqOptionSpec>(rawOptions.Count);
        for (int idx = 0; idx < rawOptions.Count; idx++)
        {
            var (isCorrect, sb) = rawOptions[idx];
            var optRaw = sb.ToString().Trim();
            string? optImage = null;
            var imgMatches = InlineImageRegex().Matches(optRaw);
            if (imgMatches.Count > 1)
                return new Error("Markdown.InvalidMcq", $"MCQ option {idx + 1} at directive line {openLine} of '{sourceLabel}' contains multiple images; at most one image per option is supported.", ErrorType.Validation, Path: $"markdown:{openLine}");
            if (imgMatches.Count == 1)
            {
                var m = imgMatches[0];
                optImage = (m.Groups["ang"].Success ? m.Groups["ang"].Value : m.Groups["path"].Value).Trim();
                optRaw = (optRaw[..m.Index] + optRaw[(m.Index + m.Length)..]).Trim();
            }

            options.Add(new JsonMcqOptionSpec(
                idx + 1,
                string.IsNullOrWhiteSpace(optRaw) ? null : optRaw,
                string.IsNullOrWhiteSpace(optImage) ? null : optImage,
                isCorrect));
        }

        var expRes = ResolveExplanation(attrs, body, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;
        string? explanation = expRes.Value;

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        int? effectiveMaxOptions = explicitMaxOptions ?? (options.Count >= 2 ? Math.Clamp(options.Count, 2, 26) : null);

        return new JsonQuestionSpec(
            Type: "mcq",
            Text: string.IsNullOrWhiteSpace(promptText) ? null : promptText,
            MaxOptions: effectiveMaxOptions,
            IsMultiCorrect: isMultiCorrect,
            Options: options,
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: promptImagePath,
            Explanation: explanation,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey);
    }

    private static Result<JsonQuestionSpec> ParseFlashcardDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        attrs.TryGetValue("frontPosition", out var frontPos);
        attrs.TryGetValue("backPosition", out var backPos);

        string frontRaw;
        if (body.SubSections.TryGetValue("front", out var explicitFront))
        {
            if (!string.IsNullOrWhiteSpace(body.MainBody))
                return new Error("Markdown.InvalidFlashcard", $"Flashcard at line {openLine} of '{sourceLabel}' has content before ':::front'. Put front content inside ':::front' or omit ':::front'.", ErrorType.Validation, Path: $"markdown:{openLine}");
            frontRaw = explicitFront;
        }
        else
        {
            frontRaw = body.MainBody;
        }

        if (!body.SubSections.TryGetValue("back", out var backRaw))
            return new Error("Markdown.InvalidFlashcard", $"Flashcard directive at line {openLine} of '{sourceLabel}' requires a ':::back' section.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var frontRes = ParseFlashcardFace(frontRaw, frontPos, "front", openLine, sourceLabel);
        if (!frontRes.IsSuccess) return frontRes.Error!;
        var backRes = ParseFlashcardFace(backRaw, backPos, "back", openLine, sourceLabel);
        if (!backRes.IsSuccess) return backRes.Error!;

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        return new JsonQuestionSpec(
            Type: "flashcard",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: frontRes.Value,
            Back: backRes.Value,
            LayoutOverrides: layoutOverrides,
            ImagePath: null,
            Explanation: null,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey);
    }

    private static Result<JsonFlashcardSideSpec> ParseFlashcardFace(
        string raw,
        string? positionAttr,
        string faceLabel,
        int openLine,
        string sourceLabel)
    {
        var imgRes = ExtractSingleStandaloneImage(
            raw, openLine, sourceLabel, "Markdown.InvalidFlashcard",
            $"Flashcard {faceLabel} face at line {openLine} of '{sourceLabel}' contains multiple images; at most one image per face is supported.",
            initialImagePath: null);
        if (!imgRes.IsSuccess) return imgRes.Error!;

        var (text, imagePath, _) = imgRes.Value;
        return new JsonFlashcardSideSpec(
            string.IsNullOrWhiteSpace(text) ? null : text,
            string.IsNullOrWhiteSpace(imagePath) ? null : imagePath,
            string.IsNullOrWhiteSpace(positionAttr) ? null : positionAttr);
    }

    private static Result<DeclarativeArticleBlockSpec> ParseTableDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        attrs.TryGetValue("title", out var title);
        attrs.TryGetValue("caption", out var caption);

        var stickyRes = TryParseBoolAttr(attrs, "stickyFirstCol", false, ":::table", openLine, sourceLabel);
        if (!stickyRes.IsSuccess) return stickyRes.Error!;
        bool stickyFirstCol = stickyRes.Value;

        var tableRes = Phase1PayloadRules.ParseMarkdownTable(body.MainBody);
        if (!tableRes.IsSuccess)
            return new Error("Markdown.InvalidTable", $"Invalid ':::table' at line {openLine} of '{sourceLabel}': {tableRes.Error!.Description}", ErrorType.Validation, Path: $"markdown:{openLine}");

        var (headers, alignments, rows) = tableRes.Value;
        var hideableCols = new List<int>();
        if (attrs.TryGetValue("hideableCols", out var rawHideable) && !string.IsNullOrWhiteSpace(rawHideable))
        {
            foreach (var part in rawHideable.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int colIdx) || colIdx < 1 || colIdx > headers.Count)
                {
                    return new Error(
                        "Markdown.InvalidDirectiveAttr",
                        $"Invalid 'hideableCols' entry '{part.Trim()}' on ':::table' at line {openLine} of '{sourceLabel}'. Column indices must be between 1 and {headers.Count}.",
                        ErrorType.Validation,
                        Path: $"markdown:{openLine}");
                }
                if (!hideableCols.Contains(colIdx))
                    hideableCols.Add(colIdx);
            }
        }

        string? footnote = body.SubSections.TryGetValue("footnote", out var rawFn) && !string.IsNullOrWhiteSpace(rawFn)
            ? rawFn
            : null;

        var payload = new TableBlockPayload(
            string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
            stickyFirstCol,
            hideableCols,
            headers,
            alignments,
            rows,
            footnote);

        return new DeclarativeArticleBlockSpec(
            Kind: "table",
            Text: null,
            ImagePath: null,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            Caption: payload.Caption,
            Alt: null,
            Tone: null,
            Title: payload.Title,
            PayloadJson: Phase1PayloadCodec.Serialize(payload));
    }

    private static Result<JsonQuestionSpec> ParseFillBlankDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);

        var fuzzyRes = TryParseBoolAttr(attrs, "fuzzy", true, ":::fill-blank", openLine, sourceLabel);
        if (!fuzzyRes.IsSuccess) return fuzzyRes.Error!;
        bool fuzzy = fuzzyRes.Value;

        var csRes = TryParseBoolAttr(attrs, "caseSensitive", false, ":::fill-blank", openLine, sourceLabel);
        if (!csRes.IsSuccess) return csRes.Error!;
        bool caseSensitive = csRes.Value;

        attrs.TryGetValue("placeholder", out var rawPlaceholder);
        var placeholder = string.IsNullOrWhiteSpace(rawPlaceholder) ? null : rawPlaceholder.Trim();

        var template = body.MainBody.Trim();
        var slotsRes = Phase1PayloadRules.ParseFillBlankTemplate(template);
        if (!slotsRes.IsSuccess)
            return new Error("Markdown.InvalidFillBlank", $"Invalid ':::fill-blank' at line {openLine} of '{sourceLabel}': {slotsRes.Error!.Description}", ErrorType.Validation, Path: $"markdown:{openLine}");

        var expRes = ResolveExplanation(attrs, body, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;
        string? explanation = expRes.Value;

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        var payload = new FillBlankPayload(template, slotsRes.Value, fuzzy, caseSensitive, explanation, placeholder);
        return new JsonQuestionSpec(
            Type: "fill-blank",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: null,
            Explanation: explanation,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            PayloadJson: Phase1PayloadCodec.Serialize(payload));
    }

    private static Result<JsonQuestionSpec> ParseAssertionReasonDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        if (!attrs.TryGetValue("answer", out var rawAnswer) || string.IsNullOrWhiteSpace(rawAnswer))
            return new Error("Markdown.InvalidAssertionReason", $"':::assertion-reason' at line {openLine} of '{sourceLabel}' requires an 'answer' attribute ({string.Join(", ", Phase1PayloadRules.AssertionReasonAnswers)}).", ErrorType.Validation, Path: $"markdown:{openLine}");

        var answer = Phase1PayloadRules.NormalizeAssertionReasonAnswer(rawAnswer);
        if (!Phase1PayloadRules.IsValidAssertionReasonAnswer(answer))
            return new Error("Markdown.InvalidDirectiveAttr", $"Invalid 'answer' value '{rawAnswer}' on ':::assertion-reason' at line {openLine} of '{sourceLabel}'. Allowed: {string.Join(", ", Phase1PayloadRules.AssertionReasonAnswers)}.", ErrorType.Validation, Path: $"markdown:{openLine}");

        attrs.TryGetValue("scheme", out var rawScheme);
        if (!string.IsNullOrWhiteSpace(rawScheme) && !Phase1PayloadRules.IsValidAssertionReasonScheme(rawScheme))
            return new Error("Markdown.InvalidDirectiveAttr", $"Invalid 'scheme' value '{rawScheme}' on ':::assertion-reason' at line {openLine} of '{sourceLabel}'. Use '4opt' or '5opt'.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var scheme = Phase1PayloadRules.ResolveAssertionReasonScheme(rawScheme, answer);
        if (answer == "both-false" && scheme == "4opt")
            return new Error("Markdown.InvalidAssertionReason", $"':::assertion-reason' at line {openLine} of '{sourceLabel}' cannot pair answer 'both-false' with scheme '4opt'. Use scheme='5opt' or omit scheme.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var assertionLines = new List<string>();
        var reasonLines = new List<string>();
        string? activePart = null;
        bool seenA = false;
        bool seenR = false;

        foreach (var rawLine in body.MainBody.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("A:", StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Length == 2 || char.IsWhiteSpace(trimmed[2])))
            {
                if (seenA)
                    return new Error("Markdown.InvalidAssertionReason", $"Duplicate 'A:' statement in ':::assertion-reason' at line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");
                seenA = true;
                activePart = "A";
                assertionLines.Add(trimmed[2..].Trim());
                continue;
            }
            if (trimmed.StartsWith("R:", StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Length == 2 || char.IsWhiteSpace(trimmed[2])))
            {
                if (seenR)
                    return new Error("Markdown.InvalidAssertionReason", $"Duplicate 'R:' statement in ':::assertion-reason' at line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");
                seenR = true;
                activePart = "R";
                reasonLines.Add(trimmed[2..].Trim());
                continue;
            }
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (activePart == "A") assertionLines.Add(string.Empty);
                else if (activePart == "R") reasonLines.Add(string.Empty);
                continue;
            }
            if (activePart == "A") assertionLines.Add(trimmed);
            else if (activePart == "R") reasonLines.Add(trimmed);
            else
                return new Error("Markdown.InvalidAssertionReason", $"Unexpected line '{trimmed}' in ':::assertion-reason' at line {openLine} of '{sourceLabel}'. Prefix statements with 'A:' and 'R:'.", ErrorType.Validation, Path: $"markdown:{openLine}");
        }

        var assertion = string.Join('\n', assertionLines).Trim();
        var reason = string.Join('\n', reasonLines).Trim();
        if (string.IsNullOrWhiteSpace(assertion) || string.IsNullOrWhiteSpace(reason))
            return new Error("Markdown.InvalidAssertionReason", $"':::assertion-reason' at line {openLine} of '{sourceLabel}' requires both non-empty 'A:' and 'R:' statements.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var expRes = ResolveExplanation(attrs, body, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;
        string? explanation = expRes.Value;

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        var payload = new AssertionReasonPayload(assertion, reason, answer, scheme, explanation);
        return new JsonQuestionSpec(
            Type: "assertion-reason",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: null,
            Explanation: explanation,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            PayloadJson: Phase1PayloadCodec.Serialize(payload));
    }

    private static Result<JsonQuestionSpec> ParseSpotErrorDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);

        var rfRes = TryParseBoolAttr(attrs, "requireFix", false, ":::spot-error", openLine, sourceLabel);
        if (!rfRes.IsSuccess) return rfRes.Error!;
        bool requireFix = rfRes.Value;

        var neRes = TryParseBoolAttr(attrs, "noError", false, ":::spot-error", openLine, sourceLabel);
        if (!neRes.IsSuccess) return neRes.Error!;
        bool noError = neRes.Value;

        var aneRes = TryParseBoolAttr(attrs, "allowNoError", noError, ":::spot-error", openLine, sourceLabel);
        if (!aneRes.IsSuccess) return aneRes.Error!;
        bool allowNoError = aneRes.Value;
        if (noError && !allowNoError)
            return new Error("Markdown.InvalidSpotError", $"':::spot-error' at line {openLine} of '{sourceLabel}' cannot set 'noError=true' with 'allowNoError=false'.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var imgAttrRes = TryResolvePairAttr(attrs, "image", "imagePath", ":::spot-error", required: false, null, openLine, sourceLabel);
        if (!imgAttrRes.IsSuccess) return imgAttrRes.Error!;

        var extractRes = ExtractSingleStandaloneImage(
            body.MainBody, openLine, sourceLabel, "Markdown.InvalidSpotError",
            $"':::spot-error' at line {openLine} of '{sourceLabel}' specifies multiple images. Keep only one image (attribute or standalone image line).",
            imgAttrRes.Value);
        if (!extractRes.IsSuccess) return extractRes.Error!;
        var (template, imagePath, _) = extractRes.Value;
        var segsRes = Phase1PayloadRules.ParseSpotErrorTemplate(template);
        if (!segsRes.IsSuccess)
            return new Error("Markdown.InvalidSpotError", $"Invalid ':::spot-error' at line {openLine} of '{sourceLabel}': {segsRes.Error!.Description}", ErrorType.Validation, Path: $"markdown:{openLine}");

        int candidateCount = segsRes.Value.Count(s => s.IsCandidate);
        int errorCount = segsRes.Value.Count(s => s.IsError);
        if (candidateCount < 2)
            return new Error("Markdown.InvalidSpotError", $"':::spot-error' at line {openLine} of '{sourceLabel}' must define at least 2 candidate '[span]' segments.", ErrorType.Validation, Path: $"markdown:{openLine}");
        if (noError && errorCount > 0)
            return new Error("Markdown.InvalidSpotError", $"':::spot-error' at line {openLine} of '{sourceLabel}' has 'noError=true' but also marks error span(s).", ErrorType.Validation, Path: $"markdown:{openLine}");
        if (!noError && errorCount == 0)
            return new Error("Markdown.InvalidSpotError", $"':::spot-error' at line {openLine} of '{sourceLabel}' must mark at least one error span '[~~wrong~~ -> right]' or set 'noError=true'.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var expRes = ResolveExplanation(attrs, body, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;
        string? explanation = expRes.Value;

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        var payload = new SpotErrorPayload(template, segsRes.Value, requireFix, allowNoError, noError, explanation, imagePath);
        return new JsonQuestionSpec(
            Type: "spot-error",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: imagePath,
            Explanation: explanation,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            PayloadJson: Phase1PayloadCodec.Serialize(payload));
    }

    private static Result<DeclarativeLinkCardSpec> ParseLinkDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        if (!attrs.TryGetValue("id", out var rawId) || string.IsNullOrWhiteSpace(rawId))
            return new Error("Markdown.InvalidDirectiveAttr", $"':::link' at line {openLine} of '{sourceLabel}' requires a non-empty 'id' attribute.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var idRes = ArticleLinkRules.ValidateAndNormalizeCardId(rawId);
        if (!idRes.IsSuccess || string.IsNullOrWhiteSpace(idRes.Value))
            return idRes.Error ?? new Error("Markdown.InvalidDirectiveAttr", $"Invalid link card id '{rawId}' at line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");

        attrs.TryGetValue("title", out var rawTitle);
        var imgAttrRes = TryResolvePairAttr(attrs, "image", "imagePath", ":::link", required: false, null, openLine, sourceLabel);
        if (!imgAttrRes.IsSuccess) return imgAttrRes.Error!;

        var extractRes = ExtractSingleStandaloneImage(
            body.MainBody, openLine, sourceLabel, "Markdown.InvalidLink",
            $"':::link' at line {openLine} of '{sourceLabel}' specifies multiple images; at most one image is supported.",
            imgAttrRes.Value);
        if (!extractRes.IsSuccess) return extractRes.Error!;
        var (text, imagePath, _) = extractRes.Value;

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(imagePath))
            return new Error("Markdown.InvalidLink", $"':::link' '{idRes.Value}' at line {openLine} of '{sourceLabel}' must contain text, an image, or both.", ErrorType.Validation, Path: $"markdown:{openLine}");

        return new DeclarativeLinkCardSpec(
            idRes.Value,
            string.IsNullOrWhiteSpace(rawTitle) ? null : rawTitle.Trim(),
            string.IsNullOrWhiteSpace(text) ? null : text,
            imagePath);
    }

    private static Result<JsonQuestionSpec> ParseDiffDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        attrs.TryGetValue("title", out var title);

        attrs.TryGetValue("placeholder", out var rawPlaceholder);
        var placeholder = string.IsNullOrWhiteSpace(rawPlaceholder) ? null : rawPlaceholder.Trim();
        if (!attrs.TryGetValue("mode", out var rawMode) || string.IsNullOrWhiteSpace(rawMode))
            return new Error("Markdown.InvalidDirectiveAttr", $"':::diff' at line {openLine} of '{sourceLabel}' requires a 'mode' attribute ('fill', 'spot-wrong', or 'rearrange').", ErrorType.Validation, Path: $"markdown:{openLine}");

        var mode = Phase2PayloadRules.NormalizeDiffMode(rawMode);
        if (!Phase2PayloadRules.IsValidDiffMode(mode))
            return new Error("Markdown.InvalidDirectiveAttr", $"Invalid 'mode' value '{rawMode}' on ':::diff' at line {openLine} of '{sourceLabel}'. Use 'fill', 'spot-wrong', or 'rearrange'.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var bankRes = TryParseBoolAttr(attrs, "bank", false, ":::diff", openLine, sourceLabel);
        if (!bankRes.IsSuccess) return bankRes.Error!;
        var fuzzyRes = TryParseBoolAttr(attrs, "fuzzy", true, ":::diff", openLine, sourceLabel);
        if (!fuzzyRes.IsSuccess) return fuzzyRes.Error!;
        var csRes = TryParseBoolAttr(attrs, "caseSensitive", false, ":::diff", openLine, sourceLabel);
        if (!csRes.IsSuccess) return csRes.Error!;
        var aneRes = TryParseBoolAttr(attrs, "allowNoError", false, ":::diff", openLine, sourceLabel);
        if (!aneRes.IsSuccess) return aneRes.Error!;

        bool bank = bankRes.Value, fuzzy = fuzzyRes.Value, caseSensitive = csRes.Value, allowNoError = aneRes.Value;

        var tableRes = Phase1PayloadRules.ParseMarkdownTable(body.MainBody);
        if (!tableRes.IsSuccess)
            return new Error("Markdown.InvalidDiff", $"Invalid ':::diff' table at line {openLine} of '{sourceLabel}': {tableRes.Error!.Description}", ErrorType.Validation, Path: $"markdown:{openLine}");

        var (headers, alignments, rows) = tableRes.Value;
        var specialRes = Phase2PayloadRules.ParseDifferentiationTable(mode, headers, rows);
        if (!specialRes.IsSuccess)
            return new Error("Markdown.InvalidDiff", $"Invalid ':::diff' at line {openLine} of '{sourceLabel}': {specialRes.Error!.Description}", ErrorType.Validation, Path: $"markdown:{openLine}");

        if (mode == "fill" && specialRes.Value.Count(c => c.IsBlank) == 0)
            return new Error("Markdown.InvalidDiff", $"':::diff{{mode=\"fill\"}}' at line {openLine} of '{sourceLabel}' must contain at least one '{{{{answer}}}}' blank cell.", ErrorType.Validation, Path: $"markdown:{openLine}");

        if (mode == "spot-wrong" && !allowNoError && specialRes.Value.Count(c => c.IsError) == 0)
            return new Error("Markdown.InvalidDiff", $"':::diff{{mode=\"spot-wrong\"}}' at line {openLine} of '{sourceLabel}' must mark at least one '~~wrong~~ -> correction' cell or set 'allowNoError=true'.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var expRes = ResolveExplanation(attrs, body, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;
        string? explanation = expRes.Value;

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        var payload = new DifferentiationPayload(
            mode,
            string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            bank,
            fuzzy,
            caseSensitive,
            allowNoError,
            headers,
            alignments,
            rows,
            specialRes.Value, explanation, placeholder);

        return new JsonQuestionSpec(
            Type: "diff",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: null,
            Explanation: explanation,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            PayloadJson: Phase1PayloadCodec.Serialize(payload));
    }

    private static Result<JsonQuestionSpec> ParseMechanismDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        attrs.TryGetValue("title", out var title);

        var stepsRes = Phase2PayloadRules.ParseMechanismLines(body.MainBody);
        if (!stepsRes.IsSuccess)
            return new Error("Markdown.InvalidMechanism", $"Invalid ':::mechanism' at line {openLine} of '{sourceLabel}': {stepsRes.Error!.Description}", ErrorType.Validation, Path: $"markdown:{openLine}");

        var steps = stepsRes.Value;
        if (steps.Count(s => !s.IsDistractor) < 2)
            return new Error("Markdown.InvalidMechanism", $"':::mechanism' at line {openLine} of '{sourceLabel}' requires at least 2 ordered steps.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (!seen.Add(step.Text.Trim()))
                return new Error("Markdown.InvalidMechanism", $"':::mechanism' at line {openLine} of '{sourceLabel}' contains duplicate step text '{step.Text.Trim()}'.", ErrorType.Validation, Path: $"markdown:{openLine}");
        }

        var expRes = ResolveExplanation(attrs, body, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;
        string? explanation = expRes.Value;

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        var payload = new MechanismPayload(
            string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            steps,
            explanation);

        return new JsonQuestionSpec(
            Type: "mechanism",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: null,
            Explanation: explanation,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            PayloadJson: Phase1PayloadCodec.Serialize(payload));
    }

    private static Result<JsonQuestionSpec> ParseRelabelDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);

        var imgRes = TryResolvePairAttr(attrs, "image", "imagePath", ":::relabel", required: true,
            $"':::relabel' at line {openLine} of '{sourceLabel}' requires an 'image' (or 'imagePath') attribute.", openLine, sourceLabel);
        if (!imgRes.IsSuccess) return imgRes.Error!;
        var imagePath = imgRes.Value!;

        var masksRes = TryResolvePairAttr(attrs, "masks", "masksPath", ":::relabel", required: false, null, openLine, sourceLabel);
        if (!masksRes.IsSuccess) return masksRes.Error!;
        string? masksPath = masksRes.Value;

        string mode = "bank";
        if (attrs.TryGetValue("mode", out var rawMode) && !string.IsNullOrWhiteSpace(rawMode))
        {
            mode = Phase2PayloadRules.NormalizeRelabelMode(rawMode);
            if (!Phase2PayloadRules.IsValidRelabelMode(mode))
                return new Error("Markdown.InvalidDirectiveAttr", $"Invalid 'mode' value '{rawMode}' on ':::relabel' at line {openLine} of '{sourceLabel}'. Use 'reveal', 'bank', or 'recall'.", ErrorType.Validation, Path: $"markdown:{openLine}");
        }

        string style = "solid";
        if (attrs.TryGetValue("style", out var rawStyle) && !string.IsNullOrWhiteSpace(rawStyle))
        {
            style = Phase2PayloadRules.NormalizeRelabelStyle(rawStyle);
            if (!Phase2PayloadRules.IsValidRelabelStyle(style))
                return new Error("Markdown.InvalidDirectiveAttr", $"Invalid 'style' value '{rawStyle}' on ':::relabel' at line {openLine} of '{sourceLabel}'. Use 'solid', 'blur', 'pixelate', or 'whiteout'.", ErrorType.Validation, Path: $"markdown:{openLine}");
        }

        var fuzzyRes = TryParseBoolAttr(attrs, "fuzzy", true, ":::relabel", openLine, sourceLabel);
        if (!fuzzyRes.IsSuccess) return fuzzyRes.Error!;
        var csRes = TryParseBoolAttr(attrs, "caseSensitive", false, ":::relabel", openLine, sourceLabel);
        if (!csRes.IsSuccess) return csRes.Error!;
        bool fuzzy = fuzzyRes.Value, caseSensitive = csRes.Value;

        string? prompt = null;
        List<string>? include = null;
        List<string> distractors = [];
        List<RelabelBox> boxes = [];

        string? section = null;
        bool inBoxAliases = false;
        string? curLabel = null;
        List<string>? curAliases = null;
        double? curX = null, curY = null, curW = null, curH = null;
        bool hasCurBox = false;

        Result FlushCurrentBox()
        {
            if (!hasCurBox) return Result.Success();
            if (string.IsNullOrWhiteSpace(curLabel))
                return new Error("Markdown.InvalidRelabel", $"':::relabel' at line {openLine} of '{sourceLabel}' has a box missing a non-empty 'label'.", ErrorType.Validation, Path: $"markdown:{openLine}");
            if (!curX.HasValue || !curY.HasValue || !curW.HasValue || !curH.HasValue)
                return new Error("Markdown.InvalidRelabel", $"':::relabel' at line {openLine} of '{sourceLabel}' box '{curLabel}' is missing 'rect: [x, y, w, h]' or x/y/w/h coordinates.", ErrorType.Validation, Path: $"markdown:{openLine}");
            var candidate = new RelabelBox(boxes.Count + 1, curLabel.Trim(), curAliases ?? [], curX.Value, curY.Value, curW.Value, curH.Value);
            if (!Phase2PayloadRules.ValidateRelabelBox(candidate))
                return new Error("Markdown.InvalidRelabel", $"':::relabel' at line {openLine} of '{sourceLabel}' box '{curLabel}' has out-of-bounds normalized coordinates.", ErrorType.Validation, Path: $"markdown:{openLine}");
            boxes.Add(candidate);
            hasCurBox = false; inBoxAliases = false; curLabel = null; curAliases = null;
            curX = curY = curW = curH = null;
            return Result.Success();
        }

        static Result<List<string>> ParseInlineYamlList(string raw, string keyName, int line, string label)
        {
            var t = raw.Trim();
            if (!t.StartsWith('[') || !t.EndsWith(']'))
                return new Error("Markdown.InvalidRelabel", $"Invalid '{keyName}' list syntax in ':::relabel' at line {line} of '{label}'. Expected '[item1, item2]'.", ErrorType.Validation, Path: $"markdown:{line}");
            var list = new List<string>();
            foreach (var part in SplitRespectingQuotes(t[1..^1], ','))
            {
                var item = UnquoteYamlScalar(part);
                if (!string.IsNullOrWhiteSpace(item)) list.Add(item);
            }
            return list;
        }

        Result ApplyBoxKeyVal(string bKey, string bVal)
        {
            inBoxAliases = false;
            switch (bKey.ToLowerInvariant())
            {
                case "label":
                    curLabel = UnquoteYamlScalar(bVal);
                    return Result.Success();
                case "aliases":
                    if (string.IsNullOrEmpty(bVal)) { curAliases ??= []; inBoxAliases = true; return Result.Success(); }
                    var alRes = ParseInlineYamlList(bVal, "aliases", openLine, sourceLabel);
                    if (!alRes.IsSuccess) return alRes.Error!;
                    curAliases = alRes.Value;
                    return Result.Success();
                case "rect":
                    var rectRes = ParseYamlRect(bVal, "Markdown.InvalidRelabel", ":::relabel", openLine, sourceLabel);
                    if (!rectRes.IsSuccess) return rectRes.Error!;
                    (curX, curY, curW, curH) = rectRes.Value;
                    return Result.Success();
                case "x":
                case "y":
                case "w":
                case "h":
                    if (!double.TryParse(bVal.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                        return new Error("Markdown.InvalidRelabel", $"Invalid coordinate '{bKey}: {bVal}' in ':::relabel' at line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");
                    if (bKey.Equals("x", StringComparison.OrdinalIgnoreCase)) curX = num;
                    else if (bKey.Equals("y", StringComparison.OrdinalIgnoreCase)) curY = num;
                    else if (bKey.Equals("w", StringComparison.OrdinalIgnoreCase)) curW = num;
                    else curH = num;
                    return Result.Success();
                default:
                    return new Error("Markdown.InvalidRelabel", $"Unknown box key '{bKey}' in ':::relabel' at line {openLine} of '{sourceLabel}'. Allowed box keys: label, aliases, rect, x, y, w, h.", ErrorType.Validation, Path: $"markdown:{openLine}");
            }
        }

        foreach (var rawLine in body.MainBody.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            bool indented = rawLine.Length > 0 && (rawLine[0] == ' ' || rawLine[0] == '\t');

            if (section == "boxes" && (indented || trimmed.StartsWith('-')))
            {
                if (inBoxAliases && trimmed.StartsWith('-') && !trimmed.Contains(':'))
                {
                    var aliasItem = UnquoteYamlScalar(trimmed[1..]);
                    if (!string.IsNullOrWhiteSpace(aliasItem)) (curAliases ??= []).Add(aliasItem);
                    continue;
                }

                if (trimmed.StartsWith('-'))
                {
                    var flushRes = FlushCurrentBox();
                    if (!flushRes.IsSuccess) return flushRes.Error!;
                    hasCurBox = true;
                    curAliases = [];
                    var afterDash = trimmed[1..].Trim();
                    if (!string.IsNullOrEmpty(afterDash))
                    {
                        int cIdx = afterDash.IndexOf(':');
                        if (cIdx <= 0)
                            return new Error("Markdown.InvalidRelabel", $"Expected 'key: value' after '-' in ':::relabel' boxes at line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");
                        var kvRes = ApplyBoxKeyVal(afterDash[..cIdx].Trim(), afterDash[(cIdx + 1)..].Trim());
                        if (!kvRes.IsSuccess) return kvRes.Error!;
                    }
                    continue;
                }

                if (hasCurBox)
                {
                    int cIdx = trimmed.IndexOf(':');
                    if (cIdx <= 0)
                        return new Error("Markdown.InvalidRelabel", $"Expected 'key: value' inside ':::relabel' box at line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");
                    var kvRes = ApplyBoxKeyVal(trimmed[..cIdx].Trim(), trimmed[(cIdx + 1)..].Trim());
                    if (!kvRes.IsSuccess) return kvRes.Error!;
                    continue;
                }
            }

            if ((section is "include" or "distractors") && trimmed.StartsWith('-'))
            {
                var item = UnquoteYamlScalar(trimmed[1..]);
                if (!string.IsNullOrWhiteSpace(item))
                {
                    if (section == "include") (include ??= []).Add(item);
                    else distractors.Add(item);
                }
                continue;
            }

            var flushPrev = FlushCurrentBox();
            if (!flushPrev.IsSuccess) return flushPrev.Error!;
            section = null;

            int sep = trimmed.IndexOf(':');
            if (sep <= 0)
                return new Error("Markdown.InvalidRelabel", $"Invalid line '{trimmed}' in ':::relabel' at line {openLine} of '{sourceLabel}'. Use YAML keys 'prompt:', 'include:', 'distractors:', or 'boxes:'.", ErrorType.Validation, Path: $"markdown:{openLine}");

            var key = trimmed[..sep].Trim();
            var val = trimmed[(sep + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "prompt":
                    prompt = UnquoteYamlScalar(val);
                    break;
                case "include":
                    if (string.IsNullOrEmpty(val)) { include ??= []; section = "include"; }
                    else
                    {
                        var listRes = ParseInlineYamlList(val, "include", openLine, sourceLabel);
                        if (!listRes.IsSuccess) return listRes.Error!;
                        include = listRes.Value;
                    }
                    break;
                case "distractors":
                    if (string.IsNullOrEmpty(val)) { section = "distractors"; }
                    else
                    {
                        var listRes = ParseInlineYamlList(val, "distractors", openLine, sourceLabel);
                        if (!listRes.IsSuccess) return listRes.Error!;
                        distractors.AddRange(listRes.Value);
                    }
                    break;
                case "boxes":
                    if (!string.IsNullOrEmpty(val))
                        return new Error("Markdown.InvalidRelabel", $"'boxes:' in ':::relabel' at line {openLine} of '{sourceLabel}' must be followed by indented '- label: ...' items.", ErrorType.Validation, Path: $"markdown:{openLine}");
                    section = "boxes";
                    break;
                default:
                    return new Error("Markdown.InvalidRelabel", $"Unknown YAML key '{key}' in ':::relabel' at line {openLine} of '{sourceLabel}'. Allowed keys: prompt, include, distractors, boxes.", ErrorType.Validation, Path: $"markdown:{openLine}");
            }
        }

        var finalFlush = FlushCurrentBox();
        if (!finalFlush.IsSuccess) return finalFlush.Error!;

        if (masksPath is not null && boxes.Count > 0)
            return new Error("Markdown.InvalidRelabel", $"':::relabel' at line {openLine} of '{sourceLabel}' cannot specify both a 'masks'/'masksPath' sidecar and inline 'boxes'.", ErrorType.Validation, Path: $"markdown:{openLine}");
        if (masksPath is null && boxes.Count == 0)
            return new Error("Markdown.InvalidRelabel", $"':::relabel' at line {openLine} of '{sourceLabel}' must specify either a 'masks'/'masksPath' sidecar or at least 1 inline box under 'boxes:'.", ErrorType.Validation, Path: $"markdown:{openLine}");
        if (include is { Count: > 0 } && masksPath is null)
            return new Error("Markdown.InvalidRelabel", $"':::relabel' at line {openLine} of '{sourceLabel}' can only use 'include:' with a 'masks'/'masksPath' sidecar.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var expRes = ResolveExplanation(attrs, body, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;
        string? explanation = expRes.Value;

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        var payload = new RelabelImagePayload(
            imagePath,
            string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim(),
            mode,
            style,
            boxes,
            distractors,
            fuzzy,
            caseSensitive,
            explanation,
            SourceMasksPath: masksPath,
            IncludeLabels: include);

        return new JsonQuestionSpec(
            Type: "relabel",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: null,
            Explanation: explanation,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            PayloadJson: Phase1PayloadCodec.Serialize(payload));
    }

    private static Result<string?> ResolveExplanation(
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body,
        int openLine,
        string sourceLabel)
    {
        bool hasAttr = attrs.TryGetValue("explanation", out var rawAttr) && !string.IsNullOrWhiteSpace(rawAttr);
        bool hasSection = body.SubSections.TryGetValue("explanation", out var rawSec) && !string.IsNullOrWhiteSpace(rawSec);
        if (hasAttr && hasSection)
        {
            return new Error(
                "Markdown.DuplicateSubSection",
                $"Directive at line {openLine} of '{sourceLabel}' specifies both explanation=\"...\" attribute and ':::explanation' sub-section. Use only one.",
                ErrorType.Validation,
                Path: $"markdown:{openLine}");
        }
        return hasSection ? rawSec!.Trim() : (hasAttr ? rawAttr!.Trim() : (string?)null);
    }

    private static Result<DeclarativeArticleBlockSpec> ParseDiagramExploreDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        attrs.TryGetValue("title", out var title);
        attrs.TryGetValue("caption", out var caption);
        attrs.TryGetValue("alt", out var alt);

        var imgRes = TryResolvePairAttr(attrs, "image", "imagePath", ":::diagram-explore", required: true,
            $"':::diagram-explore' at line {openLine} of '{sourceLabel}' requires an 'image' (or 'imagePath') attribute.", openLine, sourceLabel);
        if (!imgRes.IsSuccess) return imgRes.Error!;
        var imagePath = imgRes.Value!;

        var masksRes = TryResolvePairAttr(attrs, "masks", "masksPath", ":::diagram-explore", required: false, null, openLine, sourceLabel);
        if (!masksRes.IsSuccess) return masksRes.Error!;
        string? masksPath = masksRes.Value;

        List<DiagramHotspot> hotspots;
        if (masksPath is not null)
        {
            if (body.MainBody.Replace("\r\n", "\n").Split('\n').Any(l => l.TrimStart().StartsWith("boxes:", StringComparison.OrdinalIgnoreCase)))
                return new Error("Markdown.InvalidDiagramExplore", $"':::diagram-explore' at line {openLine} of '{sourceLabel}' cannot combine 'masks' sidecar with inline 'boxes:'. Use '- Label: Annotation Markdown' bullets instead.", ErrorType.Validation, Path: $"markdown:{openLine}");

            var annRes = Phase3PayloadRules.ParseDiagramHotspotAnnotations(body.MainBody);
            if (!annRes.IsSuccess)
                return new Error("Markdown.InvalidDiagramExplore", $"Invalid ':::diagram-explore' at line {openLine} of '{sourceLabel}': {annRes.Error!.Description}", ErrorType.Validation, Path: $"markdown:{openLine}");

            hotspots = annRes.Value.Select((a, idx) => new DiagramHotspot(idx, a.Label, a.Note, 0, 0, 0, 0)).ToList();
        }
        else
        {
            var inlineRes = ParseInlineDiagramHotspotsYaml(body.MainBody, openLine, sourceLabel);
            if (!inlineRes.IsSuccess) return inlineRes.Error!;
            hotspots = inlineRes.Value;
        }

        var payload = new DiagramExplorePayload(
            imagePath,
            string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
            hotspots,
            masksPath);

        return new DeclarativeArticleBlockSpec(
            "diagram-explore",
            null,
            imagePath,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            Caption: string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
            Alt: string.IsNullOrWhiteSpace(alt) ? null : alt.Trim(),
            Title: string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            PayloadJson: Phase1PayloadCodec.Serialize(payload));
    }

    private static Result<List<DiagramHotspot>> ParseInlineDiagramHotspotsYaml(string rawBody, int openLine, string sourceLabel)
    {
        var lines = rawBody.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool inBoxes = false;
        var hotspots = new List<DiagramHotspot>();
        bool hasCur = false;
        string? curLabel = null, curNote = null;
        double? curX = null, curY = null, curW = null, curH = null;

        Result FlushCur()
        {
            if (!hasCur) return Result.Success();
            if (string.IsNullOrWhiteSpace(curLabel))
                return new Error("Markdown.InvalidDiagramExplore", $"':::diagram-explore' at line {openLine} of '{sourceLabel}' has a box missing 'label'.", ErrorType.Validation, Path: $"markdown:{openLine}");
            if (string.IsNullOrWhiteSpace(curNote))
                return new Error("Markdown.InvalidDiagramExplore", $"':::diagram-explore' at line {openLine} of '{sourceLabel}' box '{curLabel}' is missing 'note'.", ErrorType.Validation, Path: $"markdown:{openLine}");
            if (!curX.HasValue || !curY.HasValue || !curW.HasValue || !curH.HasValue)
                return new Error("Markdown.InvalidDiagramExplore", $"':::diagram-explore' at line {openLine} of '{sourceLabel}' box '{curLabel}' is missing 'rect: [x,y,w,h]' or x/y/w/h.", ErrorType.Validation, Path: $"markdown:{openLine}");
            if (!Phase3PayloadRules.HasValidHotspotBounds(curX.Value, curY.Value, curW.Value, curH.Value))
                return new Error("Markdown.InvalidDiagramExplore", $"':::diagram-explore' at line {openLine} of '{sourceLabel}' box '{curLabel}' has out-of-bounds coordinates.", ErrorType.Validation, Path: $"markdown:{openLine}");
            hotspots.Add(new DiagramHotspot(hotspots.Count, curLabel.Trim(), curNote.Trim(), curX.Value, curY.Value, curW.Value, curH.Value));
            hasCur = false; curLabel = null; curNote = null; curX = curY = curW = curH = null;
            return Result.Success();
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

            if (!inBoxes)
            {
                var col = trimmed.IndexOf(':');
                if (col <= 0)
                    return new Error("Markdown.InvalidDiagramExplore", $"':::diagram-explore' without 'masks=' requires YAML 'boxes:' at line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");
                var k = trimmed[..col].Trim().ToLowerInvariant();
                if (k != "boxes")
                    return new Error("Markdown.InvalidDiagramExplore", $"Unknown YAML key '{k}' in ':::diagram-explore' at line {openLine}. Use 'boxes:' when 'masks' is omitted.", ErrorType.Validation, Path: $"markdown:{openLine}");
                inBoxes = true;
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var fl = FlushCur();
                if (!fl.IsSuccess) return fl.Error!;
                hasCur = true;
                trimmed = trimmed[2..].Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
            }
            else if (!hasCur)
            {
                return new Error("Markdown.InvalidDiagramExplore", $"Expected '- label: ...' under 'boxes:' in ':::diagram-explore' at line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");
            }

            var cIdx = trimmed.IndexOf(':');
            if (cIdx <= 0)
                return new Error("Markdown.InvalidDiagramExplore", $"Invalid box entry line '{trimmed}' in ':::diagram-explore' at line {openLine}.", ErrorType.Validation, Path: $"markdown:{openLine}");

            var bKey = trimmed[..cIdx].Trim().ToLowerInvariant();
            var bVal = trimmed[(cIdx + 1)..].Trim();
            switch (bKey)
            {
                case "label":
                    curLabel = UnquoteYamlScalar(bVal);
                    break;
                case "note":
                    curNote = UnquoteYamlScalar(bVal);
                    break;
                case "rect":
                    var rectRes = ParseYamlRect(bVal, "Markdown.InvalidDiagramExplore", ":::diagram-explore", openLine, sourceLabel);
                    if (!rectRes.IsSuccess) return rectRes.Error!;
                    (curX, curY, curW, curH) = rectRes.Value;
                    break;
                case "x":
                    if (!double.TryParse(bVal, NumberStyles.Float, CultureInfo.InvariantCulture, out var vx))
                        return new Error("Markdown.InvalidDiagramExplore", $"Invalid 'x' '{bVal}' in ':::diagram-explore' at line {openLine}.", ErrorType.Validation);
                    curX = vx;
                    break;
                case "y":
                    if (!double.TryParse(bVal, NumberStyles.Float, CultureInfo.InvariantCulture, out var vy))
                        return new Error("Markdown.InvalidDiagramExplore", $"Invalid 'y' '{bVal}' in ':::diagram-explore' at line {openLine}.", ErrorType.Validation);
                    curY = vy;
                    break;
                case "w":
                    if (!double.TryParse(bVal, NumberStyles.Float, CultureInfo.InvariantCulture, out var vw))
                        return new Error("Markdown.InvalidDiagramExplore", $"Invalid 'w' '{bVal}' in ':::diagram-explore' at line {openLine}.", ErrorType.Validation);
                    curW = vw;
                    break;
                case "h":
                    if (!double.TryParse(bVal, NumberStyles.Float, CultureInfo.InvariantCulture, out var vh))
                        return new Error("Markdown.InvalidDiagramExplore", $"Invalid 'h' '{bVal}' in ':::diagram-explore' at line {openLine}.", ErrorType.Validation);
                    curH = vh;
                    break;
                default:
                    return new Error("Markdown.InvalidDiagramExplore", $"Unknown box key '{bKey}' in ':::diagram-explore' at line {openLine}. Allowed: label, note, rect, x, y, w, h.", ErrorType.Validation, Path: $"markdown:{openLine}");
            }
        }

        var endFlush = FlushCur();
        if (!endFlush.IsSuccess) return endFlush.Error!;
        if (hotspots.Count == 0)
            return new Error("Markdown.InvalidDiagramExplore", $"':::diagram-explore' at line {openLine} of '{sourceLabel}' must define at least 1 hotspot.", ErrorType.Validation, Path: $"markdown:{openLine}");
        return hotspots;
    }

    private static Result<DeclarativeArticleBlockSpec> ParseRevealDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        attrs.TryGetValue("title", out var title);
        var sfRes = TryParseBoolAttr(attrs, "showFirst", false, ":::reveal", openLine, sourceLabel);
        if (!sfRes.IsSuccess) return sfRes.Error!;
        bool showFirst = sfRes.Value;

        var stepsRes = Phase3PayloadRules.ParseRevealSteps(body.MainBody);
        if (!stepsRes.IsSuccess)
            return new Error("Markdown.InvalidReveal", $"Invalid ':::reveal' at line {openLine} of '{sourceLabel}': {stepsRes.Error!.Description}", ErrorType.Validation, Path: $"markdown:{openLine}");

        var payload = new RevealBlockPayload(
            string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            showFirst,
            stepsRes.Value);

        return new DeclarativeArticleBlockSpec(
            "reveal",
            null,
            null,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            Title: string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            PayloadJson: Phase1PayloadCodec.Serialize(payload));
    }

    private static Result<JsonQuestionSpec> ParseClassifyDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        attrs.TryGetValue("title", out var title);

        var expRes = ResolveExplanation(attrs, body, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;

        var parsed = Phase3PayloadRules.ParseClassifyLines(body.MainBody, title, expRes.Value);
        if (!parsed.IsSuccess)
            return new Error("Markdown.InvalidClassify", $"Invalid ':::classify' at line {openLine} of '{sourceLabel}': {parsed.Error!.Description}", ErrorType.Validation, Path: $"markdown:{openLine}");

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        return new JsonQuestionSpec(
            Type: "classify",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: null,
            Explanation: expRes.Value,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            PayloadJson: Phase1PayloadCodec.Serialize(parsed.Value));
    }

    private static Result<JsonQuestionSpec> ParseMatchDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        attrs.TryGetValue("title", out var title);

        var expRes = ResolveExplanation(attrs, body, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;

        var parsed = Phase3PayloadRules.ParseMatchLines(body.MainBody, title, expRes.Value);
        if (!parsed.IsSuccess)
            return new Error("Markdown.InvalidMatch", $"Invalid ':::match' at line {openLine} of '{sourceLabel}': {parsed.Error!.Description}", ErrorType.Validation, Path: $"markdown:{openLine}");

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        return new JsonQuestionSpec(
            Type: "match",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: null,
            Explanation: expRes.Value,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            PayloadJson: Phase1PayloadCodec.Serialize(parsed.Value));
    }

    private static Result<JsonQuestionSpec> ParseNumericDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedDirectiveBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        attrs.TryGetValue("unit", out var unit);
        attrs.TryGetValue("placeholder", out var placeholder);
        attrs.TryGetValue("toleranceMode", out var tolModeAttr);

        if (!attrs.TryGetValue("answer", out var rawAns) || string.IsNullOrWhiteSpace(rawAns))
            return new Error("Markdown.InvalidDirectiveAttr", $"':::numeric' at line {openLine} of '{sourceLabel}' requires an 'answer' attribute.", ErrorType.Validation, Path: $"markdown:{openLine}");
        if (!double.TryParse(rawAns.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ansVal) || !double.IsFinite(ansVal))
            return new Error("Markdown.InvalidDirectiveAttr", $"Invalid numeric 'answer' '{rawAns}' on ':::numeric' at line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");

        attrs.TryGetValue("tolerance", out var rawTol);
        if (!Phase3PayloadRules.TryParseToleranceSpec(rawTol, tolModeAttr, out var tolVal, out var tolMode))
            return new Error("Markdown.InvalidDirectiveAttr", $"Invalid 'tolerance'/'toleranceMode' on ':::numeric' at line {openLine} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var imgAttrRes = TryResolvePairAttr(attrs, "image", "imagePath", ":::numeric", required: false, null, openLine, sourceLabel);
        if (!imgAttrRes.IsSuccess) return imgAttrRes.Error!;

        var extractRes = ExtractSingleStandaloneImage(
            body.MainBody, openLine, sourceLabel, "Markdown.InvalidNumeric",
            $"':::numeric' at line {openLine} of '{sourceLabel}' specifies multiple images.",
            imgAttrRes.Value, normalizeNewlines: true);
        if (!extractRes.IsSuccess) return extractRes.Error!;
        var (prompt, imagePath, _) = extractRes.Value;

        var expRes = ResolveExplanation(attrs, body, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;

        var layoutRes = TryResolveLayoutOverrides(body, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        var payload = new NumericPayload(
            prompt,
            ansVal,
            tolVal,
            tolMode,
            string.IsNullOrWhiteSpace(unit) ? null : unit.Trim(),
            string.IsNullOrWhiteSpace(placeholder) ? null : placeholder.Trim(),
            expRes.Value,
            imagePath);

        return new JsonQuestionSpec(
            Type: "numeric",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: null,
            Explanation: expRes.Value,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            PayloadJson: Phase1PayloadCodec.Serialize(payload));
    }

    private sealed record ParsedCaseBody(
        string StemRaw,
        List<string> ChildDirectiveDocs,
        Dictionary<string, string> SubSections);

    private static Result<ParsedCaseBody> ReadCaseDirectiveBody(
        string[] lines,
        ref int lineIdx,
        int openLine,
        string sourceLabel)
    {
        var stemBuffer = new StringBuilder();
        var subSections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var childDocs = new List<string>();
        var currentSectionBuffer = stemBuffer;
        string? activeSection = null;
        StringBuilder? activeChildBuffer = null;
        string? innerCodeFence = null;
        bool closed = false;

        for (lineIdx = lineIdx + 1; lineIdx < lines.Length; lineIdx++)
        {
            var innerLine = lines[lineIdx];
            var innerTrim = innerLine.Trim();

            if (innerCodeFence is null && TryOpenCodeFence(innerTrim, out var innerToken))
            {
                innerCodeFence = innerToken;
                (activeChildBuffer ?? currentSectionBuffer).AppendLine(innerLine);
                continue;
            }
            if (innerCodeFence is not null)
            {
                (activeChildBuffer ?? currentSectionBuffer).AppendLine(innerLine);
                if (IsCloseCodeFence(innerTrim, innerCodeFence))
                    innerCodeFence = null;
                continue;
            }

            if (activeChildBuffer is not null)
            {
                if (innerTrim == "::::")
                {
                    activeChildBuffer.AppendLine(":::");
                    childDocs.Add(activeChildBuffer.ToString());
                    activeChildBuffer = null;
                    continue;
                }
                if (innerTrim.StartsWith("::::", StringComparison.Ordinal))
                {
                    var subM = QuadSubSectionRegex().Match(innerTrim);
                    if (subM.Success)
                    {
                        activeChildBuffer.AppendLine(":::" + subM.Groups["section"].Value);
                        continue;
                    }
                    return new Error("Markdown.UnclosedDirective", $"Nested '::::' fence inside ':::case' sub-question at line {lineIdx + 1} of '{sourceLabel}' is not supported.", ErrorType.Validation, Path: $"markdown:{lineIdx + 1}");
                }
                if (innerTrim == ":::")
                {
                    return new Error("Markdown.UnclosedDirective", $"Unclosed child '::::' block inside ':::case' starting at line {openLine} before closing ':::' at line {lineIdx + 1}.", ErrorType.Validation, Path: $"markdown:{openLine}");
                }
                activeChildBuffer.AppendLine(innerLine);
                continue;
            }

            if (innerTrim == ":::")
            {
                closed = true;
                break;
            }

            if (innerTrim.StartsWith("::::", StringComparison.Ordinal))
            {
                if (activeSection is not null)
                    return new Error("Markdown.InvalidCase", $"Child directive '{innerTrim}' at line {lineIdx + 1} cannot appear inside ':::{activeSection}' of ':::case'. Put all child directives before ':::explanation' or ':::layout'.", ErrorType.Validation, Path: $"markdown:{lineIdx + 1}");

                var quadOpen = QuadDirectiveOpenRegex().Match(innerTrim);
                if (!quadOpen.Success)
                    return new Error("Markdown.InvalidDirectiveAttr", $"Malformed quad-colon directive '{innerTrim}' inside ':::case' at line {lineIdx + 1} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{lineIdx + 1}");

                activeChildBuffer = new StringBuilder();
                activeChildBuffer.AppendLine(":::" + quadOpen.Groups["name"].Value + quadOpen.Groups["rest"].Value);
                continue;
            }

            if (innerTrim.StartsWith(":::", StringComparison.Ordinal))
            {
                var subMatch = DirectiveSubSectionRegex().Match(innerTrim);
                if (subMatch.Success)
                {
                    var sectionName = subMatch.Groups["section"].Value.Trim().ToLowerInvariant();
                    if (!AllowedExplanationLayoutSubSections.Contains(sectionName))
                    {
                        return new Error(
                            "Markdown.UnknownSubSection",
                            $"Unknown sub-section ':::{sectionName}' inside ':::case' at line {lineIdx + 1} of '{sourceLabel}'. Allowed sub-sections: ':::explanation', ':::layout'.",
                            ErrorType.Validation,
                            Path: $"markdown:{lineIdx + 1}");
                    }
                    if (string.Equals(activeSection, sectionName, StringComparison.OrdinalIgnoreCase) || subSections.ContainsKey(sectionName))
                    {
                        return new Error("Markdown.DuplicateSubSection", $"Duplicate sub-section ':::{sectionName}' inside ':::case' at line {lineIdx + 1} of '{sourceLabel}'.", ErrorType.Validation, Path: $"markdown:{lineIdx + 1}");
                    }
                    if (activeSection is not null)
                        subSections[activeSection] = currentSectionBuffer.ToString().Trim();
                    activeSection = sectionName;
                    currentSectionBuffer = new StringBuilder();
                    continue;
                }

                return new Error("Markdown.UnclosedDirective", $"Unclosed ':::case' starting at line {openLine} before new fence '{innerTrim}' at line {lineIdx + 1}. Use quad-colon '::::<type>' for nested sub-questions.", ErrorType.Validation, Path: $"markdown:{openLine}");
            }

            currentSectionBuffer.AppendLine(innerLine);
        }

        if (!closed)
            return new Error("Markdown.UnclosedDirective", $"Unclosed ':::case' starting at line {openLine} of '{sourceLabel}'. Add a closing ':::' line.", ErrorType.Validation, Path: $"markdown:{openLine}");
        if (activeSection is not null)
            subSections[activeSection] = currentSectionBuffer.ToString().Trim();

        return new ParsedCaseBody(stemBuffer.ToString(), childDocs, subSections);
    }

    private static Result<JsonQuestionSpec> ParseCaseDirective(
        int openLine,
        string sourceLabel,
        Dictionary<string, string> attrs,
        ParsedCaseBody body)
    {
        attrs.TryGetValue("ref", out var refKey);
        attrs.TryGetValue("title", out var title);
        attrs.TryGetValue("caption", out var captionAttr);

        var imgAttrRes = TryResolvePairAttr(attrs, "image", "imagePath", ":::case", required: false, null, openLine, sourceLabel);
        if (!imgAttrRes.IsSuccess) return imgAttrRes.Error!;

        var extractRes = ExtractSingleStandaloneImage(
            body.StemRaw, openLine, sourceLabel, "Markdown.InvalidCase",
            $"':::case' at line {openLine} of '{sourceLabel}' specifies multiple stem images.",
            imgAttrRes.Value, normalizeNewlines: true);
        if (!extractRes.IsSuccess) return extractRes.Error!;
        var (stem, stemImagePath, extractedCaption) = extractRes.Value;
        string? stemCaption = string.IsNullOrWhiteSpace(captionAttr) ? extractedCaption : captionAttr.Trim();

        var subQuestionSpecs = new List<JsonQuestionSpec>();
        for (int idx = 0; idx < body.ChildDirectiveDocs.Count; idx++)
        {
            var childDoc = body.ChildDirectiveDocs[idx];
            var childLines = childDoc.Replace("\r\n", "\n").Split('\n');
            var childRes = ParseBody(childLines, 0, $"{sourceLabel}:case:{idx + 1}", "quiz");
            if (!childRes.IsSuccess) return childRes.Error!;
            foreach (var childQ in childRes.Value.Questions)
            {
                if (string.Equals(childQ.Type?.Trim(), "case", StringComparison.OrdinalIgnoreCase))
                    return new Error("Case.NestedCaseNotAllowed", $"Nested '::::case' inside ':::case' at line {openLine} of '{sourceLabel}' is not allowed.", ErrorType.Validation, Path: $"markdown:{openLine}");
                subQuestionSpecs.Add(childQ);
            }
        }

        if (subQuestionSpecs.Count == 0)
            return new Error("Markdown.InvalidCase", $"':::case' at line {openLine} of '{sourceLabel}' must contain at least one '::::<type>' sub-question.", ErrorType.Validation, Path: $"markdown:{openLine}");

        var expBody = new ParsedDirectiveBody(string.Empty, body.SubSections);
        var expRes = ResolveExplanation(attrs, expBody, openLine, sourceLabel);
        if (!expRes.IsSuccess) return expRes.Error!;

        var layoutRes = TryResolveLayoutOverrides(expBody, openLine, sourceLabel);
        if (!layoutRes.IsSuccess) return layoutRes.Error!;
        var layoutOverrides = layoutRes.Value;

        var payload = new CaseGroupPayload(
            string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            stem,
            stemImagePath,
            stemCaption,
            [],
            expRes.Value);

        return new JsonQuestionSpec(
            Type: "case",
            Text: null,
            MaxOptions: null,
            IsMultiCorrect: false,
            Options: [],
            Front: null,
            Back: null,
            LayoutOverrides: layoutOverrides,
            ImagePath: null,
            Explanation: expRes.Value,
            Ref: string.IsNullOrWhiteSpace(refKey) ? null : refKey,
            PayloadJson: Phase1PayloadCodec.Serialize(payload),
            SubQuestions: subQuestionSpecs);
    }

    private static string ResolveQuizType(string? frontmatterKind, string? frontmatterQuizType)
    {
        if (!string.IsNullOrWhiteSpace(frontmatterQuizType))
            return frontmatterQuizType.Trim().ToLowerInvariant();
        var k = frontmatterKind?.Trim().ToLowerInvariant();
        if (k is "mcq" or "flashcard" or "mixed")
            return k;
        return "mixed";
    }

    private static bool TryOpenCodeFence(string trimmed, out string fenceToken) =>
        MarkdownFenceHelper.TryOpenCodeFence(trimmed, out fenceToken);

    private static bool IsCloseCodeFence(string trimmed, string openingToken) =>
        MarkdownFenceHelper.IsCloseCodeFence(trimmed, openingToken);

    private static string UnescapeQuotedValue(string raw, char quoteChar) =>
        quoteChar switch
        {
            '"' => raw.Replace("\\\"", "\"", StringComparison.Ordinal),
            '\'' => raw.Replace("\\'", "'", StringComparison.Ordinal),
            _ => raw
        };

    private static string UnquoteYamlScalar(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            return UnescapeQuotedValue(t[1..^1], '"').Trim();
        if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'')
            return UnescapeQuotedValue(t[1..^1], '\'').Trim();
        return t;
    }

    private static bool TryParseStandaloneImage(string trimmedLine, out string alt, out string path, out string? caption)
    {
        alt = string.Empty;
        path = string.Empty;
        caption = null;

        if (!trimmedLine.StartsWith("![", StringComparison.Ordinal) || !trimmedLine.EndsWith(')'))
            return false;

        int closeBracket = -1;
        for (int i = 2; i < trimmedLine.Length; i++)
        {
            if (trimmedLine[i] == ']' && !Phase1PayloadRules.IsEscaped(trimmedLine, i))
            {
                closeBracket = i;
                break;
            }
        }
        if (closeBracket < 0 || closeBracket + 1 >= trimmedLine.Length || trimmedLine[closeBracket + 1] != '(')
            return false;

        int parenDepth = 1;
        char activeQuote = '\0';
        bool inAngle = false;
        int closeParen = -1;
        for (int i = closeBracket + 2; i < trimmedLine.Length; i++)
        {
            char ch = trimmedLine[i];
            if (inAngle)
            {
                if (ch == '>' && !Phase1PayloadRules.IsEscaped(trimmedLine, i))
                    inAngle = false;
                continue;
            }
            if (activeQuote != '\0')
            {
                if (ch == activeQuote && !Phase1PayloadRules.IsEscaped(trimmedLine, i))
                    activeQuote = '\0';
                continue;
            }
            if (ch == '<' && i == closeBracket + 2)
            {
                inAngle = true;
                continue;
            }
            if (ch is '"' or '\'' && i > closeBracket + 2 && char.IsWhiteSpace(trimmedLine[i - 1]))
            {
                activeQuote = ch;
                continue;
            }
            if (ch == '(' && !Phase1PayloadRules.IsEscaped(trimmedLine, i))
            {
                parenDepth++;
                continue;
            }
            if (ch == ')' && !Phase1PayloadRules.IsEscaped(trimmedLine, i))
            {
                parenDepth--;
                if (parenDepth == 0)
                {
                    closeParen = i;
                    break;
                }
            }
        }

        if (closeParen != trimmedLine.Length - 1)
            return false;

        alt = trimmedLine[2..closeBracket].Trim();
        var inner = trimmedLine[(closeBracket + 2)..closeParen].Trim();
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
                if (c == activeQuote && !Phase1PayloadRules.IsEscaped(input, i))
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
