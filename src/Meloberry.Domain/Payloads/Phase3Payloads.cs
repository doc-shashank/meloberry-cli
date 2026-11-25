using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Sessions;

namespace Meloberry.Domain.Payloads;

public sealed record DiagramHotspot(
    int Index,
    string Label,
    string Note,
    double X,
    double Y,
    double W,
    double H);

public sealed record DiagramExplorePayload(
    string ImagePath,
    string? Title,
    string? Caption,
    IReadOnlyList<DiagramHotspot> Hotspots,
    string? SourceMasksPath = null);

public sealed record DiagramHotspotAnnotation(
    string Label,
    string Note);

public sealed record RevealStep(
    int Index,
    string Heading,
    string Body);

public sealed record RevealBlockPayload(
    string? Title,
    bool ShowFirst,
    IReadOnlyList<RevealStep> Steps);

public sealed record ClassifyBucket(
    int BucketId,
    string Name);

public sealed record ClassifyItem(
    int ItemId,
    int BucketId,
    string Text);

public sealed record ClassifyPayload(
    string? Title,
    IReadOnlyList<ClassifyBucket> Buckets,
    IReadOnlyList<ClassifyItem> Items,
    string? Explanation);

public sealed record MatchPair(
    int PairId,
    string Left,
    string Right);

public sealed record MatchPayload(
    string? Title,
    IReadOnlyList<MatchPair> Pairs,
    IReadOnlyList<string> Distractors,
    string? Explanation);

public sealed record NumericPayload(
    string Prompt,
    double Answer,
    double Tolerance,
    string ToleranceMode,
    string? Unit,
    string? Placeholder,
    string? Explanation,
    string? ImagePath = null);

public sealed record CaseSubMcqOption(
    int Index,
    string? Text,
    string? ImagePath,
    bool IsCorrect);

public sealed record CaseSubMcqData(
    string Text,
    int MaxOptions,
    bool IsMultiCorrect,
    IReadOnlyList<CaseSubMcqOption> Options,
    string? ImagePath = null,
    string? Explanation = null);

public sealed record CaseSubFlashcardSide(
    string? Text,
    string? ImagePath,
    string Position);

public sealed record CaseSubFlashcardData(
    CaseSubFlashcardSide Front,
    CaseSubFlashcardSide Back);

public sealed record CaseSubQuestion(
    int Index,
    string Type,
    CaseSubMcqData? Mcq = null,
    CaseSubFlashcardData? Flashcard = null,
    string? PayloadJson = null);

public sealed record CaseGroupPayload(
    string? Title,
    string Stem,
    string? ImagePath,
    string? Caption,
    IReadOnlyList<CaseSubQuestion> Questions,
    string? Explanation = null);

public static partial class Phase3PayloadRules
{
    public const int MaxHotspots = 20;
    public const int MaxRevealSteps = 20;
    public const int MaxClassifyBuckets = 4;
    public const int MaxClassifyItems = 24;
    public const int MaxMatchPairs = 12;
    public const int MaxMatchDistractors = 6;
    public const int MaxCaseQuestions = 10;

    [GeneratedRegex(@"^\s*[-*]\s+(.+)$")]
    private static partial Regex BulletLineRegex();

    [GeneratedRegex(@"^\s*!\s*(.+)$")]
    private static partial Regex DistractorLineRegex();

    public static bool HasValidHotspotBounds(DiagramHotspot h) =>
        Phase2PayloadRules.HasValidNormalizedBox(h.X, h.Y, h.W, h.H);

    public static bool HasValidHotspotBounds(double x, double y, double w, double h) =>
        Phase2PayloadRules.HasValidNormalizedBox(x, y, w, h);

