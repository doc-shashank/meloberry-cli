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
    ChainTransactionManager? transactionManager = null,
    IWorkspaceRepository? workspaceRepository = null) : IWebAppGenerator
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
    private static readonly Regex MarkdownUrlAttributeRegex = new(@"(?<attr>\b(?:href|src)\s*=\s*)(?<quote>[""'])(?<url>[^""']*)\k<quote>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlTagStripRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceCollapseRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string RenderSafeMarkdownHtml(string markdown) =>
        MarkdownUrlAttributeRegex.Replace(Markdown.ToHtml(markdown, MarkdownPipeline), static match =>
        {
            var rawAttr = match.Groups["attr"].Value;
            var q = match.Groups["quote"].Value;
            var decoded = WebUtility.HtmlDecode(match.Groups["url"].Value);
            var normalized = new string(decoded.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
            if (normalized.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
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
        string? outputDirectory, CancellationToken cancellationToken = default)
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
            cancellationToken);
    }

    public Task<Result<string>> GenerateForQuizAsync(
        Quiz quiz, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputDirectory, CancellationToken cancellationToken = default)
    {
        var title = string.IsNullOrWhiteSpace(quiz.Name) ? "Meloberry Quiz" : quiz.Name;
        var desc = string.IsNullOrWhiteSpace(quiz.Description) ? $"Standalone quiz ({quiz.Questions.Count} questions)" : quiz.Description;
        return WriteWebAppBundleAsync(quiz.Id, title, desc, storageDirectory,
            quiz.Questions.Select(q => BuildQuestionSlide(q, layouts, null)).ToList(), layouts, MbypkgExporter.CollectQuizAssets(quiz), outputDirectory, null, null, cancellationToken);
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
                    !string.IsNullOrWhiteSpace(p.Title) ? RenderInlineSafeMarkdownHtml(p.Title) : null);
            }
            return new ArticleBlockRenderModel(
                b.Id, "table", null, null,
                Title: b.CalloutTitle,
                Table: tableModel,
                TitleHtml: !string.IsNullOrWhiteSpace(b.CalloutTitle) ? RenderInlineSafeMarkdownHtml(b.CalloutTitle) : null);
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
                diagModel = new DiagramExploreRenderModel(
                    imgPath,
                    dp.Title ?? b.CalloutTitle,
                    !string.IsNullOrWhiteSpace(dp.Title ?? b.CalloutTitle) ? RenderInlineSafeMarkdownHtml((dp.Title ?? b.CalloutTitle)!) : null,
                    !string.IsNullOrWhiteSpace(dp.Caption ?? b.Caption) ? RenderSafeMarkdownHtml((dp.Caption ?? b.Caption)!) : null,
                    b.Alt,
                    hotspots);
            }
            return new ArticleBlockRenderModel(
                b.Id, "diagram-explore", null, EncodeAssetPath(b.ImagePath),
                CaptionHtml: !string.IsNullOrWhiteSpace(b.Caption) ? RenderSafeMarkdownHtml(b.Caption) : null,
                Alt: b.Alt,
                Title: b.CalloutTitle,
                TitleHtml: !string.IsNullOrWhiteSpace(b.CalloutTitle) ? RenderInlineSafeMarkdownHtml(b.CalloutTitle) : null,
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
                revModel = new RevealBlockRenderModel(
                    rp.Title ?? b.CalloutTitle,
                    !string.IsNullOrWhiteSpace(rp.Title ?? b.CalloutTitle) ? RenderInlineSafeMarkdownHtml((rp.Title ?? b.CalloutTitle)!) : null,
                    rp.ShowFirst,
                    steps);
            }
            return new ArticleBlockRenderModel(
                b.Id, "reveal", null, null,
                Title: b.CalloutTitle,
                TitleHtml: !string.IsNullOrWhiteSpace(b.CalloutTitle) ? RenderInlineSafeMarkdownHtml(b.CalloutTitle) : null,
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
                FillBlank: new FillBlankRenderModel(promptHtml, blanks, fb.Fuzzy, fb.CaseSensitive, expHtml, fb.Placeholder));
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
                    !string.IsNullOrWhiteSpace(cl.Title) ? RenderInlineSafeMarkdownHtml(cl.Title) : null,
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
                    !string.IsNullOrWhiteSpace(mt.Title) ? RenderInlineSafeMarkdownHtml(mt.Title) : null,
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
            var subSlides = new List<SlideRenderModel>();
            foreach (var sub in cs.Questions ?? [])
            {
                if (sub is null || !Quiz.TryParseQuestionType(sub.Type, out var subType) || subType == QuestionType.CaseGroup)
                    continue;
                var subId = $"{question.Id}_sub{sub.Index}";
                var childDomain = sub.RehydrateToDomainQuestion(subType, subId, question.ContainerId);
                subSlides.Add(BuildQuestionSlide(childDomain, layouts, mergedLayout));
            }

            var expHtml = !string.IsNullOrWhiteSpace(cs.Explanation) ? RenderSafeMarkdownHtml(cs.Explanation) : null;
            return new SlideRenderModel(question.Id, "case", mergedLayout, null, null, null, null,
                CaseGroup: new CaseGroupRenderModel(
                    cs.Title,
                    !string.IsNullOrWhiteSpace(cs.Title) ? RenderInlineSafeMarkdownHtml(cs.Title) : null,
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
        if (componentOverrides is not null)
        {
            foreach (var kv in componentOverrides)
            {
                if (LayoutBoundsCatalog.IsValidPropertyForTarget(targetType, kv.Key) && !LayoutBoundsCatalog.IsGlobalOnlyProperty(kv.Key))
                    result[LayoutBoundsCatalog.GetCanonicalPropertyName(targetType, kv.Key)] = kv.Value;
            }
        }
        if (questionOverrides is not null)
        {
            foreach (var kv in questionOverrides)
            {
                if (LayoutBoundsCatalog.IsValidPropertyForTarget(targetType, kv.Key) && !LayoutBoundsCatalog.IsGlobalOnlyProperty(kv.Key))
                    result[LayoutBoundsCatalog.GetCanonicalPropertyName(targetType, kv.Key)] = kv.Value;
            }
        }
        return result;
    }

    private async Task<Result<string>> WriteWebAppBundleAsync(
        string id, string title, string? description, string storageDirectory, List<SlideRenderModel> slides,
        IReadOnlyDictionary<string, LayoutProfile> layouts, IReadOnlyList<string> referencedAssets, string? outputDirectory,
        Dictionary<string, LinkCardRenderModel>? linkCards,
        Dictionary<string, SessionPreviewSidecarModel>? externalPreviews,
        CancellationToken cancellationToken)
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
        var generatorInfo = new WebGeneratorInfoModel(toolMeta.ProductName, toolMeta.ExecutableName, toolMeta.Version, toolMeta.Runtime, DateTimeOffset.UtcNow.ToString("O"), toolMeta.AppName, toolMeta.RepositoryUrl);
        var payloadJson = JsonSerializer.Serialize(new WebBundlePayload(id, title, description, globalTheme, slideMs, generatorInfo, slides, linkCards, externalPreviews), JsonOptions);

        bool includeKatex = slides.Any(SlideContainsMath) ||
            (linkCards is not null && linkCards.Values.Any(c => !string.IsNullOrEmpty(c.TextHtml) && c.TextHtml.Contains("class=\"math\"", StringComparison.Ordinal)));
        if (includeKatex)
            await CopyEmbeddedKatexAssetsAsync(outDir, cancellationToken);

        var indexPath = Path.Combine(outDir, "index.html");
        await File.WriteAllTextAsync(indexPath, BuildHtml(title, description, globalTheme, toolMeta, includeKatex), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outDir, "styles.css"), BuildCss(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outDir, "app.js"), BuildJs(payloadJson), cancellationToken);

        var sidecarJson = JsonSerializer.Serialize(BuildSessionPreviewSidecar(id, title, description, slides), JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(outDir, "preview-index.json"), sidecarJson, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outDir, "preview-index.js"), $"window.__mbyRegisterSessionPreview&&window.__mbyRegisterSessionPreview({sidecarJson});", cancellationToken);
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
        var decoded = WebUtility.HtmlDecode(HtmlTagStripRegex.Replace(html, " "));
        return WhitespaceCollapseRegex.Replace(decoded, " ").Trim();
    }

    private static string TruncateExcerpt(string text, int maxLen)
    {
        var clean = WhitespaceCollapseRegex.Replace(text ?? string.Empty, " ").Trim();
        if (clean.Length <= maxLen) return clean;
        return clean[..Math.Max(0, maxLen - 1)].TrimEnd() + "…";
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
                b.Table.RowsHtml.Any(r => r.Any(HasMath)))) ||
            (b.DiagramExplore is not null && (
                HasMath(b.DiagramExplore.TitleHtml) ||
                HasMath(b.DiagramExplore.CaptionHtml) ||
                b.DiagramExplore.Hotspots.Any(h => HasMath(h.NoteHtml)))) ||
            (b.Reveal is not null && (
                HasMath(b.Reveal.TitleHtml) ||
                b.Reveal.Steps.Any(st => HasMath(st.HeadingHtml) || HasMath(st.BodyHtml)))))) return true;
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
        if (slide.Classify is not null && (
            HasMath(slide.Classify.TitleHtml) ||
            HasMath(slide.Classify.ExplanationHtml) ||
            slide.Classify.Buckets.Any(b => HasMath(b.NameHtml)) ||
            slide.Classify.Items.Any(i => HasMath(i.Html)))) return true;
        if (slide.Match is not null && (
            HasMath(slide.Match.TitleHtml) ||
            HasMath(slide.Match.ExplanationHtml) ||
            slide.Match.Pairs.Any(p => HasMath(p.LeftHtml) || HasMath(p.RightHtml)) ||
            slide.Match.RightPool.Any(r => HasMath(r.Html)))) return true;
        if (slide.Numeric is not null && (HasMath(slide.Numeric.PromptHtml) || HasMath(slide.Numeric.ExplanationHtml))) return true;
        if (slide.CaseGroup is not null && (
            HasMath(slide.CaseGroup.TitleHtml) ||
            HasMath(slide.CaseGroup.StemHtml) ||
            HasMath(slide.CaseGroup.CaptionHtml) ||
            HasMath(slide.CaseGroup.ExplanationHtml) ||
            slide.CaseGroup.Questions.Any(SlideContainsMath))) return true;
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
  <div id="linkPreviewPopover" class="mby-link-popover hidden" role="dialog" aria-live="polite"></div>
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
.mby-icon-btn { width: 34px; height: 34px; border-radius: 9px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font-size: 16px; font-weight: 700; cursor: pointer; display: inline-flex; align-items: center; justify-content: center; line-height: 1; padding: 0; transition: border-color 140ms ease, color 140ms ease, background 140ms ease; }
.mby-icon-btn:hover { border-color: var(--accent); color: var(--accent); background: var(--accent-soft); }
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
const searchModal = $('searchModal'), searchInput = $('searchInput'), searchClearBtn = $('searchClearBtn'), searchFiltersEl = $('searchFilters'), searchResultsEl = $('searchResults'), searchMetaEl = $('searchMeta'), topbarSearchBtn = $('topbarSearchBtn'), greetingSearchBtn = $('greetingSearchBtn'), searchCloseBtn = $('searchCloseBtn'), linkPreviewPopover = $('linkPreviewPopover');
const SAVE_ICON_SVG = '<svg class="mby-save-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M17 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V7l-4-4zm-5 16c-1.66 0-3-1.34-3-3s1.34-3 3-3 3 1.34 3 3-1.34 3-3 3zm3-10H5V5h10v4z"/></svg>';
const GITHUB_ICON_SVG = '<svg class="mby-github-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 .5C5.65.5.5 5.65.5 12c0 5.08 3.29 9.39 7.86 10.91.58.11.79-.25.79-.56 0-.28-.01-1.02-.02-2-3.2.7-3.87-1.54-3.87-1.54-.52-1.33-1.28-1.68-1.28-1.68-1.05-.71.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.77 2.7 1.26 3.36.96.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.68 0-1.26.45-2.29 1.19-3.09-.12-.29-.52-1.47.11-3.06 0 0 .97-.31 3.17 1.18a11.1 11.1 0 0 1 5.78 0c2.2-1.49 3.17-1.18 3.17-1.18.63 1.59.23 2.77.11 3.06.74.8 1.19 1.83 1.19 3.09 0 4.41-2.69 5.39-5.25 5.67.41.36.78 1.07.78 2.15 0 1.55-.01 2.8-.01 3.18 0 .31.21.67.8.56A11.51 11.51 0 0 0 23.5 12C23.5 5.65 18.35.5 12 .5z"/></svg>';
const TABLE_EYE_SVG = '<svg class="mby-th-eye-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></svg>';
const TABLE_EYE_OFF_SVG = '<svg class="mby-th-eye-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"/><line x1="1" y1="1" x2="23" y2="23"/></svg>';
function createRetryBtn(onRetry, visible, label = 'Retry', compact = false) {
  const btn = mk('button', 'mby-retry-btn' + (compact ? ' is-compact' : '') + (visible ? '' : ' hidden'), label);
  btn.type = 'button';
  btn.addEventListener('click', () => { if (onRetry) onRetry(); });
  return btn;
}
function createExplanationBox(html, initiallyVisible = false) {
  if (!html) return null;
  const content = mk('div');
  content.innerHTML = html;
  renderMathIn(content);
  return mk('div', 'mby-explanation-box' + (initiallyVisible ? '' : ' hidden'), null, [mk('div', 'mby-explanation-title', 'Explanation'), content]);
}
function recordGradeAttempt(m, isCorrect) {
  if (!m) return;
  m.checkAttempts = (m.checkAttempts || 0) + 1;
  if (m.firstTryCorrect === null) m.firstTryCorrect = isCorrect;
  m.isCorrect = isCorrect;
}
function setFeedbackMessage(el, text, isCorrect) {
  el.textContent = text;
  el.style.color = isCorrect ? 'var(--ok)' : 'var(--bad)';
  el.style.fontWeight = '600';
}
function bindDropTarget(el, mimeTypes, onDrop, checkContains = false) {
  const accepted = Array.isArray(mimeTypes) ? mimeTypes : [mimeTypes];
  el.addEventListener('dragover', e => {
    if (e.dataTransfer && accepted.some(t => e.dataTransfer.types.includes(t))) {
      e.preventDefault();
      el.classList.add('mby-drop-over');
    }
  });
  el.addEventListener('dragleave', e => {
    if (!checkContains || !el.contains(e.relatedTarget)) el.classList.remove('mby-drop-over');
  });
  el.addEventListener('drop', e => {
    e.preventDefault();
    el.classList.remove('mby-drop-over');
    onDrop(e);
  });
}

