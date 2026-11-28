using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Json;
using Meloberry.Application.UseCases.Layouts;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Application.UseCases.Quizzes;
using Microsoft.Extensions.DependencyInjection;

namespace Meloberry.Application;

public static class DependencyInjection
{
    private static readonly Type[] HandlerTypes =
    [
        typeof(EntityAddressResolver),
        typeof(DeleteEntityHandler),
        typeof(CloneEntityHandler),
        typeof(AddAssetHandler),
        typeof(ListAssetsHandler),
        typeof(PruneAssetsHandler),
        typeof(ListWorkspacesHandler),
        typeof(ApplySpecHandler),
        typeof(ScaffoldHandler),
        typeof(PatchEntityHandler),
        typeof(BatchOperationsHandler),
        typeof(LintHandler),
        typeof(InspectEntityHandler),
        typeof(GetPrimeGuideHandler),
        typeof(GetSchemaHandler),
        typeof(AnalyzeWebAppHandler),
        typeof(VerifyWebAppHandler),
        typeof(GetLayoutBoundsHandler),
        typeof(SetLayoutPropertyHandler),
        typeof(SaveLayoutProfilesHandler),
        typeof(ResetLayoutProfileHandler),
        typeof(ExportPackageHandler),
        typeof(ImportPackageHandler),
        typeof(BuildWebAppHandler)
    ];

    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        foreach (var type in HandlerTypes) services.AddScoped(type);
        return services;
    }
}
