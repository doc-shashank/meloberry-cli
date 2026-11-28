using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Layouts;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Quizzes;

namespace Meloberry.Domain.Sessions;

public enum ComponentKind { Article, Quiz }

public sealed class SessionComponent : IIndexedEntity
{
    private readonly List<ArticleBlock> _articleBlocks;
    private readonly List<ArticleLinkCard> _linkCards;
    private readonly Dictionary<string, string> _layoutOverrides;

    private SessionComponent(
        string id,
        string sessionId,
        int index,
        ComponentKind kind,
        Quiz? quiz,
        List<ArticleBlock> articleBlocks,
        Dictionary<string, string>? layoutOverrides,
        string? title,
        string? refKey = null,
        List<ArticleLinkCard>? linkCards = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);

        Id = id;
        SessionId = sessionId;
        Index = index;
        Kind = kind;
        Quiz = quiz;
        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        RefKey = string.IsNullOrWhiteSpace(refKey) ? null : refKey.Trim().ToLowerInvariant();
        _articleBlocks = articleBlocks;
        IndexedListReorderer.SortInPlace(_articleBlocks);
        _linkCards = linkCards ?? [];
        _layoutOverrides = layoutOverrides is not null
            ? new Dictionary<string, string>(layoutOverrides, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public string Id { get; private set; }
    public string SessionId { get; private set; }
    public int Index { get; private set; }
    public ComponentKind Kind { get; private set; }
    public Quiz? Quiz { get; private set; }
    public string? Title { get; private set; }
    public string? RefKey { get; private set; }
    public IReadOnlyList<ArticleBlock> ArticleBlocks => _articleBlocks;
    public IReadOnlyList<ArticleLinkCard> LinkCards => _linkCards;
    public IReadOnlyDictionary<string, string> LayoutOverrides => _layoutOverrides;

    public static SessionComponent CreateArticle(string id, string sessionId, int index) =>
        new(id, sessionId, index, ComponentKind.Article, null, [], null, null);

    public static SessionComponent CreateQuiz(string id, string sessionId, int index, QuizType quizType, string workingDirectory) =>
        new(id, sessionId, index, ComponentKind.Quiz, Quiz.CreateForComponent(id, quizType, workingDirectory), [], null, null);

    public static SessionComponent Rehydrate(
        string id,
        string sessionId,
        int index,
        ComponentKind kind,
        Quiz? quiz,
        IEnumerable<ArticleBlock> articleBlocks,
        Dictionary<string, string>? layoutOverrides = null,
        string? title = null,
        string? refKey = null,
        IEnumerable<ArticleLinkCard>? linkCards = null) =>
        new(id, sessionId, index, kind, quiz, [.. articleBlocks], layoutOverrides, title, refKey, linkCards is null ? null : [.. linkCards]);

    public Result SetRefKey(string? refKey)
    {
        var res = RefKeyValidator.ValidateAndNormalize(refKey);
        if (!res.IsSuccess) return res.Error!;
        RefKey = res.Value;
        return Result.Success();
    }

    public bool RemoveLayoutOverride(string propertyName) =>
        _layoutOverrides.Remove(propertyName);

    public Result SetTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return ComponentErrors.EmptyTitle();
        Title = title.Trim();
        return Result.Success();
    }

    public Result<ArticleBlock> AddTextBlock(string artCompId, string textContent, int? targetIndex = null)
    {
        if (Kind != ComponentKind.Article) return ComponentErrors.WrongKind(Id, "article");
        var blockResult = ArticleBlock.CreateText(artCompId, Id, textContent);
        if (!blockResult.IsSuccess) return blockResult.Error!;
        var insertResult = IndexedListReorderer.InsertAt(_articleBlocks, blockResult.Value, targetIndex);
        return insertResult.IsSuccess ? blockResult.Value : insertResult.Error!;
    }

