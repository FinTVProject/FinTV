using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.FinTV.Services;

public sealed class CatalogSyncTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapters;
    private readonly FinTvServerClient _client;

    public CatalogSyncTask(ILibraryManager libraryManager, IChapterManager chapters, FinTvServerClient client)
    {
        _libraryManager = libraryManager;
        _chapters = chapters;
        _client = client;
    }

    public string Name => "FinTV Catalog Sync";

    public string Key => "FinTVCatalogSync";

    public string Description => "Pushes Jellyfin library metadata, paths, and chapters to FinTV Server.";

    public string Category => "FinTV";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var kinds = new[]
        {
            BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode,
            BaseItemKind.MusicVideo, BaseItemKind.Audio, BaseItemKind.Playlist,
            BaseItemKind.CollectionFolder
        };

        var result = _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = kinds
        });

        var items = new List<object>();
        var total = Math.Max(1, result.Items.Count);
        var index = 0;
        foreach (var item in result.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(Map(item));
            index++;
            if (index % 250 == 0)
            {
                progress.Report(index * 90d / total);
            }
        }

        await _client.PostJsonAsync("/api/plugin/catalog", new { replaceAll = true, items }, cancellationToken);
        progress.Report(100);
    }

    public async Task PushChaptersAsync(Guid itemId, IReadOnlyList<(TimeSpan Start, string Name)> chapters, CancellationToken cancellationToken)
    {
        var payload = chapters.Select(c => new { startPositionTicks = c.Start.Ticks, name = c.Name }).ToList();
        await _client.PatchJsonAsync($"/api/plugin/catalog/{itemId:N}/chapters", payload, cancellationToken);
    }

    private object Map(BaseItem item)
    {
        var chapters = _chapters.GetChapters(item.Id)
            .Select(c => new { startPositionTicks = c.StartPositionTicks, name = c.Name })
            .ToList();

        Guid? parentId = item.ParentId == Guid.Empty ? null : item.ParentId;
        Guid? seriesId = null;
        string? seriesName = null;
        int? indexNumber = item.IndexNumber;
        int? parentIndexNumber = item.ParentIndexNumber;
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            seriesId = episode.SeriesId == Guid.Empty ? null : episode.SeriesId;
            seriesName = episode.SeriesName;
        }

        string? collectionType = null;
        if (item is CollectionFolder folder)
        {
            collectionType = folder.CollectionType?.ToString();
        }

        return new
        {
            id = item.Id,
            name = item.Name,
            sortName = item.SortName,
            overview = item.Overview,
            kind = MapKind(item),
            path = item.Path,
            parentId,
            seriesId,
            seriesName,
            productionYear = item.ProductionYear,
            premiereDate = item.PremiereDate,
            officialRating = item.OfficialRating,
            runtimeTicks = item.RunTimeTicks,
            indexNumber,
            parentIndexNumber,
            libraryId = item.GetTopParent()?.Id,
            libraryName = item.GetTopParent()?.Name,
            collectionType,
            primaryImagePath = item.HasImage(ImageType.Primary) ? item.GetImagePath(ImageType.Primary) : null,
            genres = item.Genres ?? Array.Empty<string>(),
            tags = item.Tags ?? Array.Empty<string>(),
            studios = item.Studios ?? Array.Empty<string>(),
            collectionNames = Array.Empty<string>(),
            chapters
        };
    }

    private static int MapKind(BaseItem item)
    {
        if (item is MediaBrowser.Controller.Entities.Movies.Movie) return 0;
        if (item is MediaBrowser.Controller.Entities.TV.Series) return 1;
        if (item is MediaBrowser.Controller.Entities.TV.Episode) return 2;
        if (item.GetBaseItemKind() == BaseItemKind.MusicVideo) return 3;
        if (item is MediaBrowser.Controller.Entities.Audio.Audio) return 4;
        if (item.GetBaseItemKind() == BaseItemKind.Playlist) return 5;
        if (item is CollectionFolder) return 6;
        return 7;
    }
}
