namespace FinTv.Weather;

public enum WeatherSourceKind
{
    Auto = 0,
    UnitedStates = 1,
    World = 2
}

public enum WeatherStarScreen
{
    Hazards,
    Current,
    Observations,
    Hourly,
    HourlyGraph,
    Travel,
    Regional,
    LocalForecast,
    ExtendedForecast,
    Almanac,
    SpcOutlook,
    Radar
}

public sealed record GeoPlace
{
    public required string Query { get; init; }

    public required string DisplayName { get; init; }

    public required double Latitude { get; init; }

    public required double Longitude { get; init; }

    public string? CountryCode { get; init; }

    public string? Admin1 { get; init; }

    public string? Timezone { get; init; }
}

public sealed class WeatherCurrent
{
    public string IconKey { get; init; } = "No-Data";

    public string ConditionText { get; init; } = "No data";

    public double Temperature { get; init; }

    public double? FeelsLike { get; init; }

    public double? Dewpoint { get; init; }

    public int? Humidity { get; init; }

    public double? WindSpeed { get; init; }

    public string? WindDirection { get; init; }

    public double? Pressure { get; init; }

    public double? Visibility { get; init; }

    public string? StationName { get; init; }
}

public sealed class WeatherHourly
{
    public DateTimeOffset Time { get; init; }

    public double Temperature { get; init; }

    public string IconKey { get; init; } = "No-Data";

    public int? PrecipitationChance { get; init; }
}

public sealed class WeatherDaily
{
    public DateOnly Date { get; init; }

    public string Name { get; init; } = "";

    public string IconKey { get; init; } = "No-Data";

    public string Narrative { get; init; } = "";

    public double High { get; init; }

    public double Low { get; init; }
}

public sealed class WeatherAlert
{
    public string Event { get; init; } = "";

    public string Headline { get; init; } = "";

    public string Severity { get; init; } = "";
}

public sealed class WeatherRadarFrame
{
    public DateTimeOffset Time { get; init; }

    public byte[] Image { get; init; } = [];
}

public sealed class WeatherSnapshot
{
    public required GeoPlace Place { get; init; }

    public required bool IsUnitedStates { get; init; }

    public required string Backend { get; init; }

    public required bool UseMetric { get; init; }

    public WeatherCurrent? Current { get; init; }

    public IReadOnlyList<WeatherHourly> Hourly { get; init; } = [];

    public IReadOnlyList<WeatherDaily> Daily { get; init; } = [];

    public IReadOnlyList<WeatherAlert> Alerts { get; init; } = [];

    public IReadOnlyList<WeatherRadarFrame> Radar { get; init; } = [];

    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
}
