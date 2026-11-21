using System.Net;
using System.Text;
using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Layouts;
using Meloberry.Cli.Abstractions;
using Meloberry.Domain.Layouts;
using Microsoft.Extensions.DependencyInjection;

namespace Meloberry.Cli.GuiEditor;

public sealed class LocalPreviewHost(
    IServiceScopeFactory scopeFactory,
    ILayoutEditorPageBuilder layoutEditorPageBuilder,
    ICliToolMetadataProvider metadataProvider) : ILocalPreviewHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = JsonOptions;

    public Task<int> ServeDirectoryAsync(
        string directoryPath,
        IConsole console,
        bool noWait,
        bool json = false,
        bool silent = false,
        CancellationToken cancellationToken = default) =>
        RunWebAppServerLoopAsync(directoryPath, console, exitAfterFirstAnalysis: false, noWait, json, silent, cancellationToken);

    public Task<int> ServeForLiveAnalysisAsync(
        string directoryPath,
        IConsole console,
        bool noWait,
        bool json,
        CancellationToken cancellationToken = default) =>
        RunWebAppServerLoopAsync(directoryPath, console, exitAfterFirstAnalysis: true, noWait, json, silent: false, cancellationToken);

    private async Task<int> RunWebAppServerLoopAsync(
        string directoryPath,
        IConsole console,
        bool exitAfterFirstAnalysis,
        bool noWait,
        bool jsonOutput,
        bool silent,
        CancellationToken cancellationToken)
    {
        var fullDir = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDir) || !File.Exists(Path.Combine(fullDir, "index.html")))
        {
            return CliResultExtensions.WriteNotFoundError(
                console,
                "Analysis.TargetNotFound",
                $"WebApp directory '{directoryPath}' does not exist or is missing index.html.",
                jsonOutput);
        }

        var prefix = $"http://127.0.0.1:{FindFreePort()}/";
        if (noWait)
        {
            if (jsonOutput)
            {
                WriteJsonEnvelope(console, new
                {
                    mode = exitAfterFirstAnalysis ? "live-analysis" : "preview",
                    url = prefix,
                    root = fullDir
                });
            }
            else if (!silent)
            {
                var modeLabel = exitAfterFirstAnalysis ? "Live analysis capture ready" : "Preview ready";
                console.Out.WriteLine($"{modeLabel} at {prefix} (root: '{fullDir}')");
            }

            return ExitCodes.Success;
        }

        if (!silent)
        {
            console.Out.WriteLine(exitAfterFirstAnalysis
                ? $"Serving Meloberry WebApp at {prefix} — complete the quiz and click 'Finish & Analyze' to capture results (Ctrl+C to cancel)..."
                : $"Serving Meloberry WebApp at {prefix} — press Ctrl+C to stop.");
        }

        await RunHttpServerAsync(prefix, cancellationToken, async ctx =>
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (ctx.Request.HttpMethod == "POST" && string.Equals(path, "/api/analysis", StringComparison.OrdinalIgnoreCase))
            {
                var shouldStop = await HandleAnalysisPostAsync(ctx, fullDir, console, exitAfterFirstAnalysis, jsonOutput, cancellationToken);
                return shouldStop;
            }

            if (!Uri.TryCreate("http://localhost" + path, UriKind.Absolute, out _))
            {
                await TryWriteStatusAsync(ctx.Response, 400, cancellationToken);
                return false;
            }

            string rel;
            try
            {
                rel = Uri.UnescapeDataString(path.TrimStart('/'));
            }
            catch (UriFormatException)
            {
                await TryWriteStatusAsync(ctx.Response, 400, cancellationToken);
                return false;
            }

            if (string.IsNullOrWhiteSpace(rel))
                rel = "index.html";

            if (!PathConfinement.TryResolveInside(fullDir, rel.Replace('/', Path.DirectorySeparatorChar), out var safePath) ||
                !File.Exists(safePath))
            {
                await TryWriteStatusAsync(ctx.Response, 404, cancellationToken);
                return false;
            }

            var ext = Path.GetExtension(safePath);
            if (string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase))
                ctx.Response.AddHeader("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; sandbox");

            var bytes = await File.ReadAllBytesAsync(safePath, cancellationToken);
            await TryWriteBytesAsync(ctx.Response, 200, GetMimeType(ext), bytes, cancellationToken);
            return false;
        });

        return ExitCodes.Success;
    }

    private async Task<bool> HandleAnalysisPostAsync(
        HttpListenerContext ctx,
        string fullDir,
        IConsole console,
        bool exitAfterFirstAnalysis,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);

        string reportElement;
        string? markdownText = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                await TryWriteJsonAsync(ctx.Response, 400, new { saved = false, error = "Request body must be a JSON object." }, cancellationToken);
                return false;
            }

            reportElement = doc.RootElement.TryGetProperty("report", out var rEl) ? rEl.GetRawText() : body;
            if (doc.RootElement.TryGetProperty("markdown", out var mEl))
            {
                if (mEl.ValueKind == JsonValueKind.String)
                    markdownText = mEl.GetString();
                else if (mEl.ValueKind != JsonValueKind.Null)
                {
                    await TryWriteJsonAsync(ctx.Response, 400, new { saved = false, error = "Field 'markdown' must be a JSON string." }, cancellationToken);
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            await TryWriteJsonAsync(ctx.Response, 400, new { saved = false, error = $"Invalid JSON payload: {ex.Message}" }, cancellationToken);
            return false;
        }

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAnalysisReportStore>();
        var saveRes = await store.SaveReportToWebAppDirectoryAsync(fullDir, reportElement, markdownText, cancellationToken);
        if (!saveRes.IsSuccess)
        {
            await TryWriteJsonAsync(ctx.Response, 400, new { saved = false, error = saveRes.Error!.Description }, cancellationToken);
            return false;
        }

        await TryWriteJsonAsync(ctx.Response, 200, new
        {
            saved = true,
            jsonPath = saveRes.Value.JsonPath,
            markdownPath = saveRes.Value.MarkdownPath
        }, cancellationToken);

        if (!exitAfterFirstAnalysis)
            return false;

        if (jsonOutput)
        {
            WriteJsonEnvelope(console, new
            {
                jsonPath = saveRes.Value.JsonPath,
                markdownPath = saveRes.Value.MarkdownPath,
                report = saveRes.Value.Report
            });
        }
        else
        {
            console.Out.WriteLine();
            console.Out.WriteLine(saveRes.Value.Report.ToMarkdownReport());
            console.Out.WriteLine();
            console.Out.WriteLine($"Saved JSON:     {saveRes.Value.JsonPath}");
            console.Out.WriteLine($"Saved Markdown: {saveRes.Value.MarkdownPath}");
        }

        return true;
    }

    public async Task<int> ServeLayoutEditorAsync(
        string targetType,
        IConsole console,
        bool noWait,
        bool json = false,
        bool silent = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveComponentTarget(targetType, out var initialComponent, out var defaultProfile))
        {
            return CliResultExtensions.WriteValidationError(
                console,
                "Layout.InvalidTargetType",
                $"Invalid target '{targetType}'. Use 'article-text', 'image', 'mcq-question', 'mcq-options', 'flashcard' (or legacy 'article', 'mcq').",
                json);
        }

        var prefix = $"http://127.0.0.1:{FindFreePort()}/";
        if (noWait)
        {
            if (json)
                WriteJsonEnvelope(console, new { target = initialComponent, profile = defaultProfile, url = prefix });
            else if (!silent)
                console.Out.WriteLine($"Component GUI Layout Editor ready for '{initialComponent}' at {prefix}");

            return ExitCodes.Success;
        }

        var toolMeta = metadataProvider.GetMetadata();
        if (!silent)
        {
            console.Out.WriteLine(
                $"Meloberry ({toolMeta.ExecutableName} v{toolMeta.Version}) Component GUI Layout Editor ({initialComponent}) running at {prefix} — press Ctrl+C to stop.");
        }

        await RunHttpServerAsync(prefix, cancellationToken, async ctx =>
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            if (method == "GET" && path == "/api/layout")
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<GetLayoutBoundsHandler>();
                var res = await handler.HandleAsync(new GetLayoutBoundsQuery(null), cancellationToken);
                if (!res.IsSuccess)
                {
                    await TryWriteJsonAsync(ctx.Response, 400, res.Error, cancellationToken);
                    return false;
                }

                var grouped = res.Value.Properties
                    .GroupBy(p => p.TargetType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                await TryWriteJsonAsync(ctx.Response, 200, new { tool = toolMeta, initialComponent, profiles = grouped }, cancellationToken);
                return false;
            }

            if (method == "GET" && path == "/api/bounds")
            {
                var requestedTarget = ctx.Request.QueryString["target"];
                var effectiveTarget = string.Equals(requestedTarget, "all", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : (!string.IsNullOrWhiteSpace(requestedTarget) ? requestedTarget : defaultProfile);

                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<GetLayoutBoundsHandler>();
                var res = await handler.HandleAsync(new GetLayoutBoundsQuery(effectiveTarget), cancellationToken);
                await TryWriteJsonAsync(ctx.Response, res.IsSuccess ? 200 : 400, res.IsSuccess ? res.Value : res.Error, cancellationToken);
                return false;
            }

            if (method == "POST" && path == "/api/layout")
            {
                await HandleLayoutSaveAsync(ctx, defaultProfile, cancellationToken);
                return false;
            }

            if (method == "POST" && path == "/api/layout/reset")
            {
                await HandleLayoutResetAsync(ctx, cancellationToken);
                return false;
            }

            var html = layoutEditorPageBuilder.BuildHtml(initialComponent, toolMeta);
            await TryWriteBytesAsync(ctx.Response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), cancellationToken);
            return false;
        });

        return ExitCodes.Success;
    }

    private async Task HandleLayoutSaveAsync(HttpListenerContext ctx, string defaultProfile, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var errors = new List<string>();

        using var scope = scopeFactory.CreateScope();
        var setter = scope.ServiceProvider.GetRequiredService<SetLayoutPropertyHandler>();

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Request body must be a JSON object.");
            }
            else if (root.TryGetProperty("profiles", out var profilesEl) && profilesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var profileProp in profilesEl.EnumerateObject())
                {
                    if (profileProp.Value.ValueKind == JsonValueKind.Object)
                        await ApplyProfileUpdatesAsync(setter, profileProp.Name, profileProp.Value, errors, cancellationToken);
                }
            }
            else if (root.TryGetProperty("targetType", out var targetEl) &&
                     root.TryGetProperty("values", out var valuesEl) &&
                     valuesEl.ValueKind == JsonValueKind.Object)
            {
                await ApplyProfileUpdatesAsync(setter, targetEl.GetString() ?? defaultProfile, valuesEl, errors, cancellationToken);
            }
            else
            {
                await ApplyProfileUpdatesAsync(setter, defaultProfile, root, errors, cancellationToken);
            }
        }
        catch (JsonException ex)
        {
            errors.Add(ex.Message);
        }

        await TryWriteJsonAsync(ctx.Response, errors.Count == 0 ? 200 : 400, new { saved = errors.Count == 0, errors }, cancellationToken);
    }

    private async Task HandleLayoutResetAsync(HttpListenerContext ctx, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var targetsToReset = new List<string>();
        List<string>? keysToReset = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    await TryWriteJsonAsync(ctx.Response, 400, new { reset = false, errors = new[] { "Request body must be a JSON object." } }, cancellationToken);
                    return;
                }

                if (doc.RootElement.TryGetProperty("targetType", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                {
                    var tVal = tEl.GetString();
                    if (!string.IsNullOrWhiteSpace(tVal))
                        targetsToReset.Add(tVal);
                }

                if (doc.RootElement.TryGetProperty("keys", out var kEl) && kEl.ValueKind == JsonValueKind.Array)
                {
                    keysToReset = kEl.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x!)
                        .ToList();
                }
            }
            catch (JsonException ex)
            {
                await TryWriteJsonAsync(ctx.Response, 400, new { reset = false, errors = new[] { $"Invalid JSON payload: {ex.Message}" } }, cancellationToken);
                return;
            }
        }

        if (targetsToReset.Count == 0)
            targetsToReset.AddRange(LayoutBoundsCatalog.SupportedTargets);

        using var scope = scopeFactory.CreateScope();
        var resetHandler = scope.ServiceProvider.GetRequiredService<ResetLayoutProfileHandler>();
        var errors = new List<string>();

        foreach (var target in targetsToReset)
        {
            var r = await resetHandler.HandleAsync(new ResetLayoutProfileCommand(target, keysToReset), cancellationToken);
            if (!r.IsSuccess)
                errors.Add(r.Error!.Description);
        }

        await TryWriteJsonAsync(ctx.Response, errors.Count == 0 ? 200 : 400, new { reset = errors.Count == 0, errors }, cancellationToken);
    }

    private static async Task RunHttpServerAsync(
        string prefix,
        CancellationToken cancellationToken,
        Func<HttpListenerContext, Task<bool>> handleRequestAsync)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            try
            {
                if (await handleRequestAsync(ctx))
                    break;
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Ignore client disconnect or cancellation while handling request.
            }
            catch (Exception ex)
            {
                await TryWriteJsonAsync(ctx.Response, 500, new { error = ex.Message }, cancellationToken);
            }
        }
    }

    private static async Task ApplyProfileUpdatesAsync(
        SetLayoutPropertyHandler setter,
        string targetType,
        JsonElement valuesObj,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        foreach (var prop in valuesObj.EnumerateObject())
        {
            var valStr = prop.Value.ValueKind == JsonValueKind.String
                ? (prop.Value.GetString() ?? string.Empty)
                : prop.Value.ToString();

            var r = await setter.HandleAsync(new SetLayoutPropertyCommand(targetType, prop.Name, valStr), cancellationToken);
            if (!r.IsSuccess)
                errors.Add($"{targetType}.{prop.Name}: {r.Error!.Description}");
        }
    }

    private static Task TryWriteJsonAsync(HttpListenerResponse response, int statusCode, object? payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, CompactJsonOptions));
        return TryWriteBytesAsync(response, statusCode, "application/json; charset=utf-8", bytes, cancellationToken);
    }

    private static async Task TryWriteStatusAsync(HttpListenerResponse response, int statusCode, CancellationToken cancellationToken)
    {
        await TryWriteBytesAsync(response, statusCode, "text/plain; charset=utf-8", [], cancellationToken);
    }

    private static async Task TryWriteBytesAsync(
        HttpListenerResponse response,
        int statusCode,
        string contentType,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            if (bytes.Length > 0)
                await response.OutputStream.WriteAsync(bytes, cancellationToken);
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Client disconnected or listener closed; avoid crashing the server loop.
        }
    }

    private static void WriteJsonEnvelope(IConsole console, object data) =>
        console.Out.WriteLine(JsonSerializer.Serialize(new { success = true, data }, JsonOptions));

    private static bool TryResolveComponentTarget(string rawTarget, out string initialComponent, out string defaultProfile)
    {
        switch (rawTarget.Trim().ToLowerInvariant())
        {
            case "article-text":
            case "article":
                initialComponent = "article-text";
                defaultProfile = "article";
                return true;
            case "image":
                initialComponent = "image";
                defaultProfile = "article";
                return true;
            case "mcq-question":
            case "mcq":
                initialComponent = "mcq-question";
                defaultProfile = "mcq";
                return true;
            case "mcq-options":
                initialComponent = "mcq-options";
                defaultProfile = "mcq";
                return true;
            case "flashcard":
                initialComponent = "flashcard";
                defaultProfile = "flashcard";
                return true;
            default:
                initialComponent = string.Empty;
                defaultProfile = string.Empty;
                return false;
        }
    }

    private static int FindFreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private static string GetMimeType(string ext) => ext.ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        _ => "application/octet-stream"
    };
}
