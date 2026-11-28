using System.Text.Json.Nodes;
using Meloberry.Application.UseCases.Common;
using Meloberry.Domain.Common;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Sessions;

namespace Meloberry.Application.UseCases.Diagnostics;

public enum InspectViewMode { Outline, Full, Ids, Spec, Markdown }

public sealed record InspectEntityQuery(
    string Address,
    InspectViewMode View = InspectViewMode.Outline,
    string? OutputDirectory = null);

public sealed record InspectFlashcardSideDto(string? Text, string? ImagePath, string Position);
public sealed record InspectMcqOptionDto(int Index, string? Text, string? ImagePath, bool IsCorrect);

public sealed record InspectQuestionDto(
    string Id,
    int Index,
    string Type,
    string? Path = null,
    string? Ref = null,
    string? Snippet = null,
    int? OptionCount = null,
    string? Text = null,
    int? MaxOptions = null,
    bool? IsMultiCorrect = null,
    IReadOnlyList<InspectMcqOptionDto>? Options = null,
    InspectFlashcardSideDto? Front = null,
    InspectFlashcardSideDto? Back = null,
    IReadOnlyDictionary<string, string>? LayoutOverrides = null,
    string? ImagePath = null,
    string? Explanation = null,
    string? PayloadJson = null);

public sealed record InspectArticleBlockDto(
    string Id,
    int Index,
    string Kind,
    string? Path = null,
    string? Ref = null,
    string? Snippet = null,
    string? TextContent = null,
    string? ImagePath = null,
    string? Caption = null,
    string? Alt = null,
    string? Tone = null,
    string? Title = null,
    string? PayloadJson = null);

public sealed record InspectComponentDto(
    string Id,
    int Index,
    string Kind,
    string? Path = null,
    string? Ref = null,
    string? QuizType = null,
    string? Title = null,
    int? ItemCount = null,
    IReadOnlyList<InspectArticleBlockDto>? ArticleBlocks = null,
    IReadOnlyList<InspectQuestionDto>? Questions = null,
    IReadOnlyDictionary<string, string>? LayoutOverrides = null,
    IReadOnlyList<ArticleLinkCard>? Links = null);

public sealed record InspectEntityResponse(
    string EntityKind,
    string Id,
    string View,
    string? StorageDirectory = null,
    string? SessionId = null,
    string? ContainerId = null,
    string? Name = null,
    string? Description = null,
    string? QuizType = null,
    IReadOnlyList<InspectComponentDto>? Components = null,
    InspectComponentDto? Component = null,
    InspectArticleBlockDto? ArticleBlock = null,
    IReadOnlyList<InspectQuestionDto>? Questions = null,
    InspectQuestionDto? Question = null,
    JsonNode? Spec = null,
    InspectIdMapDto? IdMap = null,
    IReadOnlyDictionary<string, string>? MarkdownFiles = null,
    string? WrittenDirectory = null);

