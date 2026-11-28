using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;

namespace Meloberry.Domain.Common;

/// <summary>Collects all referenced relative asset paths across a session or standalone quiz.</summary>
public static class WorkspaceAssetCollector
{
    public static List<string> CollectSessionAssets(StudySession session)
    {
        var assets = new List<string>();
        foreach (var comp in session.Components)
        {
            if (comp.Kind == ComponentKind.Article)
            {
                List<string>? diagPayloadPaths = null;
                foreach (var b in comp.ArticleBlocks)
                {
                    if (b.Kind is ArticleBlockKind.Image or ArticleBlockKind.DiagramExplore && !string.IsNullOrWhiteSpace(b.ImagePath))
                        assets.Add(b.ImagePath);
                    if (b.Kind == ArticleBlockKind.DiagramExplore &&
                        Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(b.PayloadJson, out var dp) &&
                        !string.IsNullOrWhiteSpace(dp?.ImagePath))
                    {
                        (diagPayloadPaths ??= []).Add(dp.ImagePath);
                    }
                }
                if (diagPayloadPaths is not null)
                    assets.AddRange(diagPayloadPaths);
                foreach (var l in comp.LinkCards)
                {
                    if (!string.IsNullOrWhiteSpace(l.ImagePath))
                        assets.Add(l.ImagePath);
                }
            }
            else if (comp.Quiz is not null)
            {
                assets.AddRange(CollectQuizAssets(comp.Quiz));
            }
        }
        return assets;
    }

    public static List<string> CollectQuizAssets(Quiz quiz)
    {
        var assets = new List<string>();
        foreach (var q in quiz.Questions)
            CollectQuestionAssets(q, assets);
        return assets;
    }

    private static void CollectQuestionAssets(Question q, List<string> assets)
    {
        if (q is McqQuestion mcq)
        {
            if (!string.IsNullOrWhiteSpace(mcq.ImagePath)) assets.Add(mcq.ImagePath);
            assets.AddRange(mcq.Options.Where(o => !string.IsNullOrWhiteSpace(o.ImagePath)).Select(o => o.ImagePath!));
            return;
        }
        if (q is FlashcardQuestion card)
        {
            if (!string.IsNullOrWhiteSpace(card.Front.ImagePath)) assets.Add(card.Front.ImagePath);
            if (!string.IsNullOrWhiteSpace(card.Back.ImagePath)) assets.Add(card.Back.ImagePath);
            return;
        }
        if (q is not PayloadQuestion pq)
            return;

        switch (pq.Type)
        {
            case QuestionType.RelabelImage when Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(pq.PayloadJson, out var rel) && !string.IsNullOrWhiteSpace(rel?.ImagePath):
                assets.Add(rel.ImagePath);
                break;
            case QuestionType.SpotError when Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(pq.PayloadJson, out var se) && !string.IsNullOrWhiteSpace(se?.ImagePath):
                assets.Add(se.ImagePath);
                break;
            case QuestionType.Numeric when Phase1PayloadCodec.TryDeserialize<NumericPayload>(pq.PayloadJson, out var numP) && !string.IsNullOrWhiteSpace(numP?.ImagePath):
                assets.Add(numP.ImagePath);
                break;
            case QuestionType.CaseGroup when Phase1PayloadCodec.TryDeserialize<CaseGroupPayload>(pq.PayloadJson, out var caseP) && caseP is not null:
                if (!string.IsNullOrWhiteSpace(caseP.ImagePath))
                    assets.Add(caseP.ImagePath);
                if (caseP.Questions is not null)
                {
                    foreach (var sub in caseP.Questions)
                    {
                        if (sub is not null && Quiz.TryParseQuestionType(sub.Type, out var subType) && subType != QuestionType.CaseGroup)
                            CollectQuestionAssets(sub.RehydrateToDomainQuestion(subType, $"{q.Id}:sub-{sub.Index}", q.ContainerId), assets);
                    }
                }
                break;
        }
    }
}
