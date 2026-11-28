using System.Text.Json;
using System.Text.RegularExpressions;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Meloberry.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Meloberry.CliTests;

[Collection("CwdTests")]
public sealed partial class WrapperEditModeWorkflowTests : IDisposable
{
    private readonly WorkspaceTestFixture _fx = new("mby_wrapper_e2e");

    [GeneratedRegex(@"_[0-9a-f]{8}_[0-9a-f]{8}\.", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DoubleHashRegex();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task ExternalSqliteWorkspace_WrapperEditMode_RoundTripPreservesIdsAssetsAndBuildsCleanly()
    {
        var authorAppData = _fx.SubDir("author_appdata");
        var wrapperAppData = _fx.SubDir("wrapper_appdata");
        var externalStore = _fx.SubDir("external_store");
        var authorSrc = _fx.SubDir("author_src");
        var unrelatedCwd = _fx.SubDir("unrelated_cwd");

        await TestAssets.WritePngAsync(Path.Combine(authorSrc, "diagram.png"), [0x01, 0x02]);
        await TestAssets.WritePngAsync(Path.Combine(authorSrc, "card_keep.png"), [0x03, 0x04]);

        var initialSpecPath = Path.Combine(authorSrc, "session.json");
        await File.WriteAllTextAsync(initialSpecPath, """
        {
          "kind": "session",
          "name": "Renal Edit Session",
          "description": "Original description",
          "components": [
            {
              "kind": "article",
              "ref": "art-main",
              "title": "Nephron Basics",
              "links": [
                { "id": "card-keep", "title": "Loop Card", "text": "Original loop summary.", "imagePath": "card_keep.png" },
                { "id": "card-drop", "title": "Obsolete Card", "text": "To be removed." }
              ],
              "blocks": [
                { "kind": "text", "ref": "blk-text", "text": "See [Loop](mby://card/card-keep) and [Old](mby://card/card-drop)." },
                { "kind": "image", "ref": "blk-img", "imagePath": "diagram.png", "caption": "Original diagram", "alt": "Nephron" },
                {
                  "kind": "table",
                  "ref": "blk-tbl",
                  "title": "Diuretics",
                  "stickyFirstCol": true,
                  "hideableCols": [2],
                  "headers": ["Drug", "Site"],
                  "alignments": ["left", "left"],
                  "rows": [["Furosemide", "TAL"]]
                }
              ]
            },
            {
              "kind": "quiz",
              "ref": "quiz-main",
              "title": "Renal Check",
              "quizType": "mixed",
              "questions": [
                {
                  "type": "mcq",
                  "ref": "q-keep",
                  "text": "Primary site of loop diuretics?",
                  "options": [
                    { "text": "Thick ascending limb", "isCorrect": true },
                    { "text": "Glomerulus", "isCorrect": false }
                  ]
                },
                {
                  "type": "relabel",
                  "ref": "q-rel",
                  "imagePath": "diagram.png",
                  "prompt": "Identify nephron parts.",
                  "mode": "bank",
                  "style": "blur",
                  "boxes": [
                    { "label": "Glomerulus", "rect": [0.10, 0.15, 0.20, 0.10] },
                    { "label": "PCT", "rect": [0.50, 0.20, 0.20, 0.10] }
                  ]
                },
                {
                  "type": "case",
                  "ref": "q-case",
                  "title": "AKI Vignette",
                  "stem": "Patient presents with oliguria and BUN:Cr > 20.",
                  "questions": [
                    {
                      "type": "mcq",
                      "text": "Initial likely etiology?",
                      "options": [
                        { "text": "Prerenal", "isCorrect": true },
                        { "text": "Postrenal", "isCorrect": false }
                      ]
                    },
                    {
                      "type": "numeric",
                      "text": "FeNa threshold (%)?",
                      "answer": 1,
                      "tolerance": 0.2,
                      "unit": "%"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """);

        var applyC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", initialSpecPath, "-o", externalStore, "--json"], applyC, authorAppData));
        string wsId;
        string storageDir;
        using (var applyDoc = JsonDocument.Parse(applyC.StandardOutput))
        {
            var data = applyDoc.RootElement.GetProperty("data");
            wsId = data.GetProperty("targetId").GetString()!;
            storageDir = data.GetProperty("storageDirectory").GetString()!;
        }

        using var cwdScope = _fx.PushCwd(unrelatedCwd);

        // Wrapper opens unregistered external workspace folder directly (no prior mby ls)
        var openC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", storageDir, "--view", "spec", "--json"], openC, wrapperAppData));
        Assert.DoesNotContain("payloadJson", openC.StandardOutput, StringComparison.OrdinalIgnoreCase);

        string artCompId, quizCompId, textBlockId, tableBlockId, mcqQId, relabelQId, caseQId;
        string existingRelabelAsset, existingKeepCardAsset;
        using (var openDoc = JsonDocument.Parse(openC.StandardOutput))
        {
            var data = openDoc.RootElement.GetProperty("data");
            var spec = data.GetProperty("spec");
            var idMap = data.GetProperty("idMap");

            Assert.Equal(wsId, idMap.GetProperty("id").GetString());
            var compMaps = idMap.GetProperty("components");
            artCompId = compMaps[0].GetProperty("id").GetString()!;
            quizCompId = compMaps[1].GetProperty("id").GetString()!;
            textBlockId = compMaps[0].GetProperty("blocks")[0].GetProperty("id").GetString()!;
            tableBlockId = compMaps[0].GetProperty("blocks")[2].GetProperty("id").GetString()!;
            mcqQId = compMaps[1].GetProperty("questions")[0].GetProperty("id").GetString()!;
            relabelQId = compMaps[1].GetProperty("questions")[1].GetProperty("id").GetString()!;
            caseQId = compMaps[1].GetProperty("questions")[2].GetProperty("id").GetString()!;

            existingKeepCardAsset = spec.GetProperty("components")[0].GetProperty("links")[0].GetProperty("imagePath").GetString()!;
            existingRelabelAsset = spec.GetProperty("components")[1].GetProperty("questions")[1].GetProperty("imagePath").GetString()!;
            Assert.StartsWith("assets/", existingKeepCardAsset);
            Assert.StartsWith("assets/", existingRelabelAsset);
        }

        // Stage wrapper edit batch with new assets in staging dir
        var stagingDir = _fx.SubDir("wrapper_staging");
        await TestAssets.WritePngAsync(Path.Combine(stagingDir, "new_card.png"), [0x11, 0x22]);
        await TestAssets.WritePngAsync(Path.Combine(stagingDir, "case_sub.png"), [0x33, 0x44]);

        var batchOps = new object[]
        {
            new
            {
                op = "patch",
                addr = textBlockId,
                data = new { text = "Updated prose linking [Loop](mby://card/card-keep) and [New Card](mby://card/card-added)." }
            },
            new
            {
                op = "patch",
                addr = artCompId,
                data = new
                {
                    links = new object[]
                    {
                        new { id = "card-keep", title = "Loop Card (Edited)", text = "Refined NKCC2 mechanism.", imagePath = existingKeepCardAsset },
                        new { id = "card-added", title = "Added Card", text = "Brand new hover note.", imagePath = "new_card.png" }
                    }
                }
            },
            new
            {
                op = "patch",
                addr = tableBlockId,
                data = new
                {
                    title = "Diuretics (Updated)",
                    headers = new[] { "Drug", "Site" },
                    rows = new[]
                    {
                        new[] { "Furosemide", "TAL" },
                        new[] { "Hydrochlorothiazide", "DCT" }
                    }
                }
            },
            new
            {
                op = "patch",
                addr = relabelQId,
                data = new
                {
                    imagePath = existingRelabelAsset,
                    prompt = "Label updated nephron segments.",
                    style = "whiteout"
                }
            },
            new
            {
                op = "patch",
                addr = caseQId,
                data = new
                {
                    stem = "Updated AKI vignette with urine sodium < 20 mEq/L.",
                    questions = new object[]
                    {
                        new
                        {
                            type = "mcq",
                            text = "Updated diagnostic question?",
                            imagePath = "case_sub.png",
                            options = new object[]
                            {
                                new { text = "Prerenal azotemia", isCorrect = true },
                                new { text = "ATN", isCorrect = false }
                            }
                        },
                        new
                        {
                            type = "flashcard",
                            front = new { text = "Urinary Na in prerenal AKI?" },
                            back = new { text = "< 20 mEq/L" }
                        },
                        new
                        {
                            type = "numeric",
                            text = "Typical FeNa cutoff (%):",
                            answer = 1,
                            tolerance = 0.2,
                            unit = "%"
                        }
                    }
                }
            },
            new
            {
                op = "apply",
                addr = quizCompId,
                spec = new
                {
                    questions = new object[]
                    {
                        new
                        {
                            type = "numeric",
                            text = "Normal GFR (mL/min)?",
                            answer = 125,
                            tolerance = "5%",
                            unit = "mL/min"
                        }
                    }
                }
            },
            new
            {
                op = "patch",
                addr = quizCompId,
                move = "1:shift"
            }
        };

        var opsPath = Path.Combine(stagingDir, "ops.json");
        await File.WriteAllTextAsync(opsPath, JsonSerializer.Serialize(batchOps, new JsonSerializerOptions { WriteIndented = true }));

        var batchC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["batch", opsPath, "--json"], batchC, wrapperAppData));
        using (var batchDoc = JsonDocument.Parse(batchC.StandardOutput))
        {
            var data = batchDoc.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("rolledBack").GetBoolean());
            Assert.Equal(7, data.GetProperty("stepCount").GetInt32());
        }

