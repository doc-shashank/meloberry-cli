namespace Meloberry.Domain.Questions;

public sealed class McqOption
{
    private McqOption(int index, string? text, string? imagePath, bool isCorrect)
    {
        Index = index;
        Text = text;
        ImagePath = imagePath;
        IsCorrect = isCorrect;
    }

    public int Index { get; private set; }
    public string? Text { get; private set; }
    public string? ImagePath { get; private set; }
    public bool IsCorrect { get; private set; }

    public static McqOption Create(int index, string? text, string? imagePath, bool isCorrect = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        return new McqOption(index, text, imagePath, isCorrect);
    }

    internal void UpdateContent(string? text, string? imagePath) { Text = text; ImagePath = imagePath; }
    internal void SetCorrect(bool isCorrect) => IsCorrect = isCorrect;
}
