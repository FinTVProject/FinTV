using FinTv.Services;

namespace FinTv.Weather;

public sealed class WeatherStarSequencer
{
    private readonly IReadOnlyList<WeatherStarScreen> _screens;
    private readonly TimeSpan _screenDuration;
    private readonly bool _scanlines;
    public readonly WeatherStarDockerVariant Skin;
    public readonly bool Wide;

    public WeatherStarSequencer(
        string? permalinkQuery,
        WeatherStarDockerVariant skin,
        bool wide,
        bool channelScanlines)
    {
        Skin = skin;
        Wide = wide;
        var flags = Parse(permalinkQuery);
        _scanlines = channelScanlines || Flag(flags, "scanLines", false);
        var speed = 1.0;
        if (flags.TryGetValue("speed", out var speedRaw) && double.TryParse(speedRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            speed = Math.Clamp(parsed, 0.5, 2.0);
        }

        _screenDuration = TimeSpan.FromSeconds(10 * speed);
        var screens = new List<WeatherStarScreen>();
        Add(screens, flags, "hazards", WeatherStarScreen.Hazards);
        Add(screens, flags, "current-weather", WeatherStarScreen.Current);
        Add(screens, flags, "latest-observations", WeatherStarScreen.Observations);
        Add(screens, flags, "hourly", WeatherStarScreen.Hourly);
        Add(screens, flags, "hourly-graph", WeatherStarScreen.HourlyGraph);
        Add(screens, flags, "local-forecast", WeatherStarScreen.LocalForecast);
        Add(screens, flags, "extended-forecast", WeatherStarScreen.ExtendedForecast);
        Add(screens, flags, "regional-forecast", WeatherStarScreen.Regional);
        Add(screens, flags, "travel", WeatherStarScreen.Travel);
        Add(screens, flags, "almanac", WeatherStarScreen.Almanac);
        Add(screens, flags, "spc-outlook", WeatherStarScreen.SpcOutlook);
        Add(screens, flags, "radar", WeatherStarScreen.Radar);
        _screens = screens.Count == 0 ? [WeatherStarScreen.Current, WeatherStarScreen.LocalForecast] : screens;
    }

    public bool Scanlines => _scanlines;

    public (WeatherStarScreen Screen, int RadarIndex) At(TimeSpan elapsed)
    {
        if (_screens.Count == 0)
        {
            return (WeatherStarScreen.Current, 0);
        }

        var cycle = _screenDuration.TotalMilliseconds * _screens.Count;
        var pos = elapsed.TotalMilliseconds % cycle;
        if (pos < 0)
        {
            pos += cycle;
        }

        var index = Math.Min(_screens.Count - 1, (int)(pos / _screenDuration.TotalMilliseconds));
        var screen = _screens[index];
        var within = pos - index * _screenDuration.TotalMilliseconds;
        var radarIndex = (int)(within / 400);
        return (screen, radarIndex);
    }

    private static void Add(List<WeatherStarScreen> screens, Dictionary<string, string> flags, string key, WeatherStarScreen screen)
    {
        if (Flag(flags, key, true))
        {
            screens.Add(screen);
        }
    }

    private static bool Flag(Dictionary<string, string> flags, string key, bool fallback)
    {
        if (!flags.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        return raw is not "false" and not "0";
    }

    private static Dictionary<string, string> Parse(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var trimmed = query.Trim().TrimStart('?');
        foreach (var segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var sep = segment.IndexOf('=');
            if (sep < 0)
            {
                result[Uri.UnescapeDataString(segment)] = "true";
                continue;
            }

            result[Uri.UnescapeDataString(segment[..sep])] = Uri.UnescapeDataString(segment[(sep + 1)..]);
        }

        return result;
    }
}
