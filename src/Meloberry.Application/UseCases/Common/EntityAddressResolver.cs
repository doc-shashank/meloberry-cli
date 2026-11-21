using System.Text.RegularExpressions;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;

namespace Meloberry.Application.UseCases.Common;

public sealed record ResolvedEntity(
    string EntityKind,
    string EntityId,
    string WorkspaceId,
    string StorageDirectory,
    StudySession? Session = null,
    SessionComponent? Component = null,
    ArticleBlock? ArticleBlock = null,
    Quiz? Quiz = null,
    Question? Question = null);

public sealed partial class EntityAddressResolver(IWorkspaceRepository repository)
{
    [GeneratedRegex(@"^c(?<c>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComponentPathRegex();

    [GeneratedRegex(@"^c(?<c>\d+)\.b(?<b>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BlockPathRegex();

    [GeneratedRegex(@"^c(?<c>\d+)\.q(?<q>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComponentQuestionPathRegex();

    [GeneratedRegex(@"^q(?<q>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex QuizQuestionPathRegex();

    public async Task<Result<ResolvedEntity>> ResolveAsync(string address, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return AddressErrors.InvalidAddress(address);

        var trimmed = address.Trim();
        int hashIndex = trimmed.IndexOf('#');
        if (hashIndex < 0)
            return await ResolveDirectIdAsync(trimmed, cancellationToken);

        var wsId = trimmed[..hashIndex].Trim();
        var subPath = trimmed[(hashIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(wsId) || string.IsNullOrWhiteSpace(subPath))
            return AddressErrors.InvalidAddress(address);

        if (wsId.StartsWith("Session_", StringComparison.Ordinal))
        {
            var sessionLookup = await repository.FindSessionAsync(wsId, cancellationToken);
            if (sessionLookup is null) return SessionErrors.NotFound(wsId);
            return ResolveSessionSubPath(sessionLookup.Session, sessionLookup.StorageDirectory, subPath, address);
        }

        if (wsId.StartsWith("Set_", StringComparison.Ordinal))
        {
            var quizLookup = await repository.FindQuizContainerAsync(wsId, cancellationToken);
            if (quizLookup is null || quizLookup.Session is not null) return QuizErrors.NotFound(wsId);
            return ResolveQuizSubPath(quizLookup.Quiz, quizLookup.StorageDirectory, subPath, address);
        }

        return AddressErrors.InvalidAddress(address);
    }

    private async Task<Result<ResolvedEntity>> ResolveDirectIdAsync(string id, CancellationToken cancellationToken)
    {
        if (id.StartsWith("Session_", StringComparison.Ordinal))
        {
            var s = await repository.FindSessionAsync(id, cancellationToken);
            return s is null
                ? SessionErrors.NotFound(id)
                : new ResolvedEntity("session", s.Session.Id, s.Session.Id, s.StorageDirectory, Session: s.Session);
        }

        if (id.StartsWith("Set_", StringComparison.Ordinal))
        {
            var q = await repository.FindQuizContainerAsync(id, cancellationToken);
            return q is null || q.Session is not null
                ? QuizErrors.NotFound(id)
                : new ResolvedEntity("quiz", q.Quiz.Id, q.Quiz.Id, q.StorageDirectory, Quiz: q.Quiz);
        }

        if (id.StartsWith("Component_", StringComparison.Ordinal))
        {
            var c = await repository.FindComponentAsync(id, cancellationToken);
            return c is null
                ? ComponentErrors.NotFound(id)
                : new ResolvedEntity("component", c.Component.Id, c.Session.Id, c.StorageDirectory, Session: c.Session, Component: c.Component, Quiz: c.Component.Quiz);
        }

        if (id.StartsWith("ArtComp_", StringComparison.Ordinal))
        {
            var b = await repository.FindArticleBlockAsync(id, cancellationToken);
            return b is null
                ? ArticleErrors.BlockNotFound(id)
                : new ResolvedEntity("articleBlock", b.Block.Id, b.Session.Id, b.StorageDirectory, Session: b.Session, Component: b.Component, ArticleBlock: b.Block);
        }

        if (id.StartsWith("Q_", StringComparison.Ordinal))
        {
            var q = await repository.FindQuestionOwnerAsync(id, cancellationToken);
            if (q is null) return QuestionErrors.NotFound(id);
            var wsId = q.Session?.Id ?? q.Quiz.Id;
            return new ResolvedEntity("question", q.Question.Id, wsId, q.StorageDirectory, Session: q.Session, Component: q.Component, Quiz: q.Quiz, Question: q.Question);
        }

        return AddressErrors.NotFound(id);
    }

    private static Result<ResolvedEntity> ResolveSessionSubPath(StudySession session, string storageDir, string subPath, string fullAddress)
    {
        if (ComponentPathRegex().Match(subPath) is { Success: true } compMatch &&
            int.TryParse(compMatch.Groups["c"].Value, out int compIdx))
        {
            var comp = session.Components.FirstOrDefault(c => c.Index == compIdx);
            return comp is null
                ? AddressErrors.NotFound(fullAddress)
                : new ResolvedEntity("component", comp.Id, session.Id, storageDir, Session: session, Component: comp, Quiz: comp.Quiz);
        }

        if (BlockPathRegex().Match(subPath) is { Success: true } blockMatch &&
            int.TryParse(blockMatch.Groups["c"].Value, out int bcIdx) &&
            int.TryParse(blockMatch.Groups["b"].Value, out int bIdx))
        {
            var comp = session.Components.FirstOrDefault(c => c.Index == bcIdx);
            if (comp is null || comp.Kind != ComponentKind.Article) return AddressErrors.NotFound(fullAddress);
            var block = comp.ArticleBlocks.FirstOrDefault(b => b.Index == bIdx);
            return block is null
                ? AddressErrors.NotFound(fullAddress)
                : new ResolvedEntity("articleBlock", block.Id, session.Id, storageDir, Session: session, Component: comp, ArticleBlock: block);
        }

        if (ComponentQuestionPathRegex().Match(subPath) is { Success: true } qMatch &&
            int.TryParse(qMatch.Groups["c"].Value, out int qcIdx) &&
            int.TryParse(qMatch.Groups["q"].Value, out int qIdx))
        {
            var comp = session.Components.FirstOrDefault(c => c.Index == qcIdx);
            if (comp is null || comp.Kind != ComponentKind.Quiz || comp.Quiz is null) return AddressErrors.NotFound(fullAddress);
            var question = comp.Quiz.Questions.FirstOrDefault(q => q.Index == qIdx);
            return question is null
                ? AddressErrors.NotFound(fullAddress)
                : new ResolvedEntity("question", question.Id, session.Id, storageDir, Session: session, Component: comp, Quiz: comp.Quiz, Question: question);
        }

        // Ref slug lookup across session components, blocks, and questions
        var refNorm = subPath.ToLowerInvariant();
        foreach (var comp in session.Components)
        {
            if (string.Equals(comp.RefKey, refNorm, StringComparison.Ordinal))
                return new ResolvedEntity("component", comp.Id, session.Id, storageDir, Session: session, Component: comp, Quiz: comp.Quiz);

            foreach (var block in comp.ArticleBlocks)
            {
                if (string.Equals(block.RefKey, refNorm, StringComparison.Ordinal))
                    return new ResolvedEntity("articleBlock", block.Id, session.Id, storageDir, Session: session, Component: comp, ArticleBlock: block);
            }

            if (comp.Quiz is not null)
            {
                foreach (var question in comp.Quiz.Questions)
                {
                    if (string.Equals(question.RefKey, refNorm, StringComparison.Ordinal))
                        return new ResolvedEntity("question", question.Id, session.Id, storageDir, Session: session, Component: comp, Quiz: comp.Quiz, Question: question);
                }
            }
        }

        return AddressErrors.NotFound(fullAddress);
    }

    private static Result<ResolvedEntity> ResolveQuizSubPath(Quiz quiz, string storageDir, string subPath, string fullAddress)
    {
        if (QuizQuestionPathRegex().Match(subPath) is { Success: true } qMatch &&
            int.TryParse(qMatch.Groups["q"].Value, out int qIdx))
        {
            var question = quiz.Questions.FirstOrDefault(q => q.Index == qIdx);
            return question is null
                ? AddressErrors.NotFound(fullAddress)
                : new ResolvedEntity("question", question.Id, quiz.Id, storageDir, Quiz: quiz, Question: question);
        }

        var refNorm = subPath.ToLowerInvariant();
        var byRef = quiz.Questions.FirstOrDefault(q => string.Equals(q.RefKey, refNorm, StringComparison.Ordinal));
        return byRef is null
            ? AddressErrors.NotFound(fullAddress)
            : new ResolvedEntity("question", byRef.Id, quiz.Id, storageDir, Quiz: quiz, Question: byRef);
    }
}
