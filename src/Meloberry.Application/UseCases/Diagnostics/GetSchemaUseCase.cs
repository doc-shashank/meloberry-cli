using Meloberry.Domain.Common;
using Meloberry.Domain.Layouts;

namespace Meloberry.Application.UseCases.Diagnostics;

public sealed record GetSchemaQuery(IReadOnlyList<string>? Topics = null);

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
    object Example);

public sealed record GetSchemaResponse(
    IReadOnlyList<string> RequestedTopics,
    IReadOnlyList<SchemaTopicDefinition> Schemas,
    IReadOnlyList<SchemaLayoutPropertySpec>? LayoutProperties = null);

public sealed record SchemaLayoutPropertySpec(
    string Name,
    string ValueType,
    string DefaultValue,
    int? MinValue,
    int? MaxValue,
    IReadOnlyList<string>? AllowedValues,
    IReadOnlyList<string> ApplicableTargets,
    string Description);

public sealed class GetSchemaHandler
{
    public static readonly IReadOnlyList<string> SupportedTopics =
        ["session", "article", "quiz", "text", "image", "mcq", "flashcard", "layouts", "callout", "markdown", "analyze", "pkg", "build"];

    public Task<Result<GetSchemaResponse>> HandleAsync(GetSchemaQuery query, CancellationToken cancellationToken = default)
    {
        var requested = (query.Topics ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var topic in requested)
        {
            if (!SupportedTopics.Contains(topic, StringComparer.Ordinal))
            {
                return Task.FromResult(Result<GetSchemaResponse>.Failure(new Error(
                    "Schema.TopicNotFound",
                    $"Unknown spec topic '{topic}'. Supported topics: {string.Join(", ", SupportedTopics)}.",
                    ErrorType.NotFound)));
            }
        }

        var activeTopics = requested.Count == 0 ? SupportedTopics.ToList() : requested;
        var schemas = new List<SchemaTopicDefinition>();

        foreach (var t in activeTopics)
        {
            switch (t)
            {
                case "session": schemas.Add(BuildSessionSchema()); break;
                case "article": schemas.Add(BuildArticleSchema()); break;
                case "quiz": schemas.Add(BuildQuizSchema()); break;
                case "text": schemas.Add(BuildTextBlockSchema()); break;
                case "image": schemas.Add(BuildImageBlockSchema()); break;
                case "mcq": schemas.Add(BuildMcqSchema()); break;
                case "flashcard": schemas.Add(BuildFlashcardSchema()); break;
                case "callout": schemas.Add(BuildCalloutSchema()); break;
                case "markdown": schemas.Add(BuildMarkdownSchema()); break;
                case "analyze": schemas.Add(BuildAnalyzeSchema()); break;
                case "pkg": schemas.Add(BuildPkgSchema()); break;
                case "build": schemas.Add(BuildBuildSchema()); break;
            }
        }

        var layoutProps = activeTopics.Contains("layouts", StringComparer.Ordinal) ? BuildLayoutSpecs() : null;
        return Task.FromResult(Result<GetSchemaResponse>.Success(new GetSchemaResponse(activeTopics, schemas, layoutProps)));
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
            });

    private static SchemaTopicDefinition BuildArticleSchema() =>
        new(
            Topic: "article",
            CompactSignature: """
                // Article component (ref auto-slugified from title if omitted; max 32, ^[a-z0-9][a-z0-9_-]{0,31}$)
                { kind:"article", ref?:string, title?:string, layoutOverrides?:Record<string,string>, blocks:[TextBlock|ImageBlock|CalloutBlock] }
                // Ex: {"kind":"article","ref":"core-laws","title":"Basics","blocks":[{"kind":"text","text":"## Law\n$$F=ma$$"}]}
                """,
            Command: "mby apply <sessionID|componentID> <spec.json|md|->",
            Summary: "Article component with ordered text/image/callout blocks.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("kind", "string", true, "Use 'article'.", ["article"]),
                new("ref", "string", false, "Optional workspace-unique slug (auto-generated from title if omitted).", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern),
                new("title", "string", false, "Section title."),
                new("layoutOverrides", "object<string,string>", false, "Bounded article layout overrides."),
                new("blocks", "array<block>", true, "Ordered text, image, or callout blocks.")
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
            });

    private static SchemaTopicDefinition BuildQuizSchema() =>
        new(
            Topic: "quiz",
            CompactSignature: """
                // Standalone Quiz (Set_*) or Embedded Quiz Component (ref max 32, auto if omitted on component)
                { kind:"quiz", ref?:string /* max 32, ^[a-z0-9][a-z0-9_-]{0,31}$ */, quizType?:"mcq"|"flashcard"|"mixed", name?:string, description?:string, title?:string, layoutOverrides?:Record<string,string>, questions:[Mcq|Flashcard] }
                // Ex: {"kind":"quiz","quizType":"mixed","name":"Alg Quiz","questions":[{"type":"flashcard","front":{"text":"O(log n)?"},"back":{"text":"Binary search"}}]}
                """,
            Command: "mby apply [<setID|componentID>] <spec.json|-> [-o dir]",
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
                new("questions", "array<question>", true, "Ordered mcq/flashcard objects.")
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
            });

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
                new("kind", "string", true, "Must be 'text'.", ["text"]),
                new("ref", "string", false, "Optional slug.", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern),
                new("text", "string", true, "Non-empty Markdown/KaTeX text.")
            ],
            Example: new { kind = "text", text = "# Heading\n\nBody text with $E = mc^2$." });

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
                new("kind", "string", true, "Must be 'image'.", ["image"]),
                new("ref", "string", false, "Optional slug.", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern),
                new("imagePath", "string", true, "Relative path inside JSON/MD/workspace root."),
                new("caption", "string", false, "Optional caption rendered atomically inside <figcaption> (supports Markdown/KaTeX)."),
                new("alt", "string", false, "Optional accessibility alt text.")
            ],
            Example: new { kind = "image", imagePath = "assets/fig_2.1.png", caption = "Fig. 2.1 Overview", alt = "Overview diagram" });

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
                new("type", "string", true, "Must be 'mcq'.", ["mcq"]),
                new("ref", "string", false, "Optional slug.", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern),
                new("text", "string", true, "Prompt Markdown."),
                new("imagePath", "string", false, "Optional prompt image."),
                new("explanation", "string", false, "Optional explanation Markdown."),
                new("isMultiCorrect", "boolean", false, "Multiple correct answers (default false)."),
                new("maxOptions", "integer", false, "Option slot cap (2..26, default 4).", null, null, null, 2, 26),
                new("options", "array<mcqOption>", true, "2..26 options with text/imagePath + isCorrect."),
                new("layoutOverrides", "object<string,string>", false, "Bounded mcq layout overrides.")
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
            });

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
                new("type", "string", true, "Must be 'flashcard'.", ["flashcard"]),
                new("ref", "string", false, "Optional slug.", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern),
                new("front", "flashcardSide", true, "Front face: text and/or imagePath + position."),
                new("back", "flashcardSide", true, "Back face: text and/or imagePath + position."),
                new("layoutOverrides", "object<string,string>", false, "Bounded flashcard layout overrides.")
            ],
            Example: new
            {
                type = "flashcard",
                front = new { text = "Binary search worst case?", position = "belowImage" },
                back = new { text = "$O(\\log n)$" }
            });

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
                new("kind", "string", true, "Must be 'callout'.", ["callout"]),
                new("tone", "string", false, "Callout style tone (defaults to 'pearl' when omitted).", ["pearl", "clinical", "warning"]),
                new("title", "string", false, "Optional callout header title."),
                new("text", "string", true, "Non-empty Markdown/KaTeX body."),
                new("ref", "string", false, "Optional slug.", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern)
            ],
            Example: new { kind = "callout", tone = "clinical", title = "Clinical Correlation", text = "Elevated serum troponin indicates cardiac myocyte necrosis." });

    private static SchemaTopicDefinition BuildMarkdownSchema() =>
        new(
            Topic: "markdown",
            CompactSignature: """
                // Markdown article / directory input (replaces verbose JSON escaping)
                // Optional YAML frontmatter: ---\nkind: article\ntitle: "Cell Injury"\nref: cell-injury\n---
                // Standalone image: ![alt text](assets/fig_2.1.png "Caption text") -> ImageBlock(imagePath, alt, caption)
                // Callout fence: :::callout{tone="clinical" title="Clinical Note"}\n...\n:::  (or :::pearl Title / :::warning Title)
                // Directory mode: mby apply ./articles/  or  mby build ./articles/ -o ./web (reads _session.md|session.json + sorted *.md/*.json)
                """,
            Command: "mby apply <article.md|dir|->  |  mby build <dir|article.md> -o <webDir>",
            Summary: "Author articles in clean Markdown + frontmatter or modular directories.",
            DisallowUnmappedProperties: false,
            Fields:
            [
                new("title", "string", false, "YAML frontmatter title (falls back to first # Heading or filename stem)."),
                new("ref", "string", false, "YAML frontmatter ref (auto-slugified from filename/title if omitted).", null, RefKeyValidator.MaxLength, RefKeyValidator.Pattern),
                new("kind", "string", false, "'article' (default for .md) or 'session' (in _session.md).", ["article", "session"]),
                new("name", "string", false, "Session name in _session.md frontmatter."),
                new("order", "array<string>", false, "Optional filename order override in _session.md.")
            ],
            Example: new
            {
                file = "01-intro.md",
                preview = "---\ntitle: Cellular Responses\n---\n\n# Overview\n\n![Necrosis](assets/fig_2.1.png \"Fig. 2.1 Stages\")\n\n:::clinical Correlation\nReversible swelling precedes lysis.\n:::"
            });

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
                new("--live", "boolean", false, "Launch interactive browser preview + /__mby_analysis endpoint to record *.analysis.json."),
                new("--all", "boolean", false, "Aggregate all saved *.analysis.json reports in <webDir>/reports.")
            ],
            Example: new { command = "mby analyze ./study_web --headless --json" });

    private static SchemaTopicDefinition BuildPkgSchema() =>
        new(
            Topic: "pkg",
            CompactSignature: """
                // Export/Import portable .mbypkg archive (1.0 schema; desktop or android target)
                // Export: mby pkg export <wsId> <desktop|android> [outPath.mbypkg] [--include-layout]
                // Import: mby pkg import <file.mbypkg> [destDir]
                """,
            Command: "mby pkg export <wsId> <desktop|android> [outPath] [--include-layout] | mby pkg import <file.mbypkg> [destDir]",
            Summary: "Mandatory lint runs before export; default output writes <wsId>.mbypkg into CWD.",
            DisallowUnmappedProperties: false,
            Fields:
            [
                new("targetPlatform", "string", true, "Target package consumer.", ["desktop", "android"]),
                new("--include-layout", "boolean", false, "Bundle non-default global/entity layout overrides."),
                new("outputPath", "string", false, "Optional .mbypkg destination path (defaults to <cwd>/<wsId>.mbypkg).")
            ],
            Example: new { exportCmd = "mby pkg export Session_123 android ./chapter2.mbypkg --json" });

    private static SchemaTopicDefinition BuildBuildSchema() =>
        new(
            Topic: "build",
            CompactSignature: """
                // Compile Study WebApp from Workspace ID, JSON, Markdown file, or Directory
                // mby build <addr|spec.json|article.md|dir|-> [-o webDir] [--verify] [--preview] [--no-wait] [--verbose]
                """,
            Command: "mby build <addr|spec|dir|-> [-o webDir] [--verify] [--preview] [--no-wait] [--quiet|--verbose]",
            Summary: "Lints workspace, compiles standalone offline HTML/CSS/JS (+ search index), optionally runs --verify CDP check.",
            DisallowUnmappedProperties: false,
            Fields:
            [
                new("target", "string", true, "Existing Session_*/Set_* ID, spec file, Markdown directory, or '-'."),
                new("-o", "string", false, "Output web directory (defaults to <id>_web in project root)."),
                new("--verify", "boolean", false, "Run headless browser verification immediately after building (exits 2 on failure)."),
                new("--preview", "boolean", false, "Open interactive local preview server.")
            ],
            Example: new { buildCmd = "mby build ./articles/ -o ./study_web --verify --json" });

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
}
