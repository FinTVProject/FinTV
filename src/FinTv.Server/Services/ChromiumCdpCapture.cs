using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Captures an internal WeatherStar page as JPEG frames over Chrome DevTools Protocol.
/// Does not use Playwright and does not publish a kiosk website.
/// </summary>
public sealed class ChromiumCdpCapture : IAsyncDisposable
{
    private readonly ILogger _logger;
    private Process? _chromium;
    private ClientWebSocket? _socket;
    private long _nextId = 1;
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private CancellationTokenSource? _readCts;
    private readonly StringBuilder _capturedOutput = new();
    private const int MaxCapturedOutput = 8000;

    public ChromiumCdpCapture(ILogger logger)
    {
        _logger = logger;
    }

    public static string? FindChromium()
    {
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("CHROMIUM_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured.Trim());
        }

        candidates.Add("/usr/local/bin/fintv-chromium");
        candidates.Add("/usr/lib/chromium/chromium");
        candidates.Add("/usr/lib/chromium-browser/chromium");
        candidates.Add("/opt/google/chrome/chrome");
        foreach (var name in new[] { "chromium", "google-chrome", "google-chrome-stable", "chrome", "chromium-browser" })
        {
            var found = FindOnPath(name);
            if (found is not null)
            {
                candidates.Add(found);
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (IsUsableChromium(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task StartAsync(string pageUrl, int width, int height, CancellationToken cancellationToken)
    {
        var chrome = FindChromium() ?? throw new FileNotFoundException(
            "Chromium was not found. Install the chromium package (not the Ubuntu snap stub) or set CHROMIUM_PATH to the real binary.");
        _logger.LogInformation("Starting WeatherStar Chromium from {Path}", chrome);
        var port = GetFreeTcpPort();
        var userData = Path.Combine(Path.GetTempPath(), "fintv-chrome-" + port);
        Directory.CreateDirectory(userData);
        _capturedOutput.Clear();
        _chromium = Process.Start(new ProcessStartInfo
        {
            FileName = chrome,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "--headless",
                "--disable-gpu",
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--hide-scrollbars",
                "--mute-audio",
                "--no-first-run",
                "--disable-extensions",
                "--disable-background-networking",
                "--remote-allow-origins=*",
                "--user-data-dir=" + userData,
                "--remote-debugging-address=127.0.0.1",
                "--remote-debugging-port=" + port,
                "--window-size=" + width + "," + height,
                "about:blank"
            }
        }) ?? throw new InvalidOperationException("Failed to start Chromium.");

        _chromium.OutputDataReceived += (_, args) => CaptureProcessOutput(args.Data);
        _chromium.ErrorDataReceived += (_, args) => CaptureProcessOutput(args.Data);
        _chromium.BeginOutputReadLine();
        _chromium.BeginErrorReadLine();

        await Task.Delay(400, cancellationToken);
        if (_chromium.HasExited)
        {
            throw new InvalidOperationException(
                "Chromium exited immediately. Exit code " + _chromium.ExitCode + "." + FormatCapturedOutput());
        }

        var wsUrl = await WaitForPageDebuggerUrlAsync(port, pageUrl, cancellationToken);
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri(wsUrl), cancellationToken);
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => ReadLoopAsync(_readCts.Token));

        await CallAsync("Page.enable", cancellationToken: cancellationToken);
        await CallAsync("Runtime.enable", cancellationToken: cancellationToken);
        await CallAsync(
            "Emulation.setDeviceMetricsOverride",
            new { width, height, deviceScaleFactor = 1, mobile = false },
            cancellationToken);
        await CallAsync("Page.bringToFront", cancellationToken: cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        await TryStartPlaybackAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }

    public async Task<byte[]> CaptureJpegAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync(
            "Page.captureScreenshot",
            new { format = "jpeg", quality = 80 },
            cancellationToken);
        var data = result.GetProperty("data").GetString();
        if (string.IsNullOrWhiteSpace(data))
        {
            throw new InvalidOperationException("Chromium screenshot was empty.");
        }

        return Convert.FromBase64String(data);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _readCts?.Cancel();
        }
        catch
        {
            // ignored
        }

        if (_socket is not null)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch
            {
                // ignored
            }

            _socket.Dispose();
        }

        try
        {
            if (_chromium is { HasExited: false })
            {
                _chromium.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }

        _chromium?.Dispose();
    }

    private async Task TryStartPlaybackAsync(CancellationToken cancellationToken)
    {
        const string script = """
(() => {
  const labels = ['GO', 'Press Here', 'Play'];
  const buttons = Array.from(document.querySelectorAll('button, [role=button], .play-button, #btn-kiosk'));
  for (const el of buttons) {
    const text = (el.innerText || el.textContent || '').trim();
    if (labels.some(l => text.toUpperCase().includes(l.toUpperCase()))) {
      el.click();
      return text;
    }
  }
  document.body?.click();
  return 'body';
})()
""";
        try
        {
            await CallAsync("Runtime.evaluate", new { expression = script, awaitPromise = false }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Weather kiosk start click failed");
        }
    }

    private async Task<JsonElement> CallAsync(string method, object? @params = null, CancellationToken cancellationToken = default)
    {
        if (_socket is null)
        {
            throw new InvalidOperationException("CDP session is not connected.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[id] = tcs;
        }

        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method
        };
        if (@params is not null)
        {
            payload["params"] = @params;
        }

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await tcs.Task.WaitAsync(timeout.Token);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 20];
        var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken);
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("id", out var idElement))
                {
                    continue;
                }

