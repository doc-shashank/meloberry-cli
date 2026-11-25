using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meloberry.Application.Abstractions;
using Meloberry.Application.UseCases.Layouts;
using Meloberry.Cli.Abstractions;
using Meloberry.Domain.Layouts;
using Meloberry.Infrastructure.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Meloberry.Cli.GuiEditor;

public sealed class LocalPreviewHost(
    IServiceScopeFactory scopeFactory,
    ILayoutEditorPageBuilder layoutEditorPageBuilder,
    ICliToolMetadataProvider metadataProvider) : ILocalPreviewHost
{
    private const int MaxLayoutRequestBodyBytes = 64 * 1024;

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

        if (noWait)
        {
            var noWaitPrefix = $"http://127.0.0.1:{WebHostUtilities.FindFreeLoopbackPort()}/";
            if (jsonOutput)
            {
                WriteJsonEnvelope(console, new
                {
                    mode = exitAfterFirstAnalysis ? "live-analysis" : "preview",
                    url = noWaitPrefix,
                    root = fullDir
                });
            }
            else if (!silent)
            {
                var modeLabel = exitAfterFirstAnalysis ? "Live analysis capture ready" : "Preview ready";
                console.Out.WriteLine($"{modeLabel} at {noWaitPrefix} (root: '{fullDir}')");
            }

            return ExitCodes.Success;
        }

        var (listener, prefix) = StartHttpListenerWithRetry();
        using (listener)
        {
            if (!silent)
            {
                console.Out.WriteLine(exitAfterFirstAnalysis
                    ? $"Serving Meloberry WebApp at {prefix} — complete the quiz and click 'Finish & Analyze' to capture results (Ctrl+C to cancel)..."
                    : $"Serving Meloberry WebApp at {prefix} — press Ctrl+C to stop.");
                console.Out.Flush();
            }

            await RunHttpServerLoopAsync(listener, cancellationToken, async ctx =>
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
                if (IsImmutableHashedAsset(rel))
                    ctx.Response.AddHeader("Cache-Control", "public, max-age=31536000, immutable");

                await TryWriteFileStreamAsync(ctx.Response, 200, WebHostUtilities.GetMimeType(ext), safePath, cancellationToken);
                return false;
            });
        }

        return ExitCodes.Success;
    }

    private static bool IsImmutableHashedAsset(string relativePath)
    {
        var norm = relativePath.Replace('\\', '/').TrimStart('/');
        return norm.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
               norm.StartsWith("katex/fonts/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HandleAnalysisPostAsync(
        HttpListenerContext ctx,
        string fullDir,
        IConsole console,
        bool exitAfterFirstAnalysis,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        const int MaxAnalysisRequestBodyBytes = 5 * 1024 * 1024;
        var (tooLarge, body) = await ReadRequestBodyBoundedAsync(ctx.Request, MaxAnalysisRequestBodyBytes, cancellationToken);
        if (tooLarge)
        {
            await TryWriteJsonAsync(ctx.Response, 413, new { saved = false, error = "Analysis payload exceeds 5 MB limit." }, cancellationToken);
            return false;
        }

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
                $"Invalid target '{targetType}'. Use 'article-text', 'callout', 'table', 'image', 'mcq-question', 'mcq-options', 'fill-blank', 'assertion-reason', 'spot-error', 'diff', 'mechanism', 'relabel', 'flashcard' (or legacy 'article', 'mcq').",
                json);
        }

        if (noWait)
        {
            var noWaitPrefix = $"http://127.0.0.1:{WebHostUtilities.FindFreeLoopbackPort()}/";
            if (json)
                WriteJsonEnvelope(console, new { target = initialComponent, profile = defaultProfile, url = noWaitPrefix });
            else if (!silent)
                console.Out.WriteLine($"Component GUI Layout Editor ready for '{initialComponent}' at {noWaitPrefix}");

            return ExitCodes.Success;
        }

        var toolMeta = metadataProvider.GetMetadata();
        var csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var (listener, prefix) = StartHttpListenerWithRetry();

        using (listener)
        {
            if (!silent)
            {
                if (json)
                {
                    WriteJsonEnvelope(console, new
                    {
                        target = initialComponent,
                        profile = defaultProfile,
                        url = prefix,
                        running = true
                    });
                }
                else
                {
                    console.Out.WriteLine(
                        $"Meloberry ({toolMeta.ExecutableName} v{toolMeta.Version}) Component GUI Layout Editor ({initialComponent}) running at {prefix} — press Ctrl+C to stop.");
                }
                console.Out.Flush();
            }

            await RunHttpServerLoopAsync(listener, cancellationToken, async ctx =>
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                var method = ctx.Request.HttpMethod;

                if (string.Equals(path, "/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMethodNotAllowedAsync(ctx.Response, "GET", isApi: false, cancellationToken);
                        return false;
                    }

                    ctx.Response.AddHeader("Cache-Control", "no-store");
                    var html = layoutEditorPageBuilder.BuildHtml(initialComponent, toolMeta, csrfToken);
                    await TryWriteBytesAsync(ctx.Response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), cancellationToken);
                    return false;
                }

                if (string.Equals(path, "/favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMethodNotAllowedAsync(ctx.Response, "GET, HEAD", isApi: false, cancellationToken);
                        return false;
                    }

                    await TryWriteNoContentAsync(ctx.Response);
                    return false;
                }

                if (string.Equals(path, "/api/layout", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
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

                    if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsValidCsrfToken(ctx.Request, csrfToken))
                        {
                            await TryWriteJsonAsync(ctx.Response, 403, new { saved = false, errors = new[] { "Invalid or missing editor CSRF token." } }, cancellationToken);
                            return false;
                        }

                        await HandleLayoutSaveAsync(ctx, defaultProfile, cancellationToken);
                        return false;
                    }

                    await WriteMethodNotAllowedAsync(ctx.Response, "GET, POST", isApi: true, cancellationToken);
                    return false;
                }

                if (string.Equals(path, "/api/layout/reset", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMethodNotAllowedAsync(ctx.Response, "POST", isApi: true, cancellationToken);
                        return false;
                    }

                    if (!IsValidCsrfToken(ctx.Request, csrfToken))
                    {
                        await TryWriteJsonAsync(ctx.Response, 403, new { reset = false, errors = new[] { "Invalid or missing editor CSRF token." } }, cancellationToken);
                        return false;
                    }

                    await HandleLayoutResetAsync(ctx, cancellationToken);
                    return false;
                }

                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    await TryWriteJsonAsync(ctx.Response, 404, new { error = $"Endpoint '{path}' not found." }, cancellationToken);
                    return false;
                }

                await TryWriteBytesAsync(ctx.Response, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"), cancellationToken);
                return false;
            });
        }

        return ExitCodes.Success;
    }

    private async Task HandleLayoutSaveAsync(HttpListenerContext ctx, string defaultProfile, CancellationToken cancellationToken)
    {
        var (tooLarge, body) = await ReadRequestBodyBoundedAsync(ctx.Request, MaxLayoutRequestBodyBytes, cancellationToken);
        if (tooLarge)
        {
            await TryWriteJsonAsync(ctx.Response, 413, new { saved = false, errors = new[] { "Request body exceeds 64 KB limit." } }, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            await TryWriteJsonAsync(ctx.Response, 400, new { saved = false, errors = new[] { "Request body must be a non-empty JSON object." } }, cancellationToken);
            return;
        }

        var errors = new List<string>();
        var parsedProfiles = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Request body must be a JSON object.");
            }
            else if (root.TryGetProperty("profiles", out var profilesEl))
            {
                if (profilesEl.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Field 'profiles' must be a JSON object.");
                }
                else
                {
                    foreach (var profileProp in profilesEl.EnumerateObject())
                    {
                        if (profileProp.Value.ValueKind != JsonValueKind.Object)
                        {
                            errors.Add($"Profile '{profileProp.Name}' must be a JSON object.");
                            continue;
                        }

                        var propMap = ExtractScalarPropertyMap(profileProp.Name, profileProp.Value, errors);
                        if (propMap.Count == 0 && errors.Count == 0)
                            errors.Add($"Profile '{profileProp.Name}' must contain at least one property update.");
                        else
                            parsedProfiles[profileProp.Name] = propMap;
                    }

                    if (parsedProfiles.Count == 0 && errors.Count == 0)
                        errors.Add("Field 'profiles' must contain at least one profile update.");
                }
            }
            else if (root.TryGetProperty("targetType", out var targetEl) || root.TryGetProperty("values", out _))
            {
                if (targetEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(targetEl.GetString()))
                {
                    errors.Add("Field 'targetType' must be a non-empty string.");
                }
                else if (!root.TryGetProperty("values", out var valuesEl) || valuesEl.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Field 'values' must be a JSON object.");
                }
                else
                {
                    var targetName = targetEl.GetString()!;
                    var propMap = ExtractScalarPropertyMap(targetName, valuesEl, errors);
                    if (propMap.Count == 0 && errors.Count == 0)
                        errors.Add("Field 'values' must contain at least one property update.");
                    else
                        parsedProfiles[targetName] = propMap;
                }
            }
            else
            {
                var propMap = ExtractScalarPropertyMap(defaultProfile, root, errors);
                if (propMap.Count == 0 && errors.Count == 0)
                    errors.Add("Request object must contain at least one property update.");
                else
                    parsedProfiles[defaultProfile] = propMap;
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON payload: {ex.Message}");
        }

        if (errors.Count > 0)
        {
            await TryWriteJsonAsync(ctx.Response, 400, new { saved = false, errors }, cancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var saveHandler = scope.ServiceProvider.GetRequiredService<SaveLayoutProfilesHandler>();
        var saveRes = await saveHandler.HandleAsync(new SaveLayoutProfilesCommand(parsedProfiles), cancellationToken);
        if (!saveRes.IsSuccess)
        {
            await TryWriteJsonAsync(ctx.Response, 400, new { saved = false, errors = new[] { saveRes.Error!.Description } }, cancellationToken);
            return;
        }

        if (!saveRes.Value.Saved)
        {
            await TryWriteJsonAsync(ctx.Response, 400, new { saved = false, errors = saveRes.Value.Errors }, cancellationToken);
            return;
        }

        await TryWriteJsonAsync(ctx.Response, 200, new
        {
            saved = true,
            updatedTargets = saveRes.Value.UpdatedTargets,
            errors = Array.Empty<string>()
        }, cancellationToken);
    }

    private static Dictionary<string, string> ExtractScalarPropertyMap(string targetLabel, JsonElement objEl, List<string> errors)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in objEl.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                map[prop.Name] = prop.Value.GetString() ?? string.Empty;
            else if (prop.Value.ValueKind == JsonValueKind.Number)
                map[prop.Name] = prop.Value.GetRawText();
            else
                errors.Add($"{targetLabel}.{prop.Name}: Value must be a string or number.");
        }
        return map;
    }

    private async Task HandleLayoutResetAsync(HttpListenerContext ctx, CancellationToken cancellationToken)
    {
        var (tooLarge, body) = await ReadRequestBodyBoundedAsync(ctx.Request, MaxLayoutRequestBodyBytes, cancellationToken);
        if (tooLarge)
        {
            await TryWriteJsonAsync(ctx.Response, 413, new { reset = false, errors = new[] { "Request body exceeds 64 KB limit." } }, cancellationToken);
            return;
        }

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

                if (doc.RootElement.TryGetProperty("targetType", out var tEl))
                {
                    if (tEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tEl.GetString()))
                    {
                        await TryWriteJsonAsync(ctx.Response, 400, new { reset = false, errors = new[] { "Field 'targetType' must be a non-empty valid target string ('article', 'mcq', or 'flashcard')." } }, cancellationToken);
                        return;
                    }

                    var tVal = tEl.GetString()!.Trim();
                    if (!LayoutBoundsCatalog.IsValidTargetType(tVal))
                    {
                        await TryWriteJsonAsync(ctx.Response, 400, new { reset = false, errors = new[] { $"Invalid layout target type '{tVal}'." } }, cancellationToken);
                        return;
                    }

                    targetsToReset.Add(tVal.ToLowerInvariant());
                }

                if (doc.RootElement.TryGetProperty("keys", out var kEl))
                {
                    if (kEl.ValueKind != JsonValueKind.Array)
                    {
                        await TryWriteJsonAsync(ctx.Response, 400, new { reset = false, errors = new[] { "Field 'keys' must be a non-empty array of property names." } }, cancellationToken);
                        return;
                    }

                    var parsedKeys = new List<string>();
                    foreach (var item in kEl.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            await TryWriteJsonAsync(ctx.Response, 400, new { reset = false, errors = new[] { "Every entry in 'keys' must be a non-empty property name string." } }, cancellationToken);
                            return;
                        }

                        parsedKeys.Add(item.GetString()!.Trim());
                    }

                    if (parsedKeys.Count == 0)
                    {
                        await TryWriteJsonAsync(ctx.Response, 400, new { reset = false, errors = new[] { "Field 'keys' must contain at least one property name, or omit 'keys' to reset the whole profile." } }, cancellationToken);
                        return;
                    }

                    keysToReset = parsedKeys;
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

        var preErrors = new List<string>();
        if (keysToReset is not null)
        {
            foreach (var target in targetsToReset)
            {
                foreach (var key in keysToReset)
                {
                    if (!LayoutBoundsCatalog.IsValidPropertyForTarget(target, key))
                        preErrors.Add($"Layout property '{key}' is not valid for target '{target}'.");
                }
            }
        }

        if (preErrors.Count > 0)
        {
            await TryWriteJsonAsync(ctx.Response, 400, new { reset = false, errors = preErrors }, cancellationToken);
            return;
        }

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

    private static async Task RunHttpServerLoopAsync(
        HttpListener listener,
        CancellationToken cancellationToken,
        Func<HttpListenerContext, Task<bool>> handleRequestAsync)
    {
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

    private static (HttpListener Listener, string Prefix) StartHttpListenerWithRetry()
    {
        HttpListenerException? lastEx = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var prefix = $"http://127.0.0.1:{WebHostUtilities.FindFreeLoopbackPort()}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return (listener, prefix);
            }
            catch (HttpListenerException ex)
            {
                lastEx = ex;
                listener.Close();
            }
        }

        throw lastEx ?? new HttpListenerException(0, "Failed to bind local preview HTTP listener.");
    }

    private static bool IsValidCsrfToken(HttpListenerRequest request, string expectedToken)
    {
        var supplied = request.Headers["X-Mby-Editor-Token"];
        if (string.IsNullOrWhiteSpace(supplied))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.Trim());
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static async Task<(bool TooLarge, string Body)> ReadRequestBodyBoundedAsync(
        HttpListenerRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > maxBytes)
            return (true, string.Empty);

        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int totalRead = 0;
        while (true)
        {
            int read = await request.InputStream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
                break;

            totalRead += read;
            if (totalRead > maxBytes)
                return (true, string.Empty);

            ms.Write(buffer, 0, read);
        }

        return (false, Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static Task WriteMethodNotAllowedAsync(
        HttpListenerResponse response,
        string allowHeader,
        bool isApi,
        CancellationToken cancellationToken)
    {
        response.AddHeader("Allow", allowHeader);
        return isApi
            ? TryWriteJsonAsync(response, 405, new { error = $"Method not allowed. Allowed: {allowHeader}." }, cancellationToken)
            : TryWriteBytesAsync(response, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes($"Method Not Allowed. Allowed: {allowHeader}"), cancellationToken);
    }

    private static Task TryWriteNoContentAsync(HttpListenerResponse response)
    {
        try
        {
            response.StatusCode = 204;
            response.ContentLength64 = 0;
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // Client disconnected or listener closed.
        }
        return Task.CompletedTask;
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

    private static async Task TryWriteFileStreamAsync(
        HttpListenerResponse response,
        int statusCode,
        string contentType,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentLength64 = fs.Length;
            if (fs.Length > 0)
                await fs.CopyToAsync(response.OutputStream, 81920, cancellationToken);
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Client disconnected or listener closed; avoid crashing the server loop.
        }
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
            case "diagram-explore":
            case "reveal":
                initialComponent = "article-text";
                defaultProfile = "article";
                return true;
            case "callout":
                initialComponent = "callout";
                defaultProfile = "article";
                return true;
            case "table":
                initialComponent = "table";
                defaultProfile = "article";
                return true;
            case "image":
                initialComponent = "image";
                defaultProfile = "article";
                return true;
            case "mcq-question":
            case "mcq":
            case "classify":
            case "match":
            case "numeric":
            case "case":
                initialComponent = "mcq-question";
                defaultProfile = "mcq";
                return true;
            case "mcq-options":
                initialComponent = "mcq-options";
                defaultProfile = "mcq";
                return true;
            case "fill-blank":
                initialComponent = "fill-blank";
                defaultProfile = "mcq";
                return true;
            case "assertion-reason":
                initialComponent = "assertion-reason";
                defaultProfile = "mcq";
                return true;
            case "spot-error":
                initialComponent = "spot-error";
                defaultProfile = "mcq";
                return true;
            case "diff":
                initialComponent = "diff";
                defaultProfile = "mcq";
                return true;
            case "mechanism":
                initialComponent = "mechanism";
                defaultProfile = "mcq";
                return true;
            case "relabel":
                initialComponent = "relabel";
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
}
