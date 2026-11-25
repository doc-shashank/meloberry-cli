using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Layouts;
using Meloberry.Domain.Layouts;
using Meloberry.Infrastructure;
using Meloberry.Infrastructure.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.UnitTests;

public sealed class LayoutEditorAndProfilesTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_layout_unit_{Guid.NewGuid():N}");

    public LayoutEditorAndProfilesTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private ServiceProvider CreateProvider(string subDir = "appdata")
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = Path.Combine(_tempRoot, subDir));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SaveLayoutProfilesHandler_IsAtomicOnValidationFailure_AndSyncsGlobalsOnSuccess()
    {
        using var provider = CreateProvider();
        var saveHandler = provider.GetRequiredService<SaveLayoutProfilesHandler>();
        var getBounds = provider.GetRequiredService<GetLayoutBoundsHandler>();

        // Invalid batch (1 valid + 1 out-of-bounds) must leave DB completely unchanged
        var failRes = await saveHandler.HandleAsync(new SaveLayoutProfilesCommand(
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["mcq"] = new Dictionary<string, string>
                {
                    ["optionColumns"] = "2",
                    ["cardMaxWidthPx"] = "99999"
                }
            }));
        Assert.True(failRes.IsSuccess);
        Assert.False(failRes.Value.Saved);
        Assert.NotEmpty(failRes.Value.Errors);

        var afterFail = (await getBounds.HandleAsync(new GetLayoutBoundsQuery("mcq"))).Value;
        Assert.Equal("1", afterFail.Properties.First(p => p.Property == "optionColumns").CurrentValue);

        // Valid batch saves atomically and syncs globals across all 3 profiles
        var okRes = await saveHandler.HandleAsync(new SaveLayoutProfilesCommand(
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["mcq"] = new Dictionary<string, string>
                {
                    ["optionColumns"] = "2",
                    ["themeMode"] = "dark",
                    ["slideTransitionMs"] = "375"
                },
                ["article"] = new Dictionary<string, string>
                {
                    ["blockSpacingPx"] = "28"
                }
            }));
        Assert.True(okRes.IsSuccess);
        Assert.True(okRes.Value.Saved);
        Assert.Empty(okRes.Value.Errors);

        var allAfterOk = (await getBounds.HandleAsync(new GetLayoutBoundsQuery(null))).Value.Properties;
        Assert.Equal("2", allAfterOk.First(p => p.TargetType == "mcq" && p.Property == "optionColumns").CurrentValue);
        Assert.Equal("28", allAfterOk.First(p => p.TargetType == "article" && p.Property == "blockSpacingPx").CurrentValue);
        Assert.All(
            allAfterOk.Where(p => p.Property == "themeMode"),
            p => Assert.Equal("dark", p.CurrentValue));
        Assert.All(
            allAfterOk.Where(p => p.Property == "slideTransitionMs"),
            p => Assert.Equal("375", p.CurrentValue));
    }

    [Fact]
    public async Task ResetLayoutProfileHandler_RejectsEmptyAndUnknownKeys_PreservesGlobalsOnScopedReset_AndResetsAllOnNullKeys()
    {
        using var provider = CreateProvider();
        var setHandler = provider.GetRequiredService<SetLayoutPropertyHandler>();
        var resetHandler = provider.GetRequiredService<ResetLayoutProfileHandler>();
        var getBounds = provider.GetRequiredService<GetLayoutBoundsHandler>();

        Assert.True((await setHandler.HandleAsync(new SetLayoutPropertyCommand("mcq", "optionColumns", "2"))).IsSuccess);
        Assert.True((await setHandler.HandleAsync(new SetLayoutPropertyCommand("mcq", "themeMode", "dark"))).IsSuccess);

        // Explicit empty keys array must fail without resetting anything
        var emptyRes = await resetHandler.HandleAsync(new ResetLayoutProfileCommand("mcq", []));
        Assert.False(emptyRes.IsSuccess);
        Assert.Equal("Layout.EmptyResetKeys", emptyRes.Error!.Code);

        // Unknown key must fail without resetting anything
        var unknownRes = await resetHandler.HandleAsync(new ResetLayoutProfileCommand("mcq", ["notAProperty"]));
        Assert.False(unknownRes.IsSuccess);
        Assert.Equal("Layout.UnknownProperty", unknownRes.Error!.Code);

        var stillSet = (await getBounds.HandleAsync(new GetLayoutBoundsQuery("mcq"))).Value;
        Assert.Equal("2", stillSet.Properties.First(p => p.Property == "optionColumns").CurrentValue);
        Assert.Equal("dark", stillSet.Properties.First(p => p.Property == "themeMode").CurrentValue);

        // Scoped key reset resets only optionColumns and preserves themeMode
        var scopedRes = await resetHandler.HandleAsync(new ResetLayoutProfileCommand("mcq", ["optionColumns"]));
        Assert.True(scopedRes.IsSuccess);
        var afterScoped = (await getBounds.HandleAsync(new GetLayoutBoundsQuery("mcq"))).Value;
        Assert.Equal("1", afterScoped.Properties.First(p => p.Property == "optionColumns").CurrentValue);
        Assert.Equal("dark", afterScoped.Properties.First(p => p.Property == "themeMode").CurrentValue);

        // Omitted (null) keys resets entire profile and syncs globals across all profiles
        var fullRes = await resetHandler.HandleAsync(new ResetLayoutProfileCommand("mcq", null));
        Assert.True(fullRes.IsSuccess);
        var afterFull = (await getBounds.HandleAsync(new GetLayoutBoundsQuery("article"))).Value;
        Assert.Equal("berry", afterFull.Properties.First(p => p.Property == "themeMode").CurrentValue);
    }

    [Fact]
    public void LayoutBoundsCatalog_BlocksEntityGlobalOverrides_AndPrioritizesOutOfBoundsOnMixedTargets()
    {
        var globalOverrideRes = LayoutBoundsCatalog.ValidateAndResolveForEntityOverride("mcq", "themeMode", "dark");
        Assert.False(globalOverrideRes.IsSuccess);
        Assert.Equal("Layout.GlobalPropertyOnEntity", globalOverrideRes.Error!.Code);

        var multiGlobalRes = LayoutBoundsCatalog.ValidateAndResolveForEntityOverride(["mcq", "flashcard"], "slideTransitionMs", "500");
        Assert.False(multiGlobalRes.IsSuccess);
        Assert.Equal("Layout.GlobalPropertyOnEntity", multiGlobalRes.Error!.Code);

        // Mixed quiz validation with out-of-bounds flashcard property should report OutOfBounds rather than UnknownProperty on mcq
        var mixedErrRes = LayoutBoundsCatalog.ValidateAndResolve(["mcq", "flashcard"], "flipDurationMs", "9999");
        Assert.False(mixedErrRes.IsSuccess);
        Assert.Equal("Layout.OutOfBounds", mixedErrRes.Error!.Code);
    }

    [Fact]
    public void SharedStylesAndEditorHtml_ContainCssParityFixes_CancellationHandle_AndAllV2Targets()
    {
        using var provider = CreateProvider();
        var meta = provider.GetRequiredService<ICliToolMetadataProvider>().GetMetadata();
        var builder = provider.GetRequiredService<ILayoutEditorPageBuilder>();
        var html = builder.BuildHtml("relabel", meta, "csrf-sample-123");

        // Shared CSS / JS invariants
        Assert.Contains(".mby-card { width: 100%; max-width: var(--card-max-width, 840px); display: flex; flex-direction: column;", html);
        Assert.Contains(".mby-flashcard-body h1 {", html);
        Assert.Contains(".mby-flashcard-body h3 {", html);
        Assert.Contains("--media-align", html);
        Assert.Contains("align-items: var(--media-align, flex-start);", html);
        Assert.Contains("text-align: var(--content-align, left);", html);
        Assert.Contains("triggerFlip.cancel = () =>", html);
        Assert.DoesNotContain("var(--image-max-height, 420px)", html);
        Assert.Contains("var(--image-max-height, 360px)", html);

        // Editor page invariants
        Assert.Contains("<link rel=\"icon\" href=\"data:,\" />", html);
        Assert.Contains("EDITOR_CSRF_TOKEN = \"csrf-sample-123\"", html);
        Assert.Contains("'X-Mby-Editor-Token': EDITOR_CSRF_TOKEN", html);
        Assert.Contains("id=\"globalSlideRange\"", html);
        Assert.Contains("step=\"1\"", html);
        Assert.Contains("id=\"globalSlideNum\"", html);
        Assert.Contains("id=\"editorZoomModal\"", html);
        Assert.Contains("resolveDeckGlobalValue", html);
        Assert.Contains("hasUnsavedChanges", html);
        Assert.Contains("beforeunload", html);
        Assert.Contains("minmax(220px, 44vh)", html);

        foreach (var targetId in new[]
        {
            "article-text", "callout", "table", "image",
            "mcq-question", "mcq-options", "fill-blank", "assertion-reason",
            "spot-error", "diff", "mechanism", "relabel", "flashcard"
        })
        {
            Assert.Contains($"id: '{targetId}'", html);
        }
    }
}
