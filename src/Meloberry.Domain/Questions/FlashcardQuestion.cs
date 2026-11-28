using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;

namespace Meloberry.Domain.Questions;

public sealed class FlashcardQuestion : Question
{
    private FlashcardQuestion(
        string id,
        string containerId,
        int index,
        FlashcardSide front,
        FlashcardSide back,
        Dictionary<string, string>? layoutOverrides,
        string? refKey = null)
        : base(id, containerId, index, QuestionType.Flashcard, layoutOverrides, refKey)
    {
        Front = front;
        Back = back;
    }

    public FlashcardSide Front { get; private set; }
    public FlashcardSide Back { get; private set; }

    public static FlashcardQuestion CreateNew(string id, string containerId, int index) =>
        new(id, containerId, index, FlashcardSide.Empty(), FlashcardSide.Empty(), null);

    public static FlashcardQuestion Rehydrate(
        string id,
        string containerId,
        int index,
        FlashcardSide front,
        FlashcardSide back,
        Dictionary<string, string>? layoutOverrides = null,
        string? refKey = null) =>
        new(id, containerId, index, front, back, layoutOverrides, refKey);

    public Result SetSideText(FlashcardFace face, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return QuestionErrors.EmptyText();
        GetSide(face).SetText(text);
        return Result.Success();
    }

    public void ClearSideText(FlashcardFace face) => GetSide(face).ClearText();

    public Result SetSideImage(FlashcardFace face, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return QuestionErrors.EmptyImagePath();
        GetSide(face).SetImage(imagePath);
        return Result.Success();
    }

    public void ClearSideImage(FlashcardFace face) => GetSide(face).ClearImage();

    public MediaPosition SetSidePosition(FlashcardFace face, MediaPosition position)
    {
        var side = GetSide(face);
        side.SetPosition(position);
        return side.Position;
    }

    public MediaPosition SwitchPosition(FlashcardFace face)
    {
        var side = GetSide(face);
        side.SwitchPosition();
        return side.Position;
    }

    public override Question CloneWithNewId(string newQuestionId, string targetContainerId, string? newRefKey = null) =>
        Rehydrate(
            newQuestionId,
            targetContainerId,
            1,
            FlashcardSide.Rehydrate(Front.Text, Front.ImagePath, Front.Position, Front.IsPositionExplicit),
            FlashcardSide.Rehydrate(Back.Text, Back.ImagePath, Back.Position, Back.IsPositionExplicit),
            CloneLayoutOverrides(),
            newRefKey);

    private FlashcardSide GetSide(FlashcardFace face) => face == FlashcardFace.Front ? Front : Back;
}
