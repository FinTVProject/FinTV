using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using FinTv.Services;
using Microsoft.Extensions.Logging;

namespace FinTv.Weather;

public sealed class WeatherDataClient
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(8);
    private readonly ConcurrentDictionary<string, (WeatherSnapshot Snap, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _http;
    private readonly WeatherGeocoder _geocoder;
    private readonly ILogger<WeatherDataClient> _logger;

    public WeatherDataClient(IHttpClientFactory http, WeatherGeocoder geocoder, ILogger<WeatherDataClient> logger)
    {
        _http = http;
        _geocoder = geocoder;
        _logger = logger;
    }

    public async Task<WeatherSnapshot> GetSnapshotAsync(
        string locationQuery,
        WeatherSourceKind source,
        bool useMetric,
        CancellationToken cancellationToken)
    {
        var place = await _geocoder.ResolveAsync(locationQuery, cancellationToken);
        var isUs = IsUnitedStates(place, locationQuery);
        var backend = ResolveBackend(source, isUs);
        var cacheKey = $"{backend}|{place.Latitude:F3}|{place.Longitude:F3}|{(useMetric ? "si" : "us")}";
        if (_cache.TryGetValue(cacheKey, out var hit) && hit.Expires > DateTimeOffset.UtcNow)
        {
            return hit.Snap;
        }

        WeatherSnapshot snap;
        try
        {
            snap = backend == "noaa"
                ? await FetchNoaaAsync(place, useMetric, cancellationToken)
                : await FetchOpenMeteoAsync(place, isUs, useMetric, cancellationToken);
        }
        catch (Exception ex) when (backend == "noaa" && ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NOAA weather failed for {Place}; using Open-Meteo", place.DisplayName);
            snap = await FetchOpenMeteoAsync(place, isUs, useMetric, cancellationToken);
        }

        _cache[cacheKey] = (snap, DateTimeOffset.UtcNow.Add(CacheTtl));
        return snap;
    }

    public static WeatherSourceKind ParseSource(string? value)
    {
        if (string.Equals(value, "us", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "unitedstates", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "noaa", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherSourceKind.UnitedStates;
        }

        if (string.Equals(value, "world", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "openmeteo", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherSourceKind.World;
        }

        return WeatherSourceKind.Auto;
    }

    private static string ResolveBackend(WeatherSourceKind source, bool isUs)
        => source switch
        {
            WeatherSourceKind.UnitedStates => "noaa",
            WeatherSourceKind.World => "open-meteo",
            _ => isUs ? "noaa" : "open-meteo"
        };

    private static bool IsUnitedStates(GeoPlace place, string query)
    {
        if (string.Equals(place.CountryCode, "US", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return WeatherLocationParser.ExtractZip(query) is not null && query.Length <= 12;
    }

    private HttpClient Client() => _http.CreateClient("Weather");

    private async Task<WeatherSnapshot> FetchOpenMeteoAsync(
        GeoPlace place,
        bool isUs,
        bool useMetric,
        CancellationToken cancellationToken)
    {
        var temp = useMetric ? "celsius" : "fahrenheit";
        var wind = useMetric ? "kmh" : "mph";
        var url =
            "https://api.open-meteo.com/v1/forecast?latitude="
            + place.Latitude.ToString(CultureInfo.InvariantCulture)
            + "&longitude=" + place.Longitude.ToString(CultureInfo.InvariantCulture)
            + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,wind_direction_10m,dew_point_2m,surface_pressure,visibility"
            + "&hourly=temperature_2m,weather_code,precipitation_probability"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
            + "&temperature_unit=" + temp
            + "&wind_speed_unit=" + wind
            + "&timezone=auto";
        using var doc = JsonDocument.Parse(await Client().GetStringAsync(url, cancellationToken));
        var root = doc.RootElement;
        var current = root.GetProperty("current");
        var code = current.GetProperty("weather_code").GetInt32();
        var night = DateTimeOffset.UtcNow.Hour is >= 0 and < 6 or >= 20;
        var snapCurrent = new WeatherCurrent
        {
            IconKey = WeatherIconMap.FromWmo(code, night),
            ConditionText = WeatherIconMap.FromWmoText(code),
            Temperature = current.GetProperty("temperature_2m").GetDouble(),
            FeelsLike = GetDouble(current, "apparent_temperature"),
            Dewpoint = GetDouble(current, "dew_point_2m"),
            Humidity = GetInt(current, "relative_humidity_2m"),
            WindSpeed = GetDouble(current, "wind_speed_10m"),
            WindDirection = current.TryGetProperty("wind_direction_10m", out var wd) ? WeatherIconMap.Cardinal(wd.GetDouble()) : null,
            Pressure = GetDouble(current, "surface_pressure"),
            Visibility = GetDouble(current, "visibility"),
            StationName = place.DisplayName
        };

        var hourly = ReadUpcomingHourly(root, maxCount: 24);

        var daily = new List<WeatherDaily>();
        if (root.TryGetProperty("daily", out var dailyEl))
        {
            var times = dailyEl.GetProperty("time");
            var max = dailyEl.GetProperty("temperature_2m_max");
            var min = dailyEl.GetProperty("temperature_2m_min");
            var codes = dailyEl.GetProperty("weather_code");
            var count = Math.Min(7, times.GetArrayLength());
            for (var i = 0; i < count; i++)
            {
                var date = DateOnly.Parse(times[i].GetString()!, CultureInfo.InvariantCulture);
                daily.Add(new WeatherDaily
                {
                    Date = date,
                    Name = i == 0 ? "Today" : date.ToDateTime(TimeOnly.MinValue).ToString("dddd"),
                    IconKey = WeatherIconMap.FromWmo(codes[i].GetInt32()),
                    Narrative = WeatherIconMap.FromWmoText(codes[i].GetInt32()),
                    High = max[i].GetDouble(),
                    Low = min[i].GetDouble()
                });
            }
        }

        return new WeatherSnapshot
        {
            Place = place,
            IsUnitedStates = isUs,
            Backend = "open-meteo",
            UseMetric = useMetric,
            Current = snapCurrent,
            Hourly = hourly,
            Daily = daily,
            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<WeatherSnapshot> FetchNoaaAsync(GeoPlace place, bool useMetric, CancellationToken cancellationToken)
    {
        var client = Client();
        var lat = place.Latitude.ToString("F4", CultureInfo.InvariantCulture);
        var lon = place.Longitude.ToString("F4", CultureInfo.InvariantCulture);
        using var pointsDoc = JsonDocument.Parse(
            await client.GetStringAsync($"https://api.weather.gov/points/{lat},{lon}", cancellationToken));
        var props = pointsDoc.RootElement.GetProperty("properties");
        var forecastUrl = props.GetProperty("forecast").GetString();
        var hourlyUrl = props.GetProperty("forecastHourly").GetString();
        var stationsUrl = props.TryGetProperty("observationStations", out var st) ? st.GetString() : null;
        var relative = props.TryGetProperty("relativeLocation", out var rel) ? rel : default;
        var city = relative.ValueKind == JsonValueKind.Object
            && relative.TryGetProperty("properties", out var rp)
            && rp.TryGetProperty("city", out var cityEl)
            ? cityEl.GetString()
            : place.DisplayName;
        var state = relative.ValueKind == JsonValueKind.Object
            && relative.TryGetProperty("properties", out var rp2)
            && rp2.TryGetProperty("state", out var stateEl)
            ? stateEl.GetString()
            : place.Admin1;
        var named = new GeoPlace
        {
            Query = place.Query,
            DisplayName = string.Join(", ", new[] { city, state }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Latitude = place.Latitude,
            Longitude = place.Longitude,
            CountryCode = "US",
            Admin1 = state,
            Timezone = place.Timezone
        };

        WeatherCurrent? current = null;
        if (!string.IsNullOrWhiteSpace(stationsUrl))
        {
            current = await TryObservationAsync(client, stationsUrl, useMetric, cancellationToken);
        }

        var daily = new List<WeatherDaily>();
        if (!string.IsNullOrWhiteSpace(forecastUrl))
        {
            using var forecastDoc = JsonDocument.Parse(await client.GetStringAsync(forecastUrl, cancellationToken));
            var byDay = new Dictionary<string, WeatherDaily>(StringComparer.OrdinalIgnoreCase);
            foreach (var period in forecastDoc.RootElement.GetProperty("properties").GetProperty("periods").EnumerateArray())
            {
                var name = period.GetProperty("name").GetString() ?? "";
                var isDay = !period.TryGetProperty("isDaytime", out var dayEl) || dayEl.GetBoolean();
                var temp = period.GetProperty("temperature").GetDouble();
                if (useMetric)
                {
                    temp = (temp - 32) * 5 / 9;
                }

                var dayName = name.Replace(" Night", "", StringComparison.OrdinalIgnoreCase);
                if (!byDay.TryGetValue(dayName, out var existing))
                {
                    byDay[dayName] = new WeatherDaily
                    {
                        Date = DateOnly.FromDateTime(period.GetProperty("startTime").GetDateTime()),
                        Name = dayName,
                        IconKey = WeatherIconMap.FromNwsIcon(period.TryGetProperty("icon", out var icon) ? icon.GetString() : null, period.GetProperty("shortForecast").GetString()),
                        Narrative = period.GetProperty("detailedForecast").GetString() ?? period.GetProperty("shortForecast").GetString() ?? "",
                        High = isDay ? temp : temp,
                        Low = isDay ? temp : temp
                    };
                }
                else if (isDay)
                {
                    byDay[dayName] = new WeatherDaily
                    {
                        Date = existing.Date,
                        Name = existing.Name,
                        IconKey = WeatherIconMap.FromNwsIcon(period.TryGetProperty("icon", out var icon2) ? icon2.GetString() : null, period.GetProperty("shortForecast").GetString()),
                        Narrative = period.GetProperty("detailedForecast").GetString() ?? existing.Narrative,
                        High = temp,
                        Low = existing.Low
                    };
                }
                else
                {
                    byDay[dayName] = new WeatherDaily
                    {
                        Date = existing.Date,
                        Name = existing.Name,
                        IconKey = existing.IconKey,
                        Narrative = existing.Narrative,
                        High = existing.High,
                        Low = temp
                    };
                }

                if (byDay.Count >= 8 && !isDay)
                {
                    break;
                }
            }

            daily.AddRange(byDay.Values);
            current ??= daily.Count > 0
                ? new WeatherCurrent
                {
                    IconKey = daily[0].IconKey,
                    ConditionText = daily[0].Narrative.Split('.')[0],
                    Temperature = daily[0].High,
                    StationName = named.DisplayName
                }
                : null;
        }

        var hourly = new List<WeatherHourly>();
        if (!string.IsNullOrWhiteSpace(hourlyUrl))
        {
            using var hourlyDoc = JsonDocument.Parse(await client.GetStringAsync(hourlyUrl, cancellationToken));
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-20);
            foreach (var period in hourlyDoc.RootElement.GetProperty("properties").GetProperty("periods").EnumerateArray())
            {
                var start = period.GetProperty("startTime").GetDateTimeOffset();
                if (start < cutoff)
                {
                    continue;
                }

                var temp = period.GetProperty("temperature").GetDouble();
                if (useMetric)
                {
                    temp = (temp - 32) * 5 / 9;
                }

                hourly.Add(new WeatherHourly
                {
                    Time = start,
                    Temperature = temp,
                    IconKey = WeatherIconMap.FromNwsIcon(period.TryGetProperty("icon", out var icon) ? icon.GetString() : null, period.GetProperty("shortForecast").GetString()),
                    PrecipitationChance = period.TryGetProperty("probabilityOfPrecipitation", out var pop)
                        && pop.TryGetProperty("value", out var pv)
                        && pv.ValueKind == JsonValueKind.Number
                            ? pv.GetInt32()
                            : null
                });
                if (hourly.Count >= 24)
                {
                    break;
                }
            }
        }

        var alerts = new List<WeatherAlert>();
        try
        {
            using var alertDoc = JsonDocument.Parse(
                await client.GetStringAsync($"https://api.weather.gov/alerts/active?point={lat},{lon}", cancellationToken));
            foreach (var feature in alertDoc.RootElement.GetProperty("features").EnumerateArray().Take(8))
            {
                var ap = feature.GetProperty("properties");
                alerts.Add(new WeatherAlert
                {
                    Event = ap.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "",
                    Headline = ap.TryGetProperty("headline", out var hl) ? hl.GetString() ?? "" : "",
                    Severity = ap.TryGetProperty("severity", out var sv) ? sv.GetString() ?? "" : ""
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NWS alerts fetch failed");
        }

        var radar = await FetchRadarAsync(client, cancellationToken);

        return new WeatherSnapshot
        {
            Place = named,
            IsUnitedStates = true,
            Backend = "noaa",
            UseMetric = useMetric,
            Current = current,
            Hourly = hourly,
            Daily = daily,
            Alerts = alerts,
            Radar = radar,
            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<WeatherCurrent?> TryObservationAsync(
        HttpClient client,
        string stationsUrl,
        bool useMetric,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stationsDoc = JsonDocument.Parse(await client.GetStringAsync(stationsUrl, cancellationToken));
            var features = stationsDoc.RootElement.GetProperty("features");
            if (features.GetArrayLength() == 0)
            {
                return null;
            }

            var stationId = features[0].GetProperty("properties").GetProperty("stationIdentifier").GetString();
            var stationName = features[0].GetProperty("properties").TryGetProperty("name", out var nm) ? nm.GetString() : stationId;
            using var obsDoc = JsonDocument.Parse(
                await client.GetStringAsync($"https://api.weather.gov/stations/{stationId}/observations/latest", cancellationToken));
            var p = obsDoc.RootElement.GetProperty("properties");
            var tempC = GetUnitValue(p, "temperature");
            if (tempC is null)
            {
                return null;
            }

            var temp = useMetric ? tempC.Value : tempC.Value * 9 / 5 + 32;
            var dew = GetUnitValue(p, "dewpoint");
            var wind = GetUnitValue(p, "windSpeed");
            var vis = GetUnitValue(p, "visibility");
            var pressure = GetUnitValue(p, "barometricPressure") ?? GetUnitValue(p, "seaLevelPressure");
            return new WeatherCurrent
            {
                IconKey = WeatherIconMap.FromNwsIcon(p.TryGetProperty("icon", out var icon) ? icon.GetString() : null, p.TryGetProperty("textDescription", out var td) ? td.GetString() : null),
                ConditionText = p.TryGetProperty("textDescription", out var desc) ? desc.GetString() ?? "Current conditions" : "Current conditions",
                Temperature = temp,
                FeelsLike = temp,
                Dewpoint = dew is null ? null : useMetric ? dew : dew.Value * 9 / 5 + 32,
                Humidity = GetUnitValue(p, "relativeHumidity") is double h ? (int)Math.Round(h) : null,
                WindSpeed = wind is null ? null : useMetric ? wind : wind.Value * 2.23694,
                WindDirection = GetUnitValue(p, "windDirection") is double deg ? WeatherIconMap.Cardinal(deg) : null,
                Pressure = pressure is null ? null : useMetric ? pressure / 100 : pressure / 3386.39,
                Visibility = vis is null ? null : useMetric ? vis / 1000 : vis / 1609.34,
                StationName = stationName
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NWS observation fetch failed");
            return null;
        }
    }

    private async Task<IReadOnlyList<WeatherRadarFrame>> FetchRadarAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var frames = new List<WeatherRadarFrame>();
        foreach (var offset in new[] { 0, 5, 10, 15, 20, 25 })
        {
            try
            {
                var url = $"https://mesonet.agron.iastate.edu/data/gis/images/4326/USCOMP/n0r_{offset}.png";
                var bytes = await client.GetByteArrayAsync(url, cancellationToken);
                if (bytes.Length > 100)
                {
                    frames.Add(new WeatherRadarFrame
                    {
                        Time = DateTimeOffset.UtcNow.AddMinutes(-offset),
                        Image = bytes
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mesonet radar frame {Offset} failed", offset);
            }
        }

        return frames;
    }

    private static List<WeatherHourly> ReadUpcomingHourly(JsonElement root, int maxCount)
    {
        var hourly = new List<WeatherHourly>();
        if (!root.TryGetProperty("hourly", out var hourlyEl)
            || !hourlyEl.TryGetProperty("time", out var times)
            || times.ValueKind != JsonValueKind.Array)
        {
            return hourly;
        }

        var temps = hourlyEl.GetProperty("temperature_2m");
        var codes = hourlyEl.GetProperty("weather_code");
        var pops = hourlyEl.TryGetProperty("precipitation_probability", out var pop) ? pop : default;
        var offset = root.TryGetProperty("utc_offset_seconds", out var offEl) && offEl.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(offEl.GetInt32())
            : TimeSpan.Zero;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-20);

        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            var raw = times[i].GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var local = DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None);
            var time = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
            if (time < cutoff)
            {
                continue;
            }

            hourly.Add(new WeatherHourly
            {
                Time = time,
                Temperature = temps[i].GetDouble(),
                IconKey = WeatherIconMap.FromWmo(codes[i].GetInt32()),
                PrecipitationChance = pops.ValueKind == JsonValueKind.Array && pops[i].ValueKind == JsonValueKind.Number
                    ? pops[i].GetInt32()
                    : null
            });
            if (hourly.Count >= maxCount)
            {
                break;
            }
        }

        return hourly;
    }

    private static double? GetDouble(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? GetUnitValue(JsonElement props, string name)
    {
        if (!props.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!node.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.GetDouble();
    }
}
