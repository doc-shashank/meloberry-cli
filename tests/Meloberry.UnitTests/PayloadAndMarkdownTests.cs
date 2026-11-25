using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Json;
using Meloberry.Domain.Common;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Sessions;
using Meloberry.Infrastructure.Services;
using Meloberry.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class PayloadAndMarkdownTests : IDisposable
{
    private readonly WorkspaceTestFixture _fx = new("mby_payload_md");

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Domain_ArticleBlock_RefKeyValidator_And_ArticleLinkRules_WorkCleanly()
    {
        var img = ArticleBlock.CreateImage("ArtComp_1", "Component_1", "assets/fig1.png", caption: "Fig. 2.1 Overview", alt: "Overview diagram");
        Assert.Equal("Fig. 2.1 Overview", img.Caption);
        Assert.Equal("Overview diagram", img.Alt);
        Assert.True(img.SetImageCaption("Fig. 2.1 Revised").IsSuccess);
        Assert.True(img.SetImageAlt("Revised alt").IsSuccess);
        Assert.Equal("Fig. 2.1 Revised", img.Caption);
        Assert.Equal("Revised alt", img.Alt);

        Assert.True(ArticleBlock.TryParseCalloutTone("clinical", out var clinical));
        Assert.Equal(CalloutTone.Clinical, clinical);
        Assert.False(ArticleBlock.TryParseCalloutTone("invalid_tone", out _));

        var callout = ArticleBlock.CreateCallout("ArtComp_2", "Component_1", CalloutTone.Warning, "Never administer IV bolus.", "Boxed Warning").Value;
        Assert.Equal(CalloutTone.Warning, callout.CalloutTone);
        Assert.Equal("Boxed Warning", callout.CalloutTitle);

        // RefKeyValidator: reserved positional refs, length limit, normalization, auto-slug
        Assert.False(RefKeyValidator.ValidateAndNormalize("c1").IsSuccess);
        Assert.False(RefKeyValidator.ValidateAndNormalize("q12").IsSuccess);
        Assert.Equal("art-c1", RefKeyValidator.SuggestSlug("C1", "art"));

        var tooLong = RefKeyValidator.ValidateAndNormalize("art-07-clinicopathologic-features-extremely-long-slug");
        Assert.False(tooLong.IsSuccess);
        Assert.Equal("Address.InvalidRef", tooLong.Error!.Code);
        Assert.Contains("32", tooLong.Error.Description);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slug1 = RefKeyValidator.GenerateSlug("Clinicopathologic Features of Cellular Aging and Accumulation", "art", used);
        var slug2 = RefKeyValidator.GenerateSlug("Clinicopathologic Features of Cellular Aging and Accumulation", "art", used);
        Assert.Equal(slug1, RefKeyValidator.SuggestSlug("Clinicopathologic Features of Cellular Aging and Accumulation", "art"));
        Assert.NotEqual(slug1, slug2);
        Assert.InRange(slug1.Length, 1, 32);
        Assert.InRange(slug2.Length, 1, 32);

        // ArticleLinkRules URI parsing & extraction outside code fences
        Assert.True(ArticleLinkRules.TryParseMbyUri("mby://card/Loop-Card", out var card));
        Assert.Equal("loop-card", card.TargetId);
        Assert.True(ArticleLinkRules.TryParseMbyUri("mby://article/renal-pharm", out var art));
        Assert.Equal("renal-pharm", art.TargetId);

        Assert.True(ArticleLinkRules.TryParseMbyUri("mby://session/Session_20260401_120000/ecg-basics", out var sess));
        var resolvedHref = ArticleLinkRules.ResolveSessionHref(sess);
        Assert.Equal("../Session_20260401_120000_web/index.html#article=ecg-basics", resolvedHref);
        Assert.Equal("../Session_20260401_120000_web", ArticleLinkRules.ResolveSessionPreviewDir(resolvedHref));

        Assert.True(ArticleLinkRules.TryParseMbyUri("mby://session/Session_123/intro?href=../custom/study.html", out var customSess));
        Assert.Equal("../custom/study.html#article=intro", ArticleLinkRules.ResolveSessionHref(customSess));
        Assert.Equal("../custom", ArticleLinkRules.ResolveSessionPreviewDir("../custom/study.html#article=intro"));

        Assert.False(ArticleLinkRules.TryParseMbyUri("mby://session/Session_123?href=javascript:alert(1)", out _));
        Assert.False(ArticleLinkRules.TryParseMbyUri("mby://unknown/abc", out _));

        var md = """
            Inline `[ignored](mby://card/in-code)` should be skipped.

            ```markdown
            [also-ignored](mby://article/in-fence)
            ```

            Real [Card](mby://card/valid-card) and [Session](mby://session/Session_ABC).
            """;
        var links = ArticleLinkRules.ExtractMbyLinksFromMarkdown(md);
        Assert.Equal(2, links.Count);
        Assert.Equal("valid-card", links[0].TargetId);
        Assert.Equal("Session_ABC", links[1].TargetId);
    }

    [Fact]
    public void PayloadRules_HandleKatexPipesEscapesBackcompatAndPhase3Formats()
    {
        // Fill-blank KaTeX, pipes, escaped \|, inline code, and unbalanced prose }}
        var fbRes = Phase1PayloadRules.ParseFillBlankTemplate(
            @"The value of $\frac{1}{\sqrt{2}}$ and $\text{P}_{\text{O}_2}$ is {{0.707 | $P(A|B)$ | a\|b}}.");
        Assert.True(fbRes.IsSuccess, fbRes.Error?.Description);
        Assert.Equal(["0.707", "$P(A|B)$", "a|b"], Assert.Single(fbRes.Value).Answers);

        var fbCode = Phase1PayloadRules.ParseFillBlankTemplate("Avoid literal `{{x}}` and enter {{`a | b` | fallback}}.");
        Assert.True(fbCode.IsSuccess);
        Assert.Equal(["`a | b`", "fallback"], Assert.Single(fbCode.Value).Answers);

        Assert.Equal("FillBlank.UnbalancedDelimiters", Phase1PayloadRules.ParseFillBlankTemplate("Prose stray }} and {{ok}}.").Error!.Code);

        // Spot-error KaTeX brackets/pipes, \\[, backtick arr[0], and unbalanced ]
        var seRes = Phase1PayloadRules.ParseSpotErrorTemplate(
            @"In [acidosis], $[\text{Ca}^{2+}]$ and [$[\text{H}^+]$] give [~~$\sqrt[3]{8}$~~ -> $\sqrt[2]{4}$ | $P(A|B)$ | a\|b].");
        Assert.True(seRes.IsSuccess, seRes.Error?.Description);
        var candidates = seRes.Value.Where(s => s.IsCandidate).ToList();
        Assert.Equal(3, candidates.Count);
        Assert.Equal(@"$[\text{H}^+]$", candidates[1].Text);
        Assert.Equal([@"$\sqrt[2]{4}$", "$P(A|B)$", "a|b"], candidates[2].Corrections);

        var seCode = Phase1PayloadRules.ParseSpotErrorTemplate(@"Check `arr[0]` and \\[item] plus [~~bad~~ -> `x | y`].");
        Assert.True(seCode.IsSuccess);
        Assert.Equal(2, seCode.Value.Count(s => s.IsCandidate));
        Assert.Contains("`arr[0]`", seCode.Value[0].Text);

        Assert.Equal("SpotError.UnbalancedBrackets", Phase1PayloadRules.ParseSpotErrorTemplate("Stray ] then [one] and [~~two~~ -> three].").Error!.Code);

        // Markdown table pipe splitting
        var tblRes = Phase1PayloadRules.ParseMarkdownTable("""
            | Metric | Formula | Blank | EscapedSlash |
            | :--- | :---: | :--- | ---: |
            | Cond | $P(A|B)$ | {{alpha | beta}} | `a | b` \\|
            """);
        Assert.True(tblRes.IsSuccess);
        Assert.Equal(["Cond", "$P(A|B)$", "{{alpha | beta}}", @"`a | b` \\"], tblRes.Value.Rows[0]);

        // Backward-compatibility deserialization
        Assert.True(Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(
            """{"template":"Enzyme is {{PFK-1}}.","blanks":[{"index":1,"answers":["PFK-1"]}],"fuzzy":true,"caseSensitive":false}""", out var fbLegacy));
        Assert.Null(fbLegacy!.Placeholder);
        Assert.True(Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(
            """{"template":"[A] and [~~B~~ -> C].","segments":[{"spanIndex":1,"text":"A","isCandidate":true,"isError":false},{"spanIndex":null,"text":" and ","isCandidate":false,"isError":false},{"spanIndex":2,"text":"B","isCandidate":true,"isError":true,"corrections":["C"]}],"requireFix":false,"allowNoError":false,"noError":false}""", out var seLegacy));
        Assert.Null(seLegacy!.ImagePath);

        // Phase 3 domain payload parsers
        var annotations = Phase3PayloadRules.ParseDiagramHotspotAnnotations("""
            - Glomerulus: Filters blood plasma at **high pressure** ($P_{GC}$).
            - Bowman's Capsule: Collects ultrafiltrate; ignores `a:b` colons.
            """);
        Assert.True(annotations.IsSuccess);
        IReadOnlyList<RelabelBox> sidecarBoxes =
        [
            new RelabelBox(0, "Glomerulus Capillary", [], 0.1, 0.2, 0.2, 0.15),
            new RelabelBox(1, "Bowman's Capsule", [], 0.5, 0.2, 0.2, 0.15)
        ];
        Assert.Equal(2, Phase3PayloadRules.ResolveHotspotsFromSidecar(sidecarBoxes, annotations.Value).Value.Count);
        Assert.Equal("DiagramExplore.UnmatchedHotspot",
            Phase3PayloadRules.ResolveHotspotsFromSidecar(sidecarBoxes, [new DiagramHotspotAnnotation("MissingPart", "Note")]).Error!.Code);

        var revealSteps = Phase3PayloadRules.ParseRevealSteps("""
            > Step 1: Observe Input
            ```text
            > Not a header inside code fence
            ```
            $P = IV$

            > Step 2: Conclude
            Flow increases linearly.
            """);
        Assert.True(revealSteps.IsSuccess);
        Assert.Equal(2, revealSteps.Value.Count);
        Assert.Contains("> Not a header inside code fence", revealSteps.Value[0].Body);

        var classify = Phase3PayloadRules.ParseClassifyLines("[Beta-Lactams]\n- Penicillin G\n  (IV formulation)\n[Macrolides]\n- Azithromycin");
        Assert.True(classify.IsSuccess);
        Assert.Contains("(IV formulation)", classify.Value.Items[0].Text);

        var match = Phase3PayloadRules.ParseMatchLines("- `Code::Span` :: Target $a::b$\n- Renin :: Converts angiotensinogen\n! ANP (inhibitor)");
        Assert.True(match.IsSuccess);
        Assert.Equal("`Code::Span`", match.Value.Pairs[0].Left);
        Assert.Equal("Target $a::b$", match.Value.Pairs[0].Right);
        Assert.Single(match.Value.Distractors);

        Assert.True(Phase3PayloadRules.TryParseToleranceSpec("5%", null, out var pctVal, out var pctMode) && pctVal == 5 && pctMode == "percent");
        Assert.True(Phase3PayloadRules.TryParseToleranceSpec("2.5", null, out var absVal, out var absMode) && absVal == 2.5 && absMode == "absolute");
        Assert.False(Phase3PayloadRules.TryParseToleranceSpec("-1", null, out _, out _));
        Assert.False(Phase3PayloadRules.TryParseToleranceSpec("abc%", null, out _, out _));
    }

    [Fact]
    public async Task DirectoryAndMarkdownLoader_ParsesAllBlockAndQuestionTypes_Sidecars_And_LayoutOverrides()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var loader = scope.ServiceProvider.GetRequiredService<IDeclarativeSpecLoader>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        // 1. Modular directory with _session.md, natural sort, sidecars ignored, spaced image, code fences, callout KaTeX, all block/quiz types
        var modDir = _fx.SubDir("modular_all");
        var assetsDir = Path.Combine(modDir, "assets");
        await TestAssets.WritePngAsync(Path.Combine(assetsDir, "fig 1 (a).png"));
        await TestAssets.WritePngAsync(Path.Combine(assetsDir, "nephron.png"));
        await TestAssets.WriteMasksSidecarAsync(Path.Combine(assetsDir, "nephron.masks.json"), """
            {
              "boxes": [
                { "label": "Proximal tubule", "aliases": ["PCT"], "rect": [0.10, 0.10, 0.20, 0.08] },
                { "label": "Loop of Henle", "rect": [0.35, 0.50, 0.25, 0.10] },
                { "label": "Collecting duct", "rect": [0.70, 0.25, 0.20, 0.10] }
              ]
            }
            """);
        await TestAssets.WriteMelomgMetaAsync(Path.Combine(assetsDir, "nephron.meta.json"), """
            {
              "figureId": "Fig_1",
              "pixelWidth": 1000,
              "pixelHeight": 500,
              "appliedMasks": [
                { "index": 1, "pixelX": 100, "pixelY": 100, "pixelWidth": 200, "pixelHeight": 50, "text": "Glomerulus", "style": "solid", "source": "ocr" },
                { "index": 2, "pixelX": 400, "pixelY": 250, "pixelWidth": 150, "pixelHeight": 50, "text": null, "style": "solid", "source": "manual" }
              ]
            }
            """);
        // Root sidecars must be ignored by directory loader
        await TestAssets.WriteMasksSidecarAsync(Path.Combine(modDir, "ignored.masks.json"), """{"boxes":[{"label":"A","rect":[0.1,0.1,0.2,0.1]}]}""");
        await TestAssets.WriteMelomgMetaAsync(Path.Combine(modDir, "ignored.meta.json"), """{"image_size":[100,100],"regions":[]}""");

        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), "---\nkind: session\nname: Master Session\n---\n");
        await File.WriteAllTextAsync(Path.Combine(modDir, "2-notes.md"), """
            ---
            title: Ion Channels
            ref: ion-channels
            layoutOverrides:
              cardMaxWidthPx: 960
            ---

            ![ECG](ecg.png) showing ST elevation (inline, not an image block)

            ![a](a.png) ![b](b.png)

            ![Spaced](assets/fig 1 (a).png "Fig. 2.5 $\Delta G$")

            :::callout{tone="pearl" ref="co-pump" title="$\text{Na}^+$ Pump"}
            Maintains resting potential.
            :::

            :::pearl{ref="co-short" title="$\text{K}^+$ Efflux"}
            Drives repolarization.
            :::

            :::table{ref="tbl-ca" title="Serum $\text{Ca}^{2+}$" stickyFirstCol=true hideableCols="2"}
            | Ion | Value |
            | :--- | :---: |
            | $\text{Ca}^{2+}$ | 9.5 |
            :::

            :::diagram-explore{ref="diag-1" image="assets/nephron.png" masks="assets/nephron.meta.json" title="Nephron"}
            - Glomerulus: Ultrafiltration.
            :::

            :::reveal{ref="rev-1" title="Check" showFirst=true}
            > Step 1: Observe
            Details here.
            :::

            ```markdown
            ![ignored inside fence](assets/nope.png)
            :::pearl
            ignored
            :::
            ```
            """);
        await File.WriteAllTextAsync(Path.Combine(modDir, "10-quiz.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Master Quiz
            ref: master-quiz
            ---

            :::mcq{ref="q-mcq" multi=true image="assets/nephron.png"}
            Prompt?
            ```text
            :::not-a-fence inside code block
            ```
            - [x] A
            - [x] B ![Opt](assets/fig 1 (a).png)
            :::layout
            optionColumns: 2
            :::

            :::flashcard{ref="q-fc" frontPosition="aboveImage" backPosition="belowImage"}
            :::front
            ![F](assets/nephron.png)
            Front
            :::back
            Back
            :::layout
            flipDurationMs: 600
            :::

            :::assertion-reason{ref="q-ar" answer="both-explain"}
            A: Ratio check:
            a:b ratio is 2:1 in model.
            R: Shunt lowers PaO2.
            :::

            :::diff{ref="q-diff" mode="spot-wrong" title="Gram"}
            | Prop | G+ | G- |
            | :--- | :--- | :--- |
            | Wall | ~~Thin~~ -> Thick \| Dense | Thin |
            :::

            :::mechanism{ref="q-mech" title="RAAS"}
            1. Renin
            2a. Angiotensin I
            2b. ACE
            ! ANP
            :::

            :::relabel{ref="q-rel" image="assets/nephron.png" masks="assets/nephron.masks.json" mode="bank" style="blur"}
            prompt: Label selected parts.
            include: ["Proximal tubule", "Collecting duct"]
            :::

            :::relabel{ref="q-rel-inline" image="assets/nephron.png" mode="recall" style="whiteout"}
            prompt: Inline boxes.
            boxes:
              - label: "PCT"
                aliases: ["Proximal"]
                rect: [0.10, 0.20, 0.25, 0.12]
            :::

            :::case{ref="q-case" title="Vignette"}
            Patient stem.
            ::::mcq{explanation="Exp"}
            Sub Q?
            - [x] Yes
            - [ ] No
            ::::
            ::::numeric{answer=180 tolerance=10 unit="mg"}
            Dose:
            ::::
            :::
            """);
        await File.WriteAllTextAsync(Path.Combine(modDir, "11-json.json"), """
            {
              "kind": "quiz",
              "ref": "json-comp-ref",
              "title": "JSON Quiz",
              "questions": [{ "type": "flashcard", "front": { "text": "Q" }, "back": { "text": "A" } }]
            }
            """);

        var applied = await apply.HandleAsync(new ApplySpecCommand(null, modDir + Path.DirectorySeparatorChar, WorkingDirectory: _fx.WorkDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);
        var sess = (await repo.FindSessionAsync(applied.Value.TargetId))!.Session;
        Assert.Equal(3, sess.Components.Count);
        Assert.Equal("ion-channels", sess.Components[0].RefKey);
        Assert.Equal("960", sess.Components[0].LayoutOverrides["cardMaxWidthPx"]);
        Assert.Equal("json-comp-ref", sess.Components[2].RefKey);

        var blocks = sess.Components[0].ArticleBlocks;
        Assert.Contains(blocks, b => b.Kind == ArticleBlockKind.Text && b.TextContent!.Contains("showing ST elevation"));
        Assert.Contains(blocks, b => b.Kind == ArticleBlockKind.Text && b.TextContent!.Contains("![a](a.png) ![b](b.png)"));
        var imgBlk = Assert.Single(blocks, b => b.Kind == ArticleBlockKind.Image);
        Assert.Equal("Spaced", imgBlk.Alt);
        Assert.Equal(@"Fig. 2.5 $\Delta G$", imgBlk.Caption);
        Assert.Equal(@"$\text{Na}^+$ Pump", blocks.Single(b => b.RefKey == "co-pump").CalloutTitle);
        Assert.Equal(@"$\text{K}^+$ Efflux", blocks.Single(b => b.RefKey == "co-short").CalloutTitle);

        var qs = sess.Components[1].Quiz!.Questions;
        var mcq = Assert.IsType<McqQuestion>(qs[0]);
        Assert.Equal(2, mcq.MaxOptions);
        Assert.Contains(":::not-a-fence", mcq.Text);
        Assert.Equal("2", mcq.LayoutOverrides["optionColumns"]);

        var fc = Assert.IsType<FlashcardQuestion>(qs[1]);
        Assert.Equal(MediaPosition.TextAboveImage, fc.Front.Position);
        Assert.Equal("600", fc.LayoutOverrides["flipDurationMs"]);

        Assert.True(Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(qs[2].PayloadJson, out var ar));
        Assert.Contains("a:b ratio is 2:1 in model.", ar!.Assertion);
        Assert.True(Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(qs[5].PayloadJson, out var relFiltered));
        Assert.Equal(2, relFiltered!.Boxes.Count);
        Assert.True(Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(qs[6].PayloadJson, out var relInline));
        Assert.Contains("Proximal", relInline!.Boxes[0].Aliases);
        Assert.True(Phase1PayloadCodec.TryDeserialize<CaseGroupPayload>(qs[7].PayloadJson, out var cs));
        Assert.Equal(2, cs!.Questions.Count);

        // 2. session.json explicit order + code-fence heading protection + escaped quote in directive attr
        var orderedDir = _fx.SubDir("ordered_dir");
        await File.WriteAllTextAsync(Path.Combine(orderedDir, "session.json"), """{"kind":"session","name":"Ordered","order":["second.md","first.md"]}""");
        await File.WriteAllTextAsync(Path.Combine(orderedDir, "first.md"), "Plain paragraph.\n\n```bash\n# Not a title\n```\n");
        await File.WriteAllTextAsync(Path.Combine(orderedDir, "second.md"), "# Second Chapter\n\nBody.\n");
        var ordRes = await apply.HandleAsync(new ApplySpecCommand(null, orderedDir, WorkingDirectory: _fx.WorkDir));
        Assert.True(ordRes.IsSuccess, ordRes.Error?.Description);
        var ordSess = (await repo.FindSessionAsync(ordRes.Value.TargetId))!.Session;
        Assert.Equal("Second Chapter", ordSess.Components[0].Title);
        Assert.Equal("First", ordSess.Components[1].Title);

        var escapedAttr = await loader.LoadFromTextAsync("""
            ---
            title: Diuretics
            ---
            :::table{title="Loop (\"High-Ceiling\") Diuretics $\text{Na}^+$"}
            | Drug | Site |
            | --- | --- |
            | Furosemide | TAL |
            :::
            """);
        Assert.True(escapedAttr.IsSuccess);
        Assert.True(Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(escapedAttr.Value.Blocks![0].PayloadJson, out var escTbl));
        Assert.Equal(@"Loop (""High-Ceiling"") Diuretics $\text{Na}^+$", escTbl!.Title);

        var escapedOrder = await loader.LoadFromTextAsync("---\nkind: session\norder: [\"01-a\\\\\", \"02-b\"]\n---\n");
        Assert.True(escapedOrder.IsSuccess);
        Assert.Equal(@"01-a\\", escapedOrder.Value.Order![0]);

        // 3. Standalone auto-detected Markdown quiz + plain paragraph + inline layoutOverrides
        var standalone = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: "# Quick Auto Quiz\n\n:::mcq\nForce?\n- [x] Newton\n- [ ] Joule\n:::\n", WorkingDirectory: _fx.WorkDir));
        Assert.True(standalone.IsSuccess);
        Assert.Equal("quiz", standalone.Value.EntityKind);

        var inlineOv = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: "---\ntitle: Inline\nlayoutOverrides: { cardMaxWidthPx: 880 }\n---\n\nPlain paragraph.\n", WorkingDirectory: _fx.WorkDir));
        Assert.True(inlineOv.IsSuccess);

        // 4. Nested component asset confinement inside specRoot succeeds; escaping fails
        var specRoot = _fx.SubDir("nested_root");
        var nestedSub = Directory.CreateDirectory(Path.Combine(specRoot, "quizzes")).FullName;
        await TestAssets.WritePngAsync(Path.Combine(specRoot, "assets", "diagram.png"));
        await TestAssets.WriteMasksSidecarAsync(Path.Combine(specRoot, "assets", "diagram.masks.json"), """{"boxes":[{"label":"A","rect":[0.1,0.1,0.2,0.2]}]}""");
        await TestAssets.WritePngAsync(Path.Combine(_fx.TempRoot, "outside.png"));

        var stub = new StubSpecLoader();
        using var nestedSp = _fx.CreateProvider("appdata_nested", s => s.AddSingleton<IDeclarativeSpecLoader>(stub));
        var nestedApply = nestedSp.GetRequiredService<ApplySpecHandler>();

        stub.NextSpec = new DeclarativeApplySpec(
            "session", "Nested", null, null, null,
            [
                new DeclarativeComponentSpec("article", "Intro", null, [new DeclarativeArticleBlockSpec("image", null, "../assets/diagram.png")], null, null, SourceDirectory: nestedSub),
                new DeclarativeComponentSpec("quiz", "Q", "mixed", null,
                    [new JsonQuestionSpec("relabel", null, null, false, [], null, null, null, null, null, "rel-n",
                        Phase1PayloadCodec.Serialize(new RelabelImagePayload("../assets/diagram.png", "Nested", "bank", "solid", [], [], true, false, null, SourceMasksPath: "../assets/diagram.masks.json")))],
                    null, SourceDirectory: nestedSub)
            ],
            null, null, null, AssetBaseDirectory: specRoot);
        Assert.True((await nestedApply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: "stub", WorkingDirectory: _fx.WorkDir))).IsSuccess);

        stub.NextSpec = new DeclarativeApplySpec(
            "session", "Escape", null, null, null,
            [new DeclarativeComponentSpec("article", "Esc", null, [new DeclarativeArticleBlockSpec("image", null, "../../outside.png")], null, null, SourceDirectory: nestedSub)],
            null, null, null, AssetBaseDirectory: specRoot);
        var escRes = await nestedApply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: "stub", WorkingDirectory: _fx.WorkDir));
        Assert.False(escRes.IsSuccess);
        Assert.Equal("Asset.InvalidPath", escRes.Error!.Code);
    }

    [Theory]
    [MemberData(nameof(InvalidSpecCases))]
    public async Task LoaderAndApply_RejectMalformedMarkdownJsonAndTraversalSpecs_WithExactErrorCodes(
        string inlineText, string expectedCode, string? expectedHintSubstr)
    {
        using var sp = _fx.CreateProvider("appdata_err_matrix");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();

        var res = await apply.HandleAsync(new ApplySpecCommand(null, null, InlineJson: inlineText, WorkingDirectory: _fx.WorkDir));
        Assert.False(res.IsSuccess, $"Expected failure '{expectedCode}' but succeeded.");
        Assert.Equal(expectedCode, res.Error!.Code);
        if (expectedHintSubstr is not null)
            Assert.Contains(expectedHintSubstr, res.Error.Hint ?? string.Empty);
    }

    public static IEnumerable<object?[]> InvalidSpecCases()
    {
        yield return [":::mcq{multy=true}\nPrompt?\n- [x] A\n- [ ] B\n:::", "Markdown.InvalidDirectiveAttr", "multi"];
        yield return [":::table{stikyFirstCol=true}\n| A | B |\n|---|---|\n| 1 | 2 |\n:::", "Markdown.InvalidDirectiveAttr", "stickyFirstCol"];
        yield return [":::table{hideableCols=\"1,9\"}\n| A | B | C |\n|---|---|---|\n| 1 | 2 | 3 |\n:::", "Markdown.InvalidDirectiveAttr", null];
        yield return [":::table{title=\"x\"} extra\n| A | B |\n|---|---|\n| 1 | 2 |\n:::", "Markdown.InvalidDirectiveAttr", null];
        yield return ["---\nkind: quiz\n---\n:::diff{mode=\"bogus\"}\n| A | B |\n|---|---|\n| 1 | 2 |\n:::", "Markdown.InvalidDirectiveAttr", null];
        yield return [":::mcq\nPrompt?\n- [x] A\n- [ ] B\n:::explain\nNope\n:::", "Markdown.UnknownSubSection", null];
        yield return ["Regular prose.\n\n:::mcq\nPrompt?\n- [x] A\n- [ ] B\n:::", "Markdown.MixedContentNotAllowed", null];
        yield return ["---\nkind: article\n---\n:::mcq\nPrompt?\n- [x] A\n- [ ] B\n:::", "Markdown.InvalidDirectiveForKind", null];
        yield return ["---\nkind: quiz\n---\nStray prose.\n\n:::mcq\nPrompt?\n- [x] A\n- [ ] B\n:::", "Markdown.UnexpectedContentInQuiz", null];
        yield return [":::mcq\nPrompt?\n- [x] A\n- [ ] B\n", "Markdown.UnclosedDirective", null];
        yield return ["---\nkind: quiz\n---\n:::mcq\nStem?\n- [x] A\n- [ ] B\n:::diff{mode=\"fill\"}\n| A | B |\n|---|---|\n| {{1}} | 2 |\n:::", "Markdown.UnclosedDirective", null];
        yield return [":::pearl\nMissing close\n", "Markdown.UnclosedCallout", null];
        yield return [":::tip\nHi\n:::\n", "Markdown.UnknownFence", null];
        yield return ["---\ntitle: Empty\n---\n   \n", "Markdown.EmptyBody", null];
        yield return ["---\nlayoutOverrides: invalid_scalar\n---\nBody\n", "Markdown.InvalidFrontmatter", null];
        yield return ["---\norder: not-a-list\n---\nBody\n", "Markdown.InvalidFrontmatter", null];
        yield return [":::table\n| A | B |\n|---|---|\n:::", "Markdown.InvalidTable", null];
        yield return [":::table\n| A | B |\n|---|---|\n| 1 | 2 | 3 |\n:::", "Markdown.InvalidTable", null];
        yield return ["---\nkind: quiz\n---\n:::assertion-reason{answer=\"both-false\" scheme=\"4opt\"}\nA: One\nR: Two\n:::", "Markdown.InvalidAssertionReason", null];
        yield return ["---\nkind: quiz\n---\n:::assertion-reason{answer=\"both-explain\"}\nA: First\nA: Second\nR: R\n:::", "Markdown.InvalidAssertionReason", null];
        yield return ["---\nkind: quiz\n---\n:::spot-error{noError=true allowNoError=false}\nIn [acidosis], pH is [low].\n:::", "Markdown.InvalidSpotError", null];
        yield return ["---\nkind: quiz\n---\n:::diff{mode=\"fill\"}\n| A | B |\n|---|---|\n| 1 | 2 |\n:::", "Markdown.InvalidDiff", null];
        yield return ["---\nkind: quiz\n---\n:::mechanism\n1. First\n2. Second\n2. Dup\n:::", "Markdown.InvalidMechanism", null];
        yield return ["---\nkind: quiz\n---\n:::case{title=\"Outer\"}\nStem\n::::case{title=\"Inner\"}\nNested\n::::\n:::", "Markdown.InvalidCase", null];
        yield return ["""{"kind":"quiz","quizType":"mixed","questions":[{"type":"case","stem":"Outer","questions":[{"type":"mcq","text":"Q1","options":[{"text":"A","isCorrect":true},{"text":"B"}]},{"type":"case","stem":"Inner","questions":[]}]}]}""", "Case.NestedCaseNotAllowed", null];
        yield return ["""{"kind":"session","layoutOverrides":{"cardMaxWidthPx":"900"},"components":[{"kind":"article","blocks":[{"kind":"text","text":"Hi"}]}]}""", "Json.InvalidFieldForType", null];
        yield return ["""{"kind":"quiz","layoutOverrides":{"optionColumns":"2"},"questions":[{"type":"flashcard","front":{"text":"Q"},"back":{"text":"A"}}]}""", "Json.InvalidFieldForType", null];
        yield return ["""{"kind":"article","blocks":[{"kind":"text","text":"Hi"}],"components":[{"kind":"article","blocks":[{"kind":"text","text":"Other"}]}]}""", "Json.InvalidFieldForType", null];
        yield return ["""{"kind":"quiz","quizType":"mixed","questions":[{"type":"mechanism","placeholder":"nope","steps":[{"orderGroup":1,"text":"A"},{"orderGroup":2,"text":"B"}]}]}""", "Json.InvalidFieldForType", null];
        yield return ["""{"kind":"quiz","quizType":"mixed","questions":[{"type":"relabel","placeholder":"nope","imagePath":"img.png","boxes":[{"label":"A","rect":[0.1,0.1,0.2,0.2]}]}]}""", "Json.InvalidFieldForType", null];
        yield return ["""{"kind":"quiz","questions":[{"type":"mcq","text":"Q?","options":[null]}]}""", "Json.InvalidQuestion", null];
        yield return ["""{"kind":"quiz","questions":[{"type":"mcq","text":"Q?","options":[{"text":"A","correct":true}]}]}""", "Json.ParseError", "isCorrect"];
        yield return ["""{"kind":"article","blocks":[{"kind":"table","headers":["A",null],"rows":[["1","2"]]}]}""", "Json.InvalidSpec", null];
        yield return ["""{"kind":"article","blocks":[{"kind":"table","headers":["A","B"],"rows":[["1",null]]}]}""", "Json.InvalidSpec", null];
        yield return ["""{"kind":"quiz","quizType":"mcq","questions":[{"type":"flashcard","front":{"text":"F"},"back":{"text":"B"}}]}""", "Quiz.QuestionTypeMismatch", null];
    }

    [Fact]
    public async Task DirectoryAndSidecarGuards_CatchConflictsAndTraversal()
    {
        using var sp = _fx.CreateProvider("appdata_dir_guards");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var loader = scope.ServiceProvider.GetRequiredService<IDeclarativeSpecLoader>();

        var srcDir = _fx.SubDir("guard_src");
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "fig.png"));
        await TestAssets.WriteMasksSidecarAsync(Path.Combine(srcDir, "fig.masks.json"), """{"boxes":[{"label":"Aorta","rect":[0.1,0.1,0.2,0.2]}]}""");
        await TestAssets.WritePngAsync(Path.Combine(_fx.TempRoot, "secret.png"));
        await File.WriteAllTextAsync(Path.Combine(_fx.TempRoot, "secret.json"), """{"boxes":[{"label":"X","rect":[0.1,0.1,0.2,0.2]}]}""");

        // Conflict: both masks sidecar & inline boxes (Markdown & JSON)
        var bothMd = Path.Combine(srcDir, "both.md");
        await File.WriteAllTextAsync(bothMd, "---\nkind: quiz\nquizType: mixed\n---\n:::relabel{image=\"fig.png\" masks=\"fig.masks.json\"}\nboxes:\n  - label: \"A\"\n    rect: [0.1, 0.1, 0.2, 0.2]\n:::\n");
        Assert.Equal("Markdown.InvalidRelabel", (await apply.HandleAsync(new ApplySpecCommand(null, bothMd, WorkingDirectory: _fx.WorkDir))).Error!.Code);

        var bothJson = Path.Combine(srcDir, "both.json");
        await File.WriteAllTextAsync(bothJson, """{"kind":"quiz","quizType":"mixed","questions":[{"type":"relabel","imagePath":"fig.png","masksPath":"fig.masks.json","boxes":[{"label":"A","rect":[0.1,0.1,0.2,0.2]}]}]}""");
        Assert.Equal("Json.InvalidQuestion", (await apply.HandleAsync(new ApplySpecCommand(null, bothJson, WorkingDirectory: _fx.WorkDir))).Error!.Code);

        // include matched 0 boxes
        var noMatchMd = Path.Combine(srcDir, "nomatch.md");
        await File.WriteAllTextAsync(noMatchMd, "---\nkind: quiz\nquizType: mixed\n---\n:::relabel{image=\"fig.png\" masks=\"fig.masks.json\"}\ninclude: [\"Missing\"]\n:::\n");
        Assert.Equal("Relabel.IncludeMatchedNoBoxes", (await apply.HandleAsync(new ApplySpecCommand(null, noMatchMd, WorkingDirectory: _fx.WorkDir))).Error!.Code);

        // Unmatched hotspot
        var missHsMd = Path.Combine(srcDir, "miss_hs.md");
        await File.WriteAllTextAsync(missHsMd, "---\nkind: article\n---\n:::diagram-explore{image=\"fig.png\" masks=\"fig.masks.json\"}\n- Missing: Note\n:::\n");
        Assert.Equal("DiagramExplore.UnmatchedHotspot", (await apply.HandleAsync(new ApplySpecCommand(null, missHsMd, WorkingDirectory: _fx.WorkDir))).Error!.Code);

        // Traversal escapes
        var escImgMd = Path.Combine(srcDir, "esc_img.md");
        await File.WriteAllTextAsync(escImgMd, "---\nkind: quiz\nquizType: mixed\n---\n:::relabel{image=\"../secret.png\" masks=\"fig.masks.json\"}\nprompt: Esc\n:::\n");
        Assert.Equal("Asset.InvalidPath", (await apply.HandleAsync(new ApplySpecCommand(null, escImgMd, WorkingDirectory: _fx.WorkDir))).Error!.Code);

        var escMaskMd = Path.Combine(srcDir, "esc_mask.md");
        await File.WriteAllTextAsync(escMaskMd, "---\nkind: quiz\nquizType: mixed\n---\n:::relabel{image=\"fig.png\" masks=\"../secret.json\"}\nprompt: Esc\n:::\n");
        Assert.Equal("Asset.InvalidPath", (await apply.HandleAsync(new ApplySpecCommand(null, escMaskMd, WorkingDirectory: _fx.WorkDir))).Error!.Code);

        var escJson = Path.Combine(srcDir, "esc.json");
        await File.WriteAllTextAsync(escJson, """{"kind":"quiz","quizType":"mixed","questions":[{"type":"mcq","text":"Q?","options":[{"text":"A","imagePath":"../secret.png","isCorrect":true},{"text":"B"}]}]}""");
        Assert.Equal("Asset.InvalidPath", (await apply.HandleAsync(new ApplySpecCommand(null, escJson, WorkingDirectory: _fx.WorkDir))).Error!.Code);

        // Directory manifest errors (_session.md + session.json conflict, bad kind, body in _session.md, layoutOverrides in _session.md, mixed blocks/questions)
        var d1 = _fx.SubDir("d_conflict");
        await File.WriteAllTextAsync(Path.Combine(d1, "session.json"), """{"kind":"session","name":"A"}""");
        await File.WriteAllTextAsync(Path.Combine(d1, "_session.md"), "---\nname: B\n---\n");
        await File.WriteAllTextAsync(Path.Combine(d1, "01.md"), "Body");
        Assert.Equal("Json.InvalidSpec", (await loader.LoadAsync(d1)).Error!.Code);

        var d2 = _fx.SubDir("d_bad_sm");
        await File.WriteAllTextAsync(Path.Combine(d2, "_session.md"), "---\nkind: quiz\nname: Wrong\n---\n");
        await File.WriteAllTextAsync(Path.Combine(d2, "01.md"), "Body");
        Assert.Equal("Markdown.InvalidFrontmatter", (await loader.LoadAsync(d2)).Error!.Code);

        var d3 = _fx.SubDir("d_body_sm");
        await File.WriteAllTextAsync(Path.Combine(d3, "_session.md"), "---\nkind: session\nname: B\n---\n\nUnexpected body.\n");
        await File.WriteAllTextAsync(Path.Combine(d3, "01.md"), "Body");
        Assert.Equal("Json.InvalidFieldForType", (await loader.LoadAsync(d3)).Error!.Code);

        var d4 = _fx.SubDir("d_ov_sm");
        await File.WriteAllTextAsync(Path.Combine(d4, "_session.md"), "---\nkind: session\nlayoutOverrides:\n  cardMaxWidthPx: 900\n---\n");
        await File.WriteAllTextAsync(Path.Combine(d4, "01.md"), "Body");
        Assert.Equal("Json.InvalidFieldForType", (await loader.LoadAsync(d4)).Error!.Code);

        var d5 = _fx.SubDir("d_bad_sjson_kind");
        await File.WriteAllTextAsync(Path.Combine(d5, "session.json"), """{"kind":"article"}""");
        await File.WriteAllTextAsync(Path.Combine(d5, "01.md"), "Body");
        Assert.False((await loader.LoadAsync(d5)).IsSuccess);

        var d6 = _fx.SubDir("d_bad_sjson_blocks");
        await File.WriteAllTextAsync(Path.Combine(d6, "session.json"), """{"kind":"session","blocks":[{"kind":"text","text":"nope"}]}""");
        await File.WriteAllTextAsync(Path.Combine(d6, "01.md"), "Body");
        Assert.Equal("Json.InvalidFieldForType", (await loader.LoadAsync(d6)).Error!.Code);

        var d7 = _fx.SubDir("d_mixed_comp");
        await File.WriteAllTextAsync(Path.Combine(d7, "01-mixed.json"), """{"kind":"article","blocks":[{"kind":"text","text":"Hi"}],"questions":[{"type":"mcq","text":"Q","options":[{"text":"A","isCorrect":true},{"text":"B"}]}]}""");
        Assert.Equal("Json.InvalidFieldForType", (await loader.LoadAsync(d7)).Error!.Code);
    }
}