let currentIndex = 0, isFinished = false, sessionStartedAt = new Date(), activeLeafEnterMs = null, lastVisitedLeafId = null, autoSaveSent = false;
const activeQuizQuestionIdx = new Map(), quizControllers = new Map(), mcqStateMap = new Map(), flashcardStateMap = new Map(), gradedStateMap = new Map();
const isGradedKind = k => k === 'mcq' || k === 'fill-blank' || k === 'assertion-reason' || k === 'spot-error' || k === 'diff' || k === 'mechanism' || k === 'relabel' || k === 'classify' || k === 'match' || k === 'numeric' || k === 'case';
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
  const rawVer = String(g.version || '2.1.0').trim();
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
    if (opt.imagePath) btn.appendChild(mk('div', 'mby-option-media', null, [makeZoomImg(opt.imagePath)]));
    if (opt.textHtml) { const t = mk('div', 'mby-option-text'); t.innerHTML = opt.textHtml; renderMathIn(t); btn.appendChild(t); }
    btn.addEventListener('click', () => {
      if (!mcq.isMultiCorrect) { st.selected.clear(); optionBtns.forEach(b => b.classList.remove('selected')); }
      if (st.selected.has(opt.index)) { st.selected.delete(opt.index); btn.classList.remove('selected'); }
      else { st.selected.add(opt.index); btn.classList.add('selected'); }
    });
    grid.appendChild(btn); return btn;
  });

  const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
  const feedback = mk('span'); if (st.checked) renderMcqFeedback(feedback, st.isCorrect);
  const explanationBox = createExplanationBox(mcq.explanationHtml, st.checked);
  const retryBtn = createRetryBtn(() => {
    st.selected.clear();
    st.checked = false;
    st.isCorrect = null;
    optionBtns.forEach(b => b.classList.remove('selected', 'correct', 'wrong'));
    feedback.textContent = '';
    if (explanationBox) explanationBox.classList.add('hidden');
    retryBtn.classList.add('hidden');
    if (m) m.isCorrect = null;
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  }, st.checked);

  checkBtn.addEventListener('click', () => {
    let allCorrect = true;
    optionBtns.forEach((btn, i) => {
      const opt = mcq.options[i], picked = st.selected.has(opt.index);
      btn.classList.remove('correct', 'wrong');
      if (opt.isCorrect) btn.classList.add('correct'); else if (picked) btn.classList.add('wrong');
      if (picked !== opt.isCorrect) allCorrect = false;
    });
    st.checked = true; st.isCorrect = allCorrect;
    if (m) m.selectedOptionIndices = Array.from(st.selected).sort((a, b) => a - b);
    recordGradeAttempt(m, allCorrect);
    renderMcqFeedback(feedback, allCorrect);
    retryBtn.classList.remove('hidden');
    if (explanationBox) explanationBox.classList.remove('hidden');
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  card.appendChild(mk('div', 'mby-card-body', null, [prompt, promptMedia, grid, mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]), explanationBox]));
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

  if (t.title || t.titleHtml) {
    const toolbar = mk('div', 'mby-table-toolbar');
    const titleEl = mk('div', 'mby-table-title');
    if (t.titleHtml) { titleEl.innerHTML = t.titleHtml; renderMathIn(titleEl); }
    else { titleEl.textContent = t.title || 'Table'; }
    toolbar.appendChild(titleEl);
    wrap.appendChild(toolbar);
  }

  const scroll = mk('div', 'mby-table-scroll');
  const tableEl = mk('table');
  const thead = mk('thead');
  const headTr = mk('tr');
  (t.headersHtml || []).forEach((hHtml, idx) => {
    const colNum = idx + 1;
    const th = mk('th');
    if (t.alignments?.[idx]) th.style.textAlign = t.alignments[idx];
    if (hideableSet.has(colNum)) {
      th.classList.add('mby-th-hideable');
      th.tabIndex = 0;
      th.setAttribute('role', 'button');
      th.setAttribute('aria-pressed', 'false');
      th.title = 'Click to hide or show column';
      const inner = mk('span', 'mby-th-hideable-inner');
      const lbl = mk('span');
      lbl.innerHTML = hHtml;
      renderMathIn(lbl);
      const eyeWrap = mk('span');
      eyeWrap.innerHTML = TABLE_EYE_SVG;
      inner.appendChild(lbl);
      inner.appendChild(eyeWrap);
      th.appendChild(inner);
      const toggleColumn = () => {
        const isHidden = hiddenCols.has(colNum);
        if (isHidden) hiddenCols.delete(colNum); else hiddenCols.add(colNum);
        const nowHidden = hiddenCols.has(colNum);
        th.classList.toggle('is-col-hidden', nowHidden);
        th.setAttribute('aria-pressed', nowHidden ? 'true' : 'false');
        eyeWrap.innerHTML = nowHidden ? TABLE_EYE_OFF_SVG : TABLE_EYE_SVG;
        (tdByCol.get(colNum) || []).forEach(td => {
          td.classList.toggle('mby-cell-masked', nowHidden);
          td.classList.remove('peek');
        });
      };
      th.addEventListener('click', toggleColumn);
      th.addEventListener('keydown', e => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          toggleColumn();
        }
      });
    } else {
      th.innerHTML = hHtml;
      renderMathIn(th);
    }
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
  const placeholderAttr = fb.placeholder ? ` placeholder="${escapeHtml(fb.placeholder)}"` : '';
  (fb.blanks || []).forEach((b, idx) => {
    html = html.replace('MBYBLANKSLOT' + idx + 'END', `<span class="mby-blank-wrap" data-slot="${idx}"><input type="text" class="mby-blank-input" data-slot="${idx}" autocomplete="off" spellcheck="false"${placeholderAttr} /><span class="mby-blank-expected hidden" data-expected="${idx}"></span></span>`);
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
  const explanationBox = createExplanationBox(fb.explanationHtml, st.checked);
  const retryBtn = createRetryBtn(() => {
    st.values = [];
    st.checked = false;
    st.isCorrect = null;
    st.overridden = false;
    inputs.forEach((inp, idx) => {
      inp.value = '';
      inp.classList.remove('correct', 'fuzzy-ok', 'wrong');
      if (expectedEls[idx]) { expectedEls[idx].innerHTML = ''; expectedEls[idx].classList.add('hidden'); }
    });
    feedback.textContent = '';
    overrideBtn.classList.add('hidden');
    if (explanationBox) explanationBox.classList.add('hidden');
    retryBtn.classList.add('hidden');
    if (m) m.isCorrect = null;
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  }, st.checked);

  function applyGradeUi(allOk, anyFuzzy) {
    setFeedbackMessage(feedback, allOk ? (anyFuzzy ? 'Almost Correct!' : 'Correct!') : 'Review highlighted blanks.', allOk);
    overrideBtn.classList.toggle('hidden', allOk);
    retryBtn.classList.remove('hidden');
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
    recordGradeAttempt(m, allOk);
    applyGradeUi(allOk, anyFuzzy);
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  overrideBtn.addEventListener('click', () => {
    st.isCorrect = true; st.overridden = true;
    inputs.forEach(inp => { inp.classList.remove('wrong'); inp.classList.add('correct'); });
    if (m) m.isCorrect = true;
    setFeedbackMessage(feedback, 'Marked correct (self-override).', true);
    overrideBtn.classList.add('hidden');
    retryBtn.classList.remove('hidden');
    if (onAnswered) onAnswered();
  });

  card.appendChild(mk('div', 'mby-card-body', null, [promptEl, mk('div', 'mby-action-row', null, [checkBtn, overrideBtn, retryBtn, feedback]), explanationBox]));
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
  const explanationBox = createExplanationBox(ar.explanationHtml, st.checked);
  const retryBtn = createRetryBtn(() => {
    st.selectedCode = null;
    st.checked = false;
    st.isCorrect = null;
    optBtns.forEach(b => b.classList.remove('selected', 'correct', 'wrong'));
    feedback.textContent = '';
    if (explanationBox) explanationBox.classList.add('hidden');
    retryBtn.classList.add('hidden');
    if (m) m.isCorrect = null;
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  }, st.checked);

  checkBtn.addEventListener('click', () => {
    const isCorrect = st.selectedCode === ar.answer;
    optBtns.forEach((btn, idx) => {
      const code = ar.options[idx].code;
      btn.classList.remove('correct', 'wrong');
      if (code === ar.answer) btn.classList.add('correct');
      else if (code === st.selectedCode) btn.classList.add('wrong');
    });
    st.checked = true; st.isCorrect = isCorrect;
    recordGradeAttempt(m, isCorrect);
    renderMcqFeedback(feedback, isCorrect);
    retryBtn.classList.remove('hidden');
    if (explanationBox) explanationBox.classList.remove('hidden');
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  card.appendChild(mk('div', 'mby-card-body', null, [stack, grid, mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]), explanationBox]));
  return card;
}

function buildSpotErrorCard(slide, onAnswered) {
  const m = metricById.get(slide.id), se = slide.spotError;
  const card = mk('div', 'mby-card', null, [createUpperTab('SPOT THE ERROR')]);
  applySlideLayoutVars(card, slide.layout);
  if (!gradedStateMap.has(slide.id)) gradedStateMap.set(slide.id, { selectedSpans: new Set(), pickedNoError: false, fixes: {}, checked: false, isCorrect: null });
  const st = gradedStateMap.get(slide.id);

  const promptMedia = se.imagePath ? mk('div', 'mby-prompt-media', null, [makeZoomImg(se.imagePath)]) : null;
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
    const chipWrap = mk('span', 'mby-error-chip-wrap');
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
    chipWrap.appendChild(chip);
    chipByIdx.set(seg.spanIndex, { chip, seg });
    stmtEl.appendChild(chipWrap);
  });

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
  }

  rebuildFixInputs();
  const feedback = mk('span');
  const explanationBox = createExplanationBox(se.explanationHtml, st.checked);
  const retryBtn = createRetryBtn(() => {
    st.selectedSpans.clear();
    st.pickedNoError = false;
    st.fixes = {};
    st.checked = false;
    st.isCorrect = null;
    stmtEl.querySelectorAll('.mby-error-fix-badge').forEach(el => el.remove());
    chipByIdx.forEach(({ chip }) => chip.classList.remove('selected', 'correct', 'wrong', 'missed-error'));
    if (noErrorBtn) noErrorBtn.classList.remove('selected', 'correct', 'wrong');
    rebuildFixInputs();
    feedback.textContent = '';
    if (explanationBox) explanationBox.classList.add('hidden');
    retryBtn.classList.add('hidden');
    if (m) m.isCorrect = null;
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  }, st.checked);

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
            chip.insertAdjacentElement('beforebegin', badge);
          }
        } else if (picked) {
          chip.classList.add('wrong');
          allOk = false;
        }
      });
    }

    st.checked = true; st.isCorrect = allOk;
    recordGradeAttempt(m, allOk);
    setFeedbackMessage(feedback, allOk ? 'Correct!' : 'Review highlighted segments.', allOk);
    retryBtn.classList.remove('hidden');
    if (explanationBox) explanationBox.classList.remove('hidden');
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  const actionRowItems = noErrorBtn ? [noErrorBtn, checkBtn, retryBtn, feedback] : [checkBtn, retryBtn, feedback];
  card.appendChild(mk('div', 'mby-card-body', null, [promptMedia, stmtEl, fixRow, mk('div', 'mby-action-row', null, actionRowItems), explanationBox]));
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
  const explanationBox = createExplanationBox(d.explanationHtml, st.checked);

  const blankInputs = new Map();
  const expectedEls = new Map();
  const bankChips = [];
  const spotButtons = new Map();
  let noErrorBtn = null;
  const rearrButtons = new Map();
  const correctionsBox = mk('div', 'mby-diff-corrections hidden');

  const blankSpecs = (d.specialCells || []).filter(sc => sc.isBlank && sc.answers?.length);
  const bankItems = blankSpecs.map((sc, idx) => ({
    idx,
    label: sc.answers[0],
    answers: sc.answers,
    html: sc.primaryAnswerHtml || escapeHtml(sc.answers[0])
  }));
  const itemMatchesNorm = (bankItem, rawVal) => {
    const normVal = normalizeRecallAnswer(rawVal, d.caseSensitive);
    if (!normVal) return false;
    return (bankItem?.answers || []).some(ans => normalizeRecallAnswer(ans, d.caseSensitive) === normVal);
  };

  if (d.mode === 'fill' && d.bank) {
    const shuffledBank = deterministicShuffle(bankItems, `${slide.id}:bank`, x => x.label);
    const bankRow = mk('div', 'mby-diff-bank');
    shuffledBank.forEach(item => {
      const chip = mk('button', 'mby-diff-bank-chip mby-draggable'); chip.type = 'button';
      chip.draggable = true;
      chip.innerHTML = item.html; renderMathIn(chip);
      chip.dataset.chipIdx = String(item.idx);
      chip.addEventListener('click', () => {
        st.bankPickedChipIdx = (st.bankPickedChipIdx === item.idx) ? null : item.idx;
        bankChips.forEach(c => c.classList.toggle('selected', Number(c.dataset.chipIdx) === st.bankPickedChipIdx));
      });
      chip.addEventListener('dragstart', e => {
        if (e.dataTransfer) {
          e.dataTransfer.setData('application/x-mby-diff-bank', String(item.idx));
          e.dataTransfer.setData('text/plain', item.label);
          e.dataTransfer.effectAllowed = 'copyMove';
        }
        chip.classList.add('is-dragging');
      });
      chip.addEventListener('dragend', () => chip.classList.remove('is-dragging'));
      bankChips.push(chip);
      bankRow.appendChild(chip);
    });
    wrap.appendChild(bankRow);
  }

  const refreshBankUsed = () => {
    if (!(d.mode === 'fill' && d.bank)) return;
    const usedChips = new Set();
    const claimedSlots = new Set();
    blankSpecs.forEach(sc => {
      const sIdx = sc.slotIndex;
      const cid = st.chipBySlot[sIdx];
      if (cid === undefined || cid === null || usedChips.has(cid)) return;
      const item = bankItems[cid];
      if (item && itemMatchesNorm(item, st.values[sIdx] || '')) {
        usedChips.add(cid);
        claimedSlots.add(sIdx);
      } else {
        delete st.chipBySlot[sIdx];
      }
    });
    blankSpecs.forEach(sc => {
      const sIdx = sc.slotIndex;
      if (claimedSlots.has(sIdx)) return;
      const val = st.values[sIdx] || '';
      if (!val.trim()) return;
      const matchItem = bankItems.find(it => !usedChips.has(it.idx) && itemMatchesNorm(it, val));
      if (matchItem) {
        usedChips.add(matchItem.idx);
        st.chipBySlot[sIdx] = matchItem.idx;
        claimedSlots.add(sIdx);
      }
    });
    bankChips.forEach(c => c.classList.toggle('used', usedChips.has(Number(c.dataset.chipIdx))));
  };

  const applyBankChipToSlot = (chipIdx, sIdx, inp) => {
    const bankItem = bankItems[chipIdx];
    if (!bankItem || !inp) return;
    for (const [k, v] of Object.entries(st.chipBySlot)) {
      if (v === chipIdx) delete st.chipBySlot[k];
    }
    inp.value = bankItem.label;
    st.values[sIdx] = inp.value;
    st.chipBySlot[sIdx] = chipIdx;
    st.bankPickedChipIdx = null;
    bankChips.forEach(ch => ch.classList.remove('selected'));
    refreshBankUsed();
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
    const fillPlaceholderAttr = d.placeholder ? ` placeholder="${escapeHtml(d.placeholder)}"` : '';
    for (let r = 0; r < rowCount; r++) {
      const tr = mk('tr');
      const cols = d.mode === 'rearrange' ? st.rearrRows[r] : (d.rowsHtml[r] || []).map((html, c) => ({ colIdx: c, html }));
      for (let c = 0; c < cols.length; c++) {
        const td = mk('td');
        if (d.alignments?.[c]) td.style.textAlign = d.alignments[c];
        const spec = specialByCell.get(`${r}:${c}`);

        if (d.mode === 'fill' && spec && spec.isBlank) {
          const sIdx = spec.slotIndex;
          const cellHtml = (cols[c].html || '').replace(`MBYDIFFSLOT${sIdx}END`, `<span class="mby-blank-wrap"><input type="text" class="mby-blank-input" data-diff-slot="${sIdx}" autocomplete="off" spellcheck="false"${fillPlaceholderAttr} /><span class="mby-blank-expected hidden" data-diff-exp="${sIdx}"></span></span>`);
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
                applyBankChipToSlot(st.bankPickedChipIdx, sIdx, inp);
              }
            });
            if (d.bank) {
              bindDropTarget(inp, ['application/x-mby-diff-bank', 'text/plain'], e => {
                const rawChipId = e.dataTransfer?.getData('application/x-mby-diff-bank');
                if (rawChipId !== undefined && rawChipId !== '') {
                  const chipIdx = Number(rawChipId);
                  if (!Number.isNaN(chipIdx)) { applyBankChipToSlot(chipIdx, sIdx, inp); return; }
                }
                const droppedText = e.dataTransfer?.getData('text/plain');
                if (droppedText) {
                  inp.value = droppedText;
                  st.values[sIdx] = inp.value;
                  delete st.chipBySlot[sIdx];
                  refreshBankUsed();
                }
              });
            }
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
          const btn = mk('button', 'mby-diff-cell-btn mby-draggable' + (isSrc ? ' swap-selected' : ''));
          btn.type = 'button';
          btn.draggable = true;
          btn.innerHTML = cols[c].html; renderMathIn(btn);
          btn.addEventListener('dragstart', e => {
            if (e.dataTransfer) {
              e.dataTransfer.setData('application/x-mby-diff-rearr', JSON.stringify({ r, c }));
              e.dataTransfer.effectAllowed = 'move';
            }
            btn.classList.add('is-dragging');
          });
          btn.addEventListener('dragend', () => btn.classList.remove('is-dragging'));
          bindDropTarget(btn, 'application/x-mby-diff-rearr', e => {
            try {
              const src = JSON.parse(e.dataTransfer?.getData('application/x-mby-diff-rearr') || 'null');
              if (src && src.r === r && src.c !== c) {
                const rowArr = st.rearrRows[r];
                const tmp = rowArr[src.c]; rowArr[src.c] = rowArr[c]; rowArr[c] = tmp;
                st.swapSrc = null;
                renderTableBody();
              }
            } catch {}
          });
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
  wrap.appendChild(correctionsBox);

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
  }

  const retryBtn = createRetryBtn(() => {
    st.values = {};
    st.bankPickedChipIdx = null;
    st.chipBySlot = {};
    st.selectedCells.clear();
    st.pickedNoError = false;
    st.swapSrc = null;
    st.rearrRows = (d.rowsHtml || []).map((row, rIdx) => {
      const cols = row.slice(1).map((html, offset) => ({ colIdx: offset + 1, html, raw: d.rowsRaw?.[rIdx]?.[offset + 1] ?? '' }));
      const shuffled = d.mode === 'rearrange' ? deterministicShuffle(cols, `${slide.id}:r${rIdx}`, x => x.raw) : cols;
      return [{ colIdx: 0, html: row[0], raw: d.rowsRaw?.[rIdx]?.[0] ?? '' }, ...shuffled];
    });
    st.checked = false;
    st.isCorrect = null;
    st.overridden = false;
    wrap.querySelectorAll('.mby-diff-fix-badge').forEach(el => el.remove());
    correctionsBox.innerHTML = '';
    correctionsBox.classList.add('hidden');
    if (noErrorBtn) noErrorBtn.classList.remove('selected', 'correct', 'wrong');
    bankChips.forEach(c => c.classList.remove('selected', 'used'));
    overrideBtn.classList.add('hidden');
    feedback.textContent = '';
    if (explanationBox) explanationBox.classList.add('hidden');
    retryBtn.classList.add('hidden');
    renderTableBody();
    if (m) m.isCorrect = null;
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  }, st.checked);

  checkBtn.addEventListener('click', () => {
    let allOk = true, anyFuzzy = false;
    wrap.querySelectorAll('.mby-diff-fix-badge').forEach(el => el.remove());
    correctionsBox.innerHTML = '';
    correctionsBox.classList.add('hidden');

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
      feedback.textContent = allOk ? (anyFuzzy ? 'Almost Correct!' : 'Correct!') : 'Review highlighted cells.';
    } else if (d.mode === 'spot-wrong') {
      const errorCells = (d.specialCells || []).filter(sc => sc.isError);
      const fixItems = [];
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
            if (sc.primaryCorrectionHtml) fixItems.push(sc);
          } else if (picked) {
            btn.classList.add('wrong');
            allOk = false;
          }
        });
      }
      if (fixItems.length > 0) {
        correctionsBox.appendChild(mk('div', 'mby-diff-corrections-title', 'Corrections'));
        const ul = mk('ul', 'mby-diff-correction-list');
        fixItems.forEach(sc => {
          const li = mk('li', 'mby-diff-correction-item');
          const rowLabel = htmlToPlainText(d.rowsHtml[sc.rowIndex]?.[0] || `Row ${sc.rowIndex + 1}`);
          const colLabel = htmlToPlainText(d.headersHtml[sc.colIndex] || `Col ${sc.colIndex + 1}`);
          li.appendChild(mk('span', 'mby-diff-correction-loc', `${rowLabel} • ${colLabel}:`));
          const wrongSpan = mk('span', 'mby-diff-wrong-strike');
          wrongSpan.innerHTML = d.rowsHtml[sc.rowIndex]?.[sc.colIndex] || '';
          renderMathIn(wrongSpan);
          const rightSpan = mk('span', 'mby-diff-right-text');
          rightSpan.innerHTML = '→ ' + sc.primaryCorrectionHtml;
          renderMathIn(rightSpan);
          li.appendChild(wrongSpan);
          li.appendChild(rightSpan);
          ul.appendChild(li);
        });
        correctionsBox.appendChild(ul);
        correctionsBox.classList.remove('hidden');
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

    setFeedbackMessage(feedback, feedback.textContent, allOk);
    st.checked = true; st.isCorrect = allOk; st.overridden = false;
    retryBtn.classList.remove('hidden');
    recordGradeAttempt(m, allOk);
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
    retryBtn.classList.remove('hidden');
    if (onAnswered) onAnswered();
  });

  const actionRowItems = noErrorBtn ? [noErrorBtn, checkBtn, overrideBtn, retryBtn, feedback] : [checkBtn, overrideBtn, retryBtn, feedback];
  card.appendChild(mk('div', 'mby-card-body', null, [wrap, mk('div', 'mby-action-row', null, actionRowItems), explanationBox]));
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
  const explanationBox = createExplanationBox(mech.explanationHtml, st.checked);

  const animateSeqUpdate = mutateFn => {
    const firstRects = new Map();
    seqEl.querySelectorAll('.mby-mech-slot[data-step-id]').forEach(el => {
      firstRects.set(el.dataset.stepId, el.getBoundingClientRect());
    });
    mutateFn();
    renderZones();
    requestAnimationFrame(() => {
      seqEl.querySelectorAll('.mby-mech-slot[data-step-id]').forEach(el => {
        const first = firstRects.get(el.dataset.stepId);
        if (!first) return;
        const last = el.getBoundingClientRect();
        const dy = first.top - last.top;
        if (Math.abs(dy) < 1) return;
        el.style.transition = 'none';
        el.style.transform = `translateY(${dy}px)`;
        el.classList.add('is-moving');
        el.getBoundingClientRect();
        el.style.transition = 'transform 220ms cubic-bezier(0.22, 1, 0.36, 1)';
        el.style.transform = 'translateY(0)';
        setTimeout(() => {
          el.style.transition = '';
          el.style.transform = '';
          el.classList.remove('is-moving');
        }, 240);
      });
      updateScrollDownHint();
    });
  };

  bindDropTarget(bankEl, 'application/x-mby-mech-step', e => {
    try {
      const payload = JSON.parse(e.dataTransfer?.getData('application/x-mby-mech-step') || 'null');
      if (payload && payload.source === 'seq' && st.placedIds.includes(payload.sid)) {
        animateSeqUpdate(() => {
          st.placedIds = st.placedIds.filter(x => x !== payload.sid);
          if (!st.bankIds.includes(payload.sid)) st.bankIds.push(payload.sid);
        });
      }
    } catch {}
  }, true);

  seqEl.addEventListener('dragover', e => {
    if (e.dataTransfer?.types.includes('application/x-mby-mech-step')) {
      e.preventDefault();
      seqEl.classList.add('mby-drop-over');
    }
  });
  seqEl.addEventListener('dragleave', e => { if (!seqEl.contains(e.relatedTarget)) seqEl.classList.remove('mby-drop-over'); });
  seqEl.addEventListener('drop', e => {
    if (e.target.closest('.mby-mech-slot')) return;
    e.preventDefault();
    seqEl.classList.remove('mby-drop-over');
    try {
      const payload = JSON.parse(e.dataTransfer?.getData('application/x-mby-mech-step') || 'null');
      if (!payload) return;
      animateSeqUpdate(() => {
        if (payload.source === 'bank') {
          st.bankIds = st.bankIds.filter(x => x !== payload.sid);
          if (!st.placedIds.includes(payload.sid)) st.placedIds.push(payload.sid);
        } else if (payload.source === 'seq') {
          st.placedIds = st.placedIds.filter(x => x !== payload.sid);
          st.placedIds.push(payload.sid);
        }
      });
    } catch {}
  });

  const renderZones = () => {
    bankEl.innerHTML = '';
    if (st.bankIds.length === 0) {
      bankEl.appendChild(mk('div', 'mby-mech-empty', 'All steps placed in sequence.'));
    } else {
      st.bankIds.forEach(sid => {
        const s = stepById.get(sid); if (!s) return;
        const chip = mk('button', 'mby-mech-chip mby-draggable'); chip.type = 'button';
        chip.draggable = true;
        chip.innerHTML = s.html; renderMathIn(chip);
        chip.addEventListener('dragstart', e => {
          if (e.dataTransfer) {
            e.dataTransfer.setData('application/x-mby-mech-step', JSON.stringify({ source: 'bank', sid }));
            e.dataTransfer.effectAllowed = 'move';
          }
          chip.classList.add('is-dragging');
        });
        chip.addEventListener('dragend', () => chip.classList.remove('is-dragging'));
        chip.addEventListener('click', () => {
          animateSeqUpdate(() => {
            st.bankIds = st.bankIds.filter(x => x !== sid);
            st.placedIds.push(sid);
          });
        });
        bankEl.appendChild(chip);
      });
    }

    seqEl.innerHTML = '';
    if (st.placedIds.length === 0) {
      seqEl.appendChild(mk('div', 'mby-mech-empty', 'Click or drag steps above in causal order (exclude distractors).'));
    } else {
      st.placedIds.forEach((sid, idx) => {
        const s = stepById.get(sid); if (!s) return;
        const slot = mk('div', 'mby-mech-slot mby-draggable');
        slot.draggable = true;
        slot.dataset.stepId = String(sid);
        slot.addEventListener('dragstart', e => {
          if (e.dataTransfer) {
            e.dataTransfer.setData('application/x-mby-mech-step', JSON.stringify({ source: 'seq', sid }));
            e.dataTransfer.effectAllowed = 'move';
          }
          slot.classList.add('is-dragging');
        });
        slot.addEventListener('dragend', () => slot.classList.remove('is-dragging'));
        slot.addEventListener('dragover', e => {
          if (e.dataTransfer?.types.includes('application/x-mby-mech-step')) {
            e.preventDefault();
            e.stopPropagation();
            slot.classList.add('mby-drop-over');
          }
        });
        slot.addEventListener('dragleave', () => slot.classList.remove('mby-drop-over'));
        slot.addEventListener('drop', e => {
          e.preventDefault();
          e.stopPropagation();
          slot.classList.remove('mby-drop-over');
          seqEl.classList.remove('mby-drop-over');
          try {
            const payload = JSON.parse(e.dataTransfer?.getData('application/x-mby-mech-step') || 'null');
            if (!payload || payload.sid === sid) return;
            const rect = slot.getBoundingClientRect();
            const insertAfter = e.clientY > rect.top + (rect.height / 2);
            animateSeqUpdate(() => {
              st.bankIds = st.bankIds.filter(x => x !== payload.sid);
              st.placedIds = st.placedIds.filter(x => x !== payload.sid);
              const targetIdx = st.placedIds.indexOf(sid);
              const destIdx = targetIdx < 0 ? st.placedIds.length : (insertAfter ? targetIdx + 1 : targetIdx);
              st.placedIds.splice(destIdx, 0, payload.sid);
            });
          } catch {}
        });
        const badge = mk('span', 'mby-mech-badge', String(idx + 1));
        const txt = mk('div', 'mby-mech-slot-text'); txt.innerHTML = s.html; renderMathIn(txt);
        const upBtn = mk('button', 'mby-mech-ctrl-btn', '↑'); upBtn.type = 'button'; upBtn.title = 'Move step up'; upBtn.disabled = idx === 0;
        upBtn.addEventListener('click', () => {
          if (idx <= 0) return;
          animateSeqUpdate(() => {
            const tmp = st.placedIds[idx - 1]; st.placedIds[idx - 1] = st.placedIds[idx]; st.placedIds[idx] = tmp;
          });
        });
        const dnBtn = mk('button', 'mby-mech-ctrl-btn', '↓'); dnBtn.type = 'button'; dnBtn.title = 'Move step down'; dnBtn.disabled = idx === st.placedIds.length - 1;
        dnBtn.addEventListener('click', () => {
          if (idx >= st.placedIds.length - 1) return;
          animateSeqUpdate(() => {
            const tmp = st.placedIds[idx + 1]; st.placedIds[idx + 1] = st.placedIds[idx]; st.placedIds[idx] = tmp;
          });
        });
        const rmBtn = mk('button', 'mby-mech-ctrl-btn', '✕'); rmBtn.type = 'button'; rmBtn.title = 'Return step to bank';
        rmBtn.addEventListener('click', () => {
          animateSeqUpdate(() => {
            st.placedIds = st.placedIds.filter(x => x !== sid);
            st.bankIds.push(sid);
          });
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

  const retryBtn = createRetryBtn(() => {
    const reshuffled = deterministicShuffle(mech.steps || [], `${slide.id}:mech`, s => String(s.stepId));
    st.bankIds = reshuffled.map(s => s.stepId);
    st.placedIds = [];
    st.checked = false;
    st.isCorrect = null;
    canonicalBox.innerHTML = '';
    canonicalBox.classList.add('hidden');
    feedback.textContent = '';
    if (explanationBox) explanationBox.classList.add('hidden');
    retryBtn.classList.add('hidden');
    renderZones();
    if (m) m.isCorrect = null;
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  }, st.checked);

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
    recordGradeAttempt(m, allOk);
    setFeedbackMessage(feedback, allOk ? 'Sequence correct!' : 'Review highlighted steps.', allOk);
    retryBtn.classList.remove('hidden');
    if (explanationBox) explanationBox.classList.remove('hidden');
    if (onAnswered) onAnswered();
    requestAnimationFrame(updateScrollDownHint);
  });

  card.appendChild(mk('div', 'mby-card-body', null, [layout, mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]), explanationBox]));
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

  const explanationBox = createExplanationBox(rel.explanationHtml, st.checked);

  const syncBoxVisuals = () => {
    boxes.forEach(b => {
      const btn = boxBtnMap.get(b.index);
      if (!btn) return;
      btn.classList.toggle('active', mode !== 'reveal' && st.activeBoxIdx === b.index);
      if (mode === 'reveal') btn.classList.toggle('revealed', st.revealed.has(b.index));
    });
  };

  let renderBankUi = null;
  let assignChipToBox = null;
  let actionRow = null;

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
    if (mode === 'bank') {
      bindDropTarget(btn, 'application/x-mby-relabel-chip', e => {
        const raw = e.dataTransfer?.getData('application/x-mby-relabel-chip');
        if (raw !== undefined && raw !== '' && assignChipToBox) {
          const cid = Number(raw);
          if (!Number.isNaN(cid)) assignChipToBox(cid, b.index);
        }
      });
    }
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

  if (mode === 'reveal') {
    const knownBtn = mk('button', 'mby-rate-btn' + (st.rating === 'known' ? ' active-known' : ''), 'Got It'); knownBtn.type = 'button';
    const reviewBtn = mk('button', 'mby-rate-btn' + (st.rating === 'needsReview' ? ' active-review' : ''), 'Needs Review'); reviewBtn.type = 'button';
    const feedback = mk('span');
    const retryBtn = createRetryBtn(() => {
      st.rating = null; st.checked = false; st.isCorrect = null;
      st.revealed.clear();
      knownBtn.classList.remove('active-known');
      reviewBtn.classList.remove('active-review');
      boxBtnMap.forEach(btn => btn.classList.remove('revealed', 'correct', 'wrong'));
      syncBoxVisuals();
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      if (m) { m.isCorrect = null; m.flashcardRating = null; }
      if (onAnswered) onAnswered();
      requestAnimationFrame(updateScrollDownHint);
    }, !!st.rating);
    const rate = r => {
      const ok = r === 'known';
      st.rating = r; st.checked = true; st.isCorrect = ok;
      unlockZoom();
      knownBtn.classList.toggle('active-known', ok);
      reviewBtn.classList.toggle('active-review', !ok);
      recordGradeAttempt(m, ok);
      if (m) m.flashcardRating = r;
      setFeedbackMessage(feedback, ok ? 'Marked as known!' : 'Flagged for review.', ok);
      retryBtn.classList.remove('hidden');
      if (explanationBox) explanationBox.classList.remove('hidden');
      if (onAnswered) onAnswered();
      requestAnimationFrame(updateScrollDownHint);
    };
    knownBtn.addEventListener('click', () => rate('known'));
    reviewBtn.addEventListener('click', () => rate('needsReview'));
    actionRow = mk('div', 'mby-action-row', null, [knownBtn, reviewBtn, retryBtn, feedback]);
  } else if (mode === 'bank') {
    assignListEl = mk('div', 'mby-relabel-assignments');
    bankEl = mk('div', 'mby-relabel-bank');
    const chipById = new Map(st.bankPool.map(it => [it.chipId, it]));
    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
    const feedback = mk('span');

    assignChipToBox = (chipId, targetBoxIdx) => {
      const destIdx = targetBoxIdx ?? st.activeBoxIdx ?? boxes[0]?.index ?? null;
      if (destIdx === null) return;
      for (const [k, v] of Object.entries(st.assignedChipByBox)) {
        if (v === chipId) delete st.assignedChipByBox[k];
      }
      st.assignedChipByBox[destIdx] = chipId;
      st.checked = false;
      st.isCorrect = null;
      boxBtnMap.forEach(btn => btn.classList.remove('correct', 'wrong'));
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      const nextEmpty = boxes.find(b => st.assignedChipByBox[b.index] === undefined);
      if (nextEmpty) st.activeBoxIdx = nextEmpty.index;
      syncBoxVisuals();
      renderBankUi();
    };

    const retryBtn = createRetryBtn(() => {
      st.assignedChipByBox = {};
      st.activeBoxIdx = boxes[0]?.index ?? null;
      st.checked = false;
      st.isCorrect = null;
      boxBtnMap.forEach(btn => btn.classList.remove('correct', 'wrong', 'revealed'));
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      syncBoxVisuals();
      renderBankUi();
      if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
      requestAnimationFrame(updateScrollDownHint);
    }, st.checked);

    renderBankUi = () => {
      assignListEl.innerHTML = '';
      boxes.forEach(b => {
        const assignedId = st.assignedChipByBox[b.index];
        const assignedItem = assignedId !== undefined ? chipById.get(assignedId) : null;
        const row = mk('div', 'mby-relabel-assign-row' + (st.activeBoxIdx === b.index ? ' active' : ''));
        row.appendChild(mk('span', 'mby-mech-badge', String(b.index)));
        row.appendChild(mk('span', null, assignedItem ? assignedItem.label : '(Click or drag a label below)'));
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
        bindDropTarget(row, 'application/x-mby-relabel-chip', e => {
          const raw = e.dataTransfer?.getData('application/x-mby-relabel-chip');
          if (raw !== undefined && raw !== '') {
            const cid = Number(raw);
            if (!Number.isNaN(cid)) assignChipToBox(cid, b.index);
          }
        });
        assignListEl.appendChild(row);
      });

      const usedIds = new Set(Object.values(st.assignedChipByBox).filter(v => v !== undefined && v !== null));
      bankEl.innerHTML = '';
      st.bankPool.forEach(item => {
        const chip = mk('button', 'mby-relabel-bank-chip mby-draggable' + (usedIds.has(item.chipId) ? ' used' : ''), item.label);
        chip.type = 'button';
        chip.draggable = true;
        chip.addEventListener('dragstart', e => {
          if (e.dataTransfer) {
            e.dataTransfer.setData('application/x-mby-relabel-chip', String(item.chipId));
            e.dataTransfer.effectAllowed = 'copyMove';
          }
          chip.classList.add('is-dragging');
        });
        chip.addEventListener('dragend', () => chip.classList.remove('is-dragging'));
        chip.addEventListener('click', () => assignChipToBox(item.chipId, null));
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
      recordGradeAttempt(m, allOk);
      setFeedbackMessage(feedback, allOk ? 'All labels correct!' : 'Review highlighted boxes.', allOk);
      retryBtn.classList.remove('hidden');
      if (explanationBox) explanationBox.classList.remove('hidden');
      if (onAnswered) onAnswered();
      requestAnimationFrame(updateScrollDownHint);
    });

    renderBankUi();
    layout.appendChild(assignListEl);
    layout.appendChild(bankEl);
    actionRow = mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]);
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

    const retryBtn = createRetryBtn(() => {
      st.recallValues = {};
      st.activeBoxIdx = boxes[0]?.index ?? null;
      st.checked = false;
      st.isCorrect = null;
      boxes.forEach(b => {
        const inp = recallInputMap.get(b.index), expEl = recallExpectedMap.get(b.index), btn = boxBtnMap.get(b.index);
        if (inp) { inp.value = ''; inp.classList.remove('correct', 'fuzzy-ok', 'wrong'); }
        if (expEl) { expEl.textContent = ''; expEl.classList.add('hidden'); }
        btn?.classList.remove('correct', 'wrong', 'revealed');
      });
      syncBoxVisuals();
      overrideBtn.classList.add('hidden');
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
      requestAnimationFrame(updateScrollDownHint);
    }, st.checked);

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
      recordGradeAttempt(m, allOk);
      setFeedbackMessage(feedback, allOk ? (anyFuzzy ? 'Almost Correct!' : 'All labels correct!') : 'Review highlighted labels.', allOk);
      overrideBtn.classList.toggle('hidden', allOk);
      retryBtn.classList.remove('hidden');
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
      retryBtn.classList.remove('hidden');
      if (onAnswered) onAnswered();
    });

    layout.appendChild(recallPanel);
    actionRow = mk('div', 'mby-action-row', null, [checkBtn, overrideBtn, retryBtn, feedback]);
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
    const resetBtn = createRetryBtn(() => {
      fcState.rating = null;
      if (slideMetric) slideMetric.flashcardRating = null;
      knownBtn.classList.remove('active-known');
      reviewBtn.classList.remove('active-review');
      resetBtn.classList.add('hidden');
      if (onRated) onRated();
    }, !!fcState.rating, 'Reset', true);
    const rate = r => {
      fcState.rating = r; if (slideMetric) slideMetric.flashcardRating = r;
      knownBtn.classList.toggle('active-known', r === 'known');
      reviewBtn.classList.toggle('active-review', r === 'needsReview');
      resetBtn.classList.remove('hidden');
      if (onRated) onRated();
    };
    knownBtn.addEventListener('click', e => { e.stopPropagation(); rate('known'); });
    reviewBtn.addEventListener('click', e => { e.stopPropagation(); rate('needsReview'); });
    resetBtn.addEventListener('click', e => e.stopPropagation());
    body.appendChild(mk('div', 'mby-rating-row', null, [knownBtn, reviewBtn, resetBtn]));
  }

  return mk('div', 'mby-flashcard-face ' + (faceLabel === 'Back' ? 'mby-flashcard-back' : ''), null,
    [createUpperTab('FLASHCARD'), body, mk('div', 'mby-flashcard-side-label', faceLabel)]);
}

