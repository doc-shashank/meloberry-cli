using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;

namespace Meloberry.Infrastructure.Services;

public sealed partial class DeclarativeSpecLoader : IDeclarativeSpecLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly string[] KnownSchemaKeys =
    [
        "kind", "name", "description", "title", "quizType", "components", "blocks", "questions",
        "layoutOverrides", "order", "ref", "text", "imagePath", "caption", "alt", "tone",
        "type", "explanation", "maxOptions", "isMultiCorrect", "options", "index", "isCorrect",
        "front", "back", "position", "textPosition",
        "stickyFirstCol", "hideableCols", "headers", "alignments", "rows", "footnote",
        "fuzzy", "caseSensitive", "placeholder", "assertion", "reason", "answer", "scheme",
        "requireFix", "allowNoError", "noError",
        "mode", "bank", "steps", "stepId", "orderGroup", "isDistractor",
        "prompt", "style", "masks", "masksPath", "include", "boxes", "label", "aliases", "rect", "x", "y", "w", "h", "distractors",
        "hotspots", "note", "showFirst", "heading", "body", "buckets", "bucketId", "items", "bucket", "pairs", "left", "right",
        "tolerance", "toleranceMode", "unit", "stem",
        "links", "id"
    ];

    [GeneratedRegex(@"property\s+'(?<prop>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex UnmappedPropertyRegex();

    public async Task<Result<DeclarativeApplySpec>> LoadAsync(string specPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(specPath))
            return new Error("Json.FileNotFound", "Input path cannot be empty.", ErrorType.NotFound);

        var cleaned = NormalizeInputPath(specPath);
        if (Directory.Exists(cleaned))
            return await LoadDirectoryAsync(cleaned, cancellationToken);

        if (!File.Exists(cleaned))
            return new Error("Json.FileNotFound", $"Input file or directory '{specPath}' was not found.", ErrorType.NotFound);

        var rawText = await File.ReadAllTextAsync(cleaned, cancellationToken);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(cleaned));
        if (IsMarkdownFile(cleaned))
            return MarkdownSpecParser.ParseSingleSpec(rawText, cleaned, cleaned);

        var res = ParseSpec(rawText, cleaned);
        return res.IsSuccess ? res.Value with { AssetBaseDirectory = baseDir } : res.Error!;
    }

    public Task<Result<DeclarativeApplySpec>> LoadFromTextAsync(string rawText, CancellationToken cancellationToken = default)
    {
        if (MarkdownSpecParser.LooksLikeMarkdown(rawText))
            return Task.FromResult(MarkdownSpecParser.ParseSingleSpec(rawText, "<stdin>"));

        return Task.FromResult(ParseSpec(rawText, "<stdin>"));
    }

    private static async Task<Result<DeclarativeApplySpec>> LoadDirectoryAsync(string directoryPath, CancellationToken ct)
    {
        var fullDir = Path.GetFullPath(directoryPath);
        string? sessionName = null;
        string? sessionDescription = null;
        IReadOnlyList<string>? explicitOrder = null;
        IReadOnlyList<DeclarativeComponentSpec>? manifestComponents = null;

        var sessionMdPath = Path.Combine(fullDir, "_session.md");
        var sessionJsonPath = Path.Combine(fullDir, "session.json");
        bool hasSessionMd = File.Exists(sessionMdPath);
        bool hasSessionJson = File.Exists(sessionJsonPath);

        if (hasSessionMd && hasSessionJson)
        {
            return new Error(
                "Json.InvalidSpec",
                $"Directory '{directoryPath}' contains both '_session.md' and 'session.json'. Keep only one session manifest.",
                ErrorType.Validation);
        }

        if (hasSessionMd)
        {
            var raw = await File.ReadAllTextAsync(sessionMdPath, ct);
            var parsed = MarkdownSpecParser.ParseDocument(raw, sessionMdPath, isSessionManifest: true);
            if (!parsed.IsSuccess) return parsed.Error!;
            if (!string.IsNullOrWhiteSpace(parsed.Value.Kind) &&
                !string.Equals(parsed.Value.Kind.Trim(), "session", StringComparison.OrdinalIgnoreCase))
                return new Error("Markdown.InvalidFrontmatter", $"Directory '_session.md' must omit 'kind' or set 'kind' to 'session', found '{parsed.Value.Kind}'.", ErrorType.Validation);
            if (!string.IsNullOrWhiteSpace(parsed.Value.QuizType))
                return new Error("Json.InvalidFieldForType", "Top-level 'quizType' is not valid on '_session.md' session manifests.", ErrorType.Validation);
            if (parsed.Value.LayoutOverrides is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Top-level 'layoutOverrides' belong on components or individual questions, not '_session.md' session manifests.", ErrorType.Validation);
            if (!string.IsNullOrWhiteSpace(parsed.Value.Ref))
                return new Error("Json.InvalidFieldForType", "Top-level 'ref' belongs on components, blocks, or questions, not '_session.md' session manifests.", ErrorType.Validation);
            sessionName = parsed.Value.Name ?? parsed.Value.Title;
            sessionDescription = parsed.Value.Description;
            explicitOrder = parsed.Value.Order;
        }
        else if (hasSessionJson)
        {
            var raw = await File.ReadAllTextAsync(sessionJsonPath, ct);
            var parsed = ParseSpec(raw, sessionJsonPath);
            if (!parsed.IsSuccess) return parsed.Error!;
            if (!string.IsNullOrWhiteSpace(parsed.Value.Kind) &&
                !string.Equals(parsed.Value.Kind.Trim(), "session", StringComparison.OrdinalIgnoreCase))
                return new Error("Json.InvalidSpec", $"Directory 'session.json' must omit 'kind' or set 'kind' to 'session', found '{parsed.Value.Kind}'.", ErrorType.Validation);
            if (parsed.Value.Blocks is { Count: > 0 } || parsed.Value.Questions is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Directory 'session.json' cannot specify root 'blocks' or 'questions'.", ErrorType.Validation);
            if (parsed.Value.LayoutOverrides is { Count: > 0 })
                return new Error("Json.InvalidFieldForType", "Top-level 'layoutOverrides' belong on components or individual questions, not 'session.json' session roots.", ErrorType.Validation);
            if (!string.IsNullOrWhiteSpace(parsed.Value.Ref))
                return new Error("Json.InvalidFieldForType", "Top-level 'ref' belongs on components, blocks, or questions, not 'session.json' session roots.", ErrorType.Validation);
            sessionName = parsed.Value.Name ?? parsed.Value.Title;
            sessionDescription = parsed.Value.Description;
            explicitOrder = parsed.Value.Order;
            manifestComponents = parsed.Value.Components;
        }

        var discoveredFiles = Directory.EnumerateFiles(fullDir)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith('_')) return false;
                if (string.Equals(name, "session.json", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (name.EndsWith(".masks.json", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                    return false;
                return IsMarkdownFile(f) || string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        discoveredFiles.Sort((a, b) => CompareComponentFiles(Path.GetFileName(a), Path.GetFileName(b), explicitOrder));

        if (discoveredFiles.Count == 0 && manifestComponents is { Count: > 0 })
        {
            return new DeclarativeApplySpec(
                "session",
                sessionName ?? MarkdownSpecParser.HumanizeFileStem(fullDir) ?? "Study Session",
                sessionDescription,
                null, null,
                manifestComponents.Select(c => c with { SourceDirectory = fullDir }).ToList(),
                null, null, null,
                AssetBaseDirectory: fullDir);
        }

        if (discoveredFiles.Count > 0 && manifestComponents is { Count: > 0 })
        {
            return new Error(
                "Json.InvalidSpec",
                $"Directory '{directoryPath}' defines inline 'components[]' in 'session.json' alongside standalone component files. Keep 'session.json' metadata-only or remove separate component files.",
                ErrorType.Validation);
        }

        if (discoveredFiles.Count == 0)
            return new Error("Json.InvalidSpec", $"Directory '{directoryPath}' contains no component '.md' or '.json' files.", ErrorType.Validation);

        var components = new List<DeclarativeComponentSpec>(discoveredFiles.Count);
        foreach (var file in discoveredFiles)
        {
            var raw = await File.ReadAllTextAsync(file, ct);
            if (IsMarkdownFile(file))
            {
                var mdRes = MarkdownSpecParser.ParseComponent(raw, file);
                if (!mdRes.IsSuccess) return mdRes.Error!;
                components.Add(mdRes.Value);
                continue;
            }

            var jsonRes = ParseSpec(raw, file);
            if (!jsonRes.IsSuccess) return jsonRes.Error!;
            var spec = jsonRes.Value;
            if (spec.Components is { Count: > 0 })
            {
                if (spec.Blocks is { Count: > 0 } || spec.Questions is { Count: > 0 })
                    return new Error("Json.InvalidFieldForType", $"Component JSON '{Path.GetFileName(file)}' cannot mix 'components[]' with root 'blocks' or 'questions'.", ErrorType.Validation);
                foreach (var c in spec.Components)
                    components.Add(c with { SourceDirectory = fullDir });
            }
            else if (spec.Blocks is { Count: > 0 } || string.Equals(spec.Kind, "article", StringComparison.OrdinalIgnoreCase))
            {
                if (spec.Questions is { Count: > 0 })
                    return new Error("Json.InvalidFieldForType", $"Article JSON '{Path.GetFileName(file)}' cannot specify quiz 'questions[]'.", ErrorType.Validation);
                components.Add(new DeclarativeComponentSpec(
                    "article",
                    spec.Title ?? spec.Name ?? MarkdownSpecParser.HumanizeFileStem(file),
                    null,
                    spec.Blocks,
                    null,
                    spec.LayoutOverrides,
                    Ref: spec.Ref,
                    SourceDirectory: fullDir,
                    Links: spec.Links));
            }
            else if (spec.Questions is { Count: > 0 } || spec.Kind?.ToLowerInvariant() is "quiz" or "mcq" or "flashcard" or "mixed")
            {
                if (spec.Blocks is { Count: > 0 })
                    return new Error("Json.InvalidFieldForType", $"Quiz JSON '{Path.GetFileName(file)}' cannot specify article 'blocks[]'.", ErrorType.Validation);
                var qType = !string.IsNullOrWhiteSpace(spec.QuizType)
                    ? spec.QuizType
                    : (spec.Kind?.Equals("quiz", StringComparison.OrdinalIgnoreCase) == false ? spec.Kind : "mixed");
                components.Add(new DeclarativeComponentSpec(
                    "quiz",
                    spec.Title ?? spec.Name ?? MarkdownSpecParser.HumanizeFileStem(file),
                    qType,
                    null,
                    spec.Questions,
                    spec.LayoutOverrides,
                    Ref: spec.Ref,
                    SourceDirectory: fullDir,
                    Links: spec.Links));
            }
            else
            {
                return new Error("Json.InvalidSpec", $"Component JSON '{Path.GetFileName(file)}' must define article 'blocks' or quiz 'questions'.", ErrorType.Validation);
            }
        }

        return new DeclarativeApplySpec(
            Kind: "session",
            Name: sessionName ?? MarkdownSpecParser.HumanizeFileStem(fullDir) ?? "Study Session",
            Description: sessionDescription,
            Title: null,
            QuizType: null,
            Components: components,
            Blocks: null,
            Questions: null,
            LayoutOverrides: null,
            AssetBaseDirectory: fullDir);
    }

    private static string NormalizeInputPath(string rawPath)
    {
        var trimmed = rawPath.Trim();
        var root = Path.GetPathRoot(trimmed);
        if (!string.IsNullOrEmpty(root) && string.Equals(trimmed, root, StringComparison.Ordinal))
            return trimmed;
        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsMarkdownFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase);
    }

    internal static int CompareComponentFiles(string nameA, string nameB, IReadOnlyList<string>? explicitOrder)
    {
        if (explicitOrder is { Count: > 0 })
        {
            int idxA = FindOrderIndex(nameA, explicitOrder);
            int idxB = FindOrderIndex(nameB, explicitOrder);
            if (idxA >= 0 && idxB >= 0 && idxA != idxB) return idxA.CompareTo(idxB);
            if (idxA >= 0 && idxB < 0) return -1;
            if (idxA < 0 && idxB >= 0) return 1;
        }
        return CompareNaturalOrdinal(nameA, nameB);
    }

    private static int FindOrderIndex(string fileName, IReadOnlyList<string> order)
    {
        for (int i = 0; i < order.Count; i++)
        {
            var entry = order[i].Trim();
            if (string.Equals(fileName, entry, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(fileName), entry, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static int CompareNaturalOrdinal(string a, string b)
    {
        int ia = 0, ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            if (char.IsDigit(a[ia]) && char.IsDigit(b[ib]))
            {
                int sa = ia; while (ia < a.Length && char.IsDigit(a[ia])) ia++;
                int sb = ib; while (ib < b.Length && char.IsDigit(b[ib])) ib++;
                var numA = a.AsSpan(sa, ia - sa).TrimStart('0');
                var numB = b.AsSpan(sb, ib - sb).TrimStart('0');
                int lenCmp = numA.Length.CompareTo(numB.Length);
                if (lenCmp != 0) return lenCmp;
                int valCmp = numA.CompareTo(numB, StringComparison.Ordinal);
                if (valCmp != 0) return valCmp;
                int leadCmp = (ia - sa).CompareTo(ib - sb);
                if (leadCmp != 0) return leadCmp;
            }
            else
            {
                int chCmp = char.ToUpperInvariant(a[ia]).CompareTo(char.ToUpperInvariant(b[ib]));
                if (chCmp != 0) return chCmp;
                ia++;
                ib++;
            }
        }
        return (a.Length - ia).CompareTo(b.Length - ib);
    }

    private static Result<DeclarativeApplySpec> ParseSpec(string rawJson, string sourceLabel)
    {
        rawJson = rawJson.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(rawJson))
            return new Error("Json.InvalidSpec", $"JSON input from '{sourceLabel}' is empty.", ErrorType.Validation);

        try
        {
            var dto = JsonSerializer.Deserialize<ApplySpecDto>(rawJson, JsonOptions);
            if (dto is null)
                return new Error("Json.InvalidSpec", "JSON input must contain a valid declarative object.", ErrorType.Validation);

            var components = new List<DeclarativeComponentSpec>();
            if (dto.Components is not null)
            {
                for (int i = 0; i < dto.Components.Count; i++)
                {
                    var c = dto.Components[i];
                    if (c is null || string.IsNullOrWhiteSpace(c.Kind))
                        return new Error("Json.InvalidSpec", $"Component at index {i} must be a non-null object with a 'kind' field.", ErrorType.Validation, Path: $"components[{i}]");

                    var blocksRes = MapBlocks(c.Blocks, $"components[{i}].blocks");
                    if (!blocksRes.IsSuccess) return blocksRes.Error!;

                    var qRes = MapQuestions(c.Questions, $"components[{i}].questions");
                    if (!qRes.IsSuccess) return qRes.Error!;

                    var linksRes = MapLinks(c.Links, $"components[{i}].links");
                    if (!linksRes.IsSuccess) return linksRes.Error!;

                    components.Add(new DeclarativeComponentSpec(c.Kind, c.Title, c.QuizType, blocksRes.Value, qRes.Value, c.LayoutOverrides, c.Ref, Links: linksRes.Value));
                }
            }

            var rootBlocksRes = MapBlocks(dto.Blocks, "blocks");
            if (!rootBlocksRes.IsSuccess) return rootBlocksRes.Error!;

            var rootQuestionsRes = MapQuestions(dto.Questions, "questions");
            if (!rootQuestionsRes.IsSuccess) return rootQuestionsRes.Error!;

            var rootLinksRes = MapLinks(dto.Links, "links");
            if (!rootLinksRes.IsSuccess) return rootLinksRes.Error!;

            return new DeclarativeApplySpec(
                dto.Kind,
                dto.Name,
                dto.Description,
                dto.Title,
                dto.QuizType,
                dto.Components is null ? null : components,
                rootBlocksRes.Value,
                rootQuestionsRes.Value,
                dto.LayoutOverrides,
                Ref: dto.Ref,
                Order: dto.Order,
                Links: rootLinksRes.Value);
        }
        catch (JsonException ex)
        {
            return CreateJsonParseError(sourceLabel, ex);
        }
    }

    internal static Error CreateJsonParseError(string sourceLabel, JsonException ex)
    {
        string? hint = null;
        var match = UnmappedPropertyRegex().Match(ex.Message);
        if (match.Success)
        {
            var unknown = match.Groups["prop"].Value;
            var suggestion = FindClosestKey(unknown);
            if (suggestion is not null)
                hint = $"Did you mean '{suggestion}'?";
        }

        return new Error("Json.ParseError", $"Invalid JSON in '{sourceLabel}': {ex.Message}", ErrorType.Validation, Path: ex.Path, Hint: hint);
    }

    internal static string? FindClosestKey(string unknown, IEnumerable<string>? candidateKeys = null)
    {
        if (string.IsNullOrWhiteSpace(unknown)) return null;
        string? best = null;
        int bestDist = 3; // max distance 2
        foreach (var candidate in candidateKeys ?? KnownSchemaKeys)
        {
            int d = Levenshtein(unknown.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (d < bestDist)
            {
                bestDist = d;
                best = candidate;
            }
        }
        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Length, b.Length];
    }

    private static Result<IReadOnlyList<DeclarativeArticleBlockSpec>?> MapBlocks(List<ApplyArticleBlockDto?>? rawBlocks, string pathPrefix)
    {
        if (rawBlocks is null) return Result<IReadOnlyList<DeclarativeArticleBlockSpec>?>.Success(null);
        var list = new List<DeclarativeArticleBlockSpec>(rawBlocks.Count);
        for (int i = 0; i < rawBlocks.Count; i++)
        {
            var b = rawBlocks[i];
            if (b is null || string.IsNullOrWhiteSpace(b.Kind))
                return new Error("Json.InvalidSpec", $"Article block at index {i} must be a non-null object with a 'kind' field.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");

            bool hasTableFields = b.StickyFirstCol.HasValue || b.HideableCols is not null || b.Headers is not null ||
                                  b.Alignments is not null || b.Rows is not null || b.Footnote is not null;
            bool hasDiagramFields = b.Hotspots is not null || b.Masks is not null || b.MasksPath is not null;
            bool hasRevealFields = b.ShowFirst.HasValue || b.Steps is not null;

            if (string.Equals(b.Kind.Trim(), "table", StringComparison.OrdinalIgnoreCase))
            {
                if (hasDiagramFields || hasRevealFields)
                    return new Error("Json.InvalidFieldForType", $"Article block kind '{b.Kind}' cannot specify diagram-explore or reveal fields.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                if (b.Headers is not null && b.Headers.Any(h => h is null))
                    return new Error("Json.InvalidSpec", $"Table block at '{pathPrefix}[{i}]' contains a null entry in 'headers'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].headers");
                if (b.Alignments is not null && b.Alignments.Any(a => a is null))
                    return new Error("Json.InvalidSpec", $"Table block at '{pathPrefix}[{i}]' contains a null entry in 'alignments'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].alignments");
                if (b.Rows is not null)
                {
                    for (int rIdx = 0; rIdx < b.Rows.Count; rIdx++)
                    {
                        var row = b.Rows[rIdx];
                        if (row is null || row.Any(c => c is null))
                            return new Error("Json.InvalidSpec", $"Table block at '{pathPrefix}[{i}]' contains a null row or cell in 'rows[{rIdx}]'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].rows[{rIdx}]");
                    }
                }

                var headers = (IReadOnlyList<string>)(b.Headers?.Select(h => h!).ToList() ?? []);
                var alignments = b.Alignments is { Count: > 0 }
                    ? (IReadOnlyList<string>)b.Alignments.Select(a => string.IsNullOrWhiteSpace(a) ? "left" : a!.Trim().ToLowerInvariant()).ToList()
                    : Enumerable.Repeat("left", headers.Count).ToList();
                var rows = (IReadOnlyList<IReadOnlyList<string>>)(b.Rows?.Select(r => (IReadOnlyList<string>)r!.Select(c => c!).ToList()).ToList() ?? []);
                var hideable = (IReadOnlyList<int>)(b.HideableCols ?? []);

                var payload = new TableBlockPayload(
                    string.IsNullOrWhiteSpace(b.Title) ? null : b.Title.Trim(),
                    string.IsNullOrWhiteSpace(b.Caption) ? null : b.Caption.Trim(),
                    b.StickyFirstCol ?? false,
                    hideable,
                    headers,
                    alignments,
                    rows,
                    string.IsNullOrWhiteSpace(b.Footnote) ? null : b.Footnote.Trim());

                list.Add(new DeclarativeArticleBlockSpec(
                    b.Kind, b.Text, b.ImagePath, b.Ref, b.Caption, b.Alt, b.Tone, b.Title,
                    PayloadJson: Phase1PayloadCodec.Serialize(payload)));
                continue;
            }

            if (b.Kind.Trim().Equals("diagram-explore", StringComparison.OrdinalIgnoreCase) ||
                b.Kind.Trim().Equals("diagramexplore", StringComparison.OrdinalIgnoreCase))
            {
                if (hasTableFields || hasRevealFields || !string.IsNullOrWhiteSpace(b.Text) || !string.IsNullOrWhiteSpace(b.Tone))
                    return new Error("Json.InvalidFieldForType", "Article diagram-explore block specifies fields belonging to another block type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                if (!string.IsNullOrWhiteSpace(b.Masks) && !string.IsNullOrWhiteSpace(b.MasksPath))
                    return new Error("Json.InvalidSpec", "Diagram Explorer block cannot specify both 'masks' and 'masksPath'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");

                var masksPath = !string.IsNullOrWhiteSpace(b.MasksPath) ? b.MasksPath.Trim() : (string.IsNullOrWhiteSpace(b.Masks) ? null : b.Masks.Trim());
                var hotspots = new List<DiagramHotspot>();
                if (b.Hotspots is not null)
                {
                    for (int hIdx = 0; hIdx < b.Hotspots.Count; hIdx++)
                    {
                        var hs = b.Hotspots[hIdx];
                        if (hs is null)
                            return new Error("Json.InvalidSpec", $"Hotspot at '{pathPrefix}[{i}].hotspots[{hIdx}]' cannot be null.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].hotspots[{hIdx}]");

                        double bx = 0, by = 0, bw = 0, bh = 0;
                        bool hasInlineCoords = hs.Rect is not null || hs.X.HasValue || hs.Y.HasValue || hs.W.HasValue || hs.H.HasValue;
                        if (masksPath is not null && hasInlineCoords)
                            return new Error("Json.InvalidSpec", $"Hotspot '{hs.Label}' cannot specify both 'masksPath' sidecar and inline coordinates.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].hotspots[{hIdx}]");

                        if (masksPath is null)
                        {
                            if (hs.Rect is not null)
                            {
                                if (hs.Rect.Count != 4)
                                    return new Error("Json.InvalidSpec", $"Hotspot at '{pathPrefix}[{i}].hotspots[{hIdx}]' 'rect' must contain 4 numbers [x,y,w,h].", ErrorType.Validation, Path: $"{pathPrefix}[{i}].hotspots[{hIdx}].rect");
                                bx = hs.Rect[0]; by = hs.Rect[1]; bw = hs.Rect[2]; bh = hs.Rect[3];
                            }
                            else if (hs.X.HasValue && hs.Y.HasValue && hs.W.HasValue && hs.H.HasValue)
                            {
                                bx = hs.X.Value; by = hs.Y.Value; bw = hs.W.Value; bh = hs.H.Value;
                            }
                            else
                            {
                                return new Error("Json.InvalidSpec", $"Hotspot at '{pathPrefix}[{i}].hotspots[{hIdx}]' requires 'rect: [x,y,w,h]' or 'x','y','w','h' when no masksPath is set.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].hotspots[{hIdx}]");
                            }
                        }

                        hotspots.Add(new DiagramHotspot(hIdx, hs.Label?.Trim() ?? string.Empty, hs.Note?.Trim() ?? string.Empty, bx, by, bw, bh));
                    }
                }

                var payload = new DiagramExplorePayload(
                    b.ImagePath?.Trim() ?? string.Empty,
                    string.IsNullOrWhiteSpace(b.Title) ? null : b.Title.Trim(),
                    string.IsNullOrWhiteSpace(b.Caption) ? null : b.Caption.Trim(),
                    hotspots,
                    masksPath);

                list.Add(new DeclarativeArticleBlockSpec(
                    "diagram-explore", null, null, b.Ref, b.Caption, b.Alt, null, b.Title,
                    PayloadJson: Phase1PayloadCodec.Serialize(payload)));
                continue;
            }

            if (b.Kind.Trim().Equals("reveal", StringComparison.OrdinalIgnoreCase))
            {
                if (hasTableFields || hasDiagramFields || !string.IsNullOrWhiteSpace(b.Text) ||
                    !string.IsNullOrWhiteSpace(b.ImagePath) || !string.IsNullOrWhiteSpace(b.Caption) ||
                    !string.IsNullOrWhiteSpace(b.Alt) || !string.IsNullOrWhiteSpace(b.Tone))
                {
                    return new Error("Json.InvalidFieldForType", "Article reveal block specifies fields belonging to another block type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                }
                if (b.Steps is not null && b.Steps.Any(s => s is null))
                    return new Error("Json.InvalidSpec", $"Reveal block at '{pathPrefix}[{i}]' contains a null step.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].steps");

                var steps = (b.Steps ?? [])
                    .Select((s, sIdx) => new RevealStep(sIdx, s!.Heading?.Trim() ?? string.Empty, s.Body?.Trim() ?? string.Empty))
                    .ToList();
                var payload = new RevealBlockPayload(
                    string.IsNullOrWhiteSpace(b.Title) ? null : b.Title.Trim(),
                    b.ShowFirst ?? false,
                    steps);

                list.Add(new DeclarativeArticleBlockSpec(
                    "reveal", null, null, b.Ref, null, null, null, b.Title,
                    PayloadJson: Phase1PayloadCodec.Serialize(payload)));
                continue;
            }

            if (hasTableFields || hasDiagramFields || hasRevealFields)
                return new Error("Json.InvalidFieldForType", $"Article block kind '{b.Kind}' cannot specify table, diagram-explore, or reveal fields.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");

            list.Add(new DeclarativeArticleBlockSpec(b.Kind, b.Text, b.ImagePath, b.Ref, b.Caption, b.Alt, b.Tone, b.Title));
        }
        return Result<IReadOnlyList<DeclarativeArticleBlockSpec>?>.Success(list);
    }

    private static Result<IReadOnlyList<JsonQuestionSpec>?> MapQuestions(List<ApplyQuestionDto?>? rawQuestions, string pathPrefix, int caseDepth = 0)
    {
        if (rawQuestions is null) return Result<IReadOnlyList<JsonQuestionSpec>?>.Success(null);
        var list = new List<JsonQuestionSpec>(rawQuestions.Count);
        for (int i = 0; i < rawQuestions.Count; i++)
        {
            var q = rawQuestions[i];
            if (q is null || string.IsNullOrWhiteSpace(q.Type))
                return new Error("Json.InvalidQuestion", $"Question at index {i} must be a non-null object with a 'type' field.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
            if (q.Options is not null && q.Options.Any(o => o is null))
                return new Error("Json.InvalidQuestion", $"Question '{q.Type}' contains a null option entry.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].options");

            var options = (q.Options ?? [])
                .Select((o, idx) => new JsonMcqOptionSpec(o!.Index > 0 ? o.Index : idx + 1, o.Text, o.ImagePath, o.IsCorrect))
                .ToList();

            static JsonFlashcardSideSpec? MapSide(ApplySideDto? s) =>
                s is null ? null : new JsonFlashcardSideSpec(s.Text, s.ImagePath, s.Position ?? s.TextPosition);

            static string? AsString(JsonElement? el) =>
                el is { ValueKind: JsonValueKind.String } ? el.Value.GetString() :
                el is { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } ? el.Value.GetRawText() : null;

            bool hasMcqOnly = q.MaxOptions.HasValue || q.IsMultiCorrect || options.Count > 0;
            bool hasImagePath = !string.IsNullOrWhiteSpace(q.ImagePath);
            bool hasFlashcardOnly = q.Front is not null || q.Back is not null;
            bool hasRecallFlags = q.Fuzzy.HasValue || q.CaseSensitive.HasValue;
            bool hasPlaceholder = q.Placeholder is not null;
            bool hasArOnly = q.Assertion is not null || q.Reason is not null || q.Scheme is not null;
            bool hasAnswer = q.Answer.HasValue && q.Answer.Value.ValueKind != JsonValueKind.Null;
            bool hasSpotOnly = q.RequireFix.HasValue || q.NoError.HasValue;
            bool hasTableShape = q.Headers is not null || q.Alignments is not null || q.Rows is not null;
            bool hasMode = q.Mode is not null;
            bool hasDiffOnly = q.Bank.HasValue || hasTableShape;
            bool hasMechOnly = q.Steps is not null;
            bool hasRelabelOnly = q.Style is not null || q.Masks is not null || q.MasksPath is not null ||
                                  q.Include is not null || q.Boxes is not null;
            bool hasPrompt = q.Prompt is not null;
            bool hasDistractors = q.Distractors is not null;
            bool hasClassifyOnly = q.Buckets is not null || q.Items is not null;
            bool hasMatchOnly = q.Pairs is not null;
            bool hasNumericOnly = (q.Tolerance.HasValue && q.Tolerance.Value.ValueKind != JsonValueKind.Null) || q.ToleranceMode is not null || q.Unit is not null;
            bool hasCaseOnly = q.Stem is not null || q.Questions is not null || q.Caption is not null;

            if (Quiz.TryParseQuestionType(q.Type, out var qType) &&
                qType is QuestionType.FillBlank or QuestionType.AssertionReason or QuestionType.SpotError
                    or QuestionType.Differentiation or QuestionType.Mechanism or QuestionType.RelabelImage
                    or QuestionType.Classify or QuestionType.Match or QuestionType.Numeric or QuestionType.CaseGroup)
            {
                if (qType == QuestionType.CaseGroup && caseDepth > 0)
                    return new Error("Case.NestedCaseNotAllowed", "Nested 'case' sub-questions inside 'case' are not allowed.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");

                string payloadJson;
                IReadOnlyList<JsonQuestionSpec>? subQuestionSpecs = null;

                if (qType == QuestionType.FillBlank)
                {
                    if (hasMcqOnly || hasImagePath || hasFlashcardOnly || hasArOnly || hasAnswer || hasSpotOnly || q.AllowNoError.HasValue || hasMode || hasDiffOnly || hasMechOnly || hasRelabelOnly || hasPrompt || hasDistractors || hasClassifyOnly || hasMatchOnly || hasNumericOnly || hasCaseOnly || q.Title is not null)
                        return new Error("Json.InvalidFieldForType", "Fill-in-the-blank question specifies fields belonging to another question type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                    var template = q.Text ?? string.Empty;
                    var slotsRes = Phase1PayloadRules.ParseFillBlankTemplate(template);
                    var payload = new FillBlankPayload(template, slotsRes.IsSuccess ? slotsRes.Value : [], q.Fuzzy ?? true, q.CaseSensitive ?? false, string.IsNullOrWhiteSpace(q.Explanation) ? null : q.Explanation.Trim(), string.IsNullOrWhiteSpace(q.Placeholder) ? null : q.Placeholder.Trim());
                    payloadJson = Phase1PayloadCodec.Serialize(payload);
                }
                else if (qType == QuestionType.AssertionReason)
                {
                    if (hasMcqOnly || hasImagePath || hasFlashcardOnly || hasRecallFlags || hasPlaceholder || hasSpotOnly || q.AllowNoError.HasValue || hasMode || hasDiffOnly || hasMechOnly || hasRelabelOnly || hasPrompt || hasDistractors || hasClassifyOnly || hasMatchOnly || hasNumericOnly || hasCaseOnly || q.Title is not null || !string.IsNullOrWhiteSpace(q.Text))
                        return new Error("Json.InvalidFieldForType", "Assertion-Reason question cannot specify 'text' or fields belonging to another question type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                    if (q.Answer is { ValueKind: JsonValueKind.Number })
                        return new Error("Json.InvalidQuestion", "Assertion-Reason 'answer' must be a code string (e.g. 'both-explain').", ErrorType.Validation, Path: $"{pathPrefix}[{i}].answer");
                    var ans = Phase1PayloadRules.NormalizeAssertionReasonAnswer(AsString(q.Answer));
                    var sch = Phase1PayloadRules.ResolveAssertionReasonScheme(q.Scheme, ans);
                    var payload = new AssertionReasonPayload(q.Assertion?.Trim() ?? string.Empty, q.Reason?.Trim() ?? string.Empty, ans, sch, string.IsNullOrWhiteSpace(q.Explanation) ? null : q.Explanation.Trim());
                    payloadJson = Phase1PayloadCodec.Serialize(payload);
                }
                else if (qType == QuestionType.SpotError)
                {
                    if (hasMcqOnly || hasFlashcardOnly || hasRecallFlags || hasPlaceholder || hasArOnly || hasAnswer || hasMode || hasDiffOnly || hasMechOnly || hasRelabelOnly || hasPrompt || hasDistractors || hasClassifyOnly || hasMatchOnly || hasNumericOnly || hasCaseOnly || q.Title is not null)
                        return new Error("Json.InvalidFieldForType", "Spot-the-error question specifies fields belonging to another question type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                    var template = q.Text ?? string.Empty;
                    var segsRes = Phase1PayloadRules.ParseSpotErrorTemplate(template);
                    bool noError = q.NoError ?? false;
                    bool allowNoError = q.AllowNoError ?? noError;
                    var payload = new SpotErrorPayload(template, segsRes.IsSuccess ? segsRes.Value : [], q.RequireFix ?? false, allowNoError, noError, string.IsNullOrWhiteSpace(q.Explanation) ? null : q.Explanation.Trim(), string.IsNullOrWhiteSpace(q.ImagePath) ? null : q.ImagePath.Trim());
                    payloadJson = Phase1PayloadCodec.Serialize(payload);
                }
                else if (qType == QuestionType.Differentiation)
                {
                    if (hasMcqOnly || hasImagePath || hasFlashcardOnly || hasPlaceholder || hasArOnly || hasAnswer || hasSpotOnly || hasMechOnly || hasRelabelOnly || hasPrompt || hasDistractors || hasClassifyOnly || hasMatchOnly || hasNumericOnly || hasCaseOnly || !string.IsNullOrWhiteSpace(q.Text))
                        return new Error("Json.InvalidFieldForType", "Differentiation question cannot specify 'text' or fields belonging to another question type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                    if (q.Headers is not null && q.Headers.Any(h => h is null))
                        return new Error("Json.InvalidSpec", $"Differentiation question at '{pathPrefix}[{i}]' contains a null entry in 'headers'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].headers");
                    if (q.Alignments is not null && q.Alignments.Any(a => a is null))
                        return new Error("Json.InvalidSpec", $"Differentiation question at '{pathPrefix}[{i}]' contains a null entry in 'alignments'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].alignments");
                    if (q.Rows is not null)
                    {
                        for (int rIdx = 0; rIdx < q.Rows.Count; rIdx++)
                        {
                            if (q.Rows[rIdx] is null || q.Rows[rIdx]!.Any(c => c is null))
                                return new Error("Json.InvalidSpec", $"Differentiation question at '{pathPrefix}[{i}]' contains a null row or cell in 'rows[{rIdx}]'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].rows[{rIdx}]");
                        }
                    }

                    var mode = Phase2PayloadRules.NormalizeDiffMode(q.Mode);
                    var headers = (IReadOnlyList<string>)(q.Headers?.Select(h => h!).ToList() ?? []);
                    var alignments = q.Alignments is { Count: > 0 }
                        ? (IReadOnlyList<string>)q.Alignments.Select(a => string.IsNullOrWhiteSpace(a) ? "left" : a!.Trim().ToLowerInvariant()).ToList()
                        : Enumerable.Repeat("left", headers.Count).ToList();
                    var rows = (IReadOnlyList<IReadOnlyList<string>>)(q.Rows?.Select(r => (IReadOnlyList<string>)r!.Select(c => c!).ToList()).ToList() ?? []);
                    var specialRes = Phase2PayloadRules.ParseDifferentiationTable(mode, headers, rows);
                    var payload = new DifferentiationPayload(
                        mode,
                        string.IsNullOrWhiteSpace(q.Title) ? null : q.Title.Trim(),
                        q.Bank ?? false,
                        q.Fuzzy ?? true,
                        q.CaseSensitive ?? false,
                        q.AllowNoError ?? false,
                        headers,
                        alignments,
                        rows,
                        specialRes.IsSuccess ? specialRes.Value : [],
                        string.IsNullOrWhiteSpace(q.Explanation) ? null : q.Explanation.Trim());
                    payloadJson = Phase1PayloadCodec.Serialize(payload);
                }
                else if (qType == QuestionType.Mechanism)
                {
                    if (hasMcqOnly || hasImagePath || hasFlashcardOnly || hasRecallFlags || hasPlaceholder || hasArOnly || hasAnswer || hasSpotOnly || q.AllowNoError.HasValue || hasMode || hasDiffOnly || hasRelabelOnly || hasPrompt || hasDistractors || hasClassifyOnly || hasMatchOnly || hasNumericOnly || hasCaseOnly || !string.IsNullOrWhiteSpace(q.Text))
                        return new Error("Json.InvalidFieldForType", "Mechanism question cannot specify 'text' or fields belonging to another question type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                    if (q.Steps is not null && q.Steps.Any(s => s is null))
                        return new Error("Json.InvalidSpec", $"Mechanism question at '{pathPrefix}[{i}]' contains a null entry in 'steps'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].steps");

                    var steps = (q.Steps ?? [])
                        .Select((s, sIdx) => new MechanismStep(
                            s!.StepId > 0 ? s.StepId : sIdx + 1,
                            s.IsDistractor ? 0 : s.OrderGroup,
                            s.Text?.Trim() ?? string.Empty,
                            s.IsDistractor))
                        .ToList();
                    var payload = new MechanismPayload(
                        string.IsNullOrWhiteSpace(q.Title) ? null : q.Title.Trim(),
                        steps,
                        string.IsNullOrWhiteSpace(q.Explanation) ? null : q.Explanation.Trim());
                    payloadJson = Phase1PayloadCodec.Serialize(payload);
                }
                else if (qType == QuestionType.RelabelImage)
                {
                    if (hasMcqOnly || hasFlashcardOnly || hasPlaceholder || hasArOnly || hasAnswer || hasSpotOnly || q.AllowNoError.HasValue || hasDiffOnly || hasMechOnly || hasClassifyOnly || hasMatchOnly || hasNumericOnly || hasCaseOnly || q.Title is not null || !string.IsNullOrWhiteSpace(q.Text))
                        return new Error("Json.InvalidFieldForType", "Relabel question cannot specify 'text' or fields belonging to another question type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                    if (!string.IsNullOrWhiteSpace(q.Masks) && !string.IsNullOrWhiteSpace(q.MasksPath))
                        return new Error("Json.InvalidQuestion", "Relabel question cannot specify both 'masks' and 'masksPath'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                    var masksPath = !string.IsNullOrWhiteSpace(q.MasksPath) ? q.MasksPath.Trim() : (string.IsNullOrWhiteSpace(q.Masks) ? null : q.Masks.Trim());
                    if (masksPath is not null && q.Boxes is { Count: > 0 })
                        return new Error("Json.InvalidQuestion", "Relabel question cannot specify both 'masks'/'masksPath' sidecar and inline 'boxes'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                    if (q.Include is not null && q.Include.Any(x => x is null))
                        return new Error("Json.InvalidSpec", $"Relabel question at '{pathPrefix}[{i}]' contains a null entry in 'include'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].include");
                    if (q.Distractors is not null && q.Distractors.Any(x => x is null))
                        return new Error("Json.InvalidSpec", $"Relabel question at '{pathPrefix}[{i}]' contains a null entry in 'distractors'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].distractors");

                    var boxes = new List<RelabelBox>();
                    if (q.Boxes is not null)
                    {
                        for (int bIdx = 0; bIdx < q.Boxes.Count; bIdx++)
                        {
                            var b = q.Boxes[bIdx];
                            if (b is null)
                                return new Error("Json.InvalidSpec", $"Relabel question at '{pathPrefix}[{i}]' contains a null entry in 'boxes'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].boxes[{bIdx}]");
                            if (b.Aliases is not null && b.Aliases.Any(a => a is null))
                                return new Error("Json.InvalidSpec", $"Relabel box at '{pathPrefix}[{i}].boxes[{bIdx}]' contains a null alias.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].boxes[{bIdx}].aliases");

                            double bx, by, bw, bh;
                            if (b.Rect is not null)
                            {
                                if (b.Rect.Count != 4)
                                    return new Error("Json.InvalidQuestion", $"Relabel box at '{pathPrefix}[{i}].boxes[{bIdx}]' 'rect' must contain exactly 4 numbers [x, y, w, h].", ErrorType.Validation, Path: $"{pathPrefix}[{i}].boxes[{bIdx}].rect");
                                bx = b.Rect[0]; by = b.Rect[1]; bw = b.Rect[2]; bh = b.Rect[3];
                            }
                            else if (b.X.HasValue && b.Y.HasValue && b.W.HasValue && b.H.HasValue)
                            {
                                bx = b.X.Value; by = b.Y.Value; bw = b.W.Value; bh = b.H.Value;
                            }
                            else
                            {
                                return new Error("Json.InvalidQuestion", $"Relabel box at '{pathPrefix}[{i}].boxes[{bIdx}]' must specify 'rect: [x,y,w,h]' or numeric 'x', 'y', 'w', 'h'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].boxes[{bIdx}]");
                            }

                            var aliases = (b.Aliases ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim()).ToList();
                            boxes.Add(new RelabelBox(bIdx + 1, b.Label?.Trim() ?? string.Empty, aliases, bx, by, bw, bh));
                        }
                    }

                    var payload = new RelabelImagePayload(
                        q.ImagePath?.Trim() ?? string.Empty,
                        string.IsNullOrWhiteSpace(q.Prompt) ? null : q.Prompt.Trim(),
                        string.IsNullOrWhiteSpace(q.Mode) ? "bank" : Phase2PayloadRules.NormalizeRelabelMode(q.Mode),
                        string.IsNullOrWhiteSpace(q.Style) ? "solid" : Phase2PayloadRules.NormalizeRelabelStyle(q.Style),
                        boxes,
                        (q.Distractors ?? []).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!.Trim()).ToList(),
                        q.Fuzzy ?? true,
                        q.CaseSensitive ?? false,
                        string.IsNullOrWhiteSpace(q.Explanation) ? null : q.Explanation.Trim(),
                        SourceMasksPath: masksPath,
                        IncludeLabels: q.Include?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList());
                    payloadJson = Phase1PayloadCodec.Serialize(payload);
                }
                else if (qType == QuestionType.Classify)
                {
                    if (hasMcqOnly || hasImagePath || hasFlashcardOnly || hasRecallFlags || hasPlaceholder || hasArOnly || hasAnswer || hasSpotOnly || q.AllowNoError.HasValue || hasMode || hasDiffOnly || hasMechOnly || hasRelabelOnly || hasPrompt || hasDistractors || hasMatchOnly || hasNumericOnly || hasCaseOnly || !string.IsNullOrWhiteSpace(q.Text))
                        return new Error("Json.InvalidFieldForType", "Classify question specifies fields belonging to another question type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");

                    var buckets = new List<ClassifyBucket>();
                    if (q.Buckets is not null)
                    {
                        for (int bIdx = 0; bIdx < q.Buckets.Count; bIdx++)
                        {
                            var be = q.Buckets[bIdx];
                            if (be.ValueKind == JsonValueKind.String)
                            {
                                buckets.Add(new ClassifyBucket(bIdx, be.GetString()?.Trim() ?? string.Empty));
                            }
                            else if (be.ValueKind == JsonValueKind.Object)
                            {
                                int bid = be.TryGetProperty("bucketId", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var idVal) ? idVal : bIdx;
                                string bName = be.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString()?.Trim() ?? string.Empty : string.Empty;
                                buckets.Add(new ClassifyBucket(bid, bName));
                            }
                            else
                            {
                                return new Error("Json.InvalidQuestion", $"Classify bucket at '{pathPrefix}[{i}].buckets[{bIdx}]' must be string or object {{bucketId, name}}.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].buckets[{bIdx}]");
                            }
                        }
                    }

                    var items = new List<ClassifyItem>();
                    if (q.Items is not null)
                    {
                        for (int itIdx = 0; itIdx < q.Items.Count; itIdx++)
                        {
                            var it = q.Items[itIdx];
                            if (it is null)
                                return new Error("Json.InvalidQuestion", $"Classify item at '{pathPrefix}[{i}].items[{itIdx}]' cannot be null.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].items[{itIdx}]");
                            int bid = it.BucketId ?? (string.IsNullOrWhiteSpace(it.Bucket)
                                ? -1
                                : buckets.FirstOrDefault(b => string.Equals(b.Name, it.Bucket.Trim(), StringComparison.OrdinalIgnoreCase))?.BucketId ?? -1);
                            items.Add(new ClassifyItem(itIdx, bid, it.Text?.Trim() ?? string.Empty));
                        }
                    }

                    payloadJson = Phase1PayloadCodec.Serialize(new ClassifyPayload(
                        string.IsNullOrWhiteSpace(q.Title) ? null : q.Title.Trim(),
                        buckets,
                        items,
                        string.IsNullOrWhiteSpace(q.Explanation) ? null : q.Explanation.Trim()));
                }
                else if (qType == QuestionType.Match)
                {
                    if (hasMcqOnly || hasImagePath || hasFlashcardOnly || hasRecallFlags || hasPlaceholder || hasArOnly || hasAnswer || hasSpotOnly || q.AllowNoError.HasValue || hasMode || hasDiffOnly || hasMechOnly || hasRelabelOnly || hasPrompt || hasClassifyOnly || hasNumericOnly || hasCaseOnly || !string.IsNullOrWhiteSpace(q.Text))
                        return new Error("Json.InvalidFieldForType", "Match question specifies fields belonging to another question type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
                    if (q.Pairs is not null && q.Pairs.Any(p => p is null))
                        return new Error("Json.InvalidQuestion", $"Match question at '{pathPrefix}[{i}]' contains a null entry in 'pairs'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].pairs");
                    if (q.Distractors is not null && q.Distractors.Any(d => d is null))
                        return new Error("Json.InvalidQuestion", $"Match question at '{pathPrefix}[{i}]' contains a null entry in 'distractors'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].distractors");

                    var pairs = (q.Pairs ?? [])
                        .Select((p, pIdx) => new MatchPair(pIdx, p!.Left?.Trim() ?? string.Empty, p.Right?.Trim() ?? string.Empty))
                        .ToList();
                    var distractors = (q.Distractors ?? []).Select(d => d!.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    payloadJson = Phase1PayloadCodec.Serialize(new MatchPayload(
                        string.IsNullOrWhiteSpace(q.Title) ? null : q.Title.Trim(),
                        pairs,
                        distractors,
                        string.IsNullOrWhiteSpace(q.Explanation) ? null : q.Explanation.Trim()));
                }
                else if (qType == QuestionType.Numeric)
                {
                    if (hasMcqOnly || hasFlashcardOnly || hasRecallFlags || hasArOnly || hasSpotOnly || q.AllowNoError.HasValue || hasMode || hasDiffOnly || hasMechOnly || hasRelabelOnly || hasDistractors || hasClassifyOnly || hasMatchOnly || hasCaseOnly || q.Title is not null)
                        return new Error("Json.InvalidFieldForType", "Numeric question specifies fields belonging to another question type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");

                    double ansVal = double.NaN;
                    if (q.Answer is { ValueKind: JsonValueKind.Number } numEl && numEl.TryGetDouble(out var parsedAns))
                        ansVal = parsedAns;
                    else if (q.Answer is { ValueKind: JsonValueKind.String } strAns &&
                             double.TryParse(strAns.GetString()?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedStrAns))
                        ansVal = parsedStrAns;
                    else if (hasAnswer)
                        return new Error("Json.InvalidQuestion", $"Numeric question 'answer' must be a number.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].answer");

                    var rawTolStr = q.Tolerance is { ValueKind: JsonValueKind.String or JsonValueKind.Number } ? AsString(q.Tolerance) : null;
                    if (!Phase3PayloadRules.TryParseToleranceSpec(rawTolStr, q.ToleranceMode, out var tolVal, out var tolMode))
                        tolVal = -1;

                    var promptText = !string.IsNullOrWhiteSpace(q.Prompt) ? q.Prompt.Trim() : (q.Text?.Trim() ?? string.Empty);
                    payloadJson = Phase1PayloadCodec.Serialize(new NumericPayload(
                        promptText,
                        ansVal,
                        tolVal,
                        tolMode,
                        string.IsNullOrWhiteSpace(q.Unit) ? null : q.Unit.Trim(),
                        string.IsNullOrWhiteSpace(q.Placeholder) ? null : q.Placeholder.Trim(),
                        string.IsNullOrWhiteSpace(q.Explanation) ? null : q.Explanation.Trim(),
                        string.IsNullOrWhiteSpace(q.ImagePath) ? null : q.ImagePath.Trim()));
                }
                else
                {
                    if (hasMcqOnly || hasFlashcardOnly || hasRecallFlags || hasPlaceholder || hasArOnly || hasAnswer || hasSpotOnly || q.AllowNoError.HasValue || hasMode || hasDiffOnly || hasMechOnly || hasRelabelOnly || hasPrompt || hasDistractors || hasClassifyOnly || hasMatchOnly || hasNumericOnly || !string.IsNullOrWhiteSpace(q.Text))
                        return new Error("Json.InvalidFieldForType", "Case question specifies fields belonging to another question type.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");

                    var subRes = MapQuestions(q.Questions, $"{pathPrefix}[{i}].questions", caseDepth + 1);
                    if (!subRes.IsSuccess) return subRes.Error!;
                    subQuestionSpecs = subRes.Value ?? [];

                    payloadJson = Phase1PayloadCodec.Serialize(new CaseGroupPayload(
                        string.IsNullOrWhiteSpace(q.Title) ? null : q.Title.Trim(),
                        q.Stem?.Trim() ?? string.Empty,
                        string.IsNullOrWhiteSpace(q.ImagePath) ? null : q.ImagePath.Trim(),
                        string.IsNullOrWhiteSpace(q.Caption) ? null : q.Caption.Trim(),
                        [],
                        string.IsNullOrWhiteSpace(q.Explanation) ? null : q.Explanation.Trim()));
                }

                var outerImagePath = qType is QuestionType.RelabelImage or QuestionType.SpotError or QuestionType.Numeric or QuestionType.CaseGroup ? null : q.ImagePath;
                list.Add(new JsonQuestionSpec(q.Type, null, q.MaxOptions, q.IsMultiCorrect, options, MapSide(q.Front), MapSide(q.Back), q.LayoutOverrides, outerImagePath, q.Explanation, q.Ref, payloadJson, subQuestionSpecs));
                continue;
            }

            if (hasRecallFlags || hasPlaceholder || hasArOnly || hasAnswer || hasSpotOnly || q.AllowNoError.HasValue || hasMode || hasDiffOnly || hasMechOnly || hasRelabelOnly || hasPrompt || hasDistractors || hasClassifyOnly || hasMatchOnly || hasNumericOnly || hasCaseOnly || q.Title is not null)
                return new Error("Json.InvalidFieldForType", $"Question type '{q.Type}' cannot specify specialized payload fields.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");

            list.Add(new JsonQuestionSpec(q.Type, q.Text, q.MaxOptions, q.IsMultiCorrect, options, MapSide(q.Front), MapSide(q.Back), q.LayoutOverrides, q.ImagePath, q.Explanation, q.Ref));
        }
        return Result<IReadOnlyList<JsonQuestionSpec>?>.Success(list);
    }

    private static Result<IReadOnlyList<DeclarativeLinkCardSpec>?> MapLinks(List<ApplyLinkCardDto?>? rawLinks, string pathPrefix)
    {
        if (rawLinks is null) return Result<IReadOnlyList<DeclarativeLinkCardSpec>?>.Success(null);
        var list = new List<DeclarativeLinkCardSpec>(rawLinks.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rawLinks.Count; i++)
        {
            var l = rawLinks[i];
            if (l is null || string.IsNullOrWhiteSpace(l.Id))
                return new Error("Json.InvalidSpec", $"Link card at '{pathPrefix}[{i}]' must specify a non-empty 'id'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
            var idRes = ArticleLinkRules.ValidateAndNormalizeCardId(l.Id);
            if (!idRes.IsSuccess || string.IsNullOrWhiteSpace(idRes.Value))
                return idRes.Error ?? new Error("Json.InvalidSpec", $"Invalid link card id '{l.Id}' at '{pathPrefix}[{i}]'.", ErrorType.Validation, Path: $"{pathPrefix}[{i}].id");
            if (!seen.Add(idRes.Value))
                return new Error("Json.DuplicateLinkId", $"Duplicate link card id '{idRes.Value}' in '{pathPrefix}'.", ErrorType.Conflict, Path: $"{pathPrefix}[{i}].id");
            var text = string.IsNullOrWhiteSpace(l.Text) ? null : l.Text.Trim();
            var img = string.IsNullOrWhiteSpace(l.ImagePath) ? null : l.ImagePath.Trim();
            if (text is null && img is null)
                return new Error("Json.InvalidSpec", $"Link card '{idRes.Value}' at '{pathPrefix}[{i}]' must specify 'text', 'imagePath', or both.", ErrorType.Validation, Path: $"{pathPrefix}[{i}]");
            list.Add(new DeclarativeLinkCardSpec(idRes.Value, string.IsNullOrWhiteSpace(l.Title) ? null : l.Title.Trim(), text, img));
        }
        return Result<IReadOnlyList<DeclarativeLinkCardSpec>?>.Success(list);
    }

    private sealed class ApplySpecDto
    {
        public string? Kind { get; set; }
        public string? Ref { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Title { get; set; }
        public string? QuizType { get; set; }
        public List<string>? Order { get; set; }
        public List<ApplyComponentDto?>? Components { get; set; }
        public List<ApplyArticleBlockDto?>? Blocks { get; set; }
        public List<ApplyQuestionDto?>? Questions { get; set; }
        public List<ApplyLinkCardDto?>? Links { get; set; }
        public Dictionary<string, string>? LayoutOverrides { get; set; }
    }

    private sealed class ApplyComponentDto
    {
        public string? Kind { get; set; }
        public string? Ref { get; set; }
        public string? Title { get; set; }
        public string? QuizType { get; set; }
        public List<ApplyArticleBlockDto?>? Blocks { get; set; }
        public List<ApplyQuestionDto?>? Questions { get; set; }
        public List<ApplyLinkCardDto?>? Links { get; set; }
        public Dictionary<string, string>? LayoutOverrides { get; set; }
    }

    private sealed class ApplyLinkCardDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Text { get; set; }
        public string? ImagePath { get; set; }
    }

    private sealed class ApplyArticleBlockDto
    {
        public string? Kind { get; set; }
        public string? Ref { get; set; }
        public string? Text { get; set; }
        public string? ImagePath { get; set; }
        public string? Caption { get; set; }
        public string? Alt { get; set; }
        public string? Tone { get; set; }
        public string? Title { get; set; }
        public bool? StickyFirstCol { get; set; }
        public List<int>? HideableCols { get; set; }
        public List<string?>? Headers { get; set; }
        public List<string?>? Alignments { get; set; }
        public List<List<string?>?>? Rows { get; set; }
        public string? Footnote { get; set; }
        public string? Masks { get; set; }
        public string? MasksPath { get; set; }
        public List<ApplyDiagramHotspotDto?>? Hotspots { get; set; }
        public bool? ShowFirst { get; set; }
        public List<ApplyRevealStepDto?>? Steps { get; set; }
    }

    private sealed class ApplyQuestionDto
    {
        public string? Type { get; set; }
        public string? Ref { get; set; }
        public string? Text { get; set; }
        public string? ImagePath { get; set; }
        public string? Explanation { get; set; }
        public int? MaxOptions { get; set; }
        public bool IsMultiCorrect { get; set; }
        public List<ApplyOptionDto?>? Options { get; set; }
        public ApplySideDto? Front { get; set; }
        public ApplySideDto? Back { get; set; }
        public Dictionary<string, string>? LayoutOverrides { get; set; }
        public bool? Fuzzy { get; set; }
        public bool? CaseSensitive { get; set; }
        public string? Placeholder { get; set; }
        public string? Assertion { get; set; }
        public string? Reason { get; set; }
        public JsonElement? Answer { get; set; }
        public string? Scheme { get; set; }
        public bool? RequireFix { get; set; }
        public bool? AllowNoError { get; set; }
        public bool? NoError { get; set; }
        public string? Mode { get; set; }
        public bool? Bank { get; set; }
        public string? Title { get; set; }
        public List<string?>? Headers { get; set; }
        public List<string?>? Alignments { get; set; }
        public List<List<string?>?>? Rows { get; set; }
        public List<ApplyMechanismStepDto?>? Steps { get; set; }
        public string? Prompt { get; set; }
        public string? Style { get; set; }
        public string? Masks { get; set; }
        public string? MasksPath { get; set; }
        public List<string?>? Include { get; set; }
        public List<ApplyRelabelBoxDto?>? Boxes { get; set; }
        public List<string?>? Distractors { get; set; }
        public List<JsonElement>? Buckets { get; set; }
        public List<ApplyClassifyItemDto?>? Items { get; set; }
        public List<ApplyMatchPairDto?>? Pairs { get; set; }
        public JsonElement? Tolerance { get; set; }
        public string? ToleranceMode { get; set; }
        public string? Unit { get; set; }
        public string? Stem { get; set; }
        public string? Caption { get; set; }
        public List<ApplyQuestionDto?>? Questions { get; set; }
    }

    private sealed class ApplyOptionDto
    {
        public int Index { get; set; }
        public string? Text { get; set; }
        public string? ImagePath { get; set; }
        public bool IsCorrect { get; set; }
    }

    private sealed class ApplySideDto
    {
        public string? Text { get; set; }
        public string? ImagePath { get; set; }
        public string? Position { get; set; }
        public string? TextPosition { get; set; }
    }

    private sealed class ApplyMechanismStepDto
    {
        public int StepId { get; set; }
        public int OrderGroup { get; set; }
        public string? Text { get; set; }
        public bool IsDistractor { get; set; }
    }

    private sealed class ApplyRelabelBoxDto
    {
        public string? Label { get; set; }
        public List<string?>? Aliases { get; set; }
        public List<double>? Rect { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? W { get; set; }
        public double? H { get; set; }
    }

    private sealed class ApplyDiagramHotspotDto
    {
        public string? Label { get; set; }
        public string? Note { get; set; }
        public List<double>? Rect { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? W { get; set; }
        public double? H { get; set; }
    }

    private sealed class ApplyRevealStepDto
    {
        public string? Heading { get; set; }
        public string? Body { get; set; }
    }

    private sealed class ApplyClassifyItemDto
    {
        public int? BucketId { get; set; }
        public string? Bucket { get; set; }
        public string? Text { get; set; }
    }

    private sealed class ApplyMatchPairDto
    {
        public string? Left { get; set; }
        public string? Right { get; set; }
    }
}
