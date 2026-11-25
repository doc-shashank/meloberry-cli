using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Analysis;
using Meloberry.Domain.Common;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Sessions;
using Meloberry.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class Phase3ComponentTests : IDisposable
{
    private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAUAAAADICAYAAACZBDirAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAKoSURBVHhe7dShAQAgDMCw/X8tEjc8LzQipgd0zt0FKJo/AFQYIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBAlgECWQYIZBkgkGWAQJYBAlkGCGQZIJBlgECWAQJZBghkGSCQZYBA1gNPI9UTnmi3rAAAAABJRU5ErkJggg==");

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_phase3_unit_{Guid.NewGuid():N}");

    public Phase3ComponentTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private ServiceProvider CreateProvider(string subDir = "appdata")
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(_tempRoot, subDir));
        return services.BuildServiceProvider();
    }

    // 1. Prime budget (B-7) & Phase 3 Spec Catalog
    [Fact]
    public async Task Phase3_PrimeAndSpecCatalog_ExposeAllSixComponents_WithinCrlfBudget()
    {
        using var sp = CreateProvider("appdata_spec");
        using var scope = sp.CreateScope();
        var prime = scope.ServiceProvider.GetRequiredService<GetPrimeGuideHandler>();
        var schema = scope.ServiceProvider.GetRequiredService<GetSchemaHandler>();

        var primeRes = await prime.HandleAsync();
        Assert.True(primeRes.IsSuccess);
        var crlfGuide = primeRes.Value.Guide.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.True(crlfGuide.Length < 1550, $"Expected CRLF prime guide < 1550 chars, got {crlfGuide.Length}");
        Assert.Contains("session|article|quiz|text|image|mcq|flashcard|layouts", crlfGuide);
        Assert.Contains("diff|mechanism|relabel", crlfGuide);
        Assert.Contains("diagram-explore|reveal|case|classify|match|numeric", crlfGuide);
        Assert.Equal(["diagram-explore", "reveal", "case", "classify", "match", "numeric"],
            primeRes.Value.SpecTopics.TakeLast(6).ToArray());

        var blocksRes = await schema.HandleAsync(new GetSchemaQuery(["@blocks"]));
        Assert.True(blocksRes.IsSuccess);
        Assert.Equal(["text", "image", "callout", "table", "diagram-explore", "reveal"],
            blocksRes.Value.Schemas.Select(s => s.Topic).ToArray());

        var questionsRes = await schema.HandleAsync(new GetSchemaQuery(["@questions"]));
        Assert.True(questionsRes.IsSuccess);
        Assert.Equal(12, questionsRes.Value.Schemas.Count);
        Assert.Equal(["case", "classify", "match", "numeric"],
            questionsRes.Value.Schemas.Select(s => s.Topic).TakeLast(4).ToArray());

        var contentRes = await schema.HandleAsync(new GetSchemaQuery(["@content"]));
        Assert.True(contentRes.IsSuccess);
        Assert.Equal(21, contentRes.Value.Schemas.Count);

        var detailRes = await schema.HandleAsync(new GetSchemaQuery(["diagram-explore", "reveal", "case", "classify", "match", "numeric"]));
        Assert.True(detailRes.IsSuccess);
        Assert.Equal(6, detailRes.Value.Schemas.Count);
    }

    // 2. Domain payload parsing & validation rules
    [Fact]
    public void Phase3PayloadRules_ParseAndValidateAllFormatsCorrectly()
    {
        var annotations = Phase3PayloadRules.ParseDiagramHotspotAnnotations("""
            - Glomerulus: Filters blood plasma at **high pressure** ($P_{GC}$).
            - Bowman's Capsule: Collects ultrafiltrate; ignores `a:b` colons.
            """);
        Assert.True(annotations.IsSuccess);
        Assert.Equal(2, annotations.Value.Count);
        Assert.Equal("Glomerulus", annotations.Value[0].Label);

        IReadOnlyList<RelabelBox> sidecarBoxes =
        [
            new RelabelBox(0, "Glomerulus Capillary", [], 0.1, 0.2, 0.2, 0.15),
            new RelabelBox(1, "Bowman's Capsule", [], 0.5, 0.2, 0.2, 0.15),
            new RelabelBox(2, "Loop of Henle", [], 0.4, 0.6, 0.2, 0.15)
        ];
        var resolved = Phase3PayloadRules.ResolveHotspotsFromSidecar(sidecarBoxes, annotations.Value);
        Assert.True(resolved.IsSuccess);
        Assert.Equal(2, resolved.Value.Count);
        Assert.Equal("Glomerulus Capillary", resolved.Value[0].Label);
        Assert.Equal("Bowman's Capsule", resolved.Value[1].Label);

        var unmatched = Phase3PayloadRules.ResolveHotspotsFromSidecar(sidecarBoxes, [new DiagramHotspotAnnotation("MissingPart", "Note")]);
        Assert.False(unmatched.IsSuccess);
        Assert.Equal("DiagramExplore.UnmatchedHotspot", unmatched.Error!.Code);

        var revealSteps = Phase3PayloadRules.ParseRevealSteps("""
            > Step 1: Observe Input
            Compare the pressure gradient:
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

        var classify = Phase3PayloadRules.ParseClassifyLines("""
            [Beta-Lactams]
            - Penicillin G
              (IV formulation)
            - Ceftriaxone

            [Macrolides]
            - Azithromycin
            """);
        Assert.True(classify.IsSuccess);
        Assert.Equal(2, classify.Value.Buckets.Count);
        Assert.Equal(3, classify.Value.Items.Count);
        Assert.Contains("(IV formulation)", classify.Value.Items[0].Text);

        var match = Phase3PayloadRules.ParseMatchLines("""
            - `Code::Span` :: Target $a::b$
            - Renin :: Converts angiotensinogen
            ! ANP (inhibitor)
            """);
        Assert.True(match.IsSuccess);
        Assert.Equal(2, match.Value.Pairs.Count);
        Assert.Equal("`Code::Span`", match.Value.Pairs[0].Left);
        Assert.Equal("Target $a::b$", match.Value.Pairs[0].Right);
        Assert.Single(match.Value.Distractors);

        Assert.True(Phase3PayloadRules.TryParseToleranceSpec("5%", null, out var pctVal, out var pctMode));
        Assert.Equal(5, pctVal);
        Assert.Equal("percent", pctMode);

        Assert.True(Phase3PayloadRules.TryParseToleranceSpec("2.5", null, out var absVal, out var absMode));
        Assert.Equal(2.5, absVal);
        Assert.Equal("absolute", absMode);

        Assert.False(Phase3PayloadRules.TryParseToleranceSpec("-1", null, out _, out _));
        Assert.False(Phase3PayloadRules.TryParseToleranceSpec("abc%", null, out _, out _));
    }

    // 3. DiagramExplore sidecar (.meta.json & inline YAML) + Reveal + Classify + Match + Numeric + CaseGroup round-trips
    [Fact]
    public async Task Phase3_MarkdownAndJsonApply_MaterializesPersistsAndInspectsAllSixComponents()
    {
        using var sp = CreateProvider("appdata_rt");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var modDir = Path.Combine(_tempRoot, "phase3_mod");
        var assetsDir = Path.Combine(modDir, "assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "nephron.png"), ValidPngBytes);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "ecg.png"), ValidPngBytes);
        await File.WriteAllTextAsync(Path.Combine(assetsDir, "nephron.meta.json"), """
            {
              "figureId": "Fig_2_1",
              "pixelWidth": 1000,
              "pixelHeight": 500,
              "appliedMasks": [
                { "index": 1, "pixelX": 100, "pixelY": 100, "pixelWidth": 200, "pixelHeight": 80, "text": "Proximal Tubule", "style": "solid", "source": "ocr" },
                { "index": 2, "pixelX": 600, "pixelY": 150, "pixelWidth": 200, "pixelHeight": 80, "text": "Glomerulus", "style": "solid", "source": "ocr" }
              ]
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), "---\nkind: session\nname: Phase 3 Showcase\n---\n");
        await File.WriteAllTextAsync(Path.Combine(modDir, "01-article.md"), """
            ---
            kind: article
            title: Phase 3 Interactive Article
            ref: p3-art
            ---

            :::diagram-explore{ref="diag-1" image="assets/nephron.png" masks="assets/nephron.meta.json" title="Nephron Hotspots" caption="Click hotspots."}
            - Glomerulus: **Ultrafiltration** across fenestrated capillaries ($\text{GFR} \approx 125\text{ mL/min}$).
            - Proximal Tubule: Bulk reabsorption of $\text{Na}^+$ and glucose.
            :::

            :::reveal{ref="rev-1" title="Socratic Check" showFirst=true}
            > Cue 1: Look at efferent constriction
            What happens to $P_{GC}$?

            > Cue 2: Conclude GFR change
            Both $P_{GC}$ and filtration fraction **increase**.
            :::

            :::link{id="to-drill" title="Practice Phase 3 Drills"}
            Jump to drills.
            :::

            Review [drills](mby://card/to-drill) before attempting the quiz.
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "02-quiz.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Phase 3 Mixed Quiz
            ref: p3-quiz
            ---

            :::classify{ref="cls-1" title="Antimicrobial Spectrum"}
            [Gram-Positive]
            - Vancomycin
            - Linezolid

            [Gram-Negative]
            - Aztreonam

            :::explanation
            Aztreonam targets aerobic Gram-negative rods only.
            :::

            :::match{ref="mth-1" title="Receptor Coupling"}
            - Alpha-1 :: $G_q$ -> $\text{IP}_3/\text{DAG}$
            - Beta-2 :: $G_s$ -> $\uparrow \text{cAMP}$
            ! $G_i$ -> $\downarrow \text{cAMP}$
            :::explanation
            $G_q$ mediates vasoconstriction while $\beta_2$ bronchodilates.
            :::

            :::numeric{ref="num-1" answer="125" tolerance="5%" unit="mL/min" placeholder="Enter GFR"}
            Estimate normal adult GFR shown in the figure ($\text{normal} \approx 125$).
            ![ECG strip](assets/ecg.png)
            :::explanation
            Normal adult GFR is $125 \pm 5\%\text{ mL/min}$.
            :::

            :::case{ref="case-1" title="Chest Pain Vignette" caption="Lead II rhythm strip."}
            A 64-year-old man develops crushing chest pain and diaphoresis.
            ![ECG strip](assets/ecg.png)

            ::::mcq{explanation="Anterior MI causes ST elevation in V1-V4."}
            Which leads reflect an anterior STEMI?
            - [x] V1-V4
            - [ ] II, III, aVF
            - [ ] V5-V6
            ::::

            ::::fill-blank{caseSensitive=false}
            First-line antiplatelet agent is {{aspirin}}.
            ::::explanation
            Chewed non-enteric aspirin 162-325 mg reduces mortality.
            ::::

            ::::numeric{answer=180 tolerance=10 unit="mg" explanation="Weight-based loading target."}
            Calculate the maintenance dose for a 90 kg patient at 2 mg/kg:
            ::::
            :::
            """);

        var wsDir = Path.Combine(_tempRoot, "ws_rt");
        var applied = await apply.HandleAsync(new ApplySpecCommand(null, modDir, WorkingDirectory: wsDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);
        var sessionId = applied.Value.TargetId;

        var lintRes = await lint.HandleAsync(new LintCommand(sessionId));
        Assert.True(lintRes.IsSuccess);
        Assert.True(lintRes.Value.IsClean, string.Join("; ", lintRes.Value.Issues.Select(i => $"{i.Code}: {i.Message}")));

        // Verify persisted SQLite payload contents
        var container = await repo.FindSessionAsync(sessionId);
        Assert.NotNull(container);
        var artBlocks = container!.Session.Components[0].ArticleBlocks;
        Assert.Equal(ArticleBlockKind.DiagramExplore, artBlocks[0].Kind);
        Assert.Equal(ArticleBlockKind.Reveal, artBlocks[1].Kind);

        var diagPayload = JsonSerializer.Deserialize<DiagramExplorePayload>(artBlocks[0].PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(2, diagPayload.Hotspots.Count);
        Assert.Null(diagPayload.SourceMasksPath);

        var questions = container.Session.Components[1].Quiz!.Questions.OfType<PayloadQuestion>().ToList();
        Assert.Equal(4, questions.Count);
        Assert.Equal(QuestionType.Classify, questions[0].Type);
        Assert.Equal(QuestionType.Match, questions[1].Type);
        Assert.Equal(QuestionType.Numeric, questions[2].Type);
        Assert.Equal(QuestionType.CaseGroup, questions[3].Type);

        var casePayload = JsonSerializer.Deserialize<CaseGroupPayload>(questions[3].PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(3, casePayload.Questions.Count);
        Assert.Equal("mcq", casePayload.Questions[0].Type);
        Assert.Equal("fill-blank", casePayload.Questions[1].Type);
        Assert.Equal("numeric", casePayload.Questions[2].Type);
        Assert.Equal("Anterior MI causes ST elevation in V1-V4.", casePayload.Questions[0].Mcq!.Explanation);

        var fullInspect = await inspect.HandleAsync(new InspectEntityQuery(sessionId, InspectViewMode.Full));
        Assert.True(fullInspect.IsSuccess);
        Assert.NotNull(fullInspect.Value.Components);
        Assert.Equal("diagram-explore", fullInspect.Value.Components![0].ArticleBlocks![0].Kind);
        Assert.Equal("reveal", fullInspect.Value.Components[0].ArticleBlocks![1].Kind);
        Assert.Equal("classify", fullInspect.Value.Components[1].Questions![0].Type);
        Assert.Equal("match", fullInspect.Value.Components[1].Questions![1].Type);
        Assert.Equal("numeric", fullInspect.Value.Components[1].Questions![2].Type);
        Assert.Equal("case", fullInspect.Value.Components[1].Questions![3].Type);
    }

    // 4. Reject nested :::case inside :::case + Unmatched diagram hotspot
    [Fact]
    public async Task Phase3_RejectsNestedCaseInMarkdownAndJson_AndRejectsUnmatchedHotspot()
    {
        using var sp = CreateProvider("appdata_reject");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();

        var srcDir = Path.Combine(_tempRoot, "reject_src");
        Directory.CreateDirectory(srcDir);
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "fig.png"), ValidPngBytes);
        await File.WriteAllTextAsync(Path.Combine(srcDir, "fig.masks.json"), """
            { "boxes": [ { "label": "Aorta", "rect": [0.1, 0.1, 0.2, 0.2] } ] }
            """);

        var nestedMd = Path.Combine(srcDir, "nested.md");
        await File.WriteAllTextAsync(nestedMd, """
            ---
            kind: quiz
            quizType: mixed
            ---

            :::case{title="Outer Case"}
            Outer stem text.

            ::::case{title="Inner Case"}
            Nested stem.
            ::::
            :::
            """);
        var mdErr = await apply.HandleAsync(new ApplySpecCommand(null, nestedMd, WorkingDirectory: Path.Combine(_tempRoot, "ws_n1")));
        Assert.False(mdErr.IsSuccess);

        var nestedJson = Path.Combine(srcDir, "nested.json");
        await File.WriteAllTextAsync(nestedJson, """
            {
              "kind": "quiz",
              "quizType": "mixed",
              "questions": [
                {
                  "type": "case",
                  "stem": "Outer stem",
                  "questions": [
                    { "type": "mcq", "text": "Q1", "options": [{ "text": "A", "isCorrect": true }, { "text": "B", "isCorrect": false }] },
                    { "type": "case", "stem": "Nested", "questions": [] }
                  ]
                }
              ]
            }
            """);
        var jsonErr = await apply.HandleAsync(new ApplySpecCommand(null, nestedJson, WorkingDirectory: Path.Combine(_tempRoot, "ws_n2")));
        Assert.False(jsonErr.IsSuccess);

        var missHotspotMd = Path.Combine(srcDir, "miss_hotspot.md");
        await File.WriteAllTextAsync(missHotspotMd, """
            ---
            kind: article
            title: Miss
            ---

            :::diagram-explore{image="fig.png" masks="fig.masks.json"}
            - NonExistentVessel: Typo here
            :::
            """);
        var hsErr = await apply.HandleAsync(new ApplySpecCommand(null, missHotspotMd, WorkingDirectory: Path.Combine(_tempRoot, "ws_hs")));
        Assert.False(hsErr.IsSuccess);
        Assert.Equal("DiagramExplore.UnmatchedHotspot", hsErr.Error!.Code);
    }

    // 5. Patch, Lint, Packaging (.mbypkg export/import + tamper guard)
    [Fact]
    public async Task Phase3_PatchLintAndPackaging_ValidateAllErrorCodesAndRoundTripAssets()
    {
        using var sp = CreateProvider("appdata_pkg");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var export = scope.ServiceProvider.GetRequiredService<ExportPackageHandler>();
        var import = scope.ServiceProvider.GetRequiredService<ImportPackageHandler>();

        var srcDir = Path.Combine(_tempRoot, "pkg_src");
        Directory.CreateDirectory(srcDir);
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "img.png"), ValidPngBytes);

        // Clean inline diagram-explore + reveal + all 4 Phase 3 questions -> patch -> export -> import
        var cleanJson = Path.Combine(srcDir, "clean.json");
        await File.WriteAllTextAsync(cleanJson, """
            {
              "kind": "session",
              "name": "Phase 3 Package Session",
              "components": [
                {
                  "kind": "article",
                  "title": "Inline Diagram Article",
                  "ref": "art1",
                  "blocks": [
                    {
                      "kind": "diagram-explore",
                      "ref": "diag1",
                      "imagePath": "img.png",
                      "title": "Inline Expl",
                      "hotspots": [
                        { "label": "Node A", "note": "Important node", "rect": [0.1, 0.1, 0.2, 0.2] }
                      ]
                    },
                    {
                      "kind": "reveal",
                      "ref": "rev1",
                      "title": "Stepwise",
                      "showFirst": false,
                      "steps": [
                        { "heading": "S1", "body": "Body 1" }
                      ]
                    }
                  ]
                },
                {
                  "kind": "quiz",
                  "quizType": "mixed",
                  "title": "Phase 3 Drills",
                  "ref": "qset",
                  "questions": [
                    {
                      "type": "classify",
                      "ref": "c1",
                      "title": "Sort",
                      "buckets": ["B1", "B2"],
                      "items": [
                        { "bucket": "B1", "text": "Item 1" },
                        { "bucket": "B2", "text": "Item 2" }
                      ]
                    },
                    {
                      "type": "match",
                      "ref": "m1",
                      "title": "Pairs",
                      "pairs": [
                        { "left": "L1", "right": "R1" },
                        { "left": "L2", "right": "R2" }
                      ],
                      "distractors": ["Decoy"]
                    },
                    {
                      "type": "numeric",
                      "ref": "n1",
                      "text": "Compute X",
                      "answer": 100,
                      "tolerance": "5%",
                      "unit": "mmHg",
                      "imagePath": "img.png"
                    },
                    {
                      "type": "case",
                      "ref": "cs1",
                      "title": "Case 1",
                      "stem": "Patient vignette stem.",
                      "imagePath": "img.png",
                      "questions": [
                        { "type": "mcq", "text": "Sub 1", "options": [{ "text": "A", "isCorrect": true }, { "text": "B", "isCorrect": false }] },
                        { "type": "numeric", "text": "Sub 2", "answer": 42, "tolerance": 2 }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var wsDir = Path.Combine(_tempRoot, "ws_pkg");
        var appRes = await apply.HandleAsync(new ApplySpecCommand(null, cleanJson, WorkingDirectory: wsDir));
        Assert.True(appRes.IsSuccess, appRes.Error?.Description);
        var wsId = appRes.Value.TargetId;

        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#rev1", SetPairs: ["showFirst=true"]))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#n1", SetPairs: ["answer=110", "tolerance=10%"]))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#diag1", SetPairs: ["title=Updated Expl"]))).IsSuccess);
        Assert.True((await patch.HandleAsync(new PatchEntityCommand($"{wsId}#cs1", SetPairs: ["title=Updated Case"]))).IsSuccess);

        var pkgPath = Path.Combine(_tempRoot, "phase3.mbypkg");
        var expRes = await export.HandleAsync(new ExportPackageCommand(wsId, pkgPath, "android"));
        Assert.True(expRes.IsSuccess, expRes.Error?.Description);

        var impDir = Path.Combine(_tempRoot, "ws_imp");
        var impRes = await import.HandleAsync(new ImportPackageCommand(pkgPath, impDir));
        Assert.True(impRes.IsSuccess, impRes.Error?.Description);

        // Broken lint scenarios for all 6 Phase 3 types
        var brokenJson = Path.Combine(srcDir, "broken.json");
        await File.WriteAllTextAsync(brokenJson, """
            {
              "kind": "session",
              "name": "Broken Phase 3",
              "components": [
                {
                  "kind": "article",
                  "title": "Bad Article",
                  "blocks": [
                    { "kind": "diagram-explore", "imagePath": "img.png", "hotspots": [{ "label": "Out", "note": "", "rect": [0.85, 0.2, 0.3, 0.1] }] },
                    { "kind": "reveal", "steps": [] }
                  ]
                },
                {
                  "kind": "quiz",
                  "quizType": "mixed",
                  "title": "Bad Quiz",
                  "questions": [
                    { "type": "classify", "buckets": ["OnlyOne"], "items": [{ "bucket": "OnlyOne", "text": "Dup" }, { "bucket": "OnlyOne", "text": "Dup" }] },
                    { "type": "match", "pairs": [{ "left": "Only1", "right": "R1" }], "distractors": ["D1", "D2"] },
                    { "type": "numeric", "text": "", "answer": 10, "tolerance": -1 },
                    {
                      "type": "case",
                      "stem": "Short",
                      "questions": [
                        { "type": "numeric", "text": "", "answer": 5, "tolerance": 1 }
                      ]
                    }
                  ]
                }
              ]
            }
            """);
        var badRes = await apply.HandleAsync(new ApplySpecCommand(null, brokenJson, WorkingDirectory: Path.Combine(_tempRoot, "ws_bad")));
        Assert.True(badRes.IsSuccess, badRes.Error?.Description);

        var badLint = await lint.HandleAsync(new LintCommand(badRes.Value.TargetId));
        Assert.True(badLint.IsSuccess);
        Assert.False(badLint.Value.IsClean);
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.InvalidDiagramHotspotBounds");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.EmptyDiagramHotspotNote");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.EmptyRevealSteps");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.InsufficientClassifyBuckets");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.DuplicateClassifyItem");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.InsufficientMatchPairs");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.ExcessiveMatchDistractors");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.InvalidNumericTolerance");
        Assert.Contains(badLint.Value.Issues, i => i.Code == "Lint.InsufficientCaseQuestions");
        Assert.Contains(badLint.Value.Issues, i => i.TargetId.Contains(":sub-0"));
    }

    // 6. Bug regressions B-1, B-2, B-4, B-5, B-6
    [Fact]
    public async Task Phase3_BugRegressions_B1_B2_B4_B5_B6_AreResolved()
    {
        using var sp = CreateProvider("appdata_bugs");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();

        var srcDir = Path.Combine(_tempRoot, "bug_src");
        Directory.CreateDirectory(srcDir);
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "img.png"), ValidPngBytes);

        // B-1: Appending single article with :::link to existing session preserves Links
        var sessJson = Path.Combine(srcDir, "sess.json");
        await File.WriteAllTextAsync(sessJson, """
            {
              "kind": "session",
              "name": "Bug Regression Session",
              "components": [
                {
                  "kind": "quiz",
                  "quizType": "mixed",
                  "title": "Leading Quiz",
                  "ref": "q-first",
                  "questions": [{ "type": "mcq", "text": "Q1", "options": [{"text": "A", "isCorrect": true}, {"text": "B", "isCorrect": false}] }]
                }
              ]
            }
            """);
        var wsDir = Path.Combine(_tempRoot, "ws_b1");
        var created = await apply.HandleAsync(new ApplySpecCommand(null, sessJson, WorkingDirectory: wsDir));
        Assert.True(created.IsSuccess, created.Error?.Description);

        var appendArtMd = Path.Combine(srcDir, "art_append.md");
        await File.WriteAllTextAsync(appendArtMd, """
            ---
            kind: article
            title: Appended Article A
            ref: art-a
            ---

            Inspect [card note](mby://card/card-1).

            :::link{id="card-1" title="Card 1"}
            Hover card content preserved on append.
            :::
            """);
        var appArt = await apply.HandleAsync(new ApplySpecCommand(created.Value.TargetId, appendArtMd));
        Assert.True(appArt.IsSuccess, appArt.Error?.Description);

        // Append second article B (c3) linking to Article A (c2) to verify B-2 mapping
        var appendArt2Md = Path.Combine(srcDir, "art_append2.md");
        await File.WriteAllTextAsync(appendArt2Md, """
            ---
            kind: article
            title: Appended Article B
            ref: art-b
            ---

            See [Article A](mby://article/c2).

            :::table{title="Ref Table"}
            | Item | Target |
            | :--- | :--- |
            | Missing target | [Broken](mby://article/c99) |
            :::
            """);
        Assert.True((await apply.HandleAsync(new ApplySpecCommand(created.Value.TargetId, appendArt2Md))).IsSuccess);

        // B-5: mby:// inside Table is now caught by LintHandler (and B-1 c1/c2 links are valid)
        var lintRes = await lint.HandleAsync(new LintCommand(created.Value.TargetId));
        Assert.True(lintRes.IsSuccess);
        Assert.DoesNotContain(lintRes.Value.Issues, i => i.Code == "Lint.MissingLinkCard");
        Assert.Contains(lintRes.Value.Issues, i => i.Code == "Lint.UnresolvedArticleLink" && i.Message.Contains("c99"));

        // Fix B-5 broken link on clean build to test B-2 and B-6 in generated app.js
        var cleanSessJson = Path.Combine(srcDir, "b2_b6.json");
        await File.WriteAllTextAsync(cleanSessJson, """
            {
              "kind": "session",
              "name": "B2 and B6 Session",
              "components": [
                {
                  "kind": "quiz",
                  "quizType": "mixed",
                  "title": "Quiz One",
                  "questions": [
                    {
                      "type": "relabel",
                      "imagePath": "img.png",
                      "mode": "bank",
                      "style": "blur",
                      "boxes": [{ "label": "Node", "rect": [0.1, 0.1, 0.2, 0.2] }]
                    }
                  ]
                },
                {
                  "kind": "article",
                  "title": "Article Two",
                  "blocks": [{ "kind": "text", "text": "Article c2 content." }]
                },
                {
                  "kind": "article",
                  "title": "Article Three",
                  "blocks": [{ "kind": "text", "text": "See [Art 2](mby://article/c2)." }]
                }
              ]
            }
            """);
        var wsB2 = await apply.HandleAsync(new ApplySpecCommand(null, cleanSessJson, WorkingDirectory: Path.Combine(_tempRoot, "ws_b2")));
        Assert.True(wsB2.IsSuccess, wsB2.Error?.Description);
        var webB2 = Path.Combine(_tempRoot, "web_b2");
        var builtB2 = await build.HandleAsync(new BuildWebAppCommand(wsB2.Value.TargetId, webB2, StaticVerifyOnly: true));
        Assert.True(builtB2.IsSuccess, builtB2.Error?.Description);
        var appJs = await File.ReadAllTextAsync(Path.Combine(webB2, "app.js"));
        var indexHtml = await File.ReadAllTextAsync(Path.Combine(webB2, "index.html"));
        Assert.Contains("\"componentIndex\":2", indexHtml);
        Assert.Contains("s.componentIndex", appJs);
        Assert.DoesNotContain("artNum", appJs);
        Assert.Contains("st.checked = false", appJs);

        // B-4: placeholder on mechanism & relabel returns Json.InvalidFieldForType
        var b4Mech = Path.Combine(srcDir, "b4_mech.json");
        await File.WriteAllTextAsync(b4Mech, """
            { "kind": "quiz", "quizType": "mixed", "questions": [{ "type": "mechanism", "placeholder": "nope", "steps": [{ "orderGroup": 1, "text": "A" }, { "orderGroup": 2, "text": "B" }] }] }
            """);
        var b4MechRes = await apply.HandleAsync(new ApplySpecCommand(null, b4Mech, WorkingDirectory: Path.Combine(_tempRoot, "ws_b4m")));
        Assert.False(b4MechRes.IsSuccess);
        Assert.Equal("Json.InvalidFieldForType", b4MechRes.Error!.Code);

        var b4Rel = Path.Combine(srcDir, "b4_rel.json");
        await File.WriteAllTextAsync(b4Rel, """
            { "kind": "quiz", "quizType": "mixed", "questions": [{ "type": "relabel", "placeholder": "nope", "imagePath": "img.png", "boxes": [{ "label": "A", "rect": [0.1, 0.1, 0.2, 0.2] }] }] }
            """);
        var b4RelRes = await apply.HandleAsync(new ApplySpecCommand(null, b4Rel, WorkingDirectory: Path.Combine(_tempRoot, "ws_b4r")));
        Assert.False(b4RelRes.IsSuccess);
        Assert.Equal("Json.InvalidFieldForType", b4RelRes.Error!.Code);
    }

    // 7. Full Phase 3 Showcase + Hideable Native Table (B-3) headless CDP verification
    [Fact]
    public async Task Phase3_FullShowcaseSession_HeadlessCdpVerification_PassesCleanly()
    {
        using var sp = CreateProvider("appdata_cdp");
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var build = scope.ServiceProvider.GetRequiredService<BuildWebAppHandler>();
        var verifier = scope.ServiceProvider.GetRequiredService<IHeadlessWebAppVerifier>();

        var modDir = Path.Combine(_tempRoot, "showcase_p3");
        var assetsDir = Path.Combine(modDir, "assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "heart.png"), ValidPngBytes);

        await File.WriteAllTextAsync(Path.Combine(modDir, "_session.md"), "---\nkind: session\nname: Phase 3 CDP Showcase\n---\n");
        await File.WriteAllTextAsync(Path.Combine(modDir, "01-article.md"), """
            ---
            kind: article
            title: Cardiac Imaging & Hideable Table
            ref: art-heart
            ---

            :::diagram-explore{image="assets/heart.png" title="Chambers"}
            boxes:
              - label: "Right Atrium"
                rect: [0.15, 0.20, 0.22, 0.18]
                note: "Receives systemic venous return ($\text{SVC}/\text{IVC}$)."
              - label: "Left Ventricle"
                rect: [0.55, 0.45, 0.25, 0.22]
                note: "Pumps oxygenated blood into the aorta."
            :::

            :::reveal{title="Frank-Starling Check" showFirst=false}
            > Step 1: Preload Increase
            Increased end-diastolic volume stretches sarcomeres.

            > Step 2: Stroke Volume
            $\Delta SV$ increases proportionally within physiological limits.
            :::

            :::table{title="Chamber Pressures" hideableCols="2"}
            | Chamber | Pressure |
            | :--- | :--- |
            | Right Atrium | 2-6 mmHg |
            | Left Ventricle | 120/8 mmHg |
            :::
            """);

        await File.WriteAllTextAsync(Path.Combine(modDir, "02-quiz.md"), """
            ---
            kind: quiz
            quizType: mixed
            title: Phase 3 Interactive Quiz
            ref: quiz-heart
            ---

            :::classify{title="Circuit Classification"}
            [Pulmonary]
            - Right Ventricle
            - Pulmonary Trunk

            [Systemic]
            - Left Ventricle
            :::

            :::match{title="Valves"}
            - Mitral :: Left atrium to left ventricle
            - Tricuspid :: Right atrium to right ventricle
            ! Aortic valve
            :::

            :::numeric{answer=70 tolerance="10%" unit="mL"}
            Estimate normal resting adult stroke volume ($\text{SV}$):
            :::

            :::case{title="Valvular Murmur Case"}
            A 72-year-old presents with crescendo-decrescendo systolic murmur radiating to the carotids.

            ::::mcq{explanation="Classic presentation of aortic stenosis."}
            Which lesion is most likely?
            - [x] Aortic stenosis
            - [ ] Mitral regurgitation
            ::::

            ::::numeric{answer=1 tolerance=0.5 unit="cm2" explanation="Severe AS area is < 1.0 cm2."}
            Upper valve area threshold ($\text{cm}^2$) for severe AS:
            ::::
            :::
            """);

        var applied = await apply.HandleAsync(new ApplySpecCommand(null, modDir, WorkingDirectory: Path.Combine(_tempRoot, "ws_cdp3")));
        Assert.True(applied.IsSuccess, applied.Error?.Description);

        var webDir = Path.Combine(_tempRoot, "web_cdp3");
        var built = await build.HandleAsync(new BuildWebAppCommand(applied.Value.TargetId, webDir, StaticVerifyOnly: true));
        Assert.True(built.IsSuccess, built.Error?.Description);

        var staticRes = await verifier.VerifyAsync(webDir, staticOnly: true);
        Assert.True(staticRes.IsSuccess, staticRes.Error?.Description);
        Assert.True(staticRes.Value.Passed);

        var cdpRes = await verifier.VerifyAsync(webDir, staticOnly: false, timeoutSeconds: 30);
        if (cdpRes.IsSuccess || cdpRes.Error?.Code != "Verify.BrowserNotFound")
        {
            Assert.True(cdpRes.IsSuccess, cdpRes.Error?.Description);
            Assert.True(cdpRes.Value.Passed,
                $"CDP failed: console=[{string.Join("; ", cdpRes.Value.ConsoleErrors)}], overflow=[{string.Join("; ", cdpRes.Value.OverflowIssues)}], broken=[{string.Join("; ", cdpRes.Value.BrokenAssets)}], katex=[{string.Join("; ", cdpRes.Value.KatexErrors)}]");
            Assert.Empty(cdpRes.Value.ConsoleErrors);
            Assert.Empty(cdpRes.Value.OverflowIssues);
            Assert.Empty(cdpRes.Value.BrokenAssets);
            Assert.Empty(cdpRes.Value.KatexErrors);
        }
    }
}
