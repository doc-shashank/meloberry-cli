using System.Globalization;
using System.Text;

namespace Meloberry.Domain.Analysis;

public sealed record SessionAnalysisSummary(
    int TotalSlides, int McqTotal, int McqCorrectFirstTry, int McqCorrectFinal, double McqAccuracyPercent,
    int FlashcardTotal, int FlashcardKnown, int FlashcardNeedsReview, int ArticleSlidesViewed);

public sealed record SlideAnalysisEntry(
    int SlideIndex, string Id, string Kind, long TimeSpentMs, int VisitedCount,
    int? CheckAttempts, IReadOnlyList<int>? SelectedOptionIndices, IReadOnlyList<int>? CorrectOptionIndices,
    bool? FirstTryCorrect, bool? IsCorrect, int? FlipCount, string? FlashcardRating);

public sealed record SessionAnalysisReport(
    string SchemaVersion, string WorkspaceId, string Title, string StartedAtUtc, string CompletedAtUtc,
    long TotalDurationMs, SessionAnalysisSummary Summary, IReadOnlyList<SlideAnalysisEntry> Slides, IReadOnlyList<string> WeakItemIds)
{
    public string ToMarkdownReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Meloberry Session Analysis — {WorkspaceId}\n")
          .AppendLine($"- **Title:** {Title}")
          .AppendLine($"- **Started (UTC):** {StartedAtUtc}")
          .AppendLine($"- **Completed (UTC):** {CompletedAtUtc}")
          .AppendLine($"- **Total Duration:** {(TotalDurationMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture)}s\n")
          .AppendLine("## Summary\n")
          .AppendLine($"- **Total Content Slides:** {Summary.TotalSlides}")
          .AppendLine($"- **MCQs:** {Summary.McqCorrectFinal} / {Summary.McqTotal} correct ({Summary.McqAccuracyPercent.ToString("F1", CultureInfo.InvariantCulture)}% | First-try: {Summary.McqCorrectFirstTry})")
          .AppendLine($"- **Flashcards:** {Summary.FlashcardKnown} known, {Summary.FlashcardNeedsReview} needs review (of {Summary.FlashcardTotal})")
          .AppendLine($"- **Articles Viewed:** {Summary.ArticleSlidesViewed}\n");

        if (WeakItemIds.Count > 0)
        {
            sb.AppendLine("## Weak / Review Items\n");
            foreach (var id in WeakItemIds) sb.AppendLine($"- `{id}`");
            sb.AppendLine();
        }

        sb.AppendLine("## Per-Slide Breakdown\n")
          .AppendLine("| # | ID | Kind | Time (s) | Visits | Result / Details |")
          .AppendLine("| :---: | :--- | :--- | ---: | ---: | :--- |");
        foreach (var s in Slides)
        {
            var seconds = (s.TimeSpentMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
            var details = s.Kind switch
            {
                "mcq" => $"Correct: {(s.IsCorrect == true ? "Yes" : "No")}, Checks: {s.CheckAttempts ?? 0}, Selected: [{string.Join(",", s.SelectedOptionIndices ?? [])}]",
                "flashcard" => $"Flips: {s.FlipCount ?? 0}, Rating: {s.FlashcardRating ?? "unrated"}",
                _ => "Viewed"
            };
            sb.AppendLine($"| {s.SlideIndex} | `{s.Id}` | {s.Kind} | {seconds} | {s.VisitedCount} | {details} |");
        }

        return sb.ToString().TrimEnd();
    }
}