function buildDiagramExploreBlock(b) {
  const diag = b.diagramExplore;
  const wrap = mk('div', 'mby-diagram-wrap');
  wrap.dataset.blockId = b.id;
  if (!diag) return wrap;

  const hotspots = diag.hotspots || [];
  let activeIdx = 0;
  let showAll = false;

  const head = mk('div', 'mby-diagram-head');
  if (diag.titleHtml || diag.title) {
    const tEl = mk('div', 'mby-diagram-title');
    if (diag.titleHtml) { tEl.innerHTML = diag.titleHtml; renderMathIn(tEl); }
    else tEl.textContent = diag.title;
    head.appendChild(tEl);
  }
  const nav = mk('div', 'mby-diagram-nav');
  const prevSpotBtn = mk('button', 'mby-retry-btn is-compact', 'Prev'); prevSpotBtn.type = 'button';
  const nextSpotBtn = mk('button', 'mby-retry-btn is-compact', 'Next'); nextSpotBtn.type = 'button';
  const showAllBtn = mk('button', 'mby-retry-btn is-compact', 'Show All'); showAllBtn.type = 'button';
  const zoomBtn = mk('button', 'mby-retry-btn is-compact', 'Zoom'); zoomBtn.type = 'button';
  zoomBtn.addEventListener('click', () => openZoomLightbox(diag.imagePath, diag.alt || diag.title || 'Diagram'));
  nav.appendChild(prevSpotBtn); nav.appendChild(nextSpotBtn); nav.appendChild(showAllBtn); nav.appendChild(zoomBtn);
  head.appendChild(nav);
  wrap.appendChild(head);

  const stage = mk('div', 'mby-diagram-stage');
  const img = mk('img', 'mby-diagram-img');
  img.src = diag.imagePath;
  if (diag.alt) img.alt = diag.alt;
  stage.appendChild(img);

  const spotBtns = hotspots.map((hs, idx) => {
    const pin = mk('button', 'mby-diagram-hotspot');
    pin.type = 'button';
    pin.title = hs.label;
    pin.style.left = (hs.x * 100).toFixed(4) + '%';
    pin.style.top = (hs.y * 100).toFixed(4) + '%';
    pin.style.width = (hs.w * 100).toFixed(4) + '%';
    pin.style.height = (hs.h * 100).toFixed(4) + '%';
    pin.appendChild(mk('span', 'mby-diagram-pin', String(idx + 1)));
    pin.addEventListener('click', () => { activeIdx = idx; showAll = false; syncView(); });
    stage.appendChild(pin);
    return pin;
  });
  wrap.appendChild(stage);

  if (diag.captionHtml) {
    const cap = mk('div', 'mby-figcaption');
    cap.innerHTML = diag.captionHtml;
    renderMathIn(cap);
    wrap.appendChild(cap);
  }

  const cardHost = mk('div');
  wrap.appendChild(cardHost);

  function syncView() {
    spotBtns.forEach((btn, idx) => btn.classList.toggle('active', !showAll && idx === activeIdx));
    showAllBtn.textContent = showAll ? 'Active Only' : 'Show All';
    cardHost.innerHTML = '';
    if (hotspots.length === 0) return;

    if (showAll) {
      const list = mk('div', 'mby-diagram-all-list');
      hotspots.forEach((hs, idx) => {
        const c = mk('div', 'mby-diagram-card');
        c.appendChild(mk('div', 'mby-diagram-card-title', `${idx + 1}. ${hs.label}`));
        const body = mk('div', 'mby-callout-body');
        body.innerHTML = hs.noteHtml || '';
        renderMathIn(body);
        c.appendChild(body);
        list.appendChild(c);
      });
      cardHost.appendChild(list);
    } else {
      const hs = hotspots[Math.max(0, Math.min(activeIdx, hotspots.length - 1))];
      const c = mk('div', 'mby-diagram-card');
      c.appendChild(mk('div', 'mby-diagram-card-title', `${activeIdx + 1} / ${hotspots.length} • ${hs.label}`));
      const body = mk('div', 'mby-callout-body');
      body.innerHTML = hs.noteHtml || '';
      renderMathIn(body);
      c.appendChild(body);
      cardHost.appendChild(c);
    }
    requestAnimationFrame(updateScrollDownHint);
  }

  prevSpotBtn.addEventListener('click', () => { if (hotspots.length) { showAll = false; activeIdx = (activeIdx - 1 + hotspots.length) % hotspots.length; syncView(); } });
  nextSpotBtn.addEventListener('click', () => { if (hotspots.length) { showAll = false; activeIdx = (activeIdx + 1) % hotspots.length; syncView(); } });
  showAllBtn.addEventListener('click', () => { showAll = !showAll; syncView(); });
  syncView();
  return wrap;
}

