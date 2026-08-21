using FinTv;
using FinTv.Domain;
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
    private readonly ChannelService _channels;

    public WeatherController(WeatherRendererHost renderer, JellyfinCatalogService catalog, ChannelService channels)
    {
        _renderer = renderer;
        _catalog = catalog;
        _channels = channels;
    }

    [HttpGet("status")]
    public async Task<ActionResult<object>> GetStatus(CancellationToken cancellationToken)
    {
        var config = FinTvRuntime.Current?.Configuration;
        var weatherChannels = (await _channels.GetAllAsync(cancellationToken))
            .Where(c => c.ContentType == ChannelContentType.Weather)
            .Select(c => new
            {
                id = c.Id,
                number = ChannelNumbers.Format(c.Number),
                name = c.Name,
                zip = WeatherLocationParser.ExtractZip(c.WeatherLocationQuery),
                weatherLocationQuery = c.WeatherLocationQuery
            })
            .ToList();

        return Ok(new
        {
            weatherStarVariant = _renderer.Ws3Running
                ? "ws3kp"
                : _renderer.Ws4Running
                    ? "ws4kp"
                    : NormalizeWeatherStarId(config?.WeatherStarVariant),
            ws4kpRunning = _renderer.Ws4Running,
            ws3kpRunning = _renderer.Ws3Running,
            chromium = ChromiumCdpCapture.FindChromium() is not null,
            weatherStarPermalinkQuery = config?.WeatherStarPermalinkQuery,
            weatherStarAutoWideForSixteenNine = config?.WeatherStarAutoWideForSixteenNine ?? true,
            weatherMusicLibraryId = config?.WeatherMusicLibraryId,
            weatherMusicLibraryName = config?.WeatherMusicLibraryName,
            weatherDefaultLocationQuery = config?.WeatherDefaultLocationQuery
                ?? WeatherLocationParser.ExtractZip(WeatherStarChannelService.ResolveDefaultLocationQuery()),
            weatherChannels,
            musicLibraries = _catalog.GetMusicLibraries().Select(l => new { id = l.Id, name = l.Name }),
            publicSite = false,
            bind = "127.0.0.1"
        });
    }

    [HttpPut("settings")]
    public async Task<ActionResult<object>> UpdateSettings(
        [FromBody] WeatherSettingsRequest request,
        CancellationToken cancellationToken)
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

        if (request.DefaultZip is not null)
        {
            try
            {
                plugin.Configuration.WeatherDefaultLocationQuery = WeatherLocationParser.NormalizeZip(request.DefaultZip);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        if (request.Channels is not null)
        {
            foreach (var row in request.Channels)
            {
                try
                {
                    var zip = WeatherLocationParser.NormalizeZip(row.Zip);
                    var updated = await _channels.UpdateWeatherLocationAsync(row.Id, zip, cancellationToken);
                    if (updated is null)
                    {
                        return NotFound(new { message = "Weather channel not found." });
                    }
                }
                catch (ArgumentException ex)
                {
                    return BadRequest(new { message = ex.Message });
                }
            }
        }

        var previousVariant = plugin.Configuration.WeatherStarVariant;
        if (!string.IsNullOrWhiteSpace(request.WeatherStarVariant))
        {
            plugin.Configuration.WeatherStarVariant = NormalizeWeatherStarId(request.WeatherStarVariant);
        }

        plugin.SaveConfiguration();
        if (!string.Equals(previousVariant, plugin.Configuration.WeatherStarVariant, StringComparison.OrdinalIgnoreCase))
        {
            await _renderer.EnsureRunningAsync(ParseWeatherStarVariant(plugin.Configuration.WeatherStarVariant), cancellationToken);
        }

        return await GetStatus(cancellationToken);
    }

    [HttpPost("renderer/{variant}/start")]
    public async Task<ActionResult<object>> StartRenderer(string variant, CancellationToken cancellationToken)
    {
        var parsed = ParseWeatherStarVariant(variant);
        var plugin = FinTvRuntime.Current;
        if (plugin is not null)
        {
            plugin.Configuration.WeatherStarVariant = NormalizeWeatherStarId(variant);
            plugin.SaveConfiguration();
        }

        await _renderer.EnsureRunningAsync(parsed, cancellationToken);
        await _renderer.WaitUntilReadyAsync(parsed, cancellationToken);
        return await GetStatus(cancellationToken);
    }

    private static WeatherStarDockerVariant ParseWeatherStarVariant(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains("3", StringComparison.OrdinalIgnoreCase)
            ? WeatherStarDockerVariant.Ws3kp
            : WeatherStarDockerVariant.Ws4kp;

    private static string NormalizeWeatherStarId(string? value)
        => ParseWeatherStarVariant(value) == WeatherStarDockerVariant.Ws3kp ? "ws3kp" : "ws4kp";
}

public class WeatherSettingsRequest
{
    public string? WeatherStarPermalinkQuery { get; set; }

    public string? WeatherStarFullPermalink { get; set; }

    public bool? WeatherStarAutoWideForSixteenNine { get; set; }

    public string? WeatherStarVariant { get; set; }

    public string? WeatherMusicLibraryId { get; set; }

    public string? WeatherMusicLibraryName { get; set; }

    public string? DefaultZip { get; set; }

    public List<WeatherChannelZipRequest>? Channels { get; set; }
}

public class WeatherChannelZipRequest
{
    public Guid Id { get; set; }

    public string? Zip { get; set; }
}