public sealed class InspectEntityHandler(EntityAddressResolver addressResolver)
{
    public async Task<Result<InspectEntityResponse>> HandleAsync(InspectEntityQuery query, CancellationToken cancellationToken = default)
    {
        var resolved = await addressResolver.ResolveAsync(query.Address, cancellationToken);
        if (!resolved.IsSuccess)
            return new Error("Inspect.TargetNotFound", $"Target '{query.Address}' was not found.", ErrorType.NotFound);

        var entity = resolved.Value;
        var viewName = query.View.ToString().ToLowerInvariant();
        var storage = query.View == InspectViewMode.Ids ? null : entity.StorageDirectory;

        if (query.View == InspectViewMode.Spec)
            return ProjectSpecResponse(entity, viewName, storage);

        if (query.View == InspectViewMode.Markdown)
            return await ProjectMarkdownResponseAsync(entity, viewName, storage, query.OutputDirectory, cancellationToken);

        return entity.EntityKind switch
        {
            "session" => new InspectEntityResponse(
                EntityKind: "session",
                Id: entity.Session!.Id,
                View: viewName,
                StorageDirectory: storage,
                Name: entity.Session.Name,
                Description: query.View == InspectViewMode.Ids || string.IsNullOrWhiteSpace(entity.Session.Description) ? null : entity.Session.Description,
                Components: entity.Session.Components.Select(c => MapComponent(c, query.View)).ToList()),

            "component" => new InspectEntityResponse(
                EntityKind: "component",
                Id: entity.Component!.Id,
                View: viewName,
                StorageDirectory: storage,
                SessionId: entity.Session!.Id,
                Component: MapComponent(entity.Component, query.View)),

            "articleBlock" => new InspectEntityResponse(
                EntityKind: "articleBlock",
                Id: entity.ArticleBlock!.Id,
                View: viewName,
                StorageDirectory: storage,
                SessionId: entity.Session!.Id,
                ContainerId: entity.Component!.Id,
                ArticleBlock: MapBlock(entity.ArticleBlock, $"c{entity.Component.Index}.b{entity.ArticleBlock.Index}", query.View)),

            "quiz" => new InspectEntityResponse(
                EntityKind: "quiz",
                Id: entity.Quiz!.Id,
                View: viewName,
                StorageDirectory: storage,
                Name: string.IsNullOrWhiteSpace(entity.Quiz.Name) ? null : entity.Quiz.Name,
                Description: query.View == InspectViewMode.Ids || string.IsNullOrWhiteSpace(entity.Quiz.Description) ? null : entity.Quiz.Description,
                QuizType: entity.Quiz.Type.ToString().ToLowerInvariant(),
                Questions: entity.Quiz.Questions.Select(q => MapQuestion(q, $"q{q.Index}", query.View)).ToList()),

            _ => new InspectEntityResponse(
                EntityKind: "question",
                Id: entity.Question!.Id,
                View: viewName,
                StorageDirectory: storage,
                SessionId: entity.Session?.Id,
                ContainerId: entity.Quiz!.Id,
                Question: MapQuestion(
                    entity.Question,
                    entity.Component is not null ? $"c{entity.Component.Index}.q{entity.Question.Index}" : $"q{entity.Question.Index}",
                    query.View))
        };
    }

