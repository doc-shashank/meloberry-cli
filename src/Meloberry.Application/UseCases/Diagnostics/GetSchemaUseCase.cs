using System.Text.Json.Serialization;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Layouts;

namespace Meloberry.Application.UseCases.Diagnostics;

public sealed record GetSchemaQuery(
    IReadOnlyList<string>? Topics = null,
    bool IncludeIcons = false,
    bool IncludeNested = false);

public sealed record SchemaFieldSpec(
    string Name,
    string Type,
    bool Required,
    string Description,
    IReadOnlyList<string>? AllowedValues = null,
    int? MaxLength = null,
    string? Pattern = null,
    int? MinValue = null,
    int? MaxValue = null);

public sealed record SchemaTopicDefinition(
    string Topic,
    string CompactSignature,
    string Command,
    string Summary,
    bool DisallowUnmappedProperties,
    IReadOnlyList<SchemaFieldSpec> Fields,
    object Example,
    string Scope = "content",
    string Category = "question",
    string? DisplayName = null);

public sealed record GetSchemaResponse(
    IReadOnlyList<string> RequestedTopics,
    IReadOnlyList<SchemaTopicDefinition> Schemas,
    IReadOnlyList<SchemaLayoutPropertySpec>? LayoutProperties = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Icons = null,
    string SchemaVersion = "2.0",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, IReadOnlyList<SchemaFieldSpec>>? NestedTypes = null);

public sealed record SchemaLayoutPropertySpec(
    string Name,
    string ValueType,
    string DefaultValue,
    int? MinValue,
    int? MaxValue,
    IReadOnlyList<string>? AllowedValues,
    IReadOnlyList<string> ApplicableTargets,
    string Description);

