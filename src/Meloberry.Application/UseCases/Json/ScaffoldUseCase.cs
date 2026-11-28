using System.Text.Json;
using System.Text.Json.Nodes;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Scaffolding;

namespace Meloberry.Application.UseCases.Json;

public sealed record ScaffoldCommand(
    string? InputFilePath = null,
    string? InlineJson = null,
    string Format = "md",
    string? OutputPath = null,
    bool Apply = false,
    bool AllowPlaceholders = false,
    string? ApplyWorkingDirectory = null,
    string? BaseDirectoryOverride = null);

public sealed record ScaffoldResponse(
    string Format,
    string Slug,
    string? RawJson = null,
    IReadOnlyDictionary<string, string>? MarkdownFiles = null,
    string? WrittenPath = null,
    bool RequiresPlaceholderImage = false,
    IReadOnlyList<string>? ReferencedAssetPaths = null,
    IReadOnlyList<string>? PlaceholderSpecsUsed = null,
    IReadOnlyList<string>? Warnings = null,
    ApplySpecResponse? Applied = null);

public sealed class ScaffoldHandler(
    IWorkingDirectoryProvider workingDirectoryProvider,
    ApplySpecHandler applyHandler,
    IChainTransactionManager? transactionManager = null)
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<Result<ScaffoldResponse>> HandleAsync(ScaffoldCommand command, CancellationToken cancellationToken = default)
    {
        var format = string.IsNullOrWhiteSpace(command.Format) ? "md" : command.Format.Trim().ToLowerInvariant();
        if (format is not ("md" or "markdown" or "json"))
            return new Error("Cli.InvalidArgs", $"Invalid --format '{command.Format}'. Use 'md' or 'json'.", ErrorType.Validation);
        if (format == "markdown") format = "md";

        string rawInput;
        string baseDir = !string.IsNullOrWhiteSpace(command.BaseDirectoryOverride)
            ? workingDirectoryProvider.NormalizeDirectoryPath(command.BaseDirectoryOverride)
            : workingDirectoryProvider.GetCurrentDirectory();

        if (command.InlineJson is not null)
        {
            rawInput = command.InlineJson.TrimStart('\uFEFF');
        }
        else if (!string.IsNullOrWhiteSpace(command.InputFilePath) && File.Exists(command.InputFilePath))
        {
            rawInput = (await File.ReadAllTextAsync(command.InputFilePath, cancellationToken)).TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(command.BaseDirectoryOverride))
                baseDir = Path.GetDirectoryName(Path.GetFullPath(command.InputFilePath)) ?? baseDir;
        }
        else
        {
            return new Error("Json.FileNotFound", $"Blueprint file '{command.InputFilePath}' was not found.", ErrorType.NotFound);
        }

        if (string.IsNullOrWhiteSpace(rawInput))
            return new Error("Scaffold.InvalidBlueprint", "Scaffold blueprint JSON cannot be empty.", ErrorType.Validation);

        var normRes = ParseAndNormalizeBlueprint(rawInput, command.AllowPlaceholders);
        if (!normRes.IsSuccess) return normRes.Error!;
        var blueprint = normRes.Value;

        var loadedMd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var comp in blueprint.Components)
        {
            if (comp.OrderedItems is null) continue;
            foreach (var inst in comp.OrderedItems)
            {
                if (inst.Content?.Mode != BlueprintContentMode.MarkdownFile || string.IsNullOrWhiteSpace(inst.Content.MarkdownFilePath))
                    continue;

                var rel = inst.Content.MarkdownFilePath.Replace('\\', '/').Trim();
                if (!PathConfinement.TryResolveInside(baseDir, rel, out var fullMd))
                    return new Error("Scaffold.PathEscapesWorkspace", $"Markdown path '{rel}' escapes blueprint directory.", ErrorType.Validation);
                if (File.Exists(fullMd))
                    loadedMd[rel] = await File.ReadAllTextAsync(fullMd, cancellationToken);
            }
        }

        var genRes = format == "md"
            ? ScaffoldGenerator.GenerateMarkdown(blueprint, loadedMd, blueprint.AllowPlaceholders)
            : ScaffoldGenerator.GenerateJson(blueprint, blueprint.AllowPlaceholders);
        if (!genRes.IsSuccess) return genRes.Error!;
        var generated = genRes.Value;

        if (!blueprint.AllowPlaceholders && generated.MissingContentSpecs.Count > 0)
        {
            return new Error(
                "Scaffold.MissingContent",
                $"{generated.MissingContentSpecs.Count} spec(s) are missing content. Provide content or pass --placeholders.",
                ErrorType.Validation,
                Data: generated.MissingContentSpecs);
        }

        string? writtenPath = null;
        string? tempStageDir = null;
        try
        {
            if (format == "md")
            {
                string? targetDir = !string.IsNullOrWhiteSpace(command.OutputPath)
                    ? Path.GetFullPath(command.OutputPath)
                    : (command.Apply ? Path.Combine(Path.GetTempPath(), $"mby_scaffold_{Guid.NewGuid():N}") : null);

                if (!string.IsNullOrWhiteSpace(targetDir))
                {
                    if (command.OutputPath is null) tempStageDir = targetDir;
                    else transactionManager?.NotifyWorkspaceMutating(targetDir);

                    Directory.CreateDirectory(targetDir);
                    foreach (var (fileName, content) in generated.MarkdownFiles!)
                        await File.WriteAllTextAsync(Path.Combine(targetDir, fileName), content, cancellationToken);

                    await MaterializeAssetsIntoDirectoryAsync(targetDir, baseDir, generated.ReferencedAssetPaths, generated.RequiresPlaceholderImage, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(command.OutputPath))
                        writtenPath = targetDir;
                }

                ApplySpecResponse? applied = null;
                if (command.Apply)
                {
                    var applyRes = await applyHandler.HandleAsync(
                        new ApplySpecCommand(null, targetDir, WorkingDirectory: command.ApplyWorkingDirectory, AssetBaseDirectoryOverride: targetDir),
                        cancellationToken);
                    if (!applyRes.IsSuccess) return applyRes.Error!;
                    applied = applyRes.Value;
                }

                return new ScaffoldResponse(
                    "md",
                    generated.Slug,
                    MarkdownFiles: generated.MarkdownFiles,
                    WrittenPath: writtenPath,
                    RequiresPlaceholderImage: generated.RequiresPlaceholderImage,
                    ReferencedAssetPaths: generated.ReferencedAssetPaths.Count > 0 ? generated.ReferencedAssetPaths : null,
                    PlaceholderSpecsUsed: generated.PlaceholderSpecsUsed.Count > 0 ? generated.PlaceholderSpecsUsed : null,
                    Warnings: generated.Warnings.Count > 0 ? generated.Warnings : null,
                    Applied: applied);
            }

            // format == "json"
            string? assetBaseForApply = baseDir;
            if (!string.IsNullOrWhiteSpace(command.OutputPath))
            {
                var trimmed = command.OutputPath.Trim();
                bool isDir = trimmed.EndsWith(Path.DirectorySeparatorChar) || trimmed.EndsWith(Path.AltDirectorySeparatorChar) || Directory.Exists(trimmed);
                var fullOut = Path.GetFullPath(trimmed);
                var jsonFile = isDir ? Path.Combine(fullOut, $"{generated.Slug}.json") : fullOut;
                var parentDir = Path.GetDirectoryName(jsonFile)!;
                Directory.CreateDirectory(parentDir);
                await File.WriteAllTextAsync(jsonFile, generated.RawJson!, cancellationToken);
                await MaterializeAssetsIntoDirectoryAsync(parentDir, baseDir, generated.ReferencedAssetPaths, generated.RequiresPlaceholderImage, cancellationToken);
                writtenPath = jsonFile;
                assetBaseForApply = parentDir;
            }
            else if (command.Apply && (generated.RequiresPlaceholderImage || generated.ReferencedAssetPaths.Count > 0))
            {
                tempStageDir = Path.Combine(Path.GetTempPath(), $"mby_scaffold_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempStageDir);
                await MaterializeAssetsIntoDirectoryAsync(tempStageDir, baseDir, generated.ReferencedAssetPaths, generated.RequiresPlaceholderImage, cancellationToken);
                assetBaseForApply = tempStageDir;
            }

            ApplySpecResponse? jsonApplied = null;
            if (command.Apply)
            {
                var applyRes = await applyHandler.HandleAsync(
                    new ApplySpecCommand(null, null, generated.RawJson, WorkingDirectory: command.ApplyWorkingDirectory, AssetBaseDirectoryOverride: assetBaseForApply),
                    cancellationToken);
                if (!applyRes.IsSuccess) return applyRes.Error!;
                jsonApplied = applyRes.Value;
            }

            return new ScaffoldResponse(
                "json",
                generated.Slug,
                RawJson: generated.RawJson,
                WrittenPath: writtenPath,
                RequiresPlaceholderImage: generated.RequiresPlaceholderImage,
                ReferencedAssetPaths: generated.ReferencedAssetPaths.Count > 0 ? generated.ReferencedAssetPaths : null,
                PlaceholderSpecsUsed: generated.PlaceholderSpecsUsed.Count > 0 ? generated.PlaceholderSpecsUsed : null,
                Warnings: generated.Warnings.Count > 0 ? generated.Warnings : null,
                Applied: jsonApplied);
        }
        finally
        {
            if (tempStageDir is not null && Directory.Exists(tempStageDir))
            {
                try { Directory.Delete(tempStageDir, recursive: true); } catch { }
            }
        }
    }

    private static Result<NormalizedBlueprint> ParseAndNormalizeBlueprint(string rawJson, bool cliAllowPlaceholders)
    {
        try
        {
            var node = JsonNode.Parse(rawJson, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (node is not JsonObject obj)
                return new Error("Scaffold.InvalidBlueprint", "Blueprint input must be a JSON object.", ErrorType.Validation);

            bool isPreset = obj.ContainsKey("formatVersion") || obj.ContainsKey("scope") ||
                            obj.ContainsKey("workflowPreset") || obj.ContainsKey("componentPreset");

            if (isPreset)
            {
                var preset = JsonSerializer.Deserialize<MbyPresetBlueprint>(rawJson, ReadOptions);
                if (preset is null || string.IsNullOrWhiteSpace(preset.Name))
                    return new Error("Scaffold.InvalidBlueprint", "'.mbypreset' blueprint requires a non-empty 'name'.", ErrorType.Validation);

                if (preset.Scope == BlueprintPresetScope.Component)
                {
                    if (preset.ComponentPreset is null)
                        return new Error("Scaffold.InvalidBlueprint", "Component '.mbypreset' requires 'componentPreset'.", ErrorType.Validation);
                    var cKind = preset.ComponentPreset.Kind == BlueprintComponentKind.Quiz
                        ? BlueprintContainerKind.StandaloneQuiz
                        : BlueprintContainerKind.StandaloneArticle;
                    return new NormalizedBlueprint(cKind, preset.Name, preset.Description, [preset.ComponentPreset], cliAllowPlaceholders);
                }

                if (preset.WorkflowPreset is null)
                    return new Error("Scaffold.InvalidBlueprint", "Workflow '.mbypreset' requires 'workflowPreset'.", ErrorType.Validation);

                var wf = preset.WorkflowPreset;
                var wfComps = NormalizeComponents(wf.Container, wf.TitleOrName(preset.Name), wf.Components, wf.Selections);
                return new NormalizedBlueprint(wf.Container, wf.Name ?? preset.Name, wf.Description ?? preset.Description, wfComps, cliAllowPlaceholders);
            }

            var draft = JsonSerializer.Deserialize<PromptDraftBlueprint>(rawJson, ReadOptions);
            if (draft is null)
                return new Error("Scaffold.InvalidBlueprint", "Invalid PromptDraft blueprint JSON.", ErrorType.Validation);

            var title = string.IsNullOrWhiteSpace(draft.Title) ? "Study Module" : draft.Title.Trim();
            var comps = NormalizeComponents(draft.Container, title, draft.Components, draft.Selections);
            return new NormalizedBlueprint(
                draft.Container,
                title,
                draft.Description,
                comps,
                cliAllowPlaceholders || draft.AllowPlaceholdersOnFinalize);
        }
        catch (JsonException ex)
        {
            return new Error("Json.ParseError", $"Invalid scaffold blueprint JSON: {ex.Message}", ErrorType.Validation, Path: ex.Path);
        }
    }

    private static IReadOnlyList<BlueprintComponent> NormalizeComponents(
        BlueprintContainerKind container,
        string fallbackTitle,
        IReadOnlyList<BlueprintComponent>? components,
        IReadOnlyList<BlueprintSelection>? selections)
    {
        if (components is { Count: > 0 })
            return components;

        if (selections is { Count: > 0 })
        {
            bool isQuiz = container == BlueprintContainerKind.StandaloneQuiz;
            return
            [
                new BlueprintComponent(
                    isQuiz ? "q1" : "a1",
                    isQuiz ? BlueprintComponentKind.Quiz : BlueprintComponentKind.Article,
                    fallbackTitle,
                    selections)
            ];
        }

        return [];
    }

    private static async Task MaterializeAssetsIntoDirectoryAsync(
        string destDir,
        string sourceBaseDir,
        IReadOnlyList<string> relativeAssets,
        bool requiresPlaceholderImage,
        CancellationToken ct)
    {
        if (requiresPlaceholderImage)
        {
            var phPath = Path.Combine(destDir, "assets", "placeholder.png");
            Directory.CreateDirectory(Path.GetDirectoryName(phPath)!);
            if (!File.Exists(phPath))
                await File.WriteAllBytesAsync(phPath, ScaffoldGenerator.PlaceholderPngBytes, ct);
        }

        foreach (var rel in relativeAssets)
        {
            if (PathConfinement.TryResolveInside(sourceBaseDir, rel, out var srcFull) && File.Exists(srcFull) &&
                PathConfinement.TryResolveInside(destDir, rel, out var dstFull))
            {
                if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dstFull)!);
                File.Copy(srcFull, dstFull, overwrite: true);
            }
        }
    }
}

file static class BlueprintPresetExtensions
{
    public static string TitleOrName(this BlueprintWorkflowPreset wf, string fallback) =>
        !string.IsNullOrWhiteSpace(wf.Name) ? wf.Name! : fallback;
}
