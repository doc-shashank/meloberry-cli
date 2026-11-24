using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;

namespace Meloberry.Domain.Sessions;

public enum ArticleBlockKind { Text, Image, Callout, Table }

public enum CalloutTone { Pearl, Clinical, Warning }

public sealed class ArticleBlock : IIndexedEntity
{
    private ArticleBlock(
        string id,
        string componentId,
        int index,
        ArticleBlockKind kind,
        string? textContent,
        string? imagePath,
        string? refKey = null,
        string? caption = null,
        string? alt = null,
        CalloutTone? calloutTone = null,
        string? calloutTitle = null,
        string? payloadJson = null)
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
        Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        Alt = string.IsNullOrWhiteSpace(alt) ? null : alt.Trim();
        CalloutTone = calloutTone;
        CalloutTitle = string.IsNullOrWhiteSpace(calloutTitle) ? null : calloutTitle.Trim();
        PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? null : payloadJson.Trim();
    }

    public string Id { get; private set; }
    public string ComponentId { get; private set; }
    public int Index { get; private set; }
    public ArticleBlockKind Kind { get; private set; }
    public string? TextContent { get; private set; }
    public string? ImagePath { get; private set; }
    public string? RefKey { get; private set; }
    public string? Caption { get; private set; }
    public string? Alt { get; private set; }
    public CalloutTone? CalloutTone { get; private set; }
    public string? CalloutTitle { get; private set; }
    public string? PayloadJson { get; private set; }

    public static bool TryParseCalloutTone(string? raw, out CalloutTone tone)
    {
        tone = Sessions.CalloutTone.Pearl;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "pearl":
                tone = Sessions.CalloutTone.Pearl;
                return true;
            case "clinical":
                tone = Sessions.CalloutTone.Clinical;
                return true;
            case "warning":
                tone = Sessions.CalloutTone.Warning;
                return true;
            default:
                return false;
        }
    }

    public static string CalloutToneToString(CalloutTone tone) => tone switch
    {
        Sessions.CalloutTone.Clinical => "clinical",
        Sessions.CalloutTone.Warning => "warning",
        _ => "pearl"
    };

    public static Result<ArticleBlock> CreateText(string id, string componentId, string textContent) =>
        string.IsNullOrWhiteSpace(textContent)
            ? ArticleErrors.EmptyContent()
            : new ArticleBlock(id, componentId, 1, ArticleBlockKind.Text, textContent, null);

    public static ArticleBlock CreateImage(string id, string componentId, string imagePath, string? caption = null, string? alt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        return new(id, componentId, 1, ArticleBlockKind.Image, null, imagePath, caption: caption, alt: alt);
    }

    public static Result<ArticleBlock> CreateCallout(string id, string componentId, CalloutTone tone, string textContent, string? title = null) =>
        string.IsNullOrWhiteSpace(textContent)
            ? ArticleErrors.EmptyCalloutContent()
            : new ArticleBlock(id, componentId, 1, ArticleBlockKind.Callout, textContent, null, calloutTone: tone, calloutTitle: title);

    public static Result<ArticleBlock> CreateTable(string id, string componentId, string payloadJson, string? title = null) =>
        string.IsNullOrWhiteSpace(payloadJson)
            ? ArticleErrors.InvalidTablePayload("Table payload JSON cannot be empty.")
            : new ArticleBlock(id, componentId, 1, ArticleBlockKind.Table, null, null, calloutTitle: title, payloadJson: payloadJson);

    public static ArticleBlock Rehydrate(
        string id,
        string componentId,
        int index,
        ArticleBlockKind kind,
        string? textContent,
        string? imagePath,
        string? refKey = null,
        string? caption = null,
        string? alt = null,
        CalloutTone? calloutTone = null,
        string? calloutTitle = null,
        string? payloadJson = null) =>
        new(id, componentId, index, kind, textContent, imagePath, refKey, caption, alt, calloutTone, calloutTitle, payloadJson);

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
        if (string.IsNullOrWhiteSpace(imagePath)) return ArticleErrors.EmptyImagePath();
        ImagePath = imagePath;
        return Result.Success();
    }

    public Result SetImageCaption(string? caption)
    {
        if (Kind != ArticleBlockKind.Image) return ArticleErrors.WrongBlockKind(Id, "image");
        Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        return Result.Success();
    }

    public Result SetImageAlt(string? alt)
    {
        if (Kind != ArticleBlockKind.Image) return ArticleErrors.WrongBlockKind(Id, "image");
        Alt = string.IsNullOrWhiteSpace(alt) ? null : alt.Trim();
        return Result.Success();
    }

    public Result UpdateCallout(CalloutTone? tone, string? textContent, string? title, bool updateTitle = false)
    {
        if (Kind != ArticleBlockKind.Callout) return ArticleErrors.WrongBlockKind(Id, "callout");
        if (textContent is not null)
        {
            if (string.IsNullOrWhiteSpace(textContent)) return ArticleErrors.EmptyCalloutContent();
            TextContent = textContent;
        }
        if (tone.HasValue) CalloutTone = tone.Value;
        if (updateTitle || title is not null)
            CalloutTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        return Result.Success();
    }

    public Result SetPayloadJson(string? payloadJson)
    {
        PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? null : payloadJson.Trim();
        return Result.Success();
    }

    public Result UpdateTablePayload(string payloadJson, string? title = null, bool updateTitle = false)
    {
        if (Kind != ArticleBlockKind.Table) return ArticleErrors.WrongBlockKind(Id, "table");
        if (string.IsNullOrWhiteSpace(payloadJson)) return ArticleErrors.InvalidTablePayload("Table payload JSON cannot be empty.");
        PayloadJson = payloadJson.Trim();
        if (updateTitle || title is not null)
            CalloutTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        return Result.Success();
    }

    void IIndexedEntity.SetIndexInternal(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        Index = index;
    }
}
