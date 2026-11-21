using Meloberry.Application;
using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Json;
using Meloberry.Domain.Common;
using Meloberry.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class AddressAndPatchTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_addr_patch_{Guid.NewGuid():N}");

    public AddressAndPatchTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Theory]
    [InlineData("intro", true, "intro")]
    [InlineData("  Kinematics-01_a  ", true, "kinematics-01_a")]
    [InlineData("-bad", false, null)]
    [InlineData("has space", false, null)]
    [InlineData("a/b", false, null)]
    public void RefKeyValidator_EnforcesSlugGrammar(string input, bool expectedOk, string? expectedNormalized)
    {
        var res = RefKeyValidator.ValidateAndNormalize(input);
        Assert.Equal(expectedOk, res.IsSuccess);
        if (expectedOk)
            Assert.Equal(expectedNormalized, res.Value);
    }

    [Fact]
    public async Task EntityAddressResolver_And_PatchEntityHandler_ResolvePathsRefsAndReorderCleanly()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(_tempRoot, "appdata"));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var apply = scope.ServiceProvider.GetRequiredService<ApplySpecHandler>();
        var resolver = scope.ServiceProvider.GetRequiredService<EntityAddressResolver>();
        var patch = scope.ServiceProvider.GetRequiredService<PatchEntityHandler>();

        var spec = """
        {
          "kind": "session",
          "name": "Initial",
          "components": [
            {
              "kind": "article",
              "ref": "intro",
              "title": "Section 1",
              "blocks": [
                { "kind": "text", "ref": "b-one", "text": "First block" },
                { "kind": "text", "text": "Second block" }
              ]
            },
            {
              "kind": "quiz",
              "ref": "check",
              "quizType": "mixed",
              "questions": [
                { "type": "mcq", "ref": "q-force", "text": "Old prompt?", "options": [{ "text": "A", "isCorrect": true }, { "text": "B" }] },
                { "type": "flashcard", "front": { "text": "F1" }, "back": { "text": "B1" } }
              ]
            }
          ]
        }
        """;

        var applied = (await apply.HandleAsync(new ApplySpecCommand(null, null, spec, _tempRoot))).Value;
        var wsId = applied.TargetId;

        // Positional paths and #ref slugs resolve to exact IDs
        var c1 = (await resolver.ResolveAsync($"{wsId}#c1")).Value;
        var c1Ref = (await resolver.ResolveAsync($"{wsId}#intro")).Value;
        Assert.Equal(c1.EntityId, c1Ref.EntityId);

        var b1 = (await resolver.ResolveAsync($"{wsId}#c1.b1")).Value;
        var b1Ref = (await resolver.ResolveAsync($"{wsId}#b-one")).Value;
        Assert.Equal(b1.EntityId, b1Ref.EntityId);

        var q1 = (await resolver.ResolveAsync($"{wsId}#c2.q1")).Value;
        var q1Ref = (await resolver.ResolveAsync($"{wsId}#q-force")).Value;
        Assert.Equal(q1.EntityId, q1Ref.EntityId);

        // Duplicate ref in same workspace is rejected
        var dupRes = await patch.HandleAsync(new PatchEntityCommand($"{wsId}#c1.b2", SetPairs: ["ref=b-one"]));
        Assert.False(dupRes.IsSuccess);
        Assert.Equal("Address.DuplicateRef", dupRes.Error!.Code);

        // Patch MCQ text + explanation + layoutOverrides, and move question 1 -> 2
        var patchRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{wsId}#q-force",
            InlineJson: """{"text":"Updated prompt?","explanation":"Newton","layoutOverrides":{"optionColumns":"2"}}""",
            MoveSpec: "2:shift"));
        Assert.True(patchRes.IsSuccess);
        Assert.Equal(2, patchRes.Value.NewIndex);

        var movedTo2 = (await resolver.ResolveAsync($"{wsId}#c2.q2")).Value;
        Assert.Equal(q1.EntityId, movedTo2.EntityId);

        // Unknown field returns typo hint
        var unknownRes = await patch.HandleAsync(new PatchEntityCommand(
            $"{wsId}#c2.q2",
            InlineJson: """{"explanaton":"typo"}"""));
        Assert.False(unknownRes.IsSuccess);
        Assert.Equal("Patch.UnknownField", unknownRes.Error!.Code);
        Assert.Equal("Did you mean 'explanation'?", unknownRes.Error.Hint);
    }
}
