using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Common;
using Meloberry.Application.UseCases.Diagnostics;
using Meloberry.Application.UseCases.Layouts;
using Meloberry.Application.UseCases.Packaging;
using Meloberry.Domain.Common;

namespace Meloberry.Application.UseCases.Json;

public sealed record BatchOperationsCommand(string? JsonFilePath = null, string? InlineJson = null);

public sealed record BatchStepResult(int Step, string Op, bool Success, object? Data = null);

public sealed record BatchOperationsResponse(
    int StepCount,
    bool RolledBack,
    IReadOnlyList<BatchStepResult> Steps);

public sealed partial class BatchOperationsHandler(
    ApplySpecHandler applyHandler,
    PatchEntityHandler patchHandler,
    DeleteEntityHandler deleteHandler,
    LintHandler lintHandler,
    BuildWebAppHandler buildHandler,
    ExportPackageHandler exportHandler,
    SetLayoutPropertyHandler setLayoutHandler,
    IChainTransactionManager? transactionManager = null)
{
    [GeneratedRegex(@"\$(?<ref>prev|\d+)\.id", RegexOptions.IgnoreCase)]
    private static partial Regex IdPlaceholderRegex();

    public async Task<Result<BatchOperationsResponse>> HandleAsync(BatchOperationsCommand command, CancellationToken cancellationToken = default)
    {
        string rawJson;
        if (command.InlineJson is not null)
        {
            rawJson = command.InlineJson.TrimStart('\uFEFF');
        }
        else if (!string.IsNullOrWhiteSpace(command.JsonFilePath) && File.Exists(command.JsonFilePath))
        {
            rawJson = (await File.ReadAllTextAsync(command.JsonFilePath, cancellationToken)).TrimStart('\uFEFF');
        }
        else
        {
            return new Error("Json.FileNotFound", $"Batch JSON file '{command.JsonFilePath}' was not found.", ErrorType.NotFound);
        }

        if (string.IsNullOrWhiteSpace(rawJson))
            return new Error("Batch.InvalidSpec", "Batch input must be a non-empty JSON array of operations.", ErrorType.Validation);

        JsonArray opsArray;
        try
        {
            var parsed = JsonNode.Parse(rawJson, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (parsed is not JsonArray arr || arr.Count == 0)
                return new Error("Batch.InvalidSpec", "Batch input must be a non-empty JSON array of operations.", ErrorType.Validation);
            opsArray = arr;
        }
        catch (JsonException ex)
        {
            return new Error("Json.ParseError", $"Invalid batch JSON: {ex.Message}", ErrorType.Validation, Path: ex.Path);
        }

        using var tx = transactionManager?.Begin();
        var stepResults = new List<BatchStepResult>(opsArray.Count);
        var producedIds = new List<string?>(opsArray.Count);

        try
        {
            for (int i = 0; i < opsArray.Count; i++)
            {
                if (opsArray[i] is not JsonObject opObj)
                {
                    tx?.Rollback();
                    return new Error("Batch.InvalidOp", $"Batch step {i} must be a JSON object with an 'op' field.", ErrorType.Validation, Path: $"[{i}]");
                }

                var op = GetString(opObj, "op")?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(op))
                {
                    tx?.Rollback();
                    return new Error("Batch.InvalidOp", $"Batch step {i} is missing 'op'.", ErrorType.Validation, Path: $"[{i}].op");
                }

                var (ok, data, producedId, error) = await ExecuteStepAsync(opObj, op, producedIds, cancellationToken);
                if (!ok)
                {
                    tx?.Rollback();
                    return new Error(
                        error!.Code,
                        $"Batch step {i} ('{op}') failed and rolled back: {error.Description}",
                        error.Type,
                        Path: $"[{i}]{(error.Path is null ? string.Empty : "." + error.Path)}",
                        Hint: error.Hint,
                        Data: error.Data);
                }

                producedIds.Add(producedId);
                stepResults.Add(new BatchStepResult(i, op, true, data));
            }

            tx?.Commit();
            return new BatchOperationsResponse(stepResults.Count, RolledBack: false, stepResults);
        }
        catch
        {
            tx?.Rollback();
            throw;
        }
    }

    private async Task<(bool Ok, object? Data, string? ProducedId, Error? Error)> ExecuteStepAsync(
        JsonObject obj, string op, List<string?> producedIds, CancellationToken ct)
    {
        string? ResolveAddr(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            return IdPlaceholderRegex().Replace(raw, m =>
            {
                var r = m.Groups["ref"].Value;
                if (string.Equals(r, "prev", StringComparison.OrdinalIgnoreCase))
                    return producedIds.LastOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? m.Value;
                if (int.TryParse(r, out int idx) && idx >= 0 && idx < producedIds.Count && !string.IsNullOrWhiteSpace(producedIds[idx]))
                    return producedIds[idx]!;
                return m.Value;
            });
        }

        switch (op)
        {
            case "apply":
            {
                var target = ResolveAddr(GetString(obj, "addr") ?? GetString(obj, "target"));
                var file = GetString(obj, "file");
                var outDir = GetString(obj, "out");
                var buildOut = GetString(obj, "build");
                var exportSpec = GetString(obj, "export");
                bool dryRun = GetBool(obj, "dryRun");
                bool verify = GetBool(obj, "verify");
                bool staticOnly = GetBool(obj, "staticOnly");
                int timeout = GetInt(obj, "timeout") ?? 30;
                var specNode = GetNode(obj, "spec");

                if (specNode is null && string.IsNullOrWhiteSpace(file))
                    return (false, null, null, new Error("Batch.InvalidOp", "'apply' requires 'spec' object or 'file' path.", ErrorType.Validation));

                var res = await applyHandler.HandleAsync(
                    new ApplySpecCommand(target, file, specNode?.ToJsonString(), outDir, dryRun, buildOut, exportSpec, VerifyWeb: verify, StaticVerifyOnly: staticOnly, VerifyTimeoutSeconds: timeout), ct);
                return res.IsSuccess
                    ? (true, res.Value, res.Value.TargetId, null)
                    : (false, null, null, res.Error);
            }

            case "patch":
            {
                var addr = ResolveAddr(GetString(obj, "addr") ?? GetString(obj, "target"));
                if (string.IsNullOrWhiteSpace(addr))
                    return (false, null, null, new Error("Batch.InvalidOp", "'patch' requires 'addr'.", ErrorType.Validation));

                var file = GetString(obj, "file");
                var dataNode = GetNode(obj, "data") ?? GetNode(obj, "patch");
                var move = GetString(obj, "move");
                List<string>? sets = null;
                var rawSetNode = GetNode(obj, "set");
                if (rawSetNode is not null)
                {
                    if (rawSetNode is not JsonArray setArr)
                        return (false, null, null, new Error("Batch.InvalidOp", "'patch.set' must be an array of 'key=value' strings.", ErrorType.Validation, Path: "set"));
                    sets = new List<string>(setArr.Count);
                    for (int sIdx = 0; sIdx < setArr.Count; sIdx++)
                    {
                        var item = setArr[sIdx];
                        if (item is null || item.GetValueKind() != JsonValueKind.String)
                            return (false, null, null, new Error("Batch.InvalidOp", $"'patch.set[{sIdx}]' must be a 'key=value' string.", ErrorType.Validation, Path: $"set[{sIdx}]"));
                        sets.Add(item.GetValue<string>());
                    }
                }

                var res = await patchHandler.HandleAsync(
                    new PatchEntityCommand(addr, file, dataNode?.ToJsonString(), sets, move), ct);
                return res.IsSuccess
                    ? (true, res.Value, res.Value.TargetId, null)
                    : (false, null, null, res.Error);
            }

            case "rm":
            {
                var addr = ResolveAddr(GetString(obj, "addr") ?? GetString(obj, "target"));
                if (string.IsNullOrWhiteSpace(addr))
                    return (false, null, null, new Error("Batch.InvalidOp", "'rm' requires 'addr'.", ErrorType.Validation));
                var res = await deleteHandler.HandleAsync(new DeleteEntityCommand(addr), ct);
                if (!res.IsSuccess) return (false, null, null, res.Error);
                var nextProducedId = res.Value.EntityKind is "session" or "quiz" ? null : res.Value.WorkspaceId;
                return (true, res.Value, nextProducedId, null);
            }

            case "check":
            {
                var addr = ResolveAddr(GetString(obj, "addr") ?? GetString(obj, "target"));
                if (string.IsNullOrWhiteSpace(addr))
                    return (false, null, null, new Error("Batch.InvalidOp", "'check' requires 'addr'.", ErrorType.Validation));
                var res = await lintHandler.HandleAsync(new LintCommand(addr), ct);
                if (!res.IsSuccess) return (false, null, null, res.Error);
                if (res.Value.ErrorCount > 0)
                    return (false, res.Value, null, new Error("Lint.Failed", $"Lint failed with {res.Value.ErrorCount} error(s).", ErrorType.Validation, Data: res.Value));
                return (true, res.Value, res.Value.TargetId, null);
            }

            case "build":
            {
                var addr = ResolveAddr(GetString(obj, "addr") ?? GetString(obj, "target"));
                if (string.IsNullOrWhiteSpace(addr))
                    return (false, null, null, new Error("Batch.InvalidOp", "'build' requires 'addr'.", ErrorType.Validation));
                var outDir = GetString(obj, "out");
                bool verify = GetBool(obj, "verify");
                bool staticOnly = GetBool(obj, "staticOnly");
                int timeout = GetInt(obj, "timeout") ?? 30;
                var res = await buildHandler.HandleAsync(new BuildWebAppCommand(addr, outDir, Verify: verify, StaticVerifyOnly: staticOnly, VerifyTimeoutSeconds: timeout), ct);
                return res.IsSuccess
                    ? (true, res.Value, res.Value.TargetId, null)
                    : (false, null, null, res.Error);
            }

            case "pkg":
            {
                var addr = ResolveAddr(GetString(obj, "addr") ?? GetString(obj, "target"));
                var platform = GetString(obj, "platform") ?? "desktop";
                var outPath = GetString(obj, "out");
                bool includeLayout = GetBool(obj, "includeLayout");
                if (string.IsNullOrWhiteSpace(addr))
                    return (false, null, null, new Error("Batch.InvalidOp", "'pkg' requires 'addr'.", ErrorType.Validation));
                var res = await exportHandler.HandleAsync(new ExportPackageCommand(addr, outPath, platform, includeLayout), ct);
                return res.IsSuccess
                    ? (true, res.Value, res.Value.TargetId, null)
                    : (false, null, null, res.Error);
            }

            case "layout":
            {
                var target = GetString(obj, "target") ?? string.Empty;
                var prop = GetString(obj, "property") ?? string.Empty;
                var val = GetString(obj, "value") ?? string.Empty;
                var res = await setLayoutHandler.HandleAsync(new SetLayoutPropertyCommand(target, prop, val), ct);
                return res.IsSuccess
                    ? (true, res.Value, null, null)
                    : (false, null, null, res.Error);
            }

            default:
                return (false, null, null, new Error("Batch.UnknownOp", $"Unknown batch operation '{op}'. Supported: apply, patch, rm, check, build, pkg, layout.", ErrorType.Validation));
        }
    }

    private static JsonNode? GetNode(JsonObject obj, string name)
    {
        foreach (var kv in obj)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    private static string? GetString(JsonObject obj, string name)
    {
        var node = GetNode(obj, name);
        return node is not null && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    }

    private static bool GetBool(JsonObject obj, string name)
    {
        var node = GetNode(obj, name);
        return node is JsonValue v && v.TryGetValue<bool>(out bool b) && b;
    }

    private static int? GetInt(JsonObject obj, string name)
    {
        var node = GetNode(obj, name);
        return node is JsonValue v && v.TryGetValue<int>(out int n) ? n : null;
    }
}
