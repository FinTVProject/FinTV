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
        int radarIndex,
        int screenRepeat = 0)
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
        DrawText(canvas, PlaceNow(snap).ToString("h:mm tt", CultureInfo.InvariantCulture), font, 16, width - 110, 22, white);
        if (screen == WeatherStarScreen.LocalForecast)
        {
            DrawText(canvas, "Local", large, 18, 10, 42, white);
            DrawText(canvas, "Forecast", large, 18, 10, 62, white);
        }
        else if (screen != WeatherStarScreen.Hazards)
        {
            DrawText(canvas, Title(screen), large, 22, 10, 52, white);
        }

        switch (screen)
        {
            case WeatherStarScreen.Current:
                DrawCurrent(canvas, snap, font, large, width, white, yellow);
                break;
            case WeatherStarScreen.Observations:
                DrawObservations(canvas, snap, font, width, white, yellow);
                break;
            case WeatherStarScreen.Hourly:
                DrawHourly(canvas, snap, font, large, width, height, radarIndex, white, yellow);
                break;
            case WeatherStarScreen.HourlyGraph:
                DrawHourlyGraph(canvas, snap, font, width, height, white, yellow);
                break;
            case WeatherStarScreen.LocalForecast:
                DrawLocalForecast(canvas, snap, font, width, height, radarIndex, screenRepeat, white, yellow);
                break;
            case WeatherStarScreen.ExtendedForecast:
                DrawForecast(canvas, snap, font, 6, width, white, yellow);
                break;
            case WeatherStarScreen.Regional:
                DrawRegional(canvas, snap, font, width, height, white, yellow);
                break;
            case WeatherStarScreen.Hazards:
                DrawHazards(canvas, snap, font, width, height, radarIndex, white, yellow);
                break;
            case WeatherStarScreen.Radar:
                DrawRadar(canvas, snap, font, width, height, radarIndex, white);
                break;
            case WeatherStarScreen.Almanac:
                DrawForecast(canvas, snap, font, 4, width, white, yellow);
                break;
            case WeatherStarScreen.SpcOutlook:
                DrawSpcOutlook(canvas, snap, font, width, white, yellow);
                break;
            case WeatherStarScreen.Travel:
                DrawTravel(canvas, snap, font, width, height, radarIndex, white, yellow);
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
        var wide = width > 700;
        var leftX = wide ? 90f : 72f;
        var rightX = wide ? width * 0.52f : 318f;
        var valueX = width - (wide ? 90f : 78f);
        var top = 108f;

        DrawText(
            canvas,
            Math.Round(cur.Temperature).ToString("0", CultureInfo.InvariantCulture) + "°",
            large,
            wide ? 42 : 34,
            leftX,
            top,
            white);
        DrawText(canvas, ShortenWeather(cur.ConditionText), font, 20, leftX, top + 32, yellow);

        var icon = _assets.Icon(cur.IconKey);
        if (icon is not null)
        {
            var iconSize = wide ? 150f : 118f;
            DrawBitmap(canvas, icon, new SKRect(leftX + 20, top + 48, leftX + 20 + iconSize, top + 48 + iconSize));
        }

        var windValueX = wide ? leftX + 220 : 290f;
        DrawText(canvas, "Wind:", font, 20, leftX, 318, white);
        DrawText(canvas, FormatCurrentWind(cur), font, 20, windValueX, 318, white, SKTextAlign.Right);
        if (cur.WindGust is double gust && gust > (cur.WindSpeed ?? 0) + 0.5)
        {
            DrawText(
                canvas,
                "Gusts to " + Math.Round(gust).ToString("0", CultureInfo.InvariantCulture),
                font,
                20,
                windValueX,
                348,
                white,
                SKTextAlign.Right);
        }

        var y = 108f;
        DrawText(canvas, Truncate(cur.StationName ?? snap.Place.DisplayName, wide ? 22 : 16).ToUpperInvariant(), font, 18, rightX, y, yellow);
        y += 32;
        DrawCurrentRow(canvas, font, "Humidity:", cur.Humidity is int humidity ? humidity + "%" : "-", rightX, valueX, y, white);
        y += 32;
        DrawCurrentRow(
            canvas,
            font,
            "Dewpoint:",
            cur.Dewpoint is double dew ? Math.Round(dew).ToString("0", CultureInfo.InvariantCulture) + "°" : "-",
            rightX,
            valueX,
            y,
            white);
        y += 32;
        DrawCurrentRow(canvas, font, "Ceiling:", FormatCeiling(cur.Ceiling, snap.UseMetric), rightX, valueX, y, white);
        y += 32;
        DrawCurrentRow(canvas, font, "Visibility:", FormatVisibility(cur.Visibility, snap.UseMetric), rightX, valueX, y, white);
        y += 32;
        if (cur.Pressure is double pressure)
        {
            var pressureText = snap.UseMetric
                ? Math.Round(pressure).ToString("0", CultureInfo.InvariantCulture) + " mb"
                : pressure.ToString("0.00", CultureInfo.InvariantCulture) + " in";
            if (!string.IsNullOrWhiteSpace(cur.PressureDirection))
            {
                pressureText += " " + cur.PressureDirection;
            }

            DrawCurrentRow(canvas, font, "Pressure:", pressureText, rightX, valueX, y, white);
            y += 32;
        }

        if (!string.IsNullOrWhiteSpace(cur.ApparentLabel) && cur.FeelsLike is double feels)
        {
            DrawCurrentRow(
                canvas,
                font,
                cur.ApparentLabel,
                Math.Round(feels).ToString("0", CultureInfo.InvariantCulture) + "°" + unit,
                rightX,
                valueX,
                y,
                white);
        }
    }

    private static void DrawCurrentRow(
        SKCanvas canvas,
        SKTypeface font,
        string label,
        string value,
        float labelX,
        float valueX,
        float y,
        SKPaint color)
    {
        DrawText(canvas, label, font, 18, labelX, y, color);
        DrawText(canvas, value, font, 18, valueX, y, color, SKTextAlign.Right);
    }

    private static string FormatCurrentWind(WeatherCurrent cur)
    {
        if (cur.WindSpeed is null or <= 0)
        {
            return "Calm";
        }

        var dir = string.IsNullOrWhiteSpace(cur.WindDirection) ? "" : cur.WindDirection + " ";
        return dir + Math.Round(cur.WindSpeed.Value).ToString("0", CultureInfo.InvariantCulture);
    }

    private static string FormatCeiling(double? ceiling, bool metric)
    {
        if (ceiling is null or <= 0)
        {
            return "Unlimited";
        }

        return Math.Round(ceiling.Value).ToString("0", CultureInfo.InvariantCulture) + (metric ? " m." : " ft.");
    }

    private static string FormatVisibility(double? visibility, bool metric)
    {
        if (visibility is null)
        {
            return "-";
        }

        return Math.Round(visibility.Value).ToString("0", CultureInfo.InvariantCulture) + (metric ? " km." : " mi.");
    }

    private static void DrawObservations(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, SKPaint white, SKPaint yellow)
    {
        var rows = snap.Observations;
        if (rows.Count == 0 && snap.Current is { } cur)
        {
            rows =
            [
                new WeatherStationObservation
                {
                    Location = Truncate(cur.StationName ?? snap.Place.DisplayName, 16),
                    Temperature = cur.Temperature,
                    Weather = ShortenWeather(cur.ConditionText),
                    Wind = FormatWind(cur.WindDirection, cur.WindSpeed, snap.UseMetric)
                }
            ];
        }

        var wide = width > 700;
        var locX = 24f;
        var tempX = wide ? 380f : 236f;
        var weatherX = wide ? 500f : 310f;
        var windX = wide ? 760f : 470f;
        var y = 92f;
        DrawText(canvas, "LOCATION", font, 16, locX, y, yellow);
        DrawText(canvas, snap.UseMetric ? "°C" : "°F", font, 16, tempX, y, yellow);
        DrawText(canvas, "WEATHER", font, 16, weatherX, y, yellow);
        DrawText(canvas, "WIND", font, 16, windX, y, yellow);

        if (rows.Count == 0)
        {
            DrawText(canvas, "NO STATION DATA", font, 22, locX, 180, white);
            return;
        }

        y = 128f;
        foreach (var row in rows.Take(7))
        {
            DrawText(canvas, Truncate(row.Location, wide ? 22 : 14).ToUpperInvariant(), font, 18, locX, y, yellow);
            DrawText(canvas, Math.Round(row.Temperature).ToString("0", CultureInfo.InvariantCulture), font, 18, tempX, y, white);
            DrawText(canvas, Truncate(row.Weather, wide ? 14 : 10), font, 18, weatherX, y, white);
            DrawText(canvas, row.Wind, font, 18, windX, y, white);
            y += 36;
        }
    }

    private static string FormatWind(string? direction, double? speed, bool metric)
    {
        if (speed is null || speed <= 0)
        {
            return "Calm";
        }

        var dir = string.IsNullOrWhiteSpace(direction) ? "" : direction + " ";
        return dir + Math.Round(speed.Value).ToString("0", CultureInfo.InvariantCulture) + (metric ? " km/h" : "");
    }

    private static string ShortenWeather(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return "-";
        }

        return condition
            .Replace("Light ", "L ", StringComparison.OrdinalIgnoreCase)
            .Replace("Heavy ", "H ", StringComparison.OrdinalIgnoreCase)
            .Replace("Partly ", "P ", StringComparison.OrdinalIgnoreCase)
            .Replace("Mostly ", "M ", StringComparison.OrdinalIgnoreCase)
            .Replace("Thunderstorm", "T'storm", StringComparison.OrdinalIgnoreCase)
            .Replace(" and ", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("Freezing Rain", "Frz Rn", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max].TrimEnd();

    private void DrawHourly(
        SKCanvas canvas,
        WeatherSnapshot snap,
        SKTypeface font,
        SKTypeface large,
        int width,
        int height,
        int radarIndex,
        SKPaint white,
        SKPaint gold)
    {
        var hours = UpcomingHours(snap);
        if (hours.Count == 0)
        {
            DrawText(canvas, "NO HOURLY DATA", font, 22, 20, 160, white);
            return;
        }

        const int pageSize = 4;
        var maxOffset = Math.Max(0, hours.Count - pageSize);
        var offset = maxOffset == 0 ? 0 : Math.Min(maxOffset, radarIndex * Math.Max(1, maxOffset) / 20);
        var page = hours.Skip(offset).Take(pageSize).ToList();
        var scale = width / 640f;
        var hourX = 25f * scale;
        var iconX = 248f * scale;
        var tempX = 355f * scale;
        var likeX = 425f * scale;
        var windRight = 605f * scale;
        var iconSize = 56f * scale;

        using var headerBar = new SKPaint { Color = new SKColor(32, 0, 87) };
        using var heat = new SKPaint { Color = new SKColor(0xEE, 0x00, 0x00), IsAntialias = true };
        using var chill = new SKPaint { Color = new SKColor(0x80, 0x80, 0xFF), IsAntialias = true };
        canvas.DrawRect(0, 76, width, 22, headerBar);
        DrawText(canvas, "TEMP", font, 16, tempX, 94, gold);
        DrawText(canvas, "LIKE", font, 16, likeX, 94, gold);
        DrawText(canvas, "WIND", font, 16, windRight - 70, 94, gold);

        var y = 148f;
        const float row = 72f;
        foreach (var hour in page)
        {
            var local = InPlace(hour.Time, snap);
            DrawText(canvas, local.ToString("ddd h tt", CultureInfo.InvariantCulture), large, 22, hourX, y, gold);
            var icon = _assets.Icon(hour.IconKey);
            if (icon is not null)
            {
                DrawBitmap(canvas, icon, new SKRect(iconX, y - 42, iconX + iconSize, y + 14));
            }

            DrawText(canvas, Math.Round(hour.Temperature).ToString("0", CultureInfo.InvariantCulture), large, 24, tempX, y, white);
            var feels = hour.FeelsLike ?? hour.Temperature;
            var likePaint = feels < hour.Temperature - 0.5 ? chill
                : feels > hour.Temperature + 0.5 ? heat
                : white;
            DrawText(canvas, Math.Round(feels).ToString("0", CultureInfo.InvariantCulture), large, 24, likeX, y, likePaint);
            DrawText(canvas, FormatHourlyWind(hour), large, 22, windRight, y, white, SKTextAlign.Right);
            y += row;
            if (y > height - 36)
            {
                break;
            }
        }
    }

    private static void DrawHourlyGraph(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, int height, SKPaint white, SKPaint gold)
    {
        var hours = UpcomingHours(snap).Take(36).ToList();
        if (hours.Count < 2)
        {
            DrawText(canvas, "NO HOURLY DATA", font, 22, 20, 160, white);
            return;
        }

        using var tempPaint = new SKPaint { Color = new SKColor(0xFF, 0x40, 0x40), IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        using var dewPaint = new SKPaint { Color = new SKColor(0x33, 0xCC, 0x55), IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        using var precipPaint = new SKPaint { Color = new SKColor(0x40, 0xE0, 0xE0), IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        using var cloudPaint = new SKPaint { Color = new SKColor(0xC0, 0xC0, 0xC0), IsAntialias = true, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
        using var tempFill = new SKPaint { Color = tempPaint.Color, IsAntialias = true };
        using var dewFill = new SKPaint { Color = dewPaint.Color, IsAntialias = true };
        using var precipFill = new SKPaint { Color = precipPaint.Color, IsAntialias = true };

        DrawText(canvas, "Temp", font, 14, width - 220, 78, tempFill);
        DrawText(canvas, "Dew", font, 14, width - 150, 78, dewFill);
        DrawText(canvas, "Precip%", font, 14, width - 90, 78, precipFill);

        var left = 58f;
        var right = width - 28f;
        var top = 96f;
        var bottom = height - 52f;
        var temps = hours.Select(h => h.Temperature).ToList();
        var dews = hours.Select(h => h.Dewpoint ?? h.Temperature).ToList();
        var min = Math.Min(temps.Min(), dews.Min()) - 2;
        var max = Math.Max(temps.Max(), dews.Max()) + 2;
        if (Math.Abs(max - min) < 1)
        {
            max = min + 10;
        }

        DrawText(canvas, Math.Round(max) + "°", font, 14, 8, top + 8, white);
        DrawText(canvas, Math.Round((max + min) / 2) + "°", font, 14, 8, (top + bottom) / 2, white);
        DrawText(canvas, Math.Round(min) + "°", font, 14, 8, bottom - 4, white);

        float X(int i) => left + (right - left) * i / Math.Max(1, hours.Count - 1);
        float YTemp(double v) => bottom - (float)((v - min) / (max - min) * (bottom - top));
        float YPct(int? v) => bottom - (v.GetValueOrDefault() / 100f * (bottom - top));

        DrawPolyline(canvas, hours.Count, i => X(i), i => YTemp(dews[i]), dewPaint);
        DrawPolyline(canvas, hours.Count, i => X(i), i => YTemp(temps[i]), tempPaint);
        if (hours.Any(h => h.PrecipitationChance.HasValue))
        {
            DrawPolyline(canvas, hours.Count, i => X(i), i => YPct(hours[i].PrecipitationChance), precipPaint);
        }

        if (hours.Any(h => h.CloudCover.HasValue))
        {
            DrawPolyline(canvas, hours.Count, i => X(i), i => YPct(hours[i].CloudCover), cloudPaint);
        }

        var tick = Math.Max(1, hours.Count / 6);
        for (var i = 0; i < hours.Count; i += tick)
        {
            var label = InPlace(hours[i].Time, snap).ToString("htt", CultureInfo.InvariantCulture).ToLowerInvariant();
            DrawText(canvas, label, font, 12, X(i) - 10, height - 20, gold);
        }
    }

    private static void DrawPolyline(SKCanvas canvas, int count, Func<int, float> xAt, Func<int, float> yAt, SKPaint paint)
    {
        var builder = new SKPathBuilder();
        builder.MoveTo(xAt(0), yAt(0));
        for (var i = 1; i < count; i++)
        {
            builder.LineTo(xAt(i), yAt(i));
        }

        using var path = builder.Detach();
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 160),
            IsAntialias = true,
            StrokeWidth = paint.StrokeWidth + 2,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawPath(path, shadow);
        canvas.DrawPath(path, paint);
    }

    private static List<WeatherHourly> UpcomingHours(WeatherSnapshot snap)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-20);
        return snap.Hourly.Where(hour => hour.Time >= cutoff).Take(24).ToList();
    }

    private static string FormatHourlyWind(WeatherHourly hour)
    {
        if (hour.WindSpeed is null or <= 0)
        {
            return "Calm";
        }

        var dir = string.IsNullOrWhiteSpace(hour.WindDirection) ? "" : hour.WindDirection + " ";
        return dir + Math.Round(hour.WindSpeed.Value).ToString("0", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset PlaceNow(WeatherSnapshot snap) => InPlace(DateTimeOffset.UtcNow, snap);

    private static DateTimeOffset InPlace(DateTimeOffset time, WeatherSnapshot snap)
    {
        if (!string.IsNullOrWhiteSpace(snap.Place.Timezone)
            && TimeZoneInfo.TryFindSystemTimeZoneById(snap.Place.Timezone, out var tz))
        {
            return TimeZoneInfo.ConvertTime(time, tz);
        }

        return time;
    }

    private void DrawForecast(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int count, int width, SKPaint white, SKPaint gold)
    {
        var y = 88;
        foreach (var day in snap.Daily.Take(count))
        {
            DrawText(canvas, day.Name.ToUpperInvariant(), font, 18, 16, y, gold);
            var temps = FormatDayTemps(day);
            if (temps.Length > 0)
            {
                DrawText(canvas, temps, font, 18, width - 140, y, white);
            }

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

    private void DrawRegional(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, int height, SKPaint white, SKPaint gold)
    {
        var cities = snap.Regional;
        if (cities.Count == 0)
        {
            cities = snap.Observations.Select(row => new WeatherRegionalCity
            {
                Name = row.Location,
                IconKey = row.IconKey,
                High = row.Temperature
            }).ToList();
        }

        if (cities.Count == 0 && snap.Daily.Count > 0)
        {
            var today = snap.Daily[0];
            cities =
            [
                new WeatherRegionalCity
                {
                    Name = snap.Place.DisplayName,
                    IconKey = today.IconKey,
                    High = today.High,
                    Low = today.Low
                }
            ];
        }

        if (cities.Count == 0)
        {
            DrawText(canvas, "NO REGIONAL DATA", font, 22, 24, 180, white);
            return;
        }

        var columns = 3;
        var colWidth = (width - 48f) / columns;
        var rows = (int)Math.Ceiling(Math.Min(6, cities.Count) / (double)columns);
        var rowHeight = Math.Min(150f, (height - 120f) / Math.Max(1, rows));
        for (var i = 0; i < Math.Min(6, cities.Count); i++)
        {
            var city = cities[i];
            var col = i % columns;
            var row = i / columns;
            var x = 24f + col * colWidth;
            var y = 100f + row * rowHeight;
            DrawText(canvas, Truncate(city.Name, 14).ToUpperInvariant(), font, 16, x, y, gold);
            var icon = _assets.Icon(city.IconKey);
            if (icon is not null)
            {
                DrawBitmap(canvas, icon, new SKRect(x + 36, y + 8, x + 108, y + 80));
            }

            var temps = FormatRegionalTemps(city);
            DrawText(canvas, temps, font, 22, x + 20, y + 102, white);
        }
    }

    private static string FormatRegionalTemps(WeatherRegionalCity city)
    {
        if (city.High is double high && city.Low is double low)
        {
            return Math.Round(high).ToString("0", CultureInfo.InvariantCulture)
                + "/"
                + Math.Round(low).ToString("0", CultureInfo.InvariantCulture);
        }

        if (city.High is double hi)
        {
            return Math.Round(hi).ToString("0", CultureInfo.InvariantCulture);
        }

        if (city.Low is double lo)
        {
            return Math.Round(lo).ToString("0", CultureInfo.InvariantCulture);
        }

        return "";
    }

    private void DrawTravel(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, int height, int radarIndex, SKPaint white, SKPaint gold)
    {
        var cities = snap.Travel;
        if (cities.Count == 0)
        {
            DrawText(canvas, "NO TRAVEL DATA", font, 22, 24, 180, white);
            return;
        }

        var wide = width > 700;
        var cityX = 28f;
        var iconX = wide ? 280f : 250f;
        var lowX = wide ? 400f : 340f;
        var highX = wide ? 500f : 430f;
        DrawText(canvas, "LOW", font, 16, lowX, 92, gold);
        DrawText(canvas, "HIGH", font, 16, highX, 92, gold);

        const int pageSize = 7;
        var maxOffset = Math.Max(0, cities.Count - pageSize);
        var offset = maxOffset == 0 ? 0 : Math.Min(maxOffset, radarIndex * Math.Max(1, maxOffset) / 20);
        var y = 128f;
        var row = (height - 170f) / pageSize;
        foreach (var city in cities.Skip(offset).Take(pageSize))
        {
            DrawText(canvas, Truncate(city.Name, 16).ToUpperInvariant(), font, 16, cityX, y, gold);
            var icon = _assets.Icon(city.IconKey);
            if (icon is not null)
            {
                DrawBitmap(canvas, icon, new SKRect(iconX, y - 22, iconX + 36, y + 14));
            }

            if (city.Low is double low)
            {
                DrawText(canvas, Math.Round(low).ToString("0", CultureInfo.InvariantCulture), font, 18, lowX, y, white);
            }

            if (city.High is double high)
            {
                DrawText(canvas, Math.Round(high).ToString("0", CultureInfo.InvariantCulture), font, 18, highX, y, white);
            }

            y += row;
        }
    }

    private static void DrawSpcOutlook(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, SKPaint white, SKPaint gold)
    {
        if (!snap.IsUnitedStates)
        {
            DrawText(canvas, "U.S. CONVECTIVE OUTLOOK", font, 22, 24, 160, gold);
            DrawText(canvas, "Not available outside the United States.", font, 16, 24, 200, white);
            return;
        }

        var days = snap.SpcOutlook;
        if (days.Count == 0)
        {
            DrawText(canvas, "OUTLOOK UNAVAILABLE", font, 22, 24, 180, white);
            return;
        }

        DrawText(canvas, "CATEGORICAL OUTLOOK", font, 14, 24, 88, gold);
        var y = 120f;
        foreach (var day in days.Take(3))
        {
            DrawText(canvas, day.DayName.ToUpperInvariant(), font, 18, 16, y + 28, gold);
            var (barWidth, color) = SpcBar(day.RiskLabel, width);
            if (barWidth > 0)
            {
                using var fill = new SKPaint { Color = color, IsAntialias = true };
                using var edge = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                var rect = new SKRect(200, y + 8, 200 + barWidth, y + 44);
                canvas.DrawRect(rect, fill);
                canvas.DrawRect(rect, edge);
            }

            DrawText(canvas, day.RiskText.ToUpperInvariant(), font, 16, 212, y + 28, white);
            y += 70;
        }
    }

    private static (float Width, SKColor Color) SpcBar(string label, int width)
    {
        var max = Math.Max(120f, width - 240f);
        return label.ToUpperInvariant() switch
        {
            "TSTM" => (max * 0.22f, new SKColor(0xC0, 0xE8, 0x70)),
            "MRGL" => (max * 0.38f, new SKColor(0x00, 0xBB, 0x00)),
            "SLGT" => (max * 0.54f, new SKColor(0xFF, 0xE1, 0x00)),
            "ENH" => (max * 0.70f, new SKColor(0xFF, 0x99, 0x00)),
            "MDT" => (max * 0.85f, new SKColor(0xFF, 0x66, 0x00)),
            "HIGH" => (max, new SKColor(0xFF, 0x20, 0x20)),
            _ => (0, SKColors.Transparent)
        };
    }

    private static void DrawLocalForecast(
        SKCanvas canvas,
        WeatherSnapshot snap,
        SKTypeface font,
        int width,
        int height,
        int radarIndex,
        int screenRepeat,
        SKPaint white,
        SKPaint gold)
    {
        var periods = snap.Periods.Take(6).ToList();
        if (periods.Count == 0)
        {
            periods = snap.Daily.Take(6).Select(day => new WeatherForecastPeriod
            {
                Name = day.Name,
                Narrative = day.Narrative,
                IconKey = day.IconKey,
                Temperature = day.High ?? day.Low ?? 0,
                IsDaytime = day.High is not null
            }).ToList();
        }

        if (periods.Count == 0)
        {
            DrawText(canvas, "NO FORECAST DATA", font, 22, 20, 160, white);
            return;
        }

        var period = periods[Math.Clamp(screenRepeat, 0, periods.Count - 1)];
        var narrative = period.Narrative.Trim();
        var ellipsis = narrative.IndexOf("...", StringComparison.Ordinal);
        if (ellipsis >= 0)
        {
            narrative = narrative[..ellipsis] + " " + narrative[(ellipsis + 3)..].TrimStart();
        }

        var text = period.Name.ToUpperInvariant() + "..." + narrative;
        var left = 74f;
        var maxWidth = width - 148f;
        var lines = WrapText(text, font, 22, maxWidth);
        const int visible = 7;
        var maxOffset = Math.Max(0, lines.Count - visible);
        var offset = maxOffset == 0 ? 0 : Math.Min(maxOffset, radarIndex * Math.Max(1, maxOffset) / 20);
        var y = 108f;
        var first = true;
        foreach (var line in lines.Skip(offset).Take(visible))
        {
            if (first && offset == 0)
            {
                var name = period.Name.ToUpperInvariant() + "...";
                if (line.StartsWith(name, StringComparison.Ordinal))
                {
                    DrawText(canvas, name, font, 22, left, y, gold);
                    var rest = line[name.Length..];
                    if (rest.Length > 0)
                    {
                        using var measure = new SKFont(font, 22);
                        DrawText(canvas, rest, font, 22, left + measure.MeasureText(name), y, white);
                    }
                }
                else
                {
                    DrawText(canvas, line, font, 22, left, y, gold);
                }
            }
            else
            {
                DrawText(canvas, line, font, 22, left, y, white);
            }

            first = false;
            y += 40;
            if (y > height - 40)
            {
                break;
            }
        }
    }

    private static string FormatDayTemps(WeatherDaily day)
    {
        if (day.High is double high && day.Low is double low)
        {
            return Math.Round(high).ToString("0", CultureInfo.InvariantCulture)
                + "/"
                + Math.Round(low).ToString("0", CultureInfo.InvariantCulture);
        }

        if (day.High is double hi)
        {
            return Math.Round(hi).ToString("0", CultureInfo.InvariantCulture);
        }

        if (day.Low is double lo)
        {
            return Math.Round(lo).ToString("0", CultureInfo.InvariantCulture);
        }

        return "";
    }

    private static List<string> WrapText(string text, SKTypeface typeface, float size, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return lines;
        }

        using var skFont = new SKFont(typeface, size);
        var words = text.Replace("...", " ", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var word in words)
        {
            var trial = current.Length == 0 ? word : current + " " + word;
            if (skFont.MeasureText(trial) <= maxWidth || current.Length == 0)
            {
                current = trial;
                continue;
            }

            lines.Add(current);
            current = word;
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private static void DrawHazards(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, int height, int radarIndex, SKPaint white, SKPaint gold)
    {
        if (snap.Alerts.Count == 0)
        {
            DrawText(canvas, "NO WATCHES, WARNINGS, OR ADVISORIES", font, 18, 24, 200, gold);
            return;
        }

        var fs = Math.Max(16, width / 60);
        var wrapWidth = Math.Max(40, width - (width >= 1200 ? 160 : 80));
        var lines = new List<(bool Header, string Text)>();
        foreach (var alert in snap.Alerts.Take(5))
        {
            lines.Add((true, alert.Event.ToUpperInvariant()));
            lines.Add((false, ""));
            var body = string.IsNullOrWhiteSpace(alert.Description) ? alert.Headline : alert.Description;
            foreach (var wrap in WrapText(body.Replace('\n', ' '), font, fs, wrapWidth))
            {
                lines.Add((false, wrap.ToUpperInvariant()));
            }

            lines.Add((false, ""));
            lines.Add((false, ""));
        }

        var visible = width >= 1200 ? 16 : 12;
        var maxOffset = Math.Max(0, lines.Count - visible);
        var offset = maxOffset == 0 ? 0 : Math.Min(maxOffset, radarIndex * Math.Max(1, maxOffset) / 18);
        var y = width >= 1200 ? 90f : 70f;
        var step = fs + 12;
        foreach (var line in lines.Skip(offset).Take(visible))
        {
            if (line.Text.Length > 0)
            {
                DrawText(canvas, line.Text, font, line.Header ? fs + 4 : fs, 40, y, line.Header ? gold : white);
            }

            y += step;
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

        DrawText(canvas, InPlace(frame.Time, snap).ToString("h:mm tt", CultureInfo.InvariantCulture), font, 16, 24, height - 18, white);
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

    private static void DrawText(
        SKCanvas canvas,
        string text,
        SKTypeface typeface,
        float size,
        float x,
        float y,
        SKPaint color,
        SKTextAlign align = SKTextAlign.Left)
    {
        using var font = new SKFont(typeface, size);
        canvas.DrawText(text, x, y, align, font, color);
    }
}
