namespace Meloberry.Application.UseCases.Diagnostics;

internal static class SpecTopicUiMetadata
{
    private sealed record TopicUiEntry(
        bool SupportsImage,
        IReadOnlyList<SchemaModeGroupSpec> ModeGroups,
        string MarkdownHint,
        string MarkdownStarter);

    private static readonly Dictionary<string, TopicUiEntry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = new(
            false,
            [],
            "Standard Markdown + KaTeX ($inline$ or $$block$$).",
            "## Heading\n\nExplain the core concept clearly here."),
        ["image"] = new(
            true,
            [],
            "Pick a workspace image or extract from PDF below. Optional caption & alt text.",
            ""),
        ["callout"] = new(
            false,
            [
                new("tone", "Tone", true,
                [
                    new("pearl", "Pearl", "callout"),
                    new("clinical", "Clinical", "callout-clinical"),
                    new("warning", "Warning", "callout-warning")
                ])
            ],
            "Plain Markdown body (wrapped automatically with your selected tone: pearl / clinical / warning).",
            "**Key takeaway:** Highlight the clinical pearl or pitfall here."),
        ["table"] = new(
            false,
            [
                new("options", "Table", true,
                [
                    new("hideableCols", "Hideable cols"),
                    new("stickyFirstCol", "Sticky 1st col")
                ])
            ],
            "Standard Markdown pipe table. Use hideableCols / stickyFirstCol subtypes for study recall.",
            "| Feature | Entity A | Entity B |\n| --- | --- | --- |\n| Mechanism | Value A | Value B |"),
        ["diagram-explore"] = new(
            true,
            [],
            "Attach an image above, then list clickable landmarks (`- Label: Explanation`).",
            "- Landmark 1: Describe this structure.\n- Landmark 2: Describe its function."),
        ["reveal"] = new(
            false,
            [
                new("options", "Behavior", true,
                [
                    new("showFirst", "Show first")
                ])
            ],
            "Use `> Step Title` lines to separate progressive reveal cards.",
            "> Step 1: Trigger\nInitial physiological stimulus.\n\n> Step 2: Response\nDownstream compensatory mechanism."),
        ["link"] = new(
            true,
            [],
            "Hover card body shown when referenced via `[text](mby://card/card-1)`.",
            "Concise definition or clinical note shown on hover."),
        ["mcq"] = new(
            true,
            [
                new("variant", "Selection", false,
                [
                    new("single", "Single", "mcq"),
                    new("multi", "Multi-correct", "mcq-multi"),
                    new("mixed", "Mixed", "mcq-mixed")
                ])
            ],
            "Write the question stem, then options with `- [x]` (correct) and `- [ ]` (distractor). Optional `> Explanation` or `:::explanation`.",
            "Which mechanism best explains this finding?\n- [x] Correct option\n- [ ] Distractor A\n- [ ] Distractor B\n- [ ] Distractor C\n\n> **Explanation:** Brief rationale."),
        ["flashcard"] = new(
            true,
            [
                new("variant", "Style", false,
                [
                    new("text", "Text"),
                    new("diagram", "Diagram/Masked"),
                    new("mixed", "Mixed")
                ])
            ],
            "Separate sides with `:::front` and `:::back`. Attached images are placed on the front automatically.",
            ":::front\nPrompt or structure to recall\n:::back\nConcise high-yield answer"),
        ["fill-blank"] = new(
            false,
            [
                new("matching", "Matching", true,
                [
                    new("fuzzy", "Fuzzy"),
                    new("caseSensitive", "Case-sensitive")
                ])
            ],
            "Wrap target answers in `{{primary | alias}}`.",
            "Depolarization is driven by rapid influx of {{sodium | Na+}} ions."),
        ["assertion-reason"] = new(
            false,
            [
                new("scheme", "Scheme", false,
                [
                    new("4opt", "4-opt"),
                    new("5opt", "5-opt")
                ]),
                new("options", "Extra", true,
                [
                    new("both-false", "Include both-false")
                ])
            ],
            "Provide `A:` (Assertion) and `R:` (Reason) lines.",
            "A: Assertion statement.\nR: Reason explaining the assertion."),
        ["spot-error"] = new(
            true,
            [
                new("options", "Behavior", true,
                [
                    new("requireFix", "Require fix"),
                    new("allowNoError", "Allow no-error")
                ])
            ],
            "Wrap checkable spans in `[valid span]` and errors in `[~~wrong~~ -> correction]` (at least 2 spans).",
            "The [left ventricle] pumps blood into the [~~pulmonary artery~~ -> aorta]."),
        ["diff"] = new(
            false,
            [
                new("mode", "Mode", false,
                [
                    new("fill", "Fill"),
                    new("spot-wrong", "Spot wrong"),
                    new("rearrange", "Rearrange")
                ]),
                new("options", "Extra", true,
                [
                    new("bank", "Word bank")
                ])
            ],
            "Pipe table using `{{answer}}` (fill mode) or `~~wrong~~ -> right` (spot-wrong mode).",
            "| Property | Type A | Type B |\n| --- | --- | --- |\n| Onset | {{Acute}} | {{Chronic}} |"),
        ["mechanism"] = new(
            false,
            [
                new("features", "Structure", true,
                [
                    new("parallel", "Parallel steps"),
                    new("distractors", "Distractors")
                ])
            ],
            "Numbered pathway steps (`1.`, `2.`). Use duplicate numbers for parallel steps.",
            "1. Initial trigger\n2. Intermediate cascade\n3. Final physiological effect"),
        ["relabel"] = new(
            true,
            [
                new("mode", "Mode", false,
                [
                    new("reveal", "Reveal"),
                    new("bank", "Bank"),
                    new("recall", "Recall")
                ]),
                new("style", "Mask", false,
                [
                    new("solid", "Solid"),
                    new("blur", "Blur", "relabel-blur"),
                    new("pixelate", "Pixelate"),
                    new("whiteout", "Whiteout")
                ])
            ],
            "Attach a diagram above, then provide prompt and label boxes (`rect: [x, y, w, h]` in 0..1 coords).",
            "prompt: Identify the labeled structures.\nboxes:\n  - label: \"Structure A\"\n    rect: [0.15, 0.2, 0.25, 0.1]"),
        ["case"] = new(
            true,
            [],
            "Write the clinical vignette stem, followed by nested `::::mcq` or `::::numeric` sub-questions.",
            "A 55-year-old patient presents with...\n::::mcq\nWhat is the most likely diagnosis?\n- [x] Diagnosis A\n- [ ] Diagnosis B\n::::"),
        ["classify"] = new(
            false,
            [],
            "Group items under `[Bucket Name]` headings.",
            "[Category A]\n- Item 1\n- Item 2\n[Category B]\n- Item 3"),
        ["match"] = new(
            false,
            [],
            "List matching pairs as `- Left :: Right`.",
            "- Receptor A :: Mechanism 1\n- Receptor B :: Mechanism 2"),
        ["numeric"] = new(
            true,
            [],
            "Use `:::numeric{answer=42 tolerance=\"5%\" unit=\"mL\"}` followed by the question prompt.",
            ":::numeric{answer=42 tolerance=\"5%\" unit=\"units\"}\nCalculate the expected value.\n:::")
    };

    public static SchemaTopicDefinition Enrich(SchemaTopicDefinition def, bool includeTemplates)
    {
        if (!Entries.TryGetValue(def.Topic, out var entry))
            return def;

        return def with
        {
            ModeGroups = entry.ModeGroups,
            SupportsImage = entry.SupportsImage,
            MarkdownHint = includeTemplates ? entry.MarkdownHint : null,
            MarkdownStarter = includeTemplates ? entry.MarkdownStarter : null
        };
    }
}
