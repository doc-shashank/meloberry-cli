using System.Text;
using System.Text.Json;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Commands;
using Meloberry.Cli.Hosting;
using Meloberry.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Meloberry.Cli.Parsing;

public sealed class CommandDispatcher(
    IEnumerable<CliCommand> commands,
    IServiceScopeFactory scopeFactory)
{
    private static readonly HashSet<string> GlobalKnownValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "app-data", "out", "build", "export", "view", "set", "move", "timeout"
    };

    private static readonly HashSet<string> GlobalKnownFlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "help", "version", "dry-run", "preview", "no-wait", "include-layout", "all", "live",
        "quiet", "verbose", "headless", "verify", "static-only", "ephemeral"
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

        bool jsonMode = envAgent || args.TakeWhile(a => a != "--").Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));

        try
        {
            if (!TryParseArgs(args, envAgent, out var verb, out var positional, out var named, out var globals, out var showHelp, out var showVersion, out var parseErr))
            {
                return CliResultExtensions.WriteValidationError(console, "Cli.ParseError", parseErr!, globals.Json);
            }

            jsonMode = globals.Json;

            if (showVersion)
                return await InvokeCommandAsync(_commands["version"], console, globals, positional, named, cancellationToken);

            if (string.Equals(verb, "help", StringComparison.OrdinalIgnoreCase))
            {
                if (positional.Count == 0)
                    return await InvokeCommandAsync(_commands["prime"], console, globals, positional, named, cancellationToken);

                if (!_commands.TryGetValue(positional[0], out var helpTarget))
                {
                    return CliResultExtensions.WriteValidationError(
                        console,
                        "Cli.UnknownCommand",
                        $"Unknown command '{positional[0]}'. Run 'mby prime' for supported commands.",
                        globals.Json);
                }

                WriteCommandHelp(helpTarget, console, globals.Json);
                return ExitCodes.Success;
            }

            if (string.IsNullOrWhiteSpace(verb))
                return await InvokeCommandAsync(_commands["prime"], console, globals, positional, named, cancellationToken);

            if (!_commands.TryGetValue(verb, out var cliCommand))
            {
                if (showHelp)
                    return await InvokeCommandAsync(_commands["prime"], console, globals, positional, named, cancellationToken);

                return CliResultExtensions.WriteValidationError(
                    console,
                    "Cli.UnknownCommand",
                    $"Unknown command '{verb}'. Run 'mby prime' for supported commands.",
                    globals.Json);
            }

            if (showHelp)
            {
                WriteCommandHelp(cliCommand, console, globals.Json);
                return ExitCodes.Success;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CliResultExtensions.WriteFailureError(console, "Cli.Cancelled", "Operation was cancelled.", jsonMode);
        }
        catch (Exception ex)
        {
            return CliResultExtensions.WriteFailureError(console, "Cli.UnexpectedError", ex.Message, jsonMode);
        }
    }

    private static void WriteCommandHelp(CliCommand command, IConsole console, bool json)
    {
        var d = command.Descriptor;
        if (json)
        {
            var payload = new
            {
                verb = d.Verb,
                usage = d.Usage,
                description = d.Description,
                prerequisites = d.Prerequisites,
                options = d.OptionsHelp,
                examples = d.Examples
            };
            console.Out.WriteLine(JsonSerializer.Serialize(new { success = true, data = payload }, CliCommandHelpers.JsonOptions));
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"mby {d.Usage}");
        sb.AppendLine();
        sb.AppendLine(d.Description);
        if (!string.IsNullOrWhiteSpace(d.Prerequisites))
        {
            sb.AppendLine();
            sb.AppendLine("Prerequisites:");
            sb.AppendLine($"  {d.Prerequisites}");
        }
        if (d.OptionsHelp is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Options:");
            foreach (var opt in d.OptionsHelp)
                sb.AppendLine($"  {opt}");
        }
        if (d.Examples is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Examples:");
            foreach (var ex in d.Examples)
                sb.AppendLine($"  {ex}");
        }
        console.Out.WriteLine(sb.ToString().TrimEnd());
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
                var requestedFull = Path.GetFullPath(globals.AppDataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? activeFull = null;
                using (var checkScope = scopeFactory.CreateScope())
                {
                    var activeOpts = checkScope.ServiceProvider.GetService<MeloberryStorageOptions>();
                    if (!string.IsNullOrWhiteSpace(activeOpts?.AppDataDirectory))
                        activeFull = Path.GetFullPath(activeOpts.AppDataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                if (!string.Equals(requestedFull, activeFull, StringComparison.OrdinalIgnoreCase))
                {
                    overrideProvider = CliApplicationRunner.BuildServiceProvider(requestedFull);
                    activeFactory = overrideProvider.GetRequiredService<IServiceScopeFactory>();
                }
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
        string? appDataDir = null;
        bool quiet = false;
        bool verbose = false;
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
                    globals = new GlobalCliOptions(json, appDataDir, quiet, verbose);
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
                    case "quiet":
                        quiet = true;
                        break;
                    case "verbose":
                        verbose = true;
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
                            globals = new GlobalCliOptions(json, appDataDir, quiet, verbose);
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
                                globals = new GlobalCliOptions(json, appDataDir, quiet, verbose);
                                errorMessage = $"Option '--{opt}' requires a value.";
                                return false;
                            }
                            AddNamedValue(named, opt, args[++i]);
                        }
                        else
                        {
                            globals = new GlobalCliOptions(json, appDataDir, quiet, verbose);
                            errorMessage = $"Unknown option '--{opt}'.";
                            return false;
                        }
                        break;
                }
            }
            else if (!endOfOptions && token.Length > 1 && token[0] == '-')
            {
                globals = new GlobalCliOptions(json, appDataDir, quiet, verbose);
                errorMessage = $"Unknown option '{token}'.";
                return false;
            }
            else
            {
                rawPositionals.Add(token);
            }
        }

        globals = new GlobalCliOptions(json, appDataDir, quiet, verbose);
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