    public static Result<IReadOnlyList<DiagramHotspotAnnotation>> ParseDiagramHotspotAnnotations(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            return new Error("DiagramExplore.EmptyHotspots", "Diagram Explorer hotspot list cannot be empty.", ErrorType.Validation);

        var lines = rawBody.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var list = new List<DiagramHotspotAnnotation>();
        string? curLabel = null;
        var curNote = new StringBuilder();
        string? activeFence = null;

        void FlushCurrent()
        {
            if (curLabel is null) return;
            list.Add(new DiagramHotspotAnnotation(curLabel.Trim(), curNote.ToString().Trim()));
            curLabel = null;
            curNote.Clear();
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (TryUpdateFenceState(trimmed, ref activeFence))
            {
                if (curLabel is null)
                    return new Error("DiagramExplore.InvalidAnnotationLine", "Code fence cannot appear before the first '- Label: Annotation' bullet.", ErrorType.Validation);
                curNote.Append('\n').Append(rawLine.TrimEnd());
                continue;
            }

            if (activeFence is null && rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]))
            {
                var bullet = BulletLineRegex().Match(rawLine);
                if (bullet.Success)
                {
                    FlushCurrent();
                    var entryText = bullet.Groups[1].Value;
                    int colonIdx = FindFirstUnquotedColon(entryText);
                    if (colonIdx <= 0)
                        return new Error("DiagramExplore.InvalidAnnotationLine", $"Hotspot bullet '{rawLine.Trim()}' must follow '- Label: Annotation Markdown'.", ErrorType.Validation);

                    var label = entryText[..colonIdx].Trim();
                    var notePart = entryText[(colonIdx + 1)..].Trim();
                    if (string.IsNullOrWhiteSpace(label))
                        return new Error("DiagramExplore.EmptyHotspotLabel", "Hotspot annotation label cannot be empty.", ErrorType.Validation);

                    curLabel = label;
                    curNote.Append(notePart);
                    continue;
                }
            }

            if (activeFence is null && string.IsNullOrWhiteSpace(rawLine))
            {
                if (curLabel is not null && curNote.Length > 0)
                    curNote.Append('\n');
                continue;
            }

            if (curLabel is not null)
            {
                if (curNote.Length > 0) curNote.Append('\n');
                curNote.Append(rawLine.Trim());
                continue;
            }

