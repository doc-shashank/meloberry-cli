using Meloberry.Infrastructure.Web.Components.AuxiliarySpecs;
using Meloberry.Infrastructure.Web.Components.ContentSpecs;

namespace Meloberry.Infrastructure.Web.Components;

internal static class WebComponentCatalog
{
    public static IReadOnlyList<WebComponentModule> AllModules { get; } =
    [
        .. AuxiliarySpecsCatalog.Modules,
        .. BlockSpecs.Modules,
        .. QuestionSpecsSet1.Modules,
        .. QuestionSpecsSet2.Modules,
        .. QuestionSpecsSet3.Modules
    ];

    public static IReadOnlyList<WebComponentModule> ResolveNeeded(WebAppFeatureSet features) =>
        AllModules.Where(m => m.IsNeeded(features)).ToList();

    public static string BuildCombinedCss(WebAppFeatureSet features) =>
        string.Join("\n", AllModules
            .Where(m => m.IsNeeded(features) && !string.IsNullOrWhiteSpace(m.Css))
            .Select(m => m.Css!.TrimEnd()));
}
