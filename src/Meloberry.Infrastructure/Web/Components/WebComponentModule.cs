namespace Meloberry.Infrastructure.Web.Components;

internal sealed record WebComponentModule(
    string Name,
    string OutputSubDir,
    Func<WebAppFeatureSet, bool> IsNeeded,
    string? Css = null,
    string? Js = null);