        // Verify persisted state, preserved IDs, and edits via --view spec
        var afterC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", storageDir, "--view", "spec", "--json"], afterC, wrapperAppData));
        using (var afterDoc = JsonDocument.Parse(afterC.StandardOutput))
        {
            var data = afterDoc.RootElement.GetProperty("data");
            var spec = data.GetProperty("spec");
            var idMap = data.GetProperty("idMap");

            // Session & component IDs preserved, components reordered (quiz now first, article second)
            Assert.Equal(wsId, idMap.GetProperty("id").GetString());
            Assert.Equal(quizCompId, idMap.GetProperty("components")[0].GetProperty("id").GetString());
            Assert.Equal(artCompId, idMap.GetProperty("components")[1].GetProperty("id").GetString());

            // Untouched MCQ ID preserved + new question appended
            var quizMapQs = idMap.GetProperty("components")[0].GetProperty("questions");
            Assert.Equal(4, quizMapQs.GetArrayLength());
            Assert.Equal(mcqQId, quizMapQs[0].GetProperty("id").GetString());
            Assert.Equal(relabelQId, quizMapQs[1].GetProperty("id").GetString());
            Assert.Equal(caseQId, quizMapQs[2].GetProperty("id").GetString());

            var quizSpec = spec.GetProperty("components")[0];
            var relabelSpec = quizSpec.GetProperty("questions")[1];
            Assert.Equal("whiteout", relabelSpec.GetProperty("style").GetString());
            Assert.Equal(existingRelabelAsset, relabelSpec.GetProperty("imagePath").GetString());

            var caseSpec = quizSpec.GetProperty("questions")[2];
            var caseSubs = caseSpec.GetProperty("questions");
            Assert.Equal(3, caseSubs.GetArrayLength());
            Assert.Equal("mcq", caseSubs[0].GetProperty("type").GetString());
            Assert.StartsWith("assets/case_sub_", caseSubs[0].GetProperty("imagePath").GetString());
            Assert.Equal("flashcard", caseSubs[1].GetProperty("type").GetString());
            Assert.Equal("numeric", caseSubs[2].GetProperty("type").GetString());

            var artSpec = spec.GetProperty("components")[1];
            Assert.Contains("Updated prose", artSpec.GetProperty("blocks")[0].GetProperty("text").GetString());
            Assert.Equal("Diuretics (Updated)", artSpec.GetProperty("blocks")[2].GetProperty("title").GetString());
            Assert.Equal(2, artSpec.GetProperty("blocks")[2].GetProperty("rows").GetArrayLength());

            var links = artSpec.GetProperty("links");
            Assert.Equal(2, links.GetArrayLength());
            Assert.Equal("card-keep", links[0].GetProperty("id").GetString());
            Assert.Equal("Loop Card (Edited)", links[0].GetProperty("title").GetString());
            Assert.Equal(existingKeepCardAsset, links[0].GetProperty("imagePath").GetString());
            Assert.Equal("card-added", links[1].GetProperty("id").GetString());
            Assert.StartsWith("assets/new_card_", links[1].GetProperty("imagePath").GetString());
        }

