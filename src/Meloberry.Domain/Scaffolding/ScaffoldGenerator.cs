using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meloberry.Domain.Common;

namespace Meloberry.Domain.Scaffolding;

/// <summary>
/// Pure domain scaffold generator supporting all 19 content topics in both JSON and Modular Markdown formats,
/// compatible with `.mbypreset` v1 and Wrapper `PromptDraft` blueprints.
/// </summary>
public static class ScaffoldGenerator
{
    public static readonly byte[] PlaceholderPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Result<GeneratedScaffoldOutput> GenerateMarkdown(
        NormalizedBlueprint blueprint,
        IReadOnlyDictionary<string, string> loadedMarkdownFiles,
        bool allowPlaceholders)
    {
        var active = ResolveActiveComponents(blueprint);
        if (active.Count == 0)
            return new Error("Scaffold.EmptyBlueprint", "Cannot scaffold an empty blueprint. Add an Article or Quiz component first.", ErrorType.Validation);

        var title = string.IsNullOrWhiteSpace(blueprint.Title) ? "Study Module" : blueprint.Title.Trim();
        var slug = Slugify(title, "study_module");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var referencedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingSpecs = new List<ScaffoldMissingContentItem>();
        var placeholdersUsed = new List<string>();
        var warnings = new List<string>();
        bool requiresPlaceholderImg = false;
        var componentFileOrder = new List<string>();

        for (int i = 0; i < active.Count; i++)
        {
            var comp = active[i];
            var compTitle = string.IsNullOrWhiteSpace(comp.Title)
                ? (comp.Kind == BlueprintComponentKind.Quiz ? $"Quiz {i + 1}" : $"Article {i + 1}")
                : comp.Title.Trim();
            var compSlug = Slugify(compTitle, comp.Kind == BlueprintComponentKind.Quiz ? $"quiz_{i + 1}" : $"article_{i + 1}");
            var fileName = active.Count == 1 && blueprint.Container != BlueprintContainerKind.Session
                ? $"{compSlug}.md"
                : $"{i + 1:D2}_{compSlug}.md";

            componentFileOrder.Add(fileName);
            files[fileName] = AssembleComponentMarkdown(
                comp,
                compTitle,
                compSlug,
                blueprint,
                loadedMarkdownFiles,
                allowPlaceholders,
                referencedAssets,
                missingSpecs,
                placeholdersUsed,
                warnings,
                ref requiresPlaceholderImg);
        }

        if (blueprint.Container == BlueprintContainerKind.Session)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("kind: session");
            sb.AppendLine($"name: {YamlQuote(title)}");
            if (!string.IsNullOrWhiteSpace(blueprint.Description))
                sb.AppendLine($"description: {YamlQuote(blueprint.Description.Trim())}");
            sb.AppendLine("order:");
            foreach (var f in componentFileOrder)
                sb.AppendLine($"  - {f}");
            sb.AppendLine("---");
            files["_session.md"] = sb.ToString();
        }