public sealed class GetSchemaHandler(ISpecIconProvider? iconProvider = null)
{
    private static readonly Dictionary<string, string> TopicDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["session"] = "Study Session",
        ["article"] = "Study Article",
        ["quiz"] = "Practice Quiz",
        ["text"] = "Text Block",
        ["image"] = "Image Block",
        ["callout"] = "Callout Box",
        ["table"] = "Study Table",
        ["diagram-explore"] = "Interactive Diagram",
        ["reveal"] = "Step-by-Step Reveal",
        ["mcq"] = "Multiple Choice",
        ["flashcard"] = "Flashcard",
        ["fill-blank"] = "Fill in the Blank",
        ["assertion-reason"] = "Assertion-Reason",
        ["spot-error"] = "Spot the Error",
        ["diff"] = "Differentiation Table",
        ["mechanism"] = "Mechanism Sequence",
        ["relabel"] = "Diagram Relabel",
        ["case"] = "Case Vignette",
        ["classify"] = "Classification",
        ["match"] = "Matching",
        ["numeric"] = "Numeric Response",
        ["markdown"] = "Markdown Format",
        ["layouts"] = "Layout Properties",
        ["patch"] = "Surgical Patch",
        ["batch"] = "Atomic Batch",
        ["pkg"] = "Package Export/Import",
        ["build"] = "WebApp Build",
        ["analyze"] = "Verification & Telemetry"
    };

    public sealed record TopicClassification(string Topic, string Scope, string Category);

    public static readonly IReadOnlyList<TopicClassification> TopicCatalog =
    [
        new("session", "content", "container"),
        new("article", "content", "container"),
        new("quiz", "content", "container"),
        new("text", "content", "block"),
        new("image", "content", "block"),
        new("callout", "content", "block"),
        new("table", "content", "block"),
        new("diagram-explore", "content", "block"),
        new("reveal", "content", "block"),
        new("mcq", "content", "question"),
        new("flashcard", "content", "question"),
        new("fill-blank", "content", "question"),
        new("assertion-reason", "content", "question"),
        new("spot-error", "content", "question"),
        new("diff", "content", "question"),
        new("mechanism", "content", "question"),
        new("relabel", "content", "question"),
        new("case", "content", "question"),
        new("classify", "content", "question"),
        new("match", "content", "question"),
        new("numeric", "content", "question"),
        new("markdown", "other", "format"),
        new("layouts", "other", "format"),
        new("patch", "other", "workflow"),
        new("batch", "other", "workflow"),
        new("pkg", "other", "workflow"),
        new("build", "other", "workflow"),
        new("analyze", "other", "workflow")
    ];

    public static readonly IReadOnlyList<string> SupportedTopics =
        ["session", "article", "quiz", "text", "image", "mcq", "flashcard", "layouts", "callout", "markdown", "analyze", "pkg", "build", "batch", "patch", "table", "fill-blank", "assertion-reason", "spot-error", "diff", "mechanism", "relabel", "diagram-explore", "reveal", "case", "classify", "match", "numeric"];

    public static readonly IReadOnlyList<string> SupportedGroups =
        ["@content", "@containers", "@blocks", "@questions", "@other", "@formats", "@workflows"];

    private static readonly SchemaFieldSpec RefField =
        new("ref", "string", false, "Optional slug.", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern);

    private static readonly SchemaFieldSpec ExplanationField =
        new("explanation", "string", false, "Optional explanation Markdown/KaTeX.");

    private static readonly SchemaFieldSpec McqLayoutOverridesField =
        new("layoutOverrides", "object<string,string>", false, "Bounded mcq layout overrides.");

    private static SchemaFieldSpec TypeField(string typeName) =>
        new("type", "string", true, $"Must be '{typeName}'.", [typeName]);

    private static SchemaFieldSpec KindField(string kindName) =>
        new("kind", "string", true, $"Must be '{kindName}'.", [kindName]);

    private static readonly Dictionary<string, Func<SchemaTopicDefinition>> TopicSchemaBuilders = new(StringComparer.Ordinal)
    {
        ["session"] = BuildSessionSchema,
        ["article"] = BuildArticleSchema,
        ["quiz"] = BuildQuizSchema,
        ["text"] = BuildTextBlockSchema,
        ["image"] = BuildImageBlockSchema,
        ["mcq"] = BuildMcqSchema,
        ["flashcard"] = BuildFlashcardSchema,
        ["callout"] = BuildCalloutSchema,
        ["table"] = BuildTableBlockSchema,
        ["diagram-explore"] = BuildDiagramExploreSchema,
        ["reveal"] = BuildRevealSchema,
        ["fill-blank"] = BuildFillBlankSchema,
        ["assertion-reason"] = BuildAssertionReasonSchema,
        ["spot-error"] = BuildSpotErrorSchema,
        ["diff"] = BuildDiffSchema,
        ["mechanism"] = BuildMechanismSchema,
        ["relabel"] = BuildRelabelSchema,
        ["case"] = BuildCaseSchema,
        ["classify"] = BuildClassifySchema,
        ["match"] = BuildMatchSchema,
        ["numeric"] = BuildNumericSchema,
        ["markdown"] = BuildMarkdownSchema,
        ["analyze"] = BuildAnalyzeSchema,
        ["pkg"] = BuildPkgSchema,
        ["build"] = BuildBuildSchema,
        ["batch"] = BuildBatchSchema,
        ["patch"] = BuildPatchSchema
    };

    public Task<Result<GetSchemaResponse>> HandleAsync(GetSchemaQuery query, CancellationToken cancellationToken = default)
    {
        var rawTokens = (query.Topics ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .ToList();

        var requested = new List<string>();
        foreach (var token in rawTokens)
        {
            if (token.StartsWith('@'))
            {
                var expanded = ExpandGroupToken(token);
                if (expanded is null)
                {
                    return Task.FromResult(Result<GetSchemaResponse>.Failure(new Error(
                        "Schema.TopicNotFound",
                        $"Unknown spec group '{token}'. Supported groups: {string.Join(", ", SupportedGroups)}. Supported topics: {string.Join(", ", SupportedTopics)}.",
                        ErrorType.NotFound)));
                }
                foreach (var topic in expanded)
                {
                    if (!requested.Contains(topic, StringComparer.Ordinal))
                        requested.Add(topic);
                }
                continue;
            }

            if (!SupportedTopics.Contains(token, StringComparer.Ordinal))
            {
                return Task.FromResult(Result<GetSchemaResponse>.Failure(new Error(
                    "Schema.TopicNotFound",
                    $"Unknown spec topic '{token}'. Supported topics: {string.Join(", ", SupportedTopics)}. Supported groups: {string.Join(", ", SupportedGroups)}.",
                    ErrorType.NotFound)));
            }

            if (!requested.Contains(token, StringComparer.Ordinal))
                requested.Add(token);
        }

        var activeTopics = requested.Count == 0 ? SupportedTopics.ToList() : requested;
        var schemas = new List<SchemaTopicDefinition>();

        foreach (var t in activeTopics)
        {
            if (TopicSchemaBuilders.TryGetValue(t, out var builder))
            {
                var def = builder();
                schemas.Add(def with { DisplayName = def.DisplayName ?? TopicDisplayNames.GetValueOrDefault(t, t) });
            }
        }

        var layoutProps = activeTopics.Contains("layouts", StringComparer.Ordinal) ? BuildLayoutSpecs() : null;
        IReadOnlyDictionary<string, string>? icons = query.IncludeIcons && iconProvider is not null
            ? iconProvider.GetIcons(requested.Count == 0 ? null : activeTopics)
            : null;
        IReadOnlyDictionary<string, IReadOnlyList<SchemaFieldSpec>>? nestedTypes = query.IncludeNested
            ? BuildNestedTypes(activeTopics, includeAll: requested.Count == 0)
            : null;
        return Task.FromResult(Result<GetSchemaResponse>.Success(
            new GetSchemaResponse(activeTopics, schemas, layoutProps, icons, SchemaVersion: "2.0", NestedTypes: nestedTypes)));
    }

    private static IReadOnlyList<string>? ExpandGroupToken(string groupToken)
    {
        return groupToken switch
        {
            "@content" => TopicCatalog.Where(t => t.Scope == "content").Select(t => t.Topic).ToList(),
            "@containers" or "@container" => TopicCatalog.Where(t => t.Category == "container").Select(t => t.Topic).ToList(),
            "@blocks" or "@block" => TopicCatalog.Where(t => t.Category == "block").Select(t => t.Topic).ToList(),
            "@questions" or "@question" => TopicCatalog.Where(t => t.Category == "question").Select(t => t.Topic).ToList(),
            "@other" => TopicCatalog.Where(t => t.Scope == "other").Select(t => t.Topic).ToList(),
            "@formats" or "@format" => TopicCatalog.Where(t => t.Category == "format").Select(t => t.Topic).ToList(),
            "@workflows" or "@workflow" => TopicCatalog.Where(t => t.Category == "workflow").Select(t => t.Topic).ToList(),
            _ => null
        };
    }

    private static SchemaTopicDefinition BuildSessionSchema() =>
        new(
            Topic: "session",
            CompactSignature: """
                // Session root or append
                { kind:"session", name?:string, description?:string, components:[Article|Quiz] }
                // Ex: {"kind":"session","name":"Kinematics","components":[{"kind":"article","blocks":[{"kind":"text","text":"# Motion"}]}]}
                """,
            Command: "mby apply [<sessionID>] <spec.json|-> [-o dir]",
            Summary: "Study Session spec (creates Session_* or appends components).",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("kind", "string", false, "Must be 'session' when creating a session.", ["session"]),
                new("name", "string", false, "Session title."),
                new("description", "string", false, "Session description."),
                new("components", "array<component>", false, "Ordered article or quiz components.")
            ],
            Example: new
            {
                kind = "session",
                name = "Physics: Kinematics",
                description = "Motion and acceleration",
                components = new object[]
                {
                    new { kind = "article", title = "Equations", blocks = new object[] { new { kind = "text", text = "# Kinematics\n\n$$v = u + at$$" } } },
                    new { kind = "quiz", title = "Check", quizType = "mixed", questions = new object[] { new { type = "mcq", text = "SI unit of force?", options = new object[] { new { text = "N", isCorrect = true }, new { text = "J", isCorrect = false } } } } }
                }
            },
            Scope: "content",
            Category: "container");

    private static SchemaTopicDefinition BuildArticleSchema() =>
        new(
            Topic: "article",
            CompactSignature: """
                // Article component (ref auto-slugified from title if omitted; max 32, ^[a-z0-9][a-z0-9_-]{0,31}$)
                { kind:"article", ref?:string, title?:string, layoutOverrides?:Record<string,string>, blocks:[TextBlock|ImageBlock|CalloutBlock|TableBlock|DiagramExploreBlock|RevealBlock], links?:[{id:string,title?:string,text?:string,imagePath?:string}] }
                // Ex: {"kind":"article","ref":"core-laws","title":"Basics","blocks":[{"kind":"text","text":"## Law\n$$F=ma$$"}]}
                """,
            Command: "mby apply <sessionID|componentID> <spec.json|md|->",
            Summary: "Article component with ordered text/image/callout/table/diagram-explore/reveal blocks and optional hover link cards.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("kind", "string", true, "Use 'article'.", ["article"]),
                new("ref", "string", false, "Optional workspace-unique slug (auto-generated from title if omitted).", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern),
                new("title", "string", false, "Section title."),
                new("layoutOverrides", "object<string,string>", false, "Bounded article layout overrides."),
                new("blocks", "array<block>", true, "Ordered text, image, callout, table, diagram-explore, or reveal blocks."),
                new("links", "array<linkCard>", false, "Optional hover link cards referenced via [text](mby://card/<id>).")
            ],
            Example: new
            {
                kind = "article",
                @ref = "core-laws",
                title = "Core Formulas",
                blocks = new object[]
                {
                    new { kind = "text", text = "## Newton's Second Law\n\n$$F = ma$$" },
                    new { kind = "image", imagePath = "images/force.png", caption = "Fig. 1: Force diagram", alt = "Force vector diagram" },
                    new { kind = "callout", tone = "pearl", title = "Key Rule", text = "SI unit of force is Newton (N)." }
                }
            },
            Scope: "content",
            Category: "container");

    private static SchemaTopicDefinition BuildQuizSchema() =>
        new(
            Topic: "quiz",
            CompactSignature: """
                // Standalone Quiz (Set_*) or Embedded Quiz Component (ref max 32, auto if omitted on component)
                { kind:"quiz", ref?:string /* max 32, ^[a-z0-9][a-z0-9_-]{0,31}$ */, quizType?:"mcq"|"flashcard"|"mixed", name?:string, description?:string, title?:string, layoutOverrides?:Record<string,string>, questions:[Mcq|Flashcard|FillBlank|AssertionReason|SpotError|Differentiation|Mechanism|RelabelImage|CaseGroup|Classify|Match|Numeric] }
                // Ex: {"kind":"quiz","quizType":"mixed","name":"Alg Quiz","questions":[{"type":"flashcard","front":{"text":"O(log n)?"},"back":{"text":"Binary search"}}]}
                """,
            Command: "mby apply [<setID|componentID>] <spec.json|md|-> [-o dir]",
            Summary: "Standalone Quiz or embedded Quiz Component.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("kind", "string", false, "Use 'quiz' (or 'mcq'|'flashcard'|'mixed').", ["quiz", "mcq", "flashcard", "mixed"]),
                new("ref", "string", false, "Optional slug when embedded component (auto-generated if omitted).", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern),
                new("quizType", "string", false, "Question mode.", ["mcq", "flashcard", "mixed"]),
                new("name", "string", false, "Title for standalone Set_*."),
                new("description", "string", false, "Description for standalone Set_*."),
                new("title", "string", false, "Section title when embedded Component_*."),
                new("layoutOverrides", "object<string,string>", false, "Bounded layout overrides (component only)."),
                new("questions", "array<question>", true, "Ordered mcq, flashcard, fill-blank, assertion-reason, spot-error, diff, mechanism, relabel, case, classify, match, or numeric objects.")
            ],
            Example: new
            {
                kind = "quiz",
                quizType = "mixed",
                name = "Algorithms Quiz",
                questions = new object[]
                {
                    new { type = "flashcard", front = new { text = "Binary search complexity?" }, back = new { text = "$O(\\log n)$" } }
                }
            },
            Scope: "content",
            Category: "container");

    private static SchemaTopicDefinition BuildTextBlockSchema() =>
        new(
            Topic: "text",
            CompactSignature: """
                // Article block: text
                { kind:"text", ref?:string /* max 32, ^[a-z0-9][a-z0-9_-]{0,31}$ */, text:string }
                // Ex: {"kind":"text","ref":"summary","text":"# Summary\nKey points."}
                """,
            Command: "Inside article.blocks[]",
            Summary: "Markdown + KaTeX vertical text block.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                KindField("text"),
                RefField,
                new("text", "string", true, "Non-empty Markdown/KaTeX text.")
            ],
            Example: new { kind = "text", text = "# Heading\n\nBody text with $E = mc^2$." },
            Scope: "content",
            Category: "block");

    private static SchemaTopicDefinition BuildImageBlockSchema() =>
        new(
            Topic: "image",
            CompactSignature: """
                // Article block: image
                { kind:"image", ref?:string /* max 32, ^[a-z0-9][a-z0-9_-]{0,31}$ */, imagePath:string, caption?:string, alt?:string }
                // Ex: {"kind":"image","imagePath":"assets/fig_2.1.png","caption":"Fig. 2.1 Cellular response","alt":"Diagram of necrosis vs apoptosis"}
                """,
            Command: "Inside article.blocks[]",
            Summary: "Validated image block with optional caption (<figcaption>) and alt text.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                KindField("image"),
                RefField,
                new("imagePath", "string", true, "Relative path inside JSON/MD/workspace root (copied & stored as assets/<stem>_<hash8>.<ext>)."),
                new("caption", "string", false, "Optional caption rendered atomically inside <figcaption> (supports Markdown/KaTeX)."),
                new("alt", "string", false, "Optional accessibility alt text.")
            ],
            Example: new { kind = "image", imagePath = "assets/fig_2.1.png", caption = "Fig. 2.1 Overview", alt = "Overview diagram" },
            Scope: "content",
            Category: "block");

    private static SchemaTopicDefinition BuildMcqSchema() =>
        new(
            Topic: "mcq",
            CompactSignature: """
                // Question: mcq (ref max 32 ^[a-z0-9][a-z0-9_-]*$; options: 2..26, >=1 isCorrect)
                { type:"mcq", ref?:string, text:string, imagePath?:string, explanation?:string, isMultiCorrect?:bool, maxOptions?:2..26, options:[{index?:int,text?:string,imagePath?:string,isCorrect?:bool}], layoutOverrides?:Record<string,string> }
                // Ex: {"type":"mcq","text":"Prime?","options":[{"text":"2","isCorrect":true},{"text":"4","isCorrect":false}]}
                """,
            Command: "Inside quiz.questions[]",
            Summary: "MCQ question with optional image & explanation.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("mcq"),
                RefField,
                new("text", "string", true, "Prompt Markdown."),
                new("imagePath", "string", false, "Optional prompt image."),
                new("explanation", "string", false, "Optional explanation Markdown."),
                new("isMultiCorrect", "boolean", false, "Multiple correct answers (default false)."),
                new("maxOptions", "integer", false, "Option slot cap (2..26, default 4; auto-expands up to 26 when omitted and >4 options provided; Markdown auto-sizes).", null, null, null, 2, 26),
                new("options", "array<mcqOption>", true, "2..26 options with text/imagePath + isCorrect (enforced by check/build/pkg; >=2 enforced on patch)."),
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "mcq",
                text = "Which are prime?",
                isMultiCorrect = true,
                explanation = "2 and 3 are prime.",
                options = new object[]
                {
                    new { text = "2", isCorrect = true },
                    new { text = "3", isCorrect = true },
                    new { text = "4", isCorrect = false }
                }
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildFlashcardSchema() =>
        new(
            Topic: "flashcard",
            CompactSignature: """
                // Question: flashcard (ref max 32, ^[a-z0-9][a-z0-9_-]{0,31}$)
                { type:"flashcard", ref?:string, front:{text?:string,imagePath?:string,position?:"belowImage"|"aboveImage"}, back:{text?:string,imagePath?:string,position?:"belowImage"|"aboveImage"}, layoutOverrides?:Record<string,string> }
                // Ex: {"type":"flashcard","front":{"text":"H2O"},"back":{"text":"Water"}}
                """,
            Command: "Inside quiz.questions[]",
            Summary: "Two-sided flashcard with text and/or image per face.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("flashcard"),
                RefField,
                new("front", "flashcardSide", true, "Front face: text and/or imagePath + position."),
                new("back", "flashcardSide", true, "Back face: text and/or imagePath + position."),
                new("layoutOverrides", "object<string,string>", false, "Bounded flashcard layout overrides.")
            ],
            Example: new
            {
                type = "flashcard",
                front = new { text = "Binary search worst case?", position = "belowImage" },
                back = new { text = "$O(\\log n)$" }
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildCalloutSchema() =>
        new(
            Topic: "callout",
            CompactSignature: """
                // Article block: callout
                { kind:"callout", tone:"pearl"|"clinical"|"warning", title?:string, text:string, ref?:string /* max 32, ^[a-z0-9][a-z0-9_-]{0,31}$ */ }
                // Ex: {"kind":"callout","tone":"clinical","title":"Clinical Correlation","text":"ATP depletion swells mitochondria."}
                """,
            Command: "Inside article.blocks[] or :::callout{tone=\"clinical\" title=\"...\"} in Markdown",
            Summary: "High-yield textbook callout box ('pearl' | 'clinical' | 'warning').",
            DisallowUnmappedProperties: true,
            Fields:
            [
                KindField("callout"),
                new("tone", "string", false, "Callout style tone (defaults to 'pearl' when omitted).", ["pearl", "clinical", "warning"]),
                new("title", "string", false, "Optional callout header title."),
                new("text", "string", true, "Non-empty Markdown/KaTeX body."),
                RefField
            ],
            Example: new { kind = "callout", tone = "clinical", title = "Clinical Correlation", text = "Elevated serum troponin indicates cardiac myocyte necrosis." },
            Scope: "content",
            Category: "block");

    private static SchemaTopicDefinition BuildMarkdownSchema() =>
        new(
            Topic: "markdown",
            CompactSignature: """
                // Markdown article, quiz, or modular directory input (replaces verbose JSON escaping)
                // Article frontmatter: ---\nkind: article\ntitle: "Cell Injury"\nref: cell-injury\n---
                //   Standalone image: ![alt](assets/fig_2.1.png "Caption") -> ImageBlock (stored as assets/<stem>_<hash8>.<ext>)
                //   Callout fence:    :::callout{ref?="slug" tone="clinical" title="Clinical Note"}\n...\n:::  (or :::pearl / :::clinical / :::warning with optional ref/title)
                //   Table fence:      :::table{title="..." stickyFirstCol=true hideableCols="2,3" /* 1-based */}\n| A | B |\n| --- | --- |\n| 1 | 2 |\n:::footnote\n...\n:::
                //   Native links:     :::link{id="slug" title?="..." image?="assets/fig.png"}\nCard Markdown\n:::  with [text](mby://card/slug), [art](mby://article/ref), or [ext](mby://session/Session_Id/ref)
                // Quiz frontmatter: ---\nkind: quiz\nquizType: mixed\ntitle: "Chapter Review"\n---
                //   MCQ fence:              :::mcq{ref?="slug" multi?=false maxOptions?=auto image?="path"}\nPrompt\n- [x] Right\n- [ ] Wrong\n:::explanation\n...\n:::
                //   Flashcard fence:        :::flashcard{ref?="slug" frontPosition?="belowImage"}\n:::front\n...\n:::back\n...\n:::
                //   Fill-blank fence:       :::fill-blank{fuzzy?=true caseSensitive?=false placeholder?="..."}\nText with {{primary | alias}}.\n:::explanation\n...\n:::
                //   Assertion-Reason fence: :::assertion-reason{answer="both-explain" scheme?="4opt"|"5opt" /* def: 5opt for both-false, else 4opt */}\nA: ...\nR: ...\n:::explanation\n...\n:::
                //   Spot-error fence:       :::spot-error{image?="assets/fig.png" requireFix?=false allowNoError?=false noError?=false}\nIn [span], x is [~~wrong~~ -> right].\n:::explanation\n...\n:::
                //   Diff fence:             :::diff{mode="fill"|"spot-wrong"|"rearrange" title?="..." bank?=false fuzzy?=true allowNoError?=false}\n| Feature | A | B |\n|---|---|---|\n| Row | {{ans | alias}} | ~~bad~~ -> fix \| alt |\n:::explanation\n...\n:::
                //   Mechanism fence:        :::mechanism{title?="..."}\n1. Step one\n2a. Parallel A\n2b. Parallel B\n! Decoy step\n:::explanation\n...\n:::
                //   Relabel fence:          :::relabel{image="assets/fig.png" masks?="assets/fig.masks.json" mode?="reveal"|"bank"|"recall" style?="solid"|"blur"|"pixelate"|"whiteout"}\nprompt: ...\nboxes:\n  - label: "A"\n    rect: [0.1, 0.2, 0.2, 0.1]\n:::explanation\n...\n:::
                //   Diagram-Explore fence:  :::diagram-explore{image="assets/fig.png" masks?="assets/fig.masks.json" title?="..." caption?="..."}\n- LabelA: Annotation Markdown\n:::  (or inline YAML boxes: [{label,rect,note}])
                //   Reveal fence:           :::reveal{title?="..." showFirst?=false}\n> Step 1 Heading\nStep 1 body\n:::
                //   Classify fence:         :::classify{title?="..."}\n[Bucket A]\n- Item 1\n[Bucket B]\n- Item 2\n:::explanation\n...\n:::
                //   Match fence:            :::match{title?="..."}\n- Term :: Match\n! Distractor\n:::explanation\n...\n:::
                //   Numeric fence:          :::numeric{answer=120 tolerance?="5%" unit?="mmHg" placeholder?="..." image?="..."}\nQuestion prompt\n:::explanation\n...\n:::
                //   Case fence:             :::case{title?="..." caption?="..."}\nShared vignette stem + optional ![img](assets/fig.png)\n::::mcq{explanation?="..."}\n...\n::::\n::::numeric{answer=5}\n...\n::::\n:::
                // Directory mode: mby apply ./articles/  or  mby build ./articles/ -o ./web (reads frontmatter-only _session.md or session.json + sorted *.md/*.json)
                """,
            Command: "mby apply <file.md|dir|->  |  mby build <dir|file.md> -o <webDir>",
            Summary: "Author articles and quizzes in clean Markdown + frontmatter or modular directories.",
            DisallowUnmappedProperties: false,
            Fields:
            [
                new("title", "string", false, "YAML frontmatter title (falls back to first # Heading or filename stem)."),
                new("ref", "string", false, "YAML frontmatter ref (auto-slugified from filename/title if omitted).", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern),
                new("kind", "string", false, "'article' (default for prose .md), 'quiz' ('mcq'|'flashcard'|'mixed'), or 'session' (in _session.md).", ["article", "quiz", "mcq", "flashcard", "mixed", "session"]),
                new("quizType", "string", false, "Quiz mode when kind is 'quiz' (defaults to 'mixed').", ["mcq", "flashcard", "mixed"]),
                new("name", "string", false, "Session or standalone quiz name in frontmatter."),
                new("order", "array<string>", false, "Optional filename order override in _session.md.")
            ],
            Example: new
            {
                article = "---\ntitle: Cellular Responses\n---\n\n![Necrosis](assets/fig_2.1.png \"Fig. 2.1\")\n\n:::table{title=\"Necrosis vs Apoptosis\" stickyFirstCol=true hideableCols=\"2,3\"}\n| Feature | Necrosis | Apoptosis |\n| --- | --- | --- |\n| Energy | Passive | ATP-dependent |\n:::",
                quiz = "---\nkind: quiz\nquizType: mixed\ntitle: Quick Check\n---\n\n:::fill-blank{fuzzy=true}\nRate-limiting enzyme of glycolysis: {{phosphofructokinase-1 | PFK-1}}.\n:::\n\n:::assertion-reason{answer=\"both-no-explain\"}\nA: Aspirin is avoided in pediatric viral illness.\nR: Aspirin acetylates COX-1 irreversibly.\n:::\n\n:::spot-error{allowNoError=true}\nIn [hyperparathyroidism], calcium is [high] and phosphate is [~~high~~ -> low].\n:::"
            },
            Scope: "other",
            Category: "format");

    private static SchemaTopicDefinition BuildAnalyzeSchema() =>
        new(
            Topic: "analyze",
            CompactSignature: """
                // Zero-touch browser verification OR live telemetry analysis
                // Headless check: mby analyze <webDir> --headless [--static-only] [--timeout 30] [--json]
                // Telemetry check: mby analyze <webDir> [--all] [--live] [--no-wait] [--json]
                """,
            Command: "mby analyze <webDir|report.json> [--headless] [--static-only] [--timeout 30] [--all] [--live] [--no-wait]",
            Summary: "Verifies generated WebApp DOM/assets/console/overflow headlessly or reads runtime *.analysis.json telemetry.",
            DisallowUnmappedProperties: false,
            Fields:
            [
                new("--headless", "boolean", false, "Zero-touch headless Edge/Chrome CDP check (slides, images, overflow, KaTeX, console errors -> reports/verify.latest.json)."),
                new("--static-only", "boolean", false, "Validate HTML/JS/CSS/DATA/asset existence without launching a browser."),
                new("--timeout", "integer", false, "Headless browser timeout in seconds (default 30).", null, null, null, 5, 180),
                new("--live", "boolean", false, "Launch interactive browser preview + /api/analysis endpoint to record *.analysis.json."),
                new("--all", "boolean", false, "Aggregate all saved *.analysis.json reports in <webDir>/reports.")
            ],
            Example: new { command = "mby analyze ./study_web --headless --json" },
            Scope: "other",
            Category: "workflow");

    private static SchemaTopicDefinition BuildPkgSchema() =>
        new(
            Topic: "pkg",
            CompactSignature: """
                // Export/Import portable .mbypkg archive (2.0 schema; imports v1.0 & v2.0; desktop or android target)
                // Export: mby pkg export <wsId> <desktop|android> [outPath.mbypkg] [--include-layout]
                // Import: mby pkg import <file.mbypkg> [destDir]
                """,
            Command: "mby pkg export <wsId> <desktop|android> [outPath] [--include-layout] | mby pkg import <file.mbypkg> [destDir]",
            Summary: "Mandatory lint runs before export; default export writes <wsId>.mbypkg into the workspace's project root (for .meloberry/workspaces) or parent directory.",
            DisallowUnmappedProperties: false,
            Fields:
            [
                new("targetPlatform", "string", true, "Target package consumer.", ["desktop", "android"]),
                new("--include-layout", "boolean", false, "Bundle non-default global/entity layout overrides."),
                new("outputPath", "string", false, "Optional .mbypkg file path, existing directory, or directory path ending with '/' or '\\' (non-existent paths without a trailing separator are treated as a file stem and receive '.mbypkg'; omitted defaults to <projectRoot>/<wsId>.mbypkg for .meloberry/workspaces, else <workspaceParent>/<wsId>.mbypkg)."),
                new("destDir", "string", false, "Optional import destination directory (defaults to CWD).")
            ],
            Example: new { exportCmd = "mby pkg export Session_123 android ./chapter2.mbypkg --json" },
            Scope: "other",
            Category: "workflow");

    private static SchemaTopicDefinition BuildBuildSchema() =>
        new(
            Topic: "build",
            CompactSignature: """
                // Compile Study WebApp from Workspace ID, JSON, Markdown file, or Directory
                // mby build <addr|spec.json|article.md|dir|-> [-o webDir] [--verify] [--static-only] [--ephemeral] [--timeout 30] [--preview] [--no-wait]
                """,
            Command: "mby build <addr|spec|dir|-> [-o webDir] [--verify] [--static-only] [--ephemeral] [--timeout 30] [--preview] [--no-wait] [--quiet|--verbose]",
            Summary: "Lints workspace, compiles standalone offline HTML/CSS/JS (+ search index), optionally runs --verify CDP or --static-only check.",
            DisallowUnmappedProperties: false,
            Fields:
            [
                new("target", "string", true, "Existing Session_*/Set_* ID (or workspace DB folder), spec file, Markdown directory, or '-'."),
                new("-o", "string", false, "Output web directory (defaults to <projectRoot>/<id>_web or <workspaceParent>/<id>_web for existing workspaces; ./mby_web for one-shot builds)."),
                new("--verify", "boolean", false, "Run headless browser verification immediately after building (exits 2 on failure)."),
                new("--static-only", "boolean", false, "Run static HTML/JS/CSS/DATA/asset verification without launching a browser."),
                new("--ephemeral", "boolean", false, "For one-shot builds, compile inside a temporary workspace and delete it afterwards."),
                new("--timeout", "integer", false, "Headless browser verification timeout in seconds (default 30).", null, null, null, 5, 180),
                new("--preview", "boolean", false, "Open interactive local preview server."),
                new("--no-wait", "boolean", false, "Exit immediately after printing preview URL (with --preview).")
            ],
            Example: new { buildCmd = "mby build ./articles/ -o ./study_web --verify --json" },
            Scope: "other",
            Category: "workflow");

    private static SchemaTopicDefinition BuildBatchSchema() =>
        new(
            Topic: "batch",
            CompactSignature: """
                // Atomic JSON operations array (rolls back all steps on any failure; $0.id / $prev.id substitution in addr)
                // Ops: apply | patch | rm | check | build | pkg | layout
                // [{ op:"apply", spec:{...}, out?:"...", build?:"...", verify?:bool, staticOnly?:bool, timeout?:int },
                //  { op:"patch", addr:"$0.id", data?:{...}, set?:["k=v"], move?:"1:shift" },
                //  { op:"check"|"rm"|"build"|"pkg", addr:"$0.id", ... },
                //  { op:"layout", target:"mcq", property:"optionColumns", value:"2" }]
                """,
            Command: "mby batch <file|->",
            Summary: "Executes a non-empty JSON array of operations atomically in one transaction with $0.id/$prev.id chaining.",
            DisallowUnmappedProperties: false,
            Fields:
            [
                new("op", "string", true, "Operation kind for the step.", ["apply", "patch", "rm", "check", "build", "pkg", "layout"]),
                new("addr", "string", false, "Target address (alias: target); supports $0.id..$N.id and $prev.id placeholders."),
                new("spec", "object", false, "Inline declarative spec for 'apply' (or pass 'file')."),
                new("data", "object", false, "Patch JSON object for 'patch' (alias: patch; or pass 'file', 'set', 'move')."),
                new("set", "array<string>", false, "Array of 'key=value' strings for 'patch'."),
                new("move", "string", false, "Reorder spec '<index>:<shift|exchange>' for 'patch'."),
                new("out", "string", false, "Output directory/path for 'apply', 'build', or 'pkg'."),
                new("platform", "string", false, "Package platform for 'pkg' (default 'desktop').", ["desktop", "android"]),
                new("includeLayout", "boolean", false, "Bundle layout overrides for 'pkg'."),
                new("property", "string", false, "Layout property name for 'layout' (with 'target' and 'value').")
            ],
            Example: new object[]
            {
                new { op = "apply", spec = new { kind = "quiz", quizType = "mcq", name = "Draft" } },
                new { op = "patch", addr = "$0.id", data = new { name = "Final Quiz" } },
                new { op = "check", addr = "$0.id" }
            },
            Scope: "other",
            Category: "workflow");

    private static SchemaTopicDefinition BuildPatchSchema() =>
        new(
            Topic: "patch",
            CompactSignature: """
                // Surgical partial update / reorder by ID, #path, or #ref (strict per-entity keys; --set k=null clears nullable fields/overrides and rejects non-nullable fields; empty {} fails with Patch.Empty)
                // session|quiz: { name?, description? } | component: { title?, ref?, layoutOverrides?, links? }
                // block(text): { text?, ref? } | block(image): { imagePath?, caption?, alt?, ref? } | block(callout): { tone?, title?, text?, ref? }
                // block(table): { title?, caption?, stickyFirstCol?, hideableCols?, headers?, alignments?, rows?, footnote?, ref? }
                // block(diagram-explore): { title?, caption?, alt?, imagePath?, hotspots?, ref? } | block(reveal): { title?, showFirst?, steps?, ref? }
                // mcq: { text?, imagePath?, explanation?, isMultiCorrect?, maxOptions?, options?, ref?, layoutOverrides? }
                // flashcard: { front?:{text?,imagePath?,position?}, back?:{text?,imagePath?,position?}, ref?, layoutOverrides? }
                // fill-blank: { text?, fuzzy?, caseSensitive?, placeholder?, explanation?, ref?, layoutOverrides? }
                // assertion-reason: { assertion?, reason?, answer?, scheme?, explanation?, ref?, layoutOverrides? }
                // spot-error: { text?, imagePath?, requireFix?, allowNoError?, noError?, explanation?, ref?, layoutOverrides? }
                // diff: { mode?, title?, bank?, fuzzy?, caseSensitive?, allowNoError?, placeholder?, headers?, alignments?, rows?, explanation?, ref?, layoutOverrides? }
                // mechanism: { title?, steps?, explanation?, ref?, layoutOverrides? }
                // relabel: { imagePath?, prompt?, mode?, style?, boxes?, distractors?, fuzzy?, caseSensitive?, explanation?, ref?, layoutOverrides? }
                // case: { title?, stem?, imagePath?, caption?, questions?, explanation?, ref?, layoutOverrides? }
                // classify: { title?, buckets?, items?, explanation?, ref?, layoutOverrides? } | match: { title?, pairs?, distractors?, explanation?, ref?, layoutOverrides? }
                // numeric: { text?, prompt?, answer?, tolerance?, toleranceMode?, unit?, placeholder?, imagePath?, explanation?, ref?, layoutOverrides? }
                """,
            Command: "mby patch <addr> [file|-] [--set k=v] [--move idx:shift|exchange]",
            Summary: "Updates allowed fields of an existing entity via JSON object and/or repeatable --set k=v, or reorders with --move.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("addr", "string", true, "Target entity ID, <wsId>#c<n>[.b<m>|.q<m>], <setId>#q<m>, or <wsId>#<ref>."),
                new("--set", "string", false, "Repeatable key=value update; supports dot paths (layoutOverrides.<k>=<v|null>, front.text=...)."),
                new("--move", "string", false, "Reorder component, block, or question to 1-based index: '<idx>:shift' or '<idx>:exchange'."),
                new("layoutOverrides", "object<string,string|null>", false, "Set bounded overrides or clear an override key with null (component/question).")
            ],
            Example: new
            {
                command = "mby patch Session_123#c2.q1 --set text=\"Updated prompt\" --set layoutOverrides.optionColumns=2 --json"
            },
            Scope: "other",
            Category: "workflow");

    private static SchemaTopicDefinition BuildTableBlockSchema() =>
        new(
            Topic: "table",
            CompactSignature: """
                // Article block: table (interactive study table with hideable columns & sticky first column)
                { kind:"table", ref?:string, title?:string, caption?:string, stickyFirstCol?:bool, hideableCols?:int[] /* 1-based (1..headers.length) */, headers:string[], alignments?:("left"|"center"|"right")[] /* len==headers.length */, rows:string[][], footnote?:string }
                // Markdown: :::table{title="..." stickyFirstCol=true hideableCols="2,3"}\n| Col1 | Col2 |\n|---|---|\n| A | B |\n:::footnote\n...\n:::
                """,
            Command: "Inside article.blocks[] or :::table{...} in Markdown",
            Summary: "First-class interactive study table with column masking, sticky first column, caption, and footnote.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                KindField("table"),
                RefField,
                new("title", "string", false, "Optional table header toolbar title."),
                new("caption", "string", false, "Optional Markdown/KaTeX table caption."),
                new("stickyFirstCol", "boolean", false, "Stick first column during horizontal scroll (default false)."),
                new("hideableCols", "array<integer>", false, "1-based column indices that can be blurred/masked for active recall."),
                new("headers", "array<string>", true, "Non-empty column header labels (Markdown/KaTeX supported)."),
                new("alignments", "array<string>", false, "Optional per-column alignment ('left' | 'center' | 'right'); length must match headers.length."),
                new("rows", "array<array<string>>", true, "At least 1 row; each row must match headers.length."),
                new("footnote", "string", false, "Optional Markdown/KaTeX table footnote.")
            ],
            Example: new
            {
                kind = "table",
                title = "Loop vs Thiazide Diuretics",
                stickyFirstCol = true,
                hideableCols = new[] { 2, 3 },
                headers = new[] { "Feature", "Loop", "Thiazide" },
                rows = new[] { new[] { "Serum Ca2+", "Decreased", "Increased" } },
                footnote = "Both can cause hypokalemia."
            },
            Scope: "content",
            Category: "block");

    private static SchemaTopicDefinition BuildFillBlankSchema() =>
        new(
            Topic: "fill-blank",
            CompactSignature: """
                // Question: fill-blank (free recall with {{primary | alias}} slots, fuzzy/Greek matching & self-grade override)
                { type:"fill-blank", ref?:string, text:string, fuzzy?:bool, caseSensitive?:bool, placeholder?:string, explanation?:string, layoutOverrides?:Record<string,string> }
                // Markdown: :::fill-blank{fuzzy=true caseSensitive=false placeholder?="..."}\nEnzyme is {{phosphofructokinase-1 | PFK-1}}.\n:::explanation\n...\n:::
                """,
            Command: "Inside quiz.questions[] or :::fill-blank{...} in Markdown",
            Summary: "Free-recall fill-in-the-blank question with aliases, Greek/fuzzy tolerance, and self-grade override.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("fill-blank"),
                RefField,
                new("text", "string", true, "Prompt containing >=1 '{{primary | alias}}' blank slot."),
                new("fuzzy", "boolean", false, "Allow Levenshtein distance <= 1 for answers >= 6 chars (default true)."),
                new("caseSensitive", "boolean", false, "Require exact letter case match (default false)."),
                new("placeholder", "string", false, "Optional blank input placeholder text (empty by default)."),
                ExplanationField,
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "fill-blank",
                text = "Rate-limiting enzyme of glycolysis is {{phosphofructokinase-1 | PFK-1}}.",
                fuzzy = true,
                caseSensitive = false,
                explanation = "PFK-1 catalyzes the committed step."
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildAssertionReasonSchema() =>
        new(
            Topic: "assertion-reason",
            CompactSignature: """
                // Question: assertion-reason (auto-generates standard 4- or 5-option choices from canonical answer enum)
                { type:"assertion-reason", ref?:string, assertion:string, reason:string, answer:"both-explain"|"both-no-explain"|"a-true-r-false"|"a-false-r-true"|"both-false", scheme?:"4opt"|"5opt" /* def: 5opt for both-false, else 4opt; both-false forbids 4opt */, explanation?:string, layoutOverrides?:Record<string,string> }
                // Markdown: :::assertion-reason{answer="both-no-explain" scheme?="4opt"|"5opt"}\nA: ...\nR: ...\n:::explanation\n...\n:::
                """,
            Command: "Inside quiz.questions[] or :::assertion-reason{...} in Markdown",
            Summary: "Standardized Assertion (A) & Reason (R) question without repetitive option boilerplate.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("assertion-reason"),
                RefField,
                new("assertion", "string", true, "Assertion (A) statement Markdown/KaTeX."),
                new("reason", "string", true, "Reason (R) statement Markdown/KaTeX."),
                new("answer", "string", true, "Canonical answer enum.", ["both-explain", "both-no-explain", "a-true-r-false", "a-false-r-true", "both-false"]),
                new("scheme", "string", false, "Option scheme (defaults to '5opt' when answer is 'both-false', else '4opt'; explicit '4opt' with 'both-false' fails lint).", ["4opt", "5opt"]),
                ExplanationField,
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "assertion-reason",
                assertion = "Aspirin is avoided in children with viral infections.",
                reason = "Aspirin irreversibly acetylates COX-1 and COX-2.",
                answer = "both-no-explain",
                scheme = "4opt",
                explanation = "Avoidance is specifically due to Reye syndrome risk."
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildSpotErrorSchema() =>
        new(
            Topic: "spot-error",
            CompactSignature: """
                // Question: spot-error (tappable [span] chips with [~~wrong~~ -> correction | alias] error spans)
                { type:"spot-error", ref?:string, text:string, imagePath?:string, requireFix?:bool, allowNoError?:bool, noError?:bool, explanation?:string, layoutOverrides?:Record<string,string> }
                // Markdown: :::spot-error{image?="assets/fig.png" requireFix=false allowNoError=true}\nIn [condition], calcium is [high] and phosphate is [~~high~~ -> low].\n:::explanation\n...\n:::
                """,
            Command: "Inside quiz.questions[] or :::spot-error{...} in Markdown",
            Summary: "Identify incorrect segment(s) in a statement, with optional image, correction input, and 'No error' trap.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("spot-error"),
                RefField,
                new("text", "string", true, "Statement with >=2 '[span]' segments; mark faulty spans as '[~~wrong~~ -> fix | alias]'."),
                new("imagePath", "string", false, "Optional prompt diagram image path."),
                new("requireFix", "boolean", false, "Require typing the correction for tapped error span(s) (default false)."),
                new("allowNoError", "boolean", false, "Display a 'No error in statement' button (default false; defaults true when noError=true)."),
                new("noError", "boolean", false, "Statement has zero error spans and 'No error' is the correct answer (default false)."),
                ExplanationField,
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "spot-error",
                text = "In [primary hyperparathyroidism], serum calcium is [elevated] and phosphate is [~~elevated~~ -> decreased | low].",
                requireFix = false,
                allowNoError = true,
                explanation = "PTH decreases proximal tubular phosphate reabsorption."
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildDiffSchema() =>
        new(
            Topic: "diff",
            CompactSignature: """
                // Question: diff (comparative table drill: mode="fill" | "spot-wrong" | "rearrange")
                { type:"diff", ref?:string, mode:"fill"|"spot-wrong"|"rearrange", title?:string, bank?:bool, fuzzy?:bool, caseSensitive?:bool, allowNoError?:bool, placeholder?:string, headers:string[], alignments?:("left"|"center"|"right")[], rows:string[][], explanation?:string, layoutOverrides?:Record<string,string> }
                // Markdown: :::diff{mode="spot-wrong" title="..." placeholder?="..."}\n| Prop | A | B |\n|---|---|---|\n| X | ~~wrong~~ -> fix \| alias | {{fix | alias}} |\n:::
                """,
            Command: "Inside quiz.questions[] or :::diff{...} in Markdown",
            Summary: "Comparative differentiation table supporting fill-blanks (with optional bank), spot-wrong cells, and row-wise rearrange.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("diff"),
                RefField,
                new("mode", "string", true, "Interaction mode.", ["fill", "spot-wrong", "rearrange"]),
                new("title", "string", false, "Optional table prompt/header title (Markdown/KaTeX supported)."),
                new("bank", "boolean", false, "Show clickable shuffled word bank in 'fill' mode (default false)."),
                new("fuzzy", "boolean", false, "Allow Levenshtein distance <= 1 in 'fill' mode (default true)."),
                new("caseSensitive", "boolean", false, "Require exact case match in 'fill' mode (default false)."),
                new("allowNoError", "boolean", false, "Display 'No error in table' option in 'spot-wrong' mode (default false)."),
                new("placeholder", "string", false, "Optional blank input placeholder text in 'fill' mode."),
                new("headers", "array<string>", true, ">=2 column headers (>=3 for 'rearrange': col 0 feature + >=2 entities)."),
                new("alignments", "array<string>", false, "Optional per-column alignment ('left' | 'center' | 'right'); length must match headers.length."),
                new("rows", "array<array<string>>", true, ">=1 row; mark 'fill' blanks with '{{ans | alias}}' and 'spot-wrong' faults with '~~wrong~~ -> fix \\| alias' or '~~wrong~~ -> {{fix | alias}}'."),
                ExplanationField,
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "diff",
                mode = "spot-wrong",
                title = "Gram-Positive vs Gram-Negative",
                headers = new[] { "Property", "Gram-Positive", "Gram-Negative" },
                rows = new[] { new[] { "Outer membrane", "~~Present~~ -> Absent | None", "Present" } },
                explanation = "Gram-positive bacteria lack an outer lipid membrane."
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildMechanismSchema() =>
        new(
            Topic: "mechanism",
            CompactSignature: """
                // Question: mechanism (pathway/cascade ordering with parallel orderGroup steps & optional distractors)
                { type:"mechanism", ref?:string, title?:string, steps:[{orderGroup?:int, text:string, isDistractor?:bool}], explanation?:string, layoutOverrides?:Record<string,string> }
                // Markdown: :::mechanism{title="..."}\n1. Step one\n2a. Parallel A\n2b. Parallel B\n! Decoy step\n:::explanation\n...\n:::
                """,
            Command: "Inside quiz.questions[] or :::mechanism{...} in Markdown",
            Summary: "Pathway and cascade sequencing question supporting parallel/commutative step groups and distractor decoys.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("mechanism"),
                RefField,
                new("title", "string", false, "Optional mechanism prompt/title (Markdown/KaTeX supported)."),
                new("steps", "array<mechanismStep>", true, ">=2 ordered steps (orderGroup >= 1; equal orderGroup = parallel/commutative) plus optional distractors (isDistractor=true)."),
                ExplanationField,
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "mechanism",
                title = "RAAS Activation",
                steps = new object[]
                {
                    new { orderGroup = 1, text = "Renin cleaves angiotensinogen to Angiotensin I" },
                    new { orderGroup = 2, text = "ACE converts Angiotensin I to Angiotensin II" },
                    new { isDistractor = true, text = "ANP converts Angiotensin I to Angiotensin II" }
                }
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildRelabelSchema() =>
        new(
            Topic: "relabel",
            CompactSignature: """
                // Question: relabel (single unmasked image + normalized [0..1] overlay boxes via inline YAML/JSON, .masks.json, or melomg .meta.json)
                { type:"relabel", ref?:string, imagePath:string, prompt?:string, mode?:"reveal"|"bank"|"recall", style?:"solid"|"blur"|"pixelate"|"whiteout", masksPath?:string, include?:string[], boxes?:[{label:string,aliases?:string[],rect?:[x,y,w,h],x?:number,y?:number,w?:number,h?:number}], distractors?:string[], fuzzy?:bool, caseSensitive?:bool, explanation?:string, layoutOverrides?:Record<string,string> }
                // Markdown: :::relabel{image="assets/fig.png" masks?="assets/fig.masks.json" mode="bank" style="blur"}\nprompt: ...\ninclude: ["A","B"]\n:::
                // melomg handoff: extract unmasked PNG with 'melomg extract ... --no-masks' for image=, and pair with a .masks.json or masked-pass '--mask-all --meta' .meta.json sidecar.
                """,
            Command: "Inside quiz.questions[] or :::relabel{...} in Markdown",
            Summary: "Interactive image-occlusion and diagram relabeling with reveal, word-bank, and typed recall modes over one unmasked image.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("relabel"),
                RefField,
                new("imagePath", "string", true, "Relative path to the single unmasked diagram image (Markdown attr: image= or imagePath=)."),
                new("prompt", "string", false, "Optional prompt/instruction Markdown/KaTeX."),
                new("mode", "string", false, "Interaction mode (default 'bank').", ["reveal", "bank", "recall"]),
                new("style", "string", false, "Occlusion box style (default 'solid').", ["solid", "blur", "pixelate", "whiteout"]),
                new("masksPath", "string", false, "Optional sidecar (.masks.json or melomg --mask-all --meta .meta.json; alias: masks). Mutually exclusive with inline 'boxes'."),
                new("include", "array<string>", false, "Optional label filter applied to sidecar boxes (case-insensitive exact then substring)."),
                new("boxes", "array<relabelBox>", false, "Inline normalized [0..1] boxes ({label, aliases?, rect:[x,y,w,h] or x/y/w/h}). Mutually exclusive with masksPath."),
                new("distractors", "array<string>", false, "Optional decoy labels added to the shuffled bank in 'bank' mode."),
                new("fuzzy", "boolean", false, "Allow Levenshtein distance <= 1 in 'recall' mode (default true)."),
                new("caseSensitive", "boolean", false, "Require exact case match in 'recall' mode (default false)."),
                ExplanationField,
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "relabel",
                imagePath = "assets/nephron.png",
                prompt = "Identify the nephron segments.",
                mode = "bank",
                style = "blur",
                distractors = new[] { "Collecting duct" },
                boxes = new object[]
                {
                    new { label = "Proximal convoluted tubule", aliases = new[] { "PCT" }, rect = new[] { 0.12, 0.24, 0.18, 0.06 } },
                    new { label = "Glomerulus", rect = new[] { 0.45, 0.15, 0.14, 0.05 } }
                }
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildDiagramExploreSchema() =>
        new(
            Topic: "diagram-explore",
            CompactSignature: """
                // Article block: diagram-explore (interactive hotspot explorer over one unmasked image + .masks.json / melomg .meta.json or inline boxes)
                { kind:"diagram-explore", ref?:string, imagePath:string, title?:string, caption?:string, alt?:string, masksPath?:string, hotspots:[{label:string,note:string,rect?:[x,y,w,h],x?:number,y?:number,w?:number,h?:number}] }
                // Markdown: :::diagram-explore{image="assets/fig.png" masks?="assets/fig.masks.json" title?="..."}\n- Label: Annotation Markdown\n:::
                """,
            Command: "Inside article.blocks[] or :::diagram-explore{...} in Markdown",
            Summary: "Interactive anatomical/conceptual diagram with clickable normalized hotspots revealing Markdown/KaTeX annotation cards.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                KindField("diagram-explore"),
                RefField,
                new("imagePath", "string", true, "Relative path to the unmasked diagram image."),
                new("title", "string", false, "Optional explorer title."),
                new("caption", "string", false, "Optional caption."),
                new("alt", "string", false, "Optional alt text."),
                new("masksPath", "string", false, "Optional .masks.json or melomg .meta.json sidecar (hotspots specify {label, note} matched case-insensitively)."),
                new("hotspots", "array<diagramHotspot>", true, "Non-empty list of hotspot annotations ({label, note, rect:[x,y,w,h] or x/y/w/h if no masksPath}).")
            ],
            Example: new
            {
                kind = "diagram-explore",
                imagePath = "assets/nephron.png",
                title = "Nephron Explorer",
                hotspots = new[]
                {
                    new { label = "Glomerulus", note = "Filters plasma.", rect = new[] { 0.15, 0.2, 0.2, 0.12 } }
                }
            },
            Scope: "content",
            Category: "block");

    private static SchemaTopicDefinition BuildRevealSchema() =>
        new(
            Topic: "reveal",
            CompactSignature: """
                // Article block: reveal (progressive step-through / Socratic active-recall cards inside an article)
                { kind:"reveal", ref?:string, title?:string, showFirst?:bool, steps:[{heading:string,body:string}] }
                // Markdown: :::reveal{title?="..." showFirst?=false}\n> Step 1 Title\nStep 1 body\n> Step 2 Title\nStep 2 body\n:::
                """,
            Command: "Inside article.blocks[] or :::reveal{...} in Markdown",
            Summary: "Sequential progressive disclosure cards inside articles for active-recall pacing.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                KindField("reveal"),
                RefField,
                new("title", "string", false, "Optional header title."),
                new("showFirst", "boolean", false, "Reveal the first step immediately on load (default false)."),
                new("steps", "array<revealStep>", true, "Non-empty ordered list of {heading, body} steps.")
            ],
            Example: new
            {
                kind = "reveal",
                title = "Derive Ohm's Law",
                showFirst = false,
                steps = new[]
                {
                    new { heading = "Cue 1", body = "Current density is proportional to electric field." }
                }
            },
            Scope: "content",
            Category: "block");

    private static SchemaTopicDefinition BuildClassifySchema() =>
        new(
            Topic: "classify",
            CompactSignature: """
                // Question: classify (sort shuffled chips into 2-4 named category buckets)
                { type:"classify", ref?:string, title?:string, buckets:string[]|[{bucketId:int,name:string}], items:[{bucket:string|bucketId:int,text:string}], explanation?:string, layoutOverrides?:Record<string,string> }
                // Markdown: :::classify{title?="..."}\n[Bucket A]\n- Item 1\n[Bucket B]\n- Item 2\n:::explanation\n...\n:::
                """,
            Command: "Inside quiz.questions[] or :::classify{...} in Markdown",
            Summary: "Interactive multi-bucket classification / grouping drill with click-to-sort and drag-and-drop.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("classify"),
                RefField,
                new("title", "string", false, "Optional prompt/heading."),
                new("buckets", "array<string|bucket>", true, "2 to 4 bucket names or {bucketId, name} objects."),
                new("items", "array<classifyItem>", true, "Items mapped via bucket name or bucketId."),
                ExplanationField,
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "classify",
                title = "Sort Diuretics",
                buckets = new[] { "Loop", "Thiazide" },
                items = new object[]
                {
                    new { bucket = "Loop", text = "Furosemide" },
                    new { bucket = "Thiazide", text = "Hydrochlorothiazide" }
                }
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildMatchSchema() =>
        new(
            Topic: "match",
            CompactSignature: """
                // Question: match (two-column pairing with optional distractor decoys)
                { type:"match", ref?:string, title?:string, pairs:[{left:string,right:string}], distractors?:string[], explanation?:string, layoutOverrides?:Record<string,string> }
                // Markdown: :::match{title?="..."}\n- Left :: Right\n! Distractor\n:::explanation\n...\n:::
                """,
            Command: "Inside quiz.questions[] or :::match{...} in Markdown",
            Summary: "Two-column matching question with optional unmatched distractors.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("match"),
                RefField,
                new("title", "string", false, "Optional prompt/heading."),
                new("pairs", "array<matchPair>", true, "At least 2 {left, right} pairs."),
                new("distractors", "array<string>", false, "Optional right-column decoy choices."),
                ExplanationField,
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "match",
                title = "Match Receptors",
                pairs = new object[]
                {
                    new { left = "Alpha-1", right = "$G_q$" },
                    new { left = "Beta-2", right = "$G_s$" }
                },
                distractors = new[] { "$G_i$" }
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildNumericSchema() =>
        new(
            Topic: "numeric",
            CompactSignature: """
                // Question: numeric (quantitative free-recall with absolute or percentage tolerance)
                { type:"numeric", ref?:string, text?:string, prompt?:string, answer:number, tolerance?:number|string /* e.g. 5 or "5%" */, toleranceMode?:"absolute"|"percent", unit?:string, placeholder?:string, imagePath?:string, explanation?:string, layoutOverrides?:Record<string,string> }
                // Markdown: :::numeric{answer=125 tolerance="5%" unit="mL/min" placeholder?="..." image?="..."}\nPrompt...\n:::explanation\n...\n:::
                """,
            Command: "Inside quiz.questions[] or :::numeric{...} in Markdown",
            Summary: "Quantitative calculation question graded against an absolute or percentage tolerance range.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("numeric"),
                RefField,
                new("text", "string", false, "Question prompt (alias for 'prompt'; provide either 'text' or 'prompt')."),
                new("prompt", "string", false, "Question prompt (alias for 'text'; provide either 'prompt' or 'text')."),
                new("answer", "number", true, "Exact finite numeric answer."),
                new("tolerance", "number|string", false, "Non-negative tolerance (number or 'X%' percentage; default 0)."),
                new("toleranceMode", "string", false, "Tolerance interpretation (default 'absolute').", ["absolute", "percent"]),
                new("unit", "string", false, "Optional displayed unit badge."),
                new("placeholder", "string", false, "Optional input placeholder."),
                new("imagePath", "string", false, "Optional diagram/figure image."),
                ExplanationField,
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "numeric",
                text = "Calculate MAP for BP 120/80 mmHg.",
                answer = 93.3,
                tolerance = "5%",
                unit = "mmHg"
            },
            Scope: "content",
            Category: "question");

    private static SchemaTopicDefinition BuildCaseSchema() =>
        new(
            Topic: "case",
            CompactSignature: """
                // Question: case (shared clinical/case vignette stem + 2+ nested sub-questions; no nested 'case')
                { type:"case", ref?:string, title?:string, stem:string, imagePath?:string, caption?:string, questions:[Mcq|Flashcard|FillBlank|AssertionReason|SpotError|Diff|Mechanism|Relabel|Classify|Match|Numeric], explanation?:string, layoutOverrides?:Record<string,string> }
                // Markdown: :::case{title?="..." caption?="..."}\nStem Markdown + optional ![alt](img.png)\n::::mcq{explanation?="..."}\n...\n::::\n::::numeric{answer=5}\n...\n::::\n:::
                """,
            Command: "Inside quiz.questions[] or :::case{...} in Markdown",
            Summary: "Pinned shared vignette/case stem driving multiple linked interactive sub-questions with rolled-up telemetry.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                TypeField("case"),
                RefField,
                new("title", "string", false, "Optional case heading."),
                new("stem", "string", true, "Shared Markdown/KaTeX vignette stem."),
                new("imagePath", "string", false, "Optional image attached to the shared stem."),
                new("caption", "string", false, "Optional caption for the stem image."),
                new("questions", "array<question>", true, "2 or more non-case sub-questions (inside Markdown use quad-colon fences ::::mcq ... ::::)."),
                new("explanation", "string", false, "Optional overall case takeaway Markdown/KaTeX."),
                McqLayoutOverridesField
            ],
            Example: new
            {
                type = "case",
                title = "Renal Vignette",
                stem = "A patient presents with hypertension and hypokalemia.",
                questions = new object[]
                {
                    new { type = "mcq", text = "Likely hormone elevated?", options = new object[] { new { text = "Aldosterone", isCorrect = true }, new { text = "ADH", isCorrect = false } } },
                    new { type = "numeric", text = "Normal serum K+ lower bound:", answer = 3.5, tolerance = 0.2, unit = "mEq/L" }
                }
            },
            Scope: "content",
            Category: "question");

    private static IReadOnlyList<SchemaLayoutPropertySpec> BuildLayoutSpecs()
    {
        var seen = new Dictionary<string, (LayoutPropertySpec Spec, HashSet<string> Targets)>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in LayoutBoundsCatalog.SupportedTargets)
        {
            foreach (var spec in LayoutBoundsCatalog.GetSpecsForTarget(target))
            {
                if (!seen.TryGetValue(spec.Name, out var entry))
                {
                    entry = (spec, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    seen[spec.Name] = entry;
                }
                entry.Targets.Add(target);
            }
        }

        return seen.Values
            .Select(v => new SchemaLayoutPropertySpec(
                v.Spec.Name,
                v.Spec.ValueType == LayoutPropertyType.Integer ? "integer" : "enum",
                v.Spec.DefaultValue,
                v.Spec.MinValue,
                v.Spec.MaxValue,
                v.Spec.AllowedValues.Count > 0 ? v.Spec.AllowedValues : null,
                v.Targets.ToList(),
                v.Spec.Description))
            .ToList();
    }

    private static readonly Dictionary<string, string[]> TopicNestedTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["article"] = ["linkCard"],
        ["mcq"] = ["mcqOption"],
        ["flashcard"] = ["flashcardSide"],
        ["relabel"] = ["relabelBox"],
        ["diagram-explore"] = ["diagramHotspot"],
        ["reveal"] = ["revealStep"],
        ["mechanism"] = ["mechanismStep"],
        ["classify"] = ["bucket", "classifyItem"],
        ["match"] = ["matchPair"]
    };

    private static readonly Dictionary<string, IReadOnlyList<SchemaFieldSpec>> AllNestedTypes = new(StringComparer.Ordinal)
    {
        ["mcqOption"] =
        [
            new("index", "integer", false, "Optional 1-based option slot index (auto-assigned if omitted).", null, null, null, 1, 26),
            new("text", "string", false, "Option Markdown/KaTeX text (provide text and/or imagePath)."),
            new("imagePath", "string", false, "Optional option image path (provide text and/or imagePath)."),
            new("isCorrect", "boolean", false, "Marks this option as a correct answer (default false).")
        ],
        ["flashcardSide"] =
        [
            new("text", "string", false, "Face Markdown/KaTeX text (provide text and/or imagePath)."),
            new("imagePath", "string", false, "Optional face image path (provide text and/or imagePath)."),
            new("position", "string", false, "Text placement relative to image (alias: textPosition; default 'belowImage').", ["belowImage", "aboveImage"])
        ],
        ["relabelBox"] =
        [
            new("label", "string", true, "Primary canonical label text."),
            new("aliases", "array<string>", false, "Optional additional accepted answers in recall mode."),
            new("rect", "array<number>", false, "Normalized [x, y, w, h] coordinates in 0..1 (alternative to x/y/w/h)."),
            new("x", "number", false, "Normalized left coordinate in [0..1)."),
            new("y", "number", false, "Normalized top coordinate in [0..1)."),
            new("w", "number", false, "Normalized width in (0..1]."),
            new("h", "number", false, "Normalized height in (0..1].")
        ],
        ["diagramHotspot"] =
        [
            new("label", "string", true, "Hotspot label (matched against sidecar when masksPath is set, or displayed directly)."),
            new("note", "string", true, "Markdown/KaTeX annotation revealed when clicked."),
            new("rect", "array<number>", false, "Normalized [x, y, w, h] coordinates in 0..1 (alternative to x/y/w/h when no masksPath)."),
            new("x", "number", false, "Normalized left coordinate in [0..1)."),
            new("y", "number", false, "Normalized top coordinate in [0..1)."),
            new("w", "number", false, "Normalized width in (0..1]."),
            new("h", "number", false, "Normalized height in (0..1].")
        ],
        ["revealStep"] =
        [
            new("heading", "string", true, "Step cue/header text."),
            new("body", "string", true, "Step Markdown/KaTeX body revealed on click.")
        ],
        ["mechanismStep"] =
        [
            new("stepId", "integer", false, "Optional step identifier (auto-assigned if omitted)."),
            new("orderGroup", "integer", false, "1-based sequence group; equal values are parallel/commutative (required when isDistractor is false).", null, null, null, 1, null),
            new("text", "string", true, "Step Markdown/KaTeX text."),
            new("isDistractor", "boolean", false, "Marks a decoy step excluded from the correct sequence (default false).")
        ],
        ["bucket"] =
        [
            new("bucketId", "integer", true, "1-based category bucket identifier (when passing objects instead of string names).", null, null, null, 1, 4),
            new("name", "string", true, "Category bucket display name.")
        ],
        ["classifyItem"] =
        [
            new("bucket", "string", false, "Target bucket name (provide either bucket or bucketId)."),
            new("bucketId", "integer", false, "1-based target bucket identifier (provide either bucket or bucketId).", null, null, null, 1, 4),
            new("text", "string", true, "Sortable item chip Markdown/KaTeX text.")
        ],
        ["matchPair"] =
        [
            new("left", "string", true, "Left-column prompt item."),
            new("right", "string", true, "Right-column matching item.")
        ],
        ["linkCard"] =
        [
            new("id", "string", true, "Unique link card slug referenced via [text](mby://card/<id>)."),
            new("title", "string", false, "Optional hover card header title."),
            new("text", "string", false, "Hover card Markdown/KaTeX body text."),
            new("imagePath", "string", false, "Optional hover card image path.")
        ]
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<SchemaFieldSpec>>? BuildNestedTypes(
        IReadOnlyList<string> activeTopics,
        bool includeAll)
    {
        if (includeAll)
            return new Dictionary<string, IReadOnlyList<SchemaFieldSpec>>(AllNestedTypes, StringComparer.Ordinal);

        var result = new Dictionary<string, IReadOnlyList<SchemaFieldSpec>>(StringComparer.Ordinal);
        foreach (var topic in activeTopics)
        {
            if (!TopicNestedTypeMap.TryGetValue(topic, out var typeNames))
                continue;
            foreach (var typeName in typeNames)
            {
                if (AllNestedTypes.TryGetValue(typeName, out var fields))
                    result[typeName] = fields;
            }
        }

        return result.Count > 0 ? result : null;
    }
}
