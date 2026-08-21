using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Runs vendored ws4kp/ws3kp Node servers on loopback only.
/// </summary>
public sealed class WeatherRendererHost : IHostedService, IDisposable
{
    private readonly ILogger<WeatherRendererHost> _logger;
    private readonly IWebHostEnvironment _env;
    private Process? _ws4;
    private Process? _ws3;

    public WeatherRendererHost(ILogger<WeatherRendererHost> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public bool Ws4Running => _ws4 is { HasExited: false };

    public bool Ws3Running => _ws3 is { HasExited: false };

    public WeatherStarDockerVariant? ResolveLocalVariant(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return WeatherStarDockerVariant.Ws4kp;
        }

        if (baseUrl.Contains("8083", StringComparison.Ordinal) || baseUrl.Contains("ws3", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherStarDockerVariant.Ws3kp;
        }

        if (baseUrl.Contains("127.0.0.1", StringComparison.Ordinal) || baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherStarDockerVariant.Ws4kp;
        }

        return WeatherStarDockerVariant.Ws4kp;
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => EnsureRunningAsync(WeatherStarDockerVariant.Ws4kp, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopAsync(WeatherStarDockerVariant.Ws4kp, cancellationToken);
        await StopAsync(WeatherStarDockerVariant.Ws3kp, cancellationToken);
    }

    public Task EnsureRunningAsync(WeatherStarDockerVariant variant, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (variant == WeatherStarDockerVariant.Ws4kp && !Ws4Running)
        {
            _ws4 = StartNode("ws4kp", 8080);
        }

        if (variant == WeatherStarDockerVariant.Ws3kp && !Ws3Running)
        {
            _ws3 = StartNode("ws3kp", 8083);
        }

        return Task.CompletedTask;
    }

    public async Task WaitUntilReadyAsync(WeatherStarDockerVariant variant, CancellationToken cancellationToken)
    {
        var port = variant == WeatherStarDockerVariant.Ws4kp ? 8080 : 8083;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/", cancellationToken);
                if ((int)response.StatusCode < 500)
                {
                    return;
                }
            }
            catch
            {
                await Task.Delay(400, cancellationToken);
            }
        }

        _logger.LogWarning("Weather renderer on port {Port} did not become ready", port);
    }

    public Task StopAsync(WeatherStarDockerVariant variant, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (variant == WeatherStarDockerVariant.Ws4kp)
        {
            TryKill(ref _ws4);
        }
        else
        {
            TryKill(ref _ws3);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        TryKill(ref _ws4);
        TryKill(ref _ws3);
    }

    private Process? StartNode(string folder, int port)
    {
        var root = Path.Combine(_env.ContentRootPath, "vendor", folder);
        if (!Directory.Exists(root))
        {
            root = Path.Combine(_env.ContentRootPath, "..", "..", "vendor", folder);
        }

        if (!Directory.Exists(root))
        {
            _logger.LogWarning("Weather vendor folder {Folder} was not found", folder);
            return null;
        }

        var entry = File.Exists(Path.Combine(root, "index.mjs")) ? "index.mjs" : "index.js";
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = entry,
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Environment =
                    {
                        ["PORT"] = port.ToString(),
                        ["HOST"] = "127.0.0.1",
                        ["WS4KP_PORT"] = "8080",
                        ["WS4KP_HOST"] = "127.0.0.1",
                        ["WS3KP_PORT"] = "8083",
                        ["WS3KP_HOST"] = "127.0.0.1"
                    }
                }
            };
            process.Start();
            _logger.LogInformation("Started {Folder} weather renderer on 127.0.0.1:{Port}", folder, port);
            return process;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start {Folder} weather renderer", folder);
            return null;
        }
    }

    private static void TryKill(ref Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }

        process = null;
    }
}