    public Result<ArticleBlock> AddImageBlock(string artCompId, string imagePath, int? targetIndex = null, string? caption = null, string? alt = null)
    {
        if (Kind != ComponentKind.Article) return ComponentErrors.WrongKind(Id, "article");
        var block = ArticleBlock.CreateImage(artCompId, Id, imagePath, caption, alt);
        var insertResult = IndexedListReorderer.InsertAt(_articleBlocks, block, targetIndex);
        return insertResult.IsSuccess ? block : insertResult.Error!;
    }

    public Result<ArticleBlock> AddCalloutBlock(string artCompId, CalloutTone tone, string textContent, string? title = null, int? targetIndex = null)
    {
        if (Kind != ComponentKind.Article) return ComponentErrors.WrongKind(Id, "article");
        var blockResult = ArticleBlock.CreateCallout(artCompId, Id, tone, textContent, title);
        if (!blockResult.IsSuccess) return blockResult.Error!;
        var insertResult = IndexedListReorderer.InsertAt(_articleBlocks, blockResult.Value, targetIndex);
        return insertResult.IsSuccess ? blockResult.Value : insertResult.Error!;
    }

    public Result<ArticleBlock> AddTableBlock(string artCompId, string payloadJson, string? title = null, int? targetIndex = null)
    {
        if (Kind != ComponentKind.Article) return ComponentErrors.WrongKind(Id, "article");
        var blockResult = ArticleBlock.CreateTable(artCompId, Id, payloadJson, title);
        if (!blockResult.IsSuccess) return blockResult.Error!;
        var insertResult = IndexedListReorderer.InsertAt(_articleBlocks, blockResult.Value, targetIndex);
        return insertResult.IsSuccess ? blockResult.Value : insertResult.Error!;
    }

    public Result<ArticleBlock> AddDiagramExploreBlock(
        string artCompId,
        string imagePath,
        string payloadJson,
        string? title = null,
        string? caption = null,
        string? alt = null,
        int? targetIndex = null)
    {
        if (Kind != ComponentKind.Article) return ComponentErrors.WrongKind(Id, "article");
        var blockResult = ArticleBlock.CreateDiagramExplore(artCompId, Id, imagePath, payloadJson, title, caption, alt);
        if (!blockResult.IsSuccess) return blockResult.Error!;
        var insertResult = IndexedListReorderer.InsertAt(_articleBlocks, blockResult.Value, targetIndex);
        return insertResult.IsSuccess ? blockResult.Value : insertResult.Error!;
    }

    public Result<ArticleBlock> AddRevealBlock(string artCompId, string payloadJson, string? title = null, int? targetIndex = null)
    {
        if (Kind != ComponentKind.Article) return ComponentErrors.WrongKind(Id, "article");
        var blockResult = ArticleBlock.CreateReveal(artCompId, Id, payloadJson, title);
        if (!blockResult.IsSuccess) return blockResult.Error!;
        var insertResult = IndexedListReorderer.InsertAt(_articleBlocks, blockResult.Value, targetIndex);
        return insertResult.IsSuccess ? blockResult.Value : insertResult.Error!;
    }

    public Result ReorderBlock(string artCompId, int targetIndex, ReorderMode mode) =>
        Kind != ComponentKind.Article
            ? ComponentErrors.WrongKind(Id, "article")
            : IndexedListReorderer.Reorder(_articleBlocks, artCompId, targetIndex, mode, ArticleErrors.BlockNotFound(artCompId));

    public Result DeleteBlock(string artCompId) =>
        Kind != ComponentKind.Article
            ? ComponentErrors.WrongKind(Id, "article")
            : IndexedListReorderer.RemoveAndShiftUp(_articleBlocks, artCompId, ArticleErrors.BlockNotFound(artCompId));

    public ArticleBlock? FindBlock(string artCompId) =>
        _articleBlocks.FirstOrDefault(b => string.Equals(b.Id, artCompId, StringComparison.Ordinal));

