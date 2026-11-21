using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Analysis;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;

namespace Meloberry.Infrastructure.Services;

public sealed class AnalysisReportStore : IAnalysisReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<Result<IReadOnlyList<SessionAnalysisReport>>> LoadReportsAsync(string webAppOrReportPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(webAppOrReportPath.Trim());
        if (File.Exists(fullPath))
        {
            var single = await DeserializeReportFileAsync(fullPath, cancellationToken);
            return single.IsSuccess ? new List<SessionAnalysisReport> { single.Value } : single.Error!;
        }
        if (!Directory.Exists(fullPath)) return AnalysisErrors.WebAppOrReportNotFound(webAppOrReportPath);

        var candidateFiles = new List<string>();
        var reportsDir = Path.Combine(fullPath, "reports");
        if (Directory.Exists(reportsDir))
            candidateFiles.AddRange(Directory.EnumerateFiles(reportsDir, "*.analysis.json", SearchOption.TopDirectoryOnly));
        candidateFiles.AddRange(Directory.EnumerateFiles(fullPath, "*.analysis.json", SearchOption.TopDirectoryOnly));

        var distinctFiles = candidateFiles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctFiles.Count == 0) return AnalysisErrors.NoReportsFound(fullPath);

        var loaded = new List<SessionAnalysisReport>(distinctFiles.Count);
        foreach (var file in distinctFiles)
        {
            var itemRes = await DeserializeReportFileAsync(file, cancellationToken);
            if (!itemRes.IsSuccess) return itemRes.Error!;
            loaded.Add(itemRes.Value);
        }
        return loaded;
    }

    public async Task<Result<SavedAnalysisFiles>> SaveReportToWebAppDirectoryAsync(
        string webAppDirectory, string rawReportJson, string? rawMarkdown = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webAppDirectory) || !Directory.Exists(webAppDirectory))
            return AnalysisErrors.WebAppOrReportNotFound(webAppDirectory);

        SessionAnalysisReport? parsed;
        try { parsed = JsonSerializer.Deserialize<SessionAnalysisReport>(rawReportJson, JsonOptions); }
        catch (JsonException ex) { return AnalysisErrors.InvalidReportFormat("POST /api/analysis", ex.Message); }

        if (!IsValidReport(parsed))
            return AnalysisErrors.InvalidReportFormat("POST /api/analysis", "Missing required workspaceId, summary, or slides fields.");

        var reportsDir = Path.Combine(Path.GetFullPath(webAppDirectory), "reports");
        Directory.CreateDirectory(reportsDir);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var safeId = parsed!.WorkspaceId;
        foreach (var ch in Path.GetInvalidFileNameChars()) safeId = safeId.Replace(ch, '_');

        var jsonPath = Path.Combine(reportsDir, $"{safeId}_analysis_{stamp}.analysis.json");
        var mdPath = Path.Combine(reportsDir, $"{safeId}_analysis_{stamp}.analysis.md");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(parsed, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(mdPath, string.IsNullOrWhiteSpace(rawMarkdown) ? parsed.ToMarkdownReport() : rawMarkdown, cancellationToken);
        return new SavedAnalysisFiles(jsonPath, mdPath, parsed);
    }

    private static async Task<Result<SessionAnalysisReport>> DeserializeReportFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var report = await JsonSerializer.DeserializeAsync<SessionAnalysisReport>(stream, JsonOptions, cancellationToken);
            return IsValidReport(report)
                ? report!
                : AnalysisErrors.InvalidReportFormat(filePath, "Missing required workspaceId, summary, or slides fields.");
        }
        catch (JsonException ex) { return AnalysisErrors.InvalidReportFormat(filePath, ex.Message); }
    }

    private static bool IsValidReport(SessionAnalysisReport? r) =>
        r is not null && !string.IsNullOrWhiteSpace(r.WorkspaceId) && r.Summary is not null && r.Slides is not null;
}
