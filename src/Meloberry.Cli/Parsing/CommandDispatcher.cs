using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Commands;
using Meloberry.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Meloberry.Cli.Parsing;

public sealed class CommandDispatcher(
    IEnumerable<CliCommand> commands,
    IServiceScopeFactory scopeFactory)
{
    private static readonly HashSet<string> GlobalKnownValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "app-data", "out", "build", "export", "view", "set", "move"
    };

    private static readonly HashSet<string> GlobalKnownFlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "help", "version", "dry-run", "preview", "no-wait", "include-layout", "all", "live"
    };

    private readonly Dictionary<string, CliCommand> _commands =
        commands.ToDictionary(c => c.Descriptor.Verb, StringComparer.OrdinalIgnoreCase);

    public async Task<int> ExecuteAsync(
        string[] args,
        IConsole console,
        CancellationToken cancellationToken = default)
    {
        bool envAgent = string.Equals(Environment.GetEnvironmentVariable("MBY_AGENT"), "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Environment.GetEnvironmentVariable("MBY_AGENT"), "true", StringComparison.OrdinalIgnoreCase);
        string? envAppData = Environment.GetEnvironmentVariable("MBY_APP_DATA");

        if (!TryParseArgs(args, envAgent, envAppData, out var verb, out var positional, out var named, out var globals, out var showPrime, out var showVersion, out var parseErr))
        {
            return CliResultExtensions.WriteValidationError(console, "Cli.ParseError", parseErr!, globals.Json);
        }

        if (showVersion)
            return await InvokeCommandAsync(_commands["version"], console, globals, positional, named, cancellationToken);

        if (showPrime || string.IsNullOrWhiteSpace(verb) || string.Equals(verb, "help", StringComparison.OrdinalIgnoreCase))
            return await InvokeCommandAsync(_commands["prime"], console, globals, positional, named, cancellationToken);

        if (!_commands.TryGetValue(verb, out var cliCommand))
        {
            return CliResultExtensions.WriteValidationError(
                console,
                "Cli.UnknownCommand",
                $"Unknown command '{verb}'. Run 'mby prime' for supported commands.",
                globals.Json);
        }

        foreach (var (optName, values) in named)
        {
            bool isFlag = values.Count == 0;
            bool supported = isFlag
                ? cliCommand.Descriptor.AllowedFlagOptions?.Contains(optName) == true
                : cliCommand.Descriptor.AllowedValueOptions?.Contains(optName) == true;

            if (!supported)
            {
                return CliResultExtensions.WriteValidationError(
                    console,
                    "Cli.UnsupportedOption",
                    $"Option '--{optName}' is not supported by command '{verb}'.",
                    globals.Json);
            }
        }

        return await InvokeCommandAsync(cliCommand, console, globals, positional, named, cancellationToken);
    }

    private async Task<int> InvokeCommandAsync(
        CliCommand cliCommand,
        IConsole console,
        GlobalCliOptions globals,
        IReadOnlyList<string> positional,
        IReadOnlyDictionary<string, List<string>> named,
        CancellationToken cancellationToken)
    {
        ServiceProvider? overrideProvider = null;
        IServiceScopeFactory activeFactory = scopeFactory;
        try
        {
            if (!string.IsNullOrWhiteSpace(globals.AppDataDirectory))
            {
                overrideProvider = CliApplicationRunner.BuildServiceProvider(globals.AppDataDirectory);
                activeFactory = overrideProvider.GetRequiredService<IServiceScopeFactory>();
            }

            using var scope = activeFactory.CreateScope();
            var handler = (ICommandHandler)scope.ServiceProvider.GetRequiredService(cliCommand.Descriptor.HandlerType);
            return await handler.InvokeAsync(new CliInvocationContext(console, globals, positional, named, cancellationToken));
        }
        finally
        {
            overrideProvider?.Dispose();
        }
    }

    private static bool TryParseArgs(
        string[] args,
        bool envAgent,
        string? envAppData,
        out string verb,
        out List<string> positional,
        out Dictionary<string, List<string>> named,
        out GlobalCliOptions globals,
        out bool showPrime,
        out bool showVersion,
        out string? errorMessage)
    {
        verb = string.Empty;
        positional = [];
        named = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        showPrime = false;
        showVersion = false;
        errorMessage = null;

        bool json = envAgent;
        string? appDataDir = string.IsNullOrWhiteSpace(envAppData) ? null : envAppData;
        bool endOfOptions = false;
        var rawPositionals = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!endOfOptions && token == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && token == "-v")
            {
                showVersion = true;
                continue;
            }

            if (!endOfOptions && token == "-h")
            {
                showPrime = true;
                continue;
            }

            if (!endOfOptions && token == "-o")
            {
                if (i + 1 >= args.Length)
                {
                    globals = new GlobalCliOptions(json, appDataDir);
                    errorMessage = "Option '-o' requires a directory path.";
                    return false;
                }
                AddNamedValue(named, "out", args[++i]);
                continue;
            }

            if (!endOfOptions && token.StartsWith("--", StringComparison.Ordinal))
            {
                var opt = token[2..].ToLowerInvariant();
                switch (opt)
                {
                    case "json":
                        json = true;
                        break;
                    case "help":
                        showPrime = true;
                        break;
                    case "version":
                        showVersion = true;
                        break;
                    case "app-data":
                        if (i + 1 >= args.Length)
                        {
                            globals = new GlobalCliOptions(json, appDataDir);
                            errorMessage = "Option '--app-data' requires a directory path.";
                            return false;
                        }
                        appDataDir = args[++i];
                        break;
                    default:
                        if (GlobalKnownFlagOptions.Contains(opt))
                        {
                            if (!named.ContainsKey(opt)) named[opt] = [];
                        }
                        else if (GlobalKnownValueOptions.Contains(opt))
                        {
                            if (i + 1 >= args.Length)
                            {
                                globals = new GlobalCliOptions(json, appDataDir);
                                errorMessage = $"Option '--{opt}' requires a value.";
                                return false;
                            }
                            AddNamedValue(named, opt, args[++i]);
                        }
                        else
                        {
                            globals = new GlobalCliOptions(json, appDataDir);
                            errorMessage = $"Unknown option '--{opt}'.";
                            return false;
                        }
                        break;
                }
            }
            else
            {
                rawPositionals.Add(token);
            }
        }

        globals = new GlobalCliOptions(json, appDataDir);
        if (rawPositionals.Count == 0)
            return true;

        verb = rawPositionals[0];
        positional.AddRange(rawPositionals.Skip(1));
        return true;
    }

    private static void AddNamedValue(Dictionary<string, List<string>> named, string key, string value)
    {
        if (!named.TryGetValue(key, out var list))
        {
            list = [];
            named[key] = list;
        }
        list.Add(value);
    }
}
