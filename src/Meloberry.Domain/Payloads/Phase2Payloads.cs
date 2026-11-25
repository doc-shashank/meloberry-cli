using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meloberry.Domain.Common;

namespace Meloberry.Domain.Payloads;

public sealed record DiffCellSpec(
    int RowIndex,
    int ColIndex,
    string RawText,
    bool IsBlank,
    IReadOnlyList<string>? BlankAnswers,
    bool IsError,
    string? WrongText,
    IReadOnlyList<string>? Corrections);

public sealed record DifferentiationPayload(
    string Mode,
    string? Title,
    bool Bank,
    bool Fuzzy,
    bool CaseSensitive,
    bool AllowNoError,
    IReadOnlyList<string> Headers,
    IReadOnlyList<string> Alignments,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<DiffCellSpec> SpecialCells,
    string? Explanation);

public sealed record MechanismStep(
    int StepId,
    int OrderGroup,
    string Text,
    bool IsDistractor);

public sealed record MechanismPayload(
    string? Title,
    IReadOnlyList<MechanismStep> Steps,
    string? Explanation);

public sealed record RelabelBox(
    int Index,
    string Label,
    IReadOnlyList<string> Aliases,
    double X,
    double Y,
    double W,
    double H);

public sealed record RelabelImagePayload(
    string ImagePath,
    string? Prompt,
    string Mode,
    string Style,
    IReadOnlyList<RelabelBox> Boxes,
    IReadOnlyList<string> Distractors,
    bool Fuzzy,
    bool CaseSensitive,
    string? Explanation,
    string? SourceMasksPath = null,
    IReadOnlyList<string>? IncludeLabels = null);

public static partial class Phase2PayloadRules
{
    public static readonly IReadOnlyList<string> DiffModes =
        ["fill", "spot-wrong", "rearrange"];

    public static readonly IReadOnlyList<string> RelabelModes =
        ["reveal", "bank", "recall"];

    public static readonly IReadOnlyList<string> RelabelStyles =
        ["solid", "blur", "pixelate", "whiteout"];