            return new Error("DiagramExplore.InvalidAnnotationLine", $"Unexpected line '{trimmed}' in :::diagram-explore annotations. Use '- Label: Annotation Markdown'.", ErrorType.Validation);
        }

        FlushCurrent();
        if (list.Count == 0)
            return new Error("DiagramExplore.EmptyHotspots", "Diagram Explorer requires at least one annotated hotspot.", ErrorType.Validation);

        foreach (var item in list)
        {
            if (string.IsNullOrWhiteSpace(item.Note))
                return new Error("DiagramExplore.EmptyHotspotNote", $"Hotspot '{item.Label}' must have a non-empty annotation note.", ErrorType.Validation);
        }

        return list;
    }

    public static Result<IReadOnlyList<DiagramHotspot>> ResolveHotspotsFromSidecar(
        IReadOnlyList<RelabelBox> sidecarBoxes,
        IReadOnlyList<DiagramHotspotAnnotation> annotations)
    {
        if (sidecarBoxes.Count == 0)
            return new Error("DiagramExplore.EmptySidecarBoxes", "Masks sidecar does not contain any bounding boxes.", ErrorType.Validation);
        if (annotations.Count == 0)
            return new Error("DiagramExplore.EmptyHotspots", "Diagram Explorer requires at least one annotated hotspot.", ErrorType.Validation);

        var hotspots = new List<DiagramHotspot>(annotations.Count);
        for (int i = 0; i < annotations.Count; i++)
        {
            var ann = annotations[i];
            var target = ann.Label.Trim();
            var matched = sidecarBoxes.FirstOrDefault(b => string.Equals(b.Label, target, StringComparison.OrdinalIgnoreCase))
                          ?? sidecarBoxes.FirstOrDefault(b =>
                              b.Label.Contains(target, StringComparison.OrdinalIgnoreCase)
                              || target.Contains(b.Label, StringComparison.OrdinalIgnoreCase));

            if (matched is null)
            {
                var available = string.Join(", ", sidecarBoxes.Select(b => $"'{b.Label}'"));
                return new Error(
                    "DiagramExplore.UnmatchedHotspot",
                    $"Hotspot label '{ann.Label}' did not match any box in masks sidecar. Available labels: {available}.",
                    ErrorType.Validation);
            }

            hotspots.Add(new DiagramHotspot(i, matched.Label, ann.Note, matched.X, matched.Y, matched.W, matched.H));
        }

        return hotspots;
    }

    public static Result<IReadOnlyList<RevealStep>> ParseRevealSteps(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            return new Error("Reveal.EmptySteps", ":::reveal block must contain at least one '> Step Heading' card.", ErrorType.Validation);

        var lines = rawBody.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var steps = new List<RevealStep>();
        string? curHeading = null;
        var curBody = new StringBuilder();
        string? activeFence = null;

        void FlushStep()
        {
            if (curHeading is null) return;
            steps.Add(new RevealStep(steps.Count, curHeading.Trim(), curBody.ToString().Trim()));
            curHeading = null;
            curBody.Clear();
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (TryUpdateFenceState(trimmed, ref activeFence))
            {
                if (curHeading is null)
                    return new Error("Reveal.MissingHeading", "Text or code fences inside :::reveal must appear under a '> Step Heading' line.", ErrorType.Validation);
                if (curBody.Length > 0) curBody.Append('\n');
                curBody.Append(rawLine.TrimEnd());
                continue;
            }

            if (activeFence is null && rawLine.TrimStart().StartsWith('>'))
            {
                FlushStep();
                var heading = rawLine.TrimStart()[1..].Trim();
                if (string.IsNullOrWhiteSpace(heading))
                    return new Error("Reveal.EmptyHeading", "Each '> Step Heading' in :::reveal must have a non-empty heading.", ErrorType.Validation);
                curHeading = heading;
                continue;
            }

            if (curHeading is null)
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                return new Error("Reveal.MissingHeading", $"Line '{trimmed}' appears before any '> Step Heading' in :::reveal.", ErrorType.Validation);
            }

            if (curBody.Length > 0) curBody.Append('\n');
            curBody.Append(rawLine.TrimEnd());
        }

        FlushStep();
        if (steps.Count == 0)
            return new Error("Reveal.EmptySteps", ":::reveal block must contain at least one '> Step Heading' card.", ErrorType.Validation);

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Body))
                return new Error("Reveal.EmptyStepBody", $"Step '{step.Heading}' inside :::reveal must have non-empty body content.", ErrorType.Validation);
        }

        return steps;
    }

    public static Result<ClassifyPayload> ParseClassifyLines(string? rawBody, string? title = null, string? explanation = null)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            return new Error("Classify.EmptyBody", ":::classify requires '[Bucket]' headers and '- Item' bullets.", ErrorType.Validation);

        var lines = rawBody.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var buckets = new List<ClassifyBucket>();
        var items = new List<ClassifyItem>();
        int currentBucketId = -1;
        string? curItemText = null;
        int curItemBucketId = -1;
        string? activeFence = null;

        void FlushItem()
        {
            if (curItemText is null) return;
            items.Add(new ClassifyItem(items.Count, curItemBucketId, curItemText.Trim()));
            curItemText = null;
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (TryUpdateFenceState(trimmed, ref activeFence))
            {
                if (curItemText is null)
                    return new Error("Classify.InvalidLine", "Code blocks cannot appear outside a '- Item' inside :::classify.", ErrorType.Validation);
                curItemText += "\n" + rawLine.TrimEnd();
                continue;
            }

            if (activeFence is null && trimmed.StartsWith('[') && trimmed.EndsWith(']') && !trimmed.Contains("]("))
            {
                FlushItem();
                var bucketName = trimmed[1..^1].Trim();
                if (string.IsNullOrWhiteSpace(bucketName))
                    return new Error("Classify.EmptyBucketName", "Bucket header '[]' cannot be empty.", ErrorType.Validation);

                var existing = buckets.FirstOrDefault(b => string.Equals(b.Name, bucketName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    currentBucketId = existing.BucketId;
                }
                else
                {
                    currentBucketId = buckets.Count;
                    buckets.Add(new ClassifyBucket(currentBucketId, bucketName));
                }
                continue;
            }

            if (activeFence is null && rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]))
            {
                var m = BulletLineRegex().Match(rawLine);
                if (m.Success)
                {
                    if (currentBucketId < 0)
                        return new Error("Classify.ItemBeforeBucket", $"Item '{m.Groups[1].Value.Trim()}' appears before any '[Bucket]' header.", ErrorType.Validation);

                    FlushItem();
                    var text = m.Groups[1].Value.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                        return new Error("Classify.EmptyItem", "Classification bullet item cannot be empty.", ErrorType.Validation);
                    curItemText = text;
                    curItemBucketId = currentBucketId;
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (activeFence is not null && curItemText is not null)
                    curItemText += "\n";
                continue;
            }

            if (curItemText is not null && rawLine.Length > 0 && char.IsWhiteSpace(rawLine[0]))
            {
                curItemText += "\n" + trimmed;
                continue;
            }

            return new Error("Classify.InvalidLine", $"Unexpected line '{trimmed}' in :::classify. Expected '[BucketName]' or '- Item text'.", ErrorType.Validation);
        }

        FlushItem();
        if (buckets.Count == 0 || items.Count == 0)
            return new Error("Classify.InsufficientContent", ":::classify requires '[Bucket]' headers and at least one '- Item'.", ErrorType.Validation);

        return new ClassifyPayload(
            string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            buckets,
            items,
            string.IsNullOrWhiteSpace(explanation) ? null : explanation.Trim());
    }

    public static Result<MatchPayload> ParseMatchLines(string? rawBody, string? title = null, string? explanation = null)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            return new Error("Match.EmptyBody", ":::match requires 'Left :: Right' pair lines.", ErrorType.Validation);

        var lines = rawBody.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var pairs = new List<MatchPair>();
        var distractors = new List<string>();

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var distractorMatch = DistractorLineRegex().Match(trimmed);
            if (distractorMatch.Success)
            {
                var decoy = distractorMatch.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(decoy))
                    return new Error("Match.EmptyDistractor", "Distractor line '!' cannot be empty in :::match.", ErrorType.Validation);
                distractors.Add(decoy);
                continue;
            }

            var contentLine = trimmed;
            var bulletMatch = BulletLineRegex().Match(rawLine);
            if (bulletMatch.Success)
                contentLine = bulletMatch.Groups[1].Value.Trim();

            int sepIdx = FindDoubleColonSeparator(contentLine);
            if (sepIdx <= 0)
                return new Error("Match.InvalidLine", $"Line '{trimmed}' must contain 'Left :: Right' or start with '! Distractor'.", ErrorType.Validation);

            var left = contentLine[..sepIdx].Trim();
            var right = contentLine[(sepIdx + 2)..].Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return new Error("Match.EmptySide", $"Both Left and Right sides of '{trimmed}' must be non-empty.", ErrorType.Validation);

            pairs.Add(new MatchPair(pairs.Count, left, right));
        }

        if (pairs.Count == 0)
            return new Error("Match.EmptyPairs", ":::match must contain at least one 'Left :: Right' pair.", ErrorType.Validation);

        return new MatchPayload(
            string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            pairs,
            distractors,
            string.IsNullOrWhiteSpace(explanation) ? null : explanation.Trim());
    }

    public static bool TryParseToleranceSpec(string? rawTolerance, string? explicitMode, out double value, out string mode)
    {
        mode = NormalizeToleranceMode(explicitMode);
        if (string.IsNullOrWhiteSpace(rawTolerance))
        {
            value = 0;
            return explicitMode is null || IsValidToleranceMode(explicitMode);
        }

        var trimmed = rawTolerance.Trim();
        if (trimmed.EndsWith('%'))
        {
            mode = "percent";
            trimmed = trimmed[..^1].Trim();
        }
        else if (explicitMode is not null && !IsValidToleranceMode(explicitMode))
        {
            value = 0;
            return false;
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        return double.IsFinite(value) && value >= 0;
    }

    public static bool IsValidToleranceMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode)
        || mode.Trim().ToLowerInvariant() is "absolute" or "abs" or "percent" or "pct" or "relative" or "%";

    public static string NormalizeToleranceMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() is "percent" or "pct" or "relative" or "%"
            ? "percent"
            : "absolute";

    public static IEnumerable<string> EnumerateArticleBlockMarkdownStrings(SessionComponent comp)
    {
        if (comp.Kind != ComponentKind.Article) yield break;

        foreach (var b in comp.ArticleBlocks)
        {
            switch (b.Kind)
            {
                case ArticleBlockKind.Text:
                    if (!string.IsNullOrWhiteSpace(b.TextContent))
                        yield return b.TextContent;
                    break;
                case ArticleBlockKind.Image:
                    if (!string.IsNullOrWhiteSpace(b.Caption))
                        yield return b.Caption;
                    break;
                case ArticleBlockKind.Callout:
                    if (!string.IsNullOrWhiteSpace(b.CalloutTitle))
                        yield return b.CalloutTitle;
                    if (!string.IsNullOrWhiteSpace(b.TextContent))
                        yield return b.TextContent;
                    break;
                case ArticleBlockKind.Table:
                    if (Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(b.PayloadJson, out var tbl) && tbl is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(tbl.Title)) yield return tbl.Title;
                        if (!string.IsNullOrWhiteSpace(tbl.Caption)) yield return tbl.Caption;
                        if (!string.IsNullOrWhiteSpace(tbl.Footnote)) yield return tbl.Footnote;
                        if (tbl.Headers is not null)
                            foreach (var h in tbl.Headers)
                                if (!string.IsNullOrWhiteSpace(h)) yield return h;
                        if (tbl.Rows is not null)
                            foreach (var row in tbl.Rows)
                                foreach (var cell in row)
                                    if (!string.IsNullOrWhiteSpace(cell)) yield return cell;
                    }
                    break;
                case ArticleBlockKind.DiagramExplore:
                    if (Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(b.PayloadJson, out var diag) && diag is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(diag.Title)) yield return diag.Title;
                        if (!string.IsNullOrWhiteSpace(diag.Caption)) yield return diag.Caption;
                        if (diag.Hotspots is not null)
                            foreach (var hs in diag.Hotspots)
                            {
                                if (!string.IsNullOrWhiteSpace(hs.Label)) yield return hs.Label;
                                if (!string.IsNullOrWhiteSpace(hs.Note)) yield return hs.Note;
                            }
                    }
                    break;
                case ArticleBlockKind.Reveal:
                    if (Phase1PayloadCodec.TryDeserialize<RevealBlockPayload>(b.PayloadJson, out var rev) && rev is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(rev.Title)) yield return rev.Title;
                        if (rev.Steps is not null)
                            foreach (var step in rev.Steps)
                            {
                                if (!string.IsNullOrWhiteSpace(step.Heading)) yield return step.Heading;
                                if (!string.IsNullOrWhiteSpace(step.Body)) yield return step.Body;
                            }
                    }
                    break;
            }
        }

        foreach (var card in comp.LinkCards)
        {
            if (!string.IsNullOrWhiteSpace(card.Title)) yield return card.Title;
            if (!string.IsNullOrWhiteSpace(card.Text)) yield return card.Text;
        }
    }

    private static bool TryUpdateFenceState(string trimmedLine, ref string? activeFence) =>
        MarkdownFenceHelper.TryUpdateFenceState(trimmedLine, ref activeFence);

    private static int FindFirstUnquotedColon(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (Phase1PayloadRules.TrySkipInlineCodeSpan(text, i, out int nextCode))
            {
                i = nextCode;
                continue;
            }
            if (Phase1PayloadRules.TrySkipMathSpan(text, i, out int nextMath))
            {
                i = nextMath;
                continue;
            }
            if (text[i] == ':' && !(i + 1 < text.Length && text[i + 1] == ':'))
                return i;
            i++;
        }
        return -1;
    }

    private static int FindDoubleColonSeparator(string text)
    {
        int i = 0;
        while (i + 1 < text.Length)
        {
            if (Phase1PayloadRules.TrySkipInlineCodeSpan(text, i, out int nextCode))
            {
                i = nextCode;
                continue;
            }
            if (Phase1PayloadRules.TrySkipMathSpan(text, i, out int nextMath))
            {
                i = nextMath;
                continue;
            }
            if (text[i] == ':' && text[i + 1] == ':')
                return i;
            i++;
        }
        return -1;
    }
}