        // Assert zero double-hashed filenames in assets/
        var assetFiles = Directory.GetFiles(Path.Combine(storageDir, "assets")).Select(Path.GetFileName).ToList();
        Assert.NotEmpty(assetFiles);
        Assert.DoesNotContain(assetFiles, f => f is not null && DoubleHashRegex().IsMatch(f));

        // Lint & static-only build pass cleanly via direct folder path
        var checkC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["check", storageDir, "--json"], checkC, wrapperAppData));
        using (var checkDoc = JsonDocument.Parse(checkC.StandardOutput))
        {
            var checkData = checkDoc.RootElement.GetProperty("data");
            Assert.True(checkData.GetProperty("isClean").GetBoolean());
            Assert.Equal(0, checkData.GetProperty("errorCount").GetInt32());
        }

        var webOut = Path.Combine(_fx.TempRoot, "wrapper_built_web");
        var buildC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["build", storageDir, "-o", webOut, "--static-only", "--json"], buildC, wrapperAppData));
        using (var buildDoc = JsonDocument.Parse(buildC.StandardOutput))
        {
            Assert.True(buildDoc.RootElement.GetProperty("data").GetProperty("verify").GetProperty("passed").GetBoolean());
        }
    }

    [Fact]
    public async Task MbypkgSandbox_WrapperEditMode_RoundTripPreservesMetadataEditsAssetsLinksAndLayouts()
    {
        var authorAppData = _fx.SubDir("pkg_author_appdata");
        var authorWork = _fx.SubDir("pkg_author_work");
        var authorSrc = _fx.SubDir("pkg_author_src");

        await TestAssets.WritePngAsync(Path.Combine(authorSrc, "hero.png"), [0x21, 0x22]);

        // Configure non-default global layout override in author environment
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["layout", "set", "mcq", "optionColumns", "2", "--json"], new TestConsole(), authorAppData));

        var specPath = Path.Combine(authorSrc, "session.json");
        await File.WriteAllTextAsync(specPath, """
        {
          "kind": "session",
          "name": "Android Package Session",
          "components": [
            {
              "kind": "article",
              "ref": "art-pkg",
              "title": "Cardiology Intro",
              "layoutOverrides": { "cardMaxWidthPx": "960" },
              "links": [
                { "id": "ecg-card", "title": "ECG Note", "text": "P wave = atrial depolarization.", "imagePath": "hero.png" }
              ],
              "blocks": [
                { "kind": "text", "ref": "b-intro", "text": "Review [ECG Note](mby://card/ecg-card)." },
                { "kind": "image", "ref": "b-hero", "imagePath": "hero.png", "caption": "ECG trace" }
              ]
            },
            {
              "kind": "quiz",
              "ref": "qz-pkg",
              "title": "ECG Quiz",
              "quizType": "mcq",
              "questions": [
                {
                  "type": "mcq",
                  "ref": "q-ecg",
                  "text": "What does the P wave represent?",
                  "layoutOverrides": { "optionGapPx": "20" },
                  "options": [
                    { "text": "Atrial depolarization", "isCorrect": true },
                    { "text": "Ventricular depolarization", "isCorrect": false }
                  ]
                }
              ]
            }
          ]
        }
        """);

        var applyC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", specPath, "-o", authorWork, "--json"], applyC, authorAppData));
        var origWsId = JsonDocument.Parse(applyC.StandardOutput).RootElement.GetProperty("data").GetProperty("targetId").GetString()!;

        var pkgPath = Path.Combine(_fx.TempRoot, "study_android.mbypkg");
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["pkg", "export", origWsId, "android", pkgPath, "--include-layout", "--json"], new TestConsole(), authorAppData));

        // Wrapper Sandbox 1: isolated temp workspace + temp --app-data
        var sandbox1Work = _fx.SubDir("sandbox1_work");
        var sandbox1AppData = _fx.SubDir("sandbox1_appdata");
        var import1C = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["pkg", "import", pkgPath, sandbox1Work, "--app-data", sandbox1AppData, "--json"], import1C));

        string importedId, targetPlatform;
        bool includedLayout;
        using (var impDoc = JsonDocument.Parse(import1C.StandardOutput))
        {
            var data = impDoc.RootElement.GetProperty("data");
            importedId = data.GetProperty("importedId").GetString()!;
            targetPlatform = data.GetProperty("targetPlatform").GetString()!;
            includedLayout = data.GetProperty("includedLayout").GetBoolean();
        }
        Assert.Equal(origWsId, importedId);
        Assert.Equal("android", targetPlatform);
        Assert.True(includedLayout);

        // Wrapper reads via --view spec
        var read1C = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["get", importedId, "--view", "spec", "--app-data", sandbox1AppData, "--json"], read1C));

        string artCompId, textBlockId, mcqQId, existingCardImg;
        using (var read1Doc = JsonDocument.Parse(read1C.StandardOutput))
        {
            var data = read1Doc.RootElement.GetProperty("data");
            var idMap = data.GetProperty("idMap");
            var spec = data.GetProperty("spec");
            artCompId = idMap.GetProperty("components")[0].GetProperty("id").GetString()!;
            textBlockId = idMap.GetProperty("components")[0].GetProperty("blocks")[0].GetProperty("id").GetString()!;
            mcqQId = idMap.GetProperty("components")[1].GetProperty("questions")[0].GetProperty("id").GetString()!;
            existingCardImg = spec.GetProperty("components")[0].GetProperty("links")[0].GetProperty("imagePath").GetString()!;
        }

        // Wrapper stages edits + new link card asset in sandbox staging dir
        var stageDir = _fx.SubDir("sandbox1_stage");
        await TestAssets.WritePngAsync(Path.Combine(stageDir, "qrs_card.png"), [0x55, 0x66]);
        var opsPath = Path.Combine(stageDir, "save_ops.json");
        await File.WriteAllTextAsync(opsPath, JsonSerializer.Serialize(new object[]
        {
            new
            {
                op = "patch",
                addr = textBlockId,
                data = new { text = "Edited sandbox text with [ECG](mby://card/ecg-card) and [QRS](mby://card/qrs-card)." }
            },
            new
            {
                op = "patch",
                addr = artCompId,
                data = new
                {
                    links = new object[]
                    {
                        new { id = "ecg-card", title = "ECG Note (Saved)", text = "Updated P wave card.", imagePath = existingCardImg },
                        new { id = "qrs-card", title = "QRS Complex", text = "Ventricular depolarization.", imagePath = "qrs_card.png" }
                    }
                }
            },
            new
            {
                op = "patch",
                addr = mcqQId,
                data = new { text = "Edited: What does the P wave signify?" }
            }
        }));

        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["batch", opsPath, "--app-data", sandbox1AppData, "--json"], new TestConsole()));
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["check", importedId, "--app-data", sandbox1AppData, "--json"], new TestConsole()));

        // Re-export directly back over the original .mbypkg using preserved platform + --include-layout
        var exportArgs = new List<string> { "pkg", "export", importedId, targetPlatform, pkgPath, "--app-data", sandbox1AppData, "--json" };
        if (includedLayout) exportArgs.Add("--include-layout");
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(exportArgs.ToArray(), new TestConsole()));

        // Brand-new Verification Sandbox 2
        var sandbox2Work = _fx.SubDir("sandbox2_work");
        var sandbox2AppData = _fx.SubDir("sandbox2_appdata");
        var import2C = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["pkg", "import", pkgPath, sandbox2Work, "--app-data", sandbox2AppData, "--json"], import2C));

        string reimportedStorage;
        using (var imp2Doc = JsonDocument.Parse(import2C.StandardOutput))
        {
            var data = imp2Doc.RootElement.GetProperty("data");
            Assert.Equal("android", data.GetProperty("targetPlatform").GetString());
            Assert.True(data.GetProperty("includedLayout").GetBoolean());
            reimportedStorage = data.GetProperty("storageDirectory").GetString()!;
        }

        // Verify global layout profile survived round-trip
        var layoutC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["layout", "get", "mcq", "--app-data", sandbox2AppData, "--json"], layoutC));
        using (var layoutDoc = JsonDocument.Parse(layoutC.StandardOutput))
        {
            var props = layoutDoc.RootElement.GetProperty("data").GetProperty("properties");
            var optCols = props.EnumerateArray().Single(p => p.GetProperty("property").GetString() == "optionColumns");
            Assert.Equal("2", optCols.GetProperty("currentValue").GetString());
        }

        // Verify all edits, assets, links, and entity layout overrides survived round-trip
        var verifyC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["get", importedId, "--view", "spec", "--app-data", sandbox2AppData, "--json"], verifyC));
        using (var vDoc = JsonDocument.Parse(verifyC.StandardOutput))
        {
            var spec = vDoc.RootElement.GetProperty("data").GetProperty("spec");
            var art = spec.GetProperty("components")[0];
            Assert.Equal("960", art.GetProperty("layoutOverrides").GetProperty("cardMaxWidthPx").GetString());
            Assert.Contains("Edited sandbox text", art.GetProperty("blocks")[0].GetProperty("text").GetString());

            var links = art.GetProperty("links");
            Assert.Equal(2, links.GetArrayLength());
            Assert.Equal("ECG Note (Saved)", links[0].GetProperty("title").GetString());
            Assert.Equal("qrs-card", links[1].GetProperty("id").GetString());
            var qrsImg = links[1].GetProperty("imagePath").GetString()!;
            Assert.True(File.Exists(Path.Combine(reimportedStorage, qrsImg.Replace('/', Path.DirectorySeparatorChar))));

            var mcq = spec.GetProperty("components")[1].GetProperty("questions")[0];
            Assert.Equal("Edited: What does the P wave signify?", mcq.GetProperty("text").GetString());
            Assert.Equal("20", mcq.GetProperty("layoutOverrides").GetProperty("optionGapPx").GetString());
        }

        var finalCheckC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
            ["check", importedId, "--app-data", sandbox2AppData, "--json"], finalCheckC));
        Assert.True(JsonDocument.Parse(finalCheckC.StandardOutput).RootElement.GetProperty("data").GetProperty("isClean").GetBoolean());
    }

    [Fact]
    public async Task AtomicSaveRollback_MidBatchFailure_RestoresDbLinksAndAssetsBitForBit()
    {
        var appData = _fx.SubDir("rb_appdata");
        var workRoot = _fx.SubDir("rb_work");
        var srcDir = _fx.SubDir("rb_src");

        await TestAssets.WritePngAsync(Path.Combine(srcDir, "orig.png"), [0x77, 0x88]);
        var specPath = Path.Combine(srcDir, "session.json");
        await File.WriteAllTextAsync(specPath, """
        {
          "kind": "session",
          "name": "Rollback Safety Session",
          "components": [
            {
              "kind": "article",
              "ref": "art-rb",
              "title": "Immutable Before Failure",
              "links": [
                { "id": "orig-card", "title": "Original Card", "text": "Keep intact.", "imagePath": "orig.png" }
              ],
              "blocks": [
                { "kind": "text", "ref": "b-rb", "text": "Original text referencing [Orig](mby://card/orig-card)." }
              ]
            },
            {
              "kind": "quiz",
              "ref": "qz-rb",
              "title": "Quiz",
              "quizType": "mcq",
              "questions": [
                {
                  "type": "mcq",
                  "ref": "q-rb",
                  "text": "Original MCQ prompt?",
                  "options": [
                    { "text": "Yes", "isCorrect": true },
                    { "text": "No", "isCorrect": false }
                  ]
                }
              ]
            }
          ]
        }
        """);

        var applyC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["apply", specPath, "-o", workRoot, "--json"], applyC, appData));
        string wsId, storageDir;
        using (var doc = JsonDocument.Parse(applyC.StandardOutput))
        {
            var data = doc.RootElement.GetProperty("data");
            wsId = data.GetProperty("targetId").GetString()!;
            storageDir = data.GetProperty("storageDirectory").GetString()!;
        }

        // Capture pre-save canonical spec + bit-for-bit DB and assets snapshot
        var preSpecC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", storageDir, "--view", "spec", "--json"], preSpecC, appData));

        string artCompId, textBlockId, mcqQId;
        using (var preDoc = JsonDocument.Parse(preSpecC.StandardOutput))
        {
            var idMap = preDoc.RootElement.GetProperty("data").GetProperty("idMap");
            artCompId = idMap.GetProperty("components")[0].GetProperty("id").GetString()!;
            textBlockId = idMap.GetProperty("components")[0].GetProperty("blocks")[0].GetProperty("id").GetString()!;
            mcqQId = idMap.GetProperty("components")[1].GetProperty("questions")[0].GetProperty("id").GetString()!;
        }

        SqliteConnection.ClearAllPools();
        var dbPath = Path.Combine(storageDir, $"{wsId}.db");
        var preDbBytes = await File.ReadAllBytesAsync(dbPath);
        var assetsDir = Path.Combine(storageDir, "assets");
        var preAssets = SnapshotDirectoryBytes(assetsDir);

        // Build failing mid-batch Edit Mode save (step 0 adds a new image & mutates links; step 1 mutates text & MCQ; step 2 fails validation)
        var stageDir = _fx.SubDir("rb_stage");
        await TestAssets.WritePngAsync(Path.Combine(stageDir, "should_be_removed.png"), [0xAA, 0xBB, 0xCC]);
        var badOpsPath = Path.Combine(stageDir, "failing_ops.json");
        await File.WriteAllTextAsync(badOpsPath, JsonSerializer.Serialize(new object[]
        {
            new
            {
                op = "patch",
                addr = artCompId,
                data = new
                {
                    links = new object[]
                    {
                        new { id = "temp-card", title = "Temp", text = "Should roll back.", imagePath = "should_be_removed.png" }
                    }
                }
            },
            new
            {
                op = "patch",
                addr = textBlockId,
                data = new { text = "Mutated text that must NOT persist." }
            },
            new
            {
                op = "patch",
                addr = mcqQId,
                data = new { unknownInvalidField = "boom" }
            }
        }));

        var failC = new TestConsole();
        Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["batch", badOpsPath, "--json"], failC, appData));
        Assert.Contains("rolled back", failC.StandardError, StringComparison.OrdinalIgnoreCase);

        // Verify bit-for-bit restoration of DB and assets/
        SqliteConnection.ClearAllPools();
        var postDbBytes = await File.ReadAllBytesAsync(dbPath);
        Assert.Equal(preDbBytes, postDbBytes);

        var postAssets = SnapshotDirectoryBytes(assetsDir);
        Assert.Equal(preAssets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), postAssets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        foreach (var (fileName, bytes) in preAssets)
            Assert.Equal(bytes, postAssets[fileName]);

        // Verify logical spec + links match pre-save state identically
        var postSpecC = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["get", storageDir, "--view", "spec", "--json"], postSpecC, appData));
        Assert.Equal(preSpecC.StandardOutput, postSpecC.StandardOutput);
    }

    private static Dictionary<string, byte[]> SnapshotDirectoryBytes(string directory)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return result;
        foreach (var file in Directory.EnumerateFiles(directory))
            result[Path.GetFileName(file)] = File.ReadAllBytes(file);
        return result;
    }
}