    public Result AddOrReplaceLinkCard(ArticleLinkCard card)
    {
        if (Kind != ComponentKind.Article) return ComponentErrors.WrongKind(Id, "article");
        if (card is null || string.IsNullOrWhiteSpace(card.Id))
            return new Error("ArticleLink.InvalidId", "Link card requires a non-empty 'id'.", ErrorType.Validation);

        var normRes = ArticleLinkRules.ValidateAndNormalizeCardId(card.Id);
        if (!normRes.IsSuccess || string.IsNullOrWhiteSpace(normRes.Value))
            return normRes.Error ?? new Error("ArticleLink.InvalidId", $"Invalid link card id '{card.Id}'.", ErrorType.Validation);

        var text = string.IsNullOrWhiteSpace(card.Text) ? null : card.Text.Trim();
        var img = string.IsNullOrWhiteSpace(card.ImagePath) ? null : card.ImagePath.Trim();
        if (text is null && img is null)
            return new Error("ArticleLink.EmptyContent", $"Link card '{normRes.Value}' must contain text, an image, or both.", ErrorType.Validation);

        var normalizedCard = new ArticleLinkCard(
            normRes.Value,
            string.IsNullOrWhiteSpace(card.Title) ? null : card.Title.Trim(),
            text,
            img);

        int existingIdx = _linkCards.FindIndex(c => string.Equals(c.Id, normalizedCard.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIdx >= 0) _linkCards[existingIdx] = normalizedCard;
        else _linkCards.Add(normalizedCard);

        return Result.Success();
    }

    public bool RemoveLinkCard(string cardId)
    {
        int idx = _linkCards.FindIndex(c => string.Equals(c.Id, cardId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        _linkCards.RemoveAt(idx);
        return true;
    }

    public Result SetLinkCards(IEnumerable<ArticleLinkCard> cards)
    {
        if (Kind != ComponentKind.Article) return ComponentErrors.WrongKind(Id, "article");
        ArgumentNullException.ThrowIfNull(cards);

        var validated = new List<ArticleLinkCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in cards)
        {
            if (card is null || string.IsNullOrWhiteSpace(card.Id))
                return new Error("ArticleLink.InvalidId", "Link card requires a non-empty 'id'.", ErrorType.Validation);

            var normRes = ArticleLinkRules.ValidateAndNormalizeCardId(card.Id);
            if (!normRes.IsSuccess || string.IsNullOrWhiteSpace(normRes.Value))
                return normRes.Error ?? new Error("ArticleLink.InvalidId", $"Invalid link card id '{card.Id}'.", ErrorType.Validation);

            if (!seen.Add(normRes.Value))
                return new Error("ArticleLink.DuplicateId", $"Duplicate link card id '{normRes.Value}'.", ErrorType.Conflict);

            var text = string.IsNullOrWhiteSpace(card.Text) ? null : card.Text.Trim();
            var img = string.IsNullOrWhiteSpace(card.ImagePath) ? null : card.ImagePath.Trim();
            if (text is null && img is null)
                return new Error("ArticleLink.EmptyContent", $"Link card '{normRes.Value}' must contain text, an image, or both.", ErrorType.Validation);

            validated.Add(new ArticleLinkCard(
                normRes.Value,
                string.IsNullOrWhiteSpace(card.Title) ? null : card.Title.Trim(),
                text,
                img));
        }

        _linkCards.Clear();
        _linkCards.AddRange(validated);
        return Result.Success();
    }

    public Result<(string CanonicalName, string Value)> SetLayoutOverride(string propertyName, string rawValue)
    {
        var targets = GetLayoutTargets();
        var resolved = LayoutBoundsCatalog.ValidateAndResolveForEntityOverride(targets, propertyName, rawValue);
        if (!resolved.IsSuccess) return resolved.Error!;
        _layoutOverrides[resolved.Value.CanonicalName] = resolved.Value.Value;
        return resolved.Value;
    }

    private IReadOnlyList<string> GetLayoutTargets() =>
        Kind == ComponentKind.Article
            ? ["article"]
            : Quiz?.Type switch
            {
                QuizType.Flashcard => ["flashcard"],
                QuizType.Mixed => ["mcq", "flashcard"],
                _ => ["mcq"]
            };

    void IIndexedEntity.SetIndexInternal(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        Index = index;
    }
}