function buildRevealBlock(b) {
  const rev = b.reveal;
  const wrap = mk('div', 'mby-reveal-block');
  wrap.dataset.blockId = b.id;
  if (!rev) return wrap;

  const steps = rev.steps || [];
  let unlocked = rev.showFirst ? Math.min(1, steps.length) : 0;

  const header = mk('div', 'mby-reveal-header');
  const titleEl = mk('div', 'mby-reveal-title');
  if (rev.titleHtml) { titleEl.innerHTML = rev.titleHtml; renderMathIn(titleEl); }
  else titleEl.textContent = rev.title || 'Step-by-Step Reveal';
  header.appendChild(titleEl);

  const actions = mk('div', 'mby-reveal-actions');
  const revAllBtn = mk('button', 'mby-retry-btn is-compact', 'Reveal All'); revAllBtn.type = 'button';
  const resetBtn = mk('button', 'mby-retry-btn is-compact', 'Reset'); resetBtn.type = 'button';
  actions.appendChild(revAllBtn); actions.appendChild(resetBtn);
  header.appendChild(actions);
  wrap.appendChild(header);

  const listEl = mk('div', 'mby-reveal-steps');
  wrap.appendChild(listEl);

  function renderSteps() {
    listEl.innerHTML = '';
    steps.forEach((st, idx) => {
      const isUnlocked = idx < unlocked;
      const isNext = idx === unlocked;
      const row = mk('div', 'mby-reveal-step ' + (isUnlocked ? 'is-unlocked' : (isNext ? 'is-next' : 'is-locked')));
      const sHead = mk('div', 'mby-reveal-step-head');
      const titleSpan = mk('span');
      titleSpan.innerHTML = st.headingHtml || `Step ${idx + 1}`;
      renderMathIn(titleSpan);
      sHead.appendChild(titleSpan);
      sHead.appendChild(mk('span', 'mby-step-counter', `${idx + 1}/${steps.length}`));
      row.appendChild(sHead);

      if (isUnlocked) {
        const sBody = mk('div', 'mby-reveal-step-body');
        sBody.innerHTML = st.bodyHtml || '';
        renderMathIn(sBody);
        row.appendChild(sBody);
      } else if (isNext) {
        const unlockBtn = mk('button', 'mby-reveal-unlock-btn', `Reveal Step ${idx + 1}`);
        unlockBtn.type = 'button';
        unlockBtn.addEventListener('click', () => { unlocked = Math.min(steps.length, unlocked + 1); renderSteps(); });
        row.appendChild(unlockBtn);
      }
      listEl.appendChild(row);
    });
    requestAnimationFrame(updateScrollDownHint);
  }

  revAllBtn.addEventListener('click', () => { unlocked = steps.length; renderSteps(); });
  resetBtn.addEventListener('click', () => { unlocked = rev.showFirst ? Math.min(1, steps.length) : 0; renderSteps(); });
  renderSteps();
  return wrap;
}

