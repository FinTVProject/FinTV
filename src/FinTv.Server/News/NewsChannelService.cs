using System.Globalization;
using CliWrap;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Services;
using FinTv.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed class NewsChannelService
{
    private readonly FinTvDbContext _db;
    private readonly IFfmpegLocator _ffmpegLocator;
    private readonly FfmpegEncodingService _encoding;
    private readonly EbsService _ebs;
    private readonly JellyfinCatalogService _catalog;
    private readonly NewsHeadlineService _headlines;
    private readonly NewsTtsService _tts;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NewsChannelService> _logger;

    public NewsChannelService(
        FinTvDbContext db,
        IFfmpegLocator ffmpegLocator,
        FfmpegEncodingService encoding,
        EbsService ebs,
        JellyfinCatalogService catalog,
        NewsHeadlineService headlines,
        NewsTtsService tts,
        IHttpClientFactory http,
        ILogger<NewsChannelService> logger)
    {
        _db = db;
        _ffmpegLocator = ffmpegLocator;
        _encoding = encoding;
        _ebs = ebs;
        _catalog = catalog;
        _headlines = headlines;
        _tts = tts;
        _http = http;
        _logger = logger;
    }

    public async Task StreamAsync(Channel channel, Stream output, CancellationToken cancellationToken)
    {
        var settings = await _db.NewsSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? new NewsSettings();
        var articles = await _headlines.GetAsync(force: false, cancellationToken);
        var header = string.IsNullOrWhiteSpace(settings.HeaderText) ? channel.Name : settings.HeaderText;
        var newsDir = FinTvRuntime.Current.NewsFolder;
        Directory.CreateDirectory(newsDir);

        string? speechPath = null;
        if (settings.TtsEnabled && articles.Count > 0)
        {
            var script = BuildScript(header, articles, settings);
            speechPath = await _tts.SynthesizeAsync(script, settings.Voice, newsDir, cancellationToken);
        }

        var duration = 90;
        if (!string.IsNullOrWhiteSpace(speechPath) && File.Exists(speechPath))
        {
            var speechSeconds = await _tts.ProbeDurationSecondsAsync(speechPath, cancellationToken);
            if (speechSeconds > 1)
            {
                duration = (int)Math.Clamp(Math.Ceiling(speechSeconds) + 4, 45, 240);
            }
        }

        var (width, height) = channel.AspectRatio == AspectRatioMode.FourThree ? (640, 480) : (1280, 720);
        var imageFiles = await DownloadArticleImagesAsync(articles, newsDir, cancellationToken);
        var beats = BuildSpokenBeats(header, articles, settings, imageFiles, duration);
        var imageWindows = ImageWindows(beats);
        var assPath = Path.Combine(newsDir, "news.ass");
        await File.WriteAllTextAsync(
            assPath,
            NewsAssBuilder.BuildSpoken(width, height, beats),
            cancellationToken);

        var musicPath = ResolveNewsMusicPath(settings);
        var args = BuildAssEncodeArgs(width, height, assPath, musicPath, speechPath, imageWindows);
        AppendMux(args, duration, mpegts: true, filePath: null);

        var result = await RunFfmpegAsync(args, output, cancellationToken);
        if (result != 0 && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("News ffmpeg with ASS overlay exited {Code}; using drawtext fallback", result);
            await StreamDrawtextFallbackAsync(channel, header, articles, settings, musicPath, speechPath, duration, newsDir, beats, output, cancellationToken);
        }
    }

    public async Task<bool> RenderBulletinFileAsync(
        NewsSettings settings,
        IReadOnlyList<NewsArticle> articles,
        string header,
        string workDir,
        string outputMp4,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(Path.GetDirectoryName(outputMp4)!);

        string? speechPath = null;
        if (settings.TtsEnabled && articles.Count > 0)
        {
            var script = BuildScript(header, articles, settings);
            speechPath = await _tts.SynthesizeAsync(script, settings.Voice, workDir, cancellationToken);
        }

        var duration = 90;
        if (!string.IsNullOrWhiteSpace(speechPath) && File.Exists(speechPath))
        {
            var speechSeconds = await _tts.ProbeDurationSecondsAsync(speechPath, cancellationToken);
            if (speechSeconds > 1)
            {
                duration = (int)Math.Clamp(Math.Ceiling(speechSeconds) + 4, 45, 240);
            }
        }

        const int width = 1280;
        const int height = 720;
        var imageFiles = await DownloadArticleImagesAsync(articles, workDir, cancellationToken);
        var beats = BuildSpokenBeats(header, articles, settings, imageFiles, duration);
        var imageWindows = ImageWindows(beats);
        var assPath = Path.Combine(workDir, "news.ass");
        await File.WriteAllTextAsync(
            assPath,
            NewsAssBuilder.BuildSpoken(width, height, beats),
            cancellationToken);

        var musicPath = ResolveNewsMusicPath(settings);
        var args = BuildAssEncodeArgs(width, height, assPath, musicPath, speechPath, imageWindows);
        AppendMux(args, duration, mpegts: false, filePath: outputMp4);
        var exit = await RunFfmpegAsync(args, output: null, cancellationToken);
        if (exit != 0 && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("News bulletin ASS encode exited {Code}; using drawtext fallback", exit);
            var fallback = BuildDrawtextArgs(header, articles, settings, musicPath, speechPath, duration, width, height, workDir, beats);
            AppendMux(fallback, duration, mpegts: false, filePath: outputMp4);
            exit = await RunFfmpegAsync(fallback, output: null, cancellationToken);
        }

        return exit == 0 && File.Exists(outputMp4) && new FileInfo(outputMp4).Length > 1024;
    }

    private List<string> BuildAssEncodeArgs(
        int width,
        int height,
        string assPath,
        string? musicPath,
        string? speechPath,
        IReadOnlyList<NewsImageWindow> imageWindows)
    {
        var assFilter = NewsAssBuilder.EscapeAssFilterPath(assPath);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-y"
        };
        args.AddRange(_encoding.HardwareDeviceArgs);
        args.AddRange(["-f", "lavfi", "-i", $"color=c=0x101010:s={width}x{height}:r=30"]);
        foreach (var image in imageWindows)
        {
            args.AddRange(["-loop", "1", "-framerate", "30", "-i", image.Path]);
        }

        var hasMusic = HasAudioFile(musicPath);
        var hasSpeech = HasAudioFile(speechPath);
        if (hasMusic || !hasSpeech)
        {
            AppendAudioBed(args, musicPath);
        }

        if (hasSpeech)
        {
            args.AddRange(["-i", speechPath!]);
        }

        var video = BuildVideoGraph(width, height, imageWindows, $"ass='{assFilter}'");
        AppendEncodedMaps(args, video, imageWindows.Count, hasMusic, hasSpeech, stillImage: imageWindows.Count == 0);
        return args;
    }

    private static bool HasAudioFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static void AppendAudioBed(List<string> args, string? musicPath)
    {
        if (HasAudioFile(musicPath))
        {
            args.AddRange(["-stream_loop", "-1", "-i", musicPath!]);
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
        }
    }

    private void AppendEncodedMaps(
        List<string> args,
        string videoGraph,
        int imageCount,
        bool hasMusic,
        bool hasSpeech,
        bool stillImage)
    {
        var bedIndex = 1 + imageCount;
        var speechIndex = hasSpeech ? (hasMusic || !hasSpeech ? bedIndex + 1 : 1 + imageCount) : -1;
        if (hasSpeech)
        {
            var audioGraph = hasMusic
                ? $"[{bedIndex}:a]volume=0.18[a1];[{speechIndex}:a]volume=1.0[a2];[a1][a2]amix=inputs=2:duration=first:dropout_transition=2[aout]"
                : $"[{speechIndex}:a]volume=1.0[aout]";
            var graph = _encoding.AdaptFilterComplexForEncoder($"{videoGraph};{audioGraph}", _encoding.Encoder);
            args.AddRange(["-filter_complex", graph, "-map", "[vout]", "-map", "[aout]"]);
        }
        else if (imageCount > 0)
        {
            var graph = _encoding.AdaptFilterComplexForEncoder(videoGraph, _encoding.Encoder);
            args.AddRange(["-filter_complex", graph, "-map", "[vout]", "-map", $"{bedIndex}:a"]);
        }
        else
        {
            var vf = videoGraph;
            if (vf.StartsWith("[0:v]", StringComparison.Ordinal) && vf.EndsWith("[vout]", StringComparison.Ordinal))
            {
                vf = vf["[0:v]".Length..^"[vout]".Length];
            }

            args.AddRange([
                "-vf", _encoding.AdaptVideoFilterForEncoder(vf, _encoding.Encoder),
                "-map", "0:v", "-map", "1:a"
            ]);
        }

        _encoding.AppendVideoEncoder(args, stillImage);
        args.AddRange(["-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000"]);
    }

    private static string BuildVideoGraph(
        int width,
        int height,
        IReadOnlyList<NewsImageWindow> imageWindows,
        string overlayFilter)
    {
        if (imageWindows.Count == 0)
        {
            return $"[0:v]{overlayFilter}[vout]";
        }

        var imgW = Math.Max(240, (int)(width * 0.78));
        var imgH = Math.Max(180, (int)(height * 0.62));
        var imgX = Math.Max(0, (width - imgW) / 2);
        var imgY = Math.Max(24, (int)(height * 0.08));
        var parts = new List<string>
        {
            "[0:v]format=yuv420p[base]"
        };
        for (var i = 0; i < imageWindows.Count; i++)
        {
            parts.Add(
                $"[{i + 1}:v]scale={imgW}:{imgH}:force_original_aspect_ratio=decrease:flags=lanczos," +
                $"pad={imgW}:{imgH}:(ow-iw)/2:(oh-ih)/2:0x101010,setsar=1,format=yuv420p[im{i}]");
        }

        var prev = "[base]";
        for (var i = 0; i < imageWindows.Count; i++)
        {
            var next = i == imageWindows.Count - 1 ? "[vimg]" : $"[vo{i}]";
            var start = imageWindows[i].Start.ToString("0.###", CultureInfo.InvariantCulture);
            var end = imageWindows[i].End.ToString("0.###", CultureInfo.InvariantCulture);
            parts.Add($"{prev}[im{i}]overlay={imgX}:{imgY}:enable='gte(t\\,{start})*lt(t\\,{end})'{next}");
            prev = next;
        }

        parts.Add($"[vimg]{overlayFilter}[vout]");
        return string.Join(";", parts);
    }

    private static void AppendMux(List<string> args, int duration, bool mpegts, string? filePath)
    {
        args.AddRange(["-t", duration.ToString(CultureInfo.InvariantCulture)]);
        if (mpegts)
        {
            args.AddRange(["-f", "mpegts", "-mpegts_flags", "+resend_headers", "-flush_packets", "1", "pipe:1"]);
            return;
        }

        args.AddRange(["-f", "mp4", "-movflags", "+faststart", filePath!]);
    }

    private async Task<int> RunFfmpegAsync(IReadOnlyList<string> args, Stream? output, CancellationToken cancellationToken)
    {
        var stderr = new System.Text.StringBuilder();
        var command = Cli.Wrap(_ffmpegLocator.EncoderPath)
            .WithArguments(args)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
            .WithValidation(CommandResultValidation.None);
        if (output is not null)
        {
            command = command.WithStandardOutputPipe(PipeTarget.ToStream(output, autoFlush: true));
        }

        var result = await command.ExecuteAsync(cancellationToken);
        if (result.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("News ffmpeg exited {Code}: {Error}", result.ExitCode, stderr.ToString().Trim());
        }

        return result.ExitCode;
    }

    private async Task StreamDrawtextFallbackAsync(
        Channel channel,
        string header,
        IReadOnlyList<NewsArticle> articles,
        NewsSettings settings,
        string? musicPath,
        string? speechPath,
        int duration,
        string workDir,
        IReadOnlyList<NewsStoryBeat> beats,
        Stream output,
        CancellationToken cancellationToken)
    {
        var (width, height) = channel.AspectRatio == AspectRatioMode.FourThree ? (640, 480) : (1280, 720);
        var args = BuildDrawtextArgs(header, articles, settings, musicPath, speechPath, duration, width, height, workDir, beats);
        AppendMux(args, duration, mpegts: true, filePath: null);
        await RunFfmpegAsync(args, output, cancellationToken);
    }

    private List<string> BuildDrawtextArgs(
        string header,
        IReadOnlyList<NewsArticle> articles,
        NewsSettings settings,
        string? musicPath,
        string? speechPath,
        int duration,
        int width,
        int height,
        string workDir,
        IReadOnlyList<NewsStoryBeat> beats)
    {
        Directory.CreateDirectory(workDir);
        var tickerPath = Path.Combine(workDir, "ticker.txt");
        var ticker = SpokenTicker(beats, articles);
        File.WriteAllText(tickerPath, ticker);
        var tickerFilter = NewsAssBuilder.EscapeAssFilterPath(tickerPath);
        var vf =
            $"drawbox=x=0:y=0:w=iw:h=90:color=0xe11d48@0.92:t=fill," +
            $"drawtext=text='{EscapeDraw(header)}':fontcolor=white:fontsize=36:x=40:y=28," +
            $"drawbox=x=0:y=h-80:w=iw:h=80:color=0x202020@0.92:t=fill," +
            $"drawtext=textfile='{tickerFilter}':fontcolor=white:fontsize=26:x=w-mod(t*70\\,w+text_w):y=h-52";

        var imageWindows = ImageWindows(beats);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-y"
        };
        args.AddRange(_encoding.HardwareDeviceArgs);
        args.AddRange(["-f", "lavfi", "-i", $"color=c=0x101010:s={width}x{height}:r=30"]);
        foreach (var image in imageWindows)
        {
            args.AddRange(["-loop", "1", "-framerate", "30", "-i", image.Path]);
        }

        var hasMusic = HasAudioFile(musicPath);
        var hasSpeech = HasAudioFile(speechPath);
        if (hasMusic || !hasSpeech)
        {
            AppendAudioBed(args, musicPath);
        }

        if (hasSpeech)
        {
            args.AddRange(["-i", speechPath!]);
        }

        var video = BuildVideoGraph(width, height, imageWindows, vf);
        AppendEncodedMaps(args, video, imageWindows.Count, hasMusic, hasSpeech, stillImage: imageWindows.Count == 0);
        return args;
    }

    private static string SpokenTicker(IReadOnlyList<NewsStoryBeat> beats, IReadOnlyList<NewsArticle> articles)
    {
        var parts = beats
            .Where(beat => beat.ShowOnScreen)
            .Select(beat => string.IsNullOrWhiteSpace(beat.Body) ? beat.Title : beat.Title + ". " + beat.Body)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        if (parts.Count > 0)
        {
            return string.Join("   •   ", parts);
        }

        return string.Join("   •   ", articles.Select(a => a.Title).DefaultIfEmpty("No headlines loaded"));
    }

    private async Task<string?[]> DownloadArticleImagesAsync(
        IReadOnlyList<NewsArticle> articles,
        string workDir,
        CancellationToken cancellationToken)
    {
        var result = new string?[articles.Count];
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(workDir, "images");
        Directory.CreateDirectory(dir);
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(12);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/0.0.3 (news)");
        }

        var saved = 0;
        for (var i = 0; i < articles.Count; i++)
        {
            var url = articles[i].ImageUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (cache.TryGetValue(url, out var cached))
            {
                result[i] = cached;
                continue;
            }

            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var media = response.Content.Headers.ContentType?.MediaType;
                if (media is not null
                    && (!media.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                        || media.Contains("svg", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length < 32 || bytes.Length > 8_000_000)
                {
                    continue;
                }

                var ext = GuessImageExtension(media, url);
                var path = Path.Combine(dir, "story-" + saved.ToString(CultureInfo.InvariantCulture) + ext);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                cache[url] = path;
                result[i] = path;
                saved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "News image download failed for {Url}", url);
            }
        }

        return result;
    }

    private static string GuessImageExtension(string? mediaType, string url)
    {
        if (mediaType is not null)
        {
            if (mediaType.Contains("png", StringComparison.OrdinalIgnoreCase))
            {
                return ".png";
            }

            if (mediaType.Contains("webp", StringComparison.OrdinalIgnoreCase))
            {
                return ".webp";
            }

            if (mediaType.Contains("gif", StringComparison.OrdinalIgnoreCase))
            {
                return ".gif";
            }
        }

        var path = url.Split('?', 2)[0];
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return ".png";
        }

        if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return ".webp";
        }

        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return ".gif";
        }

        return ".jpg";
    }

    private string? ResolveNewsMusicPath(NewsSettings settings)
    {
        if (IsNoMusic(settings))
        {
            return null;
        }

        var tracks = !string.IsNullOrWhiteSpace(settings.MusicLibraryId) || !string.IsNullOrWhiteSpace(settings.MusicLibraryName)
            ? _catalog.QueryMusicAudioFromLibrary(settings.MusicLibraryId, settings.MusicLibraryName)
            : [];
        if (tracks.Count == 0)
        {
            return _ebs.ResolveBackgroundMusicPath();
        }

        return _catalog.GetMediaPath(tracks[Random.Shared.Next(tracks.Count)]);
    }

    internal const string NoMusicLibraryId = "none";

    internal static bool IsNoMusic(NewsSettings settings)
    {
        var id = settings.MusicLibraryId?.Trim();
        if (string.Equals(id, NoMusicLibraryId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(settings.MusicLibraryName?.Trim(), "None", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(id);
    }

    private static string BuildScript(string header, IReadOnlyList<NewsArticle> articles, NewsSettings settings)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.IntroText))
        {
            sb.Append(settings.IntroText.Trim()).Append(". ");
        }
        else
        {
            sb.Append(header).Append(". ");
        }

        foreach (var article in articles)
        {
            sb.Append(article.Title).Append(". ");
            if (!settings.ReadHeadlinesOnly && !string.IsNullOrWhiteSpace(article.Summary))
            {
                sb.Append(article.Summary).Append(". ");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.OutroText))
        {
            sb.Append(settings.OutroText.Trim());
        }

        return sb.ToString();
    }

    private static List<NewsStoryBeat> BuildSpokenBeats(
        string header,
        IReadOnlyList<NewsArticle> articles,
        NewsSettings settings,
        IReadOnlyList<string?> images,
        int duration)
    {
        var parts = new List<(string Title, string Body, string? Image, bool Show)>();
        var intro = !string.IsNullOrWhiteSpace(settings.IntroText) ? settings.IntroText.Trim() : header;
        if (!string.IsNullOrWhiteSpace(intro))
        {
            parts.Add((intro, "", null, settings.ShowHeader));
        }

        for (var i = 0; i < articles.Count; i++)
        {
            var article = articles[i];
            var body = !settings.ReadHeadlinesOnly && !string.IsNullOrWhiteSpace(article.Summary)
                ? article.Summary.Trim()
                : "";
            var image = i < images.Count ? images[i] : null;
            parts.Add((article.Title, body, image, true));
        }

        if (!string.IsNullOrWhiteSpace(settings.OutroText))
        {
            parts.Add((settings.OutroText.Trim(), "", null, true));
        }

        if (parts.Count == 0)
        {
            return [new NewsStoryBeat(0, duration, "ChannelFlow News", "", null, true)];
        }

        var weights = parts.Select(part => Math.Max(24, (part.Title + " " + part.Body).Length)).ToArray();
        var total = (double)weights.Sum();
        var beats = new List<NewsStoryBeat>(parts.Count);
        var t = 0.0;
        for (var i = 0; i < parts.Count; i++)
        {
            var start = t;
            var end = i == parts.Count - 1 ? duration : t + duration * weights[i] / total;
            if (end < start + 1)
            {
                end = Math.Min(duration, start + 1);
            }

            beats.Add(new NewsStoryBeat(start, end, parts[i].Title, parts[i].Body, parts[i].Image, parts[i].Show));
            t = end;
        }

        return beats;
    }

    private static List<NewsImageWindow> ImageWindows(IReadOnlyList<NewsStoryBeat> beats)
        => beats
            .Where(beat => !string.IsNullOrWhiteSpace(beat.ImagePath))
            .Select(beat => new NewsImageWindow(beat.ImagePath!, beat.StartSeconds, beat.EndSeconds))
            .ToList();

    private static string EscapeDraw(string text)
        => text.Replace("\\", "\\\\")
            .Replace("'", "\u2019")
            .Replace(":", "\\:")
            .Replace("%", "\\%");
}
