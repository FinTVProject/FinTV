using System.Threading.Channels;
using FinTv.Domain;
using FinTv.Streaming;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public class WeatherStarChannelService
{
    public const string DefaultWeatherStarBaseUrl = "http://127.0.0.1:8080";

    public const string DefaultWeatherLocationQuery = "50317, Des Moines, IA, USA";

    public const string DefaultWeatherStarPermalinkQuery =
        "hazards=true&current-weather=true&latest-observations=true&hourly=true&hourly-graph=true&travel=true&regional-forecast=true&local-forecast=true&extended-forecast=true&almanac=true&spc-outlook=true&radar=true&stickyKiosk=true&customTextEnable=false&speed=1.00&viewMode=standard&units=us&customText=&mediaVolume=0.75&wide=false&portrait=false&enhanced=false&scanLines=false";

    private static readonly HashSet<string> LocationQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "latLonQuery",
        "latLon",
        "txtLocation",
        "lat",
        "lon"
    };

    private static readonly HashSet<string> CaptureTimeQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "kiosk",
        "wide"
    };

    private const double CaptureFps = 10;

    private readonly ILogger<WeatherStarChannelService> _logger;
    private readonly FfmpegCommandBuilder _ffmpegBuilder;
    private readonly FfmpegEncodingService _encoding;
    private readonly EbsService _ebs;
    private readonly IFfmpegLocator _mediaEncoder;
    private readonly WeatherRendererHost _renderer;
    private readonly JellyfinCatalogService _catalog;
    private readonly IHttpClientFactory _http;

    public WeatherStarChannelService(
        ILogger<WeatherStarChannelService> logger,
        FfmpegCommandBuilder ffmpegBuilder,
        FfmpegEncodingService encoding,
        EbsService ebs,
        IFfmpegLocator mediaEncoder,
        WeatherRendererHost renderer,
        JellyfinCatalogService catalog,
        IHttpClientFactory http)
    {
        _logger = logger;
        _ffmpegBuilder = ffmpegBuilder;
        _encoding = encoding;
        _ebs = ebs;
        _mediaEncoder = mediaEncoder;
        _renderer = renderer;
        _catalog = catalog;
        _http = http;
    }

    public async Task StreamAsync(Domain.Channel channel, Stream output, CancellationToken cancellationToken)
    {
        var variant = ResolveVariant(channel);
        await _renderer.EnsureRunningAsync(variant, cancellationToken);
        await _renderer.WaitUntilReadyAsync(variant, cancellationToken);

        var locationQuery = string.IsNullOrWhiteSpace(channel.WeatherLocationQuery)
            ? DefaultWeatherLocationQuery
            : channel.WeatherLocationQuery.Trim();
        var permalinkQuery = FinTvRuntime.Current?.Configuration.WeatherStarPermalinkQuery;
        var autoWideForSixteenNine = FinTvRuntime.Current?.Configuration.WeatherStarAutoWideForSixteenNine ?? true;
        var baseUrl = variant == WeatherStarDockerVariant.Ws3kp
            ? "http://127.0.0.1:8083"
            : "http://127.0.0.1:8080";
        var weatherPageUrl = BuildWeatherPageUrl(
            locationQuery,
            baseUrl,
            permalinkQuery,
            autoWideForSixteenNine,
            channel.AspectRatio);
        var (width, height) = GetResolution(channel);
        var ffmpegPath = _mediaEncoder.EncoderPath;
        var backgroundMusicPath = ResolveWeatherMusicPath();

        if (ChromiumCdpCapture.FindChromium() is null)
        {
            _logger.LogWarning("Chromium not found; using NOAA overlay fallback for {Channel}", channel.Name);
            await StreamNoaaFallbackAsync(channel, locationQuery, ffmpegPath, backgroundMusicPath, output, cancellationToken);
            return;
        }

        await using var capture = new ChromiumCdpCapture(_logger);
        try
        {
            await capture.StartAsync(weatherPageUrl, width, height, cancellationToken);
            using var frameStream = new ScreenshotFrameStream();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ffmpegStderr = new System.Text.StringBuilder();

            var ffmpegTask = CliWrap.Cli.Wrap(ffmpegPath)
                .WithArguments(_ffmpegBuilder.BuildWeatherCommand(width, height, CaptureFps, backgroundMusicPath))
                .WithStandardInputPipe(CliWrap.PipeSource.FromStream(frameStream))
                .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output, autoFlush: true))
                .WithStandardErrorPipe(CliWrap.PipeTarget.ToStringBuilder(ffmpegStderr))
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteAsync(linkedCts.Token);

            var pumpTask = PumpFramesAsync(capture, weatherPageUrl, frameStream, linkedCts.Token);
            var completed = await Task.WhenAny(ffmpegTask, pumpTask);
            if (completed == pumpTask)
            {
                await pumpTask;
            }

            linkedCts.Cancel();
            frameStream.Complete();
            try
            {
                await ffmpegTask;
            }
            catch (OperationCanceledException)
            {
                // viewer disconnected
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WeatherStar renderer failed, using EBS slate");
            await WriteEbsFallbackAsync(channel, ffmpegPath, output, cancellationToken);
        }
    }

    public static WeatherStarDockerVariant ResolveVariant(Domain.Channel channel)
    {
        var tag = FilterDefinition.ExtractFintvLibraryTag(channel.FilterJson) ?? channel.Name;
        if (tag.Contains("3000", StringComparison.OrdinalIgnoreCase)
            || tag.Contains("ws3", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherStarDockerVariant.Ws3kp;
        }

        return WeatherStarDockerVariant.Ws4kp;
    }

    public string? ResolveWeatherMusicPath()
    {
        var config = FinTvRuntime.Current?.Configuration;
        if (config is null)
        {
            return _ebs.ResolveBackgroundMusicPath();
        }

        var libraryId = config.WeatherMusicLibraryId ?? config.EbsBackgroundMusicLibraryId;
        var libraryName = config.WeatherMusicLibraryName ?? config.EbsBackgroundMusicLibraryName;
        var tracks = string.IsNullOrWhiteSpace(libraryId) && string.IsNullOrWhiteSpace(libraryName)
            ? _catalog.QueryAllMusicAudio()
            : _catalog.QueryMusicAudioFromLibrary(libraryId, libraryName);
        if (tracks.Count == 0)
        {
            return _ebs.ResolveBackgroundMusicPath();
        }

        var track = tracks[Random.Shared.Next(tracks.Count)];
        return _catalog.GetMediaPath(track);
    }

    internal static string BuildWeatherPageUrl(
        string locationQuery,
        string? baseUrl = null,
        string? permalinkQuery = null,
        bool autoWideForSixteenNine = false,
        AspectRatioMode aspectRatio = AspectRatioMode.SixteenNine)
    {
        var root = NormalizeWeatherStarBaseUrl(baseUrl);
        var parameters = ParseQueryParameters(permalinkQuery ?? DefaultWeatherStarPermalinkQuery);

        foreach (var key in LocationQueryKeys)
        {
            parameters.Remove(key);
        }

        parameters["kiosk"] = "true";
        if (autoWideForSixteenNine)
        {
            parameters["wide"] = aspectRatio == AspectRatioMode.FourThree ? "false" : "true";
        }

        var trimmedLocation = locationQuery.Trim();
        parameters["latLonQuery"] = trimmedLocation;
        parameters["txtLocation"] = trimmedLocation;
        if (WeatherLocationParser.TryParseLatLon(trimmedLocation, out var latitude, out var longitude))
        {
            parameters["lat"] = latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters["lon"] = longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters["latLon"] =
                $"{{\"lat\":{latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"lon\":{longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
        }

        return $"{root}?{FormatQueryParameters(parameters)}";
    }

    internal static (string BaseUrl, string Query) SplitPermalink(string permalink)
    {
        if (string.IsNullOrWhiteSpace(permalink))
        {
            return (DefaultWeatherStarBaseUrl, DefaultWeatherStarPermalinkQuery);
        }

        var trimmed = permalink.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return (DefaultWeatherStarBaseUrl, NormalizePermalinkQuery(trimmed));
        }

        var query = NormalizePermalinkQuery(uri.Query);
        var baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return (string.IsNullOrWhiteSpace(baseUrl) ? DefaultWeatherStarBaseUrl : baseUrl, query);
    }

    internal static string NormalizePermalinkQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return DefaultWeatherStarPermalinkQuery;
        }

        var trimmed = query.Trim();
        if (trimmed.StartsWith("?", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        var parameters = ParseQueryParameters(trimmed);
        foreach (var key in LocationQueryKeys)
        {
            parameters.Remove(key);
        }

        foreach (var key in CaptureTimeQueryKeys)
        {
            parameters.Remove(key);
        }

        return parameters.Count == 0
            ? DefaultWeatherStarPermalinkQuery
            : FormatQueryParameters(parameters);
    }

    internal static string NormalizeWeatherStarBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return DefaultWeatherStarBaseUrl;
        }

        var trimmed = baseUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        var queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        return queryIndex < 0 ? trimmed.TrimEnd('/') : trimmed[..queryIndex].TrimEnd('/');
    }

    private static Dictionary<string, string> ParseQueryParameters(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var trimmed = query.Trim();
        if (trimmed.StartsWith("?", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        foreach (var segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex < 0)
            {
                result[Uri.UnescapeDataString(segment)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(segment[..separatorIndex]);
            var value = Uri.UnescapeDataString(segment[(separatorIndex + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static string FormatQueryParameters(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        return string.Join(
            "&",
            parameters.Select(pair =>
                string.IsNullOrEmpty(pair.Value)
                    ? Uri.EscapeDataString(pair.Key)
                    : $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private async Task PumpFramesAsync(
        ChromiumCdpCapture capture,
        string weatherPageUrl,
        ScreenshotFrameStream frameStream,
        CancellationToken cancellationToken)
    {
        _ = weatherPageUrl;
        var frameDelay = TimeSpan.FromSeconds(1.0 / CaptureFps);
        while (!cancellationToken.IsCancellationRequested)
        {
            var jpeg = await capture.CaptureJpegAsync(cancellationToken);
            await frameStream.WriteFrameAsync(jpeg, cancellationToken);
            await Task.Delay(frameDelay, cancellationToken);
        }
    }

    private async Task StreamNoaaFallbackAsync(
        Domain.Channel channel,
        string locationQuery,
        string ffmpegPath,
        string? musicPath,
        Stream output,
        CancellationToken cancellationToken)
    {
        var (width, height) = GetResolution(channel);
        var headline = "Local Weather";
        var detail = locationQuery;
        try
        {
            if (WeatherLocationParser.TryParseLatLon(locationQuery, out var lat, out var lon))
            {
                var client = _http.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("FinTV/0.0.3 (weather)");
                var points = await client.GetStringAsync(
                    $"https://api.weather.gov/points/{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    cancellationToken);
                using var pointsDoc = System.Text.Json.JsonDocument.Parse(points);
                var forecastUrl = pointsDoc.RootElement.GetProperty("properties").GetProperty("forecast").GetString();
                if (!string.IsNullOrWhiteSpace(forecastUrl))
                {
                    var forecast = await client.GetStringAsync(forecastUrl, cancellationToken);
                    using var forecastDoc = System.Text.Json.JsonDocument.Parse(forecast);
                    var period = forecastDoc.RootElement.GetProperty("properties").GetProperty("periods")[0];
                    headline = period.GetProperty("name").GetString() ?? headline;
                    detail = period.GetProperty("detailedForecast").GetString() ?? detail;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NOAA fallback forecast failed");
        }

        var text = EscapeDraw(headline) + ": " + EscapeDraw(detail);
        var hasMusic = !string.IsNullOrWhiteSpace(musicPath) && File.Exists(musicPath);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning"
        };
        args.AddRange(_encoding.HardwareDeviceArgs);
        args.AddRange(["-f", "lavfi", "-i", $"color=c=0x0b1d36:s={width}x{height}:r=30"]);
        if (hasMusic)
        {
            args.AddRange(["-stream_loop", "-1", "-i", musicPath!]);
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
        }

        var vf = _encoding.AdaptVideoFilterForEncoder(
            $"drawtext=text='WeatherStar':fontcolor=white:fontsize=36:x=40:y=40,drawtext=text='{text}':fontcolor=white:fontsize=22:x=40:y=120:text_w=w-80",
            _encoding.Encoder);
        args.AddRange([
            "-vf", vf,
            "-map", "0:v", "-map", "1:a"
        ]);
        _encoding.AppendVideoEncoder(args, stillImage: true);
        args.AddRange([
            "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-ar", "48000",
            "-t", "120",
            "-f", "mpegts", "pipe:1"
        ]);

        await CliWrap.Cli.Wrap(ffmpegPath)
            .WithArguments(args)
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output, autoFlush: true))
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }

    private static string EscapeDraw(string text)
        => text.Replace("\\", "\\\\").Replace("'", "\\'").Replace(":", "\\:").Replace("%", "\\%");

    private static (int Width, int Height) GetResolution(Domain.Channel channel)
    {
        return channel.AspectRatio == AspectRatioMode.FourThree
            ? (640, 480)
            : (854, 480);
    }

    private async Task WriteEbsFallbackAsync(
        Domain.Channel channel,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken)
    {
        var plan = _ebs.CreatePlaybackPlan(channel, durationSeconds: 120);
        var args = _ffmpegBuilder.BuildEbsCommand(channel, plan);
        await CliWrap.Cli.Wrap(ffmpegPath)
            .WithArguments(args)
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output))
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }

    private sealed class ScreenshotFrameStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _frames = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        private byte[]? _currentFrame;
        private int _currentOffset;

        public async Task WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
        {
            await _frames.Writer.WriteAsync(frame, cancellationToken);
        }

        public void Complete()
        {
            _frames.Writer.TryComplete();
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count == 0)
            {
                return 0;
            }

            var totalRead = 0;
            while (totalRead == 0)
            {
                if (_currentFrame is null || _currentOffset >= _currentFrame.Length)
                {
                    if (!await _frames.Reader.WaitToReadAsync(cancellationToken))
                    {
                        return 0;
                    }

                    if (!_frames.Reader.TryRead(out var next))
                    {
                        continue;
                    }

                    _currentFrame = next;
                    _currentOffset = 0;
                }

                var available = _currentFrame.Length - _currentOffset;
                var toCopy = Math.Min(count - totalRead, available);
                Buffer.BlockCopy(_currentFrame, _currentOffset, buffer, offset + totalRead, toCopy);
                _currentOffset += toCopy;
                totalRead += toCopy;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