    [GeneratedRegex(@"^~~(?<wrong>.*?)~~\s*->\s*(?<fix>.+)$", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex DiffFaultCellRegex();

    [GeneratedRegex(@"^(?<num>\d+)(?<subletter>[a-zA-Z])?\.\s*(?<text>.*)$", RegexOptions.Compiled)]
    private static partial Regex MechanismOrderedLineRegex();

    [GeneratedRegex(@"^!(?:\s+(?<text>.*))?$", RegexOptions.Compiled)]
    private static partial Regex MechanismDistractorLineRegex();

    public static bool IsValidDiffMode(string? mode) =>
        !string.IsNullOrWhiteSpace(mode) &&
        DiffModes.Contains(mode.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string NormalizeDiffMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? string.Empty : mode.Trim().ToLowerInvariant();

    public static Result<IReadOnlyList<DiffCellSpec>> ParseDifferentiationTable(
        string? mode,
        IReadOnlyList<string>? headers,
        IReadOnlyList<IReadOnlyList<string>>? rows)
    {
        var normMode = NormalizeDiffMode(mode);
        if (!IsValidDiffMode(normMode))
            return new Error("Diff.InvalidMode", $"Invalid differentiation mode '{mode}'. Supported modes: 'fill', 'spot-wrong', 'rearrange'.", ErrorType.Validation);

        if (headers is not { Count: >= 2 } || headers.All(string.IsNullOrWhiteSpace))
            return new Error("Diff.InvalidTable", "Differentiation table requires at least 2 column headers.", ErrorType.Validation);

        if (rows is not { Count: >= 1 })
            return new Error("Diff.InvalidTable", "Differentiation table requires at least 1 data row.", ErrorType.Validation);

        for (int r = 0; r < rows.Count; r++)
        {
            if (rows[r] is null || rows[r].Count != headers.Count)
                return new Error("Diff.InvalidTable", $"Differentiation row {r + 1} has {rows[r]?.Count ?? 0} cell(s), expected {headers.Count}.", ErrorType.Validation);
        }

        if (normMode == "rearrange")
        {
            if (headers.Count < 3)
                return new Error("Diff.InvalidTable", "Differentiation mode 'rearrange' requires at least 3 columns (1 feature column + at least 2 entity columns).", ErrorType.Validation);
            return Array.Empty<DiffCellSpec>();
        }

        var specialCells = new List<DiffCellSpec>();
        if (normMode == "fill")
        {
            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < headers.Count; c++)
                {
                    var cell = rows[r][c] ?? string.Empty;
                    var spansRes = Phase1PayloadRules.FindFillBlankSpans(cell);
                    if (!spansRes.IsSuccess)
                        return new Error("Diff.MalformedBlankCell", $"Malformed blank cell at row {r + 1}, column {c + 1}: {spansRes.Error!.Description}", ErrorType.Validation);

                    if (spansRes.Value.Count == 0)
                        continue;

                    if (spansRes.Value.Count > 1)
                        return new Error("Diff.MalformedBlankCell", $"Cell at row {r + 1}, column {c + 1} contains multiple '{{{{...}}}}' blanks; use at most one blank per table cell.", ErrorType.Validation);

                    var slotRes = Phase1PayloadRules.ParseFillBlankTemplate(cell);
                    if (!slotRes.IsSuccess)
                        return new Error("Diff.MalformedBlankCell", $"Invalid blank cell at row {r + 1}, column {c + 1}: {slotRes.Error!.Description}", ErrorType.Validation);

                    specialCells.Add(new DiffCellSpec(
                        r, c, cell,
                        IsBlank: true,
                        BlankAnswers: slotRes.Value[0].Answers,
                        IsError: false,
                        WrongText: null,
                        Corrections: null));
                }
            }
            return specialCells;
        }

        // normMode == "spot-wrong"
        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < headers.Count; c++)
            {
                var cell = rows[r][c] ?? string.Empty;
                if (!ContainsOutsideCodeAndMath(cell, "~~"))
                    continue;

                var trimmedCell = cell.Trim();
                var m = DiffFaultCellRegex().Match(trimmedCell);
                if (!m.Success)
                    return new Error("Diff.MalformedFaultCell", $"Malformed fault cell at row {r + 1}, column {c + 1}: '{cell}'. Expected '~~wrong~~ -> correction \\| alias' or '~~wrong~~ -> {{{{correction | alias}}}}'.", ErrorType.Validation);

                var wrongText = m.Groups["wrong"].Value.Trim();
                var fixRaw = m.Groups["fix"].Value.Trim();
                if (string.IsNullOrEmpty(wrongText) || string.IsNullOrEmpty(fixRaw))
                    return new Error("Diff.MalformedFaultCell", $"Fault cell at row {r + 1}, column {c + 1} must specify non-empty wrong text and correction.", ErrorType.Validation);

                if (fixRaw.StartsWith("{{", StringComparison.Ordinal) || fixRaw.EndsWith("}}", StringComparison.Ordinal))
                {
                    var braceSpans = Phase1PayloadRules.FindFillBlankSpans(fixRaw);
                    if (!braceSpans.IsSuccess || braceSpans.Value.Count != 1 ||
                        braceSpans.Value[0].OpenIndex != 0 || braceSpans.Value[0].CloseIndex != fixRaw.Length - 2)
                    {
                        return new Error("Diff.MalformedFaultCell", $"Malformed braced correction alias list at row {r + 1}, column {c + 1}: '{fixRaw}'.", ErrorType.Validation);
                    }
                    fixRaw = fixRaw[2..^2].Trim();
                }

                var parts = Phase1PayloadRules.SplitPipeAliases(fixRaw);
                var corrections = new List<string>(parts.Count);
                foreach (var p in parts)
                {
                    var trimmedFix = p.Trim();
                    if (string.IsNullOrEmpty(trimmedFix))
                        return new Error("Diff.MalformedFaultCell", $"Fault cell at row {r + 1}, column {c + 1} contains an empty correction alias.", ErrorType.Validation);
                    corrections.Add(trimmedFix);
                }

                specialCells.Add(new DiffCellSpec(
                    r, c, cell,
                    IsBlank: false,
                    BlankAnswers: null,
                    IsError: true,
                    WrongText: wrongText,
                    Corrections: corrections));
            }
        }

