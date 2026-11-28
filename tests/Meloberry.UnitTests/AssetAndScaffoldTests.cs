using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
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
}