        return new GeneratedScaffoldOutput(
            "md",
            slug,
            null,
            files,
            referencedAssets.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            missingSpecs,
            placeholdersUsed,
            requiresPlaceholderImg,
            warnings);
    }

    public static Result<GeneratedScaffoldOutput> GenerateJson(
        NormalizedBlueprint blueprint,
        bool allowPlaceholders = true)
    {
        var active = ResolveActiveComponents(blueprint);
        if (active.Count == 0)
            return new Error("Scaffold.EmptyBlueprint", "Cannot scaffold an empty blueprint. Add an Article or Quiz component first.", ErrorType.Validation);

        foreach (var comp in active)
        {
            var missingQty = comp.Selections.FirstOrDefault(s => s.Enabled && s.DesiredCount is <= 0);
            if (missingQty is not null)
                return new Error("Scaffold.InvalidQty", $"Spec '{missingQty.Topic}' in '{comp.Title}' has non-positive Qty.", ErrorType.Validation);
        }

        var title = string.IsNullOrWhiteSpace(blueprint.Title) ? "Draft Study Template" : blueprint.Title.Trim();
        var description = string.IsNullOrWhiteSpace(blueprint.Description) ? null : blueprint.Description.Trim();
        bool requiresImage = false;
        var warnings = new List<string>();
        var missingSpecs = new List<ScaffoldMissingContentItem>();
        var placeholdersUsed = new List<string>();
        var referencedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        object rootPayload;
        if (blueprint.Container == BlueprintContainerKind.StandaloneArticle)
        {
            rootPayload = BuildArticleObject(active[0], title, allowPlaceholders, ref requiresImage, missingSpecs, placeholdersUsed, referencedAssets, warnings);
        }
        else if (blueprint.Container == BlueprintContainerKind.StandaloneQuiz)
        {
            rootPayload = BuildQuizObject(active[0], title, description, isStandalone: true, allowPlaceholders, ref requiresImage, missingSpecs, placeholdersUsed, referencedAssets, warnings);
        }
        else
        {
            var builtComponents = new List<object>();
            foreach (var comp in active)
            {
                builtComponents.Add(comp.Kind == BlueprintComponentKind.Article
                    ? BuildArticleObject(comp, null, allowPlaceholders, ref requiresImage, missingSpecs, placeholdersUsed, referencedAssets, warnings)
                    : BuildQuizObject(comp, null, null, isStandalone: false, allowPlaceholders, ref requiresImage, missingSpecs, placeholdersUsed, referencedAssets, warnings));
            }

            rootPayload = new Dictionary<string, object?>
            {
                ["kind"] = "session",
                ["name"] = title,
                ["description"] = description,
                ["components"] = builtComponents
            };
        }

        var rawJson = JsonSerializer.Serialize(rootPayload, JsonOptions);
        return new GeneratedScaffoldOutput(
            "json",
            Slugify(title, "session"),
            rawJson,
            null,
            referencedAssets.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            missingSpecs,
            placeholdersUsed,
            requiresImage,
            warnings);
    }

    private static string AssembleComponentMarkdown(
        BlueprintComponent comp,
        string compTitle,
        string compRef,
        NormalizedBlueprint blueprint,
        IReadOnlyDictionary<string, string> loadedMarkdownFiles,
        bool allowPlaceholders,
        HashSet<string> referencedAssets,
        List<ScaffoldMissingContentItem> missingSpecs,
        List<string> placeholdersUsed,
        List<string> warnings,
        ref bool requiresPlaceholderImg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine(comp.Kind == BlueprintComponentKind.Quiz ? "kind: quiz" : "kind: article");
        if (comp.Kind == BlueprintComponentKind.Quiz)
        {
            sb.AppendLine("quizType: mixed");
            if (blueprint.Container == BlueprintContainerKind.StandaloneQuiz)
            {
                sb.AppendLine($"name: {YamlQuote(compTitle)}");
                if (!string.IsNullOrWhiteSpace(blueprint.Description))
                    sb.AppendLine($"description: {YamlQuote(blueprint.Description.Trim())}");
            }
            else
            {
                sb.AppendLine($"title: {YamlQuote(compTitle)}");
            }
        }
        else
        {
            sb.AppendLine($"title: {YamlQuote(compTitle)}");
        }
        sb.AppendLine($"ref: {compRef}");
        sb.AppendLine("---");
        sb.AppendLine();

        var instances = BuildInstanceSequence(comp, warnings);
        if (instances.Count == 0)
        {
            if (allowPlaceholders)
            {
                var fallback = comp.Kind == BlueprintComponentKind.Quiz
                    ? BuildPlaceholderBody("mcq", null, 1, ref requiresPlaceholderImg)
                    : "## Draft Section\n\nPlaceholder content.";
                sb.AppendLine(fallback);
            }
            else
            {
                missingSpecs.Add(new ScaffoldMissingContentItem(comp.Id, compTitle, $"{comp.Id}_empty", comp.Kind == BlueprintComponentKind.Quiz ? "quiz" : "article"));
            }
            return sb.ToString();
        }

        var chunks = new List<string>();
        var linkIds = new List<string>();
        bool hasNonLinkArticleBlock = false;
        int linkCounter = 0;

        foreach (var inst in instances)
        {
            var topicLower = inst.Topic.Trim().ToLowerInvariant();
            if (inst.Content?.Mode == BlueprintContentMode.ImageFile)
            {
                bool hasImg = !string.IsNullOrWhiteSpace(inst.Content.ImagePath);
                bool needsBody = topicLower != "image" && string.IsNullOrWhiteSpace(inst.Content.RawText);
                if (!hasImg || (needsBody && !allowPlaceholders))
                {
                    if (!allowPlaceholders)
                    {
                        missingSpecs.Add(new ScaffoldMissingContentItem(comp.Id, compTitle, inst.Id, inst.Topic));
                        continue;
                    }
                    placeholdersUsed.Add(inst.Id);
                    if (!hasImg)
                    {
                        chunks.Add(BuildPlaceholderOrTrackLink(inst.Topic, inst.Subtype, inst.InstanceNumber, linkIds, ref linkCounter, ref hasNonLinkArticleBlock, ref requiresPlaceholderImg));
                        continue;
                    }
                }

                var relImg = inst.Content.ImagePath!.Trim().Replace('\\', '/');
                string formattedImg;
                if (needsBody)
                {
                    placeholdersUsed.Add(inst.Id);
                    formattedImg = BuildImageDirectivePlaceholder(topicLower, inst.Subtype, relImg, inst.InstanceNumber, ref linkCounter, linkIds, ref hasNonLinkArticleBlock);
                }
                else
                {
                    string? lId = null;
                    string? lTitle = null;
                    if (topicLower == "link")
                    {
                        linkCounter++;
                        lId = $"card-{linkCounter}";
                        lTitle = $"Reference {linkCounter}";
                        linkIds.Add(lId);
                    }
                    else if (comp.Kind == BlueprintComponentKind.Article)
                    {
                        hasNonLinkArticleBlock = true;
                    }

                    formattedImg = MarkdownSmartWrapper.SmartWrapSingleFragment(
                        inst.Topic, inst.Subtype, inst.Content.RawText ?? string.Empty,
                        relImg, inst.Content.Caption, inst.Content.AltText, out bool reqPh, lId, lTitle);
                    if (reqPh) requiresPlaceholderImg = true;
                }

                MarkdownSmartWrapper.CollectReferencedAssets(formattedImg, referencedAssets);
                MarkdownSmartWrapper.TryAddRelativeAsset(relImg, referencedAssets);
                chunks.Add(formattedImg);
                continue;
            }

            var resolvedContent = ResolveSpecText(inst, loadedMarkdownFiles);
            if (string.IsNullOrWhiteSpace(resolvedContent))
            {
                if (!allowPlaceholders)
                {
                    missingSpecs.Add(new ScaffoldMissingContentItem(comp.Id, compTitle, inst.Id, inst.Topic));
                    continue;
                }
                placeholdersUsed.Add(inst.Id);
                chunks.Add(BuildPlaceholderOrTrackLink(inst.Topic, inst.Subtype, inst.InstanceNumber, linkIds, ref linkCounter, ref hasNonLinkArticleBlock, ref requiresPlaceholderImg));
                continue;
            }

            string? cardId = null;
            string? cardTitle = null;
            if (topicLower == "link")
            {
                linkCounter++;
                cardId = $"card-{linkCounter}";
                cardTitle = $"Reference {linkCounter}";
                linkIds.Add(cardId);
            }
            else if (comp.Kind == BlueprintComponentKind.Article)
            {
                hasNonLinkArticleBlock = true;
            }

            var wrapped = MarkdownSmartWrapper.SmartWrapSingleFragment(
                inst.Topic, inst.Subtype, resolvedContent, null, null, null, out bool needsPh, cardId, cardTitle);
            if (needsPh) requiresPlaceholderImg = true;
            MarkdownSmartWrapper.CollectReferencedAssets(wrapped, referencedAssets);
            chunks.Add(wrapped);
        }

        if (comp.Kind == BlueprintComponentKind.Article && linkIds.Count > 0 && !hasNonLinkArticleBlock)
        {
            var refs = string.Join(", ", linkIds.Select((id, idx) => $"[Reference {idx + 1}](mby://card/{id})"));
            chunks.Add($"Hover over {refs} for details.");
        }

        sb.Append(string.Join("\n\n", chunks));
        sb.AppendLine();
        return sb.ToString();
    }

    private static string BuildPlaceholderOrTrackLink(
        string topic, string? subtype, int num,
        List<string> linkIds, ref int linkCounter, ref bool hasNonLinkArticleBlock, ref bool requiresPlaceholderImg)
    {
        if (string.Equals(topic, "link", StringComparison.OrdinalIgnoreCase))
        {
            linkCounter++;
            var id = $"card-{linkCounter}";
            linkIds.Add(id);
            return $":::link{{id=\"{id}\" title=\"Reference Card {linkCounter}\"}}\nPlaceholder hover note.\n:::";
        }
        if (IsArticleBlockTopic(topic))
            hasNonLinkArticleBlock = true;
        return BuildPlaceholderBody(topic, subtype, num, ref requiresPlaceholderImg);
    }

    private static string BuildImageDirectivePlaceholder(
        string topic, string? subtype, string relImg, int num,
        ref int linkCounter, List<string> linkIds, ref bool hasNonLinkArticleBlock)
    {
        if (topic == "link")
        {
            linkCounter++;
            var id = $"card-{linkCounter}";
            linkIds.Add(id);
            return $":::link{{id=\"{id}\" title=\"Reference Card {linkCounter}\" image=\"{relImg}\"}}\nHover note placeholder.\n:::";
        }

        hasNonLinkArticleBlock = hasNonLinkArticleBlock || IsArticleBlockTopic(topic);
        if (topic == "flashcard")
        {
            var target = MarkdownSmartWrapper.FormatMarkdownImageTarget(relImg);
            return $":::flashcard\n:::front\n![]({target})\nFlashcard Front {num}\n:::back\nFlashcard Answer {num}\n:::";
        }

        bool dummy = false;
        var attrs = MarkdownSmartWrapper.BuildDefaultDirectiveAttributes(topic, subtype, relImg, ref dummy);
        if (topic == "numeric")
        {
            attrs.Insert(0, "tolerance=\"5%\"");
            attrs.Insert(0, "answer=42");
        }
        else if (topic == "case")
        {
            attrs.Insert(0, $"title=\"Case Vignette {num}\"");
        }

        var header = attrs.Count == 0 ? $":::{topic}" : $":::{topic}{{{string.Join(" ", attrs)}}}";
        var body = topic switch
        {
            "relabel" => "prompt: Identify labels.\nboxes:\n  - label: \"Structure A\"\n    rect: [0.1, 0.1, 0.25, 0.1]",
            "diagram-explore" => "- Landmark A: Placeholder annotation.",
            "mcq" => $"MCQ Placeholder {num}: replace with stem.\n- [x] Correct Option\n- [ ] Distractor Option",
            "spot-error" => $"In [context {num}], the value is [~~wrong~~ -> right].",
            "numeric" => $"Numeric question placeholder {num}.",
            "case" => $"Clinical vignette stem placeholder {num}.\n::::mcq\nPrimary finding?\n- [x] Finding A\n- [ ] Finding B\n::::",
            _ => $"Placeholder for {topic} #{num}."
        };
        return $"{header}\n{body}\n:::";
    }

    private static string BuildPlaceholderBody(string topic, string? subtype, int num, ref bool requiresPlaceholderImg)
    {
        var t = topic.Trim().ToLowerInvariant();
        if (t == "text") return $"## Section {num}\n\nPlaceholder prose content.";
        if (t == "image")
        {
            requiresPlaceholderImg = true;
            return $"![Placeholder Figure {num}](assets/placeholder.png \"Figure {num}\")";
        }
        if (t == "link")
            return $":::link{{id=\"card-{num}\" title=\"Reference Card {num}\"}}\nPlaceholder hover note.\n:::";

        return t switch
        {
            "callout" => $":::callout{{tone=\"{(subtype is "pearl" or "clinical" or "warning" ? subtype : "pearl")}\" title=\"Key Point {num}\"}}\nPlaceholder takeaway.\n:::",
            "table" => $":::table{{title=\"Table {num}\"}}\n| Feature | Entity A | Entity B |\n| --- | --- | --- |\n| Property | Value A | Value B |\n:::",
            "diagram-explore" => BuildImagePlaceholderDirective("diagram-explore", $"Interactive Diagram {num}", "boxes:\n  - label: \"Landmark A\"\n    note: \"Placeholder annotation.\"\n    rect: [0.1, 0.1, 0.25, 0.15]", ref requiresPlaceholderImg),
            "reveal" => $":::reveal{{title=\"Step Reveal {num}\"}}\n> Step 1\nPlaceholder reveal step.\n:::",
            "mcq" => string.Equals(subtype, "multi", StringComparison.OrdinalIgnoreCase)
                ? $":::mcq{{multi=true}}\nMCQ Placeholder {num}: replace with stem.\n- [x] Correct Option\n- [ ] Distractor Option\n:::"
                : $":::mcq\nMCQ Placeholder {num}: replace with stem.\n- [x] Correct Option\n- [ ] Distractor Option\n:::",
            "flashcard" => $":::flashcard\n:::front\nFlashcard Front {num}\n:::back\nFlashcard Answer {num}\n:::",
            "fill-blank" => $":::fill-blank{{fuzzy=true}}\nPlaceholder term {num}: {{{{answer}}}}.\n:::",
            "assertion-reason" => $":::assertion-reason{{answer=\"{(string.Equals(subtype, "both-false", StringComparison.OrdinalIgnoreCase) ? "both-false" : "both-explain")}\" scheme=\"{(subtype == "5opt" || string.Equals(subtype, "both-false", StringComparison.OrdinalIgnoreCase) ? "5opt" : "4opt")}\"}}\nA: Assertion placeholder {num}.\nR: Reason placeholder {num}.\n:::",
            "spot-error" => $":::spot-error\nIn [context {num}], the value is [~~wrong~~ -> right].\n:::",
            "diff" => $":::diff{{mode=\"{(subtype is "spot-wrong" or "rearrange" ? subtype : "fill")}\"}}\n| Property | A | B |\n| --- | --- | --- |\n| Item 1 | {(subtype == "spot-wrong" ? "~~Wrong~~ -> Right" : (subtype == "rearrange" ? "Val A" : "{{Val A}}"))} | {(subtype == "fill" || string.IsNullOrWhiteSpace(subtype) ? "{{Val B}}" : "Val B")} |\n:::",
            "mechanism" => $":::mechanism{{title=\"Pathway {num}\"}}\n1. Initial trigger\n2. Downstream effect\n:::",
            "relabel" => BuildImagePlaceholderDirective("relabel", $"mode=\"{(subtype is "reveal" or "bank" or "recall" ? subtype : "bank")}\" style=\"{(subtype is "solid" or "blur" or "pixelate" or "whiteout" ? subtype : "blur")}\"", "prompt: Identify labels.\nboxes:\n  - label: \"Structure A\"\n    rect: [0.1, 0.1, 0.25, 0.1]", ref requiresPlaceholderImg),
            "classify" => $":::classify{{title=\"Classify {num}\"}}\n[Group A]\n- Item 1\n[Group B]\n- Item 2\n:::",
            "match" => $":::match{{title=\"Match {num}\"}}\n- Term 1 :: Definition 1\n- Term 2 :: Definition 2\n:::",
            "numeric" => $":::numeric{{answer=42 tolerance=\"5%\"}}\nNumeric question placeholder {num}.\n:::",
            "case" => $":::case{{title=\"Case Vignette {num}\"}}\nClinical vignette stem placeholder {num}.\n::::mcq\nPrimary finding?\n- [x] Finding A\n- [ ] Finding B\n::::\n::::numeric{{answer=10}}\nExpected value?\n::::\n:::",
            _ => $":::{t}\nPlaceholder for {t} #{num}.\n:::"
        };
    }

    private static string BuildImagePlaceholderDirective(string topic, string extraAttrs, string body, ref bool requiresPlaceholderImg)
    {
        requiresPlaceholderImg = true;
        var attrs = topic == "diagram-explore"
            ? $"image=\"assets/placeholder.png\" title={YamlQuote(extraAttrs)}"
            : $"image=\"assets/placeholder.png\" {extraAttrs}";
        return $":::{topic}{{{attrs}}}\n{body}\n:::";
    }

    private static Dictionary<string, object?> BuildArticleObject(
        BlueprintComponent comp,
        string? standaloneTitle,
        bool allowPlaceholders,
        ref bool requiresImage,
        List<ScaffoldMissingContentItem> missingSpecs,
        List<string> placeholdersUsed,
        HashSet<string> referencedAssets,
        List<string> warnings)
    {
        var sequence = BuildInstanceSequence(comp, warnings);
        var blocks = new List<object>();
        var links = new List<object>();
        var linkIds = new List<string>();
        var selLookup = comp.Selections.Where(s => s.Enabled).ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase);

        foreach (var inst in sequence)
        {
            selLookup.TryGetValue(inst.Topic, out var sel);
            var modes = sel?.Modes ?? [];
            int num = inst.InstanceNumber;
            var t = inst.Topic.Trim().ToLowerInvariant();

            if (!allowPlaceholders && (inst.Content is null || inst.Content.Mode == BlueprintContentMode.None))
            {
                missingSpecs.Add(new ScaffoldMissingContentItem(comp.Id, comp.Title, inst.Id, inst.Topic));
                continue;
            }
            if (inst.Content is null || inst.Content.Mode == BlueprintContentMode.None)
                placeholdersUsed.Add(inst.Id);

            if (t == "link")
            {
                var cardId = $"card-{links.Count + 1}";
                linkIds.Add(cardId);
                var card = new Dictionary<string, object?>
                {
                    ["id"] = cardId,
                    ["title"] = $"Reference Card {links.Count + 1}",
                    ["text"] = !string.IsNullOrWhiteSpace(inst.Content?.RawText) ? inst.Content.RawText : "Hover card explanation placeholder."
                };
                if (!string.IsNullOrWhiteSpace(inst.Content?.ImagePath))
                {
                    var img = inst.Content.ImagePath.Trim().Replace('\\', '/');
                    card["imagePath"] = img;
                    MarkdownSmartWrapper.TryAddRelativeAsset(img, referencedAssets);
                }
                links.Add(card);
                continue;
            }

            switch (t)
            {
                case "image":
                {
                    var img = !string.IsNullOrWhiteSpace(inst.Content?.ImagePath) ? inst.Content.ImagePath.Trim().Replace('\\', '/') : "assets/placeholder.png";
                    if (img == "assets/placeholder.png") requiresImage = true;
                    else MarkdownSmartWrapper.TryAddRelativeAsset(img, referencedAssets);
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = "image",
                        ["imagePath"] = img,
                        ["caption"] = inst.Content?.Caption ?? $"Figure {num} placeholder caption",
                        ["alt"] = inst.Content?.AltText ?? $"Figure {num} placeholder"
                    });
                    break;
                }
                case "callout":
                {
                    var tone = (inst.Subtype is "pearl" or "clinical" or "warning"
                        ? inst.Subtype
                        : modes.FirstOrDefault(m => m is "pearl" or "clinical" or "warning")) ?? "pearl";
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = "callout",
                        ["tone"] = tone,
                        ["title"] = $"Callout {num}",
                        ["text"] = !string.IsNullOrWhiteSpace(inst.Content?.RawText)
                            ? inst.Content.RawText
                            : (sel?.Notes is { Length: > 0 } ? sel.Notes : $"Replace with {tone} takeaway.")
                    });
                    break;
                }
                case "table":
                {
                    var hasTableSubtype = !string.IsNullOrWhiteSpace(inst.Subtype);
                    bool hideable = string.Equals(inst.Subtype, "hideableCols", StringComparison.OrdinalIgnoreCase)
                        || (!hasTableSubtype && modes.Contains("hideableCols", StringComparer.OrdinalIgnoreCase));
                    bool sticky = string.Equals(inst.Subtype, "stickyFirstCol", StringComparison.OrdinalIgnoreCase)
                        || (!hasTableSubtype && modes.Contains("stickyFirstCol", StringComparer.OrdinalIgnoreCase));
                    var tableObj = new Dictionary<string, object?>
                    {
                        ["kind"] = "table",
                        ["title"] = $"Comparison Table {num}",
                        ["headers"] = new[] { "Feature", "Item A", "Item B" },
                        ["rows"] = new[] { new[] { "Row 1", "Value A", "Value B" } }
                    };
                    if (sticky) tableObj["stickyFirstCol"] = true;
                    if (hideable) tableObj["hideableCols"] = new[] { 2, 3 };
                    blocks.Add(tableObj);
                    break;
                }
                case "diagram-explore":
                {
                    var img = !string.IsNullOrWhiteSpace(inst.Content?.ImagePath) ? inst.Content.ImagePath.Trim().Replace('\\', '/') : "assets/placeholder.png";
                    if (img == "assets/placeholder.png") requiresImage = true;
                    else MarkdownSmartWrapper.TryAddRelativeAsset(img, referencedAssets);
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = "diagram-explore",
                        ["imagePath"] = img,
                        ["title"] = $"Interactive Diagram {num}",
                        ["hotspots"] = new[]
                        {
                            new Dictionary<string, object?> { ["label"] = "Landmark A", ["note"] = "Placeholder annotation.", ["rect"] = new[] { 0.1, 0.1, 0.25, 0.15 } }
                        }
                    });
                    break;
                }
                case "reveal":
                {
                    bool showFirst = string.Equals(inst.Subtype, "showFirst", StringComparison.OrdinalIgnoreCase)
                        || modes.Contains("showFirst", StringComparer.OrdinalIgnoreCase);
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = "reveal",
                        ["title"] = $"Step Reveal {num}",
                        ["showFirst"] = showFirst,
                        ["steps"] = new[] { new Dictionary<string, object?> { ["heading"] = "Step 1", ["body"] = "Placeholder reveal step." } }
                    });
                    break;
                }
                default:
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = "text",
                        ["text"] = !string.IsNullOrWhiteSpace(inst.Content?.RawText)
                            ? inst.Content.RawText
                            : $"## Section {num}\n\n" + (sel?.Notes is { Length: > 0 } ? sel.Notes : "Replace with study prose.")
                    });
                    break;
            }
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["kind"] = "text",
                ["text"] = "## Draft Section\n\nReplace with study prose."
            });
        }

        if (linkIds.Count > 0)
        {
            var refsLine = string.Join(", ", linkIds.Select((id, i) => $"[Reference {i + 1}](mby://card/{id})"));
            var firstTextBlock = blocks
                .OfType<Dictionary<string, object?>>()
                .FirstOrDefault(b => b.TryGetValue("kind", out var k) && string.Equals(k as string, "text", StringComparison.OrdinalIgnoreCase));
            if (firstTextBlock is not null && firstTextBlock.TryGetValue("text", out var existingObj) && existingObj is string existingText)
            {
                firstTextBlock["text"] = $"{existingText}\n\nReferences: {refsLine}.";
            }
            else
            {
                blocks.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "text",
                    ["text"] = $"Hover over {refsLine} for details."
                });
            }
        }

        var article = new Dictionary<string, object?>
        {
            ["kind"] = "article",
            ["title"] = standaloneTitle ?? (string.IsNullOrWhiteSpace(comp.Title) ? "Study Article" : comp.Title),
            ["blocks"] = blocks
        };
        if (links.Count > 0) article["links"] = links;
        return article;
    }

    private static Dictionary<string, object?> BuildQuizObject(
        BlueprintComponent comp,
        string? standaloneName,
        string? standaloneDescription,
        bool isStandalone,
        bool allowPlaceholders,
        ref bool requiresImage,
        List<ScaffoldMissingContentItem> missingSpecs,
        List<string> placeholdersUsed,
        HashSet<string> referencedAssets,
        List<string> warnings)
    {
        var sequence = BuildInstanceSequence(comp, warnings);
        var questions = new List<object>();
        var selLookup = comp.Selections.Where(s => s.Enabled).ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase);

        foreach (var inst in sequence)
        {
            selLookup.TryGetValue(inst.Topic, out var sel);
            var modes = sel?.Modes ?? [];
            int num = inst.InstanceNumber;

            if (!allowPlaceholders && (inst.Content is null || inst.Content.Mode == BlueprintContentMode.None))
            {
                missingSpecs.Add(new ScaffoldMissingContentItem(comp.Id, comp.Title, inst.Id, inst.Topic));
                continue;
            }
            if (inst.Content is null || inst.Content.Mode == BlueprintContentMode.None)
                placeholdersUsed.Add(inst.Id);

            switch (inst.Topic.Trim().ToLowerInvariant())
            {
                case "flashcard":
                {
                    bool isMixed = string.Equals(inst.Subtype, "mixed", StringComparison.OrdinalIgnoreCase)
                        || (string.IsNullOrWhiteSpace(inst.Subtype) && modes.Contains("mixed", StringComparer.OrdinalIgnoreCase));
                    bool diagramMode = isMixed ? num % 2 == 0 : !string.Equals(inst.Subtype, "text", StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(inst.Subtype, "diagram", StringComparison.OrdinalIgnoreCase) || modes.Contains("diagram", StringComparer.OrdinalIgnoreCase));
                    var img = !string.IsNullOrWhiteSpace(inst.Content?.ImagePath) ? inst.Content.ImagePath.Trim().Replace('\\', '/') : (diagramMode ? "assets/placeholder.png" : null);
                    if (img == "assets/placeholder.png") requiresImage = true;
                    else if (!string.IsNullOrWhiteSpace(img)) MarkdownSmartWrapper.TryAddRelativeAsset(img, referencedAssets);

                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "flashcard",
                        ["front"] = img is not null
                            ? new Dictionary<string, object?> { ["text"] = $"Identify structure ({num})", ["imagePath"] = img }
                            : new Dictionary<string, object?> { ["text"] = $"Flashcard Front {num}" },
                        ["back"] = new Dictionary<string, object?> { ["text"] = $"Flashcard Answer {num}" }
                    });
                    break;
                }
                case "fill-blank":
                {
                    bool hasSub = !string.IsNullOrWhiteSpace(inst.Subtype);
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "fill-blank",
                        ["text"] = $"Fill-in-the-blank prompt {num}: the target term is {{{{answer}}}}.",
                        ["fuzzy"] = string.Equals(inst.Subtype, "fuzzy", StringComparison.OrdinalIgnoreCase) || (!hasSub && modes.Contains("fuzzy", StringComparer.OrdinalIgnoreCase)),
                        ["caseSensitive"] = string.Equals(inst.Subtype, "caseSensitive", StringComparison.OrdinalIgnoreCase) || (!hasSub && modes.Contains("caseSensitive", StringComparer.OrdinalIgnoreCase)),
                        ["explanation"] = $"Explanation placeholder {num}."
                    });
                    break;
                }
                case "assertion-reason":
                {
                    bool hasBothFalse = modes.Contains("both-false", StringComparer.OrdinalIgnoreCase) || string.Equals(inst.Subtype, "both-false", StringComparison.OrdinalIgnoreCase);
                    var scheme = inst.Subtype is "4opt" or "5opt" ? inst.Subtype : (hasBothFalse || modes.Contains("5opt", StringComparer.OrdinalIgnoreCase) ? "5opt" : "4opt");
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "assertion-reason",
                        ["assertion"] = $"Assertion statement {num}.",
                        ["reason"] = $"Reason statement {num}.",
                        ["answer"] = hasBothFalse ? "both-false" : "both-explain",
                        ["scheme"] = scheme,
                        ["explanation"] = $"Assertion-reason explanation {num}."
                    });
                    break;
                }
                case "spot-error":
                {
                    bool hasSub = !string.IsNullOrWhiteSpace(inst.Subtype);
                    var spot = new Dictionary<string, object?>
                    {
                        ["type"] = "spot-error",
                        ["text"] = $"Statement {num} with [valid span] and [~~incorrect term~~ -> corrected term].",
                        ["requireFix"] = string.Equals(inst.Subtype, "requireFix", StringComparison.OrdinalIgnoreCase) || (!hasSub && modes.Contains("requireFix", StringComparer.OrdinalIgnoreCase)),
                        ["allowNoError"] = string.Equals(inst.Subtype, "allowNoError", StringComparison.OrdinalIgnoreCase) || (!hasSub && modes.Contains("allowNoError", StringComparer.OrdinalIgnoreCase)),
                        ["explanation"] = $"Spot-error explanation {num}."
                    };
                    if (!string.IsNullOrWhiteSpace(inst.Content?.ImagePath))
                    {
                        var img = inst.Content.ImagePath.Trim().Replace('\\', '/');
                        spot["imagePath"] = img;
                        MarkdownSmartWrapper.TryAddRelativeAsset(img, referencedAssets);
                    }
                    questions.Add(spot);
                    break;
                }
                case "diff":
                {
                    var diffMode = (inst.Subtype is "fill" or "spot-wrong" or "rearrange" ? inst.Subtype : modes.FirstOrDefault(m => m is "fill" or "spot-wrong" or "rearrange")) ?? "fill";
                    var cellA = diffMode switch { "spot-wrong" => "~~Incorrect~~ -> Correct", "rearrange" => "Correct Value A", _ => "{{Value A}}" };
                    var cellB = diffMode == "fill" ? "{{Value B}}" : "Value B";
                    var diffObj = new Dictionary<string, object?>
                    {
                        ["type"] = "diff",
                        ["mode"] = diffMode,
                        ["title"] = $"Differentiation {num}",
                        ["headers"] = new[] { "Property", "Entity A", "Entity B" },
                        ["rows"] = new[] { new[] { "Property 1", cellA, cellB } },
                        ["explanation"] = $"Comparison explanation {num}."
                    };
                    if (modes.Contains("bank", StringComparer.OrdinalIgnoreCase)) diffObj["bank"] = true;
                    questions.Add(diffObj);
                    break;
                }
                case "mechanism":
                {
                    var steps = new List<Dictionary<string, object?>>
                    {
                        new() { ["orderGroup"] = 1, ["text"] = "Step 1" },
                        new() { ["orderGroup"] = 2, ["text"] = "Step 2A" }
                    };
                    if (modes.Contains("parallel", StringComparer.OrdinalIgnoreCase) || string.Equals(inst.Subtype, "parallel", StringComparison.OrdinalIgnoreCase))
                        steps.Add(new Dictionary<string, object?> { ["orderGroup"] = 2, ["text"] = "Step 2B (parallel)" });
                    if (modes.Contains("distractors", StringComparer.OrdinalIgnoreCase) || string.Equals(inst.Subtype, "distractors", StringComparison.OrdinalIgnoreCase))
                        steps.Add(new Dictionary<string, object?> { ["isDistractor"] = true, ["text"] = "Distractor step" });
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "mechanism",
                        ["title"] = $"Mechanism Pathway {num}",
                        ["steps"] = steps,
                        ["explanation"] = $"Mechanism explanation {num}."
                    });
                    break;
                }
                case "relabel":
                {
                    var img = !string.IsNullOrWhiteSpace(inst.Content?.ImagePath) ? inst.Content.ImagePath.Trim().Replace('\\', '/') : "assets/placeholder.png";
                    if (img == "assets/placeholder.png") requiresImage = true;
                    else MarkdownSmartWrapper.TryAddRelativeAsset(img, referencedAssets);
                    var relMode = (inst.Subtype is "reveal" or "bank" or "recall" ? inst.Subtype : modes.FirstOrDefault(m => m is "reveal" or "bank" or "recall")) ?? "bank";
                    var relStyle = (inst.Subtype is "solid" or "blur" or "pixelate" or "whiteout" ? inst.Subtype : modes.FirstOrDefault(m => m is "solid" or "blur" or "pixelate" or "whiteout")) ?? "blur";
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "relabel",
                        ["imagePath"] = img,
                        ["prompt"] = $"Identify labeled structures ({num}).",
                        ["mode"] = relMode,
                        ["style"] = relStyle,
                        ["boxes"] = new object[]
                        {
                            new Dictionary<string, object?> { ["label"] = "Label A", ["rect"] = new[] { 0.1, 0.1, 0.25, 0.1 } },
                            new Dictionary<string, object?> { ["label"] = "Label B", ["rect"] = new[] { 0.55, 0.4, 0.25, 0.1 } }
                        }
                    });
                    break;
                }
                case "classify":
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "classify",
                        ["title"] = $"Classification {num}",
                        ["buckets"] = new[] { "Category A", "Category B" },
                        ["items"] = new object[]
                        {
                            new Dictionary<string, object?> { ["bucket"] = "Category A", ["text"] = "Item 1" },
                            new Dictionary<string, object?> { ["bucket"] = "Category B", ["text"] = "Item 2" }
                        }
                    });
                    break;
                case "match":
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "match",
                        ["title"] = $"Matching {num}",
                        ["pairs"] = new object[]
                        {
                            new Dictionary<string, object?> { ["left"] = "Term A", ["right"] = "Definition 1" },
                            new Dictionary<string, object?> { ["left"] = "Term B", ["right"] = "Definition 2" }
                        }
                    });
                    break;
                case "numeric":
                {
                    var numQ = new Dictionary<string, object?>
                    {
                        ["type"] = "numeric",
                        ["text"] = $"Numeric question placeholder {num}.",
                        ["answer"] = 42,
                        ["tolerance"] = "5%",
                        ["unit"] = "units"
                    };
                    if (!string.IsNullOrWhiteSpace(inst.Content?.ImagePath))
                    {
                        var img = inst.Content.ImagePath.Trim().Replace('\\', '/');
                        numQ["imagePath"] = img;
                        MarkdownSmartWrapper.TryAddRelativeAsset(img, referencedAssets);
                    }
                    questions.Add(numQ);
                    break;
                }
                case "case":
                {
                    var caseQ = new Dictionary<string, object?>
                    {
                        ["type"] = "case",
                        ["title"] = $"Case Vignette {num}",
                        ["stem"] = $"Clinical vignette stem placeholder {num}.",
                        ["questions"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "mcq",
                                ["text"] = "Primary finding?",
                                ["options"] = new object[]
                                {
                                    new Dictionary<string, object?> { ["text"] = "Finding A", ["isCorrect"] = true },
                                    new Dictionary<string, object?> { ["text"] = "Finding B", ["isCorrect"] = false }
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["type"] = "numeric",
                                ["text"] = "Expected value?",
                                ["answer"] = 10
                            }
                        }
                    };
                    if (!string.IsNullOrWhiteSpace(inst.Content?.ImagePath))
                    {
                        var img = inst.Content.ImagePath.Trim().Replace('\\', '/');
                        caseQ["imagePath"] = img;
                        MarkdownSmartWrapper.TryAddRelativeAsset(img, referencedAssets);
                    }
                    questions.Add(caseQ);
                    break;
                }
                default:
                {
                    bool isMixedMcq = string.Equals(inst.Subtype, "mixed", StringComparison.OrdinalIgnoreCase)
                        || (string.IsNullOrWhiteSpace(inst.Subtype) && modes.Contains("mixed", StringComparer.OrdinalIgnoreCase));
                    bool multi = isMixedMcq ? num % 2 == 0 : !string.Equals(inst.Subtype, "single", StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(inst.Subtype, "multi", StringComparison.OrdinalIgnoreCase) || modes.Contains("multi", StringComparer.OrdinalIgnoreCase));
                    var mcqQ = new Dictionary<string, object?>
                    {
                        ["type"] = "mcq",
                        ["text"] = $"MCQ Placeholder {num}: replace with stem.",
                        ["isMultiCorrect"] = multi,
                        ["explanation"] = $"Rationale placeholder {num}.",
                        ["options"] = multi
                            ? new object[]
                            {
                                new Dictionary<string, object?> { ["text"] = "Correct Option A", ["isCorrect"] = true },
                                new Dictionary<string, object?> { ["text"] = "Correct Option B", ["isCorrect"] = true },
                                new Dictionary<string, object?> { ["text"] = "Distractor C", ["isCorrect"] = false }
                            }
                            : new object[]
                            {
                                new Dictionary<string, object?> { ["text"] = "Correct Option A", ["isCorrect"] = true },
                                new Dictionary<string, object?> { ["text"] = "Distractor B", ["isCorrect"] = false },
                                new Dictionary<string, object?> { ["text"] = "Distractor C", ["isCorrect"] = false },
                                new Dictionary<string, object?> { ["text"] = "Distractor D", ["isCorrect"] = false }
                            }
                    };
                    if (!string.IsNullOrWhiteSpace(inst.Content?.ImagePath))
                    {
                        var img = inst.Content.ImagePath.Trim().Replace('\\', '/');
                        mcqQ["imagePath"] = img;
                        MarkdownSmartWrapper.TryAddRelativeAsset(img, referencedAssets);
                    }
                    questions.Add(mcqQ);
                    break;
                }
            }
        }

        var quiz = new Dictionary<string, object?>
        {
            ["kind"] = "quiz",
            ["quizType"] = "mixed",
            ["questions"] = questions
        };
        if (isStandalone)
        {
            quiz["name"] = standaloneName ?? (string.IsNullOrWhiteSpace(comp.Title) ? "Draft Quiz" : comp.Title);
            if (!string.IsNullOrWhiteSpace(standaloneDescription)) quiz["description"] = standaloneDescription;
        }
        else
        {
            quiz["title"] = string.IsNullOrWhiteSpace(comp.Title) ? "Practice Quiz" : comp.Title;
        }
        return quiz;
    }

    private static string? ResolveSpecText(BlueprintSpecInstance inst, IReadOnlyDictionary<string, string> loadedMarkdownFiles)
    {
        if (inst.Content is null || inst.Content.Mode == BlueprintContentMode.None)
            return null;

        string? raw = inst.Content.Mode switch
        {
            BlueprintContentMode.RawText => inst.Content.RawText,
            BlueprintContentMode.MarkdownFile when !string.IsNullOrWhiteSpace(inst.Content.MarkdownFilePath) =>
                loadedMarkdownFiles.TryGetValue(inst.Content.MarkdownFilePath.Replace('\\', '/'), out var md) ? md : null,
            _ => null
        };

        return string.IsNullOrWhiteSpace(raw) ? null : raw.TrimStart('\uFEFF').Trim();
    }

    private static IReadOnlyList<BlueprintComponent> ResolveActiveComponents(NormalizedBlueprint blueprint)
    {
        var comps = blueprint.Components ?? [];
        return blueprint.Container switch
        {
            BlueprintContainerKind.StandaloneArticle => comps.Where(c => c.Kind == BlueprintComponentKind.Article).Take(1).ToList(),
            BlueprintContainerKind.StandaloneQuiz => comps.Where(c => c.Kind == BlueprintComponentKind.Quiz).Take(1).ToList(),
            _ => comps.ToList()
        };
    }

    private static List<BlueprintSpecInstance> BuildInstanceSequence(BlueprintComponent comp, List<string> warnings)
    {
        if (comp.OrderedItems is { Count: > 0 })
        {
            var enabledSelections = comp.Selections.Where(s => s.Enabled).ToList();
            if (enabledSelections.Count == 0)
                return comp.OrderedItems.ToList();

            var desiredByTopic = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var sel in enabledSelections)
                desiredByTopic[sel.Topic] = Math.Clamp(sel.DesiredCount ?? 1, 1, 50);

            var retained = new List<BlueprintSpecInstance>();
            var retainedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var trimmedByTopic = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var inst in comp.OrderedItems)
            {
                if (!desiredByTopic.TryGetValue(inst.Topic, out var targetQty))
                    continue;

                var cur = retainedCounts.GetValueOrDefault(inst.Topic, 0);
                if (cur >= targetQty)
                {
                    trimmedByTopic[inst.Topic] = trimmedByTopic.GetValueOrDefault(inst.Topic, 0) + 1;
                    continue;
                }

                retainedCounts[inst.Topic] = cur + 1;
                retained.Add(inst);
            }

            var appendedByTopic = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var sel in enabledSelections)
            {
                var targetQty = desiredByTopic[sel.Topic];
                var cur = retainedCounts.GetValueOrDefault(sel.Topic, 0);
                if (cur >= targetQty) continue;

                var missing = targetQty - cur;
                appendedByTopic[sel.Topic] = missing;

                var remainingQuotas = sel.ModeCounts is { Count: > 0 }
                    ? new Dictionary<string, int>(sel.ModeCounts, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var existing in retained.Where(r => string.Equals(r.Topic, sel.Topic, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!string.IsNullOrWhiteSpace(existing.Subtype)
                        && remainingQuotas.TryGetValue(existing.Subtype, out var quota)
                        && quota > 0)
                    {
                        remainingQuotas[existing.Subtype] = quota - 1;
                    }
                }

                int added = 0;
                foreach (var kv in remainingQuotas)
                {
                    if (kv.Value <= 0 || string.IsNullOrWhiteSpace(kv.Key)) continue;
                    for (int c = 0; c < kv.Value && added < missing; c++)
                    {
                        added++;
                        retained.Add(new BlueprintSpecInstance($"{sel.Topic}_{cur + added}", sel.Topic, cur + added, kv.Key));
                    }
                }

                while (added < missing)
                {
                    added++;
                    retained.Add(new BlueprintSpecInstance($"{sel.Topic}_{cur + added}", sel.Topic, cur + added));
                }
            }

            var finalSequence = new List<BlueprintSpecInstance>(retained.Count);
            var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in retained)
            {
                int nextNum = counters.GetValueOrDefault(inst.Topic, 0) + 1;
                counters[inst.Topic] = nextNum;
                finalSequence.Add(inst with { InstanceNumber = nextNum });
            }

            if (trimmedByTopic.Count > 0 || appendedByTopic.Count > 0)
            {
                var compLabel = string.IsNullOrWhiteSpace(comp.Title) ? comp.Id : comp.Title.Trim();
                warnings.Add($"Reconciled organizer sequence in '{compLabel}' to match Qty.");
            }

            return finalSequence;
        }

        // Expand directly from Selections
        var expanded = new List<BlueprintSpecInstance>();
        foreach (var sel in comp.Selections.Where(s => s.Enabled))
        {
            int qty = Math.Clamp(sel.DesiredCount ?? 1, 1, 50);
            var subtypes = new List<string?>(qty);
            if (sel.ModeCounts is { Count: > 0 })
            {
                foreach (var kv in sel.ModeCounts)
                    for (int k = 0; k < kv.Value && subtypes.Count < qty; k++)
                        subtypes.Add(kv.Key);
            }
            if (subtypes.Count < qty && sel.Modes is { Count: > 0 })
            {
                int idx = 0;
                while (subtypes.Count < qty)
                {
                    subtypes.Add(sel.Modes[idx % sel.Modes.Count]);
                    idx++;
                }
            }
            while (subtypes.Count < qty)
                subtypes.Add(null);

            for (int n = 1; n <= qty; n++)
                expanded.Add(new BlueprintSpecInstance($"{sel.Topic}_{n}", sel.Topic, n, subtypes[n - 1]));
        }
        return expanded;
    }

    private static bool IsArticleBlockTopic(string topic) => topic.Trim().ToLowerInvariant() switch
    {
        "text" or "image" or "callout" or "table" or "diagram-explore" or "reveal" => true,
        _ => false
    };

    private static string YamlQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Slugify(string raw, string fallback)
    {
        var chars = raw.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("__", StringComparison.Ordinal))
            collapsed = collapsed.Replace("__", "_", StringComparison.Ordinal);
        collapsed = collapsed.Trim('_');
        return string.IsNullOrWhiteSpace(collapsed) ? fallback : (collapsed.Length > 28 ? collapsed[..28] : collapsed);
    }
}