function buildClassifyCard(slide, onAnswered) {
  const cl = slide.classify;
  const card = mk('div', 'mby-card', null, [createUpperTab('CLASSIFICATION')]); applySlideLayoutVars(card, slide.layout);
  const st = gradedStateMap.get(slide.id) || {
    placements: {},
    selectedItemId: null,
    checked: false,
    isCorrect: null,
    order: cl.items.map(it => it.itemId).sort(() => Math.random() - 0.5)
  };
  gradedStateMap.set(slide.id, st);

  const itemsById = new Map(cl.items.map(it => [it.itemId, it]));
  const bucketNameById = new Map(cl.buckets.map(b => [b.bucketId, b.name]));

  const body = mk('div', 'mby-card-body');
  if (cl.titleHtml || cl.title) {
    const titleEl = mk('div', 'mby-diff-title');
    if (cl.titleHtml) { titleEl.innerHTML = cl.titleHtml; renderMathIn(titleEl); }
    else titleEl.textContent = cl.title;
    body.appendChild(titleEl);
  }

  const poolEl = mk('div', 'mby-classify-pool');
  const bucketsEl = mk('div', 'mby-classify-buckets');
  body.appendChild(poolEl);
  body.appendChild(bucketsEl);

  const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
  const feedback = mk('span');
  const explanationBox = createExplanationBox(cl.explanationHtml, st.checked);

  function assignItem(itemId, bucketId) {
    if (bucketId === null || bucketId === undefined) delete st.placements[itemId];
    else st.placements[itemId] = bucketId;
    st.selectedItemId = null;
    st.checked = false;
    st.isCorrect = null;
    feedback.textContent = '';
    if (explanationBox) explanationBox.classList.add('hidden');
    renderClassifyUi();
  }

  const retryBtn = createRetryBtn(() => {
    st.placements = {};
    st.selectedItemId = null;
    st.checked = false;
    st.isCorrect = null;
    feedback.textContent = '';
    if (explanationBox) explanationBox.classList.add('hidden');
    retryBtn.classList.add('hidden');
    renderClassifyUi();
    const m = metricById.get(slide.id); if (m) m.isCorrect = null;
    if (onAnswered) onAnswered();
  }, st.checked);

  function createChipNode(it) {
    const chip = mk('button', 'mby-classify-chip' + (st.selectedItemId === it.itemId ? ' selected' : ''));
    chip.type = 'button';
    chip.draggable = true;
    const span = mk('span');
    span.innerHTML = it.html;
    renderMathIn(span);
    chip.appendChild(span);
    if (st.checked) {
      const placed = st.placements[it.itemId];
      const ok = placed === it.bucketId;
      chip.classList.add(ok ? 'correct' : 'wrong');
      if (!ok) chip.appendChild(mk('span', 'mby-classify-fix', '→ ' + (bucketNameById.get(it.bucketId) || '')));
    }
    chip.addEventListener('dragstart', e => {
      if (e.dataTransfer) { e.dataTransfer.setData('application/x-mby-classify-item', String(it.itemId)); e.dataTransfer.effectAllowed = 'move'; }
    });
    chip.addEventListener('click', e => {
      e.stopPropagation();
      st.selectedItemId = st.selectedItemId === it.itemId ? null : it.itemId;
      renderClassifyUi();
    });
    return chip;
  }

  bindDropTarget(poolEl, 'application/x-mby-classify-item', e => {
    const raw = e.dataTransfer?.getData('application/x-mby-classify-item');
    if (raw !== undefined && raw !== '') assignItem(Number(raw), null);
  });
  poolEl.addEventListener('click', () => { if (st.selectedItemId !== null) assignItem(st.selectedItemId, null); });

  function renderClassifyUi() {
    poolEl.innerHTML = '';
    const unplaced = st.order.filter(id => st.placements[id] === undefined).map(id => itemsById.get(id)).filter(Boolean);
    if (unplaced.length === 0) poolEl.appendChild(mk('span', 'mby-diff-subnote', 'All items sorted — click a chip or bucket to adjust.'));
    else unplaced.forEach(it => poolEl.appendChild(createChipNode(it)));

    bucketsEl.innerHTML = '';
    cl.buckets.forEach(b => {
      const box = mk('div', 'mby-classify-bucket' + (st.selectedItemId !== null ? ' target-ready' : ''));
      const bTitle = mk('div', 'mby-classify-bucket-title');
      bTitle.innerHTML = b.nameHtml || b.name;
      renderMathIn(bTitle);
      box.appendChild(bTitle);

      const itemsRow = mk('div', 'mby-classify-bucket-items');
      st.order.filter(id => st.placements[id] === b.bucketId).map(id => itemsById.get(id)).filter(Boolean).forEach(it => itemsRow.appendChild(createChipNode(it)));
      box.appendChild(itemsRow);

      bindDropTarget(box, 'application/x-mby-classify-item', e => {
        const raw = e.dataTransfer?.getData('application/x-mby-classify-item');
        if (raw !== undefined && raw !== '') assignItem(Number(raw), b.bucketId);
      });
      box.addEventListener('click', () => { if (st.selectedItemId !== null) assignItem(st.selectedItemId, b.bucketId); });
      bucketsEl.appendChild(box);
    });
    retryBtn.classList.toggle('hidden', !st.checked);
  }

  checkBtn.addEventListener('click', () => {
    const placedCount = cl.items.filter(it => st.placements[it.itemId] !== undefined).length;
    const ok = placedCount === cl.items.length && cl.items.every(it => st.placements[it.itemId] === it.bucketId);
    st.checked = true;
    st.isCorrect = ok;
    recordGradeAttempt(metricById.get(slide.id), ok);
    setFeedbackMessage(feedback, ok ? `Correct! All ${cl.items.length} items sorted.` : `${cl.items.filter(it => st.placements[it.itemId] === it.bucketId).length} / ${cl.items.length} correct.`, ok);
    if (explanationBox) explanationBox.classList.remove('hidden');
    renderClassifyUi();
    if (onAnswered) onAnswered();
  });

  renderClassifyUi();
  const actionRow = mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]);
  body.appendChild(actionRow);
  if (explanationBox) body.appendChild(explanationBox);
  card.appendChild(body);
  return card;
}

