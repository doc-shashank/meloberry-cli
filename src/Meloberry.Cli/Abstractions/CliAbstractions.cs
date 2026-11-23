using System.Text.Json;
using System.Text.Json.Serialization;
using Meloberry.Domain.Common;

namespace Meloberry.Cli.Abstractions;

public interface IConsole
{
    TextWriter Out { get; }
    TextWriter Error { get; }
    TextReader In { get; }
    bool IsInputRedirected { get; }
}

public sealed class SystemConsole : IConsole
{
    public TextWriter Out => Console.Out;
    public TextWriter Error => Console.Error;
    public TextReader In => Console.In;
    public bool IsInputRedirected => Console.IsInputRedirected;
}

public sealed class TestConsole(string? inputText = null) : IConsole
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();
    private TextReader _in = new StringReader(inputText ?? string.Empty);

    public TextWriter Out => _out;
    public TextWriter Error => _error;
    public TextReader In => _in;
    public bool IsInputRedirected { get; private set; } = inputText is not null;
    public string StandardOutput => _out.ToString();
    public string StandardError => _error.ToString();

    public void SetInput(string inputText) { _in = new StringReader(inputText); IsInputRedirected = true; }
}

public sealed record GlobalCliOptions(bool Json, string? AppDataDirectory = null, bool Quiet = false, bool Verbose = false);

public sealed class CliInvocationContext(
    IConsole console,
    GlobalCliOptions globalOptions,
    IReadOnlyList<string> positionalArgs,
    IReadOnlyDictionary<string, List<string>> namedOptions,
    CancellationToken cancellationToken)
{
    public IConsole Console { get; } = console;
    public GlobalCliOptions GlobalOptions { get; } = globalOptions;
    public IReadOnlyList<string> PositionalArgs { get; } = positionalArgs;
    public IReadOnlyDictionary<string, List<string>> NamedOptions { get; } = namedOptions;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public bool HasFlag(string name) => NamedOptions.ContainsKey(name);

    public string? GetOptionValue(string name) =>
        NamedOptions.TryGetValue(name, out var list) && list.Count > 0 ? list[^1] : null;

    public IReadOnlyList<string> GetOptionValues(string name) =>
        NamedOptions.TryGetValue(name, out var list) ? list : [];
}

public interface ICommandHandler
{
    Task<int> InvokeAsync(CliInvocationContext context);
}

public sealed record CommandDescriptor(
    string Verb,
    string Usage,
    string Description,
    Type HandlerType,
    IReadOnlySet<string>? AllowedFlagOptions = null,
    IReadOnlySet<string>? AllowedValueOptions = null,
    IReadOnlyList<string>? OptionsHelp = null,
    string? Prerequisites = null,
    IReadOnlyList<string>? Examples = null);

public abstract class CliCommand(CommandDescriptor descriptor)
{
    public CommandDescriptor Descriptor { get; } = descriptor;
}

public interface ILocalPreviewHost
{
    Task<int> ServeDirectoryAsync(string directoryPath, IConsole console, bool noWait, bool json = false, bool silent = false, CancellationToken cancellationToken = default);
    Task<int> ServeForLiveAnalysisAsync(string directoryPath, IConsole console, bool noWait, bool json, CancellationToken cancellationToken = default);
    Task<int> ServeLayoutEditorAsync(string targetType, IConsole console, bool noWait, bool json = false, bool silent = false, CancellationToken cancellationToken = default);
}

public static class ExitCodes
{
    public const int Success = 0, GeneralError = 1, ValidationError = 2, NotFound = 3, Conflict = 4, Unauthorized = 5;
}

public static class CliResultExtensions
{
    internal static readonly JsonSerializerOptions ErrorJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static int ToExitCode(this Result result, IConsole console, bool json = false)
    {
        if (result.IsSuccess) return ExitCodes.Success;
        var error = result.Error!;
        console.Error.WriteLine(json
            ? JsonSerializer.Serialize(new
            {
                success = false,
                error = new
                {
                    code = error.Code,
                    description = error.Description,
                    type = error.Type.ToString(),
                    path = error.Path,
                    hint = error.Hint
                }
            }, ErrorJsonOptions)
            : $"Error [{error.Code}]: {error.Description}");

        return error.Type switch
        {
            ErrorType.Validation => ExitCodes.ValidationError,
            ErrorType.NotFound => ExitCodes.NotFound,
            ErrorType.Conflict => ExitCodes.Conflict,
            ErrorType.Unauthorized or ErrorType.Forbidden => ExitCodes.Unauthorized,
            _ => ExitCodes.GeneralError
        };
    }

    public static int WriteValidationError(IConsole console, string code, string description, bool json = false, string? path = null, string? hint = null) =>
        Result.Failure(new Error(code, description, ErrorType.Validation, path, hint)).ToExitCode(console, json);

    public static int WriteNotFoundError(IConsole console, string code, string description, bool json = false) =>
        Result.Failure(new Error(code, description, ErrorType.NotFound)).ToExitCode(console, json);
}
