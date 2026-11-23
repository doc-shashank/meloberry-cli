using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;

namespace Meloberry.Infrastructure.Services;

public sealed class CdpHeadlessWebAppVerifier : IHeadlessWebAppVerifier
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp",
        [".woff2"] = "font/woff2",
        [".woff"] = "font/woff",
        [".ttf"] = "font/ttf"
    };

    public async Task<Result<WebAppVerificationReport>> VerifyAsync(
        string webDirectory,
        bool staticOnly = false,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var fullDir = Path.GetFullPath(webDirectory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var indexPath = Path.Combine(fullDir, "index.html");
        if (!Directory.Exists(fullDir) || !File.Exists(indexPath))
        {
            return new Error(
                "Analysis.TargetNotFound",
                $"WebApp directory '{webDirectory}' does not exist or is missing index.html.",
                ErrorType.NotFound);
        }

        var consoleErrors = new List<string>();
        var brokenAssets = new List<string>();
        var overflowIssues = new List<string>();
        var katexErrors = new List<string>();
        int slidesChecked = 0;
        int imagesChecked = 0;

        // 1. Always run fast static bundle checks first
        var staticRes = await RunStaticCheckAsync(fullDir, brokenAssets, consoleErrors, cancellationToken);
        slidesChecked = staticRes.SlideCount;
        imagesChecked = staticRes.ImageCount;

        if (staticOnly)
        {
            bool passedStatic = brokenAssets.Count == 0 && consoleErrors.Count == 0;
            var staticReport = new WebAppVerificationReport(
                passedStatic,
                fullDir,
                "static-verifier",
                slidesChecked,
                imagesChecked,
                consoleErrors,
                brokenAssets,
                overflowIssues,
                katexErrors,
                await TryWriteReportFileAsync(fullDir, new WebAppVerificationReport(
                    passedStatic, fullDir, "static-verifier", slidesChecked, imagesChecked,
                    consoleErrors, brokenAssets, overflowIssues, katexErrors, null), cancellationToken));
            return staticReport;
        }

        // 2. Locate Chromium / Edge binary for headless CDP verification
        var browserPath = ResolveBrowserExecutable();
        if (string.IsNullOrWhiteSpace(browserPath))
        {
            return new Error(
                "Verify.BrowserNotFound",
                "Could not locate Microsoft Edge or Google Chrome for headless verification.",
                ErrorType.NotFound,
                Hint: "Set MBY_BROWSER_PATH to msedge.exe/chrome.exe or pass --static-only.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 180)));
        var ct = timeoutCts.Token;

        int staticPort = FindFreePort();
        int cdpPort = FindFreePort();
        var tempProfileDir = Path.Combine(Path.GetTempPath(), $"mby_cdp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempProfileDir);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{staticPort}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(tempProfileDir);
            return new Error("Verify.ServerStartFailed", $"Failed to start ephemeral verification server: {ex.Message}", ErrorType.Validation);
        }

        var httpFailures = new List<string>();
        var serverTask = ServeStaticDirectoryAsync(listener, fullDir, httpFailures, ct);

        Process? browserProc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = browserPath,
                Arguments = $"--headless=new --disable-gpu --no-first-run --no-default-browser-check --disable-extensions --remote-debugging-port={cdpPort} --user-data-dir=\"{tempProfileDir}\" --window-size=1280,900 \"about:blank\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            browserProc = Process.Start(psi);
            if (browserProc is null)
                return new Error("Verify.BrowserLaunchFailed", $"Failed to start browser process '{browserPath}'.", ErrorType.Validation);

            var wsUrl = await WaitForCdpWebSocketUrlAsync(cdpPort, browserProc, ct);
            if (string.IsNullOrWhiteSpace(wsUrl))
            {
                var stderrSnippet = await TryReadProcessStderrAsync(browserProc);
                return new Error(
                    "Verify.CdpConnectFailed",
                    $"Timed out waiting for headless browser CDP endpoint on port {cdpPort}. {stderrSnippet}".Trim(),
                    ErrorType.Validation,
                    Hint: "If running in a restricted sandbox, retry with --static-only or allow browser launch.");
            }

            var cdpOutcome = await ExecuteCdpInspectionAsync(wsUrl, $"http://127.0.0.1:{staticPort}/index.html", ct);
            slidesChecked = Math.Max(slidesChecked, cdpOutcome.SlidesChecked);
            imagesChecked = Math.Max(imagesChecked, cdpOutcome.ImagesChecked);
            consoleErrors.AddRange(cdpOutcome.ConsoleErrors);
            brokenAssets.AddRange(cdpOutcome.BrokenAssets);
            brokenAssets.AddRange(httpFailures);
            overflowIssues.AddRange(cdpOutcome.OverflowIssues);
            katexErrors.AddRange(cdpOutcome.KatexErrors);

            var distinctConsole = consoleErrors.Distinct(StringComparer.Ordinal).ToList();
            var distinctBroken = brokenAssets.Distinct(StringComparer.Ordinal).ToList();
            var distinctOverflow = overflowIssues.Distinct(StringComparer.Ordinal).ToList();
            var distinctKatex = katexErrors.Distinct(StringComparer.Ordinal).ToList();

            bool passed = distinctConsole.Count == 0 && distinctBroken.Count == 0 && distinctOverflow.Count == 0 && distinctKatex.Count == 0;
            var engineName = $"cdp:{Path.GetFileNameWithoutExtension(browserPath)}";
            var preliminary = new WebAppVerificationReport(
                passed, fullDir, engineName, slidesChecked, imagesChecked,
                distinctConsole, distinctBroken, distinctOverflow, distinctKatex, null);
            var reportPath = await TryWriteReportFileAsync(fullDir, preliminary, cancellationToken);
            return preliminary with { ReportPath = reportPath };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new Error("Verify.Timeout", $"Headless browser verification timed out after {timeoutSeconds}s.", ErrorType.Validation);
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { await serverTask.WaitAsync(TimeSpan.FromMilliseconds(400), CancellationToken.None); } catch { }
            if (browserProc is not null)
            {
                try
                {
                    if (!browserProc.HasExited)
                        browserProc.Kill(entireProcessTree: true);
                }
                catch { }
                browserProc.Dispose();
            }
            TryDeleteDirectory(tempProfileDir);
        }
    }

    private static async Task<(int SlideCount, int ImageCount)> RunStaticCheckAsync(
        string fullDir,
        List<string> brokenAssets,
        List<string> consoleErrors,
        CancellationToken ct)
    {
        var html = await File.ReadAllTextAsync(Path.Combine(fullDir, "index.html"), ct);
        var stylesPath = Path.Combine(fullDir, "styles.css");
        var appJsPath = Path.Combine(fullDir, "app.js");

        if (!File.Exists(stylesPath) || new FileInfo(stylesPath).Length == 0)
            brokenAssets.Add("Missing or empty styles.css");
        if (!File.Exists(appJsPath) || new FileInfo(appJsPath).Length == 0)
        {
            brokenAssets.Add("Missing or empty app.js");
            return (0, 0);
        }

        if (html.Contains("katex/katex.min.css", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(fullDir, "katex", "katex.min.css")))
                brokenAssets.Add("Missing katex/katex.min.css referenced by index.html");
            if (!File.Exists(Path.Combine(fullDir, "katex", "katex.min.js")))
                brokenAssets.Add("Missing katex/katex.min.js referenced by index.html");
        }

        var appJs = await File.ReadAllTextAsync(appJsPath, ct);
        const string marker = "const DATA = ";
        int start = appJs.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            consoleErrors.Add("app.js does not declare 'const DATA = ...'");
            return (0, 0);
        }

        start += marker.Length;
        int endLine = appJs.IndexOf(";\n", start, StringComparison.Ordinal);
        if (endLine < 0) endLine = appJs.IndexOf(";\r\n", start, StringComparison.Ordinal);
        if (endLine < 0)
        {
            consoleErrors.Add("Unable to locate end of DATA JSON payload in app.js");
            return (0, 0);
        }

        var jsonSlice = appJs[start..endLine];
        int slideCount = 0;
        int imgCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(jsonSlice);
            if (!doc.RootElement.TryGetProperty("slides", out var slidesEl) || slidesEl.ValueKind != JsonValueKind.Array)
            {
                consoleErrors.Add("DATA payload is missing 'slides' array.");
                return (0, 0);
            }
            slideCount = slidesEl.GetArrayLength();
            CollectImagePathsRecursive(slidesEl, p =>
            {
                imgCount++;
                if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return;
                var unescaped = string.Join('/', p.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString));
                if (!PathConfinement.TryResolveInside(fullDir, unescaped, out var assetFull) || !File.Exists(assetFull) || new FileInfo(assetFull).Length == 0)
                    brokenAssets.Add($"Missing or empty asset '{p}'");
            });
        }
        catch (JsonException ex)
        {
            consoleErrors.Add($"DATA JSON parse failure in app.js: {ex.Message}");
        }

        return (slideCount, imgCount);
    }

    private static void CollectImagePathsRecursive(JsonElement element, Action<string> onImagePath)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "imagePath", StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) onImagePath(val);
                    }
                    else
                    {
                        CollectImagePathsRecursive(prop.Value, onImagePath);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectImagePathsRecursive(item, onImagePath);
                break;
        }
    }

    private static async Task<CdpInspectionOutcome> ExecuteCdpInspectionAsync(string wsUrl, string targetUrl, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);

        int nextMsgId = 1;
        var pending = new Dictionary<int, TaskCompletionSource<JsonElement>>();
        var consoleErrors = new List<string>();
        var brokenAssets = new List<string>();
        var pageLoadedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recvLoop = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            var ms = new MemoryStream();
            while (ws.State == WebSocketState.Open && !recvCts.IsCancellationRequested)
            {
                WebSocketReceiveResult res;
                ms.SetLength(0);
                do
                {
                    res = await ws.ReceiveAsync(buffer, recvCts.Token);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, res.Count);
                } while (!res.EndOfMessage);

                var msgJson = Encoding.UTF8.GetString(ms.ToArray());
                using var doc = JsonDocument.Parse(msgJson);
                var root = doc.RootElement.Clone();

                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int msgId))
                {
                    if (pending.Remove(msgId, out var tcs))
                        tcs.TrySetResult(root);
                }
                else if (root.TryGetProperty("method", out var methodEl))
                {
                    var method = methodEl.GetString();
                    root.TryGetProperty("params", out var p);
                    switch (method)
                    {
                        case "Page.loadEventFired":
                            pageLoadedTcs.TrySetResult();
                            break;
                        case "Runtime.exceptionThrown":
                            if (p.TryGetProperty("exceptionDetails", out var ed))
                                consoleErrors.Add($"Uncaught exception: {ed.GetProperty("text").GetString()}");
                            break;
                        case "Runtime.consoleAPICalled":
                            if (p.TryGetProperty("type", out var tEl) && tEl.GetString() == "error")
                            {
                                var argsText = p.TryGetProperty("args", out var aEl)
                                    ? string.Join(" ", aEl.EnumerateArray().Select(a => a.TryGetProperty("value", out var v) ? v.ToString() : a.ToString()))
                                    : "console.error";
                                consoleErrors.Add($"console.error: {argsText}");
                            }
                            break;
                        case "Log.entryAdded":
                            if (p.TryGetProperty("entry", out var le) &&
                                le.TryGetProperty("level", out var lvl) &&
                                lvl.GetString() == "error")
                            {
                                var text = le.TryGetProperty("text", out var txtEl) ? txtEl.GetString() : "Browser error";
                                consoleErrors.Add($"Log.error: {text}");
                            }
                            break;
                        case "Network.loadingFailed":
                            var errText = p.TryGetProperty("errorText", out var etEl) ? etEl.GetString() : "Failed";
                            brokenAssets.Add($"Network load failed: {errText}");
                            break;
                        case "Network.responseReceived":
                            if (p.TryGetProperty("response", out var resp) &&
                                resp.TryGetProperty("status", out var st) &&
                                st.GetInt32() >= 400)
                            {
                                var u = resp.TryGetProperty("url", out var uEl) ? uEl.GetString() : "";
                                if (!string.IsNullOrWhiteSpace(u) && u.EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                                    break;
                                brokenAssets.Add($"HTTP {st.GetInt32()}: {u}");
                            }
                            break;
                    }
                }
            }
        }, recvCts.Token);

        async Task<JsonElement> SendCdpAsync(string method, object? @params = null)
        {
            int id = nextMsgId++;
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            var payload = JsonSerializer.Serialize(new { id, method, @params });
            await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
            return await tcs.Task.WaitAsync(ct);
        }

        await SendCdpAsync("Page.enable");
        await SendCdpAsync("Runtime.enable");
        await SendCdpAsync("Log.enable");
        await SendCdpAsync("Network.enable");
        await SendCdpAsync("Page.navigate", new { url = targetUrl });
        await pageLoadedTcs.Task.WaitAsync(ct);

        const string verifyScript = """
            (async () => {
              const sleep = ms => new Promise(r => setTimeout(r, ms));
              const out = { slidesChecked: 0, imagesChecked: 0, brokenAssets: [], overflowIssues: [], katexErrors: [], consoleErrors: [] };
              if (typeof DATA !== 'object' || !Array.isArray(DATA.slides)) {
                out.consoleErrors.push('DATA.slides not found on page.');
                return out;
              }
              const startBtn = document.getElementById('startSessionBtn');
              if (!startBtn) { out.consoleErrors.push('#startSessionBtn missing'); return out; }
              startBtn.click();
              await sleep(100);

              const imgsSeen = new Set();
              const checkSlide = async (sIdx, qLabel) => {
                const track = document.getElementById('slidesTrack');
                const slideEl = track?.children?.[sIdx];
                if (!slideEl) return;
                const imgs = Array.from(slideEl.querySelectorAll('img'));
                for (const img of imgs) {
                  if (!img.complete) {
                    await Promise.race([
                      new Promise(res => { img.addEventListener('load', res, { once: true }); img.addEventListener('error', res, { once: true }); }),
                      sleep(1500)
                    ]);
                  }
                  const src = img.getAttribute('src') || img.src || '';
                  if (!imgsSeen.has(src)) { imgsSeen.add(src); out.imagesChecked += 1; }
                  if (!img.complete || img.naturalWidth === 0)
                    out.brokenAssets.push(`Image failed to decode on slide ${sIdx + 1}${qLabel}: ${src}`);
                }
                const kErrors = slideEl.querySelectorAll('.katex-error');
                if (kErrors.length > 0)
                  out.katexErrors.push(`${kErrors.length} KaTeX parse error(s) on slide ${sIdx + 1}${qLabel}`);

                const card = slideEl.querySelector('.mby-card, .mby-flashcard-scene');
                if (card) {
                  const cardRect = card.getBoundingClientRect();
                  const children = card.querySelectorAll('.mby-card-body > *, .mby-article-blocks > *');
                  for (const child of children) {
                    const tag = child.tagName?.toLowerCase();
                    if (tag === 'table' || tag === 'pre' || child.classList?.contains('math') || child.querySelector?.('table')) continue;
                    const r = child.getBoundingClientRect();
                    if (r.width > 0 && (r.right > cardRect.right + 4 || r.left < cardRect.left - 4))
                      out.overflowIssues.push(`Horizontal overflow on slide ${sIdx + 1}${qLabel}: ${tag}.${child.className}`);
                  }
                }
              };

              for (let i = 0; i < DATA.slides.length; i++) {
                if (typeof goToSlide === 'function') goToSlide(i);
                await sleep(60);
                out.slidesChecked += 1;
                const slide = DATA.slides[i];
                if (slide?.kind === 'quiz' && Array.isArray(slide.questions) && slide.questions.length > 0) {
                  const pills = document.getElementById('slidesTrack')?.children?.[i]?.querySelectorAll('.mby-q-pill') || [];
                  for (let q = 0; q < pills.length; q++) {
                    pills[q].click();
                    await sleep(45);
                    await checkSlide(i, ` Q${q + 1}`);
                  }
                } else {
                  await checkSlide(i, '');
                }
              }

              // Test search modal open & live filter
              const searchBtn = document.getElementById('topbarSearchBtn');
              const searchModal = document.getElementById('searchModal');
              const searchInput = document.getElementById('searchInput');
              if (searchBtn && searchModal && searchInput) {
                searchBtn.click();
                await sleep(40);
                if (searchModal.classList.contains('hidden'))
                  out.consoleErrors.push('Search modal failed to open on button click.');
                searchInput.value = 'a';
                searchInput.dispatchEvent(new Event('input', { bubbles: true }));
                await sleep(40);
                document.getElementById('searchCloseBtn')?.click();
              } else {
                out.consoleErrors.push('Search UI elements missing.');
              }
              return out;
            })()
            """;

        var evalResp = await SendCdpAsync("Runtime.evaluate", new
        {
            expression = verifyScript,
            awaitPromise = true,
            returnByValue = true
        });

        int slidesChecked = 0;
        int imagesChecked = 0;
        var overflowIssues = new List<string>();
        var katexErrors = new List<string>();

        if (evalResp.TryGetProperty("result", out var r1) &&
            r1.TryGetProperty("result", out var r2) &&
            r2.TryGetProperty("value", out var val))
        {
            if (val.TryGetProperty("slidesChecked", out var sc)) slidesChecked = sc.GetInt32();
            if (val.TryGetProperty("imagesChecked", out var ic)) imagesChecked = ic.GetInt32();
            AppendStrings(val, "brokenAssets", brokenAssets);
            AppendStrings(val, "overflowIssues", overflowIssues);
            AppendStrings(val, "katexErrors", katexErrors);
            AppendStrings(val, "consoleErrors", consoleErrors);
        }
        else if (evalResp.TryGetProperty("result", out var errOuter) &&
                 errOuter.TryGetProperty("exceptionDetails", out var exDet))
        {
            consoleErrors.Add($"Verification script exception: {exDet}");
        }

        recvCts.Cancel();
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        try { await recvLoop.WaitAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None); } catch { }

        return new CdpInspectionOutcome(slidesChecked, imagesChecked, consoleErrors, brokenAssets, overflowIssues, katexErrors);
    }

    private static void AppendStrings(JsonElement obj, string propName, List<string> target)
    {
        if (!obj.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) target.Add(s);
        }
    }

    private static async Task<string?> TryWriteReportFileAsync(string webDir, WebAppVerificationReport report, CancellationToken ct)
    {
        try
        {
            var reportsDir = Path.Combine(webDir, "reports");
            Directory.CreateDirectory(reportsDir);
            var reportPath = Path.Combine(reportsDir, "verify.latest.json");
            var withPath = report with { ReportPath = reportPath };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(withPath, ReportJsonOptions), ct);
            return reportPath;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveBrowserExecutable()
    {
        var envPath = Environment.GetEnvironmentVariable("MBY_BROWSER_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath.Trim()))
            return envPath.Trim();

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] candidates =
        [
            Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(localApp, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localApp, "Google", "Chrome", "Application", "chrome.exe")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<string?> WaitForCdpWebSocketUrlAsync(int cdpPort, Process proc, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && !proc.HasExited)
        {
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{cdpPort}/json/list", ct);
                using var doc = JsonDocument.Parse(json);
                foreach (var target in doc.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out var t) && t.GetString() == "page" &&
                        target.TryGetProperty("webSocketDebuggerUrl", out var ws))
                    {
                        var url = ws.GetString();
                        if (!string.IsNullOrWhiteSpace(url)) return url;
                    }
                }
            }
            catch { }
            await Task.Delay(120, ct);
        }
        return null;
    }

    private static async Task<string> TryReadProcessStderrAsync(Process proc)
    {
        try
        {
            if (proc.HasExited)
                return await proc.StandardError.ReadToEndAsync();
        }
        catch { }
        return string.Empty;
    }

    private static async Task ServeStaticDirectoryAsync(HttpListener listener, string rootDir, List<string> httpFailures, CancellationToken ct)
    {
        var fullRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (listener.IsListening && !ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            try
            {
                var relPath = (ctx.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
                if (string.IsNullOrWhiteSpace(relPath)) relPath = "index.html";
                if (string.Equals(relPath, "favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                    continue;
                }
                relPath = string.Join('/', relPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString));

                if (!PathConfinement.TryResolveInside(fullRoot, relPath, out var filePath) || !File.Exists(filePath))
                {
                    ctx.Response.StatusCode = 404;
                    httpFailures.Add($"HTTP 404: /{relPath}");
                    ctx.Response.Close();
                    continue;
                }

                var ext = Path.GetExtension(filePath);
                ctx.Response.ContentType = MimeTypes.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
                var bytes = await File.ReadAllBytesAsync(filePath, ct);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Close(); } catch { }
            }
        }
    }

    private static int FindFreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        return ((IPEndPoint)tcp.LocalEndpoint).Port;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private sealed record CdpInspectionOutcome(
        int SlidesChecked,
        int ImagesChecked,
        List<string> ConsoleErrors,
        List<string> BrokenAssets,
        List<string> OverflowIssues,
        List<string> KatexErrors);
}
