using Meloberry.Infrastructure.Services;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class MonotonicIdGeneratorTests
{
    [Fact]
    public void RapidSequentialGeneration_ProducesZeroCollisionsAndExpectedPrefixes()
    {
        var generator = new MonotonicTimestampIdGenerator();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 200; i++)
        {
            var sId = generator.NewSessionId();
            var cId = generator.NewComponentId();
            var aId = generator.NewArticleBlockId();
            var qzId = generator.NewQuizId();
            var qId = generator.NewQuestionId();

            Assert.StartsWith("Session_", sId);
            Assert.StartsWith("Component_", cId);
            Assert.StartsWith("ArtComp_", aId);
            Assert.StartsWith("Set_", qzId);
            Assert.StartsWith("Q_", qId);

            Assert.True(ids.Add(sId));
            Assert.True(ids.Add(cId));
            Assert.True(ids.Add(aId));
            Assert.True(ids.Add(qzId));
            Assert.True(ids.Add(qId));
        }

        Assert.Equal(1000, ids.Count);
    }
}
