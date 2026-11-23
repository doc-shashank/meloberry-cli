using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;

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
        "layoutOverrides", "ref", "text", "imagePath", "caption", "alt", "tone",
        "type", "explanation", "maxOptions", "isMultiCorrect", "options", "index", "isCorrect",
        "front", "back", "position", "textPosition"
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
        if (File.Exists(sessionMdPath))
        {
            var raw = await File.ReadAllTextAsync(sessionMdPath, ct);
            var parsed = MarkdownSpecParser.ParseDocument(raw, sessionMdPath, isSessionManifest: true);
            if (!parsed.IsSuccess) return parsed.Error!;
            sessionName = parsed.Value.Name ?? parsed.Value.Title;
            sessionDescription = parsed.Value.Description;
            explicitOrder = parsed.Value.Order;
        }
        else if (File.Exists(sessionJsonPath))
        {
            var raw = await File.ReadAllTextAsync(sessionJsonPath, ct);
            var parsed = ParseSpec(raw, sessionJsonPath);
            if (!parsed.IsSuccess) return parsed.Error!;
            sessionName = parsed.Value.Name ?? parsed.Value.Title;
            sessionDescription = parsed.Value.Description;
            manifestComponents = parsed.Value.Components;
        }

        var discoveredFiles = Directory.EnumerateFiles(fullDir)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith('_')) return false;
                if (string.Equals(name, "session.json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase))
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
                foreach (var c in spec.Components)
                    components.Add(c with { SourceDirectory = fullDir });
            }
            else if (spec.Blocks is { Count: > 0 } || string.Equals(spec.Kind, "article", StringComparison.OrdinalIgnoreCase))
            {
                components.Add(new DeclarativeComponentSpec(
                    "article",
                    spec.Title ?? spec.Name ?? MarkdownSpecParser.HumanizeFileStem(file),
                    null,
                    spec.Blocks,
                    null,
                    spec.LayoutOverrides,
                    SourceDirectory: fullDir));
            }
            else if (spec.Questions is { Count: > 0 } || spec.Kind?.ToLowerInvariant() is "quiz" or "mcq" or "flashcard" or "mixed")
            {
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
                    SourceDirectory: fullDir));
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

                    components.Add(new DeclarativeComponentSpec(c.Kind, c.Title, c.QuizType, blocksRes.Value, qRes.Value, c.LayoutOverrides, c.Ref));
                }
            }

            var rootBlocksRes = MapBlocks(dto.Blocks, "blocks");
            if (!rootBlocksRes.IsSuccess) return rootBlocksRes.Error!;

            var rootQuestionsRes = MapQuestions(dto.Questions, "questions");
            if (!rootQuestionsRes.IsSuccess) return rootQuestionsRes.Error!;

            return new DeclarativeApplySpec(
                dto.Kind,
                dto.Name,
                dto.Description,
                dto.Title,
                dto.QuizType,
                dto.Components is null ? null : components,
                rootBlocksRes.Value,
                rootQuestionsRes.Value,
                dto.LayoutOverrides);
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
            list.Add(new DeclarativeArticleBlockSpec(b.Kind, b.Text, b.ImagePath, b.Ref, b.Caption, b.Alt, b.Tone, b.Title));
        }
        return Result<IReadOnlyList<DeclarativeArticleBlockSpec>?>.Success(list);
    }

    private static Result<IReadOnlyList<JsonQuestionSpec>?> MapQuestions(List<ApplyQuestionDto?>? rawQuestions, string pathPrefix)
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

            list.Add(new JsonQuestionSpec(q.Type, q.Text, q.MaxOptions, q.IsMultiCorrect, options, MapSide(q.Front), MapSide(q.Back), q.LayoutOverrides, q.ImagePath, q.Explanation, q.Ref));
        }
        return Result<IReadOnlyList<JsonQuestionSpec>?>.Success(list);
    }

    private sealed class ApplySpecDto
    {
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Title { get; set; }
        public string? QuizType { get; set; }
        public List<ApplyComponentDto?>? Components { get; set; }
        public List<ApplyArticleBlockDto?>? Blocks { get; set; }
        public List<ApplyQuestionDto?>? Questions { get; set; }
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
        public Dictionary<string, string>? LayoutOverrides { get; set; }
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
}
