using System.Text.Json.Serialization;

namespace Meloberry.Domain.Scaffolding;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlueprintContainerKind { Session, StandaloneArticle, StandaloneQuiz }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlueprintComponentKind { Article, Quiz }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlueprintPresetScope { Workflow, Component }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlueprintContentMode { None, RawText, MarkdownFile, ImageFile }

public sealed record BlueprintContentSource(
    BlueprintContentMode Mode = BlueprintContentMode.None,
    string? RawText = null,
    string? MarkdownFilePath = null,
    string? ImagePath = null,
    string? Caption = null,
    string? AltText = null);

public sealed record BlueprintSelection(
    string Topic,
    bool Enabled,
    int? DesiredCount = null,
    string? Notes = null,
    IReadOnlyList<string>? Modes = null,
    IReadOnlyDictionary<string, int>? ModeCounts = null);

public sealed record BlueprintSpecInstance(
    string Id,
    string Topic,
    int InstanceNumber,
    string? Subtype = null,
    BlueprintContentSource? Content = null);

public sealed record BlueprintComponent(
    string Id,
    BlueprintComponentKind Kind,
    string Title,
    IReadOnlyList<BlueprintSelection> Selections,
    IReadOnlyList<BlueprintSpecInstance>? OrderedItems = null);

public sealed record BlueprintWorkflowPreset(
    string? Id,
    string? Name,
    string? Description,
    BlueprintContainerKind Container,
    IReadOnlyList<BlueprintSelection>? Selections = null,
    IReadOnlyList<BlueprintComponent>? Components = null);

public sealed record MbyPresetBlueprint(
    int FormatVersion,
    BlueprintPresetScope Scope,
    string Name,
    string? Description = null,
    BlueprintWorkflowPreset? WorkflowPreset = null,
    BlueprintComponent? ComponentPreset = null);

public sealed record PromptDraftBlueprint(
    BlueprintContainerKind Container,
    string? Title,
    string? Description = null,
    IReadOnlyList<BlueprintSelection>? Selections = null,
    IReadOnlyList<BlueprintComponent>? Components = null,
    bool AllowPlaceholdersOnFinalize = false);

public sealed record NormalizedBlueprint(
    BlueprintContainerKind Container,
    string Title,
    string? Description,
    IReadOnlyList<BlueprintComponent> Components,
    bool AllowPlaceholders);

public sealed record ScaffoldMissingContentItem(
    string ComponentId,
    string ComponentTitle,
    string SpecId,
    string Topic);

public sealed record GeneratedScaffoldOutput(
    string Format,
    string Slug,
    string? RawJson,
    IReadOnlyDictionary<string, string>? MarkdownFiles,
    IReadOnlyList<string> ReferencedAssetPaths,
    IReadOnlyList<ScaffoldMissingContentItem> MissingContentSpecs,
    IReadOnlyList<string> PlaceholderSpecsUsed,
    bool RequiresPlaceholderImage,
    IReadOnlyList<string> Warnings);