function buildMatchCard(slide, onAnswered) {
  const mt = slide.match;
  const card = mk('div', 'mby-card', null, [createUpperTab('MATCHING')]); applySlideLayoutVars(card, slide.layout);
  const st = gradedStateMap.get(slide.id) || {
    assignments: {},
    selectedOptId: null,
    checked: false,
    isCorrect: null,
    poolOrder: mt.rightPool.map(o => o.optionId).sort(() => Math.random() - 0.5)
  };
  gradedStateMap.set(slide.id, st);

  const optById = new Map(mt.rightPool.map(o => [o.optionId, o]));
  const body = mk('div', 'mby-card-body');
  if (mt.titleHtml || mt.title) {
    const tEl = mk('div', 'mby-diff-title');
    if (mt.titleHtml) { tEl.innerHTML = mt.titleHtml; renderMathIn(tEl); }
    else tEl.textContent = mt.title;
    body.appendChild(tEl);
  }

  const wrap = mk('div', 'mby-match-wrap');
  const rowsEl = mk('div', 'mby-match-rows');
  const bankEl = mk('div', 'mby-match-bank');
  wrap.appendChild(rowsEl);
  wrap.appendChild(bankEl);
  body.appendChild(wrap);

  const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
  const feedback = mk('span');
  const explanationBox = createExplanationBox(mt.explanationHtml, st.checked);

  function assignOption(pairId, optId) {
    if (optId !== null && optId !== undefined) {
      for (const [k, v] of Object.entries(st.assignments)) {
        if (v === optId) delete st.assignments[k];
      }
      st.assignments[pairId] = optId;
    } else {
      delete st.assignments[pairId];
    }
    st.selectedOptId = null;
    st.checked = false;
    st.isCorrect = null;
    feedback.textContent = '';
    if (explanationBox) explanationBox.classList.add('hidden');
    renderMatchUi();
  }

  const retryBtn = createRetryBtn(() => {
    st.assignments = {};
    st.selectedOptId = null;
    st.checked = false;
    st.isCorrect = null;
    feedback.textContent = '';
    if (explanationBox) explanationBox.classList.add('hidden');
    retryBtn.classList.add('hidden');
    renderMatchUi();
    const m = metricById.get(slide.id); if (m) m.isCorrect = null;
    if (onAnswered) onAnswered();
  }, st.checked);

  function renderMatchUi() {
    rowsEl.innerHTML = '';
    mt.pairs.forEach(pair => {
      const row = mk('div', 'mby-match-row');
      const left = mk('div', 'mby-match-left');
      left.innerHTML = pair.leftHtml;
      renderMathIn(left);
      row.appendChild(left);

      const optId = st.assignments[pair.pairId];
      const assigned = optId !== undefined ? optById.get(optId) : null;
      const slot = mk('button', 'mby-match-slot' + (assigned ? ' filled' : '') + (st.selectedOptId !== null ? ' target-ready' : ''));
      slot.type = 'button';
      if (assigned) {
        const val = mk('span'); val.innerHTML = assigned.html; renderMathIn(val);
        slot.appendChild(val);
        const clr = mk('span', 'mby-diff-subnote', '✕');
        clr.addEventListener('click', e => { e.stopPropagation(); assignOption(pair.pairId, null); });
        slot.appendChild(clr);
      } else {
        slot.appendChild(mk('span', 'mby-diff-subnote', '(Click or drop match)'));
      }

      if (st.checked) {
        const ok = assigned && assigned.correctPairId === pair.pairId;
        slot.classList.add(ok ? 'correct' : 'wrong');
        if (!ok) {
          const expSpan = mk('span', 'mby-diff-fix-badge');
          expSpan.innerHTML = '→ ' + pair.rightHtml;
          renderMathIn(expSpan);
          slot.appendChild(expSpan);
        }
      }

      bindDropTarget(slot, 'application/x-mby-match-opt', e => {
        const raw = e.dataTransfer?.getData('application/x-mby-match-opt');
        if (raw !== undefined && raw !== '') assignOption(pair.pairId, Number(raw));
      });
      slot.addEventListener('click', () => {
        if (st.selectedOptId !== null) assignOption(pair.pairId, st.selectedOptId);
        else if (assigned) assignOption(pair.pairId, null);
      });
      row.appendChild(slot);
      rowsEl.appendChild(row);
    });

    const usedOptIds = new Set(Object.values(st.assignments).filter(v => v !== undefined && v !== null));
    bankEl.innerHTML = '';
    st.poolOrder.map(id => optById.get(id)).filter(Boolean).forEach(opt => {
      const chip = mk('button', 'mby-match-chip' + (usedOptIds.has(opt.optionId) ? ' used' : '') + (st.selectedOptId === opt.optionId ? ' selected' : ''));
      chip.type = 'button';
      chip.draggable = true;
      chip.innerHTML = opt.html;
      renderMathIn(chip);
      chip.addEventListener('dragstart', e => {
        if (e.dataTransfer) { e.dataTransfer.setData('application/x-mby-match-opt', String(opt.optionId)); e.dataTransfer.effectAllowed = 'move'; }
      });
      chip.addEventListener('click', () => {
        st.selectedOptId = st.selectedOptId === opt.optionId ? null : opt.optionId;
        renderMatchUi();
      });
      bankEl.appendChild(chip);
    });
    retryBtn.classList.toggle('hidden', !st.checked);
  }

  checkBtn.addEventListener('click', () => {
    const okCount = mt.pairs.filter(p => {
      const oid = st.assignments[p.pairId];
      return oid !== undefined && optById.get(oid)?.correctPairId === p.pairId;
    }).length;
    const ok = okCount === mt.pairs.length;
    st.checked = true;
    st.isCorrect = ok;
    recordGradeAttempt(metricById.get(slide.id), ok);
    setFeedbackMessage(feedback, ok ? 'Correct!' : `${okCount} / ${mt.pairs.length} pairs correct.`, ok);
    if (explanationBox) explanationBox.classList.remove('hidden');
    renderMatchUi();
    if (onAnswered) onAnswered();
  });

  renderMatchUi();
  body.appendChild(mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]));
  if (explanationBox) body.appendChild(explanationBox);
  card.appendChild(body);
  return card;
}

function buildNumericCard(slide, onAnswered) {
  const num = slide.numeric;
  const card = mk('div', 'mby-card', null, [createUpperTab('NUMERIC')]); applySlideLayoutVars(card, slide.layout);
  const st = gradedStateMap.get(slide.id) || { value: '', checked: false, isCorrect: null };
  gradedStateMap.set(slide.id, st);

  const body = mk('div', 'mby-card-body');
  if (num.imagePath) body.appendChild(makeZoomImg(num.imagePath, 'Question diagram'));
  const promptEl = mk('div', 'mby-prompt');
  promptEl.innerHTML = num.promptHtml || '';
  renderMathIn(promptEl);
  body.appendChild(promptEl);

  const input = mk('input', 'mby-numeric-input');
  input.type = 'text';
  input.inputMode = 'decimal';
  input.placeholder = num.placeholder || 'Enter numeric value...';
  input.value = st.value || '';

  const rowChildren = [input];
  if (num.unit) rowChildren.push(mk('span', 'mby-numeric-unit', num.unit));
  const rangeEl = mk('span', 'mby-numeric-range');
  rowChildren.push(rangeEl);
  body.appendChild(mk('div', 'mby-numeric-row', null, rowChildren));

  const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
  const feedback = mk('span');
  const explanationBox = createExplanationBox(num.explanationHtml, st.checked);

  const tolRadius = num.toleranceMode === 'percent' ? Math.abs(num.answer * (num.tolerance / 100)) : Math.abs(num.tolerance);

  function syncVisual() {
    input.classList.remove('correct', 'wrong');
    rangeEl.textContent = '';
    if (st.checked) {
      input.classList.add(st.isCorrect ? 'correct' : 'wrong');
      const unitTxt = num.unit ? ` ${num.unit}` : '';
      if (tolRadius > 0) rangeEl.textContent = `Accepted: ${(num.answer - tolRadius).toPrecision(4)} .. ${(num.answer + tolRadius).toPrecision(4)}${unitTxt}`;
      else rangeEl.textContent = `Exact answer: ${num.answer}${unitTxt}`;
      setFeedbackMessage(feedback, st.isCorrect ? 'Correct!' : 'Out of accepted range.', st.isCorrect);
      if (explanationBox) explanationBox.classList.remove('hidden');
    } else {
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
    }
    retryBtn.classList.toggle('hidden', !st.checked);
  }

  const retryBtn = createRetryBtn(() => {
    st.value = ''; st.checked = false; st.isCorrect = null;
    input.value = '';
    syncVisual();
    const m = metricById.get(slide.id); if (m) m.isCorrect = null;
    if (onAnswered) onAnswered();
  }, st.checked);

  input.addEventListener('input', () => {
    st.value = input.value;
    if (st.checked) { st.checked = false; st.isCorrect = null; syncVisual(); }
  });
  function gradeNumeric() {
    const cleaned = (st.value || '').trim().replace(/,/g, '');
    const parsed = cleaned === '' ? NaN : Number(cleaned);
    const ok = Number.isFinite(parsed) && Math.abs(parsed - num.answer) <= tolRadius + 1e-9;
    st.checked = true;
    st.isCorrect = ok;
    recordGradeAttempt(metricById.get(slide.id), ok);
    syncVisual();
    if (onAnswered) onAnswered();
  }
  input.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); gradeNumeric(); } });
  checkBtn.addEventListener('click', gradeNumeric);

  syncVisual();
  body.appendChild(mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]));
  if (explanationBox) body.appendChild(explanationBox);
  card.appendChild(body);
  return card;
}

