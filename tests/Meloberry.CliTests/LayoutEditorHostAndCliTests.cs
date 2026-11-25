using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.CliTests;

public sealed class LayoutEditorHostAndCliTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mby_layout_cli_{Guid.NewGuid():N}");
    private readonly string _appDataDir;

    public LayoutEditorHostAndCliTests()
    {
        Directory.CreateDirectory(_tempDir);
        _appDataDir = Path.Combine(_tempDir, "appdata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class ObservableConsole : IConsole
    {
        private readonly LineCapturingWriter _out;
        private readonly StringWriter _err = new();

        public ObservableConsole()
        {
            _out = new LineCapturingWriter();
        }

        public TextWriter Out => _out;
        public TextWriter Error => _err;
        public TextReader In => TextReader.Null;
        public bool IsInputRedirected => false;

        public Task<string> WaitForFirstLineAsync(TimeSpan timeout) => _out.WaitForFirstLineAsync(timeout);

        private sealed class LineCapturingWriter : TextWriter
        {
            private readonly StringBuilder _buffer = new();
            private readonly TaskCompletionSource<string> _firstLine = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {
                lock (_buffer)
                {
                    _buffer.Append(value);
                    if (value == '\n')
                        _firstLine.TrySetResult(_buffer.ToString().Trim());
                }
            }

            public override void Write(string? value)
            {
                if (value is null) return;
                lock (_buffer)
                {
                    _buffer.Append(value);
                    if (value.Contains('\n'))
                        _firstLine.TrySetResult(_buffer.ToString().Split('\n')[0].Trim());
                }
            }

            public override void WriteLine(string? value)
            {
                lock (_buffer)
                {
                    _buffer.AppendLine(value);
                    _firstLine.TrySetResult(_buffer.ToString().Split('\n')[0].Trim());
                }
            }

            public async Task<string> WaitForFirstLineAsync(TimeSpan timeout)
            {
                using var cts = new CancellationTokenSource(timeout);
                using var reg = cts.Token.Register(() => _firstLine.TrySetCanceled());
                return await _firstLine.Task;
            }
        }
    }

    [Fact]
    public async Task CliLayoutContracts_ValidateTargetsFlagsJsonAndReset()
    {
        // All v2.0 targets work with --no-wait --json
        foreach (var target in new[] { "callout", "table", "fill-blank", "assertion-reason", "spot-error", "diff", "mechanism", "relabel", "flashcard" })
        {
            var c = new TestConsole();
            int exit = await CliApplicationRunner.ExecuteAsync(["layout", "edit", target, "--no-wait", "--json"], c, _appDataDir);
            Assert.Equal(ExitCodes.Success, exit);
            using var doc = JsonDocument.Parse(c.StandardOutput);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(target, doc.RootElement.GetProperty("data").GetProperty("target").GetString());
        }

        // Extra positional args on layout edit fail
        var extraArgsConsole = new TestConsole();
        Assert.Equal(
            ExitCodes.ValidationError,
            await CliApplicationRunner.ExecuteAsync(["layout", "edit", "article", "extra", "--json"], extraArgsConsole, _appDataDir));
        Assert.Contains("\"code\":\"Cli.InvalidArgs\"", extraArgsConsole.StandardError);

        // --no-wait rejected on get, set, reset
        foreach (var args in new[]
        {
            new[] { "layout", "get", "mcq", "--no-wait", "--json" },
            new[] { "layout", "set", "mcq", "optionColumns", "2", "--no-wait", "--json" },
            new[] { "layout", "reset", "mcq", "--no-wait", "--json" }
        })
        {
            var flagConsole = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(args, flagConsole, _appDataDir));
            Assert.Contains("\"code\":\"Cli.UnsupportedOption\"", flagConsole.StandardError);
        }

        // layout set -> layout reset scoped key -> layout reset all
        var setConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["layout", "set", "mcq", "optionColumns", "2", "--json"], setConsole, _appDataDir));

        var setThemeConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["layout", "set", "mcq", "themeMode", "dark", "--json"], setThemeConsole, _appDataDir));

        var resetKeyConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["layout", "reset", "mcq", "optionColumns", "--json"], resetKeyConsole, _appDataDir));

        var getAfterScoped = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["layout", "get", "mcq", "--json"], getAfterScoped, _appDataDir));
        using (var doc = JsonDocument.Parse(getAfterScoped.StandardOutput))
        {
            var props = doc.RootElement.GetProperty("data").GetProperty("properties").EnumerateArray().ToList();
            Assert.Equal("1", props.First(p => p.GetProperty("property").GetString() == "optionColumns").GetProperty("currentValue").GetString());
            Assert.Equal("dark", props.First(p => p.GetProperty("property").GetString() == "themeMode").GetProperty("currentValue").GetString());
        }

        var resetAllConsole = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["layout", "reset", "--json"], resetAllConsole, _appDataDir));

        var getAfterAll = new TestConsole();
        Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["layout", "get", "article", "--json"], getAfterAll, _appDataDir));
        using (var doc = JsonDocument.Parse(getAfterAll.StandardOutput))
        {
            var props = doc.RootElement.GetProperty("data").GetProperty("properties").EnumerateArray().ToList();
            Assert.Equal("berry", props.First(p => p.GetProperty("property").GetString() == "themeMode").GetProperty("currentValue").GetString());
        }
    }

    [Fact]
    public async Task LocalPreviewHost_LayoutEditorHttpApi_EnforcesRoutingCsrfAtomicityAndSafeResets()
    {
        using var provider = CliApplicationRunner.BuildServiceProvider(_appDataDir);
        var host = provider.GetRequiredService<ILocalPreviewHost>();
        var console = new ObservableConsole();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var serverTask = host.ServeLayoutEditorAsync("relabel", console, noWait: false, json: true, silent: false, cts.Token);
        try
        {
            var firstLine = await console.WaitForFirstLineAsync(TimeSpan.FromSeconds(5));
            using var startupDoc = JsonDocument.Parse(firstLine);
            Assert.True(startupDoc.RootElement.GetProperty("success").GetBoolean());
            var dataEl = startupDoc.RootElement.GetProperty("data");
            Assert.Equal("relabel", dataEl.GetProperty("target").GetString());
            Assert.True(dataEl.GetProperty("running").GetBoolean());
            var baseUrl = dataEl.GetProperty("url").GetString()!;

            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            // Routing checks: 200 /, 200 /index.html, 204 /favicon.ico, 404 /api/bounds, 404 /api/unknown, 405 PUT /api/layout
            var rootRes = await http.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, rootRes.StatusCode);
            var html = await rootRes.Content.ReadAsStringAsync();
            Assert.Contains("Meloberry Layout Editor", html);

            var csrfMatch = Regex.Match(html, @"const EDITOR_CSRF_TOKEN = ""([^""]+)"";");
            Assert.True(csrfMatch.Success, "CSRF token was not embedded in editor HTML.");
            var csrfToken = csrfMatch.Groups[1].Value;

            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/index.html")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync("/favicon.ico")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/bounds")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/unknown")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PutAsync("/api/layout", new StringContent("{}"))).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.DeleteAsync("/api/layout/reset")).StatusCode);

            // CSRF enforcement
            var noCsrfSave = await http.PostAsync("/api/layout", new StringContent("""{"profiles":{"mcq":{"optionColumns":"2"}}}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Forbidden, noCsrfSave.StatusCode);

            var noCsrfReset = await http.PostAsync("/api/layout/reset", new StringContent("""{"targetType":"mcq"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Forbidden, noCsrfReset.StatusCode);

            // Helper for authenticated POSTs
            async Task<HttpResponseMessage> PostJsonAsync(string path, string jsonBody)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, path);
                req.Headers.Add("X-Mby-Editor-Token", csrfToken);
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                return await http.SendAsync(req);
            }

            // 413 on oversized > 64KB body
            var huge = "{\"profiles\":{\"mcq\":{\"optionColumns\":\"" + new string('a', 70 * 1024) + "\"}}}";
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostJsonAsync("/api/layout", huge)).StatusCode);

            // 400 (NOT 500) on non-string targetType and malformed profiles
            Assert.Equal(HttpStatusCode.BadRequest, (await PostJsonAsync("/api/layout", """{"targetType":123,"values":{"cardMaxWidthPx":"800"}}""")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PostJsonAsync("/api/layout", """{"profiles":{"mcq":null}}""")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PostJsonAsync("/api/layout", """{"profiles":{}}""")).StatusCode);

            // Atomic validation failure: valid optionColumns + out-of-bounds cardMaxWidthPx must NOT persist optionColumns
            var partialFail = await PostJsonAsync("/api/layout", """{"profiles":{"mcq":{"optionColumns":"2","cardMaxWidthPx":"99999"}}}""");
            Assert.Equal(HttpStatusCode.BadRequest, partialFail.StatusCode);

            async Task<Dictionary<string, Dictionary<string, string>>> GetProfilesAsync()
            {
                var json = await http.GetStringAsync("/api/layout");
                using var d = JsonDocument.Parse(json);
                var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var prof in d.RootElement.GetProperty("profiles").EnumerateObject())
                {
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in prof.Value.EnumerateArray())
                        map[item.GetProperty("property").GetString()!] = item.GetProperty("currentValue").GetString()!;
                    result[prof.Name] = map;
                }
                return result;
            }

            var afterFailProfiles = await GetProfilesAsync();
            Assert.Equal("1", afterFailProfiles["mcq"]["optionColumns"]);

            // Valid multi-profile save persists atomically and syncs globals
            var okSave = await PostJsonAsync("/api/layout", """{"profiles":{"mcq":{"optionColumns":"2","themeMode":"paper","slideTransitionMs":"375"},"article":{"blockSpacingPx":"30"}}}""");
            Assert.Equal(HttpStatusCode.OK, okSave.StatusCode);

            var afterOkProfiles = await GetProfilesAsync();
            Assert.Equal("2", afterOkProfiles["mcq"]["optionColumns"]);
            Assert.Equal("30", afterOkProfiles["article"]["blockSpacingPx"]);
            Assert.Equal("paper", afterOkProfiles["article"]["themeMode"]);
            Assert.Equal("375", afterOkProfiles["flashcard"]["slideTransitionMs"]);

            // Reset safety: invalid targetType / empty keys / unknown keys must return 400 and NOT wipe saved profiles
            Assert.Equal(HttpStatusCode.BadRequest, (await PostJsonAsync("/api/layout/reset", """{"targetType":123}""")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PostJsonAsync("/api/layout/reset", """{"targetType":""}""")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PostJsonAsync("/api/layout/reset", """{"targetType":"mcq","keys":[]}""")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PostJsonAsync("/api/layout/reset", """{"targetType":"mcq","keys":["bogusKey"]}""")).StatusCode);

            var afterBadResets = await GetProfilesAsync();
            Assert.Equal("2", afterBadResets["mcq"]["optionColumns"]);
            Assert.Equal("paper", afterBadResets["article"]["themeMode"]);
            Assert.Equal("30", afterBadResets["article"]["blockSpacingPx"]);

            // Valid scoped key reset resets only optionColumns and preserves paper theme + article blockSpacingPx
            var okScopedReset = await PostJsonAsync("/api/layout/reset", """{"targetType":"mcq","keys":["optionColumns"]}""");
            Assert.Equal(HttpStatusCode.OK, okScopedReset.StatusCode);

            var afterScopedReset = await GetProfilesAsync();
            Assert.Equal("1", afterScopedReset["mcq"]["optionColumns"]);
            Assert.Equal("paper", afterScopedReset["mcq"]["themeMode"]);
            Assert.Equal("30", afterScopedReset["article"]["blockSpacingPx"]);
        }
        finally
        {
            cts.Cancel();
            Assert.Equal(ExitCodes.Success, await serverTask);
        }
    }

    [Fact]
    public async Task LocalPreviewHost_WebAppServer_StreamsImmutableAssetsAndRejectsOversizedAnalysisPost()
    {
        var webDir = Path.Combine(_tempDir, "preview_web");
        Directory.CreateDirectory(Path.Combine(webDir, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webDir, "index.html"), "<!doctype html><html><body>ok</body></html>");
        await File.WriteAllBytesAsync(Path.Combine(webDir, "assets", "diag_12345678.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        using var provider = CliApplicationRunner.BuildServiceProvider(_appDataDir);
        var host = provider.GetRequiredService<ILocalPreviewHost>();
        var console = new ObservableConsole();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var serverTask = host.ServeDirectoryAsync(webDir, console, noWait: false, json: false, silent: false, cts.Token);
        try
        {
            var firstLine = await console.WaitForFirstLineAsync(TimeSpan.FromSeconds(5));
            var urlMatch = Regex.Match(firstLine, @"http://[^\s]+/");
            Assert.True(urlMatch.Success, $"Could not parse preview URL from: {firstLine}");

            using var http = new HttpClient { BaseAddress = new Uri(urlMatch.Value) };
            var assetResp = await http.GetAsync("/assets/diag_12345678.png");
            Assert.Equal(HttpStatusCode.OK, assetResp.StatusCode);
            Assert.Contains("immutable", assetResp.Headers.CacheControl?.ToString() ?? string.Empty);

            var oversized = "{\"report\":\"" + new string('x', 5 * 1024 * 1024 + 1024) + "\"}";
            var postResp = await http.PostAsync("/api/analysis", new StringContent(oversized, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, postResp.StatusCode);
        }
        finally
        {
            cts.Cancel();
            Assert.Equal(ExitCodes.Success, await serverTask);
        }
    }
}
