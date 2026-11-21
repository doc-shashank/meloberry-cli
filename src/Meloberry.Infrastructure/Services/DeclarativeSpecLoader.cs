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
        "layoutOverrides", "ref", "text", "imagePath", "type", "explanation", "maxOptions",
        "isMultiCorrect", "options", "index", "isCorrect", "front", "back", "position", "textPosition"
    ];

    [GeneratedRegex(@"property\s+'(?<prop>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex UnmappedPropertyRegex();

    public async Task<Result<DeclarativeApplySpec>> LoadAsync(string jsonFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jsonFilePath) || !File.Exists(jsonFilePath))
            return new Error("Json.FileNotFound", $"JSON file '{jsonFilePath}' was not found.", ErrorType.NotFound);

        var rawJson = await File.ReadAllTextAsync(jsonFilePath, cancellationToken);
        return ParseSpec(rawJson, jsonFilePath);
    }

    public Task<Result<DeclarativeApplySpec>> LoadFromTextAsync(string rawJson, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParseSpec(rawJson, "<stdin>"));

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
            list.Add(new DeclarativeArticleBlockSpec(b.Kind, b.Text, b.ImagePath, b.Ref));
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
