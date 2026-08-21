using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public sealed class LiveTvRegistrationHostedService : IHostedService
{
    private readonly FinTvServerClient _client;
    private readonly ILogger<LiveTvRegistrationHostedService> _logger;

    public LiveTvRegistrationHostedService(
        FinTvServerClient client,
        IServerApplicationHost appHost,
        IHttpClientFactory http,
        ILogger<LiveTvRegistrationHostedService> logger)
    {
        _ = appHost;
        _ = http;
        _client = client;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AutoRegisterLiveTv || string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            return;
        }

        try
        {
            var urls = await _client.GetJsonAsync<LiveTvUrls>("/api/plugin/live-tv-urls", cancellationToken);
            if (urls is null)
            {
                return;
            }

            _logger.LogInformation("FinTV Live TV URLs: M3U {M3u} XMLTV {Epg}", urls.M3u, urls.Epg);
            _logger.LogInformation("Add these URLs in Dashboard → Live TV if they are not already registered.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not auto-register FinTV Live TV tuner; add the M3U/XMLTV URLs in Dashboard → Live TV");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class LiveTvUrls
    {
        public string M3u { get; set; } = string.Empty;

        public string Epg { get; set; } = string.Empty;
    }
}
