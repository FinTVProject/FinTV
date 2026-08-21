using FinTv;
using FinTv.Domain;
using FinTv.Services;
using FinTv.Weather;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

[ApiController]
[Route("api/weather")]
[Authorize(Policy = "admin")]
public class WeatherController : ControllerBase
{
    private readonly JellyfinCatalogService _catalog;
    private readonly ChannelService _channels;

    public WeatherController(JellyfinCatalogService catalog, ChannelService channels)
    {
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
                location = c.WeatherLocationQuery,
                zip = WeatherLocationParser.ExtractZip(c.WeatherLocationQuery),
                weatherLocationQuery = c.WeatherLocationQuery
            })
            .ToList();

        return Ok(new
        {
            weatherStarVariant = NormalizeWeatherStarId(config?.WeatherStarVariant),
            weatherSource = config?.WeatherSource ?? "auto",
            nativeRenderer = true,
            weatherStarPermalinkQuery = config?.WeatherStarPermalinkQuery,
            weatherStarAutoWideForSixteenNine = config?.WeatherStarAutoWideForSixteenNine ?? true,
            weatherMusicLibraryId = config?.WeatherMusicLibraryId,
            weatherMusicLibraryName = config?.WeatherMusicLibraryName,
            weatherDefaultLocationQuery = config?.WeatherDefaultLocationQuery
                ?? WeatherStarChannelService.ResolveDefaultLocationQuery(),
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

        if (request.WeatherSource is not null)
        {
            plugin.Configuration.WeatherSource = WeatherDataClient.ParseSource(request.WeatherSource) switch
            {
                WeatherSourceKind.UnitedStates => "us",
                WeatherSourceKind.World => "world",
                _ => "auto"
            };
        }

        var defaultLocation = request.DefaultLocation ?? request.DefaultZip;
        if (defaultLocation is not null)
        {
            try
            {
                plugin.Configuration.WeatherDefaultLocationQuery = WeatherLocationParser.NormalizeLocation(defaultLocation);
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
                    var location = WeatherLocationParser.NormalizeLocation(row.Location ?? row.Zip);
                    var updated = await _channels.UpdateWeatherLocationAsync(row.Id, location, cancellationToken);
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

        if (!string.IsNullOrWhiteSpace(request.WeatherStarVariant))
        {
            plugin.Configuration.WeatherStarVariant = NormalizeWeatherStarId(request.WeatherStarVariant);
        }

        plugin.SaveConfiguration();
        return await GetStatus(cancellationToken);
    }

    private static string NormalizeWeatherStarId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains("3", StringComparison.OrdinalIgnoreCase)
            ? "ws3kp"
            : "ws4kp";
}

public class WeatherSettingsRequest
{
    public string? WeatherStarPermalinkQuery { get; set; }

    public string? WeatherStarFullPermalink { get; set; }

    public bool? WeatherStarAutoWideForSixteenNine { get; set; }

    public string? WeatherStarVariant { get; set; }

    public string? WeatherSource { get; set; }

    public string? WeatherMusicLibraryId { get; set; }

    public string? WeatherMusicLibraryName { get; set; }

    public string? DefaultZip { get; set; }

    public string? DefaultLocation { get; set; }

    public List<WeatherChannelLocationRequest>? Channels { get; set; }
}

public class WeatherChannelLocationRequest
{
    public Guid Id { get; set; }

    public string? Zip { get; set; }

    public string? Location { get; set; }
}
