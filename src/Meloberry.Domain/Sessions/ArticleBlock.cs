using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;

namespace Meloberry.Domain.Sessions;

public enum ArticleBlockKind { Text, Image }

public sealed class ArticleBlock : IIndexedEntity
{
    private ArticleBlock(string id, string componentId, int index, ArticleBlockKind kind, string? textContent, string? imagePath, string? refKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        Id = id;
        ComponentId = componentId;
        Index = index;
        Kind = kind;
        TextContent = textContent;
        ImagePath = imagePath;
        RefKey = string.IsNullOrWhiteSpace(refKey) ? null : refKey.Trim().ToLowerInvariant();
    }

    public string Id { get; private set; }
    public string ComponentId { get; private set; }
    public int Index { get; private set; }
    public ArticleBlockKind Kind { get; private set; }
    public string? TextContent { get; private set; }
    public string? ImagePath { get; private set; }
    public string? RefKey { get; private set; }

    public static Result<ArticleBlock> CreateText(string id, string componentId, string textContent) =>
        string.IsNullOrWhiteSpace(textContent)
            ? ArticleErrors.EmptyContent()
            : new ArticleBlock(id, componentId, 1, ArticleBlockKind.Text, textContent, null);

    public static ArticleBlock CreateImage(string id, string componentId, string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        return new(id, componentId, 1, ArticleBlockKind.Image, null, imagePath);
    }

    public static ArticleBlock Rehydrate(string id, string componentId, int index, ArticleBlockKind kind, string? textContent, string? imagePath, string? refKey = null) =>
        new(id, componentId, index, kind, textContent, imagePath, refKey);

    public Result SetRefKey(string? refKey)
    {
        var res = RefKeyValidator.ValidateAndNormalize(refKey);
        if (!res.IsSuccess) return res.Error!;
        RefKey = res.Value;
        return Result.Success();
    }

    public Result UpdateText(string textContent)
    {
        if (Kind != ArticleBlockKind.Text) return ArticleErrors.WrongBlockKind(Id, "text");
        if (string.IsNullOrWhiteSpace(textContent)) return ArticleErrors.EmptyContent();
        TextContent = textContent;
        return Result.Success();
    }

    public Result UpdateImage(string imagePath)
    {
        if (Kind != ArticleBlockKind.Image) return ArticleErrors.WrongBlockKind(Id, "image");
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ImagePath = imagePath;
        return Result.Success();
    }

    void IIndexedEntity.SetIndexInternal(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        Index = index;
    }
}
