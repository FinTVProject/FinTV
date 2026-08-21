using FinTv.Configuration;
using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Blackframe scanning lives on the Jellyfin plugin. This keeps the admin API shape.
/// </summary>
public sealed class BlackframeChapterTask
{
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var state = FinTvRuntime.Current?.Configuration.BlackframeTaskState ?? new BlackframeTaskState();
        state.LastError = "Blackframe scan runs in the FinTV Jellyfin plugin.";
        FinTvRuntime.Current?.SaveConfiguration();
        progress.Report(100);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Library tagging stays in the Jellyfin plugin (needs ILibraryManager write access).
/// </summary>
public sealed class FinTvChannelTaggingService
{
    public Task RunAsync(bool fullRetag, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        _ = fullRetag;
        _ = progress;
        _ = cancellationToken;
        var state = FinTvRuntime.Current?.Configuration.ChannelAutoTaggingTaskState ?? new ChannelAutoTaggingTaskState();
        state.IsRunning = false;
        state.LastError = "Channel auto-tagging runs in the FinTV Jellyfin plugin.";
        state.LastCompletedAt = DateTime.UtcNow;
        FinTvRuntime.Current?.SaveConfiguration();
        return Task.CompletedTask;
    }
}

public enum WeatherStarDockerVariant
{
    Ws4kp = 0,
    Ws3kp = 1
}

public class WeatherStarDockerStatus
{
    public bool DockerAvailable { get; set; }

    public bool Running { get; set; }

    public bool HttpReachable { get; set; }

    public bool HttpListeningInsideSidecar { get; set; }

    public bool StaleNetworkAttachment { get; set; }

    public string? JellyfinContainerRef { get; set; }

    public string? SidecarNetworkParent { get; set; }

    public bool SharesJellyfinNetwork { get; set; }

    public bool JellyfinInDocker { get; set; }

    public string? StatusMessage { get; set; }

    public string ContainerName { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;

    public int HostPort { get; set; }

    public string BaseUrl { get; set; } = "http://127.0.0.1:8080";
}

public class WeatherStarDockerCombinedStatus
{
    public WeatherStarDockerStatus Ws4kp { get; set; } = new();

    public WeatherStarDockerStatus Ws3kp { get; set; } = new();

    public string? ConfiguredBaseUrl { get; set; }

    public bool UsingLocalWs4kp { get; set; }

    public bool UsingLocalWs3kp { get; set; }
}

public sealed class WeatherStarDockerService
{
    private readonly WeatherRendererHost _host;

    public WeatherStarDockerService(WeatherRendererHost host)
    {
        _host = host;
    }

    public Task<WeatherStarDockerCombinedStatus> GetCombinedStatusAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(new WeatherStarDockerCombinedStatus
        {
            Ws4kp = Status(WeatherStarDockerVariant.Ws4kp),
            Ws3kp = Status(WeatherStarDockerVariant.Ws3kp),
            ConfiguredBaseUrl = FinTvRuntime.Current?.Configuration.WeatherStarBaseUrl,
            UsingLocalWs4kp = true,
            UsingLocalWs3kp = _host.Ws3Running
        });
    }

    public Task<WeatherStarDockerStatus> GetStatusAsync(WeatherStarDockerVariant variant, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(Status(variant));
    }

    public void UpdateSettings(WeatherStarDockerVariant variant, int? hostPort, string? image)
    {
        var config = FinTvRuntime.Current?.Configuration;
        if (config is null)
        {
            return;
        }

        if (variant == WeatherStarDockerVariant.Ws4kp)
        {
            if (hostPort.HasValue)
            {
                config.Ws4kp.HostPort = hostPort.Value;
            }

            if (!string.IsNullOrWhiteSpace(image))
            {
                config.Ws4kp.Image = image;
            }
        }
        else
        {
            if (hostPort.HasValue)
            {
                config.Ws3kp.HostPort = hostPort.Value;
            }

            if (!string.IsNullOrWhiteSpace(image))
            {
                config.Ws3kp.Image = image;
            }
        }

        FinTvRuntime.Current?.SaveConfiguration();
    }

    public Task EnsureRunningAsync(WeatherStarDockerVariant variant, CancellationToken cancellationToken)
        => _host.EnsureRunningAsync(variant, cancellationToken);

    public Task StopAsync(WeatherStarDockerVariant variant, CancellationToken cancellationToken)
        => _host.StopAsync(variant, cancellationToken);

    private WeatherStarDockerStatus Status(WeatherStarDockerVariant variant)
    {
        var running = variant == WeatherStarDockerVariant.Ws4kp ? _host.Ws4Running : _host.Ws3Running;
        var port = variant == WeatherStarDockerVariant.Ws4kp
            ? (FinTvRuntime.Current?.Configuration.Ws4kp.HostPort ?? 8080)
            : (FinTvRuntime.Current?.Configuration.Ws3kp.HostPort ?? 8083);
        return new WeatherStarDockerStatus
        {
            DockerAvailable = false,
            Running = running,
            HttpReachable = running,
            HttpListeningInsideSidecar = running,
            StatusMessage = running
                ? "In-image WeatherStar renderer is running on loopback."
                : "WeatherStar renderer is stopped.",
            ContainerName = variant == WeatherStarDockerVariant.Ws4kp ? "ws4kp" : "ws3kp",
            Image = "in-image",
            HostPort = port,
            BaseUrl = $"http://127.0.0.1:{port}"
        };
    }
}
