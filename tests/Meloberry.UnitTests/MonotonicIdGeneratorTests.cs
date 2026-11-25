using Meloberry.Infrastructure;
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

    [Fact]
    public async Task ConcurrentMultiInstanceGeneration_WithSharedAppDataLease_ProducesZeroCollisions()
    {
        var appDataDir = Path.Combine(Path.GetTempPath(), $"mby_lease_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDataDir);
        try
        {
            var opts = new MeloberryStorageOptions { AppDataDirectory = appDataDir };
            var generators = Enumerable.Range(0, 4)
                .Select(_ => new MonotonicTimestampIdGenerator(opts))
                .ToArray();

            var tasks = generators.Select(gen => Task.Run(() =>
            {
                var local = new List<string>(150);
                for (int i = 0; i < 150; i++)
                    local.Add(gen.NewQuestionId());
                return local;
            })).ToArray();

            var results = await Task.WhenAll(tasks);
            var allIds = results.SelectMany(x => x).ToList();
            Assert.Equal(600, allIds.Count);
            Assert.Equal(600, allIds.Distinct(StringComparer.Ordinal).Count());
            Assert.True(File.Exists(Path.Combine(appDataDir, ".mby_id_clock")));
        }
        finally
        {
            if (Directory.Exists(appDataDir))
                Directory.Delete(appDataDir, recursive: true);
        }
    }
}
