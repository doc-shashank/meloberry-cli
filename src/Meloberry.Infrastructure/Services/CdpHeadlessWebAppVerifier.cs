using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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

        var (staticPort, cdpPort) = FindFreePortPair();
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

        var httpFailures = new ConcurrentQueue<string>();
        var serverTask = ServeStaticDirectoryAsync(listener, fullDir, httpFailures, ct);

        Process? browserProc = null;
        Task<string>? stdoutDrainTask = null;
        Task<string>? stderrDrainTask = null;
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

            stdoutDrainTask = DrainStreamAsync(browserProc.StandardOutput, maxChars: 0);
            stderrDrainTask = DrainStreamAsync(browserProc.StandardError, maxChars: 4096);

            var wsUrl = await WaitForCdpWebSocketUrlAsync(cdpPort, browserProc, ct);
            if (string.IsNullOrWhiteSpace(wsUrl))
            {
                var stderrSnippet = await TryReadProcessStderrAsync(browserProc, stderrDrainTask);
                return new Error(
                    "Verify.CdpConnectFailed",
                    $"Timed out waiting for headless browser CDP endpoint on port {cdpPort}. {stderrSnippet}".Trim(),
                    ErrorType.Validation,
                    Hint: "If running in a restricted sandbox, retry with --static-only or allow browser launch.");
            }

            var cdpOutcome = await ExecuteCdpInspectionAsync(wsUrl, $"http://127.0.0.1:{staticPort}/index.html", ct);
            try { listener.Stop(); } catch { }
            try { await serverTask.WaitAsync(TimeSpan.FromMilliseconds(400), CancellationToken.None); } catch { }

            slidesChecked = Math.Max(slidesChecked, cdpOutcome.SlidesChecked);
            imagesChecked = Math.Max(imagesChecked, cdpOutcome.ImagesChecked);
            consoleErrors.AddRange(cdpOutcome.ConsoleErrors);
            brokenAssets.AddRange(cdpOutcome.BrokenAssets);
            while (httpFailures.TryDequeue(out var httpErr))
                brokenAssets.Add(httpErr);
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
                if (stdoutDrainTask is not null)
                    try { await stdoutDrainTask.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None); } catch { }
                if (stderrDrainTask is not null)
                    try { await stderrDrainTask.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None); } catch { }
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
        var pending = new ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>();
        var consoleErrors = new ConcurrentQueue<string>();
        var brokenAssets = new ConcurrentQueue<string>();
        var pageLoadedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void FailPending(Exception ex)
        {
            pageLoadedTcs.TrySetException(ex);
            foreach (var key in pending.Keys)
            {
                if (pending.TryRemove(key, out var tcs))
                    tcs.TrySetException(ex);
            }
        }

        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recvLoop = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            var ms = new MemoryStream();
            try
            {
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

                    try
                    {
                        var msgJson = Encoding.UTF8.GetString(ms.ToArray());
                        using var doc = JsonDocument.Parse(msgJson);
                        var root = doc.RootElement.Clone();

                        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int msgId))
                        {
                            if (pending.TryRemove(msgId, out var tcs))
                                tcs.TrySetResult(root);
                        }
                        else if (root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String)
                        {
                            var method = methodEl.GetString();
                            bool hasParams = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object;
                            switch (method)
                            {
                                case "Page.loadEventFired":
                                    pageLoadedTcs.TrySetResult();
                                    break;
                                case "Runtime.exceptionThrown":
                                    if (hasParams && p.TryGetProperty("exceptionDetails", out var ed) && ed.ValueKind == JsonValueKind.Object)
                                    {
                                        var rawText = ed.TryGetProperty("text", out var txtEl) && txtEl.ValueKind == JsonValueKind.String
                                            ? txtEl.GetString()
                                            : null;
                                        var desc = ed.TryGetProperty("exception", out var excEl) &&
                                                   excEl.ValueKind == JsonValueKind.Object &&
                                                   excEl.TryGetProperty("description", out var descEl) &&
                                                   descEl.ValueKind == JsonValueKind.String
                                            ? descEl.GetString()
                                            : null;
                                        string message;
                                        if (!string.IsNullOrWhiteSpace(desc) &&
                                            (string.IsNullOrWhiteSpace(rawText) || string.Equals(rawText.Trim(), "Uncaught", StringComparison.OrdinalIgnoreCase)))
                                        {
                                            message = desc;
                                        }
                                        else
                                        {
                                            message = !string.IsNullOrWhiteSpace(rawText) ? rawText : (desc ?? "Uncaught exception");
                                        }
                                        consoleErrors.Enqueue($"Uncaught exception: {message}");
                                    }
                                    break;
                                case "Runtime.consoleAPICalled":
                                    if (hasParams &&
                                        p.TryGetProperty("type", out var tEl) &&
                                        tEl.ValueKind == JsonValueKind.String &&
                                        tEl.GetString() == "error")
                                    {
                                        var argsText = p.TryGetProperty("args", out var aEl) && aEl.ValueKind == JsonValueKind.Array
                                            ? string.Join(" ", aEl.EnumerateArray().Select(a => a.TryGetProperty("value", out var v) ? v.ToString() : a.ToString()))
                                            : "console.error";
                                        consoleErrors.Enqueue($"console.error: {argsText}");
                                    }
                                    break;
                                case "Log.entryAdded":
                                    if (hasParams &&
                                        p.TryGetProperty("entry", out var le) &&
                                        le.ValueKind == JsonValueKind.Object &&
                                        le.TryGetProperty("level", out var lvl) &&
                                        lvl.ValueKind == JsonValueKind.String &&
                                        lvl.GetString() == "error")
                                    {
                                        var text = le.TryGetProperty("text", out var txtEl) && txtEl.ValueKind == JsonValueKind.String
                                            ? txtEl.GetString()
                                            : "Browser error";
                                        consoleErrors.Enqueue($"Log.error: {text}");
                                    }
                                    break;
                                case "Network.loadingFailed":
                                    var errText = hasParams && p.TryGetProperty("errorText", out var etEl) && etEl.ValueKind == JsonValueKind.String
                                        ? etEl.GetString()
                                        : "Failed";
                                    brokenAssets.Enqueue($"Network load failed: {errText}");
                                    break;
                                case "Network.responseReceived":
                                    if (hasParams &&
                                        p.TryGetProperty("response", out var resp) &&
                                        resp.ValueKind == JsonValueKind.Object &&
                                        resp.TryGetProperty("status", out var st) &&
                                        st.TryGetInt32(out int statusCode) &&
                                        statusCode >= 400)
                                    {
                                        var u = resp.TryGetProperty("url", out var uEl) && uEl.ValueKind == JsonValueKind.String
                                            ? uEl.GetString()
                                            : "";
                                        if (!string.IsNullOrWhiteSpace(u) && u.EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                                            break;
                                        brokenAssets.Enqueue($"HTTP {statusCode}: {u}");
                                    }
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        consoleErrors.Enqueue($"CDP event parse error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (recvCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                FailPending(ex);
            }
            finally
            {
                if (!recvCts.IsCancellationRequested)
                    FailPending(new IOException("CDP WebSocket closed before receiving expected response."));
            }
        }, recvCts.Token);

        async Task<JsonElement> SendCdpAsync(string method, object? @params = null)
        {
            int id = Interlocked.Increment(ref nextMsgId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            try
            {
                var payload = JsonSerializer.Serialize(new { id, method, @params });
                await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
                return await tcs.Task.WaitAsync(ct);
            }
            catch
            {
                pending.TryRemove(id, out _);
                throw;
            }
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

              // Eliminate slide/quiz track transition lag so layout measurements never occur mid-animation
              const verifyStyle = document.createElement('style');
              verifyStyle.textContent = '.mby-slides-track, .mby-quiz-track, .mby-quiz-viewport { transition-duration: 0ms !important; }';
              document.head.appendChild(verifyStyle);

              startBtn.click();
              await sleep(100);

              const imgsSeen = new Set();
              const isHorizontallyScrollable = el => {
                const ox = getComputedStyle(el).overflowX;
                return ox === 'auto' || ox === 'scroll';
              };
              const checkOverflowOnElement = (el, cardRect, sIdx, qLabel) => {
                const tag = el.tagName?.toLowerCase();
                if (!tag || tag === 'table' || tag === 'pre' || el.classList?.contains('math') || isHorizontallyScrollable(el)) return;
                const r = el.getBoundingClientRect();
                if (r.width <= 0 || r.height <= 0) return;
                if (r.right > cardRect.right + 4 || r.left < cardRect.left - 4) {
                  out.overflowIssues.push(`Horizontal overflow on slide ${sIdx + 1}${qLabel}: ${tag}.${el.className}`);
                  return;
                }
                if (el.clientWidth > 0 && el.scrollWidth > el.clientWidth + 4 && !el.querySelector?.('table, pre, .math')) {
                  out.overflowIssues.push(`Horizontal overflow on slide ${sIdx + 1}${qLabel}: ${tag}.${el.className}`);
                }
              };
              const checkCardOverflow = (card, sIdx, qLabel) => {
                if (!card) return;
                const cardRect = card.getBoundingClientRect();
                if (cardRect.width <= 0) return;
                const children = card.querySelectorAll('.mby-card-body > *, .mby-article-blocks > *, .mby-flashcard-face.is-active .mby-flashcard-body > *');
                for (const child of children) {
                  const tag = child.tagName?.toLowerCase();
                  if (tag === 'table' || tag === 'pre' || child.classList?.contains('math')) continue;
                  if (child.parentElement?.classList?.contains('mby-article-blocks') && tag === 'div' && child.querySelector?.('table, pre, .math')) {
                    for (const sub of child.children)
                      checkOverflowOnElement(sub, cardRect, sIdx, qLabel);
                    continue;
                  }
                  checkOverflowOnElement(child, cardRect, sIdx, qLabel);
                }
                for (const optBtn of card.querySelectorAll('.mby-option-btn')) {
                  checkOverflowOnElement(optBtn, cardRect, sIdx, qLabel);
                }
              };

              const checkSlide = async (sIdx, qIdx, qLabel) => {
                const track = document.getElementById('slidesTrack');
                const slideEl = track?.children?.[sIdx];
                if (!slideEl) return;
                const scopeEl = (qIdx !== null)
                  ? (slideEl.querySelectorAll('.mby-quiz-q-slide')[qIdx] || slideEl)
                  : slideEl;

                const imgs = Array.from(scopeEl.querySelectorAll('img'));
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
                const kErrors = scopeEl.querySelectorAll('.katex-error');
                if (kErrors.length > 0)
                  out.katexErrors.push(`${kErrors.length} KaTeX parse error(s) on slide ${sIdx + 1}${qLabel}`);

                const card = scopeEl.querySelector('.mby-card, .mby-flashcard-scene');
                checkCardOverflow(card, sIdx, qLabel);

                // Reveal MCQ explanation box if present and verify its layout as well
                const expBox = card?.querySelector('.mby-explanation-box.hidden');
                const checkBtn = card?.querySelector('.mby-check-btn');
                if (expBox && checkBtn) {
                  checkBtn.click();
                  await sleep(30);
                  checkCardOverflow(card, sIdx, `${qLabel} (Explanation)`);
                }

                // Flip flashcards to verify back-face layout as well
                if (card?.classList?.contains('mby-flashcard-scene')) {
                  const backFace = card.querySelector('.mby-flashcard-back');
                  if (backFace && !backFace.classList.contains('is-active')) {
                    card.click();
                    const deadline = Date.now() + 1200;
                    while (Date.now() < deadline && (card.classList.contains('is-flipping') || !backFace.classList.contains('is-active'))) {
                      await sleep(30);
                    }
                    await sleep(20);
                    checkCardOverflow(card, sIdx, `${qLabel} (Back)`);
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
                    await checkSlide(i, q, ` Q${q + 1}`);
                  }
                } else {
                  await checkSlide(i, null, '');
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

        recvCts.Cancel();
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        try { await recvLoop.WaitAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None); } catch { }

        int slidesChecked = 0;
        int imagesChecked = 0;
        var finalConsoleErrors = new List<string>();
        while (consoleErrors.TryDequeue(out var ce)) finalConsoleErrors.Add(ce);
        var finalBrokenAssets = new List<string>();
        while (brokenAssets.TryDequeue(out var ba)) finalBrokenAssets.Add(ba);
        var overflowIssues = new List<string>();
        var katexErrors = new List<string>();

        if (evalResp.TryGetProperty("result", out var r1) &&
            r1.TryGetProperty("result", out var r2) &&
            r2.TryGetProperty("value", out var val))
        {
            if (val.TryGetProperty("slidesChecked", out var sc) && sc.TryGetInt32(out int scVal)) slidesChecked = scVal;
            if (val.TryGetProperty("imagesChecked", out var ic) && ic.TryGetInt32(out int icVal)) imagesChecked = icVal;
            AppendStrings(val, "brokenAssets", finalBrokenAssets);
            AppendStrings(val, "overflowIssues", overflowIssues);
            AppendStrings(val, "katexErrors", katexErrors);
            AppendStrings(val, "consoleErrors", finalConsoleErrors);
        }
        else if (evalResp.TryGetProperty("result", out var errOuter) &&
                 errOuter.TryGetProperty("exceptionDetails", out var exDet))
        {
            finalConsoleErrors.Add($"Verification script exception: {exDet}");
        }

        return new CdpInspectionOutcome(slidesChecked, imagesChecked, finalConsoleErrors, finalBrokenAssets, overflowIssues, katexErrors);
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

    private static async Task<string> DrainStreamAsync(StreamReader reader, int maxChars)
    {
        var sb = maxChars > 0 ? new StringBuilder() : null;
        var buffer = new char[1024];
        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0) break;
                if (sb is not null && sb.Length < maxChars)
                {
                    int take = Math.Min(read, maxChars - sb.Length);
                    sb.Append(buffer, 0, take);
                }
            }
        }
        catch { }
        return sb?.ToString() ?? string.Empty;
    }

    private static async Task<string> TryReadProcessStderrAsync(Process proc, Task<string>? stderrDrainTask)
    {
        try
        {
            if (stderrDrainTask is not null)
            {
                if (proc.HasExited)
                    return await stderrDrainTask.WaitAsync(TimeSpan.FromMilliseconds(500));
                if (stderrDrainTask.IsCompleted)
                    return await stderrDrainTask;
            }
        }
        catch { }
        return string.Empty;
    }

    private static async Task ServeStaticDirectoryAsync(HttpListener listener, string rootDir, ConcurrentQueue<string> httpFailures, CancellationToken ct)
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
                    httpFailures.Enqueue($"HTTP 404: /{relPath}");
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

    private static (int Port1, int Port2) FindFreePortPair()
    {
        using var tcp1 = new TcpListener(IPAddress.Loopback, 0);
        tcp1.Start();
        using var tcp2 = new TcpListener(IPAddress.Loopback, 0);
        tcp2.Start();
        return (((IPEndPoint)tcp1.LocalEndpoint).Port, ((IPEndPoint)tcp2.LocalEndpoint).Port);
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
