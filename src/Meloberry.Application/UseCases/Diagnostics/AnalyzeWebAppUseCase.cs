using System.Globalization;
using System.Text;
using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Analysis;
using Meloberry.Domain.Common;

namespace Meloberry.Application.UseCases.Diagnostics;

public sealed record AnalyzeWebAppQuery(string WebAppOrReportPath, bool IncludeAllRuns = false);

public sealed record IngestAnalysisCommand(
    string WebAppDirectory,
    string? SourceFilePath = null,
    string? InlineJson = null);

public sealed record AnalyzeWebAppResponse(
    string TargetPath,
    int TotalRunsFound,
    SessionAnalysisReport LatestReport,
    IReadOnlyList<SessionAnalysisReport> ReportsAnalyzed,
    string MarkdownSummary);

public sealed class AnalyzeWebAppHandler(IAnalysisReportStore reportStore)
{
    public async Task<Result<SavedAnalysisFiles>> IngestAsync(
        IngestAnalysisCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.WebAppDirectory))
        {
            return new Error("Analysis.MissingPath", "Target WebApp directory is required.", ErrorType.Validation);
        }

        var fullWebDir = Path.GetFullPath(command.WebAppDirectory.Trim());
        if (!Directory.Exists(fullWebDir))
        {
            return new Error("Analysis.WebAppOrReportNotFound", $"WebApp directory '{command.WebAppDirectory}' was not found.", ErrorType.NotFound);
        }

        string rawInput;
        if (command.InlineJson is not null)
        {
            rawInput = command.InlineJson;
        }
        else if (!string.IsNullOrWhiteSpace(command.SourceFilePath))
        {
            var fullSrc = Path.GetFullPath(command.SourceFilePath.Trim());
            if (!File.Exists(fullSrc))
            {
                return new Error("Analysis.SourceNotFound", $"Analysis payload file '{command.SourceFilePath}' was not found.", ErrorType.NotFound);
            }

            rawInput = await File.ReadAllTextAsync(fullSrc, cancellationToken);
        }
        else
        {
            return new Error("Analysis.MissingPayload", "Pass an analysis JSON file path or '-' for stdin with --ingest.", ErrorType.Validation);
        }

        string rawReportJson;
        string? rawMarkdown = null;
        try
        {
            using var doc = JsonDocument.Parse(rawInput);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Error("Analysis.InvalidReportFormat", "Analysis ingest payload must be a JSON object.", ErrorType.Validation);
            }

            if (doc.RootElement.TryGetProperty("report", out var reportEl))
            {
                if (reportEl.ValueKind != JsonValueKind.Object)
                {
                    return new Error("Analysis.InvalidReportFormat", "Wrapped 'report' property must be a JSON object.", ErrorType.Validation);
                }

                rawReportJson = reportEl.GetRawText();
                if (doc.RootElement.TryGetProperty("markdown", out var mdEl) && mdEl.ValueKind == JsonValueKind.String)
                {
                    rawMarkdown = mdEl.GetString();
                }
            }
            else
            {
                rawReportJson = rawInput;
            }
        }
        catch (JsonException ex)
        {
            return new Error("Analysis.InvalidReportFormat", $"Invalid analysis JSON: {ex.Message}", ErrorType.Validation);
        }

        return await reportStore.SaveReportToWebAppDirectoryAsync(fullWebDir, rawReportJson, rawMarkdown, cancellationToken);
    }

    public async Task<Result<AnalyzeWebAppResponse>> HandleAsync(
        AnalyzeWebAppQuery query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.WebAppOrReportPath))
            return new Error("Analysis.MissingPath", "Path to WebApp directory or analysis JSON file is required.", ErrorType.Validation);

        var loadResult = await reportStore.LoadReportsAsync(query.WebAppOrReportPath, cancellationToken);
        if (!loadResult.IsSuccess)
            return loadResult.Error!;

        var ordered = loadResult.Value
            .OrderByDescending(r => r.CompletedAtUtc, StringComparer.Ordinal)
            .ToList();

        var latest = ordered[0];
        var included = query.IncludeAllRuns ? ordered : [latest];

        var summaryMd = BuildMarkdownOutput(included, ordered.Count);
        return new AnalyzeWebAppResponse(
            Path.GetFullPath(query.WebAppOrReportPath),
            ordered.Count,
            latest,
            included,
            summaryMd);
    }

    private static string BuildMarkdownOutput(IReadOnlyList<SessionAnalysisReport> included, int totalRunsFound)
    {
        if (included.Count == 1)
            return included[0].ToMarkdownReport();

        var sb = new StringBuilder();
        double avgAccuracy = included.Average(r => r.Summary.McqAccuracyPercent);
        double avgDurationSec = included.Average(r => r.TotalDurationMs / 1000.0);

        sb.AppendLine($"# Meloberry Aggregate Analysis ({included.Count} of {totalRunsFound} run(s))");
        sb.AppendLine();
        sb.AppendLine($"- **Workspace:** `{included[0].WorkspaceId}`");
        sb.AppendLine($"- **Average Graded Accuracy:** {avgAccuracy.ToString("F1", CultureInfo.InvariantCulture)}%");
        sb.AppendLine($"- **Average Duration:** {avgDurationSec.ToString("F1", CultureInfo.InvariantCulture)}s");
        sb.AppendLine();

        var frequentWeak = included
            .SelectMany(r => r.WeakItemIds)
            .GroupBy(id => id, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (frequentWeak.Count > 0)
        {
            sb.AppendLine("## Recurring Weak Items");
            sb.AppendLine();
            foreach (var grp in frequentWeak)
                sb.AppendLine($"- `{grp.Key}` (flagged in {grp.Count()}/{included.Count} runs)");
            sb.AppendLine();
        }

        sb.AppendLine("## Latest Run Detail");
        sb.AppendLine();
        sb.AppendLine(included[0].ToMarkdownReport());
        return sb.ToString().TrimEnd();
    }
}
