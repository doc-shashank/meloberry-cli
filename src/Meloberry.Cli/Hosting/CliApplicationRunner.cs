using Meloberry.Application;
using Meloberry.Application.Abstractions;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.GuiEditor;
using Meloberry.Cli.Parsing;
using Meloberry.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Meloberry.Cli.Hosting;

public static class CliServiceRegistration
{
    public static IServiceCollection AddCli(this IServiceCollection services)
    {
        services.AddSingleton<ICliToolMetadataProvider>(
            new AssemblyCliToolMetadataProvider(typeof(CliApplicationRunner).Assembly, "mby"));
        services.AddSingleton<ILocalPreviewHost, LocalPreviewHost>();
        services.AddSingleton<CommandDispatcher>();

        foreach (var cmdType in typeof(CliServiceRegistration).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(CliCommand).IsAssignableFrom(t)))
        {
            var cmd = (CliCommand)Activator.CreateInstance(cmdType)!;
            services.AddSingleton(cmd);
            services.AddScoped(cmd.Descriptor.HandlerType);
        }

        return services;
    }
}

public static class CliApplicationRunner
{
    public static ServiceProvider BuildServiceProvider(string? appDataDirectory = null)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts =>
        {
            if (!string.IsNullOrWhiteSpace(appDataDirectory))
                opts.AppDataDirectory = Path.GetFullPath(appDataDirectory);
        });
        services.AddCli();
        return services.BuildServiceProvider();
    }

    public static async Task<int> ExecuteAsync(
        string[] args, IConsole console, string? appDataDirectory = null, CancellationToken cancellationToken = default)
    {
        using var provider = BuildServiceProvider(appDataDirectory ?? ExtractAppDataArg(args));
        return await ExecuteWithProviderAsync(args, console, provider, cancellationToken);
    }

    public static Task<int> ExecuteWithProviderAsync(
        string[] args, IConsole console, IServiceProvider provider, CancellationToken cancellationToken = default)
    {
        var dispatcher = provider.GetRequiredService<CommandDispatcher>();
        return dispatcher.ExecuteAsync(args, console, cancellationToken);
    }

    private static string? ExtractAppDataArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--") break;
            if (string.Equals(args[i], "--app-data", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return Environment.GetEnvironmentVariable("MBY_APP_DATA");
    }
}
