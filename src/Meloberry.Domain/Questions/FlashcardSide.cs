namespace Meloberry.Domain.Questions;

public sealed class FlashcardSide
{
    private FlashcardSide(string? text, string? imagePath, MediaPosition position, bool isPositionExplicit)
    {
        Text = text;
        ImagePath = imagePath;
        Position = position;
        IsPositionExplicit = isPositionExplicit;
    }

    public string? Text { get; private set; }
    public string? ImagePath { get; private set; }
    public MediaPosition Position { get; private set; }
    public bool IsPositionExplicit { get; private set; }

    public static FlashcardSide Empty() => new(null, null, MediaPosition.TextBelowImage, false);

    public static FlashcardSide Rehydrate(string? text, string? imagePath, MediaPosition position, bool isPositionExplicit = false) =>
        new(text, imagePath, position, isPositionExplicit);

    internal void SetText(string text) => Text = text;
    internal void ClearText() => Text = null;
    internal void SetImage(string imagePath) => ImagePath = imagePath;
    internal void ClearImage() => ImagePath = null;

    internal void SetPosition(MediaPosition position)
    {
        Position = position;
        IsPositionExplicit = true;
    }

    internal void SwitchPosition() =>
        SetPosition(Position == MediaPosition.TextBelowImage ? MediaPosition.TextAboveImage : MediaPosition.TextBelowImage);
}
