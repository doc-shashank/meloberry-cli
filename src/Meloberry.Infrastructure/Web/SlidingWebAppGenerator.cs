using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Layouts;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;
using Meloberry.Infrastructure.Packaging;
using Meloberry.Infrastructure.Persistence;

namespace Meloberry.Infrastructure.Web;

public sealed class SlidingWebAppGenerator(
    ICliToolMetadataProvider metadataProvider,
    ChainTransactionManager? transactionManager = null) : IWebAppGenerator
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
    private static readonly Regex MarkdownUrlAttributeRegex = new(@"(?<attr>\b(?:href|src)\s*=\s*)(?<quote>[""'])(?<url>[^""']*)\k<quote>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string RenderSafeMarkdownHtml(string markdown) =>
        MarkdownUrlAttributeRegex.Replace(Markdown.ToHtml(markdown, MarkdownPipeline), static match =>
        {
            var normalized = new string(WebUtility.HtmlDecode(match.Groups["url"].Value).Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
            if (normalized.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
            {
                var q = match.Groups["quote"].Value;
                return $"{match.Groups["attr"].Value}{q}#{q}";
            }
            return match.Value;
        });

    private static string? EncodeAssetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;
        return string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }

    private static string ResolveDeckGlobalValue(IReadOnlyDictionary<string, LayoutProfile> layouts, string propertyName, string fallbackValue)
    {
        string? firstPresent = null;
        foreach (var target in new[] { "mcq", "article", "flashcard" })
        {
            if (!layouts.TryGetValue(target, out var profile)) continue;
            var value = profile.GetValue(propertyName);
            firstPresent ??= value;
            if (!string.Equals(value, fallbackValue, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return firstPresent ?? fallbackValue;
    }

    public Task<Result<string>> GenerateForSessionAsync(
        StudySession session, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputDirectory, CancellationToken cancellationToken = default)
    {
        var slides = new List<SlideRenderModel>();
        foreach (var comp in session.Components)
        {
            if (comp.Kind == ComponentKind.Article)
                slides.Add(BuildArticleSlide(comp, layouts));
            else if (comp.Quiz is not null && comp.Quiz.Questions.Count > 0)
            {
                var baseProfile = comp.Quiz.Type == QuizType.Flashcard ? "flashcard" : "mcq";
                var mergedQuizLayout = MergeProfile(layouts, baseProfile, comp.LayoutOverrides, null);
                var questionSlides = comp.Quiz.Questions.Select(q => BuildQuestionSlide(q, layouts, comp.LayoutOverrides)).ToList();
                slides.Add(new SlideRenderModel(comp.Id, "quiz", mergedQuizLayout, null, null, null, questionSlides, comp.Title));
            }
        }
        var title = string.IsNullOrWhiteSpace(session.Name) ? "Meloberry Session" : session.Name;
        return WriteWebAppBundleAsync(session.Id, title, session.Description, storageDirectory, slides, layouts, MbypkgExporter.CollectSessionAssets(session), outputDirectory, cancellationToken);
    }

    public Task<Result<string>> GenerateForQuizAsync(
        Quiz quiz, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputDirectory, CancellationToken cancellationToken = default)
    {
        var title = string.IsNullOrWhiteSpace(quiz.Name) ? "Meloberry Quiz" : quiz.Name;
        var desc = string.IsNullOrWhiteSpace(quiz.Description) ? $"Standalone quiz ({quiz.Questions.Count} questions)" : quiz.Description;
        return WriteWebAppBundleAsync(quiz.Id, title, desc, storageDirectory,
            quiz.Questions.Select(q => BuildQuestionSlide(q, layouts, null)).ToList(), layouts, MbypkgExporter.CollectQuizAssets(quiz), outputDirectory, cancellationToken);
    }

    private static SlideRenderModel BuildArticleSlide(SessionComponent comp, IReadOnlyDictionary<string, LayoutProfile> layouts) =>
        new(comp.Id, "article", MergeProfile(layouts, "article", comp.LayoutOverrides, null), null, null,
            comp.ArticleBlocks.Select(BuildArticleBlockRenderModel).ToList(), null, comp.Title);

    private static ArticleBlockRenderModel BuildArticleBlockRenderModel(ArticleBlock b)
    {
        if (b.Kind == ArticleBlockKind.Table)
        {
            TableRenderModel? tableModel = null;
            if (Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(b.PayloadJson, out var p) && p is not null)
            {
                var headersHtml = (p.Headers ?? []).Select(RenderInlineSafeMarkdownHtml).ToList();
                var rowsHtml = (p.Rows ?? []).Select(r => (r ?? []).Select(RenderInlineSafeMarkdownHtml).ToList()).ToList();
                tableModel = new TableRenderModel(
                    p.Title,
                    !string.IsNullOrWhiteSpace(p.Caption) ? RenderSafeMarkdownHtml(p.Caption) : null,
                    p.StickyFirstCol,
                    (p.HideableCols ?? []).ToList(),
                    headersHtml,
                    (p.Alignments ?? []).ToList(),
                    rowsHtml,
                    !string.IsNullOrWhiteSpace(p.Footnote) ? RenderSafeMarkdownHtml(p.Footnote) : null,
                    !string.IsNullOrWhiteSpace(p.Title) ? RenderInlineSafeMarkdownHtml(p.Title) : null);
            }
            return new ArticleBlockRenderModel(
                b.Id, "table", null, null,
                Title: b.CalloutTitle,
                Table: tableModel,
                TitleHtml: !string.IsNullOrWhiteSpace(b.CalloutTitle) ? RenderInlineSafeMarkdownHtml(b.CalloutTitle) : null);
        }

        return new ArticleBlockRenderModel(
            b.Id,
            b.Kind switch
            {
                ArticleBlockKind.Image => "image",
                ArticleBlockKind.Callout => "callout",
                _ => "text"
            },
            (b.Kind is ArticleBlockKind.Text or ArticleBlockKind.Callout) && !string.IsNullOrWhiteSpace(b.TextContent)
                ? RenderSafeMarkdownHtml(b.TextContent)
                : null,
            EncodeAssetPath(b.ImagePath),
            !string.IsNullOrWhiteSpace(b.Caption) ? RenderSafeMarkdownHtml(b.Caption) : null,
            b.Alt,
            b.CalloutTone.HasValue ? ArticleBlock.CalloutToneToString(b.CalloutTone.Value) : null,
            b.CalloutTitle,
            TitleHtml: !string.IsNullOrWhiteSpace(b.CalloutTitle) ? RenderInlineSafeMarkdownHtml(b.CalloutTitle) : null);
    }

    private static string RenderInlineSafeMarkdownHtml(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var html = RenderSafeMarkdownHtml(markdown).Trim();
        if (html.StartsWith("<p>", StringComparison.OrdinalIgnoreCase) &&
            html.EndsWith("</p>", StringComparison.OrdinalIgnoreCase) &&
            html.IndexOf("<p>", 3, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return html[3..^4];
        }
        return html;
    }

    private static SlideRenderModel BuildQuestionSlide(
        Question question, IReadOnlyDictionary<string, LayoutProfile> layouts, IReadOnlyDictionary<string, string>? componentOverrides)
    {
        if (question is McqQuestion mcq)
        {
            var merged = MergeProfile(layouts, "mcq", componentOverrides, mcq.LayoutOverrides);
            var options = mcq.Options.Select(o => new McqOptionRenderModel(
                o.Index, !string.IsNullOrWhiteSpace(o.Text) ? RenderSafeMarkdownHtml(o.Text) : null, EncodeAssetPath(o.ImagePath), o.IsCorrect)).ToList();
            var explanationHtml = !string.IsNullOrWhiteSpace(mcq.Explanation) ? RenderSafeMarkdownHtml(mcq.Explanation) : null;
            return new SlideRenderModel(mcq.Id, "mcq", merged, new McqRenderModel(RenderSafeMarkdownHtml(mcq.Text), mcq.IsMultiCorrect, options, EncodeAssetPath(mcq.ImagePath), explanationHtml), null, null, null);
        }

        if (question is FlashcardQuestion card)
        {
            var cardLayout = MergeProfile(layouts, "flashcard", componentOverrides, card.LayoutOverrides);
            return new SlideRenderModel(card.Id, "flashcard", cardLayout, null,
                new FlashcardRenderModel(BuildSideRenderModel(card.Front, cardLayout), BuildSideRenderModel(card.Back, cardLayout)), null, null);
        }

        var mergedLayout = MergeProfile(layouts, "mcq", componentOverrides, question.LayoutOverrides);
        if (question.Type == QuestionType.FillBlank && Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(question.PayloadJson, out var fb) && fb is not null)
        {
            var promptHtml = RenderFillBlankPromptHtml(fb.Template);
            var blanks = (fb.Blanks ?? []).Select(b =>
            {
                var answers = (b.Answers ?? []).ToList();
                var primaryHtml = answers.Count > 0 ? RenderInlineSafeMarkdownHtml(answers[0]) : null;
                return new FillBlankSlotRenderModel(b.Index, answers, primaryHtml);
            }).ToList();
            var expHtml = !string.IsNullOrWhiteSpace(fb.Explanation) ? RenderSafeMarkdownHtml(fb.Explanation) : null;
            return new SlideRenderModel(question.Id, "fill-blank", mergedLayout, null, null, null, null,
                FillBlank: new FillBlankRenderModel(promptHtml, blanks, fb.Fuzzy, fb.CaseSensitive, expHtml));
        }

        if (question.Type == QuestionType.AssertionReason && Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(question.PayloadJson, out var ar) && ar is not null)
        {
            var expHtml = !string.IsNullOrWhiteSpace(ar.Explanation) ? RenderSafeMarkdownHtml(ar.Explanation) : null;
            var opts = BuildAssertionReasonOptions(ar.Scheme);
            return new SlideRenderModel(question.Id, "assertion-reason", mergedLayout, null, null, null, null,
                AssertionReason: new AssertionReasonRenderModel(
                    RenderSafeMarkdownHtml(ar.Assertion),
                    RenderSafeMarkdownHtml(ar.Reason),
                    ar.Answer,
                    ar.Scheme,
                    opts,
                    expHtml));
        }

        if (question.Type == QuestionType.SpotError && Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(question.PayloadJson, out var se) && se is not null)
        {
            var segs = (se.Segments ?? []).Select(s =>
            {
                var corrections = s.Corrections?.ToList();
                var primaryFixHtml = corrections is { Count: > 0 } ? RenderInlineSafeMarkdownHtml(corrections[0]) : null;
                return new SpotErrorSegmentRenderModel(
                    s.SpanIndex,
                    RenderInlineSafeMarkdownHtml(s.Text),
                    s.IsCandidate,
                    s.IsError,
                    corrections,
                    primaryFixHtml);
            }).ToList();
            var expHtml = !string.IsNullOrWhiteSpace(se.Explanation) ? RenderSafeMarkdownHtml(se.Explanation) : null;
            return new SlideRenderModel(question.Id, "spot-error", mergedLayout, null, null, null, null,
                SpotError: new SpotErrorRenderModel(segs, se.RequireFix, se.AllowNoError, se.NoError, expHtml));
        }

        if (question.Type == QuestionType.Differentiation && Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(question.PayloadJson, out var dp) && dp is not null)
        {
            var mode = Phase2PayloadRules.NormalizeDiffMode(dp.Mode);
            var headersList = dp.Headers ?? [];
            var alignmentsList = dp.Alignments ?? [];
            var rowsList = dp.Rows ?? [];
            var specials = dp.SpecialCells ?? [];
            var specialMap = specials
                .Where(sc => sc is not null)
                .GroupBy(sc => (sc.RowIndex, sc.ColIndex))
                .ToDictionary(g => g.Key, g => g.First());

            var headersHtml = headersList.Select(h => RenderInlineSafeMarkdownHtml(h ?? string.Empty)).ToList();
            var rowsHtml = new List<List<string>>(rowsList.Count);
            var rowsRaw = new List<List<string>>(rowsList.Count);
            int blankCounter = 0;

            for (int r = 0; r < rowsList.Count; r++)
            {
                var srcRow = rowsList[r] ?? [];
                var htmlRow = new List<string>(srcRow.Count);
                var rawRow = new List<string>(srcRow.Count);
                for (int c = 0; c < srcRow.Count; c++)
                {
                    var cellText = srcRow[c] ?? string.Empty;
                    rawRow.Add(cellText);
                    if (specialMap.TryGetValue((r, c), out var spec))
                    {
                        if (mode == "fill" && spec.IsBlank)
                        {
                            htmlRow.Add(RenderDiffFillCellHtml(cellText, blankCounter++));
                            continue;
                        }
                        if (mode == "spot-wrong" && spec.IsError)
                        {
                            htmlRow.Add(RenderInlineSafeMarkdownHtml(spec.WrongText ?? cellText));
                            continue;
                        }
                    }
                    htmlRow.Add(RenderInlineSafeMarkdownHtml(cellText));
                }
                rowsHtml.Add(htmlRow);
                rowsRaw.Add(rawRow);
            }

            int slotIdx = 0;
            var specialModels = specials
                .Where(sc => sc is not null)
                .Select(sc =>
                {
                    var answers = sc.BlankAnswers?.ToList();
                    var corrections = sc.Corrections?.ToList();
                    return new DiffCellRenderModel(
                        sc.RowIndex,
                        sc.ColIndex,
                        sc.IsBlank ? slotIdx++ : -1,
                        sc.IsBlank,
                        answers,
                        answers is { Count: > 0 } ? RenderInlineSafeMarkdownHtml(answers[0]) : null,
                        sc.IsError,
                        !string.IsNullOrWhiteSpace(sc.WrongText) ? RenderInlineSafeMarkdownHtml(sc.WrongText) : null,
                        corrections,
                        corrections is { Count: > 0 } ? RenderInlineSafeMarkdownHtml(corrections[0]) : null);
                }).ToList();

            var expHtml = !string.IsNullOrWhiteSpace(dp.Explanation) ? RenderSafeMarkdownHtml(dp.Explanation) : null;
            return new SlideRenderModel(question.Id, "diff", mergedLayout, null, null, null, null,
                Differentiation: new DifferentiationRenderModel(
                    mode,
                    dp.Title,
                    !string.IsNullOrWhiteSpace(dp.Title) ? RenderInlineSafeMarkdownHtml(dp.Title) : null,
                    dp.Bank,
                    dp.Fuzzy,
                    dp.CaseSensitive,
                    dp.AllowNoError,
                    headersHtml,
                    alignmentsList.ToList(),
                    rowsHtml,
                    rowsRaw,
                    specialModels,
                    expHtml));
        }

        if (question.Type == QuestionType.Mechanism && Phase1PayloadCodec.TryDeserialize<MechanismPayload>(question.PayloadJson, out var mp) && mp is not null)
        {
            var steps = (mp.Steps ?? [])
                .Where(s => s is not null)
                .Select(s => new MechanismStepRenderModel(s.StepId, s.OrderGroup, RenderInlineSafeMarkdownHtml(s.Text ?? string.Empty), s.IsDistractor))
                .ToList();
            var expHtml = !string.IsNullOrWhiteSpace(mp.Explanation) ? RenderSafeMarkdownHtml(mp.Explanation) : null;
            return new SlideRenderModel(question.Id, "mechanism", mergedLayout, null, null, null, null,
                Mechanism: new MechanismRenderModel(
                    mp.Title,
                    !string.IsNullOrWhiteSpace(mp.Title) ? RenderInlineSafeMarkdownHtml(mp.Title) : null,
                    steps,
                    expHtml));
        }

        if (question.Type == QuestionType.RelabelImage && Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(question.PayloadJson, out var rel) && rel is not null)
        {
            var boxes = (rel.Boxes ?? [])
                .Where(b => b is not null)
                .Select(b => new RelabelBoxRenderModel(b.Index, b.Label, (b.Aliases ?? []).ToList(), b.X, b.Y, b.W, b.H))
                .ToList();
            var promptHtml = !string.IsNullOrWhiteSpace(rel.Prompt) ? RenderSafeMarkdownHtml(rel.Prompt) : null;
            var expHtml = !string.IsNullOrWhiteSpace(rel.Explanation) ? RenderSafeMarkdownHtml(rel.Explanation) : null;
            return new SlideRenderModel(question.Id, "relabel", mergedLayout, null, null, null, null,
                Relabel: new RelabelRenderModel(
                    EncodeAssetPath(rel.ImagePath) ?? string.Empty,
                    promptHtml,
                    Phase2PayloadRules.NormalizeRelabelMode(rel.Mode),
                    Phase2PayloadRules.NormalizeRelabelStyle(rel.Style),
                    rel.Fuzzy,
                    rel.CaseSensitive,
                    boxes,
                    (rel.Distractors ?? []).ToList(),
                    expHtml));
        }

        return new SlideRenderModel(question.Id, Question.ToWireString(question.Type), mergedLayout, null, null, null, null);
    }

    private static string RenderFillBlankPromptHtml(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;
        var spansRes = Phase1PayloadRules.FindFillBlankSpans(template);
        if (!spansRes.IsSuccess || spansRes.Value.Count == 0)
            return RenderSafeMarkdownHtml(template);

        var sb = new System.Text.StringBuilder();
        int pos = 0;
        for (int slotIdx = 0; slotIdx < spansRes.Value.Count; slotIdx++)
        {
            var span = spansRes.Value[slotIdx];
            sb.Append(template, pos, span.OpenIndex - pos);
            sb.Append($"MBYBLANKSLOT{slotIdx}END");
            pos = span.CloseIndex + 2;
        }
        if (pos < template.Length)
            sb.Append(template, pos, template.Length - pos);
        return RenderSafeMarkdownHtml(sb.ToString());
    }

    private static string RenderDiffFillCellHtml(string cellText, int slotIdx)
    {
        var spansRes = Phase1PayloadRules.FindFillBlankSpans(cellText);
        if (!spansRes.IsSuccess || spansRes.Value.Count == 0)
            return RenderInlineSafeMarkdownHtml(cellText);
        var span = spansRes.Value[0];
        var replaced = string.Concat(
            cellText.AsSpan(0, span.OpenIndex),
            $"MBYDIFFSLOT{slotIdx}END",
            cellText.AsSpan(span.CloseIndex + 2));
        return RenderInlineSafeMarkdownHtml(replaced);
    }

    private static List<AssertionReasonOptionRenderModel> BuildAssertionReasonOptions(string scheme)
    {
        var list = new List<AssertionReasonOptionRenderModel>
        {
            new("both-explain", "Both Assertion (A) and Reason (R) are true, and R is the correct explanation of A."),
            new("both-no-explain", "Both Assertion (A) and Reason (R) are true, but R is NOT the correct explanation of A."),
            new("a-true-r-false", "Assertion (A) is true, but Reason (R) is false."),
            new("a-false-r-true", "Assertion (A) is false, but Reason (R) is true.")
        };
        if (string.Equals(scheme, "5opt", StringComparison.OrdinalIgnoreCase))
            list.Add(new("both-false", "Both Assertion (A) and Reason (R) are false."));
        return list;
    }

    private static FlashcardSideRenderModel BuildSideRenderModel(FlashcardSide side, IReadOnlyDictionary<string, string> cardLayout)
    {
        var defaultPos = cardLayout.TryGetValue("defaultTextPosition", out var dtp) && string.Equals(dtp, "aboveImage", StringComparison.OrdinalIgnoreCase)
            ? "aboveImage" : "belowImage";
        var pos = side.IsPositionExplicit
            ? (side.Position == MediaPosition.TextAboveImage ? "aboveImage" : "belowImage")
            : (side.Position == MediaPosition.TextAboveImage ? "aboveImage" : defaultPos);
        return new(!string.IsNullOrWhiteSpace(side.Text) ? RenderSafeMarkdownHtml(side.Text) : null, EncodeAssetPath(side.ImagePath), pos);
    }

    private static Dictionary<string, string> MergeProfile(
        IReadOnlyDictionary<string, LayoutProfile> layouts, string targetType,
        IReadOnlyDictionary<string, string>? componentOverrides, IReadOnlyDictionary<string, string>? questionOverrides)
    {
        var baseProfile = layouts.TryGetValue(targetType, out var p) ? p : LayoutProfile.CreateDefault(targetType).Value;
        var result = new Dictionary<string, string>(baseProfile.Values, StringComparer.OrdinalIgnoreCase);
        if (componentOverrides is not null) foreach (var kv in componentOverrides) result[kv.Key] = kv.Value;
        if (questionOverrides is not null) foreach (var kv in questionOverrides) result[kv.Key] = kv.Value;
        return result;
    }

    private async Task<Result<string>> WriteWebAppBundleAsync(
        string id, string title, string? description, string storageDirectory, List<SlideRenderModel> slides,
        IReadOnlyDictionary<string, LayoutProfile> layouts, IReadOnlyList<string> referencedAssets, string? outputDirectory, CancellationToken cancellationToken)
    {
        var outDir = !string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetFullPath(outputDirectory)
            : Path.Combine(Packaging.MbypkgExporter.ResolveDefaultOutputBaseDirectory(storageDirectory), $"{id}_web");
        transactionManager?.NotifyOutputDirectoryMutating(outDir);
        Directory.CreateDirectory(outDir);

        var dstAssets = Path.Combine(outDir, "assets");
        foreach (var relAsset in referencedAssets.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (PathConfinement.TryResolveInside(storageDirectory, relAsset, out var src) && File.Exists(src))
            {
                Directory.CreateDirectory(dstAssets);
                File.Copy(src, Path.Combine(dstAssets, Path.GetFileName(src)), overwrite: true);
            }
        }

        var globalTheme = ResolveDeckGlobalValue(layouts, "themeMode", "berry");
        var slideMs = ResolveDeckGlobalValue(layouts, "slideTransitionMs", "380");
        var toolMeta = metadataProvider.GetMetadata();
        var generatorInfo = new WebGeneratorInfoModel(toolMeta.ProductName, toolMeta.ExecutableName, toolMeta.Version, toolMeta.Runtime, DateTimeOffset.UtcNow.ToString("O"));
        var payloadJson = JsonSerializer.Serialize(new WebBundlePayload(id, title, description, globalTheme, slideMs, generatorInfo, slides), JsonOptions);

        bool includeKatex = slides.Any(SlideContainsMath);
        if (includeKatex)
            await CopyEmbeddedKatexAssetsAsync(outDir, cancellationToken);

        var indexPath = Path.Combine(outDir, "index.html");
        await File.WriteAllTextAsync(indexPath, BuildHtml(title, description, globalTheme, toolMeta, includeKatex), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outDir, "styles.css"), BuildCss(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outDir, "app.js"), BuildJs(payloadJson), cancellationToken);
        return indexPath;
    }

    private static bool SlideContainsMath(SlideRenderModel slide)
    {
        static bool HasMath(string? html) => !string.IsNullOrEmpty(html) && html.Contains("class=\"math\"", StringComparison.Ordinal);

        if (slide.ArticleBlocks is not null && slide.ArticleBlocks.Any(b =>
            HasMath(b.TextHtml) ||
            HasMath(b.CaptionHtml) ||
            HasMath(b.TitleHtml) ||
            (b.Table is not null && (
                HasMath(b.Table.TitleHtml) ||
                HasMath(b.Table.CaptionHtml) ||
                HasMath(b.Table.FootnoteHtml) ||
                b.Table.HeadersHtml.Any(HasMath) ||
                b.Table.RowsHtml.Any(r => r.Any(HasMath)))))) return true;
        if (slide.Mcq is not null && (HasMath(slide.Mcq.PromptHtml) || HasMath(slide.Mcq.ExplanationHtml) || slide.Mcq.Options.Any(o => HasMath(o.TextHtml)))) return true;
        if (slide.Flashcard is not null && (HasMath(slide.Flashcard.Front.TextHtml) || HasMath(slide.Flashcard.Back.TextHtml))) return true;
        if (slide.FillBlank is not null && (HasMath(slide.FillBlank.PromptHtml) || HasMath(slide.FillBlank.ExplanationHtml) || slide.FillBlank.Blanks.Any(b => HasMath(b.PrimaryAnswerHtml)))) return true;
        if (slide.AssertionReason is not null && (HasMath(slide.AssertionReason.AssertionHtml) || HasMath(slide.AssertionReason.ReasonHtml) || HasMath(slide.AssertionReason.ExplanationHtml))) return true;
        if (slide.SpotError is not null && (HasMath(slide.SpotError.ExplanationHtml) || slide.SpotError.Segments.Any(s => HasMath(s.Html) || HasMath(s.PrimaryCorrectionHtml)))) return true;
        if (slide.Differentiation is not null && (
            HasMath(slide.Differentiation.TitleHtml) ||
            HasMath(slide.Differentiation.ExplanationHtml) ||
            slide.Differentiation.HeadersHtml.Any(HasMath) ||
            slide.Differentiation.RowsHtml.Any(r => r.Any(HasMath)) ||
            slide.Differentiation.SpecialCells.Any(sc => HasMath(sc.PrimaryAnswerHtml) || HasMath(sc.WrongHtml) || HasMath(sc.PrimaryCorrectionHtml)))) return true;
        if (slide.Mechanism is not null && (
            HasMath(slide.Mechanism.TitleHtml) ||
            HasMath(slide.Mechanism.ExplanationHtml) ||
            slide.Mechanism.Steps.Any(s => HasMath(s.Html)))) return true;
        if (slide.Relabel is not null && (HasMath(slide.Relabel.PromptHtml) || HasMath(slide.Relabel.ExplanationHtml))) return true;
        return slide.Questions is not null && slide.Questions.Any(SlideContainsMath);
    }

    private static async Task CopyEmbeddedKatexAssetsAsync(string outDir, CancellationToken ct)
    {
        const string rootPrefix = "Meloberry.Infrastructure.Web.Assets.katex.";
        const string fontPrefix = rootPrefix + "fonts.";
        var katexDir = Path.Combine(outDir, "katex");
        var fontsDir = Path.Combine(katexDir, "fonts");
        Directory.CreateDirectory(fontsDir);

        var asm = typeof(SlidingWebAppGenerator).Assembly;
        foreach (var resName in asm.GetManifestResourceNames())
        {
            if (!resName.StartsWith(rootPrefix, StringComparison.Ordinal))
                continue;

            string destPath;
            if (resName.StartsWith(fontPrefix, StringComparison.Ordinal))
                destPath = Path.Combine(fontsDir, resName[fontPrefix.Length..]);
            else
                destPath = Path.Combine(katexDir, resName[rootPrefix.Length..]);

            await using var src = asm.GetManifestResourceStream(resName)!;
            await using var dst = File.Create(destPath);
            await src.CopyToAsync(dst, ct);
        }
    }

    private static string BuildHtml(string title, string? description, string themeMode, CliToolMetadata toolMeta, bool includeKatex) => $$"""
<!DOCTYPE html>
<html lang="en" data-theme="{{WebUtility.HtmlEncode(themeMode)}}">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <meta name="generator" content="{{WebUtility.HtmlEncode($"{toolMeta.ProductName} ({toolMeta.ExecutableName}) {toolMeta.Version}")}}" />
  <title>{{WebUtility.HtmlEncode(title)}} — Meloberry</title>
  <link rel="icon" href="data:," />
  {{(includeKatex ? "<link rel=\"stylesheet\" href=\"katex/katex.min.css\" />\n  " : string.Empty)}}<link rel="stylesheet" href="styles.css" />
</head>
<body>
  <div class="mby-shell">
    <section id="greetingScreen" class="mby-greeting">
      <div class="mby-greeting-card">
        <div class="mby-type-tab">MELOBERRY</div>
        <div class="mby-greeting-body">
          <h1 id="greetingTitle" class="mby-greeting-title">{{WebUtility.HtmlEncode(title)}}</h1>
          <p id="greetingDesc" class="mby-greeting-desc">{{WebUtility.HtmlEncode(description ?? string.Empty)}}</p>
          <div id="greetingMeta" class="mby-greeting-meta"></div>
          <div class="mby-greeting-actions">
            <button id="startSessionBtn" class="mby-nav-btn primary mby-start-btn" type="button">Start Session →</button>
            <button id="greetingSearchBtn" class="mby-nav-btn mby-start-search-btn" type="button" title="Search study materials (Ctrl+K)">🔍 Search</button>
          </div>
        </div>
        <div id="greetingGeneratorFooter" class="mby-generator-footer"></div>
      </div>
    </section>
    <header id="playerTopbar" class="mby-topbar hidden">
      <div class="mby-topbar-left">
        <button id="backToSlidesBtn" class="mby-icon-btn hidden" type="button" title="Back to slides">←</button>
        <div class="mby-brand-wrap"><div id="topbarTitle" class="mby-brand">{{WebUtility.HtmlEncode(title)}}</div></div>
      </div>
      <div class="mby-topbar-actions">
        <button id="topbarSearchBtn" class="mby-search-trigger" type="button" title="Search (Ctrl+K or /)">🔍 Search <kbd>Ctrl+K</kbd></button>
        <span id="topbarModeHint" class="mby-mode-hint"></span>
      </div>
    </header>
    <main id="playerViewport" class="mby-viewport hidden">
      <div id="slidesTrack" class="mby-slides-track"></div>
      <button id="scrollDownBtn" class="mby-scroll-down-btn hidden" type="button" title="Scroll down" aria-label="Scroll down">
        <svg class="mby-scroll-down-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="6 9 12 15 18 9"/></svg>
      </button>
    </main>
    <main id="finishScreen" class="mby-finish-viewport hidden"></main>
    <footer id="playerControls" class="mby-controls hidden">
      <div class="mby-prev-wrap">
        <div id="prevCompMenu" class="mby-prev-menu hidden" role="menu">
          <button id="prevCompMenuItem" class="mby-prev-menu-item" type="button" role="menuitem">Previous Article</button>
        </div>
        <button id="prevBtn" class="mby-nav-btn" type="button">← Previous</button>
      </div>
      <div id="dotsNav" class="mby-dots"></div>
      <button id="nextBtn" class="mby-nav-btn primary" type="button">Next →</button>
    </footer>
  </div>
  <div id="earlyFinishModal" class="mby-modal hidden" role="dialog" aria-modal="true" aria-labelledby="earlyFinishTitle">
    <div class="mby-modal-card">
      <h2 id="earlyFinishTitle" class="mby-modal-title">Finish session early?</h2>
      <p class="mby-modal-text">You are in between components. Do you want to finish now and save your session analysis?</p>
      <div class="mby-modal-actions">
        <button id="continueSessionBtn" class="mby-nav-btn" type="button">Continue</button>
        <button id="finishBtn" class="mby-nav-btn primary" type="button">Finish</button>
      </div>
    </div>
  </div>
  <div id="imageZoomModal" class="mby-zoom-modal hidden" role="dialog" aria-modal="true" aria-label="Zoomable image viewer">
    <div class="mby-zoom-topbar">
      <span id="zoomPercentBadge" class="mby-zoom-pill">100%</span>
      <div class="mby-zoom-actions">
        <button id="zoomOutBtn" class="mby-zoom-btn" type="button">−</button>
        <button id="zoomInBtn" class="mby-zoom-btn" type="button">+</button>
        <button id="zoomResetBtn" class="mby-zoom-btn hidden" type="button">Reset</button>
        <button id="zoomCloseBtn" class="mby-zoom-btn" type="button" aria-label="Close zoom view">✕</button>
      </div>
    </div>
    <div id="zoomStage" class="mby-zoom-stage"><img id="zoomTargetImg" class="mby-zoom-img" alt="Zoomed diagram" draggable="false" /></div>
  </div>
  <div id="searchModal" class="mby-modal mby-search-modal hidden" role="dialog" aria-modal="true" aria-label="Search study materials">
    <div class="mby-search-card">
      <div class="mby-search-header">
        <input id="searchInput" class="mby-search-input" type="search" placeholder="Search concepts, figures, tables, callouts, questions..." autocomplete="off" />
        <button id="searchCloseBtn" class="mby-icon-btn" type="button" aria-label="Close search">✕</button>
      </div>
      <div id="searchMeta" class="mby-search-meta"></div>
      <div id="searchResults" class="mby-search-results" role="listbox"></div>
    </div>
  </div>
  {{(includeKatex ? "<script src=\"katex/katex.min.js\"></script>\n  " : string.Empty)}}<script src="app.js"></script>
</body>
</html>
""";

    private static string BuildCss() => WebAppSharedStyles.BuildCoreCss() + """
html, body { height: 100%; overflow: hidden; }
.mby-shell { display: flex; flex-direction: column; height: 100vh; overflow: hidden; }
.mby-greeting { flex: 1; display: flex; align-items: center; justify-content: center; padding: 24px; overflow-y: auto; }
.mby-greeting-card { width: 100%; max-width: 560px; }
.mby-greeting-body { padding: 24px 32px 32px; display: flex; flex-direction: column; gap: 14px; }
.mby-greeting-title { margin: 0; font-size: 26px; font-weight: 800; letter-spacing: -0.02em; color: var(--text); }
.mby-greeting-desc { margin: 0; font-size: 15px; line-height: 1.55; color: var(--muted); }
.mby-greeting-meta { font-size: 13px; font-weight: 600; color: var(--accent); }
.mby-greeting-actions { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-top: 6px; }
.mby-start-btn { padding: 12px 24px !important; font-size: 15px; }
.mby-start-search-btn { padding: 11px 18px !important; font-size: 14px; }
.mby-topbar { flex-shrink: 0; display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 14px 24px; border-bottom: 1px solid var(--border); background: var(--surface); }
.mby-topbar-left, .mby-topbar-actions { display: flex; align-items: center; gap: 10px; min-width: 0; }
.mby-search-trigger { display: inline-flex; align-items: center; gap: 8px; padding: 6px 12px; border-radius: 10px; border: 1px solid var(--border); background: var(--bg); color: var(--muted); font-size: 13px; font-weight: 600; cursor: pointer; }
.mby-search-trigger:hover { border-color: var(--accent); color: var(--accent); }
.mby-search-trigger kbd { font-family: ui-monospace, Consolas, monospace; font-size: 11px; padding: 1px 6px; border-radius: 5px; border: 1px solid var(--border); background: var(--surface); color: var(--muted); }
.mby-brand-wrap { display: flex; flex-direction: column; min-width: 0; }
.mby-brand { font-weight: 800; font-size: 18px; color: var(--accent); letter-spacing: -0.02em; }
.mby-icon-btn { width: 34px; height: 34px; border-radius: 9px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font-size: 16px; font-weight: 700; cursor: pointer; }
.mby-mode-hint { font-size: 12px; color: var(--muted); font-weight: 600; }
.mby-viewport { position: relative; flex: 1; min-height: 0; overflow: hidden; display: flex; }
.mby-slides-track { display: flex; width: 100%; height: 100%; transition: transform var(--slide-ms) cubic-bezier(0.22, 1, 0.36, 1); }
.mby-slide, .mby-finish-viewport { min-height: 0; overflow-y: auto; display: flex; justify-content: center; align-items: flex-start; }
.mby-slide { min-width: 100%; height: 100%; padding: 24px 20px 32px; }
.mby-finish-viewport { flex: 1; padding: 24px 20px 36px; }
.mby-quiz-bundle { width: 100%; max-width: 100%; display: flex; flex-direction: column; align-items: center; gap: 14px; }
.mby-quiz-pills { display: flex; flex-wrap: wrap; gap: 8px; justify-content: center; align-items: center; }
.mby-q-pill { min-width: 38px; height: 38px; padding: 0 10px; border-radius: 10px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-size: 12px; font-weight: 700; display: inline-flex; align-items: center; justify-content: center; cursor: pointer; transition: background 140ms ease, border-color 140ms ease, color 140ms ease; }
.mby-q-pill.answered { background: var(--accent-soft); border-color: var(--accent); color: var(--accent); }
.mby-q-pill.active { background: var(--accent); border-color: var(--accent); color: #ffffff; }
.mby-quiz-viewport { width: 100%; overflow: hidden; transition: height var(--slide-ms) cubic-bezier(0.22, 1, 0.36, 1); }
.mby-quiz-track { display: flex; width: 100%; transition: transform var(--slide-ms) cubic-bezier(0.22, 1, 0.36, 1); align-items: flex-start; }
.mby-quiz-q-slide { min-width: 100%; width: 100%; padding: 0; display: flex; justify-content: center; align-items: flex-start; }
.mby-scroll-down-btn { position: absolute; bottom: 20px; width: 34px; height: 34px; border-radius: 50%; border: 1px solid var(--border); background: var(--surface); color: var(--accent); display: flex; align-items: center; justify-content: center; cursor: pointer; z-index: 20; padding: 0; transition: background 140ms ease, border-color 140ms ease; }
.mby-scroll-down-btn:hover { background: var(--accent-soft); border-color: var(--accent); }
.mby-scroll-down-icon { width: 18px; height: 18px; display: block; }
.mby-controls {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 28px;
  padding: 12px 24px;
  border-top: 1px solid var(--border);
  background: var(--surface);
}
.mby-prev-wrap { position: relative; display: inline-flex; }
.mby-prev-menu { position: absolute; bottom: calc(100% + 10px); left: 0; background: var(--surface); border: 1px solid var(--border); border-radius: 12px; box-shadow: 0 10px 28px rgba(15, 10, 20, 0.16); padding: 6px; z-index: 35; min-width: 170px; }
.mby-prev-menu-item { width: 100%; text-align: left; padding: 9px 14px; border: none; border-radius: 8px; background: transparent; color: var(--text); font-weight: 600; font-size: 13px; cursor: pointer; white-space: nowrap; }
.mby-prev-menu-item:hover { background: var(--accent-soft); color: var(--accent); }
.mby-dots { display: flex; gap: 8px; flex-wrap: wrap; justify-content: center; }
.mby-dot { width: 10px; height: 10px; border-radius: 50%; border: none; background: var(--border); cursor: pointer; padding: 0; transition: transform 150ms ease; }
.mby-dot.active { background: var(--accent); transform: scale(1.25); }
.mby-summary-header h2 { margin: 0 0 4px; font-size: 21px; font-weight: 800; }
.mby-summary-meta { font-size: 12px; color: var(--muted); font-family: ui-monospace, Consolas, monospace; }
.mby-stats-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; margin: 18px 0; }
.mby-stat-box { border: 1px solid var(--border); border-radius: 12px; padding: 14px; background: var(--bg); }
.mby-stat-label { font-size: 11px; color: var(--muted); text-transform: uppercase; font-weight: 700; }
.mby-stat-value { font-size: 22px; font-weight: 800; margin-top: 4px; color: var(--accent); }
.mby-review-box { border-radius: 12px; padding: 14px; margin: 16px 0; border: 1px solid var(--border); }
.mby-review-box.ok { background: rgba(25, 135, 84, 0.1); border-color: rgba(25, 135, 84, 0.35); color: var(--ok); font-weight: 600; font-size: 13px; }
.mby-review-box.warn { background: rgba(220, 53, 69, 0.08); border-color: rgba(220, 53, 69, 0.3); }
.mby-review-title { color: var(--bad); font-weight: 700; font-size: 14px; margin-bottom: 8px; }
.mby-weak-chips { display: flex; flex-wrap: wrap; gap: 8px; }
.mby-weak-chip { padding: 4px 10px; border-radius: 8px; background: var(--surface); border: 1px solid rgba(220, 53, 69, 0.4); color: var(--bad); font-size: 11px; font-family: ui-monospace, Consolas, monospace; font-weight: 600; }
.mby-breakdown-list { display: flex; flex-direction: column; gap: 10px; margin-top: 10px; }
.mby-breakdown-item { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 12px 14px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-breakdown-left { display: flex; align-items: center; gap: 12px; min-width: 0; }
.mby-idx-badge { padding: 5px 9px; border-radius: 8px; background: var(--accent-soft); color: var(--accent); font-weight: 800; font-size: 12px; }
.mby-item-kind { font-weight: 700; font-size: 13px; }
.mby-item-sub { font-size: 12px; color: var(--muted); }
.mby-breakdown-right { display: flex; flex-direction: column; align-items: flex-end; gap: 4px; flex-shrink: 0; }
.mby-status-pill { padding: 3px 10px; border-radius: 999px; font-size: 11px; font-weight: 700; }
.mby-status-pill.ok { background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-status-pill.bad { background: rgba(220, 53, 69, 0.14); color: var(--bad); }
.mby-status-pill.accent { background: var(--accent-soft); color: var(--accent); }
.mby-status-pill.muted { background: var(--border); color: var(--muted); }
.mby-divider { border: none; border-top: 1px solid var(--border); margin: 18px 0; }
.mby-export-stack { display: flex; flex-direction: column; gap: 10px; }
.mby-export-row { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.mby-save-status { font-size: 13px; color: var(--muted); margin-top: 10px; }
.mby-modal { position: fixed; inset: 0; background: rgba(15, 10, 20, 0.55); display: flex; align-items: center; justify-content: center; padding: 20px; z-index: 40; }
.mby-modal-card { width: 100%; max-width: 420px; background: var(--surface); border: 1px solid var(--border); border-radius: 18px; padding: 24px; box-shadow: 0 20px 50px rgba(0, 0, 0, 0.25); }
.mby-modal-title { margin: 0 0 8px; font-size: 19px; font-weight: 800; }
.mby-modal-text { margin: 0 0 20px; font-size: 14px; color: var(--muted); line-height: 1.5; }
.mby-modal-actions { display: flex; justify-content: flex-end; gap: 10px; }
.mby-zoom-modal { position: fixed; inset: 0; background: rgba(0, 0, 0, 0.94); display: flex; flex-direction: column; z-index: 60; }
.mby-zoom-topbar { display: flex; align-items: center; justify-content: space-between; padding: 14px 20px; z-index: 2; }
.mby-zoom-pill { padding: 6px 12px; border-radius: 999px; background: rgba(255, 255, 255, 0.14); color: #ffffff; font-size: 13px; font-weight: 600; }
.mby-zoom-actions { display: flex; gap: 8px; }
.mby-zoom-btn { padding: 6px 12px; border-radius: 8px; border: 1px solid rgba(255, 255, 255, 0.22); background: rgba(255, 255, 255, 0.12); color: #ffffff; font-weight: 700; cursor: pointer; }
.mby-zoom-stage { flex: 1; min-height: 0; display: flex; align-items: center; justify-content: center; overflow: hidden; cursor: grab; }
.mby-zoom-stage:active { cursor: grabbing; }
.mby-zoom-img { max-width: 90vw; max-height: 82vh; object-fit: contain; user-select: none; transform-origin: center center; }
.mby-search-modal { align-items: flex-start; padding-top: 9vh; z-index: 55; }
.mby-search-card { width: 100%; max-width: 660px; background: var(--surface); border: 1px solid var(--border); border-radius: 18px; box-shadow: 0 24px 60px rgba(0, 0, 0, 0.28); overflow: hidden; display: flex; flex-direction: column; max-height: 76vh; }
.mby-search-header { display: flex; align-items: center; gap: 10px; padding: 14px 16px; border-bottom: 1px solid var(--border); background: var(--bg); }
.mby-search-input { flex: 1; padding: 10px 14px; border-radius: 10px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-size: 15px; outline: none; }
.mby-search-input:focus { border-color: var(--accent); }
.mby-search-meta { padding: 8px 16px; font-size: 12px; color: var(--muted); border-bottom: 1px solid var(--border); }
.mby-search-results { overflow-y: auto; padding: 8px; display: flex; flex-direction: column; gap: 6px; }
.mby-search-item { width: 100%; text-align: left; padding: 11px 14px; border-radius: 12px; border: 1px solid transparent; background: transparent; color: var(--text); cursor: pointer; display: flex; flex-direction: column; gap: 4px; }
.mby-search-item:hover, .mby-search-item.active { background: var(--accent-soft); border-color: var(--border); }
.mby-search-item-title { font-size: 12px; font-weight: 800; color: var(--accent); text-transform: uppercase; letter-spacing: 0.03em; }
.mby-search-item-snippet { font-size: 13px; line-height: 1.45; color: var(--text); }
.mby-search-item-snippet mark { background: var(--accent-soft); color: var(--accent); font-weight: 700; padding: 0 2px; border-radius: 3px; }
.mby-search-empty { padding: 24px 16px; text-align: center; color: var(--muted); font-size: 14px; }
.mby-search-pulse { outline: 2px solid var(--accent); outline-offset: 4px; border-radius: 10px; transition: outline-color 400ms ease; }
""";

    private static string BuildJs(string payloadJson) => $$"""
const DATA = {{payloadJson}};
const track = document.getElementById('slidesTrack');
const $ = id => document.getElementById(id);
const scrollDownBtn = $('scrollDownBtn'), greetingScreen = $('greetingScreen'), greetingMeta = $('greetingMeta'), greetingGeneratorFooter = $('greetingGeneratorFooter');
{{WebAppSharedStyles.BuildCoreJs()}}
const startSessionBtn = $('startSessionBtn'), playerTopbar = $('playerTopbar'), playerViewport = $('playerViewport'), playerControls = $('playerControls'), finishScreen = $('finishScreen');
const backToSlidesBtn = $('backToSlidesBtn'), topbarModeHint = $('topbarModeHint'), prevBtn = $('prevBtn'), prevCompMenu = $('prevCompMenu'), prevCompMenuItem = $('prevCompMenuItem');
const nextBtn = $('nextBtn'), finishBtn = $('finishBtn'), continueSessionBtn = $('continueSessionBtn'), earlyFinishModal = $('earlyFinishModal'), dotsNav = $('dotsNav');
const imageZoomModal = $('imageZoomModal'), zoomTargetImg = $('zoomTargetImg'), zoomStage = $('zoomStage'), zoomPercentBadge = $('zoomPercentBadge');
const searchModal = $('searchModal'), searchInput = $('searchInput'), searchResultsEl = $('searchResults'), searchMetaEl = $('searchMeta'), topbarSearchBtn = $('topbarSearchBtn'), greetingSearchBtn = $('greetingSearchBtn'), searchCloseBtn = $('searchCloseBtn');
const SAVE_ICON_SVG = '<svg class="mby-save-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M17 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V7l-4-4zm-5 16c-1.66 0-3-1.34-3-3s1.34-3 3-3 3 1.34 3 3-1.34 3-3 3zm3-10H5V5h10v4z"/></svg>';

let currentIndex = 0, isFinished = false, sessionStartedAt = new Date(), activeLeafEnterMs = null, lastVisitedLeafId = null, autoSaveSent = false;
const activeQuizQuestionIdx = new Map(), quizControllers = new Map(), mcqStateMap = new Map(), flashcardStateMap = new Map(), gradedStateMap = new Map();
const isGradedKind = k => k === 'mcq' || k === 'fill-blank' || k === 'assertion-reason' || k === 'spot-error' || k === 'diff' || k === 'mechanism' || k === 'relabel';
const leafSlides = DATA.slides.flatMap(s => (s.kind === 'quiz' && Array.isArray(s.questions)) ? s.questions : [s]);
const slideTelemetry = leafSlides.map((s, idx) => ({
  slideIndex: idx + 1, id: s.id, kind: s.kind, timeSpentMs: 0, visitedCount: 0,
  checkAttempts: isGradedKind(s.kind) ? 0 : null,
  selectedOptionIndices: s.kind === 'mcq' ? [] : null,
  correctOptionIndices: s.kind === 'mcq' ? s.mcq.options.filter(o => o.isCorrect).map(o => o.index) : null,
  firstTryCorrect: null, isCorrect: null, flipCount: s.kind === 'flashcard' ? 0 : null, flashcardRating: null
}));
const metricById = new Map(slideTelemetry.map(m => [m.id, m]));

function getActiveLeafId() {
  const s = DATA.slides[currentIndex];
  if (!s) return null;
  if (s.kind === 'quiz' && s.questions?.length) return s.questions[Math.max(0, Math.min(activeQuizQuestionIdx.get(s.id) || 0, s.questions.length - 1))].id;
  return s.id;
}
function enterActiveLeaf(forceRecord = false) {
  const leafId = getActiveLeafId();
  if (!leafId) return;
  if (forceRecord || leafId !== lastVisitedLeafId) {
    const m = metricById.get(leafId);
    if (m) m.visitedCount += 1;
    lastVisitedLeafId = leafId;
  }
  activeLeafEnterMs = Date.now();
}
function flushActiveLeafTime() {
  if (activeLeafEnterMs === null) return;
  const m = metricById.get(getActiveLeafId());
  if (m) m.timeSpentMs += Math.max(0, Math.round(Date.now() - activeLeafEnterMs));
  activeLeafEnterMs = Date.now();
}
function populateGeneratorFooter(footerEl, includeTimestamp) {
  if (!footerEl || !DATA.generator) return;
  const g = DATA.generator;
  footerEl.innerHTML = '';
  footerEl.appendChild(mk('span', 'mby-generator-badge', `Generated by ${g.executable} v${g.version}`));
  footerEl.appendChild(mk('span', 'mby-generator-meta', `${g.toolName}${includeTimestamp && g.generatedAtUtc ? ` • Built ${g.generatedAtUtc.slice(0, 10)}` : ''}`));
}

let zoomScale = 1, zoomTx = 0, zoomTy = 0, isDraggingZoom = false, dragStartX = 0, dragStartY = 0;
function updateZoomTransform() {
  if (zoomScale <= 1) { zoomScale = 1; zoomTx = 0; zoomTy = 0; }
  zoomTargetImg.style.transform = `translate(${zoomTx}px, ${zoomTy}px) scale(${zoomScale})`;
  zoomPercentBadge.textContent = `${Math.round(zoomScale * 100)}%`;
  $('zoomResetBtn').classList.toggle('hidden', zoomScale <= 1.01);
}
function openZoomLightbox(src, alt) { zoomTargetImg.src = src; zoomTargetImg.alt = alt || 'Zoomed diagram'; zoomScale = 1; zoomTx = 0; zoomTy = 0; updateZoomTransform(); imageZoomModal.classList.remove('hidden'); }
function closeZoomLightbox() { imageZoomModal.classList.add('hidden'); }
$('zoomInBtn').addEventListener('click', () => { zoomScale = Math.min(5, zoomScale + 0.5); updateZoomTransform(); });
$('zoomOutBtn').addEventListener('click', () => { zoomScale = Math.max(1, zoomScale - 0.5); updateZoomTransform(); });
$('zoomResetBtn').addEventListener('click', () => { zoomScale = 1; updateZoomTransform(); });
$('zoomCloseBtn').addEventListener('click', closeZoomLightbox);
zoomStage.addEventListener('wheel', e => { e.preventDefault(); zoomScale = Math.max(1, Math.min(5, zoomScale + (e.deltaY < 0 ? 0.25 : -0.25))); updateZoomTransform(); }, { passive: false });
zoomStage.addEventListener('dblclick', () => { zoomScale = zoomScale > 1.05 ? 1 : 2.5; updateZoomTransform(); });
zoomStage.addEventListener('pointerdown', e => { if (zoomScale <= 1) return; isDraggingZoom = true; dragStartX = e.clientX - zoomTx; dragStartY = e.clientY - zoomTy; zoomStage.setPointerCapture(e.pointerId); });
zoomStage.addEventListener('pointermove', e => { if (!isDraggingZoom) return; zoomTx = e.clientX - dragStartX; zoomTy = e.clientY - dragStartY; updateZoomTransform(); });
zoomStage.addEventListener('pointerup', () => { isDraggingZoom = false; });

const makeZoomImg = (src, alt) => createZoomableImageNode(src, openZoomLightbox, () => requestAnimationFrame(updateScrollDownHint), alt);
function htmlToPlainText(html) {
  if (!html) return '';
  const d = document.createElement('div');
  d.innerHTML = html;
  return (d.textContent || d.innerText || '').replace(/\s+/g, ' ').trim();
}
function calloutDefaultTitle(tone) {
  if (tone === 'clinical') return 'Clinical Correlation';
  if (tone === 'warning') return 'Warning';
  return 'High-Yield Pearl';
}
function renderMathIn(root) {
  if (!root || typeof katex === 'undefined') return;
  root.querySelectorAll('.math').forEach(el => {
    let tex = (el.textContent || '').trim();
    const isDisplay = el.tagName === 'DIV' || (tex.startsWith('\\[') && tex.endsWith('\\]'));
    if ((tex.startsWith('\\(') && tex.endsWith('\\)')) || (tex.startsWith('\\[') && tex.endsWith('\\]')))
      tex = tex.slice(2, -2).trim();
    try { katex.render(tex, el, { displayMode: isDisplay, throwOnError: false }); } catch {}
  });
}

function buildMcqCard(slide, onAnswered) {
  const m = metricById.get(slide.id), mcq = slide.mcq;
  const card = mk('div', 'mby-card', null, [createUpperTab(mcq.isMultiCorrect ? 'MCQ (MULTI-CORRECT)' : 'MCQ')]);
  applySlideLayoutVars(card, slide.layout);
  const prompt = mk('div', 'mby-prompt'); prompt.innerHTML = mcq.promptHtml; renderMathIn(prompt);
  const promptMedia = mcq.imagePath ? mk('div', 'mby-prompt-media', null, [makeZoomImg(mcq.imagePath)]) : null;
  const grid = mk('div', 'mby-options-grid');
  if (!mcqStateMap.has(slide.id)) mcqStateMap.set(slide.id, { selected: new Set(), checked: false, isCorrect: null });
  const st = mcqStateMap.get(slide.id);

  const optionBtns = mcq.options.map(opt => {
    const btn = mk('button', 'mby-option-btn'); btn.type = 'button';
    if (st.selected.has(opt.index)) btn.classList.add('selected');
    if (st.checked) btn.classList.add(opt.isCorrect ? 'correct' : (st.selected.has(opt.index) ? 'wrong' : 'x'));
    btn.classList.remove('x');
    if (opt.imagePath) btn.appendChild(makeZoomImg(opt.imagePath));
    if (opt.textHtml) { const t = mk('div'); t.innerHTML = opt.textHtml; renderMathIn(t); btn.appendChild(t); }
    btn.addEventListener('click', () => {
      if (!mcq.isMultiCorrect) { st.selected.clear(); optionBtns.forEach(b => b.classList.remove('selected')); }
      if (st.selected.has(opt.index)) { st.selected.delete(opt.index); btn.classList.remove('selected'); }
      else { st.selected.add(opt.index); btn.classList.add('selected'); }
    });
    grid.appendChild(btn); return btn;
  });

  const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
  const feedback = mk('span'); if (st.checked) renderMcqFeedback(feedback, st.isCorrect);
  let explanationBox = null;
  if (mcq.explanationHtml) {
    const expContent = mk('div'); expContent.innerHTML = mcq.explanationHtml; renderMathIn(expContent);
    explanationBox = mk('div', 'mby-explanation-box' + (st.checked ? '' : ' hidden'), null, [mk('div', 'mby-explanation-title', 'Explanation'), expContent]);
  }
  checkBtn.addEventListener('click', () => {
    let allCorrect = true;
    optionBtns.forEach((btn, i) => {
      const opt = mcq.options[i], picked = st.selected.has(opt.index);
      btn.classList.remove('correct', 'wrong');
      if (opt.isCorrect) btn.classList.add('correct'); else if (picked) btn.classList.add('wrong');
      if (picked !== opt.isCorrect) allCorrect = false;
    });
    st.checked = true; st.isCorrect = allCorrect;
    if (m) {
      m.checkAttempts = (m.checkAttempts || 0) + 1;
      m.selectedOptionIndices = Array.from(st.selected).sort((a, b) => a - b);
      if (m.firstTryCorrect === null) m.firstTryCorrect = allCorrect;
      m.isCorrect = allCorrect;
    }
    renderMcqFeedback(feedback, allCorrect);
    if (explanationBox) explanationBox.classList.remove('hidden');
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  card.appendChild(mk('div', 'mby-card-body', null, [prompt, promptMedia, grid, mk('div', 'mby-action-row', null, [checkBtn, feedback]), explanationBox]));
  return card;
}

const GREEK_MAP = [
  ['alpha', 'α'], ['beta', 'β'], ['gamma', 'γ'], ['delta', 'δ'], ['epsilon', 'ε'], ['epsilon', 'ϵ'],
  ['zeta', 'ζ'], ['eta', 'η'], ['theta', 'θ'], ['theta', 'ϑ'], ['iota', 'ι'], ['kappa', 'κ'],
  ['lambda', 'λ'], ['mu', 'μ'], ['mu', 'µ'], ['nu', 'ν'], ['xi', 'ξ'], ['omicron', 'ο'], ['pi', 'π'],
  ['rho', 'ρ'], ['sigma', 'σ'], ['sigma', 'ς'], ['tau', 'τ'], ['upsilon', 'υ'], ['phi', 'φ'], ['phi', 'ϕ'],
  ['chi', 'χ'], ['psi', 'ψ'], ['omega', 'ω'],
  ['Alpha', 'Α'], ['Beta', 'Β'], ['Gamma', 'Γ'], ['Delta', 'Δ'], ['Epsilon', 'Ε'],
  ['Zeta', 'Ζ'], ['Eta', 'Η'], ['Theta', 'Θ'], ['Iota', 'Ι'], ['Kappa', 'Κ'],
  ['Lambda', 'Λ'], ['Mu', 'Μ'], ['Nu', 'Ν'], ['Xi', 'Ξ'], ['Omicron', 'Ο'], ['Pi', 'Π'],
  ['Rho', 'Ρ'], ['Sigma', 'Σ'], ['Tau', 'Τ'], ['Upsilon', 'Υ'], ['Phi', 'Φ'],
  ['Chi', 'Χ'], ['Psi', 'Ψ'], ['Omega', 'Ω']
];
function normalizeRecallAnswer(raw, caseSensitive) {
  let s = String(raw ?? '').trim().replace(/\s+/g, ' ');
  if (!caseSensitive) s = s.toLowerCase();
  for (const [name, sym] of GREEK_MAP) {
    s = s.replace(new RegExp(sym, 'g'), caseSensitive ? name : name.toLowerCase());
  }
  s = s.replace(/[‐‑‒–—−]/g, '-').replace(/[,.;:()]/g, ' ').replace(/\s+/g, ' ').trim();
  return s;
}
function levenshtein(a, b) {
  const m = a.length, n = b.length;
  const dp = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0));
  for (let i = 0; i <= m; i++) dp[i][0] = i;
  for (let j = 0; j <= n; j++) dp[0][j] = j;
  for (let i = 1; i <= m; i++) {
    for (let j = 1; j <= n; j++) {
      dp[i][j] = Math.min(dp[i - 1][j] + 1, dp[i][j - 1] + 1, dp[i - 1][j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
    }
  }
  return dp[m][n];
}
function evaluateRecallMatch(userInput, acceptedAnswers, fuzzy, caseSensitive) {
  const uNorm = normalizeRecallAnswer(userInput, caseSensitive);
  if (!uNorm) return 'wrong';
  const uCompact = uNorm.replace(/[-\s]/g, '');
  for (const ans of (acceptedAnswers || [])) {
    const aNorm = normalizeRecallAnswer(ans, caseSensitive);
    if (uNorm === aNorm || uCompact === aNorm.replace(/[-\s]/g, '')) return 'exact';
  }
  if (fuzzy) {
    for (const ans of (acceptedAnswers || [])) {
      const aNorm = normalizeRecallAnswer(ans, caseSensitive);
      if (aNorm.length >= 6 && levenshtein(uNorm, aNorm) <= 1) return 'fuzzy';
      const aCompact = aNorm.replace(/[-\s]/g, '');
      if (aCompact.length >= 6 && levenshtein(uCompact, aCompact) <= 1) return 'fuzzy';
    }
  }
  return 'wrong';
}

function buildNativeTableBlock(b) {
  const t = b.table;
  if (!t) return null;
  const wrap = mk('div', 'mby-native-table' + (t.stickyFirstCol ? ' mby-sticky-col' : ''));
  wrap.dataset.blockId = b.id;

  const hiddenCols = new Set();
  const hideableSet = new Set(t.hideableCols || []);
  const tdByCol = new Map();

  if (t.title || t.titleHtml || hideableSet.size > 0) {
    const toolbar = mk('div', 'mby-table-toolbar');
    const titleEl = mk('div', 'mby-table-title');
    if (t.titleHtml) { titleEl.innerHTML = t.titleHtml; renderMathIn(titleEl); }
    else { titleEl.textContent = t.title || 'Table'; }
    toolbar.appendChild(titleEl);
    if (hideableSet.size > 0) {
      const controls = mk('div', 'mby-table-controls');
      Array.from(hideableSet).sort((a, c) => a - c).forEach(colIdx => {
        const hdrHtml = t.headersHtml[colIdx - 1] || ('Col ' + colIdx);
        const btn = mk('button', 'mby-table-toggle-btn');
        btn.type = 'button';
        const setToggleLabel = nowHidden => {
          btn.innerHTML = (nowHidden ? 'Show ' : 'Hide ') + hdrHtml;
          renderMathIn(btn);
        };
        setToggleLabel(false);
        btn.addEventListener('click', () => {
          const isHidden = hiddenCols.has(colIdx);
          if (isHidden) hiddenCols.delete(colIdx); else hiddenCols.add(colIdx);
          const nowHidden = hiddenCols.has(colIdx);
          btn.classList.toggle('active', nowHidden);
          setToggleLabel(nowHidden);
          (tdByCol.get(colIdx) || []).forEach(td => {
            td.classList.toggle('mby-cell-masked', nowHidden);
            td.classList.remove('peek');
          });
        });
        controls.appendChild(btn);
      });
      toolbar.appendChild(controls);
    }
    wrap.appendChild(toolbar);
  }

  const scroll = mk('div', 'mby-table-scroll');
  const tableEl = mk('table');
  const thead = mk('thead');
  const headTr = mk('tr');
  (t.headersHtml || []).forEach((hHtml, idx) => {
    const th = mk('th');
    if (t.alignments?.[idx]) th.style.textAlign = t.alignments[idx];
    th.innerHTML = hHtml;
    renderMathIn(th);
    headTr.appendChild(th);
  });
  thead.appendChild(headTr);
  tableEl.appendChild(thead);

  const tbody = mk('tbody');
  (t.rowsHtml || []).forEach(row => {
    const tr = mk('tr');
    row.forEach((cellHtml, idx) => {
      const colNum = idx + 1;
      const td = mk('td');
      if (t.alignments?.[idx]) td.style.textAlign = t.alignments[idx];
      const inner = mk('div', 'mby-cell-inner');
      inner.innerHTML = cellHtml;
      renderMathIn(inner);
      td.appendChild(inner);
      if (hideableSet.has(colNum)) {
        if (!tdByCol.has(colNum)) tdByCol.set(colNum, []);
        tdByCol.get(colNum).push(td);
        td.addEventListener('click', () => { if (td.classList.contains('mby-cell-masked')) td.classList.toggle('peek'); });
      }
      tr.appendChild(td);
    });
    tbody.appendChild(tr);
  });
  tableEl.appendChild(tbody);
  scroll.appendChild(tableEl);
  wrap.appendChild(scroll);

  if (t.captionHtml) {
    const cap = mk('div', 'mby-table-caption');
    cap.innerHTML = t.captionHtml;
    renderMathIn(cap);
    wrap.appendChild(cap);
  }
  if (t.footnoteHtml) {
    const fn = mk('div', 'mby-table-footnote');
    fn.innerHTML = t.footnoteHtml;
    renderMathIn(fn);
    wrap.appendChild(fn);
  }
  return wrap;
}

function buildFillBlankCard(slide, onAnswered) {
  const m = metricById.get(slide.id), fb = slide.fillBlank;
  const card = mk('div', 'mby-card', null, [createUpperTab('FILL IN THE BLANK')]);
  applySlideLayoutVars(card, slide.layout);
  if (!gradedStateMap.has(slide.id)) gradedStateMap.set(slide.id, { values: [], checked: false, isCorrect: null, overridden: false });
  const st = gradedStateMap.get(slide.id);

  let html = fb.promptHtml || '';
  (fb.blanks || []).forEach((b, idx) => {
    html = html.replace('MBYBLANKSLOT' + idx + 'END', `<span class="mby-blank-wrap" data-slot="${idx}"><input type="text" class="mby-blank-input" data-slot="${idx}" autocomplete="off" spellcheck="false" placeholder="Blank ${idx + 1}" /><span class="mby-blank-expected hidden" data-expected="${idx}"></span></span>`);
  });

  const promptEl = mk('div', 'mby-blank-prompt');
  promptEl.innerHTML = html;
  renderMathIn(promptEl);

  const inputs = Array.from(promptEl.querySelectorAll('.mby-blank-input'));
  const expectedEls = Array.from(promptEl.querySelectorAll('.mby-blank-expected'));
  inputs.forEach((inp, idx) => {
    if (st.values[idx]) inp.value = st.values[idx];
    inp.addEventListener('input', () => { st.values[idx] = inp.value; });
    inp.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); checkBtn.click(); } });
  });

  const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
  const overrideBtn = mk('button', 'mby-override-btn hidden', 'Override: Mark Correct'); overrideBtn.type = 'button';
  const feedback = mk('span');
  let explanationBox = null;
  if (fb.explanationHtml) {
    const expContent = mk('div'); expContent.innerHTML = fb.explanationHtml; renderMathIn(expContent);
    explanationBox = mk('div', 'mby-explanation-box' + (st.checked ? '' : ' hidden'), null, [mk('div', 'mby-explanation-title', 'Explanation'), expContent]);
  }

  function applyGradeUi(allOk, anyFuzzy) {
    feedback.textContent = allOk ? (anyFuzzy ? 'Correct (minor typo accepted)!' : 'Correct!') : 'Review highlighted blanks.';
    feedback.style.color = allOk ? 'var(--ok)' : 'var(--bad)';
    feedback.style.fontWeight = '600';
    overrideBtn.classList.toggle('hidden', allOk);
    if (explanationBox) explanationBox.classList.remove('hidden');
  }

  checkBtn.addEventListener('click', () => {
    let allOk = true, anyFuzzy = false;
    inputs.forEach((inp, idx) => {
      st.values[idx] = inp.value;
      const slot = fb.blanks[idx];
      const verdict = evaluateRecallMatch(inp.value, slot?.answers || [], fb.fuzzy, fb.caseSensitive);
      inp.classList.remove('correct', 'fuzzy-ok', 'wrong');
      if (verdict === 'exact') { inp.classList.add('correct'); expectedEls[idx]?.classList.add('hidden'); }
      else if (verdict === 'fuzzy') { inp.classList.add('fuzzy-ok'); anyFuzzy = true; if (expectedEls[idx]) { expectedEls[idx].innerHTML = 'Accepted: ' + (slot?.primaryAnswerHtml || ''); renderMathIn(expectedEls[idx]); expectedEls[idx].classList.remove('hidden'); } }
      else { inp.classList.add('wrong'); allOk = false; if (expectedEls[idx]) { expectedEls[idx].innerHTML = 'Answer: ' + (slot?.primaryAnswerHtml || ''); renderMathIn(expectedEls[idx]); expectedEls[idx].classList.remove('hidden'); } }
    });
    st.checked = true; st.isCorrect = allOk; st.overridden = false;
    if (m) {
      m.checkAttempts = (m.checkAttempts || 0) + 1;
      if (m.firstTryCorrect === null) m.firstTryCorrect = allOk;
      m.isCorrect = allOk;
    }
    applyGradeUi(allOk, anyFuzzy);
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  overrideBtn.addEventListener('click', () => {
    st.isCorrect = true; st.overridden = true;
    inputs.forEach(inp => { inp.classList.remove('wrong'); inp.classList.add('correct'); });
    if (m) m.isCorrect = true;
    feedback.textContent = 'Marked correct (self-override).';
    feedback.style.color = 'var(--ok)';
    overrideBtn.classList.add('hidden');
    if (onAnswered) onAnswered();
  });

  card.appendChild(mk('div', 'mby-card-body', null, [promptEl, mk('div', 'mby-action-row', null, [checkBtn, overrideBtn, feedback]), explanationBox]));
  return card;
}

function buildAssertionReasonCard(slide, onAnswered) {
  const m = metricById.get(slide.id), ar = slide.assertionReason;
  const card = mk('div', 'mby-card', null, [createUpperTab('ASSERTION — REASON')]);
  applySlideLayoutVars(card, slide.layout);
  if (!gradedStateMap.has(slide.id)) gradedStateMap.set(slide.id, { selectedCode: null, checked: false, isCorrect: null });
  const st = gradedStateMap.get(slide.id);

  const aText = mk('div', 'mby-ar-text'); aText.innerHTML = ar.assertionHtml; renderMathIn(aText);
  const rText = mk('div', 'mby-ar-text'); rText.innerHTML = ar.reasonHtml; renderMathIn(rText);
  const stack = mk('div', 'mby-ar-stack', null, [
    mk('div', 'mby-ar-box', null, [mk('div', 'mby-ar-label', 'Assertion (A)'), aText]),
    mk('div', 'mby-ar-box', null, [mk('div', 'mby-ar-label', 'Reason (R)'), rText])
  ]);

  const grid = mk('div', 'mby-options-grid');
  const optBtns = (ar.options || []).map(opt => {
    const btn = mk('button', 'mby-option-btn', opt.label); btn.type = 'button';
    if (st.selectedCode === opt.code) btn.classList.add('selected');
    btn.addEventListener('click', () => {
      st.selectedCode = opt.code;
      optBtns.forEach(b => b.classList.remove('selected'));
      btn.classList.add('selected');
    });
    grid.appendChild(btn);
    return btn;
  });

  const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
  const feedback = mk('span');
  let explanationBox = null;
  if (ar.explanationHtml) {
    const expContent = mk('div'); expContent.innerHTML = ar.explanationHtml; renderMathIn(expContent);
    explanationBox = mk('div', 'mby-explanation-box' + (st.checked ? '' : ' hidden'), null, [mk('div', 'mby-explanation-title', 'Explanation'), expContent]);
  }

  checkBtn.addEventListener('click', () => {
    const isCorrect = st.selectedCode === ar.answer;
    optBtns.forEach((btn, idx) => {
      const code = ar.options[idx].code;
      btn.classList.remove('correct', 'wrong');
      if (code === ar.answer) btn.classList.add('correct');
      else if (code === st.selectedCode) btn.classList.add('wrong');
    });
    st.checked = true; st.isCorrect = isCorrect;
    if (m) {
      m.checkAttempts = (m.checkAttempts || 0) + 1;
      if (m.firstTryCorrect === null) m.firstTryCorrect = isCorrect;
      m.isCorrect = isCorrect;
    }
    renderMcqFeedback(feedback, isCorrect);
    if (explanationBox) explanationBox.classList.remove('hidden');
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  card.appendChild(mk('div', 'mby-card-body', null, [stack, grid, mk('div', 'mby-action-row', null, [checkBtn, feedback]), explanationBox]));
  return card;
}

function buildSpotErrorCard(slide, onAnswered) {
  const m = metricById.get(slide.id), se = slide.spotError;
  const card = mk('div', 'mby-card', null, [createUpperTab('SPOT THE ERROR')]);
  applySlideLayoutVars(card, slide.layout);
  if (!gradedStateMap.has(slide.id)) gradedStateMap.set(slide.id, { selectedSpans: new Set(), pickedNoError: false, fixes: {}, checked: false, isCorrect: null });
  const st = gradedStateMap.get(slide.id);

  const stmtEl = mk('div', 'mby-spot-statement');
  const chipByIdx = new Map();
  const fixRow = mk('div', 'mby-fix-input-row hidden');
  const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';

  function rebuildFixInputs() {
    if (!se.requireFix) return;
    fixRow.innerHTML = '';
    if (st.pickedNoError || st.selectedSpans.size === 0) { fixRow.classList.add('hidden'); return; }
    fixRow.classList.remove('hidden');
    Array.from(st.selectedSpans).sort((a, b) => a - b).forEach(spanIdx => {
      const seg = se.segments.find(s => s.spanIndex === spanIdx);
      const label = mk('label', 'mby-fix-input-label', `Correction for "${htmlToPlainText(seg?.html || '')}":`);
      const inp = mk('input', 'mby-fix-input');
      inp.type = 'text'; inp.autocomplete = 'off'; inp.placeholder = 'Type the corrected wording...';
      if (st.fixes[spanIdx]) inp.value = st.fixes[spanIdx];
      inp.addEventListener('input', () => { st.fixes[spanIdx] = inp.value; });
      inp.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); checkBtn.click(); } });
      fixRow.appendChild(label); fixRow.appendChild(inp);
    });
  }

  let noErrorBtn = null;
  (se.segments || []).forEach(seg => {
    if (!seg.isCandidate) {
      const sp = mk('span'); sp.innerHTML = seg.html; renderMathIn(sp);
      stmtEl.appendChild(sp);
      return;
    }
    const chip = mk('button', 'mby-error-chip'); chip.type = 'button';
    chip.innerHTML = seg.html; renderMathIn(chip);
    if (st.selectedSpans.has(seg.spanIndex)) chip.classList.add('selected');
    chip.addEventListener('click', () => {
      st.pickedNoError = false;
      if (noErrorBtn) noErrorBtn.classList.remove('selected');
      if (st.selectedSpans.has(seg.spanIndex)) { st.selectedSpans.delete(seg.spanIndex); chip.classList.remove('selected'); }
      else { st.selectedSpans.add(seg.spanIndex); chip.classList.add('selected'); }
      rebuildFixInputs();
    });
    chipByIdx.set(seg.spanIndex, { chip, seg });
    stmtEl.appendChild(chip);
  });

  let noErrorRow = null;
  if (se.allowNoError || se.noError) {
    noErrorBtn = mk('button', 'mby-no-error-btn' + (st.pickedNoError ? ' selected' : ''), 'No error in this statement');
    noErrorBtn.type = 'button';
    noErrorBtn.addEventListener('click', () => {
      st.pickedNoError = !st.pickedNoError;
      noErrorBtn.classList.toggle('selected', st.pickedNoError);
      if (st.pickedNoError) {
        st.selectedSpans.clear();
        chipByIdx.forEach(({ chip }) => chip.classList.remove('selected'));
      }
      rebuildFixInputs();
    });
    noErrorRow = mk('div', 'mby-no-error-row', null, [noErrorBtn]);
  }

  rebuildFixInputs();
  const feedback = mk('span');
  let explanationBox = null;
  if (se.explanationHtml) {
    const expContent = mk('div'); expContent.innerHTML = se.explanationHtml; renderMathIn(expContent);
    explanationBox = mk('div', 'mby-explanation-box' + (st.checked ? '' : ' hidden'), null, [mk('div', 'mby-explanation-title', 'Explanation'), expContent]);
  }

  checkBtn.addEventListener('click', () => {
    let allOk = true;
    stmtEl.querySelectorAll('.mby-error-fix-badge').forEach(el => el.remove());

    if (se.noError) {
      allOk = st.pickedNoError && st.selectedSpans.size === 0;
      if (noErrorBtn) { noErrorBtn.classList.remove('correct', 'wrong'); noErrorBtn.classList.add('correct'); }
      chipByIdx.forEach(({ chip, seg }) => {
        chip.classList.remove('correct', 'wrong', 'missed-error');
        if (st.selectedSpans.has(seg.spanIndex)) chip.classList.add('wrong');
      });
    } else {
      if (st.pickedNoError) { allOk = false; noErrorBtn?.classList.add('wrong'); }
      else { noErrorBtn?.classList.remove('wrong', 'correct'); }

      chipByIdx.forEach(({ chip, seg }) => {
        const picked = st.selectedSpans.has(seg.spanIndex);
        chip.classList.remove('correct', 'wrong', 'missed-error');
        if (seg.isError) {
          if (picked) {
            const fixOk = !se.requireFix || evaluateRecallMatch(st.fixes[seg.spanIndex] || '', seg.corrections || [], true, false) !== 'wrong';
            chip.classList.add(fixOk ? 'correct' : 'wrong');
            if (!fixOk) allOk = false;
          } else {
            chip.classList.add('missed-error');
            allOk = false;
          }
          if (seg.corrections?.length) {
            const badge = mk('span', 'mby-error-fix-badge');
            badge.innerHTML = '→ ' + (seg.primaryCorrectionHtml || '');
            renderMathIn(badge);
            chip.insertAdjacentElement('afterend', badge);
          }
        } else if (picked) {
          chip.classList.add('wrong');
          allOk = false;
        }
      });
    }

    st.checked = true; st.isCorrect = allOk;
    if (m) {
      m.checkAttempts = (m.checkAttempts || 0) + 1;
      if (m.firstTryCorrect === null) m.firstTryCorrect = allOk;
      m.isCorrect = allOk;
    }
    feedback.textContent = allOk ? 'Correct!' : 'Review highlighted segments.';
    feedback.style.color = allOk ? 'var(--ok)' : 'var(--bad)';
    feedback.style.fontWeight = '600';
    if (explanationBox) explanationBox.classList.remove('hidden');
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  card.appendChild(mk('div', 'mby-card-body', null, [stmtEl, noErrorRow, fixRow, mk('div', 'mby-action-row', null, [checkBtn, feedback]), explanationBox]));
  return card;
}

function deterministicShuffle(arr, seedStr, keyFn) {
  const out = arr.slice();
  if (out.length < 2) return out;
  let h = 2166136261 >>> 0;
  for (let i = 0; i < seedStr.length; i++) { h ^= seedStr.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; }
  const nextRand = () => { h = (Math.imul(1664525, h) + 1013904223) >>> 0; return h / 4294967296; };
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(nextRand() * (i + 1));
    const tmp = out[i]; out[i] = out[j]; out[j] = tmp;
  }
  if (keyFn && new Set(out.map(keyFn)).size >= 2) {
    let same = true;
    for (let i = 0; i < out.length; i++) { if (keyFn(out[i]) !== keyFn(arr[i])) { same = false; break; } }
    if (same) { const tmp = out[0]; out[0] = out[1]; out[1] = tmp; }
  }
  return out;
}

function buildDiffCard(slide, onAnswered) {
  const m = metricById.get(slide.id), d = slide.differentiation;
  const tabLabel = 'DIFFERENTIATION • ' + (d.mode || 'diff').toUpperCase();
  const card = mk('div', 'mby-card', null, [createUpperTab(tabLabel)]);
  applySlideLayoutVars(card, slide.layout);

  if (!gradedStateMap.has(slide.id)) {
    const initRearr = (d.rowsHtml || []).map((row, rIdx) => {
      const cols = row.slice(1).map((html, offset) => ({ colIdx: offset + 1, html, raw: d.rowsRaw?.[rIdx]?.[offset + 1] ?? '' }));
      const shuffled = d.mode === 'rearrange' ? deterministicShuffle(cols, `${slide.id}:r${rIdx}`, x => x.raw) : cols;
      return [ { colIdx: 0, html: row[0], raw: d.rowsRaw?.[rIdx]?.[0] ?? '' }, ...shuffled ];
    });
    gradedStateMap.set(slide.id, {
      values: {}, bankPickedChipIdx: null, chipBySlot: {},
      selectedCells: new Set(), pickedNoError: false,
      rearrRows: initRearr, swapSrc: null,
      checked: false, isCorrect: null, overridden: false
    });
  }
  const st = gradedStateMap.get(slide.id);

  const wrap = mk('div', 'mby-diff-wrap');
  if (d.titleHtml) {
    const titleEl = mk('div', 'mby-diff-title');
    titleEl.innerHTML = d.titleHtml; renderMathIn(titleEl);
    wrap.appendChild(titleEl);
  }

  const specialByCell = new Map((d.specialCells || []).map(sc => [`${sc.rowIndex}:${sc.colIndex}`, sc]));
  const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
  const overrideBtn = mk('button', 'mby-override-btn hidden', 'Override: Mark Correct'); overrideBtn.type = 'button';
  const feedback = mk('span');
  let explanationBox = null;
  if (d.explanationHtml) {
    const expContent = mk('div'); expContent.innerHTML = d.explanationHtml; renderMathIn(expContent);
    explanationBox = mk('div', 'mby-explanation-box' + (st.checked ? '' : ' hidden'), null, [mk('div', 'mby-explanation-title', 'Explanation'), expContent]);
  }

  const blankInputs = new Map();
  const expectedEls = new Map();
  const bankChips = [];
  const spotButtons = new Map();
  let noErrorBtn = null;
  const rearrButtons = new Map();

  if (d.mode === 'fill' && d.bank) {
    const bankItems = (d.specialCells || [])
      .filter(sc => sc.isBlank && sc.answers?.length)
      .map((sc, idx) => ({ idx, label: sc.answers[0], html: sc.primaryAnswerHtml || escapeHtml(sc.answers[0]) }));
    const shuffledBank = deterministicShuffle(bankItems, `${slide.id}:bank`, x => x.label);
    const bankRow = mk('div', 'mby-diff-bank');
    shuffledBank.forEach(item => {
      const chip = mk('button', 'mby-diff-bank-chip'); chip.type = 'button';
      chip.innerHTML = item.html; renderMathIn(chip);
      chip.dataset.chipIdx = String(item.idx);
      chip.addEventListener('click', () => {
        st.bankPickedChipIdx = (st.bankPickedChipIdx === item.idx) ? null : item.idx;
        bankChips.forEach(c => c.classList.toggle('selected', Number(c.dataset.chipIdx) === st.bankPickedChipIdx));
      });
      bankChips.push(chip);
      bankRow.appendChild(chip);
    });
    wrap.appendChild(bankRow);
  }

  const refreshBankUsed = () => {
    const used = new Set(Object.values(st.chipBySlot).filter(v => v !== null && v !== undefined));
    bankChips.forEach(c => c.classList.toggle('used', used.has(Number(c.dataset.chipIdx))));
  };

  const scroll = mk('div', 'mby-diff-scroll');
  const tableEl = mk('table', 'mby-diff-table');
  const thead = mk('thead');
  const headTr = mk('tr');
  (d.headersHtml || []).forEach((hHtml, idx) => {
    const th = mk('th');
    if (d.alignments?.[idx]) th.style.textAlign = d.alignments[idx];
    th.innerHTML = hHtml; renderMathIn(th);
    headTr.appendChild(th);
  });
  thead.appendChild(headTr);
  tableEl.appendChild(thead);

  const tbody = mk('tbody');
  const renderTableBody = () => {
    tbody.innerHTML = '';
    blankInputs.clear(); expectedEls.clear(); spotButtons.clear(); rearrButtons.clear();
    const rowCount = (d.rowsHtml || []).length;
    for (let r = 0; r < rowCount; r++) {
      const tr = mk('tr');
      const cols = d.mode === 'rearrange' ? st.rearrRows[r] : (d.rowsHtml[r] || []).map((html, c) => ({ colIdx: c, html }));
      for (let c = 0; c < cols.length; c++) {
        const td = mk('td');
        if (d.alignments?.[c]) td.style.textAlign = d.alignments[c];
        const spec = specialByCell.get(`${r}:${c}`);

        if (d.mode === 'fill' && spec && spec.isBlank) {
          const sIdx = spec.slotIndex;
          const cellHtml = (cols[c].html || '').replace(`MBYDIFFSLOT${sIdx}END`, `<span class="mby-blank-wrap"><input type="text" class="mby-blank-input" data-diff-slot="${sIdx}" autocomplete="off" spellcheck="false" placeholder="${d.bank ? 'Pick or type' : 'Blank'}" /><span class="mby-blank-expected hidden" data-diff-exp="${sIdx}"></span></span>`);
          const host = mk('div'); host.innerHTML = cellHtml; renderMathIn(host);
          const inp = host.querySelector('.mby-blank-input');
          const expEl = host.querySelector('.mby-blank-expected');
          if (inp) {
            if (st.values[sIdx] !== undefined) inp.value = st.values[sIdx];
            inp.addEventListener('input', () => {
              st.values[sIdx] = inp.value;
              delete st.chipBySlot[sIdx];
              refreshBankUsed();
            });
            inp.addEventListener('click', () => {
              if (d.bank && st.bankPickedChipIdx !== null) {
                const pickedChip = bankChips.find(ch => Number(ch.dataset.chipIdx) === st.bankPickedChipIdx);
                const bankSpec = (d.specialCells || []).filter(x => x.isBlank)[st.bankPickedChipIdx];
                if (bankSpec?.answers?.length) {
                  inp.value = bankSpec.answers[0];
                  st.values[sIdx] = inp.value;
                  st.chipBySlot[sIdx] = st.bankPickedChipIdx;
                  st.bankPickedChipIdx = null;
                  bankChips.forEach(ch => ch.classList.remove('selected'));
                  refreshBankUsed();
                } else if (pickedChip) {
                  inp.value = pickedChip.textContent || '';
                  st.values[sIdx] = inp.value;
                }
              }
            });
            inp.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); checkBtn.click(); } });
            blankInputs.set(sIdx, inp);
          }
          if (expEl) expectedEls.set(sIdx, expEl);
          td.appendChild(host);
        } else if (d.mode === 'spot-wrong' && c > 0) {
          const key = `${r}:${c}`;
          const btn = mk('button', 'mby-diff-cell-btn' + (st.selectedCells.has(key) ? ' selected' : ''));
          btn.type = 'button';
          btn.innerHTML = cols[c].html; renderMathIn(btn);
          btn.addEventListener('click', () => {
            st.pickedNoError = false;
            if (noErrorBtn) noErrorBtn.classList.remove('selected');
            if (st.selectedCells.has(key)) { st.selectedCells.delete(key); btn.classList.remove('selected'); }
            else { st.selectedCells.add(key); btn.classList.add('selected'); }
          });
          spotButtons.set(key, btn);
          td.appendChild(btn);
        } else if (d.mode === 'rearrange' && c > 0) {
          const key = `${r}:${c}`;
          const isSrc = st.swapSrc && st.swapSrc.r === r && st.swapSrc.c === c;
          const btn = mk('button', 'mby-diff-cell-btn' + (isSrc ? ' swap-selected' : ''));
          btn.type = 'button';
          btn.innerHTML = cols[c].html; renderMathIn(btn);
          btn.addEventListener('click', () => {
            if (!st.swapSrc || st.swapSrc.r !== r) {
              st.swapSrc = { r, c };
            } else if (st.swapSrc.c === c) {
              st.swapSrc = null;
            } else {
              const rowArr = st.rearrRows[r];
              const tmp = rowArr[st.swapSrc.c]; rowArr[st.swapSrc.c] = rowArr[c]; rowArr[c] = tmp;
              st.swapSrc = null;
            }
            renderTableBody();
          });
          rearrButtons.set(key, btn);
          td.appendChild(btn);
        } else {
          const cellDiv = mk('div'); cellDiv.innerHTML = cols[c].html; renderMathIn(cellDiv);
          td.appendChild(cellDiv);
        }
        tr.appendChild(td);
      }
      tbody.appendChild(tr);
    }
    refreshBankUsed();
  };

  renderTableBody();
  tableEl.appendChild(tbody);
  scroll.appendChild(tableEl);
  wrap.appendChild(scroll);

  if (d.mode === 'spot-wrong' && d.allowNoError) {
    noErrorBtn = mk('button', 'mby-no-error-btn' + (st.pickedNoError ? ' selected' : ''), 'No error in this table');
    noErrorBtn.type = 'button';
    noErrorBtn.addEventListener('click', () => {
      st.pickedNoError = !st.pickedNoError;
      noErrorBtn.classList.toggle('selected', st.pickedNoError);
      if (st.pickedNoError) {
        st.selectedCells.clear();
        spotButtons.forEach(b => b.classList.remove('selected'));
      }
    });
    wrap.appendChild(mk('div', 'mby-no-error-row', null, [noErrorBtn]));
  }

  checkBtn.addEventListener('click', () => {
    let allOk = true, anyFuzzy = false;
    wrap.querySelectorAll('.mby-diff-fix-badge').forEach(el => el.remove());

    if (d.mode === 'fill') {
      (d.specialCells || []).filter(sc => sc.isBlank).forEach(sc => {
        const sIdx = sc.slotIndex, inp = blankInputs.get(sIdx), expEl = expectedEls.get(sIdx);
        const val = inp ? inp.value : (st.values[sIdx] || '');
        st.values[sIdx] = val;
        const verdict = evaluateRecallMatch(val, sc.answers || [], d.fuzzy, d.caseSensitive);
        if (inp) inp.classList.remove('correct', 'fuzzy-ok', 'wrong');
        if (verdict === 'exact') { inp?.classList.add('correct'); expEl?.classList.add('hidden'); }
        else if (verdict === 'fuzzy') { inp?.classList.add('fuzzy-ok'); anyFuzzy = true; if (expEl) { expEl.innerHTML = 'Accepted: ' + (sc.primaryAnswerHtml || ''); renderMathIn(expEl); expEl.classList.remove('hidden'); } }
        else { inp?.classList.add('wrong'); allOk = false; if (expEl) { expEl.innerHTML = 'Answer: ' + (sc.primaryAnswerHtml || ''); renderMathIn(expEl); expEl.classList.remove('hidden'); } }
      });
      overrideBtn.classList.toggle('hidden', allOk);
      feedback.textContent = allOk ? (anyFuzzy ? 'Correct (minor typo accepted)!' : 'Correct!') : 'Review highlighted cells.';
    } else if (d.mode === 'spot-wrong') {
      const errorCells = (d.specialCells || []).filter(sc => sc.isError);
      if (errorCells.length === 0) {
        allOk = st.pickedNoError && st.selectedCells.size === 0;
        if (noErrorBtn) { noErrorBtn.classList.remove('correct', 'wrong'); noErrorBtn.classList.add('correct'); }
        spotButtons.forEach((btn, key) => {
          btn.classList.remove('correct', 'wrong', 'missed-error');
          if (st.selectedCells.has(key)) btn.classList.add('wrong');
        });
      } else {
        if (st.pickedNoError) { allOk = false; noErrorBtn?.classList.add('wrong'); }
        else noErrorBtn?.classList.remove('wrong', 'correct');
        spotButtons.forEach((btn, key) => {
          const picked = st.selectedCells.has(key);
          const sc = specialByCell.get(key);
          btn.classList.remove('correct', 'wrong', 'missed-error');
          if (sc && sc.isError) {
            if (picked) btn.classList.add('correct');
            else { btn.classList.add('missed-error'); allOk = false; }
            if (sc.primaryCorrectionHtml) {
              const badge = mk('div', 'mby-diff-fix-badge');
              badge.innerHTML = '→ ' + sc.primaryCorrectionHtml; renderMathIn(badge);
              btn.insertAdjacentElement('afterend', badge);
            }
          } else if (picked) {
            btn.classList.add('wrong');
            allOk = false;
          }
        });
      }
      feedback.textContent = allOk ? 'Correct!' : 'Review highlighted cells.';
    } else if (d.mode === 'rearrange') {
      for (let r = 0; r < (d.rowsRaw || []).length; r++) {
        for (let c = 1; c < (d.rowsRaw[r] || []).length; c++) {
          const key = `${r}:${c}`, btn = rearrButtons.get(key);
          const currentVal = st.rearrRows[r]?.[c]?.raw ?? '';
          const expectedVal = d.rowsRaw[r][c] ?? '';
          const cellOk = currentVal === expectedVal;
          btn?.classList.remove('correct', 'wrong', 'swap-selected');
          btn?.classList.add(cellOk ? 'correct' : 'wrong');
          if (!cellOk) {
            allOk = false;
            const badge = mk('div', 'mby-diff-fix-badge');
            badge.innerHTML = '→ ' + (d.rowsHtml[r]?.[c] || escapeHtml(expectedVal)); renderMathIn(badge);
            btn?.insertAdjacentElement('afterend', badge);
          }
        }
      }
      feedback.textContent = allOk ? 'Correct!' : 'Review swapped cells.';
    }

    feedback.style.color = allOk ? 'var(--ok)' : 'var(--bad)';
    feedback.style.fontWeight = '600';
    st.checked = true; st.isCorrect = allOk; st.overridden = false;
    if (m) {
      m.checkAttempts = (m.checkAttempts || 0) + 1;
      if (m.firstTryCorrect === null) m.firstTryCorrect = allOk;
      m.isCorrect = allOk;
    }
    if (explanationBox) explanationBox.classList.remove('hidden');
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  overrideBtn.addEventListener('click', () => {
    st.isCorrect = true; st.overridden = true;
    blankInputs.forEach(inp => { inp.classList.remove('wrong'); inp.classList.add('correct'); });
    if (m) m.isCorrect = true;
    feedback.textContent = 'Marked correct (self-override).';
    feedback.style.color = 'var(--ok)';
    overrideBtn.classList.add('hidden');
    if (onAnswered) onAnswered();
  });

  card.appendChild(mk('div', 'mby-card-body', null, [wrap, mk('div', 'mby-action-row', null, [checkBtn, overrideBtn, feedback]), explanationBox]));
  return card;
}

function buildMechanismCard(slide, onAnswered) {
  const m = metricById.get(slide.id), mech = slide.mechanism;
  const card = mk('div', 'mby-card', null, [createUpperTab('MECHANISM ARRANGEMENT')]);
  applySlideLayoutVars(card, slide.layout);

  if (!gradedStateMap.has(slide.id)) {
    const shuffled = deterministicShuffle(mech.steps || [], `${slide.id}:mech`, s => String(s.stepId));
    gradedStateMap.set(slide.id, { bankIds: shuffled.map(s => s.stepId), placedIds: [], checked: false, isCorrect: null });
  }
  const st = gradedStateMap.get(slide.id);
  const stepById = new Map((mech.steps || []).map(s => [s.stepId, s]));

  const layout = mk('div', 'mby-mech-layout');
  if (mech.titleHtml) {
    const titleEl = mk('div', 'mby-mech-title');
    titleEl.innerHTML = mech.titleHtml; renderMathIn(titleEl);
    layout.appendChild(titleEl);
  }

  const bankEl = mk('div', 'mby-mech-bank');
  const seqEl = mk('div', 'mby-mech-seq');
  const canonicalBox = mk('div', 'mby-mech-canonical hidden');
  const checkBtn = mk('button', 'mby-check-btn', 'Check Sequence'); checkBtn.type = 'button';
  const feedback = mk('span');
  let explanationBox = null;
  if (mech.explanationHtml) {
    const expContent = mk('div'); expContent.innerHTML = mech.explanationHtml; renderMathIn(expContent);
    explanationBox = mk('div', 'mby-explanation-box' + (st.checked ? '' : ' hidden'), null, [mk('div', 'mby-explanation-title', 'Explanation'), expContent]);
  }

  const renderZones = () => {
    bankEl.innerHTML = '';
    if (st.bankIds.length === 0) {
      bankEl.appendChild(mk('div', 'mby-mech-empty', 'All steps placed in sequence.'));
    } else {
      st.bankIds.forEach(sid => {
        const s = stepById.get(sid); if (!s) return;
        const chip = mk('button', 'mby-mech-chip'); chip.type = 'button';
        chip.innerHTML = s.html; renderMathIn(chip);
        chip.addEventListener('click', () => {
          st.bankIds = st.bankIds.filter(x => x !== sid);
          st.placedIds.push(sid);
          renderZones();
          requestAnimationFrame(updateScrollDownHint);
        });
        bankEl.appendChild(chip);
      });
    }

    seqEl.innerHTML = '';
    if (st.placedIds.length === 0) {
      seqEl.appendChild(mk('div', 'mby-mech-empty', 'Click steps above in causal order (exclude distractors).'));
    } else {
      st.placedIds.forEach((sid, idx) => {
        const s = stepById.get(sid); if (!s) return;
        const slot = mk('div', 'mby-mech-slot');
        const badge = mk('span', 'mby-mech-badge', String(idx + 1));
        const txt = mk('div', 'mby-mech-slot-text'); txt.innerHTML = s.html; renderMathIn(txt);
        const upBtn = mk('button', 'mby-mech-ctrl-btn', '↑'); upBtn.type = 'button'; upBtn.title = 'Move step up'; upBtn.disabled = idx === 0;
        upBtn.addEventListener('click', () => {
          if (idx <= 0) return;
          const tmp = st.placedIds[idx - 1]; st.placedIds[idx - 1] = st.placedIds[idx]; st.placedIds[idx] = tmp;
          renderZones();
        });
        const dnBtn = mk('button', 'mby-mech-ctrl-btn', '↓'); dnBtn.type = 'button'; dnBtn.title = 'Move step down'; dnBtn.disabled = idx === st.placedIds.length - 1;
        dnBtn.addEventListener('click', () => {
          if (idx >= st.placedIds.length - 1) return;
          const tmp = st.placedIds[idx + 1]; st.placedIds[idx + 1] = st.placedIds[idx]; st.placedIds[idx] = tmp;
          renderZones();
        });
        const rmBtn = mk('button', 'mby-mech-ctrl-btn', '✕'); rmBtn.type = 'button'; rmBtn.title = 'Return step to bank';
        rmBtn.addEventListener('click', () => {
          st.placedIds = st.placedIds.filter(x => x !== sid);
          st.bankIds.push(sid);
          renderZones();
        });
        slot.appendChild(badge); slot.appendChild(txt);
        slot.appendChild(mk('div', 'mby-mech-slot-actions', null, [upBtn, dnBtn, rmBtn]));
        seqEl.appendChild(slot);
      });
    }
  };

  renderZones();
  layout.appendChild(mk('div', 'mby-mech-section-label', 'Available Steps'));
  layout.appendChild(bankEl);
  layout.appendChild(mk('div', 'mby-mech-section-label', 'Your Sequence'));
  layout.appendChild(seqEl);
  layout.appendChild(canonicalBox);

  checkBtn.addEventListener('click', () => {
    const canonicalSteps = (mech.steps || []).filter(s => !s.isDistractor).slice().sort((a, b) => a.orderGroup - b.orderGroup);
    const expectedGroups = canonicalSteps.map(s => s.orderGroup);
    const placedSteps = st.placedIds.map(id => stepById.get(id)).filter(Boolean);
    const slotEls = Array.from(seqEl.querySelectorAll('.mby-mech-slot'));
    let allOk = placedSteps.length === expectedGroups.length;

    placedSteps.forEach((s, idx) => {
      const slot = slotEls[idx];
      slot?.classList.remove('correct', 'wrong', 'distractor-wrong');
      if (s.isDistractor) {
        slot?.classList.add('distractor-wrong');
        allOk = false;
      } else if (idx < expectedGroups.length && s.orderGroup === expectedGroups[idx]) {
        slot?.classList.add('correct');
      } else {
        slot?.classList.add('wrong');
        allOk = false;
      }
    });

    if (!allOk) {
      canonicalBox.innerHTML = '<strong>Canonical Order:</strong>';
      const ol = mk('ol');
      canonicalSteps.forEach(cs => { const li = mk('li'); li.innerHTML = cs.html; renderMathIn(li); ol.appendChild(li); });
      canonicalBox.appendChild(ol);
      canonicalBox.classList.remove('hidden');
    } else {
      canonicalBox.classList.add('hidden');
    }

    st.checked = true; st.isCorrect = allOk;
    if (m) {
      m.checkAttempts = (m.checkAttempts || 0) + 1;
      if (m.firstTryCorrect === null) m.firstTryCorrect = allOk;
      m.isCorrect = allOk;
    }
    feedback.textContent = allOk ? 'Sequence correct!' : 'Review highlighted steps.';
    feedback.style.color = allOk ? 'var(--ok)' : 'var(--bad)';
    feedback.style.fontWeight = '600';
    if (explanationBox) explanationBox.classList.remove('hidden');
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  card.appendChild(mk('div', 'mby-card-body', null, [layout, mk('div', 'mby-action-row', null, [checkBtn, feedback]), explanationBox]));
  return card;
}

function buildRelabelCard(slide, onAnswered) {
  const m = metricById.get(slide.id), rel = slide.relabel;
  const mode = rel.mode || 'bank', styleCls = 'mby-mask-' + (rel.style || 'solid');
  const boxes = rel.boxes || [];
  const card = mk('div', 'mby-card', null, [createUpperTab('RELABEL THE IMAGE • ' + mode.toUpperCase())]);
  applySlideLayoutVars(card, slide.layout);

  if (!gradedStateMap.has(slide.id)) {
    const bankPool = mode === 'bank'
      ? deterministicShuffle([...boxes.map(b => b.label), ...(rel.distractors || [])].map((lbl, idx) => ({ chipId: idx, label: lbl })), `${slide.id}:relbank`, it => `${it.chipId}:${it.label}`)
      : [];
    gradedStateMap.set(slide.id, {
      activeBoxIdx: boxes[0]?.index ?? null,
      revealed: new Set(),
      assignedChipByBox: {},
      bankPool,
      recallValues: {},
      checked: false,
      isCorrect: null,
      rating: null,
      zoomUnlocked: false
    });
  }
  const st = gradedStateMap.get(slide.id);

  const layout = mk('div', 'mby-relabel-layout');
  if (rel.promptHtml) {
    const pEl = mk('div', 'mby-relabel-prompt');
    pEl.innerHTML = rel.promptHtml; renderMathIn(pEl);
    layout.appendChild(pEl);
  }

  const toolbar = mk('div', 'mby-relabel-toolbar');
  const zoomBtn = mk('button', 'mby-relabel-tool-btn' + (st.zoomUnlocked ? '' : ' hidden'), 'Zoom Image');
  zoomBtn.type = 'button';
  zoomBtn.addEventListener('click', () => openZoomLightbox(rel.imagePath, htmlToPlainText(rel.promptHtml) || 'Relabel diagram'));

  const stage = mk('div', 'mby-relabel-stage');
  const img = mk('img', 'mby-relabel-img');
  img.src = rel.imagePath;
  img.alt = htmlToPlainText(rel.promptHtml) || 'Diagram to relabel';
  img.draggable = false;
  img.addEventListener('load', () => requestAnimationFrame(updateScrollDownHint));
  stage.appendChild(img);

  const boxBtnMap = new Map();
  const recallInputMap = new Map();
  const recallExpectedMap = new Map();
  let assignListEl = null, bankEl = null;

  const unlockZoom = () => { st.zoomUnlocked = true; zoomBtn.classList.remove('hidden'); };

  let explanationBox = null;
  if (rel.explanationHtml) {
    const expContent = mk('div'); expContent.innerHTML = rel.explanationHtml; renderMathIn(expContent);
    explanationBox = mk('div', 'mby-explanation-box' + (st.checked ? '' : ' hidden'), null, [mk('div', 'mby-explanation-title', 'Explanation'), expContent]);
  }

  const syncBoxVisuals = () => {
    boxes.forEach(b => {
      const btn = boxBtnMap.get(b.index);
      if (!btn) return;
      btn.classList.toggle('active', mode !== 'reveal' && st.activeBoxIdx === b.index);
      if (mode === 'reveal') btn.classList.toggle('revealed', st.revealed.has(b.index));
    });
  };

  boxes.forEach(b => {
    const btn = mk('button', 'mby-mask-box ' + styleCls);
    btn.type = 'button';
    btn.style.left = (b.x * 100) + '%';
    btn.style.top = (b.y * 100) + '%';
    btn.style.width = (b.w * 100) + '%';
    btn.style.height = (b.h * 100) + '%';
    btn.title = 'Mask #' + b.index;
    btn.setAttribute('aria-label', 'Mask ' + b.index);
    btn.appendChild(mk('span', 'mby-mask-badge', String(b.index)));
    btn.addEventListener('click', () => {
      if (mode === 'reveal') {
        if (st.revealed.has(b.index)) st.revealed.delete(b.index); else st.revealed.add(b.index);
        if (st.revealed.size >= boxes.length) { unlockZoom(); if (explanationBox) explanationBox.classList.remove('hidden'); }
        syncBoxVisuals();
      } else if (mode === 'bank') {
        st.activeBoxIdx = b.index;
        syncBoxVisuals();
        if (renderBankUi) renderBankUi();
      } else {
        st.activeBoxIdx = b.index;
        syncBoxVisuals();
        recallInputMap.get(b.index)?.focus();
      }
    });
    boxBtnMap.set(b.index, btn);
    stage.appendChild(btn);
  });

  if (mode === 'reveal') {
    const revAllBtn = mk('button', 'mby-relabel-tool-btn', 'Reveal All'); revAllBtn.type = 'button';
    const hideAllBtn = mk('button', 'mby-relabel-tool-btn', 'Hide All'); hideAllBtn.type = 'button';
    revAllBtn.addEventListener('click', () => {
      boxes.forEach(b => st.revealed.add(b.index));
      unlockZoom();
      if (explanationBox) explanationBox.classList.remove('hidden');
      syncBoxVisuals();
    });
    hideAllBtn.addEventListener('click', () => {
      st.revealed.clear();
      syncBoxVisuals();
    });
    toolbar.appendChild(revAllBtn);
    toolbar.appendChild(hideAllBtn);
  }
  toolbar.appendChild(zoomBtn);
  layout.appendChild(toolbar);
  layout.appendChild(mk('div', 'mby-relabel-stage-wrap', null, [stage]));

  let renderBankUi = null;
  let actionRow = null;

  if (mode === 'reveal') {
    const knownBtn = mk('button', 'mby-rate-btn' + (st.rating === 'known' ? ' active-known' : ''), 'Got It'); knownBtn.type = 'button';
    const reviewBtn = mk('button', 'mby-rate-btn' + (st.rating === 'needsReview' ? ' active-review' : ''), 'Needs Review'); reviewBtn.type = 'button';
    const feedback = mk('span');
    const rate = r => {
      const ok = r === 'known';
      st.rating = r; st.checked = true; st.isCorrect = ok;
      unlockZoom();
      knownBtn.classList.toggle('active-known', ok);
      reviewBtn.classList.toggle('active-review', !ok);
      if (m) {
        m.checkAttempts = (m.checkAttempts || 0) + 1;
        if (m.firstTryCorrect === null) m.firstTryCorrect = ok;
        m.isCorrect = ok;
        m.flashcardRating = r;
      }
      feedback.textContent = ok ? 'Marked as known!' : 'Flagged for review.';
      feedback.style.color = ok ? 'var(--ok)' : 'var(--bad)';
      feedback.style.fontWeight = '600';
      if (explanationBox) explanationBox.classList.remove('hidden');
      if (onAnswered) onAnswered();
      requestAnimationFrame(updateScrollDownHint);
    };
    knownBtn.addEventListener('click', () => rate('known'));
    reviewBtn.addEventListener('click', () => rate('needsReview'));
    actionRow = mk('div', 'mby-action-row', null, [knownBtn, reviewBtn, feedback]);
  } else if (mode === 'bank') {
    assignListEl = mk('div', 'mby-relabel-assignments');
    bankEl = mk('div', 'mby-relabel-bank');
    const chipById = new Map(st.bankPool.map(it => [it.chipId, it]));
    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
    const feedback = mk('span');

    renderBankUi = () => {
      assignListEl.innerHTML = '';
      boxes.forEach(b => {
        const assignedId = st.assignedChipByBox[b.index];
        const assignedItem = assignedId !== undefined ? chipById.get(assignedId) : null;
        const row = mk('div', 'mby-relabel-assign-row' + (st.activeBoxIdx === b.index ? ' active' : ''));
        row.appendChild(mk('span', 'mby-mech-badge', String(b.index)));
        row.appendChild(mk('span', null, assignedItem ? assignedItem.label : '(Select a label below)'));
        if (st.checked) {
          const ok = assignedItem && evaluateRecallMatch(assignedItem.label, [b.label, ...(b.aliases || [])], false, rel.caseSensitive) !== 'wrong';
          row.classList.add(ok ? 'correct' : 'wrong');
          if (!ok) row.appendChild(mk('span', 'mby-diff-fix-badge', 'Answer: ' + b.label));
        }
        row.addEventListener('click', () => {
          st.activeBoxIdx = b.index;
          syncBoxVisuals();
          renderBankUi();
        });
        assignListEl.appendChild(row);
      });

      const usedIds = new Set(Object.values(st.assignedChipByBox).filter(v => v !== undefined && v !== null));
      bankEl.innerHTML = '';
      st.bankPool.forEach(item => {
        const chip = mk('button', 'mby-relabel-bank-chip' + (usedIds.has(item.chipId) ? ' used' : ''), item.label);
        chip.type = 'button';
        chip.addEventListener('click', () => {
          if (st.activeBoxIdx === null && boxes.length > 0) st.activeBoxIdx = boxes[0].index;
          if (st.activeBoxIdx === null) return;
          for (const [k, v] of Object.entries(st.assignedChipByBox)) {
            if (v === item.chipId) delete st.assignedChipByBox[k];
          }
          st.assignedChipByBox[st.activeBoxIdx] = item.chipId;
          const nextEmpty = boxes.find(b => st.assignedChipByBox[b.index] === undefined);
          if (nextEmpty) st.activeBoxIdx = nextEmpty.index;
          syncBoxVisuals();
          renderBankUi();
        });
        bankEl.appendChild(chip);
      });
    };

    checkBtn.addEventListener('click', () => {
      let allOk = true;
      boxes.forEach(b => {
        const assignedId = st.assignedChipByBox[b.index];
        const assignedItem = assignedId !== undefined ? chipById.get(assignedId) : null;
        const ok = !!assignedItem && evaluateRecallMatch(assignedItem.label, [b.label, ...(b.aliases || [])], false, rel.caseSensitive) !== 'wrong';
        const btn = boxBtnMap.get(b.index);
        btn?.classList.remove('correct', 'wrong', 'revealed');
        btn?.classList.add(ok ? 'correct' : 'wrong');
        if (!ok) allOk = false;
      });
      st.checked = true; st.isCorrect = allOk;
      unlockZoom();
      renderBankUi();
      if (m) {
        m.checkAttempts = (m.checkAttempts || 0) + 1;
        if (m.firstTryCorrect === null) m.firstTryCorrect = allOk;
        m.isCorrect = allOk;
      }
      feedback.textContent = allOk ? 'All labels correct!' : 'Review highlighted boxes.';
      feedback.style.color = allOk ? 'var(--ok)' : 'var(--bad)';
      feedback.style.fontWeight = '600';
      if (explanationBox) explanationBox.classList.remove('hidden');
      if (onAnswered) onAnswered();
      requestAnimationFrame(updateScrollDownHint);
    });

    renderBankUi();
    layout.appendChild(assignListEl);
    layout.appendChild(bankEl);
    actionRow = mk('div', 'mby-action-row', null, [checkBtn, feedback]);
  } else {
    // recall mode
    const recallPanel = mk('div', 'mby-relabel-recall-panel');
    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
    const overrideBtn = mk('button', 'mby-override-btn hidden', 'Override: Mark Correct'); overrideBtn.type = 'button';
    const feedback = mk('span');

    boxes.forEach(b => {
      const row = mk('div', 'mby-relabel-recall-row');
      row.appendChild(mk('span', 'mby-mech-badge', String(b.index)));
      const inp = mk('input', 'mby-relabel-recall-input');
      inp.type = 'text'; inp.autocomplete = 'off'; inp.spellcheck = false; inp.placeholder = 'Label #' + b.index;
      if (st.recallValues[b.index]) inp.value = st.recallValues[b.index];
      inp.addEventListener('focus', () => { st.activeBoxIdx = b.index; syncBoxVisuals(); });
      inp.addEventListener('input', () => { st.recallValues[b.index] = inp.value; });
      inp.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); checkBtn.click(); } });
      const expEl = mk('span', 'mby-blank-expected hidden');
      recallInputMap.set(b.index, inp);
      recallExpectedMap.set(b.index, expEl);
      row.appendChild(inp);
      row.appendChild(expEl);
      recallPanel.appendChild(row);
    });

    checkBtn.addEventListener('click', () => {
      let allOk = true, anyFuzzy = false;
      boxes.forEach(b => {
        const inp = recallInputMap.get(b.index), expEl = recallExpectedMap.get(b.index), btn = boxBtnMap.get(b.index);
        const val = inp?.value || '';
        st.recallValues[b.index] = val;
        const verdict = evaluateRecallMatch(val, [b.label, ...(b.aliases || [])], rel.fuzzy, rel.caseSensitive);
        inp?.classList.remove('correct', 'fuzzy-ok', 'wrong');
        btn?.classList.remove('correct', 'wrong', 'revealed');
        if (verdict === 'exact') {
          inp?.classList.add('correct'); btn?.classList.add('correct'); expEl?.classList.add('hidden');
        } else if (verdict === 'fuzzy') {
          anyFuzzy = true; inp?.classList.add('fuzzy-ok'); btn?.classList.add('correct');
          if (expEl) { expEl.textContent = 'Accepted: ' + b.label; expEl.classList.remove('hidden'); }
        } else {
          allOk = false; inp?.classList.add('wrong'); btn?.classList.add('wrong');
          if (expEl) { expEl.textContent = 'Answer: ' + b.label; expEl.classList.remove('hidden'); }
        }
      });
      st.checked = true; st.isCorrect = allOk;
      unlockZoom();
      if (m) {
        m.checkAttempts = (m.checkAttempts || 0) + 1;
        if (m.firstTryCorrect === null) m.firstTryCorrect = allOk;
        m.isCorrect = allOk;
      }
      feedback.textContent = allOk ? (anyFuzzy ? 'Correct (minor typo accepted)!' : 'All labels correct!') : 'Review highlighted labels.';
      feedback.style.color = allOk ? 'var(--ok)' : 'var(--bad)';
      feedback.style.fontWeight = '600';
      overrideBtn.classList.toggle('hidden', allOk);
      if (explanationBox) explanationBox.classList.remove('hidden');
      if (onAnswered) onAnswered();
      requestAnimationFrame(updateScrollDownHint);
    });

    overrideBtn.addEventListener('click', () => {
      st.isCorrect = true;
      boxes.forEach(b => {
        recallInputMap.get(b.index)?.classList.remove('wrong');
        recallInputMap.get(b.index)?.classList.add('correct');
        boxBtnMap.get(b.index)?.classList.remove('wrong');
        boxBtnMap.get(b.index)?.classList.add('correct');
      });
      if (m) m.isCorrect = true;
      feedback.textContent = 'Marked correct (self-override).';
      feedback.style.color = 'var(--ok)';
      overrideBtn.classList.add('hidden');
      if (onAnswered) onAnswered();
    });

    layout.appendChild(recallPanel);
    actionRow = mk('div', 'mby-action-row', null, [checkBtn, overrideBtn, feedback]);
  }

  syncBoxVisuals();
  card.appendChild(mk('div', 'mby-card-body', null, [layout, actionRow, explanationBox]));
  return card;
}

function renderFlashcardSide(side, faceLabel, slideMetric, fcState, onRated) {
  const body = mk('div', 'mby-flashcard-body');
  const txt = side.textHtml ? (() => { const d = mk('div'); d.innerHTML = side.textHtml; renderMathIn(d); return d; })() : null;
  const img = side.imagePath ? makeZoomImg(side.imagePath) : null;
  if (side.position === 'aboveImage') { if (txt) body.appendChild(txt); if (img) body.appendChild(img); }
  else { if (img) body.appendChild(img); if (txt) body.appendChild(txt); }

  if (faceLabel === 'Back') {
    const knownBtn = mk('button', 'mby-rate-btn' + (fcState.rating === 'known' ? ' active-known' : ''), 'Got It'); knownBtn.type = 'button';
    const reviewBtn = mk('button', 'mby-rate-btn' + (fcState.rating === 'needsReview' ? ' active-review' : ''), 'Needs Review'); reviewBtn.type = 'button';
    const rate = r => {
      fcState.rating = r; if (slideMetric) slideMetric.flashcardRating = r;
      knownBtn.classList.toggle('active-known', r === 'known');
      reviewBtn.classList.toggle('active-review', r === 'needsReview');
      if (onRated) onRated();
    };
    knownBtn.addEventListener('click', e => { e.stopPropagation(); rate('known'); });
    reviewBtn.addEventListener('click', e => { e.stopPropagation(); rate('needsReview'); });
    body.appendChild(mk('div', 'mby-rating-row', null, [knownBtn, reviewBtn]));
  }

  return mk('div', 'mby-flashcard-face ' + (faceLabel === 'Back' ? 'mby-flashcard-back' : ''), null,
    [createUpperTab('FLASHCARD'), body, mk('div', 'mby-flashcard-side-label', faceLabel)]);
}

function buildFlashcardPanel(slide, onRated, onFlipStart, onFlipEnd) {
  const slideMetric = metricById.get(slide.id);
  if (!flashcardStateMap.has(slide.id)) flashcardStateMap.set(slide.id, { flipped: false, rating: null });
  const fcState = flashcardStateMap.get(slide.id);
  const scene = mk('div', 'mby-flashcard-scene'); applySlideLayoutVars(scene, slide.layout);
  const flipMs = Math.max(50, parseInt(slide.layout?.flipDurationMs || '450', 10) || 450);
  const frontFace = renderFlashcardSide(slide.flashcard.front, 'Front', slideMetric, fcState, onRated);
  const backFace = renderFlashcardSide(slide.flashcard.back, 'Back', slideMetric, fcState, onRated);
  frontFace.classList.toggle('is-active', !fcState.flipped); backFace.classList.toggle('is-active', fcState.flipped);
  const inner = mk('div', 'mby-flashcard-inner', null, [frontFace, backFace]);
  const triggerFlip = attachFlashcardFlip(scene, inner, frontFace, backFace, flipMs, () => fcState.flipped, v => {
    fcState.flipped = v; if (slideMetric) slideMetric.flipCount = (slideMetric.flipCount || 0) + 1;
  }, onFlipStart, () => { if (onFlipEnd) onFlipEnd(); requestAnimationFrame(updateScrollDownHint); });
  scene.addEventListener('click', triggerFlip);
  scene.appendChild(inner);
  return scene;
}

function buildBundledQuiz(quizSlide) {
  const wrap = mk('div', 'mby-quiz-bundle'); applySlideLayoutVars(wrap, quizSlide.layout);
  const questions = quizSlide.questions || [];
  if (!activeQuizQuestionIdx.has(quizSlide.id)) activeQuizQuestionIdx.set(quizSlide.id, 0);
  const pillsRow = mk('div', 'mby-quiz-pills'), qTrack = mk('div', 'mby-quiz-track'), qViewport = mk('div', 'mby-quiz-viewport', null, [qTrack]);
  const qSlides = [];
  const pillButtons = questions.map((_, idx) => {
    const pill = mk('button', 'mby-q-pill', 'Q' + (idx + 1)); pill.type = 'button';
    pill.addEventListener('click', () => selectQuestion(idx));
    pillsRow.appendChild(pill); return pill;
  });

  function syncQuizViewportHeight(qIdx) {
    const cardEl = qSlides[qIdx]?.firstElementChild || qSlides[qIdx];
    const h = cardEl ? Math.ceil(cardEl.getBoundingClientRect().height) : 0;
    if (h > 0) qViewport.style.height = h + 'px';
  }
  function refreshPills(activeQIdx) {
    questions.forEach((q, idx) => {
      const answered = (q.kind === 'mcq' && mcqStateMap.get(q.id)?.checked) ||
        (q.kind === 'flashcard' && !!flashcardStateMap.get(q.id)?.rating) ||
        (isGradedKind(q.kind) && q.kind !== 'mcq' && gradedStateMap.get(q.id)?.checked);
      pillButtons[idx].classList.toggle('active', idx === activeQIdx);
      pillButtons[idx].classList.toggle('answered', idx !== activeQIdx && answered);
    });
  }

  questions.forEach(q => {
    const qSlide = mk('div', 'mby-quiz-q-slide');
    const onItemChanged = () => {
      const activeQ = activeQuizQuestionIdx.get(quizSlide.id) || 0;
      refreshPills(activeQ);
      requestAnimationFrame(() => { if (!qSlides[activeQ]?.firstElementChild?.classList.contains('is-flipping')) syncQuizViewportHeight(activeQ); updateScrollDownHint(); });
    };
    const onFlipStart = (h, ms) => { qViewport.style.transition = 'height ' + ms + 'ms cubic-bezier(0.4, 0, 0.2, 1)'; if (h > 0) qViewport.style.height = h + 'px'; };
    const onFlipEnd = () => { qViewport.style.transition = ''; syncQuizViewportHeight(activeQuizQuestionIdx.get(quizSlide.id) || 0); };
    if (q.kind === 'mcq') qSlide.appendChild(buildMcqCard(q, onItemChanged));
    else if (q.kind === 'flashcard') qSlide.appendChild(buildFlashcardPanel(q, onItemChanged, onFlipStart, onFlipEnd));
    else if (q.kind === 'fill-blank') qSlide.appendChild(buildFillBlankCard(q, onItemChanged));
    else if (q.kind === 'assertion-reason') qSlide.appendChild(buildAssertionReasonCard(q, onItemChanged));
    else if (q.kind === 'spot-error') qSlide.appendChild(buildSpotErrorCard(q, onItemChanged));
    else if (q.kind === 'diff') qSlide.appendChild(buildDiffCard(q, onItemChanged));
    else if (q.kind === 'mechanism') qSlide.appendChild(buildMechanismCard(q, onItemChanged));
    else if (q.kind === 'relabel') qSlide.appendChild(buildRelabelCard(q, onItemChanged));
    qSlides.push(qSlide); qTrack.appendChild(qSlide);
  });

  if (typeof ResizeObserver !== 'undefined') {
    const ro = new ResizeObserver(() => {
      const activeQ = activeQuizQuestionIdx.get(quizSlide.id) || 0;
      if (!qSlides[activeQ]?.firstElementChild?.classList.contains('is-flipping')) syncQuizViewportHeight(activeQ);
      if (DATA.slides[currentIndex]?.id === quizSlide.id) updateScrollDownHint();
    });
    qSlides.forEach(s => { if (s.firstElementChild) ro.observe(s.firstElementChild); });
  }
  qTrack.addEventListener('transitionend', e => { if (e.target === qTrack && e.propertyName === 'transform') requestAnimationFrame(updateScrollDownHint); });
  qViewport.addEventListener('transitionend', e => { if (e.target === qViewport && e.propertyName === 'height') requestAnimationFrame(updateScrollDownHint); });

  function selectQuestion(qIdx, opts = { recordVisit: true, flushPrevious: true, scrollToTop: true }) {
    if (questions.length === 0) return;
    const { recordVisit = true, flushPrevious = true, scrollToTop = true } = opts || {};
    closePrevCompMenu();
    if (flushPrevious) flushActiveLeafTime();
    const clamped = Math.max(0, Math.min(qIdx, questions.length - 1));
    activeQuizQuestionIdx.set(quizSlide.id, clamped);
    if (recordVisit && greetingScreen.classList.contains('hidden') && DATA.slides[currentIndex]?.id === quizSlide.id) {
      enterActiveLeaf();
    }
    qTrack.style.transform = 'translateX(-' + (clamped * 100) + '%)';
    refreshPills(clamped); updateNavButtonsUi();
    if (scrollToTop) scrollSlideToTop(wrap.closest('.mby-slide'));
    requestAnimationFrame(() => { syncQuizViewportHeight(clamped); updateScrollDownHint(); });
  }

  quizControllers.set(quizSlide.id, { selectQuestion, syncHeight: () => syncQuizViewportHeight(activeQuizQuestionIdx.get(quizSlide.id) || 0), getQuestionCount: () => questions.length });
  if (quizSlide.title) wrap.appendChild(mk('div', 'mby-quiz-section-title', quizSlide.title));
  wrap.appendChild(pillsRow); wrap.appendChild(qViewport);
  selectQuestion(activeQuizQuestionIdx.get(quizSlide.id) || 0, { recordVisit: false, flushPrevious: false, scrollToTop: false });
  return wrap;
}

function buildAnalysisReport() {
  flushActiveLeafTime();
  const completedAt = new Date(), totalDurationMs = Math.max(0, completedAt.getTime() - sessionStartedAt.getTime());
  const mcqs = slideTelemetry.filter(s => isGradedKind(s.kind)), cards = slideTelemetry.filter(s => s.kind === 'flashcard'), articles = slideTelemetry.filter(s => s.kind === 'article');
  const mcqCorrectFinal = mcqs.filter(m => m.isCorrect === true).length, mcqCorrectFirstTry = mcqs.filter(m => m.firstTryCorrect === true).length;
  const mcqAccuracy = mcqs.length === 0 ? 100 : Math.round((mcqCorrectFinal / mcqs.length) * 1000) / 10;
  const weakItemIds = [...mcqs.filter(m => m.isCorrect !== true).map(m => m.id), ...cards.filter(c => c.flashcardRating === 'needsReview').map(c => c.id)];
  return {
    schemaVersion: '1.0', workspaceId: DATA.id, title: DATA.title, startedAtUtc: sessionStartedAt.toISOString(), completedAtUtc: completedAt.toISOString(), totalDurationMs,
    summary: { totalSlides: leafSlides.length, mcqTotal: mcqs.length, mcqCorrectFirstTry, mcqCorrectFinal, mcqAccuracyPercent: mcqAccuracy,
      flashcardTotal: cards.length, flashcardKnown: cards.filter(c => c.flashcardRating === 'known').length, flashcardNeedsReview: cards.filter(c => c.flashcardRating === 'needsReview').length, articleSlidesViewed: articles.filter(a => a.visitedCount > 0).length },
    slides: slideTelemetry.map(s => ({ ...s })), weakItemIds
  };
}

function buildAnalysisMarkdown(report) {
  const lines = ['# Meloberry Session Analysis — ' + report.workspaceId, '', '- **Title:** ' + report.title, '- **Started (UTC):** ' + report.startedAtUtc, '- **Completed (UTC):** ' + report.completedAtUtc, '- **Total Duration:** ' + (report.totalDurationMs / 1000).toFixed(1) + 's', '', '## Summary', '',
    '- **Total Content Slides:** ' + report.summary.totalSlides,
    '- **Graded Questions:** ' + report.summary.mcqCorrectFinal + ' / ' + report.summary.mcqTotal + ' correct (' + report.summary.mcqAccuracyPercent.toFixed(1) + '% | First-try: ' + report.summary.mcqCorrectFirstTry + ')',
    '- **Flashcards:** ' + report.summary.flashcardKnown + ' known, ' + report.summary.flashcardNeedsReview + ' needs review (of ' + report.summary.flashcardTotal + ')',
    '- **Articles Viewed:** ' + report.summary.articleSlidesViewed, ''];
  if (report.weakItemIds.length > 0) { lines.push('## Weak / Review Items', ''); report.weakItemIds.forEach(id => lines.push('- `' + id + '`')); lines.push(''); }
  lines.push('## Per-Slide Breakdown', '', '| # | ID | Kind | Time (s) | Visits | Result / Details |', '| :---: | :--- | :--- | ---: | ---: | :--- |');
  report.slides.forEach(s => {
    let d = s.visitedCount > 0 ? 'Viewed' : 'Skipped';
    if (s.kind === 'mcq') d = 'Correct: ' + (s.isCorrect === true ? 'Yes' : 'No') + ', Checks: ' + (s.checkAttempts || 0) + ', Selected: [' + (s.selectedOptionIndices || []).join(',') + ']';
    else if (isGradedKind(s.kind)) d = 'Correct: ' + (s.isCorrect === true ? 'Yes' : 'No') + ', Checks: ' + (s.checkAttempts || 0);
    else if (s.kind === 'flashcard') d = 'Flips: ' + (s.flipCount || 0) + ', Rating: ' + (s.flashcardRating || 'unrated');
    lines.push('| ' + s.slideIndex + ' | `' + s.id + '` | ' + s.kind + ' | ' + (s.timeSpentMs / 1000).toFixed(1) + ' | ' + s.visitedCount + ' | ' + d + ' |');
  });
  return lines.join('\n');
}

function downloadTextFile(filename, content, mimeType) {
  const url = URL.createObjectURL(new Blob([content], { type: mimeType })), a = mk('a');
  a.href = url; a.download = filename; document.body.appendChild(a); a.click(); document.body.removeChild(a); URL.revokeObjectURL(url);
}

async function postAnalysisIfHosted(report, markdown, statusEl) {
  if (!window.location.protocol.startsWith('http')) { statusEl.textContent = 'Offline mode: use Export .analysis.json / Export .md to save reports.'; return; }
  try {
    const res = await fetch('/api/analysis', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ report, markdown }) });
    statusEl.textContent = res.ok ? ('Saved to reports: ' + (await res.json()).jsonPath) : 'Could not auto-save to server; use Download buttons.';
  } catch { statusEl.textContent = 'Server unreachable; use Download buttons.'; }
}

function getActiveCardElement() {
  const activeSlideEl = track.children[currentIndex], slide = DATA.slides[currentIndex];
  if (!activeSlideEl) return null;
  if (slide?.kind === 'quiz') {
    const qSlide = activeSlideEl.querySelectorAll('.mby-quiz-q-slide')[activeQuizQuestionIdx.get(slide.id) || 0];
    return qSlide ? (qSlide.querySelector('.mby-card, .mby-flashcard-scene') || qSlide) : null;
  }
  return activeSlideEl.querySelector('.mby-card, .mby-flashcard-scene');
}

function updateScrollDownHint() {
  if (!scrollDownBtn) return;
  if (!greetingScreen.classList.contains('hidden') || isFinished || playerViewport.classList.contains('hidden')) { scrollDownBtn.classList.add('hidden'); return; }
  const activeSlideEl = track.children[currentIndex];
  if (!activeSlideEl || (activeSlideEl.scrollHeight - activeSlideEl.scrollTop - activeSlideEl.clientHeight) <= 8) { scrollDownBtn.classList.add('hidden'); return; }
  const cardEl = getActiveCardElement(), vpRect = playerViewport.getBoundingClientRect();
  if (!cardEl || vpRect.width <= 0) { scrollDownBtn.classList.add('hidden'); return; }
  scrollDownBtn.style.left = Math.max(12, Math.min((cardEl.getBoundingClientRect().right - vpRect.left) + 12, Math.max(12, vpRect.width - 52))) + 'px';
  scrollDownBtn.classList.remove('hidden');
}

function openFinishScreen(triggerSave) {
  isFinished = true;
  if (scrollDownBtn) scrollDownBtn.classList.add('hidden');
  playerViewport.classList.add('hidden'); playerControls.classList.add('hidden');
  finishScreen.classList.remove('hidden'); backToSlidesBtn.classList.remove('hidden');
  topbarModeHint.textContent = 'Session Analysis';

  const report = buildAnalysisReport(), markdown = buildAnalysisMarkdown(report), metricBySlideId = new Map(report.slides.map(s => [s.id, s]));
  activeLeafEnterMs = null;
  finishScreen.innerHTML = '';
  const body = mk('div', 'mby-card-body');
  body.appendChild(mk('div', 'mby-summary-header', null, [mk('h2', null, report.title || report.workspaceId), mk('div', 'mby-summary-meta', `${report.workspaceId}  •  Completed in ${(report.totalDurationMs / 1000).toFixed(1)}s`)]));

  const grid = mk('div', 'mby-stats-grid', null, [
    ['Graded Score', `${report.summary.mcqCorrectFinal} / ${report.summary.mcqTotal}`],
    ['Accuracy', `${report.summary.mcqAccuracyPercent.toFixed(1)}%`],
    ['Flashcards Known', `${report.summary.flashcardKnown} / ${report.summary.flashcardTotal}`],
    ['Articles Viewed', `${report.summary.articleSlidesViewed}`]
  ].map(([l, v]) => mk('div', 'mby-stat-box', null, [mk('div', 'mby-stat-label', l), mk('div', 'mby-stat-value', v)])));
  body.appendChild(grid);

  if (report.weakItemIds.length === 0) {
    body.appendChild(mk('div', 'mby-review-box ok', 'All graded questions & flashcards look solid — no review flags raised.'));
  } else {
    const chips = mk('div', 'mby-weak-chips', null, report.weakItemIds.map(id => { const s = metricBySlideId.get(id); return mk('span', 'mby-weak-chip', s ? `#${s.slideIndex} ${s.kind.toUpperCase()} • ${id}` : id); }));
    body.appendChild(mk('div', 'mby-review-box warn', null, [mk('div', 'mby-review-title', `Needs Review (${report.weakItemIds.length})`), chips]));
  }

  body.appendChild(mk('strong', null, `Item-by-Item Breakdown (${report.slides.length})`));
  const list = mk('div', 'mby-breakdown-list', null, report.slides.map(s => {
    let statusText = s.visitedCount > 0 ? 'Viewed' : 'Skipped', statusCls = s.visitedCount > 0 ? 'accent' : 'muted', activity = `Visits: ${s.visitedCount}`;
    if (isGradedKind(s.kind)) { statusText = s.isCorrect === true ? 'Correct' : 'Missed'; statusCls = s.isCorrect === true ? 'ok' : 'bad'; activity = `Checks: ${s.checkAttempts || 0} • Visits: ${s.visitedCount}`; }
    else if (s.kind === 'flashcard') { statusText = s.flashcardRating === 'known' ? 'Known' : (s.flashcardRating === 'needsReview' ? 'Review' : 'Unrated'); statusCls = s.flashcardRating === 'known' ? 'ok' : (s.flashcardRating === 'needsReview' ? 'bad' : 'muted'); activity = `Flips: ${s.flipCount || 0} • Visits: ${s.visitedCount}`; }
    const left = mk('div', 'mby-breakdown-left'), right = mk('div', 'mby-breakdown-right');
    left.innerHTML = `<span class="mby-idx-badge">#${s.slideIndex}</span><div><div class="mby-item-kind">${escapeHtml(s.kind.toUpperCase())}</div><div class="mby-item-sub">${escapeHtml(activity)} • <code>${escapeHtml(s.id)}</code></div></div>`;
    right.innerHTML = `<span class="mby-status-pill ${statusCls}">${statusText}</span><span class="mby-item-sub">${(s.timeSpentMs / 1000).toFixed(1)}s</span>`;
    return mk('div', 'mby-breakdown-item', null, [left, right]);
  }));
  body.appendChild(list); body.appendChild(mk('hr', 'mby-divider'));

  const dlJson = mk('button', 'mby-nav-btn primary', 'Export .analysis.json'); dlJson.type = 'button';
  dlJson.addEventListener('click', () => downloadTextFile(report.workspaceId + '.analysis.json', JSON.stringify(report, null, 2), 'application/json'));
  const dlMd = mk('button', 'mby-nav-btn', 'Export .analysis.md'); dlMd.type = 'button';
  dlMd.addEventListener('click', () => downloadTextFile(report.workspaceId + '.analysis.md', markdown, 'text/markdown'));
  const statusEl = mk('div', 'mby-save-status');
  body.appendChild(mk('div', 'mby-export-stack', null, [mk('div', 'mby-export-row', null, [dlJson, dlMd])]));
  body.appendChild(statusEl);

  const card = mk('div', 'mby-card', null, [createUpperTab('SESSION SUMMARY'), body]);
  if (DATA.generator) { const f = mk('div', 'mby-generator-footer'); populateGeneratorFooter(f, true); card.appendChild(f); }
  finishScreen.appendChild(card);

  if (!autoSaveSent && triggerSave) { autoSaveSent = true; postAnalysisIfHosted(report, markdown, statusEl); }
}

function returnToSlides() {
  isFinished = false; autoSaveSent = false; finishScreen.classList.add('hidden');
  playerViewport.classList.remove('hidden'); playerControls.classList.remove('hidden'); backToSlidesBtn.classList.add('hidden');
  topbarModeHint.textContent = ''; activeLeafEnterMs = Date.now(); requestAnimationFrame(updateScrollDownHint);
}
const closePrevCompMenu = () => { if (prevCompMenu) prevCompMenu.classList.add('hidden'); };
function getActiveQuizContext() {
  const slide = DATA.slides[currentIndex], isQuiz = !!(slide?.kind === 'quiz' && slide.questions?.length);
  return { slide, isQuiz, qIdx: isQuiz ? (activeQuizQuestionIdx.get(slide.id) || 0) : 0, qCount: isQuiz ? slide.questions.length : 0 };
}
function isAtFinalSaveStep() {
  const { isQuiz, qIdx, qCount } = getActiveQuizContext();
  return (currentIndex >= DATA.slides.length - 1) && (!isQuiz || qIdx >= qCount - 1);
}
function getComponentDisplayName(slide) {
  return slide?.title || ({ article: 'Article', quiz: 'Quiz', mcq: 'MCQ', flashcard: 'Flashcard', 'fill-blank': 'Fill in the Blank', 'assertion-reason': 'Assertion–Reason', 'spot-error': 'Spot the Error', diff: 'Differentiation', mechanism: 'Mechanism', relabel: 'Relabel the Image' })[slide?.kind] || 'Component';
}
function updateNavButtonsUi() {
  const { isQuiz, qIdx } = getActiveQuizContext();
  prevBtn.disabled = (currentIndex === 0) && (!isQuiz || qIdx === 0);
  if (isAtFinalSaveStep()) { nextBtn.innerHTML = SAVE_ICON_SVG + '<span>Next</span>'; nextBtn.title = 'Save and Finish Session'; }
  else { nextBtn.textContent = 'Next →'; nextBtn.title = isQuiz ? 'Click for next question, hold to finish early' : 'Click for next component, hold to finish early'; }
}

const activeScrollAnimMap = new WeakMap();
function cancelScrollAnimation(slideEl) {
  if (!slideEl) return;
  const prevId = activeScrollAnimMap.get(slideEl);
  if (prevId) { cancelAnimationFrame(prevId); activeScrollAnimMap.delete(slideEl); }
}
function scrollSlideToTop(slideEl) {
  if (!slideEl) return;
  cancelScrollAnimation(slideEl);
  const startTop = slideEl.scrollTop; if (startTop <= 0) { slideEl.scrollTop = 0; return; }
  const startMs = performance.now();
  function step(now) {
    const t = Math.min(1, (now - startMs) / 220);
    slideEl.scrollTop = Math.round(startTop * (1 - Math.pow(1 - t, 3)));
    if (t < 1) activeScrollAnimMap.set(slideEl, requestAnimationFrame(step));
    else { slideEl.scrollTop = 0; activeScrollAnimMap.delete(slideEl); updateScrollDownHint(); }
  }
  activeScrollAnimMap.set(slideEl, requestAnimationFrame(step));
}

function goToSlide(idx, targetQIdx = null, opts = {}) {
  if (DATA.slides.length === 0) return;
  const shouldScrollToTop = opts?.scrollToTop !== false;
  closePrevCompMenu(); flushActiveLeafTime();
  currentIndex = Math.max(0, Math.min(idx, DATA.slides.length - 1));
  const slide = DATA.slides[currentIndex];
  if (slide?.kind === 'quiz' && targetQIdx !== null && targetQIdx !== undefined) {
    quizControllers.get(slide.id)?.selectQuestion(targetQIdx, { recordVisit: false, flushPrevious: false, scrollToTop: shouldScrollToTop });
  }
  enterActiveLeaf();
  track.style.transform = 'translateX(-' + (currentIndex * 100) + '%)';
  updateNavButtonsUi();
  Array.from(dotsNav.children).forEach((d, i) => d.classList.toggle('active', i === currentIndex));
  if (shouldScrollToTop) scrollSlideToTop(track.children[currentIndex]);
  else cancelScrollAnimation(track.children[currentIndex]);
  requestAnimationFrame(() => { if (slide?.kind === 'quiz') quizControllers.get(slide.id)?.syncHeight(); updateScrollDownHint(); });
}
function navigatePrevious() {
  closePrevCompMenu();
  const { slide, isQuiz, qIdx } = getActiveQuizContext();
  if (isQuiz && qIdx > 0) quizControllers.get(slide.id)?.selectQuestion(qIdx - 1);
  else if (currentIndex > 0) goToSlide(currentIndex - 1);
}
function navigateNext() {
  closePrevCompMenu();
  const { slide, isQuiz, qIdx, qCount } = getActiveQuizContext();
  if (isQuiz && qIdx < qCount - 1) quizControllers.get(slide.id)?.selectQuestion(qIdx + 1);
  else if (currentIndex >= DATA.slides.length - 1) openFinishScreen(true);
  else goToSlide(currentIndex + 1);
}

function renderSlides() {
  document.documentElement.style.setProperty('--slide-ms', (DATA.slideTransitionMs || '380') + 'ms');
  greetingMeta.textContent = `${DATA.slides.length} component(s)  •  ${leafSlides.length} item(s)`;
  populateGeneratorFooter(greetingGeneratorFooter, false);

  DATA.slides.forEach((slide, idx) => {
    const slideEl = mk('section', 'mby-slide');
    if (slide.kind === 'article') {
      const card = mk('div', 'mby-card', null, [createUpperTab(slide.title ? 'ARTICLE • ' + slide.title : 'ARTICLE')]); applySlideLayoutVars(card, slide.layout);
      const stack = mk('div', 'mby-article-blocks', null, (slide.articleBlocks || []).map(b => {
        if (b.kind === 'text' && b.textHtml) {
          const d = mk('div');
          d.dataset.blockId = b.id;
          d.innerHTML = b.textHtml;
          renderMathIn(d);
          return d;
        }
        if (b.kind === 'callout' && b.textHtml) {
          const tone = b.tone || 'pearl';
          const hdr = mk('div', 'mby-callout-header');
          if (b.titleHtml) { hdr.innerHTML = b.titleHtml; renderMathIn(hdr); }
          else { hdr.textContent = b.title || calloutDefaultTitle(tone); }
          const body = mk('div', 'mby-callout-body');
          body.innerHTML = b.textHtml;
          renderMathIn(body);
          const aside = mk('aside', `mby-callout mby-callout-${tone}`, null, [hdr, body]);
          aside.setAttribute('role', 'note');
          aside.dataset.blockId = b.id;
          return aside;
        }
        if (b.kind === 'table' && b.table) {
          return buildNativeTableBlock(b);
        }
        if (b.imagePath) {
          const plainCap = htmlToPlainText(b.captionHtml);
          const figChildren = [makeZoomImg(b.imagePath, b.alt || plainCap)];
          if (b.captionHtml) {
            const figCap = mk('figcaption', 'mby-figcaption');
            figCap.innerHTML = b.captionHtml;
            renderMathIn(figCap);
            figChildren.push(figCap);
          }
          const fig = mk('figure', 'mby-figure', null, figChildren);
          fig.dataset.blockId = b.id;
          return fig;
        }
        return null;
      }));
      card.appendChild(mk('div', 'mby-card-body', null, [stack]));
      slideEl.appendChild(card);
    } else if (slide.kind === 'quiz') slideEl.appendChild(buildBundledQuiz(slide));
    else if (slide.kind === 'mcq') slideEl.appendChild(buildMcqCard(slide, null));
    else if (slide.kind === 'flashcard') slideEl.appendChild(buildFlashcardPanel(slide, null));
    else if (slide.kind === 'fill-blank') slideEl.appendChild(buildFillBlankCard(slide, null));
    else if (slide.kind === 'assertion-reason') slideEl.appendChild(buildAssertionReasonCard(slide, null));
    else if (slide.kind === 'spot-error') slideEl.appendChild(buildSpotErrorCard(slide, null));
    else if (slide.kind === 'diff') slideEl.appendChild(buildDiffCard(slide, null));
    else if (slide.kind === 'mechanism') slideEl.appendChild(buildMechanismCard(slide, null));
    else if (slide.kind === 'relabel') slideEl.appendChild(buildRelabelCard(slide, null));

    slideEl.addEventListener('scroll', updateScrollDownHint, { passive: true });
    track.appendChild(slideEl);
    const dot = mk('button', 'mby-dot'); dot.type = 'button'; dot.title = getComponentDisplayName(slide); dot.addEventListener('click', () => goToSlide(idx)); dotsNav.appendChild(dot);
  });

  track.addEventListener('transitionend', e => { if (e.target === track && e.propertyName === 'transform') requestAnimationFrame(updateScrollDownHint); });
  window.addEventListener('resize', () => requestAnimationFrame(updateScrollDownHint));
  if (scrollDownBtn) scrollDownBtn.addEventListener('click', () => track.children[currentIndex]?.scrollBy({ top: 180, behavior: 'smooth' }));
}

startSessionBtn.addEventListener('click', () => {
  greetingScreen.classList.add('hidden'); playerTopbar.classList.remove('hidden'); playerViewport.classList.remove('hidden'); playerControls.classList.remove('hidden');
  sessionStartedAt = new Date(); activeLeafEnterMs = null; lastVisitedLeafId = null; goToSlide(0);
});
backToSlidesBtn.addEventListener('click', returnToSlides);

function bindLongPress(btn, opts) {
  let timer = null, triggered = false;
  const clear = () => { if (timer !== null) { clearTimeout(timer); timer = null; } };
  btn.addEventListener('pointerdown', e => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    triggered = false;
    if (opts.canTrigger()) timer = setTimeout(() => { triggered = true; opts.onLongPress(); }, opts.delayMs);
  });
  ['pointerup', 'pointerleave', 'pointercancel'].forEach(ev => btn.addEventListener(ev, clear));
  if (opts.preventContextMenu) btn.addEventListener('contextmenu', e => { if (opts.canTrigger()) e.preventDefault(); });
  btn.addEventListener('click', () => { if (triggered) { triggered = false; return; } opts.onClick(); });
}

bindLongPress(prevBtn, { delayMs: 500, preventContextMenu: true, canTrigger: () => getActiveQuizContext().isQuiz && currentIndex > 0,
  onLongPress: () => { const prevSlide = DATA.slides[currentIndex - 1]; prevCompMenuItem.textContent = 'Previous ' + getComponentDisplayName(prevSlide); prevCompMenu.classList.remove('hidden'); }, onClick: navigatePrevious });
prevCompMenuItem.addEventListener('click', e => { e.stopPropagation(); closePrevCompMenu(); if (currentIndex > 0) goToSlide(currentIndex - 1); });
document.addEventListener('click', e => { if (!prevCompMenu.classList.contains('hidden') && !prevCompMenu.contains(e.target) && e.target !== prevBtn) closePrevCompMenu(); });
bindLongPress(nextBtn, { delayMs: 550, preventContextMenu: false, canTrigger: () => !isAtFinalSaveStep(), onLongPress: () => earlyFinishModal.classList.remove('hidden'), onClick: navigateNext });
continueSessionBtn.addEventListener('click', () => earlyFinishModal.classList.add('hidden'));
finishBtn.addEventListener('click', () => { earlyFinishModal.classList.add('hidden'); openFinishScreen(true); });
const SEARCH_INDEX = (() => {
  const entries = [];
  const collectQuestionSearchText = (q, parts) => {
    if (q.mcq) {
      parts.push(htmlToPlainText(q.mcq.promptHtml));
      q.mcq.options.forEach(o => { if (o.textHtml) parts.push(htmlToPlainText(o.textHtml)); });
      if (q.mcq.explanationHtml) parts.push(htmlToPlainText(q.mcq.explanationHtml));
    } else if (q.flashcard) {
      parts.push(htmlToPlainText(q.flashcard.front.textHtml));
      parts.push(htmlToPlainText(q.flashcard.back.textHtml));
    } else if (q.fillBlank) {
      parts.push(htmlToPlainText(q.fillBlank.promptHtml).replace(/MBYBLANKSLOT\d+END/g, ' ___ '));
      (q.fillBlank.blanks || []).forEach(b => parts.push((b.answers || []).join(' ')));
      if (q.fillBlank.explanationHtml) parts.push(htmlToPlainText(q.fillBlank.explanationHtml));
    } else if (q.assertionReason) {
      parts.push(htmlToPlainText(q.assertionReason.assertionHtml));
      parts.push(htmlToPlainText(q.assertionReason.reasonHtml));
      if (q.assertionReason.explanationHtml) parts.push(htmlToPlainText(q.assertionReason.explanationHtml));
    } else if (q.spotError) {
      (q.spotError.segments || []).forEach(s => { parts.push(htmlToPlainText(s.html)); if (s.corrections) parts.push(s.corrections.join(' ')); });
      if (q.spotError.explanationHtml) parts.push(htmlToPlainText(q.spotError.explanationHtml));
    } else if (q.differentiation) {
      if (q.differentiation.titleHtml) parts.push(htmlToPlainText(q.differentiation.titleHtml));
      (q.differentiation.headersHtml || []).forEach(h => parts.push(htmlToPlainText(h)));
      (q.differentiation.rowsHtml || []).forEach(r => r.forEach(c => parts.push(htmlToPlainText(c).replace(/MBYDIFFSLOT\d+END/g, ' ___ '))));
      (q.differentiation.specialCells || []).forEach(sc => { if (sc.answers) parts.push(sc.answers.join(' ')); if (sc.corrections) parts.push(sc.corrections.join(' ')); });
      if (q.differentiation.explanationHtml) parts.push(htmlToPlainText(q.differentiation.explanationHtml));
    } else if (q.mechanism) {
      if (q.mechanism.titleHtml) parts.push(htmlToPlainText(q.mechanism.titleHtml));
      (q.mechanism.steps || []).forEach(s => parts.push(htmlToPlainText(s.html)));
      if (q.mechanism.explanationHtml) parts.push(htmlToPlainText(q.mechanism.explanationHtml));
    } else if (q.relabel) {
      if (q.relabel.promptHtml) parts.push(htmlToPlainText(q.relabel.promptHtml));
      (q.relabel.boxes || []).forEach(b => { parts.push(b.label); if (b.aliases) parts.push(b.aliases.join(' ')); });
      if (q.relabel.explanationHtml) parts.push(htmlToPlainText(q.relabel.explanationHtml));
    }
  };
  DATA.slides.forEach((slide, slideIdx) => {
    const slideName = getComponentDisplayName(slide);
    if (slide.kind === 'article') {
      (slide.articleBlocks || []).forEach(b => {
        const parts = [];
        if (slide.title) parts.push(slide.title);
        if (b.title) parts.push(b.title);
        if (b.textHtml) parts.push(htmlToPlainText(b.textHtml));
        if (b.captionHtml) parts.push(htmlToPlainText(b.captionHtml));
        if (b.alt) parts.push(b.alt);
        if (b.table) {
          if (b.table.title) parts.push(b.table.title);
          (b.table.headersHtml || []).forEach(h => parts.push(htmlToPlainText(h)));
          (b.table.rowsHtml || []).forEach(r => r.forEach(c => parts.push(htmlToPlainText(c))));
          if (b.table.captionHtml) parts.push(htmlToPlainText(b.table.captionHtml));
          if (b.table.footnoteHtml) parts.push(htmlToPlainText(b.table.footnoteHtml));
        }
        const rawText = parts.join(' ').replace(/\s+/g, ' ').trim();
        if (rawText) entries.push({ slideIdx, qIdx: null, blockId: b.id, title: `${slideName} • ${b.kind.toUpperCase()}`, rawText, lower: rawText.toLowerCase() });
      });
    } else if (slide.kind === 'quiz' && Array.isArray(slide.questions)) {
      slide.questions.forEach((q, qIdx) => {
        const parts = [slideName];
        collectQuestionSearchText(q, parts);
        const rawText = parts.join(' ').replace(/\s+/g, ' ').trim();
        if (rawText) entries.push({ slideIdx, qIdx, blockId: null, title: `${slideName} • Q${qIdx + 1}`, rawText, lower: rawText.toLowerCase() });
      });
    } else if (slide.mcq || slide.flashcard || slide.fillBlank || slide.assertionReason || slide.spotError || slide.differentiation || slide.mechanism || slide.relabel) {
      const parts = [slideName];
      collectQuestionSearchText(slide, parts);
      const rawText = parts.join(' ').replace(/\s+/g, ' ').trim();
      if (rawText) entries.push({ slideIdx, qIdx: null, blockId: null, title: slideName, rawText, lower: rawText.toLowerCase() });
    }
  });
  return entries;
})();

let activeSearchIdx = 0;
let currentSearchMatches = [];

function highlightMatchSnippet(rawText, tokens) {
  let center = 0;
  const lower = rawText.toLowerCase();
  for (const t of tokens) {
    const pos = lower.indexOf(t);
    if (pos >= 0) { center = pos; break; }
  }
  const start = Math.max(0, center - 45);
  const end = Math.min(rawText.length, start + 165);
  const slice = (start > 0 ? '...' : '') + rawText.slice(start, end) + (end < rawText.length ? '...' : '');
  if (tokens.length === 0) return escapeHtml(slice);
  const uniqueTokens = Array.from(new Set(tokens)).sort((a, b) => b.length - a.length);
  const pattern = uniqueTokens.map(t => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|');
  const regex = new RegExp(pattern, 'gi');
  let out = '', lastIdx = 0, match;
  while ((match = regex.exec(slice)) !== null) {
    if (match[0].length === 0) { regex.lastIndex += 1; continue; }
    if (match.index > lastIdx) out += escapeHtml(slice.slice(lastIdx, match.index));
    out += '<mark>' + escapeHtml(match[0]) + '</mark>';
    lastIdx = match.index + match[0].length;
  }
  if (lastIdx < slice.length) out += escapeHtml(slice.slice(lastIdx));
  return out;
}

function renderSearchResults() {
  const q = (searchInput?.value || '').trim().toLowerCase();
  const tokens = q.split(/\s+/).filter(Boolean);
  currentSearchMatches = tokens.length === 0
    ? SEARCH_INDEX.slice(0, 20)
    : SEARCH_INDEX.filter(item => tokens.every(t => item.lower.includes(t))).slice(0, 40);
  if (activeSearchIdx >= currentSearchMatches.length) activeSearchIdx = Math.max(0, currentSearchMatches.length - 1);
  if (searchMetaEl) searchMetaEl.textContent = tokens.length === 0 ? `Showing ${currentSearchMatches.length} of ${SEARCH_INDEX.length} indexed item(s)` : `${currentSearchMatches.length} match(es) for "${searchInput.value.trim()}"`;
  searchResultsEl.innerHTML = '';
  if (currentSearchMatches.length === 0) {
    searchResultsEl.appendChild(mk('div', 'mby-search-empty', 'No matching study blocks or questions found.'));
    return;
  }
  currentSearchMatches.forEach((item, idx) => {
    const btn = mk('button', 'mby-search-item' + (idx === activeSearchIdx ? ' active' : ''));
    btn.type = 'button';
    btn.appendChild(mk('div', 'mby-search-item-title', item.title));
    const snip = mk('div', 'mby-search-item-snippet');
    snip.innerHTML = highlightMatchSnippet(item.rawText, tokens);
    btn.appendChild(snip);
    btn.addEventListener('click', () => jumpToSearchEntry(item));
    searchResultsEl.appendChild(btn);
  });
}

function openSearchModal() {
  if (!searchModal) return;
  searchModal.classList.remove('hidden');
  activeSearchIdx = 0;
  renderSearchResults();
  requestAnimationFrame(() => { searchInput?.focus(); searchInput?.select(); });
}
function closeSearchModal() { if (searchModal) searchModal.classList.add('hidden'); }
function jumpToSearchEntry(item) {
  closeSearchModal();
  if (!greetingScreen.classList.contains('hidden')) {
    greetingScreen.classList.add('hidden'); playerTopbar.classList.remove('hidden'); playerViewport.classList.remove('hidden'); playerControls.classList.remove('hidden');
    sessionStartedAt = new Date(); activeLeafEnterMs = null; lastVisitedLeafId = null;
  } else if (isFinished) {
    returnToSlides();
  }
  goToSlide(item.slideIdx, item.qIdx, { scrollToTop: !item.blockId });
  if (item.blockId) {
    requestAnimationFrame(() => {
      const slideEl = track.children[item.slideIdx];
      cancelScrollAnimation(slideEl);
      const targetEl = slideEl?.querySelector(`[data-block-id="${CSS.escape(item.blockId)}"]`);
      if (targetEl) {
        targetEl.scrollIntoView({ behavior: 'smooth', block: 'center' });
        targetEl.classList.add('mby-search-pulse');
        setTimeout(() => targetEl.classList.remove('mby-search-pulse'), 1600);
      }
    });
  }
}

topbarSearchBtn?.addEventListener('click', openSearchModal);
greetingSearchBtn?.addEventListener('click', openSearchModal);
searchCloseBtn?.addEventListener('click', closeSearchModal);
searchModal?.addEventListener('click', e => { if (e.target === searchModal) closeSearchModal(); });
searchInput?.addEventListener('input', () => { activeSearchIdx = 0; renderSearchResults(); });

document.addEventListener('keydown', e => {
  if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
    e.preventDefault();
    if (searchModal && !searchModal.classList.contains('hidden')) closeSearchModal();
    else openSearchModal();
    return;
  }
  if (!searchModal?.classList.contains('hidden')) {
    if (e.key === 'Escape') { e.preventDefault(); closeSearchModal(); return; }
    if (e.key === 'ArrowDown' && currentSearchMatches.length > 0) {
      e.preventDefault();
      activeSearchIdx = Math.min(currentSearchMatches.length - 1, activeSearchIdx + 1);
      Array.from(searchResultsEl.children).forEach((el, i) => el.classList.toggle('active', i === activeSearchIdx));
      searchResultsEl.children[activeSearchIdx]?.scrollIntoView({ block: 'nearest' });
      return;
    }
    if (e.key === 'ArrowUp' && currentSearchMatches.length > 0) {
      e.preventDefault();
      activeSearchIdx = Math.max(0, activeSearchIdx - 1);
      Array.from(searchResultsEl.children).forEach((el, i) => el.classList.toggle('active', i === activeSearchIdx));
      searchResultsEl.children[activeSearchIdx]?.scrollIntoView({ block: 'nearest' });
      return;
    }
    if (e.key === 'Enter' && currentSearchMatches[activeSearchIdx]) {
      e.preventDefault();
      jumpToSearchEntry(currentSearchMatches[activeSearchIdx]);
      return;
    }
    return;
  }
  const tag = (e.target?.tagName || '').toLowerCase();
  const isTyping = tag === 'input' || tag === 'textarea' || e.target?.isContentEditable;
  if (e.key === '/' && !isTyping && !e.ctrlKey && !e.metaKey && !e.altKey) {
    e.preventDefault();
    openSearchModal();
    return;
  }
  if (!imageZoomModal.classList.contains('hidden')) { if (e.key === 'Escape') closeZoomLightbox(); return; }
  if (!earlyFinishModal.classList.contains('hidden')) { if (e.key === 'Escape') earlyFinishModal.classList.add('hidden'); return; }
  if (!prevCompMenu.classList.contains('hidden') && e.key === 'Escape') { closePrevCompMenu(); return; }
  if (!isTyping && greetingScreen.classList.contains('hidden') && !isFinished) { if (e.key === 'ArrowLeft') navigatePrevious(); if (e.key === 'ArrowRight') navigateNext(); }
});
renderSlides();
""";

    private sealed record WebBundlePayload(string Id, string Title, string? Description, string ThemeMode, string SlideTransitionMs, WebGeneratorInfoModel Generator, List<SlideRenderModel> Slides);
    private sealed record WebGeneratorInfoModel(string ToolName, string Executable, string Version, string Runtime, string GeneratedAtUtc);
    private sealed record SlideRenderModel(
        string Id,
        string Kind,
        Dictionary<string, string> Layout,
        McqRenderModel? Mcq,
        FlashcardRenderModel? Flashcard,
        List<ArticleBlockRenderModel>? ArticleBlocks,
        List<SlideRenderModel>? Questions,
        string? Title = null,
        FillBlankRenderModel? FillBlank = null,
        AssertionReasonRenderModel? AssertionReason = null,
        SpotErrorRenderModel? SpotError = null,
        DifferentiationRenderModel? Differentiation = null,
        MechanismRenderModel? Mechanism = null,
        RelabelRenderModel? Relabel = null);
    private sealed record ArticleBlockRenderModel(
        string Id,
        string Kind,
        string? TextHtml,
        string? ImagePath,
        string? CaptionHtml = null,
        string? Alt = null,
        string? Tone = null,
        string? Title = null,
        TableRenderModel? Table = null,
        string? TitleHtml = null);
    private sealed record TableRenderModel(
        string? Title,
        string? CaptionHtml,
        bool StickyFirstCol,
        List<int> HideableCols,
        List<string> HeadersHtml,
        List<string> Alignments,
        List<List<string>> RowsHtml,
        string? FootnoteHtml,
        string? TitleHtml = null);
    private sealed record McqRenderModel(string PromptHtml, bool IsMultiCorrect, List<McqOptionRenderModel> Options, string? ImagePath = null, string? ExplanationHtml = null);
    private sealed record McqOptionRenderModel(int Index, string? TextHtml, string? ImagePath, bool IsCorrect);
    private sealed record FlashcardRenderModel(FlashcardSideRenderModel Front, FlashcardSideRenderModel Back);
    private sealed record FlashcardSideRenderModel(string? TextHtml, string? ImagePath, string Position);
    private sealed record FillBlankSlotRenderModel(int Index, List<string> Answers, string? PrimaryAnswerHtml = null);
    private sealed record FillBlankRenderModel(string PromptHtml, List<FillBlankSlotRenderModel> Blanks, bool Fuzzy, bool CaseSensitive, string? ExplanationHtml);
    private sealed record AssertionReasonOptionRenderModel(string Code, string Label);
    private sealed record AssertionReasonRenderModel(string AssertionHtml, string ReasonHtml, string Answer, string Scheme, List<AssertionReasonOptionRenderModel> Options, string? ExplanationHtml);
    private sealed record SpotErrorSegmentRenderModel(int? SpanIndex, string Html, bool IsCandidate, bool IsError, List<string>? Corrections, string? PrimaryCorrectionHtml = null);
    private sealed record SpotErrorRenderModel(List<SpotErrorSegmentRenderModel> Segments, bool RequireFix, bool AllowNoError, bool NoError, string? ExplanationHtml);
    private sealed record DiffCellRenderModel(
        int RowIndex,
        int ColIndex,
        int SlotIndex,
        bool IsBlank,
        List<string>? Answers,
        string? PrimaryAnswerHtml,
        bool IsError,
        string? WrongHtml,
        List<string>? Corrections,
        string? PrimaryCorrectionHtml);
    private sealed record DifferentiationRenderModel(
        string Mode,
        string? Title,
        string? TitleHtml,
        bool Bank,
        bool Fuzzy,
        bool CaseSensitive,
        bool AllowNoError,
        List<string> HeadersHtml,
        List<string> Alignments,
        List<List<string>> RowsHtml,
        List<List<string>> RowsRaw,
        List<DiffCellRenderModel> SpecialCells,
        string? ExplanationHtml);
    private sealed record MechanismStepRenderModel(int StepId, int OrderGroup, string Html, bool IsDistractor);
    private sealed record MechanismRenderModel(string? Title, string? TitleHtml, List<MechanismStepRenderModel> Steps, string? ExplanationHtml);
    private sealed record RelabelBoxRenderModel(int Index, string Label, List<string> Aliases, double X, double Y, double W, double H);
    private sealed record RelabelRenderModel(
        string ImagePath,
        string? PromptHtml,
        string Mode,
        string Style,
        bool Fuzzy,
        bool CaseSensitive,
        List<RelabelBoxRenderModel> Boxes,
        List<string> Distractors,
        string? ExplanationHtml);
}
