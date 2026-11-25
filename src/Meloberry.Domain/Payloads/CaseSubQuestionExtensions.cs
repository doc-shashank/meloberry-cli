using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;

namespace Meloberry.Domain.Payloads;

public static class CaseSubQuestionExtensions
{
    public static Question RehydrateToDomainQuestion(this CaseSubQuestion sub, QuestionType subType, string subId, string containerId) =>
        subType switch
        {
            QuestionType.Mcq when sub.Mcq is not null => McqQuestion.Rehydrate(
                subId,
                containerId,
                sub.Index + 1,
                sub.Mcq.Text ?? string.Empty,
                Math.Max(2, sub.Mcq.MaxOptions),
                sub.Mcq.IsMultiCorrect,
                (sub.Mcq.Options ?? []).Select(o => McqOption.Create(o.Index, o.Text, o.ImagePath, o.IsCorrect)),
                imagePath: sub.Mcq.ImagePath,
                explanation: sub.Mcq.Explanation),
            QuestionType.Flashcard when sub.Flashcard is not null => FlashcardQuestion.Rehydrate(
                subId,
                containerId,
                sub.Index + 1,
                FlashcardSide.Rehydrate(sub.Flashcard.Front.Text, sub.Flashcard.Front.ImagePath,
                    string.Equals(sub.Flashcard.Front.Position, "aboveImage", StringComparison.OrdinalIgnoreCase)
                        ? MediaPosition.TextAboveImage : MediaPosition.TextBelowImage),
                FlashcardSide.Rehydrate(sub.Flashcard.Back.Text, sub.Flashcard.Back.ImagePath,
                    string.Equals(sub.Flashcard.Back.Position, "aboveImage", StringComparison.OrdinalIgnoreCase)
                        ? MediaPosition.TextAboveImage : MediaPosition.TextBelowImage)),
            _ => PayloadQuestion.Rehydrate(subId, containerId, sub.Index + 1, subType, sub.PayloadJson ?? "{}")
        };
}