                var id = idElement.GetInt64();
                TaskCompletionSource<JsonElement>? tcs;
                lock (_pending)
                {
                    _pending.TryGetValue(id, out tcs);
                    _pending.Remove(id);
                }

                if (tcs is null)
                {
                    continue;
                }

                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    tcs.TrySetException(new InvalidOperationException(error.ToString()));
                }
                else if (doc.RootElement.TryGetProperty("result", out var resultElement))
                {
                    tcs.TrySetResult(resultElement.Clone());
                }
                else
                {
                    tcs.TrySetResult(default);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CDP read loop ended");
        }
    }

    private async Task<string> WaitForPageDebuggerUrlAsync(int port, string pageUrl, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_chromium?.HasExited == true)
            {
                throw new InvalidOperationException(
                    "Chromium exited before DevTools was ready. Exit code " + _chromium.ExitCode + "." + FormatCapturedOutput());
            }

            try
            {
                using var version = await http.GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken);
                version.EnsureSuccessStatusCode();
                var created = await http.GetStringAsync(
                    $"http://127.0.0.1:{port}/json/new?{Uri.EscapeDataString(pageUrl)}",
                    cancellationToken);
                using var doc = JsonDocument.Parse(created);
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var url)
                    && url.GetString() is { Length: > 0 } ws)
                {
                    return ws;
                }
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(300, cancellationToken);
            }
        }

        throw new TimeoutException(
            "Chromium DevTools did not become ready." + FormatCapturedOutput(),
            last);
    }

    private void CaptureProcessOutput(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (_capturedOutput)
        {
            if (_capturedOutput.Length >= MaxCapturedOutput)
            {
                return;
            }

            _capturedOutput.AppendLine(line);
        }
    }

    private string FormatCapturedOutput()
    {
        lock (_capturedOutput)
        {
            var text = _capturedOutput.ToString().Trim();
            return string.IsNullOrEmpty(text) ? string.Empty : " Chromium output: " + text;
        }
    }

    private static bool IsUsableChromium(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var resolved = path;
            for (var i = 0; i < 6; i++)
            {
                var link = File.ResolveLinkTarget(resolved, returnFinalTarget: false);
                if (link is null)
                {
                    break;
                }

                resolved = link.FullName;
            }

            using var stream = File.OpenRead(resolved);
            var header = new byte[Math.Min(2048, Math.Max(4, (int)stream.Length))];
            var read = stream.Read(header, 0, header.Length);
            if (read >= 4 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
            {
                return true;
            }

            var text = Encoding.UTF8.GetString(header, 0, read);
            if (text.Contains("snap install chromium", StringComparison.OrdinalIgnoreCase)
                || text.Contains("requires the chromium snap", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return text.StartsWith("#!", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
            {
                return candidate + ".exe";
            }
        }

        return null;
    }
}
