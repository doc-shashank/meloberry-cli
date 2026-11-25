namespace Meloberry.Infrastructure.Web.Components;

internal sealed record WebAppFeatureSet(
    bool IsSession,
    bool HasMath,
    bool HasArticle,
    bool HasCallout,
    bool HasTable,
    bool HasDiagramExplore,
    bool HasReveal,
    bool HasQuizBundle,
    bool HasMcq,
    bool HasFlashcard,
    bool HasFillBlank,
    bool HasAssertionReason,
    bool HasSpotError,
    bool HasDiff,
    bool HasMechanism,
    bool HasRelabel,
    bool HasClassify,
    bool HasMatch,
    bool HasNumeric,
    bool HasCase,
    bool HasImages,
    bool HasNativeLinks,
    IReadOnlySet<string>? KatexFontFamilies = null)
{
    private static readonly string[] AmsSymbols =
    [
        "\\implies", "\\iff", "\\therefore", "\\because", "\\nexists", "\\varnothing",
        "\\nsubseteq", "\\nsupseteq", "\\lesssim", "\\gtrsim", "\\blacksquare", "\\square",
        "\\triangleleft", "\\triangleright", "\\varkappa", "\\digamma", "\\beth", "\\gimel",
        "\\daleth", "\\hbar", "\\hslash", "\\mho", "\\Finv", "\\Game", "\\Bbbk",
        "\\circledS", "\\yen", "\\checkmark", "\\maltese", "\\ulcorner", "\\urcorner",
        "\\llcorner", "\\lrcorner", "\\pitchfork", "\\bowtie", "\\Join", "\\ltimes",
        "\\rtimes", "\\leftthreetimes", "\\rightthreetimes", "\\backsimeq", "\\approxeq",
        "\\triangleq", "\\bumpeq", "\\Bumpeq", "\\doteqdot", "\\risingdotseq", "\\fallingdotseq",
        "\\eqcirc", "\\circeq", "\\lessdot", "\\gtrdot", "\\lll", "\\ggg", "\\lessapprox",
        "\\gtrapprox", "\\eqslantless", "\\eqslantgtr", "\\curlyeqprec", "\\curlyeqsucc",
        "\\preccurlyeq", "\\succcurlyeq", "\\precsim", "\\succsim", "\\precapprox", "\\succapprox",
        "\\subseteqq", "\\supseteqq", "\\Subset", "\\Supset", "\\sqsubset", "\\sqsupset",
        "\\curlywedge", "\\curlyvee", "\\veebar", "\\barwedge", "\\doublebarwedge",
        "\\divideontimes", "\\centerdot", "\\intercal", "\\circledast", "\\circledcirc",
        "\\circleddash", "\\dotplus", "\\boxplus", "\\boxminus", "\\boxtimes", "\\boxdot",
        "\\smallsetminus", "\\diagup", "\\diagdown", "\\nless", "\\ngtr", "\\nleq", "\\ngeq",
        "\\nleqslant", "\\ngeqslant", "\\lneq", "\\gneq", "\\lneqq", "\\gneqq",
        "\\lvertneqq", "\\gvertneqq", "\\lnapprox", "\\gnapprox", "\\nprec", "\\nsucc",
        "\\npreceq", "\\nsucceq", "\\precneqq", "\\succneqq", "\\precnapprox", "\\succnapprox",
        "\\nsim", "\\ncong", "\\nshortmid", "\\nshortparallel", "\\nmid", "\\nparallel",
        "\\nvdash", "\\nvDash", "\\nVdash", "\\nVDash", "\\ntriangleleft", "\\ntriangleright",
        "\\ntrianglelefteq", "\\ntrianglerighteq", "\\nsubseteqq", "\\nsupseteqq",
        "\\subsetneq", "\\supsetneq", "\\varsubsetneq", "\\varsupsetneq", "\\subsetneqq",
        "\\supsetneqq", "\\varsubsetneqq", "\\varsupsetneqq", "\\twoheadrightarrow",
        "\\twoheadleftarrow", "\\leftleftarrows", "\\rightrightarrows", "\\leftrightarrows",
        "\\rightleftarrows", "\\Lleftarrow", "\\Rrightarrow", "\\looparrowleft",
        "\\looparrowright", "\\leftrightharpoons", "\\rightleftharpoons", "\\curvearrowleft",
        "\\curvearrowright", "\\circlearrowleft", "\\circlearrowright", "\\Lsh", "\\Rsh",
        "\\upuparrows", "\\downdownarrows", "\\upharpoonleft", "\\upharpoonright",
        "\\downharpoonleft", "\\downharpoonright", "\\rightsquigarrow", "\\leftrightsquigarrow",
        "\\multimap", "\\nleftarrow", "\\nrightarrow", "\\nLeftarrow", "\\nRightarrow",
        "\\nleftrightarrow", "\\nLeftrightarrow"
    ];
    public bool HasAnyGradedQuestion =>
        HasMcq || HasFillBlank || HasAssertionReason || HasSpotError ||
        HasDiff || HasMechanism || HasRelabel || HasClassify ||
        HasMatch || HasNumeric || HasCase;

    public bool HasChoiceGrid => HasMcq || HasAssertionReason;
    public bool HasRatingRow => HasFlashcard || HasRelabel;
    public bool HasRecallMatching => HasFillBlank || HasSpotError || HasDiff || HasRelabel;
    public bool HasBlankInput => HasFillBlank || HasDiff || HasRelabel;
    public bool HasNoErrorToggle => HasSpotError || HasDiff;
    public bool HasDragAndDrop => HasDiff || HasMechanism || HasRelabel || HasClassify || HasMatch;

    public static readonly WebAppFeatureSet All = new(
        IsSession: true, HasMath: true, HasArticle: true, HasCallout: true, HasTable: true,
        HasDiagramExplore: true, HasReveal: true, HasQuizBundle: true, HasMcq: true,
        HasFlashcard: true, HasFillBlank: true, HasAssertionReason: true, HasSpotError: true,
        HasDiff: true, HasMechanism: true, HasRelabel: true, HasClassify: true, HasMatch: true,
        HasNumeric: true, HasCase: true, HasImages: true, HasNativeLinks: true);

    public static WebAppFeatureSet Analyze(
        IReadOnlyList<SlidingWebAppGenerator.SlideRenderModel> slides,
        IReadOnlyDictionary<string, SlidingWebAppGenerator.LinkCardRenderModel>? linkCards,
        IReadOnlyDictionary<string, SlidingWebAppGenerator.SessionPreviewSidecarModel>? externalPreviews,
        bool isSession)
    {
        bool hasArticle = false, hasCallout = false, hasTable = false, hasDiag = false, hasReveal = false;
        bool hasQuizBundle = false, hasMcq = false, hasFlashcard = false, hasFillBlank = false;
        bool hasAr = false, hasSpot = false, hasDiff = false, hasMech = false, hasRelabel = false;
        bool hasClassify = false, hasMatch = false, hasNumeric = false, hasCase = false;
        bool hasImages = false, hasMath = false;
        bool hasLinks = (linkCards is { Count: > 0 }) || (externalPreviews is { Count: > 0 });
        var katexFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void InspectHtml(string? html)
        {
            if (string.IsNullOrEmpty(html)) return;
            if (!hasLinks && html.Contains("mby-native-link", StringComparison.Ordinal))
                hasLinks = true;
            if (html.Contains("class=\"math\"", StringComparison.Ordinal))
            {
                hasMath = true;
                if (html.Contains("\\mathfrak", StringComparison.Ordinal)) katexFonts.Add("Fraktur");
                if (html.Contains("\\mathcal", StringComparison.Ordinal)) katexFonts.Add("Caligraphic");
                if (html.Contains("\\mathscr", StringComparison.Ordinal)) katexFonts.Add("Script");
                if (html.Contains("\\mathtt", StringComparison.Ordinal) || html.Contains("\\texttt", StringComparison.Ordinal)) katexFonts.Add("Typewriter");
                if (html.Contains("\\mathsf", StringComparison.Ordinal) || html.Contains("\\textsf", StringComparison.Ordinal)) katexFonts.Add("SansSerif");
                if (!katexFonts.Contains("AMS") && (
                    html.Contains("\\mathbb", StringComparison.Ordinal) ||
                    html.Contains("\\textbb", StringComparison.Ordinal) ||
                    html.Contains("\\amsrm", StringComparison.Ordinal) ||
                    AmsSymbols.Any(sym => html.Contains(sym, StringComparison.Ordinal))))
                {
                    katexFonts.Add("AMS");
                }
            }
        }

        if (linkCards is not null)
        {
            foreach (var lc in linkCards.Values)
            {
                if (!string.IsNullOrWhiteSpace(lc.ImagePath)) hasImages = true;
                InspectHtml(lc.TextHtml);
            }
        }

        void VisitSlide(SlidingWebAppGenerator.SlideRenderModel s)
        {
            switch (s.Kind)
            {
                case "article":
                    hasArticle = true;
                    foreach (var b in s.ArticleBlocks ?? [])
                    {
                        InspectHtml(b.TextHtml);
                        InspectHtml(b.CaptionHtml);
                        InspectHtml(b.TitleHtml);
                        if (!string.IsNullOrWhiteSpace(b.ImagePath)) hasImages = true;

                        switch (b.Kind)
                        {
                            case "callout": hasCallout = true; break;
                            case "table":
                                hasTable = true;
                                if (b.Table is not null)
                                {
                                    InspectHtml(b.Table.TitleHtml);
                                    InspectHtml(b.Table.CaptionHtml);
                                    InspectHtml(b.Table.FootnoteHtml);
                                    foreach (var h in b.Table.HeadersHtml) InspectHtml(h);
                                    foreach (var r in b.Table.RowsHtml) foreach (var c in r) InspectHtml(c);
                                }
                                break;
                            case "diagram-explore":
                                hasDiag = true;
                                hasImages = true;
                                if (b.DiagramExplore is not null)
                                {
                                    InspectHtml(b.DiagramExplore.TitleHtml);
                                    InspectHtml(b.DiagramExplore.CaptionHtml);
                                    foreach (var hs in b.DiagramExplore.Hotspots) InspectHtml(hs.NoteHtml);
                                }
                                break;
                            case "reveal":
                                hasReveal = true;
                                if (b.Reveal is not null)
                                {
                                    InspectHtml(b.Reveal.TitleHtml);
                                    foreach (var st in b.Reveal.Steps) { InspectHtml(st.HeadingHtml); InspectHtml(st.BodyHtml); }
                                }
                                break;
                        }
                    }
                    break;
                case "quiz":
                    hasQuizBundle = true;
                    foreach (var q in s.Questions ?? []) VisitSlide(q);
                    break;
                case "mcq":
                    hasMcq = true;
                    if (s.Mcq is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(s.Mcq.ImagePath) || s.Mcq.Options.Any(o => !string.IsNullOrWhiteSpace(o.ImagePath)))
                            hasImages = true;
                        InspectHtml(s.Mcq.PromptHtml);
                        InspectHtml(s.Mcq.ExplanationHtml);
                        foreach (var o in s.Mcq.Options) InspectHtml(o.TextHtml);
                    }
                    break;
                case "flashcard":
                    hasFlashcard = true;
                    if (s.Flashcard is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(s.Flashcard.Front.ImagePath) || !string.IsNullOrWhiteSpace(s.Flashcard.Back.ImagePath))
                            hasImages = true;
                        InspectHtml(s.Flashcard.Front.TextHtml);
                        InspectHtml(s.Flashcard.Back.TextHtml);
                    }
                    break;
                case "fill-blank":
                    hasFillBlank = true;
                    if (s.FillBlank is not null)
                    {
                        InspectHtml(s.FillBlank.PromptHtml);
                        InspectHtml(s.FillBlank.ExplanationHtml);
                        foreach (var b in s.FillBlank.Blanks) InspectHtml(b.PrimaryAnswerHtml);
                    }
                    break;
                case "assertion-reason":
                    hasAr = true;
                    InspectHtml(s.AssertionReason?.AssertionHtml);
                    InspectHtml(s.AssertionReason?.ReasonHtml);
                    InspectHtml(s.AssertionReason?.ExplanationHtml);
                    break;
                case "spot-error":
                    hasSpot = true;
                    if (s.SpotError is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(s.SpotError.ImagePath)) hasImages = true;
                        InspectHtml(s.SpotError.ExplanationHtml);
                        foreach (var seg in s.SpotError.Segments)
                        {
                            InspectHtml(seg.Html);
                            InspectHtml(seg.PrimaryCorrectionHtml);
                        }
                    }
                    break;
                case "diff":
                    hasDiff = true;
                    if (s.Differentiation is not null)
                    {
                        InspectHtml(s.Differentiation.TitleHtml);
                        InspectHtml(s.Differentiation.ExplanationHtml);
                        foreach (var h in s.Differentiation.HeadersHtml) InspectHtml(h);
                        foreach (var r in s.Differentiation.RowsHtml) foreach (var c in r) InspectHtml(c);
                        foreach (var sc in s.Differentiation.SpecialCells)
                        {
                            InspectHtml(sc.PrimaryAnswerHtml);
                            InspectHtml(sc.WrongHtml);
                            InspectHtml(sc.PrimaryCorrectionHtml);
                        }
                    }
                    break;
                case "mechanism":
                    hasMech = true;
                    if (s.Mechanism is not null)
                    {
                        InspectHtml(s.Mechanism.TitleHtml);
                        InspectHtml(s.Mechanism.ExplanationHtml);
                        foreach (var st in s.Mechanism.Steps) InspectHtml(st.Html);
                    }
                    break;
                case "relabel":
                    hasRelabel = true;
                    hasImages = true;
                    InspectHtml(s.Relabel?.PromptHtml);
                    InspectHtml(s.Relabel?.ExplanationHtml);
                    break;
                case "classify":
                    hasClassify = true;
                    if (s.Classify is not null)
                    {
                        InspectHtml(s.Classify.TitleHtml);
                        InspectHtml(s.Classify.ExplanationHtml);
                        foreach (var b in s.Classify.Buckets) InspectHtml(b.NameHtml);
                        foreach (var it in s.Classify.Items) InspectHtml(it.Html);
                    }
                    break;
                case "match":
                    hasMatch = true;
                    if (s.Match is not null)
                    {
                        InspectHtml(s.Match.TitleHtml);
                        InspectHtml(s.Match.ExplanationHtml);
                        foreach (var p in s.Match.Pairs) { InspectHtml(p.LeftHtml); InspectHtml(p.RightHtml); }
                        foreach (var r in s.Match.RightPool) InspectHtml(r.Html);
                    }
                    break;
                case "numeric":
                    hasNumeric = true;
                    if (!string.IsNullOrWhiteSpace(s.Numeric?.ImagePath)) hasImages = true;
                    InspectHtml(s.Numeric?.PromptHtml);
                    InspectHtml(s.Numeric?.ExplanationHtml);
                    break;
                case "case":
                    hasCase = true;
                    if (s.CaseGroup is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(s.CaseGroup.ImagePath)) hasImages = true;
                        InspectHtml(s.CaseGroup.TitleHtml);
                        InspectHtml(s.CaseGroup.StemHtml);
                        InspectHtml(s.CaseGroup.CaptionHtml);
                        InspectHtml(s.CaseGroup.ExplanationHtml);
                        foreach (var sub in s.CaseGroup.Questions) VisitSlide(sub);
                    }
                    break;
            }
        }

        foreach (var slide in slides) VisitSlide(slide);

        return new WebAppFeatureSet(
            isSession, hasMath, hasArticle, hasCallout, hasTable, hasDiag, hasReveal,
            hasQuizBundle, hasMcq, hasFlashcard, hasFillBlank, hasAr, hasSpot, hasDiff,
            hasMech, hasRelabel, hasClassify, hasMatch, hasNumeric, hasCase, hasImages, hasLinks, katexFonts);
    }
}
