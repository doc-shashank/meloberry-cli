using Meloberry.Domain.Common;
using Meloberry.Domain.Layouts;

namespace Meloberry.Application.UseCases.Diagnostics;

public sealed record GetSchemaQuery(IReadOnlyList<string>? Topics = null);

public sealed record SchemaFieldSpec(
    string Name,
    string Type,
    bool Required,
    string Description,
    IReadOnlyList<string>? AllowedValues = null);

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
        ["session", "article", "quiz", "text", "image", "mcq", "flashcard", "layouts"];

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
                // Article component
                { kind:"article", ref?:string, title?:string, layoutOverrides?:Record<string,string>, blocks:[TextBlock|ImageBlock] }
                // Ex: {"kind":"article","ref":"intro","title":"Basics","blocks":[{"kind":"text","text":"## Law\n$$F=ma$$"}]}
                """,
            Command: "mby apply <sessionID|componentID> <spec.json|->",
            Summary: "Article component with ordered text/image blocks.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("kind", "string", true, "Use 'article'.", ["article"]),
                new("ref", "string", false, "Optional workspace-unique slug (^[a-z0-9][a-z0-9_-]{0,31}$)."),
                new("title", "string", false, "Section title."),
                new("layoutOverrides", "object<string,string>", false, "Bounded article layout overrides."),
                new("blocks", "array<block>", true, "Ordered text/image blocks.")
            ],
            Example: new
            {
                kind = "article",
                @ref = "core-laws",
                title = "Core Formulas",
                blocks = new object[]
                {
                    new { kind = "text", text = "## Newton's Second Law\n\n$$F = ma$$" },
                    new { kind = "image", imagePath = "images/force.png" }
                }
            });

    private static SchemaTopicDefinition BuildQuizSchema() =>
        new(
            Topic: "quiz",
            CompactSignature: """
                // Standalone Quiz (Set_*) or Embedded Quiz Component
                { kind:"quiz", ref?:string, quizType?:"mcq"|"flashcard"|"mixed", name?:string, description?:string, title?:string, layoutOverrides?:Record<string,string>, questions:[Mcq|Flashcard] }
                // Ex: {"kind":"quiz","quizType":"mixed","name":"Alg Quiz","questions":[{"type":"flashcard","front":{"text":"O(log n)?"},"back":{"text":"Binary search"}}]}
                """,
            Command: "mby apply [<setID|componentID>] <spec.json|-> [-o dir]",
            Summary: "Standalone Quiz or embedded Quiz Component.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("kind", "string", false, "Use 'quiz' (or 'mcq'|'flashcard'|'mixed').", ["quiz", "mcq", "flashcard", "mixed"]),
                new("ref", "string", false, "Optional slug when embedded component."),
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
                { kind:"text", ref?:string, text:string }
                // Ex: {"kind":"text","ref":"summary","text":"# Summary\nKey points."}
                """,
            Command: "Inside article.blocks[]",
            Summary: "Markdown + KaTeX vertical text block.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("kind", "string", true, "Must be 'text'.", ["text"]),
                new("ref", "string", false, "Optional slug."),
                new("text", "string", true, "Non-empty Markdown/KaTeX text.")
            ],
            Example: new { kind = "text", text = "# Heading\n\nBody text with $E = mc^2$." });

    private static SchemaTopicDefinition BuildImageBlockSchema() =>
        new(
            Topic: "image",
            CompactSignature: """
                // Article block: image
                { kind:"image", ref?:string, imagePath:string }
                // Ex: {"kind":"image","imagePath":"images/diagram.png"}
                """,
            Command: "Inside article.blocks[]",
            Summary: "Validated image block (PNG/JPEG/GIF/WebP/BMP/SVG).",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("kind", "string", true, "Must be 'image'.", ["image"]),
                new("ref", "string", false, "Optional slug."),
                new("imagePath", "string", true, "Relative path inside JSON/CWD root.")
            ],
            Example: new { kind = "image", imagePath = "images/diagram.png" });

    private static SchemaTopicDefinition BuildMcqSchema() =>
        new(
            Topic: "mcq",
            CompactSignature: """
                // Question: mcq (options: 2..26, at least 1 isCorrect)
                { type:"mcq", ref?:string, text:string, imagePath?:string, explanation?:string, isMultiCorrect?:bool, maxOptions?:2..26, options:[{index?:int,text?:string,imagePath?:string,isCorrect?:bool}], layoutOverrides?:Record<string,string> }
                // Ex: {"type":"mcq","text":"Prime?","options":[{"text":"2","isCorrect":true},{"text":"4","isCorrect":false}]}
                """,
            Command: "Inside quiz.questions[]",
            Summary: "Single or multi-correct MCQ question with optional prompt image & explanation.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("type", "string", true, "Must be 'mcq'.", ["mcq"]),
                new("ref", "string", false, "Optional slug."),
                new("text", "string", true, "Prompt Markdown."),
                new("imagePath", "string", false, "Optional prompt image."),
                new("explanation", "string", false, "Optional post-check explanation Markdown."),
                new("isMultiCorrect", "boolean", false, "Allow multiple correct answers (default false)."),
                new("maxOptions", "integer", false, "Option slot cap (2..26, default 4)."),
                new("options", "array<mcqOption>", true, "2..26 options with text and/or imagePath + isCorrect."),
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
                // Question: flashcard
                { type:"flashcard", ref?:string, front:{text?:string,imagePath?:string,position?:"belowImage"|"aboveImage"}, back:{text?:string,imagePath?:string,position?:"belowImage"|"aboveImage"}, layoutOverrides?:Record<string,string> }
                // Ex: {"type":"flashcard","front":{"text":"H2O"},"back":{"text":"Water"}}
                """,
            Command: "Inside quiz.questions[]",
            Summary: "Two-sided flashcard with text and/or image per face.",
            DisallowUnmappedProperties: true,
            Fields:
            [
                new("type", "string", true, "Must be 'flashcard'.", ["flashcard"]),
                new("ref", "string", false, "Optional slug."),
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
