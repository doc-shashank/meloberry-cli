using System.Globalization;
using System.Text;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Analysis;
using Meloberry.Domain.Common;

namespace Meloberry.Application.UseCases.Diagnostics;

public sealed record AnalyzeWebAppQuery(string WebAppOrReportPath, bool IncludeAllRuns = false);

public sealed record AnalyzeWebAppResponse(
    string TargetPath,
    int TotalRunsFound,
    SessionAnalysisReport LatestReport,
    IReadOnlyList<SessionAnalysisReport> ReportsAnalyzed,
    string MarkdownSummary);

public sealed class AnalyzeWebAppHandler(IAnalysisReportStore reportStore)
{
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
