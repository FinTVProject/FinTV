using System.Globalization;
using FinTv.Services;
using SkiaSharp;

namespace FinTv.Weather;

public sealed class WeatherStarCompositor
{
    private readonly WeatherStarAssets _assets;

    public WeatherStarCompositor(WeatherStarAssets assets)
    {
        _assets = assets;
    }

    public byte[] RenderJpeg(
        WeatherSnapshot snap,
        WeatherStarScreen screen,
        WeatherStarDockerVariant skin,
        int width,
        int height,
        bool scanlines,
        int radarIndex)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(skin == WeatherStarDockerVariant.Ws3kp ? new SKColor(0x10, 0x20, 0x70) : new SKColor(0x00, 0x28, 0x8A));

        var wide = width > 700;
        var bg = _assets.Background(skin, wide, screen);
        if (bg is not null)
        {
            DrawBitmap(canvas, bg, new SKRect(0, 0, width, height));
        }

        var font = _assets.Font(skin);
        var large = _assets.Font(skin, large: true);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var yellow = new SKPaint { Color = new SKColor(0xFF, 0xE1, 0x4A), IsAntialias = true };
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 140), IsAntialias = true };

        DrawText(canvas, snap.Place.DisplayName.ToUpperInvariant(), font, 18, 10, 22, yellow);
        DrawText(canvas, DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture), font, 16, width - 110, 22, white);
        DrawText(canvas, Title(screen), large, 22, 10, 52, white);

        switch (screen)
        {
            case WeatherStarScreen.Current:
            case WeatherStarScreen.Observations:
                DrawCurrent(canvas, snap, font, large, width, white, yellow);
                break;
            case WeatherStarScreen.Hourly:
            case WeatherStarScreen.HourlyGraph:
                DrawHourly(canvas, snap, font, width, white, yellow);
                break;
            case WeatherStarScreen.LocalForecast:
            case WeatherStarScreen.ExtendedForecast:
            case WeatherStarScreen.Regional:
                DrawForecast(canvas, snap, font, screen == WeatherStarScreen.ExtendedForecast ? 6 : 3, width, white, yellow);
                break;
            case WeatherStarScreen.Hazards:
                DrawHazards(canvas, snap, font, width, white, yellow);
                break;
            case WeatherStarScreen.Radar:
                DrawRadar(canvas, snap, font, width, height, radarIndex, white);
                break;
            case WeatherStarScreen.Almanac:
            case WeatherStarScreen.Travel:
            case WeatherStarScreen.SpcOutlook:
                DrawForecast(canvas, snap, font, 4, width, white, yellow);
                break;
        }

        if (snap.Alerts.Count > 0 && screen != WeatherStarScreen.Hazards)
        {
            DrawText(canvas, snap.Alerts[0].Event.ToUpperInvariant(), font, 14, 10, height - 18, yellow);
        }

        if (scanlines)
        {
            using var line = new SKPaint { Color = new SKColor(0, 0, 0, 50) };
            for (var y = 0; y < height; y += 2)
            {
                canvas.DrawRect(0, y, width, 1, line);
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
        return data.ToArray();
    }

    private void DrawCurrent(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, SKTypeface large, int width, SKPaint white, SKPaint yellow)
    {
        var cur = snap.Current;
        if (cur is null)
        {
            DrawText(canvas, "NO CURRENT DATA", font, 22, 20, 160, white);
            return;
        }

        var unit = snap.UseMetric ? "C" : "F";
        DrawText(canvas, Math.Round(cur.Temperature).ToString("0", CultureInfo.InvariantCulture) + "°" + unit, large, 72, 24, 170, white);
        DrawText(canvas, cur.ConditionText, font, 22, 24, 210, yellow);

        var icon = _assets.Icon(cur.IconKey);
        if (icon is not null)
        {
            DrawBitmap(canvas, icon, new SKRect(width - 220, 80, width - 24, 260));
        }

        var y = 250;
        if (cur.Humidity is int h)
        {
            DrawText(canvas, "Humidity  " + h + "%", font, 18, 24, y, white);
            y += 28;
        }

        if (cur.WindSpeed is double w)
        {
            DrawText(canvas, "Wind  " + cur.WindDirection + " " + Math.Round(w) + (snap.UseMetric ? " km/h" : " mph"), font, 18, 24, y, white);
            y += 28;
        }

        if (cur.Dewpoint is double d)
        {
            DrawText(canvas, "Dewpoint  " + Math.Round(d) + "°" + unit, font, 18, 24, y, white);
            y += 28;
        }

        if (cur.Pressure is double p)
        {
            DrawText(canvas, "Pressure  " + p.ToString("0.00", CultureInfo.InvariantCulture) + (snap.UseMetric ? " hPa" : " in"), font, 18, 24, y, white);
        }
    }

    private static void DrawHourly(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, SKPaint white, SKPaint gold)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-20);
        var hours = snap.Hourly.Where(hour => hour.Time >= cutoff).Take(8).ToList();
        if (hours.Count == 0)
        {
            DrawText(canvas, "NO HOURLY DATA", font, 22, 20, 160, white);
            return;
        }

        var y = 90;
        foreach (var hour in hours)
        {
            DrawText(canvas, hour.Time.ToString("h tt", CultureInfo.InvariantCulture), font, 18, 20, y, gold);
            DrawText(canvas, Math.Round(hour.Temperature) + "°", font, 18, 140, y, white);
            DrawText(canvas, hour.PrecipitationChance is int pop ? pop + "%" : "", font, 18, 220, y, white);
            DrawText(canvas, hour.IconKey.Replace("-", " ", StringComparison.Ordinal), font, 16, 300, y, white);
            y += 42;
        }
    }

    private void DrawForecast(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int count, int width, SKPaint white, SKPaint gold)
    {
        var y = 88;
        foreach (var day in snap.Daily.Take(count))
        {
            DrawText(canvas, day.Name.ToUpperInvariant(), font, 18, 16, y, gold);
            DrawText(canvas, Math.Round(day.High) + "/" + Math.Round(day.Low), font, 18, width - 140, y, white);
            var icon = _assets.Icon(day.IconKey);
            if (icon is not null)
            {
                DrawBitmap(canvas, icon, new SKRect(width - 70, y - 22, width - 18, y + 26));
            }

            var narrative = day.Narrative.Length > 90 ? day.Narrative[..87] + "..." : day.Narrative;
            DrawText(canvas, narrative, font, 14, 16, y + 24, white);
            y += 62;
        }
    }

    private static void DrawHazards(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, SKPaint white, SKPaint gold)
    {
        if (snap.Alerts.Count == 0)
        {
            DrawText(canvas, "NO WARNINGS", font, 28, 24, 180, gold);
            DrawText(canvas, "There are no active weather alerts.", font, 18, 24, 230, white);
            return;
        }

        var y = 90;
        foreach (var alert in snap.Alerts.Take(6))
        {
            DrawText(canvas, alert.Event.ToUpperInvariant(), font, 18, 16, y, gold);
            var line = alert.Headline.Length > 90 ? alert.Headline[..87] + "..." : alert.Headline;
            DrawText(canvas, line, font, 14, 16, y + 24, white);
            y += 58;
        }
    }

    private static void DrawRadar(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, int height, int radarIndex, SKPaint white)
    {
        if (snap.Radar.Count == 0)
        {
            DrawText(canvas, snap.IsUnitedStates ? "RADAR UNAVAILABLE" : "NO LOCAL RADAR", font, 24, 24, 200, white);
            DrawText(canvas, snap.Place.DisplayName, font, 16, 24, 240, white);
            return;
        }

        var frame = snap.Radar[Math.Abs(radarIndex) % snap.Radar.Count];
        using var bmp = SKBitmap.Decode(frame.Image);
        if (bmp is not null)
        {
            DrawBitmap(canvas, bmp, new SKRect(20, 70, width - 20, height - 40));
        }

        DrawText(canvas, frame.Time.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture), font, 16, 24, height - 18, white);
    }

    private static string Title(WeatherStarScreen screen)
        => screen switch
        {
            WeatherStarScreen.Current => "CURRENT CONDITIONS",
            WeatherStarScreen.Observations => "LATEST OBSERVATIONS",
            WeatherStarScreen.Hourly => "HOURLY FORECAST",
            WeatherStarScreen.HourlyGraph => "HOURLY GRAPH",
            WeatherStarScreen.LocalForecast => "LOCAL FORECAST",
            WeatherStarScreen.ExtendedForecast => "EXTENDED FORECAST",
            WeatherStarScreen.Hazards => "WEATHER ALERTS",
            WeatherStarScreen.Radar => "LOCAL RADAR",
            WeatherStarScreen.Regional => "REGIONAL FORECAST",
            WeatherStarScreen.Almanac => "ALMANAC",
            WeatherStarScreen.Travel => "TRAVEL CITIES",
            WeatherStarScreen.SpcOutlook => "STORM OUTLOOK",
            _ => "WEATHER"
        };

    private static readonly SKSamplingOptions BitmapSampling = new(SKFilterMode.Linear);

    private static void DrawBitmap(SKCanvas canvas, SKBitmap bitmap, SKRect dest)
        => canvas.DrawBitmap(bitmap, dest, BitmapSampling);

    private static void DrawText(SKCanvas canvas, string text, SKTypeface typeface, float size, float x, float y, SKPaint color)
    {
        using var font = new SKFont(typeface, size);
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, color);
    }
}
