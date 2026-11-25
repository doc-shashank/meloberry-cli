using System.Text.Json;
using System.Text.Json.Serialization;
using Meloberry.Domain.Common;

namespace Meloberry.Cli.Abstractions;

internal static class CliCommandHelpers
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<(string? FilePath, string? InlineJson, int? ErrorExit)> ReadJsonInputAsync(
        CliInvocationContext context,
        string fileOrDash)
    {
        if (string.Equals(fileOrDash, "-", StringComparison.Ordinal))
        {
            var text = (await context.Console.In.ReadToEndAsync(context.CancellationToken)).TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(text))
            {
                int err = CliResultExtensions.WriteValidationError(
                    context.Console,
                    "Cli.EmptyStdin",
                    "Expected JSON payload on stdin when passing '-'.",
                    context.GlobalOptions.Json);
                return (null, null, err);
            }
            return (null, text, null);
        }

        return (fileOrDash, null, null);
    }

    public static async Task<int> FinishAsync<T>(
        CliInvocationContext context, Task<Result<T>> task, Func<T, string> formatText)
    {
        var result = await task;
        if (!result.IsSuccess) return result.ToExitCode(context.Console, context.GlobalOptions.Json);
        WriteOutput(context, result.Value, formatText(result.Value));
        return ExitCodes.Success;
    }

    public static void WriteOutput<T>(CliInvocationContext context, T payload, string humanReadableText)
    {
        context.Console.Out.WriteLine(context.GlobalOptions.Json
            ? JsonSerializer.Serialize(new { success = true, data = payload }, JsonOptions)
            : humanReadableText);
    }

    public static bool TryParseTimeoutOption(CliInvocationContext context, out int timeout, out int? errorExit)
    {
        timeout = 30;
        errorExit = null;
        var rawTimeout = context.GetOptionValue("timeout");
        if (!string.IsNullOrWhiteSpace(rawTimeout) && (!int.TryParse(rawTimeout, out timeout) || timeout is < 5 or > 180))
        {
            errorExit = CliResultExtensions.WriteValidationError(
                context.Console,
                "Cli.InvalidArgs",
                $"Invalid --timeout '{rawTimeout}'. Pass integer seconds between 5 and 180.",
                context.GlobalOptions.Json);
            return false;
        }
        return true;
    }
}
