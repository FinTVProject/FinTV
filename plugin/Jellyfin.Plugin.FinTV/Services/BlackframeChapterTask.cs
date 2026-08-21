using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using Jellyfin.Plugin.FinTV.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public partial class BlackframeChapterTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapterManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly CatalogSyncTask _catalogSync;
    private readonly ILogger<BlackframeChapterTask> _logger;

    public BlackframeChapterTask(
        ILibraryManager libraryManager,
        IChapterManager chapterManager,
        IMediaEncoder mediaEncoder,
        CatalogSyncTask catalogSync,
        ILogger<BlackframeChapterTask> logger)
    {
        _libraryManager = libraryManager;
        _chapterManager = chapterManager;
        _mediaEncoder = mediaEncoder;
        _catalogSync = catalogSync;
        _logger = logger;
    }

    public string Name => "FinTV Commercial Blackframe Detection";

    public string Key => "FinTVBlackframe";

    public string Description => "Detect commercial segments using FFmpeg blackframe analysis and sync chapters to FinTV Server.";

    public string Category => "FinTV";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var state = config.BlackframeTaskState;
        state.IsRunning = true;
        state.LastError = null;
        Save(state);

        var tag = string.IsNullOrWhiteSpace(config.CommercialLibraryTag) ? "fintv-commercial" : config.CommercialLibraryTag;
        var result = _libraryManager.GetItemsResult(new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            Tags = new[] { tag }
        });

        state.TotalItems = result.Items.Count;
        state.ProcessedItems = 0;
        Save(state);

        var ffmpegPath = _mediaEncoder.EncoderPath;
        var index = 0;
        foreach (var item in result.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = item.Path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var stderr = new StringBuilder();
                await Cli.Wrap(ffmpegPath)
                    .WithArguments(["-hide_banner", "-i", path, "-vf", "blackdetect=d=0.5:pix_th=0.10", "-an", "-f", "null", "-"])
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(cancellationToken);

                var ranges = ParseBlackframes(stderr.ToString());
                var chapters = ranges.Select((range, i) => (
                    range.Start,
                    Name: $"Commercial {i + 1}"
                )).ToList();

                if (config.WriteChaptersToJellyfin && chapters.Count > 0)
                {
                    var infos = chapters.Select(c => new ChapterInfo
                    {
                        StartPositionTicks = c.Start.Ticks,
                        Name = c.Name
                    }).ToList();
                    _chapterManager.SaveChapters(item, infos);
                }

                await _catalogSync.PushChaptersAsync(
                    item.Id,
                    chapters.Select(c => (c.Start, c.Name)).ToList(),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                state.LastError = ex.Message;
                _logger.LogError(ex, "Blackframe scan failed for {Name}", item.Name);
            }

            index++;
            state.ProcessedItems = index;
            Save(state);
            progress.Report(result.Items.Count == 0 ? 100 : index * 100d / result.Items.Count);
        }

        state.IsRunning = false;
        state.LastCompletedAt = DateTime.UtcNow;
        Save(state);
    }

    private static void Save(BlackframeTaskState state)
    {
        if (Plugin.Instance is null)
        {
            return;
        }

        Plugin.Instance.Configuration.BlackframeTaskState = state;
        Plugin.Instance.SaveConfiguration();
    }

    public static List<(TimeSpan Start, TimeSpan End)> ParseBlackframes(string stderr)
    {
        var results = new List<(TimeSpan Start, TimeSpan End)>();
        TimeSpan? start = null;
        foreach (Match match in BlackStartRegex().Matches(stderr))
        {
            if (double.TryParse(match.Groups[1].Value, out var seconds))
            {
                start = TimeSpan.FromSeconds(seconds);
            }
        }

        foreach (Match match in BlackEndRegex().Matches(stderr))
        {
            if (start.HasValue && double.TryParse(match.Groups[1].Value, out var seconds))
            {
                results.Add((start.Value, TimeSpan.FromSeconds(seconds)));
                start = null;
            }
        }

        return results;
    }

    [GeneratedRegex(@"black_start:(\d+(?:\.\d+)?)")]
    private static partial Regex BlackStartRegex();

    [GeneratedRegex(@"black_end:(\d+(?:\.\d+)?)")]
    private static partial Regex BlackEndRegex();
}
