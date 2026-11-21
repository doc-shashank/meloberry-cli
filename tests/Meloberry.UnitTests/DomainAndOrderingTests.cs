using Meloberry.Domain.Common;
using Meloberry.Domain.Layouts;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class DomainAndOrderingTests
{
    [Fact]
    public void QuizQuestionReordering_SupportsExchangeShiftUpShiftDownInsertAndDelete()
    {
        var quiz = Quiz.Create("Set_1", "mcq", "C:\\temp\\Set_1").Value;
        for (int i = 1; i <= 6; i++)
            Assert.True(quiz.AddQuestion($"Q_{i}", QuestionType.Mcq).IsSuccess);

        // Shift forward: 2 -> 6 shifts 3,4,5,6 up to 2,3,4,5
        Assert.True(quiz.ReorderQuestion("Q_2", 6, ReorderMode.Shift).IsSuccess);
        Assert.Equal(["Q_1", "Q_3", "Q_4", "Q_5", "Q_6", "Q_2"], quiz.Questions.Select(q => q.Id).ToArray());

        // Shift backward: Q_2 (now at 6) -> 1 shifts items 1..5 down to 2..6
        Assert.True(quiz.ReorderQuestion("Q_2", 1, ReorderMode.Shift).IsSuccess);
        Assert.Equal(["Q_2", "Q_1", "Q_3", "Q_4", "Q_5", "Q_6"], quiz.Questions.Select(q => q.Id).ToArray());

        // Exchange: Q_2 (index 1) <-> Q_4 (index 4)
        Assert.True(quiz.ReorderQuestion("Q_2", 4, ReorderMode.Exchange).IsSuccess);
        Assert.Equal(["Q_4", "Q_1", "Q_3", "Q_2", "Q_5", "Q_6"], quiz.Questions.Select(q => q.Id).ToArray());

        // Insert at occupied index 2 -> shifts 2..6 down (n+1)
        Assert.True(quiz.AddQuestion("Q_NEW", QuestionType.Mcq, 2).IsSuccess);
        Assert.Equal(["Q_4", "Q_NEW", "Q_1", "Q_3", "Q_2", "Q_5", "Q_6"], quiz.Questions.Select(q => q.Id).ToArray());

        // Delete middle item Q_1 (at index 3) -> shifts subsequent items up (n-1)
        Assert.True(quiz.DeleteQuestion("Q_1").IsSuccess);
        Assert.Equal(["Q_4", "Q_NEW", "Q_3", "Q_2", "Q_5", "Q_6"], quiz.Questions.Select(q => q.Id).ToArray());
        Assert.Equal([1, 2, 3, 4, 5, 6], quiz.Questions.Select(q => q.Index).ToArray());
    }

    [Fact]
    public void OutOfIndexBounds_ReturnedForComponentsQuestionsBlocksAndOptions()
    {
        var session = StudySession.Create("Session_1", "C:\\temp\\Session_1").Value;
        Assert.Equal("Index.OutOfIndexBounds", session.AddComponent("Component_1", "article", 2).Error!.Code);

        var comp = session.AddComponent("Component_1", "article").Value;
        Assert.Equal("Index.OutOfIndexBounds", comp.AddTextBlock("ArtComp_1", "Hello", 0).Error!.Code);
        Assert.Equal("Index.OutOfIndexBounds", comp.AddTextBlock("ArtComp_1", "Hello", 2).Error!.Code);

        var mcqComp = session.AddComponent("Component_2", "mcq").Value;
        var q = (McqQuestion)mcqComp.Quiz!.AddQuestion("Q_1", QuestionType.Mcq).Value;
        Assert.Equal(4, q.MaxOptions);
        Assert.Equal("Index.OutOfIndexBounds", q.SetOption(5, "Out of bounds", null).Error!.Code);
    }

    [Fact]
    public void McqRules_Default4Options_ErrorOnShrink_AutoUnmarkPreviousOnSingleCorrect()
    {
        var q = McqQuestion.CreateNew("Q_1", "Set_1", 1);
        Assert.Equal(4, q.MaxOptions);

        Assert.True(q.SetOption(1, "A", null).IsSuccess);
        Assert.True(q.SetOption(2, "B", null).IsSuccess);
        Assert.True(q.SetOption(3, "C", null).IsSuccess);

        // Decreasing max options below populated option 3 must error (Conflict)
        var shrinkFail = q.SetMaxOptions(2);
        Assert.False(shrinkFail.IsSuccess);
        Assert.Equal(ErrorType.Conflict, shrinkFail.Error!.Type);

        // Single-correct markcorrect replaces previous selection
        Assert.True(q.MarkCorrect(1).IsSuccess);
        Assert.True(q.Options.Single(o => o.Index == 1).IsCorrect);

        Assert.True(q.MarkCorrect(2).IsSuccess);
        Assert.False(q.Options.Single(o => o.Index == 1).IsCorrect);
        Assert.True(q.Options.Single(o => o.Index == 2).IsCorrect);

        // tomultiple enables multiple correct answers
        q.ConvertToMultiple();
        Assert.True(q.MarkCorrect(1).IsSuccess);
        Assert.True(q.Options.Single(o => o.Index == 1).IsCorrect);
        Assert.True(q.Options.Single(o => o.Index == 2).IsCorrect);

        // Reverting to single while 2 correct fails
        Assert.Equal(ErrorType.Conflict, q.ConvertToSingle().Error!.Type);
    }

    [Fact]
    public void FlashcardAndLayoutBounds_EnforceDefaultsAndAllowedRanges()
    {
        var card = FlashcardQuestion.CreateNew("Q_1", "Set_1", 1);
        Assert.Equal(MediaPosition.TextBelowImage, card.Front.Position);
        Assert.Equal(MediaPosition.TextBelowImage, card.Back.Position);

        Assert.Equal(MediaPosition.TextAboveImage, card.SwitchPosition(FlashcardFace.Front));
        Assert.Equal(MediaPosition.TextBelowImage, card.Back.Position);

        var profile = LayoutProfile.CreateDefault("mcq").Value;
        Assert.Equal("840", profile.GetValue("cardMaxWidthPx"));
        Assert.Equal("Layout.OutOfBounds", profile.SetProperty("cardMaxWidthPx", "5000").Error!.Code);
        Assert.Equal("Layout.OutOfBounds", profile.SetProperty("optionColumns", "3").Error!.Code);
        Assert.Equal("2", profile.SetProperty("optionColumns", "2").Value);
    }
}