function renderSingleQuestionWidget(q, onChanged, onFlipStart, onFlipEnd) {
  if (q.kind === 'mcq') return buildMcqCard(q, onChanged);
  if (q.kind === 'flashcard') return buildFlashcardPanel(q, onChanged, onFlipStart, onFlipEnd);
  if (q.kind === 'fill-blank') return buildFillBlankCard(q, onChanged);
  if (q.kind === 'assertion-reason') return buildAssertionReasonCard(q, onChanged);
  if (q.kind === 'spot-error') return buildSpotErrorCard(q, onChanged);
  if (q.kind === 'diff') return buildDiffCard(q, onChanged);
  if (q.kind === 'mechanism') return buildMechanismCard(q, onChanged);
  if (q.kind === 'relabel') return buildRelabelCard(q, onChanged);
  if (q.kind === 'classify') return buildClassifyCard(q, onChanged);
  if (q.kind === 'match') return buildMatchCard(q, onChanged);
  if (q.kind === 'numeric') return buildNumericCard(q, onChanged);
  if (q.kind === 'case') return buildCaseGroupCard(q, onChanged);
  return mk('div', 'mby-card', 'Unsupported question type: ' + q.kind);
}

function buildCaseGroupCard(slide, onAnswered) {
  const cs = slide.caseGroup;
  const subQuestions = cs.questions || [];
  const card = mk('div', 'mby-card', null, [createUpperTab(cs.title ? 'CASE STUDY • ' + cs.title : 'CASE STUDY')]);
  applySlideLayoutVars(card, slide.layout);

  const st = gradedStateMap.get(slide.id) || { activePartIdx: 0, checked: false, isCorrect: null };
  gradedStateMap.set(slide.id, st);

  subQuestions.forEach((sq, idx) => {
    if (!metricById.has(sq.id)) {
      metricById.set(sq.id, {
        slideIndex: idx + 1, id: sq.id, kind: sq.kind, timeSpentMs: 0, visitedCount: 0,
        checkAttempts: isGradedKind(sq.kind) ? 0 : null,
        selectedOptionIndices: sq.kind === 'mcq' ? [] : null,
        correctOptionIndices: sq.kind === 'mcq' ? (sq.mcq?.options || []).filter(o => o.isCorrect).map(o => o.index) : null,
        firstTryCorrect: null, isCorrect: null, flipCount: sq.kind === 'flashcard' ? 0 : null, flashcardRating: null
      });
    }
  });

  const body = mk('div', 'mby-card-body');
  const useSplit = !!cs.imagePath || stripHtmlToText(cs.stemHtml).length > 160;
  const layout = mk('div', 'mby-case-layout' + (useSplit ? ' has-split' : ''));

  const stemBox = mk('div', 'mby-case-stem');
  const stemHeader = mk('div', 'mby-link-popover-head');
  stemHeader.appendChild(mk('span', 'mby-case-stem-badge', 'Case Stem'));
  const scoreBadge = mk('span', 'mby-step-counter');
  stemHeader.appendChild(scoreBadge);
  stemBox.appendChild(stemHeader);

  const stemContent = mk('div');
  stemContent.innerHTML = cs.stemHtml || '';
  renderMathIn(stemContent);
  stemBox.appendChild(stemContent);

  if (cs.imagePath) {
    const imgWrap = makeZoomImg(cs.imagePath, cs.title || 'Case figure');
    stemBox.appendChild(imgWrap);
    if (cs.captionHtml) {
      const capEl = mk('div', 'mby-figcaption');
      capEl.innerHTML = cs.captionHtml;
      renderMathIn(capEl);
      stemBox.appendChild(capEl);
    }
  }
  layout.appendChild(stemBox);

  const mainCol = mk('div', 'mby-case-main');
  const tabsRow = mk('div', 'mby-case-tabs');
  const subHost = mk('div', 'mby-case-subhost');
  const explanationBox = createExplanationBox(cs.explanationHtml);
  mainCol.appendChild(tabsRow);
  mainCol.appendChild(subHost);
  layout.appendChild(mainCol);
  body.appendChild(layout);
  if (explanationBox) body.appendChild(explanationBox);
  card.appendChild(body);

  function recomputeAggregate() {
    const gradedSubs = subQuestions.filter(sq => isGradedKind(sq.kind));
    const gradedMetrics = gradedSubs.map(sq => metricById.get(sq.id));
    const correctCount = gradedMetrics.filter(sm => sm?.isCorrect === true).length;
    const attemptsSum = gradedMetrics.reduce((acc, sm) => acc + (sm?.checkAttempts || 0), 0);
    const allChecked = gradedMetrics.length > 0 && gradedMetrics.every(sm => (sm?.checkAttempts || 0) > 0);
    const allCorrect = allChecked && gradedMetrics.every(sm => sm?.isCorrect === true);
    const allFirstTry = allChecked && gradedMetrics.every(sm => sm?.firstTryCorrect === true);

    scoreBadge.textContent = gradedSubs.length > 0
      ? `${correctCount} / ${gradedSubs.length} parts correct`
      : `${subQuestions.length} parts`;

    st.checked = allChecked;
    st.isCorrect = allChecked ? allCorrect : null;
    const parentMetric = metricById.get(slide.id);
    if (parentMetric) {
      parentMetric.checkAttempts = attemptsSum;
      parentMetric.isCorrect = allChecked ? allCorrect : null;
      parentMetric.firstTryCorrect = allChecked ? allFirstTry : null;
    }
    if (explanationBox && allChecked) explanationBox.classList.remove('hidden');
  }

  function renderTabsAndActiveSub() {
    recomputeAggregate();
    tabsRow.innerHTML = '';
    subQuestions.forEach((sq, idx) => {
      const sm = metricById.get(sq.id);
      const cls = ['mby-case-tab'];
      if (idx === st.activePartIdx) cls.push('active');
      if (sm && sm.isCorrect === true) cls.push('correct');
      else if (sm && sm.isCorrect === false) cls.push('wrong');
      else if (sm && sm.flashcardRating) cls.push('correct');
      const tab = mk('button', cls.join(' '), `Part ${idx + 1}`);
      tab.type = 'button';
      tab.addEventListener('click', () => { st.activePartIdx = idx; renderTabsAndActiveSub(); });
      tabsRow.appendChild(tab);
    });

    subHost.innerHTML = '';
    const activeSq = subQuestions[Math.max(0, Math.min(st.activePartIdx, subQuestions.length - 1))];
    if (activeSq) {
      subHost.appendChild(renderSingleQuestionWidget(activeSq, () => {
        recomputeAggregate();
        renderTabsOnly();
        if (onAnswered) onAnswered();
      }));
    }
    requestAnimationFrame(updateScrollDownHint);
  }

  function renderTabsOnly() {
    Array.from(tabsRow.children).forEach((tab, idx) => {
      const sq = subQuestions[idx];
      const sm = sq ? metricById.get(sq.id) : null;
      tab.classList.toggle('active', idx === st.activePartIdx);
      tab.classList.toggle('correct', !!(sm && (sm.isCorrect === true || sm.flashcardRating)));
      tab.classList.toggle('wrong', !!(sm && sm.isCorrect === false));
    });
  }

  function stripHtmlToText(html) { return htmlToPlainText(html || ''); }

  renderTabsAndActiveSub();
  return card;
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
    if (q) qSlide.appendChild(renderSingleQuestionWidget(q, onItemChanged, onFlipStart, onFlipEnd));
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

function getHumanKindLabel(kind) {
  return ({ article: 'Article', quiz: 'Quiz', mcq: 'MCQ', flashcard: 'Flashcard', 'fill-blank': 'Fill in the Blank', 'assertion-reason': 'Assertion–Reason', 'spot-error': 'Spot the Error', diff: 'Differentiation', mechanism: 'Mechanism', relabel: 'Relabel the Image', classify: 'Classification', match: 'Matching', numeric: 'Numeric', case: 'Case Study' })[kind] || String(kind || 'Item').toUpperCase();
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
        const entry = SEARCH_INDEX.find(e => e.leafId === id);
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
  const articleCount = DATA.slides.filter(s => s.kind === 'article').length;
  const questionCount = leafSlides.filter(s => s.kind !== 'article').length;
  const greetingParts = [];
  if (articleCount > 0) greetingParts.push(`${articleCount} Article${articleCount === 1 ? '' : 's'}`);
  if (questionCount > 0) greetingParts.push(`${questionCount} Question${questionCount === 1 ? '' : 's'}`);
  greetingMeta.textContent = greetingParts.join('  •  ');
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
        if (b.kind === 'diagram-explore' && b.diagramExplore) {
          return buildDiagramExploreBlock(b);
        }
        if (b.kind === 'reveal' && b.reveal) {
          return buildRevealBlock(b);
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
    else slideEl.appendChild(renderSingleQuestionWidget(slide, null));

    slideEl.addEventListener('scroll', updateScrollDownHint, { passive: true });
    track.appendChild(slideEl);
    const dot = mk('button', 'mby-dot'); dot.type = 'button'; dot.title = getComponentDisplayName(slide); dot.addEventListener('click', () => goToSlide(idx)); dotsNav.appendChild(dot);
  });

  track.addEventListener('transitionend', e => { if (e.target === track && e.propertyName === 'transform') requestAnimationFrame(updateScrollDownHint); });
  window.addEventListener('resize', () => requestAnimationFrame(updateScrollDownHint));
  if (scrollDownBtn) scrollDownBtn.addEventListener('click', () => track.children[currentIndex]?.scrollBy({ top: 180, behavior: 'smooth' }));
  initLinkPreviewCache();
  applyInitialArticleHashRoute();
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
    } else if (q.classify) {
      if (q.classify.titleHtml) parts.push(htmlToPlainText(q.classify.titleHtml));
      (q.classify.buckets || []).forEach(b => parts.push(b.name));
      (q.classify.items || []).forEach(it => parts.push(htmlToPlainText(it.html)));
      if (q.classify.explanationHtml) parts.push(htmlToPlainText(q.classify.explanationHtml));
    } else if (q.match) {
      if (q.match.titleHtml) parts.push(htmlToPlainText(q.match.titleHtml));
      (q.match.pairs || []).forEach(p => { parts.push(htmlToPlainText(p.leftHtml)); parts.push(htmlToPlainText(p.rightHtml)); });
      if (q.match.explanationHtml) parts.push(htmlToPlainText(q.match.explanationHtml));
    } else if (q.numeric) {
      parts.push(htmlToPlainText(q.numeric.promptHtml));
      if (q.numeric.unit) parts.push(q.numeric.unit);
      if (q.numeric.explanationHtml) parts.push(htmlToPlainText(q.numeric.explanationHtml));
    } else if (q.caseGroup) {
      if (q.caseGroup.titleHtml) parts.push(htmlToPlainText(q.caseGroup.titleHtml));
      parts.push(htmlToPlainText(q.caseGroup.stemHtml));
      (q.caseGroup.questions || []).forEach(sq => collectQuestionSearchText(sq, parts));
      if (q.caseGroup.explanationHtml) parts.push(htmlToPlainText(q.caseGroup.explanationHtml));
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
        if (b.diagramExplore) {
          if (b.diagramExplore.title) parts.push(b.diagramExplore.title);
          (b.diagramExplore.hotspots || []).forEach(hs => parts.push(hs.label + ' ' + htmlToPlainText(hs.noteHtml)));
        }
        if (b.reveal) {
          if (b.reveal.title) parts.push(b.reveal.title);
          (b.reveal.steps || []).forEach(st => parts.push(htmlToPlainText(st.headingHtml) + ' ' + htmlToPlainText(st.bodyHtml)));
        }
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
    } else if (slide.mcq || slide.flashcard || slide.fillBlank || slide.assertionReason || slide.spotError || slide.differentiation || slide.mechanism || slide.relabel || slide.classify || slide.match || slide.numeric || slide.caseGroup) {
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
  return entries;
})();

const linkCardCache = new Map();
const inSessionArticleCache = new Map();
const externalSessionPreviewCache = new Map();
const loadedPreviewDirs = new Set();

window.__mbyRegisterSessionPreview = function(payload) {
  if (!payload || !payload.sessionId) return;
  externalSessionPreviewCache.set(payload.sessionId, payload);
};

function findArticleSlideIndex(target) {
  if (!target) return -1;
  const t = String(target).trim().toLowerCase();
  for (let i = 0; i < DATA.slides.length; i++) {
    const s = DATA.slides[i];
    if (s.kind !== 'article') continue;
    if (s.id && s.id.toLowerCase() === t) return i;
    if (s.refKey && s.refKey.toLowerCase() === t) return i;
    if (`c${s.componentIndex || (i + 1)}` === t) return i;
  }
  return -1;
}

