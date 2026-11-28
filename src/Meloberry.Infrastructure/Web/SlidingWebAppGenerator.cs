using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Meloberry.Infrastructure.Web.Components;

namespace Meloberry.Infrastructure.Web;

public sealed partial class SlidingWebAppGenerator(
    ICliToolMetadataProvider metadataProvider,
    ChainTransactionManager? transactionManager = null,
    IWorkspaceRepository? workspaceRepository = null) : IWebAppGenerator
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    [GeneratedRegex(@"(?<attr>\b(?:href|src)\s*=\s*)(?<quote>[""'])(?<url>[^""']*)\k<quote>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MarkdownUrlAttributeRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagStripRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceCollapseRegex();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string RenderSafeMarkdownHtml(string markdown) =>
        MarkdownUrlAttributeRegex().Replace(Markdown.ToHtml(markdown, MarkdownPipeline), static match =>
        {
            var rawAttr = match.Groups["attr"].Value;
            var q = match.Groups["quote"].Value;
            var decoded = WebUtility.HtmlDecode(match.Groups["url"].Value);
            var normalized = new string(decoded.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
            if (normalized.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return $"{rawAttr}{q}#{q}";
            }

            if (rawAttr.TrimStart().StartsWith("href", StringComparison.OrdinalIgnoreCase) &&
                ArticleLinkRules.TryParseMbyUri(decoded, out var mbyTarget))
            {
                if (mbyTarget.Kind == "card")
                {
                    return $"href={q}#{q} class={q}mby-native-link{q} data-mby-kind={q}card{q} data-mby-target={q}{WebUtility.HtmlEncode(mbyTarget.TargetId)}{q}";
                }
                if (mbyTarget.Kind == "article")
                {
                    var artHash = $"#article={Uri.EscapeDataString(mbyTarget.TargetId)}";
                    return $"href={q}{WebUtility.HtmlEncode(artHash)}{q} class={q}mby-native-link{q} data-mby-kind={q}article{q} data-mby-target={q}{WebUtility.HtmlEncode(mbyTarget.TargetId)}{q}";
                }
                if (mbyTarget.Kind == "session")
                {
                    var resolvedHref = ArticleLinkRules.ResolveSessionHref(mbyTarget);
                    var previewDir = ArticleLinkRules.ResolveSessionPreviewDir(resolvedHref);
                    var subAttr = string.IsNullOrWhiteSpace(mbyTarget.SubTargetId) ? string.Empty : $" data-mby-sub={q}{WebUtility.HtmlEncode(mbyTarget.SubTargetId)}{q}";
                    return $"href={q}{WebUtility.HtmlEncode(resolvedHref)}{q} class={q}mby-native-link{q} data-mby-kind={q}session{q} data-mby-target={q}{WebUtility.HtmlEncode(mbyTarget.TargetId)}{q}{subAttr} data-mby-preview-dir={q}{WebUtility.HtmlEncode(previewDir)}{q}";
                }
            }

            return match.Value;
        });

    private static string? EncodeAssetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;
        return string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }

    private static string ResolveDeckGlobalValue(IReadOnlyDictionary<string, LayoutProfile> layouts, string propertyName, string fallbackValue) =>
        LayoutBoundsCatalog.ResolveDeckGlobalValue(layouts, propertyName, fallbackValue);

    public async Task<Result<string>> GenerateForSessionAsync(
        StudySession session, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputDirectory, bool includeAllThemes = false, CancellationToken cancellationToken = default)
    {
        var slides = new List<SlideRenderModel>();
        var linkCards = new Dictionary<string, LinkCardRenderModel>(StringComparer.OrdinalIgnoreCase);
        var referencedSessionIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var comp in session.Components)
        {
            if (comp.Kind == ComponentKind.Article)
            {
                slides.Add(BuildArticleSlide(comp, layouts));
                foreach (var lc in comp.LinkCards)
                {
                    linkCards[lc.Id] = new LinkCardRenderModel(
                        lc.Id,
                        lc.Title,
                        !string.IsNullOrWhiteSpace(lc.Text) ? RenderSafeMarkdownHtml(lc.Text) : null,
                        EncodeAssetPath(lc.ImagePath));
                }
                foreach (var mdStr in Phase3PayloadRules.EnumerateArticleBlockMarkdownStrings(comp))
                {
                    foreach (var parsed in ArticleLinkRules.ExtractMbyLinksFromMarkdown(mdStr))
                    {
                        if (parsed.Kind == "session")
                            referencedSessionIds.Add(parsed.TargetId);
                    }
                }
            }
            else if (comp.Quiz is not null && comp.Quiz.Questions.Count > 0)
            {
                var baseProfile = comp.Quiz.Type == QuizType.Flashcard ? "flashcard" : "mcq";
                var mergedQuizLayout = MergeProfile(layouts, baseProfile, comp.LayoutOverrides, null);
                var questionSlides = comp.Quiz.Questions.Select(q => BuildQuestionSlide(q, layouts, comp.LayoutOverrides)).ToList();
                slides.Add(new SlideRenderModel(comp.Id, "quiz", mergedQuizLayout, null, null, null, questionSlides, comp.Title, RefKey: comp.RefKey, ComponentIndex: comp.Index));
            }
        }

        var externalPreviews = new Dictionary<string, SessionPreviewSidecarModel>(StringComparer.Ordinal);
        if (workspaceRepository is not null && referencedSessionIds.Count > 0)
        {
            foreach (var extId in referencedSessionIds)
            {
                var extLookup = await workspaceRepository.FindSessionAsync(extId, cancellationToken);
                if (extLookup?.Session is { } extSession)
                {
                    var extSlides = extSession.Components
                        .Where(c => c.Kind == ComponentKind.Article)
                        .Select(c => BuildArticleSlide(c, layouts))
                        .ToList();
                    var extTitle = string.IsNullOrWhiteSpace(extSession.Name) ? extSession.Id : extSession.Name;
                    externalPreviews[extId] = BuildSessionPreviewSidecar(extSession.Id, extTitle, extSession.Description, extSlides);
                }
            }
        }

        var title = string.IsNullOrWhiteSpace(session.Name) ? "Meloberry Session" : session.Name;
        return await WriteWebAppBundleAsync(
            session.Id, title, session.Description, storageDirectory, slides, layouts,
            MbypkgExporter.CollectSessionAssets(session), outputDirectory,
            linkCards.Count > 0 ? linkCards : null,
            externalPreviews.Count > 0 ? externalPreviews : null,
            isSession: true,
            includeAllThemes,
            cancellationToken);
    }

    public Task<Result<string>> GenerateForQuizAsync(
        Quiz quiz, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputDirectory, bool includeAllThemes = false, CancellationToken cancellationToken = default)
    {
        var title = string.IsNullOrWhiteSpace(quiz.Name) ? "Meloberry Quiz" : quiz.Name;
        var desc = string.IsNullOrWhiteSpace(quiz.Description) ? $"Standalone quiz ({quiz.Questions.Count} questions)" : quiz.Description;
        return WriteWebAppBundleAsync(quiz.Id, title, desc, storageDirectory,
            quiz.Questions.Select(q => BuildQuestionSlide(q, layouts, null)).ToList(), layouts, MbypkgExporter.CollectQuizAssets(quiz), outputDirectory, null, null, isSession: false, includeAllThemes, cancellationToken);
    }

    private static SlideRenderModel BuildArticleSlide(SessionComponent comp, IReadOnlyDictionary<string, LayoutProfile> layouts) =>
        new(comp.Id, "article", MergeProfile(layouts, "article", comp.LayoutOverrides, null), null, null,
            comp.ArticleBlocks.Select(BuildArticleBlockRenderModel).ToList(), null, comp.Title, RefKey: comp.RefKey, ComponentIndex: comp.Index);

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
                    RenderOptionalTitleHtml(p.Title));
            }
            return new ArticleBlockRenderModel(
                b.Id, "table", null, null,
                Title: b.CalloutTitle,
                Table: tableModel,
                TitleHtml: RenderOptionalTitleHtml(b.CalloutTitle));
        }

        if (b.Kind == ArticleBlockKind.DiagramExplore)
        {
            DiagramExploreRenderModel? diagModel = null;
            if (Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(b.PayloadJson, out var dp) && dp is not null)
            {
                var imgPath = EncodeAssetPath(!string.IsNullOrWhiteSpace(dp.ImagePath) ? dp.ImagePath : b.ImagePath) ?? string.Empty;
                var hotspots = (dp.Hotspots ?? [])
                    .Where(h => h is not null)
                    .Select(h => new DiagramHotspotRenderModel(h.Index, h.Label, RenderSafeMarkdownHtml(h.Note), h.X, h.Y, h.W, h.H))
                    .ToList();
                var diagTitle = dp.Title ?? b.CalloutTitle;
                diagModel = new DiagramExploreRenderModel(
                    imgPath,
                    diagTitle,
                    RenderOptionalTitleHtml(diagTitle),
                    !string.IsNullOrWhiteSpace(dp.Caption ?? b.Caption) ? RenderSafeMarkdownHtml((dp.Caption ?? b.Caption)!) : null,
                    b.Alt,
                    hotspots);
            }
            return new ArticleBlockRenderModel(
                b.Id, "diagram-explore", null, EncodeAssetPath(b.ImagePath),
                CaptionHtml: !string.IsNullOrWhiteSpace(b.Caption) ? RenderSafeMarkdownHtml(b.Caption) : null,
                Alt: b.Alt,
                Title: b.CalloutTitle,
                TitleHtml: RenderOptionalTitleHtml(b.CalloutTitle),
                DiagramExplore: diagModel);
        }

        if (b.Kind == ArticleBlockKind.Reveal)
        {
            RevealBlockRenderModel? revModel = null;
            if (Phase1PayloadCodec.TryDeserialize<RevealBlockPayload>(b.PayloadJson, out var rp) && rp is not null)
            {
                var steps = (rp.Steps ?? [])
                    .Where(s => s is not null)
                    .Select(s => new RevealStepRenderModel(s.Index, RenderInlineSafeMarkdownHtml(s.Heading), RenderSafeMarkdownHtml(s.Body)))
                    .ToList();
                var revTitle = rp.Title ?? b.CalloutTitle;
                revModel = new RevealBlockRenderModel(
                    revTitle,
                    RenderOptionalTitleHtml(revTitle),
                    rp.ShowFirst,
                    steps);
            }
            return new ArticleBlockRenderModel(
                b.Id, "reveal", null, null,
                Title: b.CalloutTitle,
                TitleHtml: RenderOptionalTitleHtml(b.CalloutTitle),
                Reveal: revModel);
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
            TitleHtml: RenderOptionalTitleHtml(b.CalloutTitle));
    }

    private static string? RenderOptionalTitleHtml(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var rendered = RenderInlineSafeMarkdownHtml(title);
        return string.Equals(rendered, WebUtility.HtmlEncode(title.Trim()), StringComparison.Ordinal) ? null : rendered;
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
            var fullLayout = MergeProfileRaw(layouts, "flashcard", componentOverrides, card.LayoutOverrides);
            var prunedLayout = PruneLayoutForWire("flashcard", layouts, fullLayout);
            return new SlideRenderModel(card.Id, "flashcard", prunedLayout, null,
                new FlashcardRenderModel(BuildSideRenderModel(card.Front, fullLayout), BuildSideRenderModel(card.Back, fullLayout)), null, null);
        }

        var fullQuestionLayout = MergeProfileRaw(layouts, "mcq", componentOverrides, question.LayoutOverrides);
        var mergedLayout = PruneLayoutForWire("mcq", layouts, fullQuestionLayout);
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
                FillBlank: new FillBlankRenderModel(promptHtml, blanks, fb.Fuzzy, fb.CaseSensitive, expHtml, fb.Placeholder));
        }

        if (question.Type == QuestionType.AssertionReason && Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(question.PayloadJson, out var ar) && ar is not null)
        {
            var expHtml = !string.IsNullOrWhiteSpace(ar.Explanation) ? RenderSafeMarkdownHtml(ar.Explanation) : null;
            return new SlideRenderModel(question.Id, "assertion-reason", mergedLayout, null, null, null, null,
                AssertionReason: new AssertionReasonRenderModel(
                    RenderSafeMarkdownHtml(ar.Assertion),
                    RenderSafeMarkdownHtml(ar.Reason),
                    ar.Answer,
                    ar.Scheme,
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
                SpotError: new SpotErrorRenderModel(segs, se.RequireFix, se.AllowNoError, se.NoError, expHtml, EncodeAssetPath(se.ImagePath)));
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
            var rowsRaw = mode == "rearrange" ? new List<List<string>>(rowsList.Count) : null;
            int blankCounter = 0;

            for (int r = 0; r < rowsList.Count; r++)
            {
                var srcRow = rowsList[r] ?? [];
                var htmlRow = new List<string>(srcRow.Count);
                var rawRow = rowsRaw is not null ? new List<string>(srcRow.Count) : null;
                for (int c = 0; c < srcRow.Count; c++)
                {
                    var cellText = srcRow[c] ?? string.Empty;
                    rawRow?.Add(cellText);
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
                rowsRaw?.Add(rawRow!);
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
            var effectiveAlignments = (alignmentsList.Count == 0 || alignmentsList.All(a => string.Equals(a, "left", StringComparison.OrdinalIgnoreCase)))
                ? null
                : alignmentsList.ToList();
            return new SlideRenderModel(question.Id, "diff", mergedLayout, null, null, null, null,
                Differentiation: new DifferentiationRenderModel(
                    mode,
                    dp.Title,
                    RenderOptionalTitleHtml(dp.Title),
                    dp.Bank,
                    dp.Fuzzy,
                    dp.CaseSensitive,
                    dp.AllowNoError,
                    headersHtml,
                    effectiveAlignments,
                    rowsHtml,
                    rowsRaw,
                    specialModels,
                    expHtml,
                    dp.Placeholder));
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
                    RenderOptionalTitleHtml(mp.Title),
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

        if (question.Type == QuestionType.Classify && Phase1PayloadCodec.TryDeserialize<ClassifyPayload>(question.PayloadJson, out var cl) && cl is not null)
        {
            var buckets = (cl.Buckets ?? [])
                .Where(b => b is not null)
                .Select(b => new ClassifyBucketRenderModel(b.BucketId, b.Name, RenderInlineSafeMarkdownHtml(b.Name)))
                .ToList();
            var items = (cl.Items ?? [])
                .Where(it => it is not null)
                .Select(it => new ClassifyItemRenderModel(it.ItemId, it.BucketId, RenderInlineSafeMarkdownHtml(it.Text)))
                .ToList();
            var expHtml = !string.IsNullOrWhiteSpace(cl.Explanation) ? RenderSafeMarkdownHtml(cl.Explanation) : null;
            return new SlideRenderModel(question.Id, "classify", mergedLayout, null, null, null, null,
                Classify: new ClassifyRenderModel(
                    cl.Title,
                    RenderOptionalTitleHtml(cl.Title),
                    buckets,
                    items,
                    expHtml),
                RefKey: question.RefKey);
        }

        if (question.Type == QuestionType.Match && Phase1PayloadCodec.TryDeserialize<MatchPayload>(question.PayloadJson, out var mt) && mt is not null)
        {
            var pairs = (mt.Pairs ?? [])
                .Where(p => p is not null)
                .Select(p => new MatchPairRenderModel(p.PairId, RenderInlineSafeMarkdownHtml(p.Left), RenderInlineSafeMarkdownHtml(p.Right)))
                .ToList();
            var pool = pairs.Select((p, idx) => new MatchBankOptionRenderModel(idx, p.RightHtml, p.PairId)).ToList();
            int nextOptId = pool.Count;
            foreach (var dec in (mt.Distractors ?? []).Where(d => !string.IsNullOrWhiteSpace(d)))
                pool.Add(new MatchBankOptionRenderModel(nextOptId++, RenderInlineSafeMarkdownHtml(dec), null));
            var expHtml = !string.IsNullOrWhiteSpace(mt.Explanation) ? RenderSafeMarkdownHtml(mt.Explanation) : null;
            return new SlideRenderModel(question.Id, "match", mergedLayout, null, null, null, null,
                Match: new MatchRenderModel(
                    mt.Title,
                    RenderOptionalTitleHtml(mt.Title),
                    pairs,
                    pool,
                    expHtml),
                RefKey: question.RefKey);
        }

        if (question.Type == QuestionType.Numeric && Phase1PayloadCodec.TryDeserialize<NumericPayload>(question.PayloadJson, out var nm) && nm is not null)
        {
            var expHtml = !string.IsNullOrWhiteSpace(nm.Explanation) ? RenderSafeMarkdownHtml(nm.Explanation) : null;
            return new SlideRenderModel(question.Id, "numeric", mergedLayout, null, null, null, null,
                Numeric: new NumericRenderModel(
                    RenderSafeMarkdownHtml(nm.Prompt),
                    nm.Answer,
                    nm.Tolerance,
                    Phase3PayloadRules.NormalizeToleranceMode(nm.ToleranceMode),
                    nm.Unit,
                    nm.Placeholder,
                    EncodeAssetPath(nm.ImagePath),
                    expHtml),
                RefKey: question.RefKey);
        }

        if (question.Type == QuestionType.CaseGroup && Phase1PayloadCodec.TryDeserialize<CaseGroupPayload>(question.PayloadJson, out var cs) && cs is not null)
        {
            var parentExplicitOverrides = MergeSparseOverrides("mcq", componentOverrides, question.LayoutOverrides);
            var subSlides = new List<SlideRenderModel>();
            foreach (var sub in cs.Questions ?? [])
            {
                if (sub is null || !Quiz.TryParseQuestionType(sub.Type, out var subType) || subType == QuestionType.CaseGroup)
                    continue;
                var subTargetType = subType == QuestionType.Flashcard ? "flashcard" : "mcq";
                var filteredOverrides = FilterOverridesForTarget(subTargetType, parentExplicitOverrides);
                var subId = $"{question.Id}_sub{sub.Index}";
                var childDomain = sub.RehydrateToDomainQuestion(subType, subId, question.ContainerId);
                subSlides.Add(BuildQuestionSlide(childDomain, layouts, filteredOverrides));
            }

            var expHtml = !string.IsNullOrWhiteSpace(cs.Explanation) ? RenderSafeMarkdownHtml(cs.Explanation) : null;
            return new SlideRenderModel(question.Id, "case", mergedLayout, null, null, null, null,
                CaseGroup: new CaseGroupRenderModel(
                    cs.Title,
                    RenderOptionalTitleHtml(cs.Title),
                    RenderSafeMarkdownHtml(cs.Stem),
                    EncodeAssetPath(cs.ImagePath),
                    !string.IsNullOrWhiteSpace(cs.Caption) ? RenderSafeMarkdownHtml(cs.Caption) : null,
                    subSlides,
                    expHtml),
                RefKey: question.RefKey);
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

    private static FlashcardSideRenderModel BuildSideRenderModel(FlashcardSide side, IReadOnlyDictionary<string, string> cardLayout)
    {
        var defaultPos = cardLayout.TryGetValue("defaultTextPosition", out var dtp) && string.Equals(dtp, "aboveImage", StringComparison.OrdinalIgnoreCase)
            ? "aboveImage" : "belowImage";
        var pos = side.IsPositionExplicit
            ? (side.Position == MediaPosition.TextAboveImage ? "aboveImage" : "belowImage")
            : (side.Position == MediaPosition.TextAboveImage ? "aboveImage" : defaultPos);
        return new(!string.IsNullOrWhiteSpace(side.Text) ? RenderSafeMarkdownHtml(side.Text) : null, EncodeAssetPath(side.ImagePath), pos);
    }

    private static Dictionary<string, Dictionary<string, string>> BuildProfilesMap(IReadOnlyDictionary<string, LayoutProfile> layouts)
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in LayoutBoundsCatalog.SupportedTargets)
        {
            var profile = layouts.TryGetValue(target, out var p) ? p : LayoutProfile.CreateDefault(target).Value;
            map[target] = LayoutBoundsCatalog.StripGlobalOnlyProperties(profile.Values);
        }
        return map;
    }

    private static Dictionary<string, string> MergeSparseOverrides(
        string targetType,
        IReadOnlyDictionary<string, string>? componentOverrides,
        IReadOnlyDictionary<string, string>? questionOverrides)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { componentOverrides, questionOverrides })
        {
            if (source is null) continue;
            foreach (var kv in source)
            {
                if (LayoutBoundsCatalog.IsValidPropertyForTarget(targetType, kv.Key) && !LayoutBoundsCatalog.IsGlobalOnlyProperty(kv.Key))
                    result[LayoutBoundsCatalog.GetCanonicalPropertyName(targetType, kv.Key)] = kv.Value;
            }
        }
        return result;
    }

    private static Dictionary<string, string> FilterOverridesForTarget(
        string targetType,
        IReadOnlyDictionary<string, string> overrides)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in overrides)
        {
            if (LayoutBoundsCatalog.IsValidPropertyForTarget(targetType, kv.Key) && !LayoutBoundsCatalog.IsGlobalOnlyProperty(kv.Key))
                result[LayoutBoundsCatalog.GetCanonicalPropertyName(targetType, kv.Key)] = kv.Value;
        }
        return result;
    }

    private static Dictionary<string, string>? MergeProfile(
        IReadOnlyDictionary<string, LayoutProfile> layouts, string targetType,
        IReadOnlyDictionary<string, string>? componentOverrides, IReadOnlyDictionary<string, string>? questionOverrides) =>
        PruneLayoutForWire(targetType, layouts, MergeProfileRaw(layouts, targetType, componentOverrides, questionOverrides));

    private static Dictionary<string, string> MergeProfileRaw(
        IReadOnlyDictionary<string, LayoutProfile> layouts, string targetType,
        IReadOnlyDictionary<string, string>? componentOverrides, IReadOnlyDictionary<string, string>? questionOverrides)
    {
        var baseProfile = layouts.TryGetValue(targetType, out var p) ? p : LayoutProfile.CreateDefault(targetType).Value;
        var result = new Dictionary<string, string>(baseProfile.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { componentOverrides, questionOverrides })
        {
            if (source is null) continue;
            foreach (var kv in source)
            {
                if (LayoutBoundsCatalog.IsValidPropertyForTarget(targetType, kv.Key) && !LayoutBoundsCatalog.IsGlobalOnlyProperty(kv.Key))
                    result[LayoutBoundsCatalog.GetCanonicalPropertyName(targetType, kv.Key)] = kv.Value;
            }
        }
        return result;
    }

    private static Dictionary<string, string>? PruneLayoutForWire(
        string targetType,
        IReadOnlyDictionary<string, LayoutProfile> layouts,
        Dictionary<string, string> merged)
    {
        var baseProfile = layouts.TryGetValue(targetType, out var p) ? p : LayoutProfile.CreateDefault(targetType).Value;
        var pruned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in merged)
        {
            if (LayoutBoundsCatalog.IsGlobalOnlyProperty(key) ||
                string.Equals(key, "defaultTextPosition", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(val, baseProfile.GetValue(key), StringComparison.OrdinalIgnoreCase))
                continue;
            pruned[key] = val;
        }
        return pruned.Count == 0 ? null : pruned;
    }

    private async Task<Result<string>> WriteWebAppBundleAsync(
        string id, string title, string? description, string storageDirectory, List<SlideRenderModel> slides,
        IReadOnlyDictionary<string, LayoutProfile> layouts, IReadOnlyList<string> referencedAssets, string? outputDirectory,
        Dictionary<string, LinkCardRenderModel>? linkCards,
        Dictionary<string, SessionPreviewSidecarModel>? externalPreviews,
        bool isSession,
        bool includeAllThemes,
        CancellationToken cancellationToken)
    {
        var outDir = !string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetFullPath(outputDirectory)
            : Path.Combine(Packaging.MbypkgExporter.ResolveDefaultOutputBaseDirectory(storageDirectory), $"{id}_web");
        transactionManager?.NotifyOutputDirectoryMutating(outDir);
        Directory.CreateDirectory(outDir);

        var dstAssets = Path.Combine(outDir, "assets");
        var copiedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relAsset in referencedAssets.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (PathConfinement.TryResolveInside(storageDirectory, relAsset, out var src) && File.Exists(src))
            {
                Directory.CreateDirectory(dstAssets);
                var fileName = Path.GetFileName(src);
                File.Copy(src, Path.Combine(dstAssets, fileName), overwrite: true);
                copiedAssets.Add(fileName);
            }
        }
        if (Directory.Exists(dstAssets))
        {
            foreach (var existingFile in Directory.GetFiles(dstAssets))
            {
                if (!copiedAssets.Contains(Path.GetFileName(existingFile)))
                    File.Delete(existingFile);
            }
            if (copiedAssets.Count == 0 && Directory.GetFileSystemEntries(dstAssets).Length == 0)
                Directory.Delete(dstAssets);
        }

        var globalTheme = ResolveDeckGlobalValue(layouts, "themeMode", "berry");
        var slideMs = ResolveDeckGlobalValue(layouts, "slideTransitionMs", "380");
        var profiles = BuildProfilesMap(layouts);
        var toolMeta = metadataProvider.GetMetadata();
        var generatorInfo = new WebGeneratorInfoModel(toolMeta.ProductName, toolMeta.ExecutableName, toolMeta.Version, toolMeta.Runtime, DateTimeOffset.UtcNow.ToString("O"), toolMeta.AppName, toolMeta.RepositoryUrl);
        var payloadJson = JsonSerializer.Serialize(new WebBundlePayload(id, title, description, globalTheme, slideMs, profiles, generatorInfo, slides, linkCards, externalPreviews), JsonOptions);

        var features = WebAppFeatureSet.Analyze(slides, linkCards, externalPreviews, isSession);
        var neededModules = WebComponentCatalog.ResolveNeeded(features);

        var katexDir = Path.Combine(outDir, "katex");
        if (features.HasMath)
            await CopyEmbeddedKatexAssetsAsync(outDir, features.KatexFontFamilies, cancellationToken);
        else if (Directory.Exists(katexDir))
            Directory.Delete(katexDir, recursive: true);

        var componentsDir = Path.Combine(outDir, "components");
        if (Directory.Exists(componentsDir))
            Directory.Delete(componentsDir, recursive: true);

        foreach (var mod in neededModules)
        {
            var subDir = Path.Combine(componentsDir, mod.OutputSubDir);
            if (!string.IsNullOrWhiteSpace(mod.Css))
            {
                Directory.CreateDirectory(subDir);
                await File.WriteAllTextAsync(Path.Combine(subDir, $"{mod.Name}.css"), MinifyCss(mod.Css) + "\n", cancellationToken);
            }
            if (!string.IsNullOrWhiteSpace(mod.Js))
            {
                Directory.CreateDirectory(subDir);
                await File.WriteAllTextAsync(Path.Combine(subDir, $"{mod.Name}.js"), MinifyJs(mod.Js) + "\n", cancellationToken);
            }
        }

        var indexPath = Path.Combine(outDir, "index.html");
        await File.WriteAllTextAsync(indexPath, BuildHtml(title, description, globalTheme, toolMeta, features, neededModules), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outDir, "styles.css"), MinifyCss(BuildCss(globalTheme, includeAllThemes)) + "\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outDir, "app.js"), MinifyJs(BuildJs(payloadJson, features, includeAllThemes)) + "\n", cancellationToken);

        var previewJsonPath = Path.Combine(outDir, "preview-index.json");
        var previewJsPath = Path.Combine(outDir, "preview-index.js");
        if (isSession)
        {
            var sidecarJson = JsonSerializer.Serialize(BuildSessionPreviewSidecar(id, title, description, slides), JsonOptions);
            await File.WriteAllTextAsync(previewJsonPath, sidecarJson, cancellationToken);
            await File.WriteAllTextAsync(previewJsPath, $"window.__mbyRegisterSessionPreview&&window.__mbyRegisterSessionPreview({sidecarJson});", cancellationToken);
        }
        else
        {
            if (File.Exists(previewJsonPath)) File.Delete(previewJsonPath);
            if (File.Exists(previewJsPath)) File.Delete(previewJsPath);
        }
        return indexPath;
    }

    private static SessionPreviewSidecarModel BuildSessionPreviewSidecar(string sessionId, string title, string? description, List<SlideRenderModel> slides)
    {
        var articlePreviews = new List<ArticlePreviewSidecarModel>();
        int artIndex = 0;
        foreach (var s in slides)
        {
            if (s.Kind != "article") continue;
            artIndex++;
            var parts = new List<string>();
            foreach (var b in s.ArticleBlocks ?? [])
            {
                if (!string.IsNullOrWhiteSpace(b.Title)) parts.Add(b.Title);
                if (!string.IsNullOrWhiteSpace(b.TextHtml)) parts.Add(StripHtmlToPlainText(b.TextHtml));
                if (b.Table is not null)
                {
                    if (!string.IsNullOrWhiteSpace(b.Table.Title)) parts.Add(b.Table.Title);
                    parts.AddRange(b.Table.HeadersHtml.Select(StripHtmlToPlainText));
                    foreach (var row in b.Table.RowsHtml)
                        parts.AddRange(row.Select(StripHtmlToPlainText));
                }
                if (b.DiagramExplore is not null)
                {
                    if (!string.IsNullOrWhiteSpace(b.DiagramExplore.Title)) parts.Add(b.DiagramExplore.Title);
                    foreach (var hs in b.DiagramExplore.Hotspots)
                        parts.Add($"{hs.Label}: {StripHtmlToPlainText(hs.NoteHtml)}");
                }
                if (b.Reveal is not null)
                {
                    if (!string.IsNullOrWhiteSpace(b.Reveal.Title)) parts.Add(b.Reveal.Title);
                    foreach (var st in b.Reveal.Steps)
                        parts.Add($"{StripHtmlToPlainText(st.HeadingHtml)} {StripHtmlToPlainText(st.BodyHtml)}");
                }
            }
            var excerpt = TruncateExcerpt(string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))), 650);
            articlePreviews.Add(new ArticlePreviewSidecarModel(s.Id, s.RefKey, s.ComponentIndex ?? artIndex, s.Title ?? $"Article {artIndex}", excerpt));
        }

        var sessionExcerpt = TruncateExcerpt(
            !string.IsNullOrWhiteSpace(description)
                ? description
                : string.Join(" • ", articlePreviews.Select(a => string.IsNullOrWhiteSpace(a.Excerpt) ? a.Title : $"{a.Title}: {a.Excerpt}")),
            650);

        return new SessionPreviewSidecarModel(sessionId, title, sessionExcerpt, articlePreviews);
    }

    private static string StripHtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var decoded = WebUtility.HtmlDecode(HtmlTagStripRegex().Replace(html, " "));
        return WhitespaceCollapseRegex().Replace(decoded, " ").Trim();
    }

    private static string TruncateExcerpt(string text, int maxLen)
    {
        var clean = WhitespaceCollapseRegex().Replace(text ?? string.Empty, " ").Trim();
        if (clean.Length <= maxLen) return clean;
        return clean[..Math.Max(0, maxLen - 1)].TrimEnd() + "…";
    }

    private static bool ShouldCopyKatexFont(string fontFileName, IReadOnlySet<string>? neededFontFamilies)
    {
        if (fontFileName.StartsWith("KaTeX_Main-", StringComparison.OrdinalIgnoreCase) ||
            fontFileName.StartsWith("KaTeX_Math-", StringComparison.OrdinalIgnoreCase) ||
            fontFileName.StartsWith("KaTeX_Size", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (neededFontFamilies is null || neededFontFamilies.Count == 0)
            return false;

        foreach (var fam in neededFontFamilies)
        {
            if (fontFileName.StartsWith($"KaTeX_{fam}-", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task CopyEmbeddedKatexAssetsAsync(string outDir, IReadOnlySet<string>? neededFontFamilies, CancellationToken ct)
    {
        const string rootPrefix = "Meloberry.Infrastructure.Web.Assets.katex.";
        const string fontPrefix = rootPrefix + "fonts.";
        var katexDir = Path.Combine(outDir, "katex");
        var fontsDir = Path.Combine(katexDir, "fonts");
        Directory.CreateDirectory(fontsDir);

        var copiedFontFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var asm = typeof(SlidingWebAppGenerator).Assembly;
        foreach (var resName in asm.GetManifestResourceNames())
        {
            if (!resName.StartsWith(rootPrefix, StringComparison.Ordinal))
                continue;

            string destPath;
            if (resName.StartsWith(fontPrefix, StringComparison.Ordinal))
            {
                var fontFile = resName[fontPrefix.Length..];
                if (!ShouldCopyKatexFont(fontFile, neededFontFamilies))
                    continue;
                copiedFontFiles.Add(fontFile);
                destPath = Path.Combine(fontsDir, fontFile);
            }
            else
            {
                destPath = Path.Combine(katexDir, resName[rootPrefix.Length..]);
            }

            await using var src = asm.GetManifestResourceStream(resName)!;
            await using var dst = File.Create(destPath);
            await src.CopyToAsync(dst, ct);
        }

        if (Directory.Exists(fontsDir))
        {
            foreach (var existingFont in Directory.GetFiles(fontsDir))
            {
                if (!copiedFontFiles.Contains(Path.GetFileName(existingFont)))
                    File.Delete(existingFont);
            }
        }
    }

    private static string BuildHtml(
        string title,
        string? description,
        string themeMode,
        CliToolMetadata toolMeta,
        WebAppFeatureSet features,
        IReadOnlyList<WebComponentModule> neededModules)
    {
        var headLinks = new System.Text.StringBuilder();
        if (features.HasMath)
            headLinks.Append("  <link rel=\"stylesheet\" href=\"katex/katex.min.css\" />\n");
        headLinks.Append("  <link rel=\"stylesheet\" href=\"styles.css\" />\n");
        foreach (var mod in neededModules)
        {
            if (!string.IsNullOrWhiteSpace(mod.Css))
                headLinks.Append($"  <link rel=\"stylesheet\" href=\"components/{mod.OutputSubDir}/{mod.Name}.css\" />\n");
        }

        var zoomModalHtml = features.HasImages
            ? """
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

"""
            : string.Empty;

        var linkPopoverHtml = features.HasNativeLinks
            ? "  <div id=\"linkPreviewPopover\" class=\"mby-link-popover hidden\" role=\"dialog\" aria-live=\"polite\"></div>\n"
            : string.Empty;

        var bodyScripts = new System.Text.StringBuilder();
        if (features.HasMath)
            bodyScripts.Append("  <script src=\"katex/katex.min.js\"></script>\n");
        foreach (var mod in neededModules)
        {
            if (!string.IsNullOrWhiteSpace(mod.Js))
                bodyScripts.Append($"  <script src=\"components/{mod.OutputSubDir}/{mod.Name}.js\"></script>\n");
        }
        bodyScripts.Append("  <script src=\"app.js\"></script>");

        return $$"""
<!DOCTYPE html>
<html lang="en" data-theme="{{WebUtility.HtmlEncode(themeMode)}}">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <meta name="generator" content="{{WebUtility.HtmlEncode($"{toolMeta.ProductName} ({toolMeta.ExecutableName}) {toolMeta.Version}")}}" />
  <title>{{WebUtility.HtmlEncode(title)}} — Meloberry</title>
  <link rel="icon" href="data:," />
{{headLinks.ToString().TrimEnd()}}
</head>
<body>
  <svg class="hidden" aria-hidden="true">
    <defs>
      <symbol id="mby-ico-zoom" viewBox="0 0 24 24"><circle cx="11" cy="11" r="7"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></symbol>
      <symbol id="mby-ico-eye" viewBox="0 0 24 24"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></symbol>
      <symbol id="mby-ico-eye-off" viewBox="0 0 24 24"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"/><line x1="1" y1="1" x2="23" y2="23"/></symbol>
      <symbol id="mby-ico-save" viewBox="0 0 24 24"><path d="M17 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V7l-4-4zm-5 16c-1.66 0-3-1.34-3-3s1.34-3 3-3 3 1.34 3 3-1.34 3-3 3zm3-10H5V5h10v4z"/></symbol>
      <symbol id="mby-ico-github" viewBox="0 0 24 24"><path d="M12 .5C5.65.5.5 5.65.5 12c0 5.08 3.29 9.39 7.86 10.91.58.11.79-.25.79-.56 0-.28-.01-1.02-.02-2-3.2.7-3.87-1.54-3.87-1.54-.52-1.33-1.28-1.68-1.28-1.68-1.05-.71.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.77 2.7 1.26 3.36.96.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.68 0-1.26.45-2.29 1.19-3.09-.12-.29-.52-1.47.11-3.06 0 0 .97-.31 3.17 1.18a11.1 11.1 0 0 1 5.78 0c2.2-1.49 3.17-1.18 3.17-1.18.63 1.59.23 2.77.11 3.06.74.8 1.19 1.83 1.19 3.09 0 4.41-2.69 5.39-5.25 5.67.41.36.78 1.07.78 2.15 0 1.55-.01 2.8-.01 3.18 0 .31.21.67.8.56A11.51 11.51 0 0 0 23.5 12C23.5 5.65 18.35.5 12 .5z"/></symbol>
    </defs>
  </svg>
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
        <button id="backToSlidesBtn" class="mby-icon-btn hidden" type="button" title="Back to slides" aria-label="Back to slides">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="19" y1="12" x2="5" y2="12"/><polyline points="12 19 5 12 12 5"/></svg>
        </button>
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
{{zoomModalHtml}}  <div id="searchModal" class="mby-modal mby-search-modal hidden" role="dialog" aria-modal="true" aria-label="Search study materials">
    <div class="mby-search-card">
      <div class="mby-search-header">
        <span class="mby-search-icon-wrap" aria-hidden="true"><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg></span>
        <input id="searchInput" class="mby-search-input" type="search" placeholder="Search concepts, figures, tables, callouts, questions..." autocomplete="off" />
        <button id="searchClearBtn" class="mby-search-clear-btn hidden" type="button" aria-label="Clear search">Clear</button>
        <button id="searchCloseBtn" class="mby-icon-btn" type="button" aria-label="Close search"><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg></button>
      </div>
      <div id="searchFilters" class="mby-search-filters" role="tablist" aria-label="Search category filters"></div>
      <div id="searchMeta" class="mby-search-meta"></div>
      <div id="searchResults" class="mby-search-results" role="listbox"></div>
      <div id="searchFooter" class="mby-search-footer">
        <span><kbd>↑</kbd><kbd>↓</kbd> navigate</span>
        <span><kbd>Enter</kbd> jump</span>
        <span><kbd>Tab</kbd> filter</span>
        <span><kbd>Esc</kbd> close</span>
      </div>
    </div>
  </div>
{{linkPopoverHtml}}{{bodyScripts}}
</body>
</html>
""";
    }

    private static string BuildCss(string themeMode, bool includeAllThemes) =>
        WebAppSharedStyles.BuildThemeCss(includeAllThemes ? null : themeMode) + "\n" +
        WebAppSharedStyles.BuildBaseCss() + "\n" + """
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
.mby-icon-btn { width: 34px; height: 34px; border-radius: 9px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font-size: 16px; font-weight: 700; cursor: pointer; display: inline-flex; align-items: center; justify-content: center; line-height: 1; padding: 0; transition: border-color 140ms ease, color 140ms ease, background 140ms ease; }
.mby-icon-btn:hover { border-color: var(--accent); color: var(--accent); background: var(--accent-soft); }
.mby-mode-hint { font-size: 12px; color: var(--muted); font-weight: 600; }
.mby-viewport { position: relative; flex: 1; min-height: 0; overflow: hidden; display: flex; }
.mby-slides-track { display: flex; width: 100%; height: 100%; transition: transform var(--slide-ms) cubic-bezier(0.22, 1, 0.36, 1); }
.mby-slide, .mby-finish-viewport { min-height: 0; overflow-y: auto; display: flex; justify-content: center; align-items: flex-start; }
.mby-slide { min-width: 100%; height: 100%; padding: 24px 20px 32px; }
.mby-finish-viewport { flex: 1; padding: 24px 20px 36px; }
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
.mby-stats-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px; margin: 18px 0; }
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
.mby-search-modal { align-items: flex-start; padding-top: 8vh; z-index: 55; backdrop-filter: blur(4px); -webkit-backdrop-filter: blur(4px); }
.mby-search-card { width: 100%; max-width: 700px; background: var(--surface); border: 1px solid var(--border); border-radius: 18px; box-shadow: 0 26px 68px rgba(0, 0, 0, 0.32); overflow: hidden; display: flex; flex-direction: column; max-height: 78vh; }
.mby-search-header { display: flex; align-items: center; gap: 10px; padding: 12px 14px; border-bottom: 1px solid var(--border); background: var(--bg); }
.mby-search-icon-wrap { display: inline-flex; align-items: center; justify-content: center; color: var(--muted); flex-shrink: 0; margin-left: 2px; }
.mby-search-input { flex: 1; padding: 9px 12px; border-radius: 10px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-size: 15px; outline: none; }
.mby-search-input:focus { border-color: var(--select); box-shadow: 0 0 0 2px var(--select-soft); }
.mby-search-clear-btn { padding: 5px 10px; border-radius: 8px; border: 1px solid var(--border); background: var(--surface); color: var(--muted); font-size: 12px; font-weight: 700; cursor: pointer; }
.mby-search-clear-btn:hover { border-color: var(--accent); color: var(--accent); }
.mby-search-filters { display: flex; flex-wrap: wrap; gap: 6px; padding: 8px 14px; border-bottom: 1px solid var(--border); background: var(--surface); }
.mby-search-filter-pill { display: inline-flex; align-items: center; gap: 5px; padding: 4px 10px; border-radius: 999px; border: 1px solid var(--border); background: var(--bg); color: var(--muted); font-size: 12px; font-weight: 700; cursor: pointer; transition: border-color 120ms ease, background 120ms ease, color 120ms ease; }
.mby-search-filter-pill:hover { border-color: var(--select); color: var(--select); }
.mby-search-filter-pill.active { border-color: var(--accent); background: var(--accent-soft); color: var(--accent); }
.mby-search-meta { padding: 7px 14px; font-size: 12px; font-weight: 600; color: var(--muted); border-bottom: 1px solid var(--border); background: var(--bg); }
.mby-search-results { overflow-y: auto; padding: 8px; display: flex; flex-direction: column; gap: 4px; flex: 1; min-height: 120px; }
.mby-search-group-hdr { padding: 6px 10px 2px; font-size: 11px; font-weight: 800; letter-spacing: 0.05em; text-transform: uppercase; color: var(--muted); }
.mby-search-item { width: 100%; text-align: left; padding: 10px 12px; border-radius: 11px; border: 1px solid transparent; background: transparent; color: var(--text); cursor: pointer; display: flex; flex-direction: column; gap: 4px; transition: background 120ms ease, border-color 120ms ease; }
.mby-search-item:hover, .mby-search-item.active { background: var(--select-soft); border-color: var(--select); }
.mby-search-item-row { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.mby-search-item-meta { display: flex; align-items: center; gap: 6px; min-width: 0; flex-wrap: wrap; }
.mby-search-badge { padding: 2px 7px; border-radius: 6px; font-size: 10px; font-weight: 800; text-transform: uppercase; letter-spacing: 0.04em; background: var(--accent-soft); color: var(--accent); flex-shrink: 0; }
.mby-search-crumb { font-size: 12px; font-weight: 700; color: var(--text); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.mby-search-status { font-size: 11px; font-weight: 700; color: var(--muted); flex-shrink: 0; }
.mby-search-status.ok { color: var(--ok); }
.mby-search-status.bad { color: var(--bad); }
.mby-search-item-snippet { font-size: 13px; line-height: 1.45; color: var(--muted); }
.mby-search-item-snippet mark { background: var(--select-soft); color: var(--select); font-weight: 700; padding: 0 3px; border-radius: 3px; }
.mby-search-footer { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; padding: 8px 14px; border-top: 1px solid var(--border); background: var(--bg); font-size: 11px; color: var(--muted); }
.mby-search-footer kbd { font-family: ui-monospace, Consolas, monospace; font-size: 10px; padding: 1px 5px; border-radius: 4px; border: 1px solid var(--border); background: var(--surface); color: var(--text); margin-right: 3px; }
.mby-search-empty { padding: 28px 16px; text-align: center; color: var(--muted); font-size: 14px; }
.mby-search-pulse { outline: 2px solid var(--select); outline-offset: 4px; border-radius: 10px; transition: outline-color 400ms ease; }
""";

    private static string BuildJs(string payloadJson, WebAppFeatureSet features, bool includeAllThemes)
    {
        var themeBridgeJs = includeAllThemes
            ? """
window.addEventListener('message', e => {
  if (e.origin && e.origin !== 'null') {
    try {
      const u = new URL(e.origin);
      if (u.hostname !== '127.0.0.1' && u.hostname !== 'localhost') return;
    } catch { return; }
  }
  const msg = e.data;
  if (!msg || msg.type !== 'mby-theme-override' || typeof msg.theme !== 'string') return;
  const t = msg.theme.toLowerCase();
  if (t !== 'berry' && t !== 'light' && t !== 'dark' && t !== 'paper') return;
  document.documentElement.dataset.theme = t;
  try { e.source?.postMessage({ type: 'mby-theme-applied', theme: t }, e.origin && e.origin !== 'null' ? e.origin : '*'); } catch {}
});
"""
            : string.Empty;
        var renderMathBlock = features.HasMath
            ? """
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
"""
            : "function renderMathIn() {}\n";

        return $$"""
const DATA = {{payloadJson}};
const MBY = window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} };
const track = document.getElementById('slidesTrack');
const $ = id => document.getElementById(id);
const scrollDownBtn = $('scrollDownBtn'), greetingScreen = $('greetingScreen'), greetingMeta = $('greetingMeta'), greetingGeneratorFooter = $('greetingGeneratorFooter');
{{WebAppSharedStyles.BuildBaseCoreJs()}}
const startSessionBtn = $('startSessionBtn'), playerTopbar = $('playerTopbar'), playerViewport = $('playerViewport'), playerControls = $('playerControls'), finishScreen = $('finishScreen');
const backToSlidesBtn = $('backToSlidesBtn'), topbarModeHint = $('topbarModeHint'), prevBtn = $('prevBtn'), prevCompMenu = $('prevCompMenu'), prevCompMenuItem = $('prevCompMenuItem');
const nextBtn = $('nextBtn'), finishBtn = $('finishBtn'), continueSessionBtn = $('continueSessionBtn'), earlyFinishModal = $('earlyFinishModal'), dotsNav = $('dotsNav');
const searchModal = $('searchModal'), searchInput = $('searchInput'), searchClearBtn = $('searchClearBtn'), searchFiltersEl = $('searchFilters'), searchResultsEl = $('searchResults'), searchMetaEl = $('searchMeta'), topbarSearchBtn = $('topbarSearchBtn'), greetingSearchBtn = $('greetingSearchBtn'), searchCloseBtn = $('searchCloseBtn');
const SAVE_ICON_SVG = '<svg class="mby-save-icon" viewBox="0 0 24 24" aria-hidden="true"><use href="#mby-ico-save"/></svg>';
const GITHUB_ICON_SVG = '<svg class="mby-github-icon" viewBox="0 0 24 24" aria-hidden="true"><use href="#mby-ico-github"/></svg>';

let currentIndex = 0, isFinished = false, sessionStartedAt = new Date(), activeLeafEnterMs = null, lastVisitedLeafId = null, autoSaveSent = false;
const activeQuizQuestionIdx = new Map(), quizControllers = new Map(), mcqStateMap = new Map(), flashcardStateMap = new Map(), gradedStateMap = new Map();
const isGradedKind = k => !!MBY.gradedKinds[k];
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
  const appName = g.appName || 'Meloberry';
  const rawVer = String(g.version || '2.1.1').trim();
  const ver = rawVer.startsWith('v') ? rawVer : ('v' + rawVer);
  const repoUrl = g.repositoryUrl || 'https://github.com/doc-shashank/meloberry-cli/';
  footerEl.innerHTML = '';
  const ghLink = mk('a', 'mby-generator-link');
  ghLink.href = repoUrl;
  ghLink.target = '_blank';
  ghLink.rel = 'noopener noreferrer';
  ghLink.innerHTML = `${GITHUB_ICON_SVG}<span>GitHub</span>`;
  footerEl.appendChild(ghLink);
  footerEl.appendChild(mk('span', 'mby-generator-meta', `Generator: ${appName} ${ver}${includeTimestamp && g.generatedAtUtc ? ` • Built ${g.generatedAtUtc.slice(0, 10)}` : ''}`));
}

const _plainDiv = document.createElement('div');
function htmlToPlainText(html) {
  if (!html) return '';
  _plainDiv.innerHTML = html;
  return (_plainDiv.textContent || _plainDiv.innerText || '').replace(/\s+/g, ' ').trim();
}
{{renderMathBlock}}MBY.helpers.renderMathIn = renderMathIn;

const ctx = {
  DATA,
  getCurrentIndex: () => currentIndex,
  setSessionStarted: () => { sessionStartedAt = new Date(); activeLeafEnterMs = null; lastVisitedLeafId = null; },
  greetingScreen, playerTopbar, playerViewport, playerControls,
  activeQuizQuestionIdx, quizControllers, mcqStateMap, flashcardStateMap, gradedStateMap,
  metricById,
  isGradedKind,
  renderMathIn,
  htmlToPlainText,
  makeZoomImg: (src, alt) => MBY.helpers.makeZoomImg ? MBY.helpers.makeZoomImg(src, alt, ctx) : mk('img', 'mby-media-img'),
  openZoomLightbox: (src, alt) => MBY.helpers.openZoomLightbox?.(src, alt),
  updateScrollDownHint: () => updateScrollDownHint(),
  renderSingleQuestionWidget: (q, onChanged, onFlipStart, onFlipEnd) => renderSingleQuestionWidget(q, onChanged, onFlipStart, onFlipEnd),
  closePrevCompMenu: () => closePrevCompMenu(),
  flushActiveLeafTime,
  enterActiveLeaf,
  updateNavButtonsUi: () => updateNavButtonsUi(),
  scrollSlideToTop: el => scrollSlideToTop(el),
  goToSlide: (idx, targetQIdx, opts) => goToSlide(idx, targetQIdx, opts)
};

function renderSingleQuestionWidget(q, onChanged, onFlipStart, onFlipEnd) {
  const builder = MBY.questions[q.kind];
  return builder ? builder(q, ctx, onChanged, onFlipStart, onFlipEnd) : mk('div', 'mby-card', 'Unsupported question type: ' + q.kind);
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

function getHumanKindLabel(kind) {
  return MBY.kindLabels[kind] || ({ article: 'Article', quiz: 'Quiz' })[kind] || String(kind || 'Item').toUpperCase();
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
  body.dataset.workspaceId = report.workspaceId;
  body.appendChild(mk('div', 'mby-summary-header', null, [
    mk('h2', null, report.title || 'Session Summary'),
    mk('div', 'mby-summary-meta', `Completed in ${(report.totalDurationMs / 1000).toFixed(1)}s`)
  ]));

  const statItems = [];
  if (report.summary.mcqTotal > 0) {
    statItems.push(['Graded Score', `${report.summary.mcqCorrectFinal} / ${report.summary.mcqTotal}`]);
    statItems.push(['Accuracy', `${report.summary.mcqAccuracyPercent.toFixed(1)}%`]);
  }
  if (report.summary.flashcardTotal > 0) {
    statItems.push(['Flashcards Known', `${report.summary.flashcardKnown} / ${report.summary.flashcardTotal}`]);
  }
  const articleTotal = report.slides.filter(s => s.kind === 'article').length;
  if (articleTotal > 0) {
    statItems.push(['Articles Viewed', `${report.summary.articleSlidesViewed} / ${articleTotal}`]);
  }
  if (statItems.length > 0) {
    const grid = mk('div', 'mby-stats-grid', null, statItems.map(([l, v]) => mk('div', 'mby-stat-box', null, [mk('div', 'mby-stat-label', l), mk('div', 'mby-stat-value', v)])));
    body.appendChild(grid);
  }

  if (report.summary.mcqTotal === 0 && report.summary.flashcardTotal === 0) {
    body.appendChild(mk('div', 'mby-review-box ok', `Reading session complete — ${report.summary.articleSlidesViewed} of ${articleTotal} article(s) viewed.`));
  } else if (report.weakItemIds.length === 0) {
    const okMsg = report.summary.flashcardTotal === 0
      ? 'All graded questions look solid — no review flags raised.'
      : (report.summary.mcqTotal === 0 ? 'All flashcards rated known — no review flags raised.' : 'All graded questions & flashcards look solid — no review flags raised.');
    body.appendChild(mk('div', 'mby-review-box ok', okMsg));
  } else {
    const chips = mk('div', 'mby-weak-chips', null, report.weakItemIds.map(id => {
      const s = metricBySlideId.get(id);
      const chip = mk('button', 'mby-weak-chip', s ? `#${s.slideIndex} ${getHumanKindLabel(s.kind)}` : 'Review Item');
      chip.type = 'button';
      chip.dataset.questionId = id;
      chip.addEventListener('click', () => {
        const entry = getSearchIndex().find(e => e.leafId === id);
        if (entry) jumpToSearchEntry(entry);
      });
      return chip;
    }));
    body.appendChild(mk('div', 'mby-review-box warn', null, [mk('div', 'mby-review-title', `Needs Review (${report.weakItemIds.length})`), chips]));
  }

  body.appendChild(mk('strong', null, `Item-by-Item Breakdown (${report.slides.length})`));
  const list = mk('div', 'mby-breakdown-list', null, report.slides.map(s => {
    let statusText = s.visitedCount > 0 ? 'Viewed' : 'Skipped', statusCls = s.visitedCount > 0 ? 'accent' : 'muted', activity = `Visits: ${s.visitedCount}`;
    if (isGradedKind(s.kind)) { statusText = s.isCorrect === true ? 'Correct' : 'Missed'; statusCls = s.isCorrect === true ? 'ok' : 'bad'; activity = `Checks: ${s.checkAttempts || 0} • Visits: ${s.visitedCount}`; }
    else if (s.kind === 'flashcard') { statusText = s.flashcardRating === 'known' ? 'Known' : (s.flashcardRating === 'needsReview' ? 'Review' : 'Unrated'); statusCls = s.flashcardRating === 'known' ? 'ok' : (s.flashcardRating === 'needsReview' ? 'bad' : 'muted'); activity = `Flips: ${s.flipCount || 0} • Visits: ${s.visitedCount}`; }
    const left = mk('div', 'mby-breakdown-left'), right = mk('div', 'mby-breakdown-right');
    left.innerHTML = `<span class="mby-idx-badge">#${s.slideIndex}</span><div><div class="mby-item-kind">${escapeHtml(getHumanKindLabel(s.kind))}</div><div class="mby-item-sub">${escapeHtml(activity)}</div></div>`;
    right.innerHTML = `<span class="mby-status-pill ${statusCls}">${statusText}</span><span class="mby-item-sub">${(s.timeSpentMs / 1000).toFixed(1)}s</span>`;
    const rowEl = mk('div', 'mby-breakdown-item', null, [left, right]);
    rowEl.dataset.componentId = s.id;
    rowEl.dataset.slideKind = s.kind;
    return rowEl;
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
  return slide?.title || getHumanKindLabel(slide?.kind);
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

const hydratedSlides = new Set();
function ensureSlideRendered(idx, eagerImages = false) {
  if (idx < 0 || idx >= DATA.slides.length) return;
  const slideEl = track.children[idx];
  if (!slideEl) return;
  if (!hydratedSlides.has(idx)) {
    hydratedSlides.add(idx);
    const slide = DATA.slides[idx];
    if (slide.kind === 'article') {
      const card = mk('div', 'mby-card', null, [createUpperTab(slide.title ? 'ARTICLE • ' + slide.title : 'ARTICLE')]);
      applySlideLayoutVars(card, resolveSlideLayout(slide, 'article'));
      const stack = mk('div', 'mby-article-blocks', null, (slide.articleBlocks || []).map(b => {
        if (b.kind === 'text' && b.textHtml) {
          const d = mk('div');
          d.dataset.blockId = b.id;
          d.innerHTML = b.textHtml;
          renderMathIn(d);
          return d;
        }
        if (b.kind === 'image' && b.imagePath) {
          const plainCap = htmlToPlainText(b.captionHtml);
          const figChildren = [ctx.makeZoomImg(b.imagePath, b.alt || plainCap)];
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
        const blockBuilder = MBY.blocks[b.kind];
        return blockBuilder ? blockBuilder(b, ctx) : null;
      }));
      card.appendChild(mk('div', 'mby-card-body', null, [stack]));
      slideEl.appendChild(card);
    } else if (slide.kind === 'quiz') {
      slideEl.appendChild(MBY.helpers.buildBundledQuiz(slide, ctx));
    } else {
      slideEl.appendChild(renderSingleQuestionWidget(slide, null));
    }
  }
  if (eagerImages) {
    slideEl.querySelectorAll('img').forEach(img => { img.loading = 'eager'; });
  }
}

function goToSlide(idx, targetQIdx = null, opts = {}) {
  if (DATA.slides.length === 0) return;
  const shouldScrollToTop = opts?.scrollToTop !== false;
  closePrevCompMenu(); flushActiveLeafTime();
  currentIndex = Math.max(0, Math.min(idx, DATA.slides.length - 1));
  ensureSlideRendered(currentIndex, true);
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
  if (currentIndex + 1 < DATA.slides.length) {
    const idle = window.requestIdleCallback || (fn => setTimeout(fn, 120));
    idle(() => ensureSlideRendered(currentIndex + 1, false));
  }
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
  const articleCount = DATA.slides.filter(s => s.kind === 'article').length;
  const questionCount = leafSlides.filter(s => s.kind !== 'article').length;
  const greetingParts = [];
  if (articleCount > 0) greetingParts.push(`${articleCount} Article${articleCount === 1 ? '' : 's'}`);
  if (questionCount > 0) greetingParts.push(`${questionCount} Question${questionCount === 1 ? '' : 's'}`);
  greetingMeta.textContent = greetingParts.join('  •  ');
  populateGeneratorFooter(greetingGeneratorFooter, false);

  DATA.slides.forEach((slide, idx) => {
    const slideEl = mk('section', 'mby-slide');
    slideEl.dataset.slideIdx = String(idx);
    slideEl.addEventListener('scroll', updateScrollDownHint, { passive: true });
    track.appendChild(slideEl);
    const dot = mk('button', 'mby-dot'); dot.type = 'button'; dot.title = getComponentDisplayName(slide); dot.addEventListener('click', () => goToSlide(idx)); dotsNav.appendChild(dot);
  });

  track.addEventListener('transitionend', e => { if (e.target === track && e.propertyName === 'transform') requestAnimationFrame(updateScrollDownHint); });
  window.addEventListener('resize', () => requestAnimationFrame(updateScrollDownHint));
  if (scrollDownBtn) scrollDownBtn.addEventListener('click', () => track.children[currentIndex]?.scrollBy({ top: 180, behavior: 'smooth' }));
  MBY.helpers.initZoom?.(ctx);
  MBY.helpers.initLinks?.(ctx);
  MBY.helpers.applyInitialArticleHashRoute?.(ctx);
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
let _searchIndexCache = null;
function getSearchIndex() {
  if (_searchIndexCache) return _searchIndexCache;
  const entries = [];
  const collectQuestionSearchText = (q, parts) => {
    MBY.search[q.kind]?.(q, parts, ctx);
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
        MBY.blockSearch[b.kind]?.(b, parts, ctx);
        const rawText = parts.join(' ').replace(/\s+/g, ' ').trim();
        if (rawText) {
          const badge = b.kind === 'table' ? 'TABLE' : (b.kind === 'callout' ? 'CALLOUT' : (b.kind === 'diagram-explore' ? 'DIAGRAM' : (b.kind === 'reveal' ? 'REVEAL' : (b.kind === 'image' ? 'FIGURE' : 'ARTICLE'))));
          entries.push({
            slideIdx, qIdx: null, blockId: b.id, leafId: slide.id, kind: 'article', badge,
            componentTitle: slideName, title: b.title || b.table?.title || b.diagramExplore?.title || b.reveal?.title || slideName,
            refKey: slide.refKey || '', titleLower: (slideName + ' ' + (b.title || '')).toLowerCase(),
            rawText, lower: rawText.toLowerCase()
          });
        }
      });
    } else if (slide.kind === 'quiz' && Array.isArray(slide.questions)) {
      slide.questions.forEach((q, qIdx) => {
        const parts = [slideName];
        collectQuestionSearchText(q, parts);
        const rawText = parts.join(' ').replace(/\s+/g, ' ').trim();
        if (rawText) {
          entries.push({
            slideIdx, qIdx, blockId: null, leafId: q.id, kind: 'question', badge: `Q${qIdx + 1} • ${getHumanKindLabel(q.kind).toUpperCase()}`,
            componentTitle: slideName, title: `${slideName} • Q${qIdx + 1}`,
            refKey: q.refKey || '', titleLower: (slideName + ' q' + (qIdx + 1) + ' ' + getHumanKindLabel(q.kind)).toLowerCase(),
            rawText, lower: rawText.toLowerCase()
          });
        }
      });
    } else if (MBY.search[slide.kind]) {
      const parts = [slideName];
      collectQuestionSearchText(slide, parts);
      const rawText = parts.join(' ').replace(/\s+/g, ' ').trim();
      if (rawText) {
        entries.push({
          slideIdx, qIdx: null, blockId: null, leafId: slide.id, kind: 'question', badge: getHumanKindLabel(slide.kind).toUpperCase(),
          componentTitle: slideName, title: slideName,
          refKey: slide.refKey || '', titleLower: (slideName + ' ' + getHumanKindLabel(slide.kind)).toLowerCase(),
          rawText, lower: rawText.toLowerCase()
        });
      }
    }
  });
  _searchIndexCache = entries;
  return entries;
}

let activeSearchIdx = 0;
let activeSearchFilter = 'all';
let currentSearchMatches = [];
const SEARCH_FILTERS = [
  { id: 'all', label: 'All' },
  { id: 'article', label: 'Articles' },
  { id: 'question', label: 'Questions' },
  { id: 'unanswered', label: 'Unanswered' },
  { id: 'weak', label: 'Needs Review' }
];

function getEntryStatus(item) {
  const m = metricById.get(item.leafId);
  if (!m) return { key: 'unanswered', text: '', cls: '' };
  if (item.kind === 'article') {
    return m.visitedCount > 0 ? { key: 'viewed', text: 'Viewed', cls: 'ok' } : { key: 'unanswered', text: 'Unread', cls: '' };
  }
  if (m.kind === 'flashcard') {
    if (m.flashcardRating === 'known') return { key: 'correct', text: 'Known', cls: 'ok' };
    if (m.flashcardRating === 'needsReview') return { key: 'weak', text: 'Needs Review', cls: 'bad' };
    return { key: 'unanswered', text: 'Unrated', cls: '' };
  }
  if (m.isCorrect === true) return { key: 'correct', text: 'Correct', cls: 'ok' };
  if (m.isCorrect === false) return { key: 'weak', text: 'Needs Review', cls: 'bad' };
  return { key: 'unanswered', text: 'Unanswered', cls: '' };
}

function matchesSearchFilter(item, filterId) {
  if (filterId === 'all') return true;
  if (filterId === 'article') return item.kind === 'article';
  if (filterId === 'question') return item.kind === 'question';
  const st = getEntryStatus(item);
  if (filterId === 'unanswered') return st.key === 'unanswered';
  if (filterId === 'weak') return st.key === 'weak';
  return true;
}

function renderSearchFilters(tokens) {
  if (!searchFiltersEl) return;
  const idx = getSearchIndex();
  const textMatched = tokens.length === 0
    ? idx
    : idx.filter(item => tokens.every(t => item.lower.includes(t)));
  searchFiltersEl.innerHTML = '';
  SEARCH_FILTERS.forEach(f => {
    const count = textMatched.filter(it => matchesSearchFilter(it, f.id)).length;
    const pill = mk('button', 'mby-search-filter-pill' + (activeSearchFilter === f.id ? ' active' : ''));
    pill.type = 'button';
    pill.setAttribute('role', 'tab');
    pill.setAttribute('aria-selected', activeSearchFilter === f.id ? 'true' : 'false');
    pill.textContent = `${f.label} (${count})`;
    pill.addEventListener('click', () => {
      activeSearchFilter = f.id;
      activeSearchIdx = 0;
      renderSearchResults();
      searchInput?.focus();
    });
    searchFiltersEl.appendChild(pill);
  });
}

function scoreSearchEntry(item, tokens, fullQuery) {
  let score = 0;
  if (fullQuery && item.titleLower.includes(fullQuery)) score += 45;
  if (fullQuery && item.lower.includes(fullQuery)) score += 25;
  tokens.forEach(t => {
    if (item.titleLower.includes(t)) score += 20;
    if (item.refKey && item.refKey.toLowerCase().includes(t)) score += 15;
    if (item.lower.includes(t)) score += 8;
  });
  return score;
}

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

function buildSearchItemButton(item, idx, tokens) {
  const btn = mk('button', 'mby-search-item' + (idx === activeSearchIdx ? ' active' : ''));
  btn.type = 'button';
  btn.setAttribute('role', 'option');
  const row = mk('div', 'mby-search-item-row');
  const meta = mk('div', 'mby-search-item-meta');
  meta.appendChild(mk('span', 'mby-search-badge', item.badge));
  meta.appendChild(mk('span', 'mby-search-crumb', item.title));
  row.appendChild(meta);
  const st = getEntryStatus(item);
  if (st.text) row.appendChild(mk('span', 'mby-search-status' + (st.cls ? ' ' + st.cls : ''), st.text));
  btn.appendChild(row);
  const snip = mk('div', 'mby-search-item-snippet');
  snip.innerHTML = highlightMatchSnippet(item.rawText, tokens);
  btn.appendChild(snip);
  btn.addEventListener('click', () => jumpToSearchEntry(item));
  return btn;
}

function renderSessionOutline(items) {
  let lastComp = null;
  items.forEach((item, idx) => {
    if (item.componentTitle !== lastComp) {
      lastComp = item.componentTitle;
      searchResultsEl.appendChild(mk('div', 'mby-search-group-hdr', item.componentTitle));
    }
    searchResultsEl.appendChild(buildSearchItemButton(item, idx, []));
  });
}

function renderSearchResults() {
  const rawQ = (searchInput?.value || '').trim();
  if (searchClearBtn) searchClearBtn.classList.toggle('hidden', rawQ.length === 0);
  const q = rawQ.toLowerCase();
  const tokens = q.split(/\s+/).filter(Boolean);
  renderSearchFilters(tokens);

  const filtered = getSearchIndex().filter(item =>
    matchesSearchFilter(item, activeSearchFilter) &&
    (tokens.length === 0 || tokens.every(t => item.lower.includes(t))));

  currentSearchMatches = tokens.length === 0
    ? filtered.slice(0, 40)
    : filtered
        .map(item => ({ item, score: scoreSearchEntry(item, tokens, q) }))
        .sort((a, b) => b.score - a.score || a.item.slideIdx - b.item.slideIdx)
        .map(x => x.item)
        .slice(0, 40);

  if (activeSearchIdx >= currentSearchMatches.length) activeSearchIdx = Math.max(0, currentSearchMatches.length - 1);
  if (searchMetaEl) {
    searchMetaEl.textContent = tokens.length === 0
      ? `Session outline • ${currentSearchMatches.length} of ${filtered.length} item(s)`
      : `${filtered.length} match(es) for "${rawQ}"`;
  }
  searchResultsEl.innerHTML = '';
  if (currentSearchMatches.length === 0) {
    searchResultsEl.appendChild(mk('div', 'mby-search-empty', 'No matching study blocks or questions found for this filter.'));
    return;
  }
  if (tokens.length === 0) {
    renderSessionOutline(currentSearchMatches);
    return;
  }
  currentSearchMatches.forEach((item, idx) => {
    searchResultsEl.appendChild(buildSearchItemButton(item, idx, tokens));
  });
}

function openSearchModal() {
  if (!searchModal) return;
  MBY.helpers.hideLinkPopover?.();
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
searchClearBtn?.addEventListener('click', () => {
  if (searchInput) { searchInput.value = ''; searchInput.focus(); }
  activeSearchIdx = 0;
  renderSearchResults();
});
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
    if (e.key === 'Tab') {
      e.preventDefault();
      const idx = SEARCH_FILTERS.findIndex(f => f.id === activeSearchFilter);
      const next = SEARCH_FILTERS[(idx + (e.shiftKey ? SEARCH_FILTERS.length - 1 : 1)) % SEARCH_FILTERS.length];
      activeSearchFilter = next.id;
      activeSearchIdx = 0;
      renderSearchResults();
      return;
    }
    const optionEls = Array.from(searchResultsEl.querySelectorAll('.mby-search-item'));
    if (e.key === 'ArrowDown' && currentSearchMatches.length > 0) {
      e.preventDefault();
      activeSearchIdx = Math.min(currentSearchMatches.length - 1, activeSearchIdx + 1);
      optionEls.forEach((el, i) => el.classList.toggle('active', i === activeSearchIdx));
      optionEls[activeSearchIdx]?.scrollIntoView({ block: 'nearest' });
      return;
    }
    if (e.key === 'ArrowUp' && currentSearchMatches.length > 0) {
      e.preventDefault();
      activeSearchIdx = Math.max(0, activeSearchIdx - 1);
      optionEls.forEach((el, i) => el.classList.toggle('active', i === activeSearchIdx));
      optionEls[activeSearchIdx]?.scrollIntoView({ block: 'nearest' });
      return;
    }
    if (e.key === 'Enter' && currentSearchMatches[activeSearchIdx]) {
      e.preventDefault();
      jumpToSearchEntry(currentSearchMatches[activeSearchIdx]);
      return;
    }
    return;
  }
  if (e.key === 'Escape' && MBY.helpers.isLinkPopoverOpen?.()) {
    MBY.helpers.hideLinkPopover?.();
    return;
  }
  const tag = (e.target?.tagName || '').toLowerCase();
  const isTyping = tag === 'input' || tag === 'textarea' || e.target?.isContentEditable;
  if (e.key === '/' && !isTyping && !e.ctrlKey && !e.metaKey && !e.altKey) {
    e.preventDefault();
    openSearchModal();
    return;
  }
  if (MBY.helpers.isZoomOpen?.()) { if (e.key === 'Escape') MBY.helpers.closeZoomLightbox?.(); return; }
  if (!earlyFinishModal.classList.contains('hidden')) { if (e.key === 'Escape') earlyFinishModal.classList.add('hidden'); return; }
  if (!prevCompMenu.classList.contains('hidden') && e.key === 'Escape') { closePrevCompMenu(); return; }
  if (!isTyping && greetingScreen.classList.contains('hidden') && !isFinished) { if (e.key === 'ArrowLeft') navigatePrevious(); if (e.key === 'ArrowRight') navigateNext(); }
});
renderSlides();
{{themeBridgeJs}}
""";
    }

    private static string MinifyCss(string css)
    {
        if (string.IsNullOrWhiteSpace(css)) return string.Empty;
        var sb = new System.Text.StringBuilder(css.Length);
        bool inString = false;
        char quote = '\0';
        for (int i = 0; i < css.Length; i++)
        {
            char c = css[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < css.Length) { sb.Append(css[++i]); continue; }
                if (c == quote) inString = false;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                inString = true;
                quote = c;
                sb.Append(c);
                continue;
            }
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < css.Length && !(css[i] == '*' && css[i + 1] == '/')) i++;
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length == 0) continue;
                char prev = sb[^1];
                int j = i + 1;
                while (j < css.Length && char.IsWhiteSpace(css[j])) j++;
                if (j >= css.Length) break;
                char next = css[j];
                if (prev is '{' or '}' or ';' or ':' or ',' || next is '{' or '}' or ';' or ':' or ',')
                {
                    i = j - 1;
                    continue;
                }
                if (prev != ' ') sb.Append(' ');
                i = j - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string MinifyJs(string js)
    {
        if (string.IsNullOrWhiteSpace(js)) return string.Empty;
        var sb = new System.Text.StringBuilder(js.Length);
        int startIdx = 0;
        if (js.StartsWith("const DATA = ", StringComparison.Ordinal))
        {
            int nl = js.IndexOf('\n');
            if (nl > 0)
            {
                sb.Append(js, 0, nl + 1);
                startIdx = nl + 1;
            }
        }

        bool inString = false;
        char quote = '\0';
        bool inRegex = false;
        bool inRegexCharClass = false;
        char lastSig = '\n';
        string lastWord = string.Empty;
        var wordBuf = new System.Text.StringBuilder();

        void FlushWord()
        {
            if (wordBuf.Length > 0)
            {
                lastWord = wordBuf.ToString();
                wordBuf.Clear();
            }
        }

        static bool CanPrecedeRegex(char sig, string word) =>
            sig is '(' or ',' or '=' or ':' or '[' or '!' or '&' or '|' or '?' or '{' or '}' or ';' or '~' or '^' or '+' or '-' or '*' or '%' or '<' or '>' or '\n' ||
            word is "return" or "typeof" or "void" or "delete" or "throw" or "in" or "of" or "case" or "else" or "yield" or "await";

        for (int i = startIdx; i < js.Length; i++)
        {
            char c = js[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < js.Length) { sb.Append(js[++i]); continue; }
                if (c == quote) { inString = false; lastSig = quote; lastWord = string.Empty; }
                continue;
            }
            if (inRegex)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < js.Length) { sb.Append(js[++i]); continue; }
                if (c == '[' && !inRegexCharClass) inRegexCharClass = true;
                else if (c == ']' && inRegexCharClass) inRegexCharClass = false;
                else if (c == '/' && !inRegexCharClass)
                {
                    inRegex = false;
                    lastSig = '/';
                    lastWord = string.Empty;
                }
                continue;
            }
            if (c is '"' or '\'' or '`')
            {
                FlushWord();
                inString = true;
                quote = c;
                sb.Append(c);
                continue;
            }
            if (c == '/' && i + 1 < js.Length && js[i + 1] == '/')
            {
                FlushWord();
                i += 2;
                while (i < js.Length && js[i] != '\n') i++;
                i--;
                continue;
            }
            if (c == '/' && i + 1 < js.Length && js[i + 1] == '*')
            {
                FlushWord();
                i += 2;
                while (i + 1 < js.Length && !(js[i] == '*' && js[i + 1] == '/')) i++;
                i++;
                if (sb.Length > 0 && sb[^1] is not (' ' or '\n')) sb.Append(' ');
                continue;
            }
            if (c == '/' && CanPrecedeRegex(lastSig, lastWord))
            {
                FlushWord();
                inRegex = true;
                inRegexCharClass = false;
                sb.Append(c);
                continue;
            }
            if (c == '\r') continue;
            if (c == '\n')
            {
                FlushWord();
                while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                lastSig = '\n';
                continue;
            }
            if (c is ' ' or '\t')
            {
                FlushWord();
                if (sb.Length > 0 && sb[^1] is not (' ' or '\n')) sb.Append(' ');
                continue;
            }

            if (char.IsLetterOrDigit(c) || c is '_' or '$')
            {
                wordBuf.Append(c);
            }
            else
            {
                FlushWord();
                lastWord = string.Empty;
            }
            lastSig = c;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private sealed record WebBundlePayload(
        string Id,
        string Title,
        string? Description,
        string ThemeMode,
        string SlideTransitionMs,
        Dictionary<string, Dictionary<string, string>> Profiles,
        WebGeneratorInfoModel Generator,
        List<SlideRenderModel> Slides,
        Dictionary<string, LinkCardRenderModel>? LinkCards = null,
        Dictionary<string, SessionPreviewSidecarModel>? ExternalPreviews = null);
    private sealed record WebGeneratorInfoModel(string ToolName, string Executable, string Version, string Runtime, string GeneratedAtUtc, string AppName = "Meloberry", string RepositoryUrl = "https://github.com/doc-shashank/meloberry-cli/");
    internal sealed record LinkCardRenderModel(string Id, string? Title, string? TextHtml, string? ImagePath);
    internal sealed record ArticlePreviewSidecarModel(string Id, string? RefKey, int Index, string Title, string Excerpt);
    internal sealed record SessionPreviewSidecarModel(string SessionId, string Title, string Description, List<ArticlePreviewSidecarModel> Articles);
    internal sealed record SlideRenderModel(
        string Id,
        string Kind,
        Dictionary<string, string>? LayoutOverrides,
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
        RelabelRenderModel? Relabel = null,
        string? RefKey = null,
        int? ComponentIndex = null,
        ClassifyRenderModel? Classify = null,
        MatchRenderModel? Match = null,
        NumericRenderModel? Numeric = null,
        CaseGroupRenderModel? CaseGroup = null);
    internal sealed record ArticleBlockRenderModel(
        string Id,
        string Kind,
        string? TextHtml,
        string? ImagePath,
        string? CaptionHtml = null,
        string? Alt = null,
        string? Tone = null,
        string? Title = null,
        TableRenderModel? Table = null,
        string? TitleHtml = null,
        DiagramExploreRenderModel? DiagramExplore = null,
        RevealBlockRenderModel? Reveal = null);
    internal sealed record TableRenderModel(
        string? Title,
        string? CaptionHtml,
        bool StickyFirstCol,
        List<int> HideableCols,
        List<string> HeadersHtml,
        List<string> Alignments,
        List<List<string>> RowsHtml,
        string? FootnoteHtml,
        string? TitleHtml = null);
    internal sealed record McqRenderModel(string PromptHtml, bool IsMultiCorrect, List<McqOptionRenderModel> Options, string? ImagePath = null, string? ExplanationHtml = null);
    internal sealed record McqOptionRenderModel(int Index, string? TextHtml, string? ImagePath, bool IsCorrect);
    internal sealed record FlashcardRenderModel(FlashcardSideRenderModel Front, FlashcardSideRenderModel Back);
    internal sealed record FlashcardSideRenderModel(string? TextHtml, string? ImagePath, string Position);
    internal sealed record FillBlankSlotRenderModel(int Index, List<string> Answers, string? PrimaryAnswerHtml = null);
    internal sealed record FillBlankRenderModel(string PromptHtml, List<FillBlankSlotRenderModel> Blanks, bool Fuzzy, bool CaseSensitive, string? ExplanationHtml, string? Placeholder = null);
    internal sealed record AssertionReasonRenderModel(string AssertionHtml, string ReasonHtml, string Answer, string Scheme, string? ExplanationHtml);
    internal sealed record SpotErrorSegmentRenderModel(int? SpanIndex, string Html, bool IsCandidate, bool IsError, List<string>? Corrections, string? PrimaryCorrectionHtml = null);
    internal sealed record SpotErrorRenderModel(List<SpotErrorSegmentRenderModel> Segments, bool RequireFix, bool AllowNoError, bool NoError, string? ExplanationHtml, string? ImagePath = null);
    internal sealed record DiffCellRenderModel(
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
    internal sealed record DifferentiationRenderModel(
        string Mode,
        string? Title,
        string? TitleHtml,
        bool Bank,
        bool Fuzzy,
        bool CaseSensitive,
        bool AllowNoError,
        List<string> HeadersHtml,
        List<string>? Alignments,
        List<List<string>> RowsHtml,
        List<List<string>>? RowsRaw,
        List<DiffCellRenderModel> SpecialCells,
        string? ExplanationHtml,
        string? Placeholder = null);
    internal sealed record MechanismStepRenderModel(int StepId, int OrderGroup, string Html, bool IsDistractor);
    internal sealed record MechanismRenderModel(string? Title, string? TitleHtml, List<MechanismStepRenderModel> Steps, string? ExplanationHtml);
    internal sealed record RelabelBoxRenderModel(int Index, string Label, List<string> Aliases, double X, double Y, double W, double H);
    internal sealed record RelabelRenderModel(
        string ImagePath,
        string? PromptHtml,
        string Mode,
        string Style,
        bool Fuzzy,
        bool CaseSensitive,
        List<RelabelBoxRenderModel> Boxes,
        List<string> Distractors,
        string? ExplanationHtml);
    internal sealed record DiagramHotspotRenderModel(int Index, string Label, string NoteHtml, double X, double Y, double W, double H);
    internal sealed record DiagramExploreRenderModel(string ImagePath, string? Title, string? TitleHtml, string? CaptionHtml, string? Alt, List<DiagramHotspotRenderModel> Hotspots);
    internal sealed record RevealStepRenderModel(int Index, string HeadingHtml, string BodyHtml);
    internal sealed record RevealBlockRenderModel(string? Title, string? TitleHtml, bool ShowFirst, List<RevealStepRenderModel> Steps);
    internal sealed record ClassifyBucketRenderModel(int BucketId, string Name, string NameHtml);
    internal sealed record ClassifyItemRenderModel(int ItemId, int BucketId, string Html);
    internal sealed record ClassifyRenderModel(string? Title, string? TitleHtml, List<ClassifyBucketRenderModel> Buckets, List<ClassifyItemRenderModel> Items, string? ExplanationHtml);
    internal sealed record MatchPairRenderModel(int PairId, string LeftHtml, string RightHtml);
    internal sealed record MatchBankOptionRenderModel(int OptionId, string Html, int? CorrectPairId);
    internal sealed record MatchRenderModel(string? Title, string? TitleHtml, List<MatchPairRenderModel> Pairs, List<MatchBankOptionRenderModel> RightPool, string? ExplanationHtml);
    internal sealed record NumericRenderModel(string PromptHtml, double Answer, double Tolerance, string ToleranceMode, string? Unit, string? Placeholder, string? ImagePath, string? ExplanationHtml);
    internal sealed record CaseGroupRenderModel(string? Title, string? TitleHtml, string StemHtml, string? ImagePath, string? CaptionHtml, List<SlideRenderModel> Questions, string? ExplanationHtml);
}
