using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Scaffolding;
using Meloberry.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class AssetAndScaffoldTests : IDisposable
{
    private readonly WorkspaceTestFixture _fx = new("mby_asset_scaffold");

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task AssetAddLsPrune_SupportsWorkspaceDirStagingAndBatchRollback()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var addAsset = scope.ServiceProvider.GetRequiredService<AddAssetHandler>();
        var listAssets = scope.ServiceProvider.GetRequiredService<ListAssetsHandler>();
        var pruneAssets = scope.ServiceProvider.GetRequiredService<PruneAssetsHandler>();
        var batch = scope.ServiceProvider.GetRequiredService<BatchOperationsHandler>();

        var srcDir = _fx.SubDir("asset_src");
        var usedPng = Path.Combine(srcDir, "used.png");
        var extraPng = Path.Combine(srcDir, "extra.png");
        await TestAssets.WritePngAsync(usedPng);
        await TestAssets.WritePngAsync(extraPng, [0x42, 0x99]);

        // 1. Standalone --dir mode (no SQLite workspace yet)
        var stagingDir = _fx.SubDir("staging_ws");
        var staged1 = await addAsset.HandleAsync(new AddAssetCommand(null, usedPng, stagingDir));
        Assert.True(staged1.IsSuccess);
        Assert.False(staged1.Value.AlreadyExisted);
        Assert.True(File.Exists(staged1.Value.FullPath));

        var staged2 = await addAsset.HandleAsync(new AddAssetCommand(null, usedPng, stagingDir));
        Assert.True(staged2.IsSuccess);
        Assert.True(staged2.Value.AlreadyExisted);

        // 2. Workspace mode: create session referencing used.png, then add extra.png as orphan
        var specJson = """
        {
          "kind": "session",
          "name": "Assets",
          "components": [
            {
              "kind": "article",
              "blocks": [{ "kind": "image", "imagePath": "used.png" }]
            }
          ]
        }
        """;
        var wsRes = await apply.HandleAsync(new ApplySpecCommand(null, null, specJson, _fx.WorkDir, AssetBaseDirectoryOverride: srcDir));
        Assert.True(wsRes.IsSuccess, wsRes.Error?.Description);
        var wsId = wsRes.Value.TargetId;

        var addedOrphan = await addAsset.HandleAsync(new AddAssetCommand(wsId, extraPng));
        Assert.True(addedOrphan.IsSuccess);
        Assert.False(addedOrphan.Value.AlreadyExisted);

        var lsRes = await listAssets.HandleAsync(new ListAssetsQuery(wsId));
        Assert.True(lsRes.IsSuccess);
        Assert.Equal(2, lsRes.Value.Assets.Count);
        Assert.Single(lsRes.Value.Assets, a => a.IsReferenced);
        Assert.Single(lsRes.Value.Assets, a => a.IsOrphan);

        // 3. Batch prune followed by failing step must roll back and restore pruned orphan file!
        var failBatch = $$"""
        [
          { "op": "asset-prune", "addr": "{{wsId}}" },
          { "op": "check", "addr": "Session_99999999_000000_000" }
        ]
        """;
        var failRes = await batch.HandleAsync(new BatchOperationsCommand(InlineJson: failBatch));
        Assert.False(failRes.IsSuccess);
        Assert.True(File.Exists(addedOrphan.Value.FullPath), "Rolled-back batch prune should restore the deleted asset.");

        // 4. Dry-run prune leaves orphan, real prune removes it
        var dryPrune = await pruneAssets.HandleAsync(new PruneAssetsCommand(wsId, DryRun: true));
        Assert.True(dryPrune.IsSuccess);
        Assert.Single(dryPrune.Value.RemovedAssets);
        Assert.True(File.Exists(addedOrphan.Value.FullPath));

        var realPrune = await pruneAssets.HandleAsync(new PruneAssetsCommand(wsId, DryRun: false));
        Assert.True(realPrune.IsSuccess);
        Assert.Single(realPrune.Value.RemovedAssets);
        Assert.False(File.Exists(addedOrphan.Value.FullPath));
    }

    [Fact]
    public async Task Scaffold_All19Types_MbyPreset_And_PromptDraft_RoundTripThroughApplyAndLint()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var scaffold = scope.ServiceProvider.GetRequiredService<ScaffoldHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var batch = scope.ServiceProvider.GetRequiredService<BatchOperationsHandler>();

        // 1. .mbypreset covering ALL 19 content topics -> md + apply + check
        var presetJson = """
        {
          "formatVersion": 1,
          "scope": "workflow",
          "name": "All 19 Topics",
          "workflowPreset": {
            "container": "session",
            "components": [
              {
                "id": "a1",
                "kind": "article",
                "title": "All Blocks",
                "selections": [
                  { "topic": "text", "enabled": true, "desiredCount": 1 },
                  { "topic": "image", "enabled": true, "desiredCount": 1 },
                  { "topic": "callout", "enabled": true, "desiredCount": 1, "modes": ["clinical"] },
                  { "topic": "table", "enabled": true, "desiredCount": 1 },
                  { "topic": "diagram-explore", "enabled": true, "desiredCount": 1 },
                  { "topic": "reveal", "enabled": true, "desiredCount": 1 },
                  { "topic": "link", "enabled": true, "desiredCount": 1 }
                ]
              },
              {
                "id": "q1",
                "kind": "quiz",
                "title": "All Questions",
                "selections": [
                  { "topic": "mcq", "enabled": true, "desiredCount": 1 },
                  { "topic": "flashcard", "enabled": true, "desiredCount": 1 },
                  { "topic": "fill-blank", "enabled": true, "desiredCount": 1 },
                  { "topic": "assertion-reason", "enabled": true, "desiredCount": 1 },
                  { "topic": "spot-error", "enabled": true, "desiredCount": 1 },
                  { "topic": "diff", "enabled": true, "desiredCount": 1 },
                  { "topic": "mechanism", "enabled": true, "desiredCount": 1 },
                  { "topic": "relabel", "enabled": true, "desiredCount": 1 },
                  { "topic": "case", "enabled": true, "desiredCount": 1 },
                  { "topic": "classify", "enabled": true, "desiredCount": 1 },
                  { "topic": "match", "enabled": true, "desiredCount": 1 },
                  { "topic": "numeric", "enabled": true, "desiredCount": 1 }
                ]
              }
            ]
          }
        }
        """;

        var mdOutDir = _fx.SubDir("scaffold_all19_md");
        var mdRes = await scaffold.HandleAsync(new ScaffoldCommand(
            InlineJson: presetJson, Format: "md", OutputPath: mdOutDir, Apply: true, AllowPlaceholders: true, ApplyWorkingDirectory: _fx.WorkDir));
        Assert.True(mdRes.IsSuccess, mdRes.Error?.Description);
        Assert.True(mdRes.Value.RequiresPlaceholderImage);
        Assert.NotNull(mdRes.Value.Applied);
        var wsId19 = mdRes.Value.Applied!.TargetId;

        var lint19 = await lint.HandleAsync(new LintCommand(wsId19));
        Assert.True(lint19.IsSuccess);
        Assert.Equal(0, lint19.Value.ErrorCount);

        // JSON format of same 19-topic preset
        var jsonRes = await scaffold.HandleAsync(new ScaffoldCommand(
            InlineJson: presetJson, Format: "json", Apply: true, AllowPlaceholders: true, ApplyWorkingDirectory: _fx.WorkDir));
        Assert.True(jsonRes.IsSuccess, jsonRes.Error?.Description);
        var lintJson19 = await lint.HandleAsync(new LintCommand(jsonRes.Value.Applied!.TargetId));
        Assert.Equal(0, lintJson19.Value.ErrorCount);

        // 2. get --view markdown -o <dir> round-trips all 19 types cleanly back through apply + check!
        var exportedMdDir = _fx.SubDir("exported_md_19");
        var getMd = await inspect.HandleAsync(new InspectEntityQuery(wsId19, InspectViewMode.Markdown, exportedMdDir));
        Assert.True(getMd.IsSuccess, getMd.Error?.Description);
        Assert.NotNull(getMd.Value.MarkdownFiles);
        Assert.True(File.Exists(Path.Combine(exportedMdDir, "_session.md")));

        var reapplied = await apply.HandleAsync(new ApplySpecCommand(null, exportedMdDir, WorkingDirectory: _fx.WorkDir));
        Assert.True(reapplied.IsSuccess, reapplied.Error?.Description);
        var lintReapplied = await lint.HandleAsync(new LintCommand(reapplied.Value.TargetId));
        Assert.Equal(0, lintReapplied.Value.ErrorCount);

        // 3. PromptDraft with SpecContentSource (RawText, MarkdownFile, ImageFile) + MissingContent gating
        var draftDir = _fx.SubDir("draft_src");
        Directory.CreateDirectory(Path.Combine(draftDir, "notes"));
        Directory.CreateDirectory(Path.Combine(draftDir, "assets"));
        await File.WriteAllTextAsync(Path.Combine(draftDir, "notes", "intro.md"), "## Intro\n\nLoaded from file.");
        await TestAssets.WritePngAsync(Path.Combine(draftDir, "assets", "diag.png"));

        var missingDraftJson = """
        {
          "container": "StandaloneArticle",
          "title": "Strict Draft",
          "components": [
            {
              "id": "a1",
              "kind": "article",
              "title": "Strict Article",
              "selections": [{ "topic": "text", "enabled": true, "desiredCount": 1 }],
              "orderedItems": [{ "id": "text_1", "topic": "text", "instanceNumber": 1, "content": { "mode": "None" } }]
            }
          ]
        }
        """;
        var missingRes = await scaffold.HandleAsync(new ScaffoldCommand(
            InlineJson: missingDraftJson, Format: "md", AllowPlaceholders: false, BaseDirectoryOverride: draftDir));
        Assert.False(missingRes.IsSuccess);
        Assert.Equal("Scaffold.MissingContent", missingRes.Error!.Code);

        var populatedDraftJson = """
        {
          "container": "Session",
          "title": "Populated Session",
          "components": [
            {
              "id": "a1",
              "kind": "article",
              "title": "Notes Article",
              "selections": [
                { "topic": "text", "enabled": true, "desiredCount": 1 },
                { "topic": "link", "enabled": true, "desiredCount": 1 }
              ],
              "orderedItems": [
                { "id": "text_1", "topic": "text", "instanceNumber": 1, "content": { "mode": "MarkdownFile", "markdownFilePath": "notes/intro.md" } },
                { "id": "link_1", "topic": "link", "instanceNumber": 1, "content": { "mode": "RawText", "rawText": "Hover definition." } }
              ]
            },
            {
              "id": "q1",
              "kind": "quiz",
              "title": "Quick Quiz",
              "selections": [{ "topic": "mcq", "enabled": true, "desiredCount": 1 }],
              "orderedItems": [
                { "id": "mcq_1", "topic": "mcq", "instanceNumber": 1, "subtype": "single", "content": { "mode": "ImageFile", "imagePath": "assets/diag.png", "rawText": "Which structure?\n- [x] Aorta\n- [ ] Vena cava" } }
              ]
            }
          ]
        }
        """;
        var draftBatch = $$"""
        [
          { "op": "scaffold", "blueprint": {{populatedDraftJson}}, "format": "md", "apply": true, "applyOut": "{{_fx.WorkDir.Replace("\\", "\\\\")}}", "baseDir": "{{draftDir.Replace("\\", "\\\\")}}" },
          { "op": "check", "addr": "$0.id" }
        ]
        """;
        var batchRes = await batch.HandleAsync(new BatchOperationsCommand(InlineJson: draftBatch));
        Assert.True(batchRes.IsSuccess, batchRes.Error?.Description);

        // 4. patch --md smart-wrapping preserves ID/RefKey and updates block & question
        var patchBlockRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{wsId19}#c1.b3",
            InlineJson: "Updated clinical pearl body.",
            IsMarkdown: true,
            MarkdownSubtype: "clinical"));
        Assert.True(patchBlockRes.IsSuccess, patchBlockRes.Error?.Description);

        var patchQRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{wsId19}#c2.q1",
            InlineJson: "Updated MCQ prompt?\n- [x] Yes\n- [ ] No",
            IsMarkdown: true,
            MarkdownSubtype: "single",
            AttachedImagePath: "assets/diag.png",
            AssetBaseDirectoryOverride: draftDir));
        Assert.True(patchQRes.IsSuccess, patchQRes.Error?.Description);
    }

    [Fact]
    public async Task MarkdownRoundTrip_PreservesMultilineYaml_EscapesTablePipes_And_ResolvesDiagramSidecarOnPatchMd()
    {
        using var sp = _fx.CreateProvider();
        using var scope = sp.CreateScope();
        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var inspect = scope.ServiceProvider.GetRequiredService<InspectEntityHandler>();
        var lint = scope.ServiceProvider.GetRequiredService<LintHandler>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var srcDir = _fx.SubDir("rt_multiline_src");
        Directory.CreateDirectory(Path.Combine(srcDir, "assets"));
        await TestAssets.WritePngAsync(Path.Combine(srcDir, "assets", "heart.png"));
        await File.WriteAllTextAsync(Path.Combine(srcDir, "assets", "heart.masks.json"), """
        {
          "boxes": [
            { "label": "Aorta", "x": 0.12, "y": 0.18, "w": 0.22, "h": 0.15 },
            { "label": "Left Ventricle", "x": 0.45, "y": 0.55, "w": 0.3, "h": 0.25 }
          ]
        }
        """);

        var specJson = """
        {
          "kind": "session",
          "name": "Multiline & Table Round-Trip",
          "description": "Line 1 of session description.\n\nLine 3 with details.",
          "components": [
            {
              "kind": "article",
              "title": "Edge Cases",
              "blocks": [
                {
                  "kind": "diagram-explore",
                  "imagePath": "assets/heart.png",
                  "title": "Heart Map",
                  "hotspots": [
                    {
                      "label": "Aorta",
                      "note": "Primary systemic artery.\n- Carries oxygenated blood\n- Leaves LV",
                      "x": 0.1, "y": 0.2, "w": 0.2, "h": 0.15
                    }
                  ]
                },
                {
                  "kind": "table",
                  "title": "Pipes & Newlines",
                  "headers": ["Case", "Content"],
                  "rows": [
                    ["Literal pipe", "a | b"],
                    ["Math pipe", "$P(A|B)$"],
                    ["Code pipe", "`x | y`"],
                    ["Multiline cell", "row line 1\nrow line 2"]
                  ]
                }
              ]
            },
            {
              "kind": "quiz",
              "title": "Relabel Quiz",
              "questions": [
                {
                  "type": "relabel",
                  "imagePath": "assets/heart.png",
                  "mode": "bank",
                  "style": "blur",
                  "prompt": "Identify the structure.\nFocus on great vessels.",
                  "boxes": [
                    { "label": "Aorta", "x": 0.1, "y": 0.2, "w": 0.2, "h": 0.15 }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var applied = await apply.HandleAsync(new ApplySpecCommand(null, null, specJson, _fx.WorkDir, AssetBaseDirectoryOverride: srcDir));
        Assert.True(applied.IsSuccess, applied.Error?.Description);
        var wsId = applied.Value.TargetId;

        var exportDir = _fx.SubDir("rt_multiline_md");
        var getMd = await inspect.HandleAsync(new InspectEntityQuery(wsId, InspectViewMode.Markdown, exportDir));
        Assert.True(getMd.IsSuccess, getMd.Error?.Description);

        var reapplied = await apply.HandleAsync(new ApplySpecCommand(null, exportDir, WorkingDirectory: _fx.WorkDir));
        Assert.True(reapplied.IsSuccess, reapplied.Error?.Description);
        var wsId2 = reapplied.Value.TargetId;

        var lint2 = await lint.HandleAsync(new LintCommand(wsId2));
        Assert.True(lint2.IsSuccess);
        Assert.True(lint2.Value.IsClean, string.Join("; ", lint2.Value.Issues.Select(i => $"{i.Code}: {i.Message}")));

        var loaded2 = await repo.FindSessionAsync(wsId2);
        Assert.NotNull(loaded2);
        Assert.Equal("Line 1 of session description.\n\nLine 3 with details.", loaded2!.Session.Description);

        var rtDiag = loaded2.Session.Components[0].ArticleBlocks[0];
        Assert.True(Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(rtDiag.PayloadJson, out var rtDiagPayload));
        Assert.Equal("Primary systemic artery.\n- Carries oxygenated blood\n- Leaves LV", rtDiagPayload!.Hotspots[0].Note);

        var rtTable = loaded2.Session.Components[0].ArticleBlocks[1];
        Assert.True(Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(rtTable.PayloadJson, out var rtTablePayload));
        Assert.Equal(4, rtTablePayload!.Rows.Count);
        Assert.Equal("a | b", rtTablePayload.Rows[0][1]);
        Assert.Equal("$P(A|B)$", rtTablePayload.Rows[1][1]);
        Assert.Equal("`x | y`", rtTablePayload.Rows[2][1]);
        Assert.Equal("row line 1<br>row line 2", rtTablePayload.Rows[3][1]);

        var rtRel = loaded2.Session.Components[1].Quiz!.Questions[0];
        Assert.True(Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(rtRel.PayloadJson, out var rtRelPayload));
        Assert.Equal("Identify the structure.\nFocus on great vessels.", rtRelPayload!.Prompt);

        // Fix #4: patch --md on diagram-explore with masks= sidecar resolves real coordinates
        var patchMd = """
            :::diagram-explore{image="assets/heart.png" masks="assets/heart.masks.json" title="Patched Heart"}
            - Aorta: Updated note for Aorta
            - Left Ventricle: Main pumping chamber
            :::
            """;
        var patched = await patch.HandleAsync(new PatchEntityCommand(
            $"{wsId2}#c1.b1",
            InlineJson: patchMd,
            IsMarkdown: true,
            AssetBaseDirectoryOverride: srcDir));
        Assert.True(patched.IsSuccess, patched.Error?.Description);

        var lintPatched = await lint.HandleAsync(new LintCommand(wsId2));
        Assert.True(lintPatched.IsSuccess);
        Assert.True(lintPatched.Value.IsClean, string.Join("; ", lintPatched.Value.Issues.Select(i => $"{i.Code}: {i.Message}")));

        var loadedPatched = await repo.FindSessionAsync(wsId2);
        Assert.NotNull(loadedPatched);
        Assert.True(Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(loadedPatched!.Session.Components[0].ArticleBlocks[0].PayloadJson, out var patchedDiag));
        Assert.Equal(2, patchedDiag!.Hotspots.Count);
        Assert.Equal(0.12, patchedDiag.Hotspots[0].X, 4);
        Assert.Equal(0.18, patchedDiag.Hotspots[0].Y, 4);
        Assert.Equal(0.22, patchedDiag.Hotspots[0].W, 4);
        Assert.Equal(0.15, patchedDiag.Hotspots[0].H, 4);
        Assert.Equal(0.45, patchedDiag.Hotspots[1].X, 4);
        Assert.Equal("Main pumping chamber", patchedDiag.Hotspots[1].Note);
    }

    [Fact]
    public void ScaffoldGenerator_And_MarkdownSmartWrapper_HandleEdgeCases()
    {
        // 8a. diff OrderedItems with Subtype="bank" sets "bank": true in GenerateJson
        var diffBankBlueprint = new NormalizedBlueprint(
            BlueprintContainerKind.StandaloneQuiz,
            "Diff Bank Test",
            null,
            [
                new BlueprintComponent(
                    "q1",
                    BlueprintComponentKind.Quiz,
                    "Diff Quiz",
                    [new BlueprintSelection("diff", Enabled: true, DesiredCount: 1)],
                    [new BlueprintSpecInstance("diff_1", "diff", 1, Subtype: "bank")])
            ],
            AllowPlaceholders: true);
        var jsonRes = ScaffoldGenerator.GenerateJson(diffBankBlueprint, allowPlaceholders: true);
        Assert.True(jsonRes.IsSuccess, jsonRes.Error?.Description);
        using var doc = JsonDocument.Parse(jsonRes.Value.RawJson!);
        var q0 = doc.RootElement.GetProperty("questions")[0];
        Assert.True(q0.TryGetProperty("bank", out var bankEl) && bankEl.GetBoolean());

        // 8b. GenerateMarkdown rejects DesiredCount <= 0 with Scaffold.InvalidQty
        var invalidQtyBlueprint = new NormalizedBlueprint(
            BlueprintContainerKind.StandaloneArticle,
            "Zero Qty Test",
            null,
            [
                new BlueprintComponent(
                    "a1",
                    BlueprintComponentKind.Article,
                    "Article 1",
                    [new BlueprintSelection("text", Enabled: true, DesiredCount: 0)],
                    null)
            ],
            AllowPlaceholders: true);
        var mdRes = ScaffoldGenerator.GenerateMarkdown(invalidQtyBlueprint, new Dictionary<string, string>(), allowPlaceholders: true);
        Assert.False(mdRes.IsSuccess);
        Assert.Equal("Scaffold.InvalidQty", mdRes.Error!.Code);

        // 8c. MarkdownSmartWrapper.TryAddRelativeAsset preserves ../ and leading dot filenames
        var collected = new HashSet<string>(StringComparer.Ordinal);
        MarkdownSmartWrapper.TryAddRelativeAsset("./assets/a.png", collected);
        MarkdownSmartWrapper.TryAddRelativeAsset("../assets/b.png", collected);
        MarkdownSmartWrapper.TryAddRelativeAsset(".hidden.png", collected);
        Assert.Contains("assets/a.png", collected);
        Assert.Contains("../assets/b.png", collected);
        Assert.Contains(".hidden.png", collected);
    }
}