        return specialCells;
    }

    public static Result<IReadOnlyList<MechanismStep>> ParseMechanismLines(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            return new Error("Mechanism.EmptySteps", "Mechanism directive body must contain ordered steps.", ErrorType.Validation);

        var builders = new List<(int OrderGroup, bool IsDistractor, StringBuilder Text)>();
        var bareNumbers = new HashSet<int>();
        var sublettersByNum = new Dictionary<int, HashSet<char>>();

        foreach (var rawLine in rawBody.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            bool indented = rawLine.Length > 0 && (rawLine[0] == ' ' || rawLine[0] == '\t');
            var ordMatch = !indented ? MechanismOrderedLineRegex().Match(trimmed) : Match.Empty;
            if (ordMatch.Success)
            {
                if (!int.TryParse(ordMatch.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num) || num < 1)
                    return new Error("Mechanism.InvalidStepNumber", $"Invalid step number '{ordMatch.Groups["num"].Value}'. Step numbers must be >= 1.", ErrorType.Validation);

                if (ordMatch.Groups["subletter"].Success)
                {
                    char sub = char.ToLowerInvariant(ordMatch.Groups["subletter"].Value[0]);
                    if (bareNumbers.Contains(num))
                        return new Error("Mechanism.DuplicateStepNumber", $"Step {num} mixes bare '{num}.' and parallel '{num}{sub}.' numbering.", ErrorType.Validation);
                    if (!sublettersByNum.TryGetValue(num, out var subs))
                    {
                        subs = [];
                        sublettersByNum[num] = subs;
                    }
                    if (!subs.Add(sub))
                        return new Error("Mechanism.DuplicateStepNumber", $"Duplicate parallel step label '{num}{sub}.' in mechanism.", ErrorType.Validation);
                }
                else
                {
                    if (sublettersByNum.ContainsKey(num) || !bareNumbers.Add(num))
                        return new Error("Mechanism.DuplicateStepNumber", $"Duplicate step number '{num}.' in mechanism. Use '{num}a.', '{num}b.' for parallel steps.", ErrorType.Validation);
                }

                builders.Add((num, false, new StringBuilder(ordMatch.Groups["text"].Value.Trim())));
                continue;
            }

            var distMatch = !indented ? MechanismDistractorLineRegex().Match(trimmed) : Match.Empty;
            if (distMatch.Success)
            {
                builders.Add((0, true, new StringBuilder(distMatch.Groups["text"].Value.Trim())));
                continue;
            }

            if (indented && builders.Count > 0)
            {
                var prev = builders[^1].Text;
                if (prev.Length > 0) prev.Append('\n');
                prev.Append(trimmed);
                continue;
            }

            return new Error("Mechanism.InvalidSyntax", $"Unexpected line '{trimmed}' in mechanism body. Prefix ordered steps with '1.', '2a.' or distractors with '! '.", ErrorType.Validation);
        }

        var steps = new List<MechanismStep>(builders.Count);
        for (int i = 0; i < builders.Count; i++)
        {
            var text = builders[i].Text.ToString().Trim();
            if (string.IsNullOrEmpty(text))
                return new Error("Mechanism.EmptyStep", $"Mechanism step #{i + 1} has empty text.", ErrorType.Validation);
            steps.Add(new MechanismStep(i + 1, builders[i].OrderGroup, text, builders[i].IsDistractor));
        }

        return steps;
    }

    public static bool IsValidRelabelMode(string? mode) =>
        !string.IsNullOrWhiteSpace(mode) &&
        RelabelModes.Contains(mode.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string NormalizeRelabelMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? string.Empty : mode.Trim().ToLowerInvariant();

    public static bool IsValidRelabelStyle(string? style) =>
        !string.IsNullOrWhiteSpace(style) &&
        RelabelStyles.Contains(style.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string NormalizeRelabelStyle(string? style) =>
        string.IsNullOrWhiteSpace(style) ? string.Empty : style.Trim().ToLowerInvariant();

    public static bool HasValidNormalizedBox(double x, double y, double w, double h) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(w) && double.IsFinite(h) &&
        x >= 0.0 && y >= 0.0 && w > 0.0 && h > 0.0 &&
        x + w <= 1.001 && y + h <= 1.001;

    public static bool HasValidRelabelBoxBounds(RelabelBox box) =>
        HasValidNormalizedBox(box.X, box.Y, box.W, box.H);

    public static bool ValidateRelabelBox(RelabelBox box) =>
        box is not null &&
        !string.IsNullOrWhiteSpace(box.Label) &&
        HasValidRelabelBoxBounds(box);

    public static Result<IReadOnlyList<RelabelBox>> ParseMaskSidecarJson(
        string rawJson,
        IReadOnlyList<string>? includeLabels = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return new Error("Relabel.InvalidSidecar", "Relabel masks sidecar JSON cannot be empty.", ErrorType.Validation);

        var cleaned = rawJson.TrimStart('\uFEFF');
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(cleaned, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            return new Error("Relabel.InvalidSidecar", $"Invalid relabel sidecar JSON: {ex.Message}", ErrorType.Validation);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var boxes = new List<RelabelBox>();

            // Format A: melomg --meta (.meta.json) with pixelWidth, pixelHeight & appliedMasks[]
            if (root.ValueKind == JsonValueKind.Object &&
                TryGetJsonPropIgnoreCase(root, "appliedMasks", out var appliedEl) &&
                appliedEl.ValueKind == JsonValueKind.Array)
            {
                if (!TryGetJsonDouble(root, "pixelWidth", out double imgW) || imgW <= 0 ||
                    !TryGetJsonDouble(root, "pixelHeight", out double imgH) || imgH <= 0)
                {
                    return new Error("Relabel.InvalidSidecar", "melomg '.meta.json' sidecar must include positive 'pixelWidth' and 'pixelHeight'.", ErrorType.Validation);
                }

                int idx = 0;
                foreach (var item in appliedEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        return new Error("Relabel.InvalidSidecar", $"melomg appliedMasks[{idx}] must be a JSON object.", ErrorType.Validation);
                    if (!TryGetJsonDouble(item, "pixelX", out double px) ||
                        !TryGetJsonDouble(item, "pixelY", out double py) ||
                        !TryGetJsonDouble(item, "pixelWidth", out double pw) ||
                        !TryGetJsonDouble(item, "pixelHeight", out double ph))
                    {
                        return new Error("Relabel.InvalidSidecar", $"melomg appliedMasks[{idx}] is missing numeric pixelX/pixelY/pixelWidth/pixelHeight.", ErrorType.Validation);
                    }

                    double x = Math.Clamp(px / imgW, 0.0, 1.0);
                    double y = Math.Clamp(py / imgH, 0.0, 1.0);
                    double w = Math.Clamp(pw / imgW, 0.0, 1.0 - x);
                    double h = Math.Clamp(ph / imgH, 0.0, 1.0 - y);

                    string label = TryGetJsonString(item, "text", out var txt)
                        ? txt.Trim()
                        : (TryGetJsonString(item, "label", out var lbl) ? lbl.Trim() : string.Empty);
                    var aliases = ReadStringArrayProp(item, "aliases");
                    boxes.Add(new RelabelBox(++idx, label, aliases, x, y, w, h));
                }
            }
            else
            {
                // Format B: normalized .masks.json ({ boxes: [...] } | { masks: [...] } | [...])
                JsonElement arrEl;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    arrEl = root;
                }
                else if (root.ValueKind == JsonValueKind.Object &&
                         TryGetJsonPropIgnoreCase(root, "boxes", out var bEl) &&
                         bEl.ValueKind == JsonValueKind.Array)
                {
                    arrEl = bEl;
                }
                else if (root.ValueKind == JsonValueKind.Object &&
                         TryGetJsonPropIgnoreCase(root, "masks", out var mEl) &&
                         mEl.ValueKind == JsonValueKind.Array)
                {
                    arrEl = mEl;
                }
                else
                {
                    return new Error("Relabel.InvalidSidecar", "Relabel sidecar JSON must be an array or an object with 'boxes', 'masks', or melomg 'appliedMasks'.", ErrorType.Validation);
                }

                int idx = 0;
                foreach (var item in arrEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        return new Error("Relabel.InvalidSidecar", $"Sidecar box entry [{idx}] must be a JSON object.", ErrorType.Validation);

                    string label = TryGetJsonString(item, "label", out var lbl)
                        ? lbl.Trim()
                        : (TryGetJsonString(item, "text", out var txt) ? txt.Trim() : string.Empty);
                    var aliases = ReadStringArrayProp(item, "aliases");

                    if (!TryReadNormalizedCoords(item, out double x, out double y, out double w, out double h))
                        return new Error("Relabel.InvalidSidecar", $"Sidecar box entry [{idx}] must specify numeric 'rect: [x,y,w,h]', 'box: {{x,y,w,h}}', or 'x,y,w,h'.", ErrorType.Validation);

                    boxes.Add(new RelabelBox(++idx, label, aliases, x, y, w, h));
                }
            }

            // Optional include filtering (case-insensitive: exact match first, fallback substring)
            if (includeLabels is { Count: > 0 })
            {
                var wanted = includeLabels
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .ToList();

                if (wanted.Count > 0)
                {
                    var matchedIndices = new HashSet<int>();
                    foreach (var term in wanted)
                    {
                        bool anyExact = false;
                        for (int i = 0; i < boxes.Count; i++)
                        {
                            if (string.Equals(boxes[i].Label, term, StringComparison.OrdinalIgnoreCase) ||
                                boxes[i].Aliases.Any(a => string.Equals(a, term, StringComparison.OrdinalIgnoreCase)))
                            {
                                matchedIndices.Add(i);
                                anyExact = true;
                            }
                        }
                        if (!anyExact)
                        {
                            for (int i = 0; i < boxes.Count; i++)
                            {
                                if (boxes[i].Label.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                    boxes[i].Aliases.Any(a => a.Contains(term, StringComparison.OrdinalIgnoreCase)))
                                {
                                    matchedIndices.Add(i);
                                }
                            }
                        }
                    }

                    boxes = boxes
                        .Where((_, i) => matchedIndices.Contains(i))
                        .ToList();
                }
            }

            var reindexed = new List<RelabelBox>(boxes.Count);
            for (int i = 0; i < boxes.Count; i++)
                reindexed.Add(boxes[i] with { Index = i + 1 });

            return reindexed;
        }
    }

    private static bool TryGetJsonPropIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static bool TryGetJsonString(JsonElement obj, string name, out string value)
    {
        if (TryGetJsonPropIgnoreCase(obj, name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetJsonDouble(JsonElement obj, string name, out double value)
    {
        if (TryGetJsonPropIgnoreCase(obj, name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
            return true;
        value = 0;
        return false;
    }

    private static IReadOnlyList<string> ReadStringArrayProp(JsonElement obj, string name)
    {
        if (!TryGetJsonPropIgnoreCase(obj, name, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        }
        return list;
    }

    private static bool TryReadNormalizedCoords(JsonElement item, out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;
        if (TryGetJsonPropIgnoreCase(item, "rect", out var rectEl) && rectEl.ValueKind == JsonValueKind.Array && rectEl.GetArrayLength() == 4)
        {
            return rectEl[0].ValueKind == JsonValueKind.Number && rectEl[0].TryGetDouble(out x) &&
                   rectEl[1].ValueKind == JsonValueKind.Number && rectEl[1].TryGetDouble(out y) &&
                   rectEl[2].ValueKind == JsonValueKind.Number && rectEl[2].TryGetDouble(out w) &&
                   rectEl[3].ValueKind == JsonValueKind.Number && rectEl[3].TryGetDouble(out h);
        }
        if (TryGetJsonPropIgnoreCase(item, "box", out var boxEl) && boxEl.ValueKind == JsonValueKind.Object)
        {
            return TryGetJsonDouble(boxEl, "x", out x) &&
                   TryGetJsonDouble(boxEl, "y", out y) &&
                   TryGetJsonDouble(boxEl, "w", out w) &&
                   TryGetJsonDouble(boxEl, "h", out h);
        }
        return TryGetJsonDouble(item, "x", out x) &&
               TryGetJsonDouble(item, "y", out y) &&
               TryGetJsonDouble(item, "w", out w) &&
               TryGetJsonDouble(item, "h", out h);
    }

    private static bool ContainsOutsideCodeAndMath(string text, string token)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (Phase1PayloadRules.TrySkipInlineCodeSpan(text, i, out int afterCode)) { i = afterCode; continue; }
            if (Phase1PayloadRules.TrySkipMathSpan(text, i, out int afterMath)) { i = afterMath; continue; }
            if (i + token.Length <= text.Length && string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
                return true;
            i++;
        }
        return false;
    }
}