    private static async Task<Result<InspectEntityResponse>> ProjectMarkdownResponseAsync(
        ResolvedEntity entity,
        string viewName,
        string? storage,
        string? outputDirectory,
        CancellationToken ct)
    {
        Dictionary<string, string> files = entity.EntityKind switch
        {
            "session" => WorkspaceMarkdownProjector.ProjectSessionToFiles(entity.Session!),
            "quiz" => WorkspaceMarkdownProjector.ProjectQuizToFiles(entity.Quiz!),
            "component" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [$"{entity.Component!.RefKey ?? $"c{entity.Component.Index}"}.md"] = WorkspaceMarkdownProjector.ProjectComponentToMarkdown(entity.Component)
            },
            "articleBlock" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [$"b{entity.ArticleBlock!.Index}.md"] = WorkspaceMarkdownProjector.ProjectBlockToMarkdown(entity.ArticleBlock)
            },
            _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [$"q{entity.Question!.Index}.md"] = WorkspaceMarkdownProjector.ProjectQuestionToMarkdown(entity.Question)
            }
        };

        string? writtenDir = null;
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            writtenDir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(writtenDir);
            foreach (var (name, content) in files)
                await File.WriteAllTextAsync(Path.Combine(writtenDir, name), content, ct);

            var assetsToCopy = entity.Session is not null
                ? WorkspaceAssetCollector.CollectSessionAssets(entity.Session)
                : entity.Quiz is not null
                    ? WorkspaceAssetCollector.CollectQuizAssets(entity.Quiz)
                    : [];

            foreach (var relAsset in assetsToCopy.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Meloberry.Application.Abstractions.PathConfinement.TryResolveInside(entity.StorageDirectory, relAsset, out var srcFull) &&
                    File.Exists(srcFull) &&
                    Meloberry.Application.Abstractions.PathConfinement.TryResolveInside(writtenDir, relAsset, out var dstFull))
                {
                    var dstParent = Path.GetDirectoryName(dstFull);
                    if (!string.IsNullOrWhiteSpace(dstParent)) Directory.CreateDirectory(dstParent);
                    File.Copy(srcFull, dstFull, overwrite: true);
                }
            }
        }

        return new InspectEntityResponse(
            EntityKind: entity.EntityKind,
            Id: entity.EntityId,
            View: viewName,
            StorageDirectory: storage,
            SessionId: entity.Session?.Id,
            ContainerId: entity.Component?.Id ?? (entity.EntityKind == "question" ? entity.Quiz?.Id : null),
            MarkdownFiles: files,
            WrittenDirectory: writtenDir);
    }

    private static InspectEntityResponse ProjectSpecResponse(ResolvedEntity entity, string viewName, string? storage)
    {
        switch (entity.EntityKind)
        {
            case "session":
            {
                var (spec, idMap) = WorkspaceSpecProjector.ProjectSession(entity.Session!);
                return new InspectEntityResponse(
                    EntityKind: "session",
                    Id: entity.Session!.Id,
                    View: viewName,
                    StorageDirectory: storage,
                    Spec: spec,
                    IdMap: idMap);
            }
            case "component":
            {
                var (spec, idMap) = WorkspaceSpecProjector.ProjectComponent(entity.Component!);
                return new InspectEntityResponse(
                    EntityKind: "component",
                    Id: entity.Component!.Id,
                    View: viewName,
                    StorageDirectory: storage,
                    SessionId: entity.Session!.Id,
                    Spec: spec,
                    IdMap: idMap);
            }
            case "articleBlock":
            {
                var bPath = $"c{entity.Component!.Index}.b{entity.ArticleBlock!.Index}";
                var (spec, idMap) = WorkspaceSpecProjector.ProjectArticleBlock(entity.ArticleBlock, bPath);
                return new InspectEntityResponse(
                    EntityKind: "articleBlock",
                    Id: entity.ArticleBlock.Id,
                    View: viewName,
                    StorageDirectory: storage,
                    SessionId: entity.Session!.Id,
                    ContainerId: entity.Component.Id,
                    Spec: spec,
                    IdMap: idMap);
            }
            case "quiz":
            {
                var (spec, idMap) = WorkspaceSpecProjector.ProjectQuiz(entity.Quiz!);
                return new InspectEntityResponse(
                    EntityKind: "quiz",
                    Id: entity.Quiz!.Id,
                    View: viewName,
                    StorageDirectory: storage,
                    Spec: spec,
                    IdMap: idMap);
            }
            default:
            {
                var qPath = entity.Component is not null
                    ? $"c{entity.Component.Index}.q{entity.Question!.Index}"
                    : $"q{entity.Question!.Index}";
                var (spec, idMap) = WorkspaceSpecProjector.ProjectQuestionWithIdMap(entity.Question!, qPath);
                return new InspectEntityResponse(
                    EntityKind: "question",
                    Id: entity.Question!.Id,
                    View: viewName,
                    StorageDirectory: storage,
                    SessionId: entity.Session?.Id,
                    ContainerId: entity.Quiz!.Id,
                    Spec: spec,
                    IdMap: idMap);
            }
        }
    }

    private static InspectComponentDto MapComponent(SessionComponent component, InspectViewMode view)
    {
        string path = $"c{component.Index}";
        string kind = component.Kind == ComponentKind.Article ? "article" : "quiz";
        string? quizType = component.Kind == ComponentKind.Quiz && component.Quiz is not null
            ? component.Quiz.Type.ToString().ToLowerInvariant()
            : null;
        int itemCount = component.Kind == ComponentKind.Article
            ? component.ArticleBlocks.Count
            : (component.Quiz?.Questions.Count ?? 0);

        if (view == InspectViewMode.Ids)
        {
            return new InspectComponentDto(
                Id: component.Id,
                Index: component.Index,
                Kind: kind,
                Path: path,
                Ref: component.RefKey,
                QuizType: quizType,
                ArticleBlocks: component.Kind == ComponentKind.Article
                    ? component.ArticleBlocks.Select(b => MapBlock(b, $"{path}.b{b.Index}", view)).ToList()
                    : null,
                Questions: component.Kind == ComponentKind.Quiz && component.Quiz is not null
                    ? component.Quiz.Questions.Select(q => MapQuestion(q, $"{path}.q{q.Index}", view)).ToList()
                    : null);
        }

        return new InspectComponentDto(
            Id: component.Id,
            Index: component.Index,
            Kind: kind,
            Path: path,
            Ref: component.RefKey,
            QuizType: quizType,
            Title: component.Title,
            ItemCount: view == InspectViewMode.Outline ? itemCount : null,
            ArticleBlocks: component.Kind == ComponentKind.Article
                ? component.ArticleBlocks.Select(b => MapBlock(b, $"{path}.b{b.Index}", view)).ToList()
                : null,
            Questions: component.Kind == ComponentKind.Quiz && component.Quiz is not null
                ? component.Quiz.Questions.Select(q => MapQuestion(q, $"{path}.q{q.Index}", view)).ToList()
                : null,
            LayoutOverrides: CopyOverridesOrNull(component.LayoutOverrides),
            Links: view == InspectViewMode.Full && component.LinkCards.Count > 0 ? component.LinkCards.ToList() : null);
    }

    private static InspectArticleBlockDto MapBlock(ArticleBlock block, string path, InspectViewMode view)
    {
        string kind = block.Kind switch
        {
            ArticleBlockKind.Image => "image",
            ArticleBlockKind.Callout => "callout",
            ArticleBlockKind.Table => "table",
            ArticleBlockKind.DiagramExplore => "diagram-explore",
            ArticleBlockKind.Reveal => "reveal",
            _ => "text"
        };
        string? toneStr = block.CalloutTone.HasValue ? ArticleBlock.CalloutToneToString(block.CalloutTone.Value) : null;

        if (view == InspectViewMode.Ids)
            return new(Id: block.Id, Index: block.Index, Kind: kind, Path: path, Ref: block.RefKey);

        if (view == InspectViewMode.Outline)
        {
            var raw = block.Kind switch
            {
                ArticleBlockKind.Image => string.IsNullOrWhiteSpace(block.Caption) ? block.ImagePath : $"{block.ImagePath} ({block.Caption})",
                ArticleBlockKind.Callout => $"[{toneStr ?? "pearl"}] {(string.IsNullOrWhiteSpace(block.CalloutTitle) ? "" : block.CalloutTitle + ": ")}{block.TextContent}",
                ArticleBlockKind.Table => FormatTableOutline(block),
                ArticleBlockKind.DiagramExplore => FormatDiagramExploreOutline(block),
                ArticleBlockKind.Reveal => FormatRevealOutline(block),
                _ => block.TextContent
            };
            return new(Id: block.Id, Index: block.Index, Kind: kind, Path: path, Ref: block.RefKey, Snippet: MakeSnippet(raw), Tone: toneStr);
        }

        return new(
            Id: block.Id,
            Index: block.Index,
            Kind: kind,
            Path: path,
            Ref: block.RefKey,
            TextContent: block.TextContent,
            ImagePath: block.ImagePath,
            Caption: block.Caption,
            Alt: block.Alt,
            Tone: toneStr,
            Title: block.CalloutTitle,
            PayloadJson: block.PayloadJson);
    }

    private static string FormatTableOutline(ArticleBlock block)
    {
        if (Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(block.PayloadJson, out var t) && t is not null)
        {
            var title = !string.IsNullOrWhiteSpace(t.Title) ? t.Title : (block.CalloutTitle ?? "Table");
            return $"[table] {title} ({t.Rows?.Count ?? 0}x{t.Headers?.Count ?? 0})";
        }
        return "[table]";
    }

    private static string FormatDiagramExploreOutline(ArticleBlock block)
    {
        if (Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(block.PayloadJson, out var d) && d is not null)
        {
            var title = !string.IsNullOrWhiteSpace(d.Title) ? d.Title : (block.CalloutTitle ?? d.ImagePath);
            return $"[diagram-explore] {title} ({d.Hotspots?.Count ?? 0} hotspots)";
        }
        return "[diagram-explore]";
    }

    private static string FormatRevealOutline(ArticleBlock block)
    {
        if (Phase1PayloadCodec.TryDeserialize<RevealBlockPayload>(block.PayloadJson, out var r) && r is not null)
        {
            var title = !string.IsNullOrWhiteSpace(r.Title) ? r.Title : (block.CalloutTitle ?? "Reveal");
            return $"[reveal] {title} ({r.Steps?.Count ?? 0} steps)";
        }
        return "[reveal]";
    }

    private static InspectQuestionDto MapQuestion(Question question, string path, InspectViewMode view)
    {
        string type = Question.ToWireString(question.Type);
        if (view == InspectViewMode.Ids)
            return new(Id: question.Id, Index: question.Index, Type: type, Path: path, Ref: question.RefKey);

        if (view == InspectViewMode.Outline)
        {
            if (question is McqQuestion m)
            {
                return new(
                    Id: m.Id,
                    Index: m.Index,
                    Type: "mcq",
                    Path: path,
                    Ref: m.RefKey,
                    Snippet: MakeSnippet(m.Text),
                    OptionCount: m.Options.Count,
                    IsMultiCorrect: m.IsMultiCorrect ? true : null,
                    LayoutOverrides: CopyOverridesOrNull(m.LayoutOverrides));
            }

            if (question is FlashcardQuestion fc)
            {
                return new(
                    Id: fc.Id,
                    Index: fc.Index,
                    Type: "flashcard",
                    Path: path,
                    Ref: fc.RefKey,
                    Snippet: MakeSnippet(fc.Front.Text ?? fc.Front.ImagePath),
                    LayoutOverrides: CopyOverridesOrNull(fc.LayoutOverrides));
            }

            return new(
                Id: question.Id,
                Index: question.Index,
                Type: type,
                Path: path,
                Ref: question.RefKey,
                Snippet: MakeSnippet(FormatPayloadQuestionOutline(question)),
                LayoutOverrides: CopyOverridesOrNull(question.LayoutOverrides));
        }

        if (question is McqQuestion mcq)
        {
            return new InspectQuestionDto(
                Id: mcq.Id,
                Index: mcq.Index,
                Type: "mcq",
                Path: path,
                Ref: mcq.RefKey,
                Text: mcq.Text,
                MaxOptions: mcq.MaxOptions,
                IsMultiCorrect: mcq.IsMultiCorrect,
                Options: mcq.Options.Select(o => new InspectMcqOptionDto(o.Index, o.Text, o.ImagePath, o.IsCorrect)).ToList(),
                LayoutOverrides: CopyOverridesOrNull(mcq.LayoutOverrides),
                ImagePath: mcq.ImagePath,
                Explanation: mcq.Explanation);
        }

        if (question is FlashcardQuestion card)
        {
            return new InspectQuestionDto(
                Id: card.Id,
                Index: card.Index,
                Type: "flashcard",
                Path: path,
                Ref: card.RefKey,
                Front: MapSide(card.Front),
                Back: MapSide(card.Back),
                LayoutOverrides: CopyOverridesOrNull(card.LayoutOverrides));
        }

        return new InspectQuestionDto(
            Id: question.Id,
            Index: question.Index,
            Type: type,
            Path: path,
            Ref: question.RefKey,
            LayoutOverrides: CopyOverridesOrNull(question.LayoutOverrides),
            PayloadJson: question.PayloadJson);
    }

    private static string? FormatPayloadQuestionOutline(Question q) => q.Type switch
    {
        QuestionType.FillBlank when Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(q.PayloadJson, out var fb) && fb is not null => fb.Template,
        QuestionType.AssertionReason when Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(q.PayloadJson, out var ar) && ar is not null => $"A: {ar.Assertion} | R: {ar.Reason}",
        QuestionType.SpotError when Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(q.PayloadJson, out var se) && se is not null => se.Template,
        QuestionType.Differentiation when Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(q.PayloadJson, out var d) && d is not null =>
            $"[diff:{d.Mode}] {(!string.IsNullOrWhiteSpace(d.Title) ? d.Title : string.Join(" vs ", (d.Headers ?? []).Skip(1)))} ({d.Rows?.Count ?? 0}x{d.Headers?.Count ?? 0})",
        QuestionType.Mechanism when Phase1PayloadCodec.TryDeserialize<MechanismPayload>(q.PayloadJson, out var m) && m is not null =>
            $"[mechanism] {(!string.IsNullOrWhiteSpace(m.Title) ? m.Title : (m.Steps ?? []).FirstOrDefault(s => s is not null && !s.IsDistractor)?.Text)} ({(m.Steps ?? []).Count(s => s is not null && !s.IsDistractor)} steps)",
        QuestionType.RelabelImage when Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(q.PayloadJson, out var r) && r is not null =>
            $"[relabel:{r.Mode}/{r.Style}] {(!string.IsNullOrWhiteSpace(r.Prompt) ? r.Prompt : r.ImagePath)} ({r.Boxes?.Count ?? 0} boxes)",
        QuestionType.Classify when Phase1PayloadCodec.TryDeserialize<ClassifyPayload>(q.PayloadJson, out var cl) && cl is not null =>
            $"[classify] {(!string.IsNullOrWhiteSpace(cl.Title) ? cl.Title : "Classification")} ({cl.Buckets?.Count ?? 0} buckets, {cl.Items?.Count ?? 0} items)",
        QuestionType.Match when Phase1PayloadCodec.TryDeserialize<MatchPayload>(q.PayloadJson, out var mt) && mt is not null =>
            $"[match] {(!string.IsNullOrWhiteSpace(mt.Title) ? mt.Title : mt.Pairs?.FirstOrDefault()?.Left)} ({mt.Pairs?.Count ?? 0} pairs)",
        QuestionType.Numeric when Phase1PayloadCodec.TryDeserialize<NumericPayload>(q.PayloadJson, out var nm) && nm is not null =>
            $"[numeric] {nm.Prompt} ({nm.Answer} +/- {nm.Tolerance}{(nm.ToleranceMode == "percent" ? "%" : "")}{(string.IsNullOrWhiteSpace(nm.Unit) ? "" : " " + nm.Unit)})",
        QuestionType.CaseGroup when Phase1PayloadCodec.TryDeserialize<CaseGroupPayload>(q.PayloadJson, out var cs) && cs is not null =>
            $"[case] {(!string.IsNullOrWhiteSpace(cs.Title) ? cs.Title : cs.Stem)} ({cs.Questions?.Count ?? 0} sub-questions)",
        _ => q.PayloadJson
    };

    private static InspectFlashcardSideDto MapSide(FlashcardSide side) =>
        new(
            Text: side.Text,
            ImagePath: side.ImagePath,
            Position: side.Position == MediaPosition.TextAboveImage ? "aboveImage" : "belowImage");

    private static Dictionary<string, string>? CopyOverridesOrNull(IReadOnlyDictionary<string, string> overrides) =>
        overrides.Count == 0 ? null : new(overrides, StringComparer.OrdinalIgnoreCase);

    private static string? MakeSnippet(string? raw, int maxLen = 40)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var flat = raw.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= maxLen ? flat : $"{flat[..maxLen]}...";
    }
}
