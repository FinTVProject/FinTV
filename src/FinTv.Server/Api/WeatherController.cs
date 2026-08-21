using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

[ApiController]
[Route("api/weather")]
[Authorize(Policy = "admin")]
public class WeatherController : ControllerBase
{
    private readonly WeatherRendererHost _renderer;
    private readonly JellyfinCatalogService _catalog;

    public WeatherController(WeatherRendererHost renderer, JellyfinCatalogService catalog)
    {
        _renderer = renderer;
        _catalog = catalog;
    }

    [HttpGet("status")]
    public ActionResult<object> GetStatus()
    {
        var config = FinTvRuntime.Current?.Configuration;
        return Ok(new
        {
            ws4kpRunning = _renderer.Ws4Running,
            ws3kpRunning = _renderer.Ws3Running,
            chromium = ChromiumCdpCapture.FindChromium() is not null,
            weatherStarPermalinkQuery = config?.WeatherStarPermalinkQuery,
            weatherStarAutoWideForSixteenNine = config?.WeatherStarAutoWideForSixteenNine ?? true,
            weatherMusicLibraryId = config?.WeatherMusicLibraryId,
            weatherMusicLibraryName = config?.WeatherMusicLibraryName,
            musicLibraries = _catalog.GetMusicLibraries().Select(l => new { id = l.Id, name = l.Name }),
            publicSite = false,
            bind = "127.0.0.1"
        });
    }

    [HttpPut("settings")]
    public ActionResult<object> UpdateSettings([FromBody] WeatherSettingsRequest request)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        if (request.WeatherStarPermalinkQuery is not null)
        {
            plugin.Configuration.WeatherStarPermalinkQuery =
                WeatherStarChannelService.NormalizePermalinkQuery(request.WeatherStarPermalinkQuery);
        }

        if (!string.IsNullOrWhiteSpace(request.WeatherStarFullPermalink))
        {
            var split = WeatherStarChannelService.SplitPermalink(request.WeatherStarFullPermalink);
            plugin.Configuration.WeatherStarPermalinkQuery = split.Query;
        }

        if (request.WeatherStarAutoWideForSixteenNine.HasValue)
        {
            plugin.Configuration.WeatherStarAutoWideForSixteenNine = request.WeatherStarAutoWideForSixteenNine.Value;
        }

        if (request.WeatherMusicLibraryId is not null)
        {
            plugin.Configuration.WeatherMusicLibraryId = string.IsNullOrWhiteSpace(request.WeatherMusicLibraryId)
                ? null
                : request.WeatherMusicLibraryId.Trim();
        }

        if (request.WeatherMusicLibraryName is not null)
        {
            plugin.Configuration.WeatherMusicLibraryName = request.WeatherMusicLibraryName.Trim();
        }

        plugin.SaveConfiguration();
        return GetStatus();
    }

    [HttpPost("renderer/{variant}/start")]
    public async Task<ActionResult<object>> StartRenderer(string variant, CancellationToken cancellationToken)
    {
        var parsed = variant.Contains("3", StringComparison.Ordinal) ? WeatherStarDockerVariant.Ws3kp : WeatherStarDockerVariant.Ws4kp;
        await _renderer.EnsureRunningAsync(parsed, cancellationToken);
        await _renderer.WaitUntilReadyAsync(parsed, cancellationToken);
        return GetStatus();
    }
}

public class WeatherSettingsRequest
{
    public string? WeatherStarPermalinkQuery { get; set; }

    public string? WeatherStarFullPermalink { get; set; }

    public bool? WeatherStarAutoWideForSixteenNine { get; set; }

    public string? WeatherMusicLibraryId { get; set; }

    public string? WeatherMusicLibraryName { get; set; }
}