function initLinkPreviewCache() {
  Object.values(DATA.linkCards || {}).forEach(c => {
    if (c && c.id) linkCardCache.set(String(c.id).toLowerCase(), c);
  });

  DATA.slides.forEach((s, slideIdx) => {
    if (s.kind !== 'article') return;
    const parts = [];
    (s.articleBlocks || []).forEach(b => {
      if (b.title) parts.push(b.title);
      if (b.textHtml) parts.push(htmlToPlainText(b.textHtml));
      if (b.table) {
        if (b.table.title) parts.push(b.table.title);
        (b.table.headersHtml || []).forEach(h => parts.push(htmlToPlainText(h)));
        (b.table.rowsHtml || []).forEach(r => r.forEach(c => parts.push(htmlToPlainText(c))));
      }
      if (b.diagramExplore) {
        if (b.diagramExplore.title) parts.push(b.diagramExplore.title);
        (b.diagramExplore.hotspots || []).forEach(hs => parts.push(hs.label + ': ' + htmlToPlainText(hs.noteHtml)));
      }
      if (b.reveal) {
        if (b.reveal.title) parts.push(b.reveal.title);
        (b.reveal.steps || []).forEach(st => parts.push(htmlToPlainText(st.headingHtml) + ' ' + htmlToPlainText(st.bodyHtml)));
      }
    });
    const rawExcerpt = parts.join(' ').replace(/\s+/g, ' ').trim();
    const excerpt = rawExcerpt.length > 520 ? (rawExcerpt.slice(0, 519).trimEnd() + '…') : rawExcerpt;
    const cIdx = s.componentIndex || (slideIdx + 1);
    const entry = { slideIdx, title: s.title || `Article ${cIdx}`, excerpt };
    if (s.id) inSessionArticleCache.set(String(s.id).toLowerCase(), entry);
    if (s.refKey) inSessionArticleCache.set(String(s.refKey).toLowerCase(), entry);
    inSessionArticleCache.set(`c${cIdx}`, entry);
  });

  Object.entries(DATA.externalPreviews || {}).forEach(([sid, payload]) => {
    if (payload) externalSessionPreviewCache.set(sid, payload);
  });

  document.querySelectorAll('.mby-native-link[data-mby-kind="session"]').forEach(anchor => {
    const dir = anchor.dataset.mbyPreviewDir;
    if (!dir || loadedPreviewDirs.has(dir)) return;
    loadedPreviewDirs.add(dir);
    const sc = document.createElement('script');
    sc.async = true;
    sc.src = dir.replace(/\/+$/, '') + '/preview-index.js';
    sc.onerror = () => {};
    document.head.appendChild(sc);
  });
}

function applyInitialArticleHashRoute() {
  const hash = window.location.hash || '';
  const m = hash.match(/article=([^&]+)/i);
  if (!m) return;
  let target = m[1];
  try { target = decodeURIComponent(target); } catch {}
  const idx = findArticleSlideIndex(target);
  if (idx >= 0) {
    greetingScreen.classList.add('hidden');
    playerTopbar.classList.remove('hidden');
    playerViewport.classList.remove('hidden');
    playerControls.classList.remove('hidden');
    sessionStartedAt = new Date();
    activeLeafEnterMs = null;
    lastVisitedLeafId = null;
    goToSlide(idx);
  }
}

let activePopoverAnchor = null, popoverHideTimer = null;
function hideLinkPopoverImmediate() {
  if (popoverHideTimer) { clearTimeout(popoverHideTimer); popoverHideTimer = null; }
  activePopoverAnchor = null;
  if (linkPreviewPopover) linkPreviewPopover.classList.add('hidden');
}
function scheduleHideLinkPopover() {
  if (popoverHideTimer) clearTimeout(popoverHideTimer);
  popoverHideTimer = setTimeout(hideLinkPopoverImmediate, 160);
}
function positionLinkPopover(anchor) {
  if (!linkPreviewPopover || !anchor) return;
  const r = anchor.getBoundingClientRect();
  const pw = Math.min(370, window.innerWidth - 24);
  const ph = Math.max(120, linkPreviewPopover.offsetHeight || 200);
  let left = Math.max(12, Math.min(r.left, window.innerWidth - pw - 12));
  let top = r.bottom + 8;
  if (top + ph > window.innerHeight - 12 && r.top - ph - 8 > 12) {
    top = r.top - ph - 8;
  }
  linkPreviewPopover.style.left = left + 'px';
  linkPreviewPopover.style.top = Math.max(12, top) + 'px';
}
function showLinkPopover(anchor) {
  if (!linkPreviewPopover || !anchor) return;
  if (popoverHideTimer) { clearTimeout(popoverHideTimer); popoverHideTimer = null; }
  activePopoverAnchor = anchor;
  const kind = anchor.dataset.mbyKind;
  const target = anchor.dataset.mbyTarget || '';
  const sub = anchor.dataset.mbySub || '';
  linkPreviewPopover.innerHTML = '';

  const head = mk('div', 'mby-link-popover-head');
  if (kind === 'card') {
    const card = linkCardCache.get(target.toLowerCase());
    head.appendChild(mk('span', 'mby-link-popover-badge', 'Reference Card'));
    linkPreviewPopover.appendChild(head);
    linkPreviewPopover.appendChild(mk('div', 'mby-link-popover-title', card?.title || anchor.textContent || target));
    if (card?.imagePath) {
      const img = mk('img', 'mby-link-popover-img');
      img.src = card.imagePath;
      img.alt = card.title || anchor.textContent || 'Reference image';
      img.addEventListener('click', () => openZoomLightbox(card.imagePath, card.title || img.alt));
      linkPreviewPopover.appendChild(img);
    }
    if (card?.textHtml) {
      const bodyEl = mk('div', 'mby-link-popover-body');
      bodyEl.innerHTML = card.textHtml;
      renderMathIn(bodyEl);
      linkPreviewPopover.appendChild(bodyEl);
    }
  } else if (kind === 'article') {
    const art = inSessionArticleCache.get(target.toLowerCase());
    head.appendChild(mk('span', 'mby-link-popover-badge', 'Article Preview'));
    linkPreviewPopover.appendChild(head);
    linkPreviewPopover.appendChild(mk('div', 'mby-link-popover-title', art?.title || anchor.textContent || target));
    const ex = mk('div', 'mby-link-popover-excerpt', art?.excerpt || 'In-session article reference.');
    linkPreviewPopover.appendChild(ex);
    linkPreviewPopover.appendChild(mk('div', 'mby-link-popover-footer', null, [mk('span', null, 'Click link to open article in deck')]));
  } else if (kind === 'session') {
    const ext = externalSessionPreviewCache.get(target);
    head.appendChild(mk('span', 'mby-link-popover-badge', 'External Session'));
    linkPreviewPopover.appendChild(head);
    let previewTitle = ext?.title || (anchor.textContent || target);
    let previewText = ext?.description || 'Cross-session study reference. Preview loads automatically once the target session is built.';
    if (ext && sub) {
      const subLower = sub.toLowerCase();
      const matchedArt = (ext.articles || []).find(a =>
        (a.refKey && a.refKey.toLowerCase() === subLower) ||
        (a.id && a.id.toLowerCase() === subLower) ||
        `c${a.index}` === subLower);
      if (matchedArt) {
        previewTitle = `${ext.title} — ${matchedArt.title}`;
        previewText = matchedArt.excerpt || previewText;
      }
    } else if (ext && !previewText && ext.articles?.length) {
      previewText = ext.articles.map(a => a.title + (a.excerpt ? ': ' + a.excerpt : '')).join(' • ');
    }
    linkPreviewPopover.appendChild(mk('div', 'mby-link-popover-title', previewTitle));
    linkPreviewPopover.appendChild(mk('div', 'mby-link-popover-excerpt', previewText));
    linkPreviewPopover.appendChild(mk('div', 'mby-link-popover-footer', null, [mk('span', null, ext ? 'Click link to open session' : 'Target session link ready (not yet built)')]));
  }

  linkPreviewPopover.classList.remove('hidden');
  requestAnimationFrame(() => positionLinkPopover(anchor));
}

document.addEventListener('mouseover', e => {
  const link = e.target?.closest?.('.mby-native-link');
  if (link) { showLinkPopover(link); return; }
  if (linkPreviewPopover && linkPreviewPopover.contains(e.target) && popoverHideTimer) {
    clearTimeout(popoverHideTimer);
    popoverHideTimer = null;
  }
});
document.addEventListener('mouseout', e => {
  const link = e.target?.closest?.('.mby-native-link');
  const inPop = linkPreviewPopover && linkPreviewPopover.contains(e.target);
  if ((link || inPop) && !e.relatedTarget?.closest?.('.mby-native-link') && !linkPreviewPopover?.contains(e.relatedTarget)) {
    scheduleHideLinkPopover();
  }
});
document.addEventListener('focusin', e => {
  const link = e.target?.closest?.('.mby-native-link');
  if (link) showLinkPopover(link);
});
document.addEventListener('focusout', e => {
  if (activePopoverAnchor && !linkPreviewPopover?.contains(e.relatedTarget) && !e.relatedTarget?.closest?.('.mby-native-link')) {
    scheduleHideLinkPopover();
  }
});
document.addEventListener('click', e => {
  const link = e.target?.closest?.('.mby-native-link');
  if (!link) return;
  const kind = link.dataset.mbyKind;
  if (kind === 'card') {
    e.preventDefault();
    showLinkPopover(link);
  } else if (kind === 'article') {
    e.preventDefault();
    const idx = findArticleSlideIndex(link.dataset.mbyTarget || '');
    hideLinkPopoverImmediate();
    if (idx >= 0) goToSlide(idx);
  }
});

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
  const textMatched = tokens.length === 0
    ? SEARCH_INDEX
    : SEARCH_INDEX.filter(item => tokens.every(t => item.lower.includes(t)));
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

  const filtered = SEARCH_INDEX.filter(item =>
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
  hideLinkPopoverImmediate();
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
  if (e.key === 'Escape' && linkPreviewPopover && !linkPreviewPopover.classList.contains('hidden')) {
    hideLinkPopoverImmediate();
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

    private sealed record WebBundlePayload(
        string Id,
        string Title,
        string? Description,
        string ThemeMode,
        string SlideTransitionMs,
        WebGeneratorInfoModel Generator,
        List<SlideRenderModel> Slides,
        Dictionary<string, LinkCardRenderModel>? LinkCards = null,
        Dictionary<string, SessionPreviewSidecarModel>? ExternalPreviews = null);
    private sealed record WebGeneratorInfoModel(string ToolName, string Executable, string Version, string Runtime, string GeneratedAtUtc, string AppName = "Meloberry", string RepositoryUrl = "https://github.com/doc-shashank/meloberry-cli/");
    private sealed record LinkCardRenderModel(string Id, string? Title, string? TextHtml, string? ImagePath);
    private sealed record ArticlePreviewSidecarModel(string Id, string? RefKey, int Index, string Title, string Excerpt);
    private sealed record SessionPreviewSidecarModel(string SessionId, string Title, string Description, List<ArticlePreviewSidecarModel> Articles);
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
        RelabelRenderModel? Relabel = null,
        string? RefKey = null,
        int? ComponentIndex = null,
        ClassifyRenderModel? Classify = null,
        MatchRenderModel? Match = null,
        NumericRenderModel? Numeric = null,
        CaseGroupRenderModel? CaseGroup = null);
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
        string? TitleHtml = null,
        DiagramExploreRenderModel? DiagramExplore = null,
        RevealBlockRenderModel? Reveal = null);
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
    private sealed record FillBlankRenderModel(string PromptHtml, List<FillBlankSlotRenderModel> Blanks, bool Fuzzy, bool CaseSensitive, string? ExplanationHtml, string? Placeholder = null);
    private sealed record AssertionReasonOptionRenderModel(string Code, string Label);
    private sealed record AssertionReasonRenderModel(string AssertionHtml, string ReasonHtml, string Answer, string Scheme, List<AssertionReasonOptionRenderModel> Options, string? ExplanationHtml);
    private sealed record SpotErrorSegmentRenderModel(int? SpanIndex, string Html, bool IsCandidate, bool IsError, List<string>? Corrections, string? PrimaryCorrectionHtml = null);
    private sealed record SpotErrorRenderModel(List<SpotErrorSegmentRenderModel> Segments, bool RequireFix, bool AllowNoError, bool NoError, string? ExplanationHtml, string? ImagePath = null);
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
    private sealed record DiagramHotspotRenderModel(int Index, string Label, string NoteHtml, double X, double Y, double W, double H);
    private sealed record DiagramExploreRenderModel(string ImagePath, string? Title, string? TitleHtml, string? CaptionHtml, string? Alt, List<DiagramHotspotRenderModel> Hotspots);
    private sealed record RevealStepRenderModel(int Index, string HeadingHtml, string BodyHtml);
    private sealed record RevealBlockRenderModel(string? Title, string? TitleHtml, bool ShowFirst, List<RevealStepRenderModel> Steps);
    private sealed record ClassifyBucketRenderModel(int BucketId, string Name, string NameHtml);
    private sealed record ClassifyItemRenderModel(int ItemId, int BucketId, string Html);
    private sealed record ClassifyRenderModel(string? Title, string? TitleHtml, List<ClassifyBucketRenderModel> Buckets, List<ClassifyItemRenderModel> Items, string? ExplanationHtml);
    private sealed record MatchPairRenderModel(int PairId, string LeftHtml, string RightHtml);
    private sealed record MatchBankOptionRenderModel(int OptionId, string Html, int? CorrectPairId);
    private sealed record MatchRenderModel(string? Title, string? TitleHtml, List<MatchPairRenderModel> Pairs, List<MatchBankOptionRenderModel> RightPool, string? ExplanationHtml);
    private sealed record NumericRenderModel(string PromptHtml, double Answer, double Tolerance, string ToleranceMode, string? Unit, string? Placeholder, string? ImagePath, string? ExplanationHtml);
    private sealed record CaseGroupRenderModel(string? Title, string? TitleHtml, string StemHtml, string? ImagePath, string? CaptionHtml, List<SlideRenderModel> Questions, string? ExplanationHtml);
}
