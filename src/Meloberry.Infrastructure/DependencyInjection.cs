using Meloberry.Application.Abstractions;
using Meloberry.Infrastructure.Packaging;
using Meloberry.Infrastructure.Persistence;
using Meloberry.Infrastructure.Services;
using Meloberry.Infrastructure.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meloberry.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        Action<MeloberryStorageOptions>? configure = null)
    {
        var options = new MeloberryStorageOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.TryAddSingleton<ICliToolMetadataProvider>(
            new AssemblyCliToolMetadataProvider(typeof(DependencyInjection).Assembly, "mby"));
        services.AddSingleton<ChainTransactionManager>();
        services.AddSingleton<IChainTransactionManager>(sp => sp.GetRequiredService<ChainTransactionManager>());
        services.AddSingleton<IWorkingDirectoryProvider, WorkingDirectoryProvider>();
        services.AddSingleton<IIdGenerator, MonotonicTimestampIdGenerator>();
        services.AddSingleton<IContentInspector, FileContentInspector>();
        services.AddSingleton<IDeclarativeSpecLoader, DeclarativeSpecLoader>();
        services.AddSingleton<SqliteLayoutRepository>();
        services.AddSingleton<ILayoutRepository>(sp => sp.GetRequiredService<SqliteLayoutRepository>());
        services.AddSingleton<ILayoutEditorPageBuilder, LayoutEditorPageBuilder>();
        services.AddScoped<IWorkspaceRepository, SqliteWorkspaceRepository>();
        services.AddScoped<IPackageExporter, MbypkgExporter>();
        services.AddScoped<IWebAppGenerator, SlidingWebAppGenerator>();
        services.AddScoped<IAnalysisReportStore, AnalysisReportStore>();
        services.AddScoped<IHeadlessWebAppVerifier, CdpHeadlessWebAppVerifier>();

        return services;
    }
}
