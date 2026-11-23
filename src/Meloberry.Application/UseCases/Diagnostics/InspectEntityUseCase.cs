using Meloberry.Application.UseCases.Common;
using Meloberry.Domain.Common;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Sessions;

namespace Meloberry.Application.UseCases.Diagnostics;

public enum InspectViewMode { Outline, Full, Ids }

public sealed record InspectEntityQuery(string Address, InspectViewMode View = InspectViewMode.Outline);

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
    string? Explanation = null);

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
    string? Title = null);

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
    IReadOnlyDictionary<string, string>? LayoutOverrides = null);

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
    InspectQuestionDto? Question = null);

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
            LayoutOverrides: CopyOverridesOrNull(component.LayoutOverrides));
    }

    private static InspectArticleBlockDto MapBlock(ArticleBlock block, string path, InspectViewMode view)
    {
        string kind = block.Kind switch
        {
            ArticleBlockKind.Image => "image",
            ArticleBlockKind.Callout => "callout",
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
            Title: block.CalloutTitle);
    }

    private static InspectQuestionDto MapQuestion(Question question, string path, InspectViewMode view)
    {
        string type = question is McqQuestion ? "mcq" : "flashcard";
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

            var fc = (FlashcardQuestion)question;
            return new(
                Id: fc.Id,
                Index: fc.Index,
                Type: "flashcard",
                Path: path,
                Ref: fc.RefKey,
                Snippet: MakeSnippet(fc.Front.Text ?? fc.Front.ImagePath),
                LayoutOverrides: CopyOverridesOrNull(fc.LayoutOverrides));
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

        var card = (FlashcardQuestion)question;
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
