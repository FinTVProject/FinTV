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
    private readonly ILogger<NewsChannelService> _logger;

    public NewsChannelService(
        FinTvDbContext db,
        IFfmpegLocator ffmpegLocator,
        FfmpegEncodingService encoding,
        EbsService ebs,
        JellyfinCatalogService catalog,
        NewsHeadlineService headlines,
        NewsTtsService tts,
        ILogger<NewsChannelService> logger)
    {
        _db = db;
        _ffmpegLocator = ffmpegLocator;
        _encoding = encoding;
        _ebs = ebs;
        _catalog = catalog;
        _headlines = headlines;
        _tts = tts;
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
        var assPath = Path.Combine(newsDir, "news.ass");
        await File.WriteAllTextAsync(
            assPath,
            NewsAssBuilder.Build(header, articles, width, height, duration, settings.ShowHeader),
            cancellationToken);

        var musicPath = ResolveNewsMusicPath(settings);
        var assFilter = NewsAssBuilder.EscapeAssFilterPath(assPath);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning"
        };
        args.AddRange(_encoding.HardwareDeviceArgs);
        args.AddRange(["-f", "lavfi", "-i", $"color=c=0x101010:s={width}x{height}:r=30"]);

        if (!string.IsNullOrWhiteSpace(musicPath) && File.Exists(musicPath))
        {
            args.AddRange(["-stream_loop", "-1", "-i", musicPath]);
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
        }

        var hasSpeech = !string.IsNullOrWhiteSpace(speechPath) && File.Exists(speechPath);
        if (hasSpeech)
        {
            args.AddRange(["-i", speechPath!]);
            var graph = _encoding.AdaptFilterComplexForEncoder(
                $"[0:v]ass='{assFilter}'[vout];[1:a]volume=0.18[a1];[2:a]volume=1.0[a2];[a1][a2]amix=inputs=2:duration=first:dropout_transition=2[aout]",
                _encoding.Encoder);
            args.AddRange(["-filter_complex", graph, "-map", "[vout]", "-map", "[aout]"]);
        }
        else
        {
            args.AddRange([
                "-vf", _encoding.AdaptVideoFilterForEncoder($"ass='{assFilter}'", _encoding.Encoder),
                "-map", "0:v", "-map", "1:a"
            ]);
        }

        _encoding.AppendVideoEncoder(args, stillImage: true);
        args.AddRange([
            "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000",
            "-t", duration.ToString(),
            "-f", "mpegts", "-mpegts_flags", "+resend_headers",
            "-flush_packets", "1", "pipe:1"
        ]);

            var result = await Cli.Wrap(_ffmpegLocator.EncoderPath)
            .WithArguments(args)
            .WithStandardOutputPipe(PipeTarget.ToStream(output, autoFlush: true))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);

        if (result.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("News ffmpeg with ASS overlay exited {Code}; using drawtext fallback", result.ExitCode);
            await StreamDrawtextFallbackAsync(channel, header, articles, musicPath, speechPath, duration, output, cancellationToken);
        }
    }

    private async Task StreamDrawtextFallbackAsync(
        Channel channel,
        string header,
        IReadOnlyList<NewsArticle> articles,
        string? musicPath,
        string? speechPath,
        int duration,
        Stream output,
        CancellationToken cancellationToken)
    {
        var (width, height) = channel.AspectRatio == AspectRatioMode.FourThree ? (640, 480) : (1280, 720);
        var scroll = EscapeDraw(string.Join("   •   ", articles.Select(a => a.Title).DefaultIfEmpty("No headlines loaded")));
        var vf = _encoding.AdaptVideoFilterForEncoder(
            $"drawbox=x=0:y=0:w=iw:h=90:color=0xe11d48@0.92:t=fill," +
            $"drawtext=text='{EscapeDraw(header)}':fontcolor=white:fontsize=36:x=40:y=28," +
            $"drawbox=x=0:y=h-80:w=iw:h=80:color=0x202020@0.92:t=fill," +
            $"drawtext=text='{scroll}':fontcolor=white:fontsize=26:x=w-mod(t*70\\,w+text_w):y=h-52",
            _encoding.Encoder);

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning"
        };
        args.AddRange(_encoding.HardwareDeviceArgs);
        args.AddRange(["-f", "lavfi", "-i", $"color=c=0x101010:s={width}x{height}:r=30"]);
        if (!string.IsNullOrWhiteSpace(musicPath) && File.Exists(musicPath))
        {
            args.AddRange(["-stream_loop", "-1", "-i", musicPath]);
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
        }

        if (!string.IsNullOrWhiteSpace(speechPath) && File.Exists(speechPath))
        {
            args.AddRange(["-i", speechPath]);
            args.AddRange(["-filter_complex", $"[0:v]{vf}[vout];[1:a]volume=0.18[a1];[2:a]volume=1.0[a2];[a1][a2]amix=inputs=2:duration=first[aout]"]);
            args.AddRange(["-map", "[vout]", "-map", "[aout]"]);
        }
        else
        {
            args.AddRange(["-vf", vf, "-map", "0:v", "-map", "1:a"]);
        }

        _encoding.AppendVideoEncoder(args, stillImage: true);
        args.AddRange([
            "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000",
            "-t", duration.ToString(),
            "-f", "mpegts", "pipe:1"
        ]);

        await Cli.Wrap(_ffmpegLocator.EncoderPath)
            .WithArguments(args)
            .WithStandardOutputPipe(PipeTarget.ToStream(output, autoFlush: true))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }

    private string? ResolveNewsMusicPath(NewsSettings settings)
    {
        var tracks = !string.IsNullOrWhiteSpace(settings.MusicLibraryId) || !string.IsNullOrWhiteSpace(settings.MusicLibraryName)
            ? _catalog.QueryMusicAudioFromLibrary(settings.MusicLibraryId, settings.MusicLibraryName)
            : [];
        if (tracks.Count == 0)
        {
            return _ebs.ResolveBackgroundMusicPath();
        }

        return _catalog.GetMediaPath(tracks[Random.Shared.Next(tracks.Count)]);
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

    private static string EscapeDraw(string text)
        => text.Replace("\\", "\\\\").Replace("'", "\\'").Replace(":", "\\:").Replace("%", "\\%");
}
