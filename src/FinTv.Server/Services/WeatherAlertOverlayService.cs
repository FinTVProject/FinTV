using System.Text;
using FinTv.Domain;
using FinTv.Weather;

namespace FinTv.Services;

public sealed class WeatherAlertOverlayService
{
    private readonly WeatherDataClient _weather;

    public WeatherAlertOverlayService(WeatherDataClient weather)
    {
        _weather = weather;
    }

    public static WeatherAlertOverlayMode ParseMode(string? value)
    {
        if (string.Equals(value, "cutin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "cut-in", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "screen", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherAlertOverlayMode.CutIn;
        }

        if (string.Equals(value, "ticker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "scroll", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherAlertOverlayMode.Ticker;
        }

        return WeatherAlertOverlayMode.Off;
    }

    public static string FormatMode(WeatherAlertOverlayMode mode)
        => mode switch
        {
            WeatherAlertOverlayMode.CutIn => "cutin",
            WeatherAlertOverlayMode.Ticker => "ticker",
            _ => "off"
        };

    public WeatherAlertOverlayMode Mode
        => ParseMode(FinTvRuntime.Current?.Configuration.WeatherAlertOverlayMode);

    public TimeSpan CutInInterval
    {
        get
        {
            var minutes = FinTvRuntime.Current?.Configuration.WeatherAlertCutInIntervalMinutes ?? 15;
            return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 180));
        }
    }

    public TimeSpan CutInDuration
    {
        get
        {
            var seconds = FinTvRuntime.Current?.Configuration.WeatherAlertCutInDurationSeconds ?? 20;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 120));
        }
    }

    public bool AppliesTo(Channel channel)
        => channel.ContentType is not ChannelContentType.Weather and not ChannelContentType.News;

    public bool AllowsTicker(Channel channel)
        => AppliesTo(channel) && !PastTenseNewsCatalog.IsPastTenseNewsChannel(channel);

    public async Task<IReadOnlyList<WeatherAlert>> GetActiveAlertsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snap = await GetSnapshotAsync(cancellationToken);
            return snap.Alerts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    public async Task<bool> ShouldCutInNowAsync(
        Channel channel,
        WeatherAlertCutInSession session,
        CancellationToken cancellationToken)
    {
        if (Mode != WeatherAlertOverlayMode.CutIn || !AppliesTo(channel))
        {
            return false;
        }

        var alerts = await GetActiveAlertsAsync(cancellationToken);
        if (alerts.Count == 0)
        {
            return false;
        }

        return session.SecondsUntilNext(CutInInterval) <= 2;
    }

    public async Task<double> CapMediaDurationAsync(
        Channel channel,
        WeatherAlertCutInSession session,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        if (Mode != WeatherAlertOverlayMode.CutIn || !AppliesTo(channel) || durationSeconds <= 2)
        {
            return durationSeconds;
        }

        var alerts = await GetActiveAlertsAsync(cancellationToken);
        if (alerts.Count == 0)
        {
            return durationSeconds;
        }

        var until = session.SecondsUntilNext(CutInInterval);
        if (until <= 2)
        {
            return durationSeconds;
        }

        return Math.Max(2, Math.Min(durationSeconds, until));
    }

    public void MarkCutInComplete(WeatherAlertCutInSession session)
        => session.LastCutInUtc = DateTime.UtcNow;

    public async Task<string?> PrepareTickerFileAsync(Channel channel, CancellationToken cancellationToken)
    {
        if (Mode != WeatherAlertOverlayMode.Ticker || !AllowsTicker(channel))
        {
            return null;
        }

        var text = await BuildTickerTextAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var folder = FinTvRuntime.Current?.WeatherStarFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "alert-ticker.txt");
        await File.WriteAllTextAsync(path, text, Encoding.UTF8, cancellationToken);
        return path;
    }

    private async Task<string?> BuildTickerTextAsync(CancellationToken cancellationToken)
    {
        var alerts = await GetActiveAlertsAsync(cancellationToken);
        if (alerts.Count == 0)
        {
            return null;
        }

        var parts = new List<string> { "WEATHER ALERT" };
        foreach (var alert in alerts.Take(5))
        {
            var eventName = Compact(alert.Event);
            var detail = Compact(string.IsNullOrWhiteSpace(alert.Headline) ? alert.Description : alert.Headline);
            if (string.IsNullOrWhiteSpace(eventName) && string.IsNullOrWhiteSpace(detail))
            {
                continue;
            }

            parts.Add(string.IsNullOrWhiteSpace(detail) || detail.Equals(eventName, StringComparison.OrdinalIgnoreCase)
                ? eventName
                : $"{eventName}: {detail}");
        }

        if (parts.Count < 2)
        {
            return null;
        }

        var body = string.Join("   •   ", parts);
        var text = $"{body}     {body}";
        return text.Length > 900 ? text[..900] : text;
    }

    private async Task<WeatherSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var config = FinTvRuntime.Current?.Configuration;
        var location = WeatherStarChannelService.ResolveDefaultLocationQuery();
        var source = WeatherDataClient.ParseSource(config?.WeatherSource);
        var useMetric = WeatherStarChannelService.PermalinkUsesMetricUnits(config?.WeatherStarPermalinkQuery);
        return await _weather.GetSnapshotAsync(location, source, useMetric, cancellationToken);
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed class WeatherAlertCutInSession
{
    public DateTime LastCutInUtc { get; set; } = DateTime.UtcNow;

    public double SecondsUntilNext(TimeSpan interval)
    {
        var due = LastCutInUtc + interval;
        return Math.Max(0, (due - DateTime.UtcNow).TotalSeconds);
    }
}
