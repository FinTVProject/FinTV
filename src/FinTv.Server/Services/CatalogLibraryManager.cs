using System.Text.Json;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

/// <summary>
/// Postgres-backed stand-in for Jellyfin ILibraryManager + IChapterManager.
/// </summary>
public sealed class CatalogLibraryManager : ILibraryManager, IChapterManager
{
    private readonly FinTvDbContext _db;
    private readonly PathRemapService _remap;

    public CatalogLibraryManager(FinTvDbContext db, PathRemapService remap)
    {
        _db = db;
        _remap = remap;
    }

    public BaseItem? GetItemById(Guid id)
    {
        var row = _db.MediaItems.AsNoTracking().Include(i => i.Chapters).FirstOrDefault(i => i.Id == id);
        return row is null ? null : Map(row);
    }

    public QueryResult<BaseItem> GetItemsResult(InternalItemsQuery query)
    {
        IQueryable<MediaItem> items = _db.MediaItems.AsNoTracking().Include(i => i.Chapters);

        if (query.IncludeItemTypes is { Length: > 0 })
        {
            var kinds = query.IncludeItemTypes.ToArray();
            items = items.Where(i => kinds.Contains(i.Kind));
        }

        if (query.ParentId != Guid.Empty)
        {
            items = items.Where(i => i.ParentId == query.ParentId || i.SeriesId == query.ParentId);
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name;
            items = items.Where(i =>
                i.Name == name
                || (i.CollectionNamesJson != null && i.CollectionNamesJson.Contains(name)));
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var term = query.SearchTerm;
            items = items.Where(i => i.Name.Contains(term) || (i.Overview != null && i.Overview.Contains(term)));
        }

        var list = items.ToList();

        if (query.Tags is { Length: > 0 })
        {
            list = list.Where(item =>
            {
                var tags = ReadStringArray(item.TagsJson);
                return query.Tags.All(required =>
                    tags.Any(tag => tag.Equals(required, StringComparison.OrdinalIgnoreCase)));
            }).ToList();
        }

        if (query.Genres is { Length: > 0 })
        {
            list = list.Where(item =>
            {
                var genres = ReadStringArray(item.GenresJson);
                return query.Genres.Any(required =>
                    genres.Any(genre => genre.Equals(required, StringComparison.OrdinalIgnoreCase)));
            }).ToList();
        }

        list = list
            .OrderBy(i => i.SortName ?? i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.ParentIndexNumber ?? 0)
            .ThenBy(i => i.IndexNumber ?? 0)
            .ToList();

        if (query.Limit is > 0)
        {
            list = list.Take(query.Limit.Value).ToList();
        }

        return new QueryResult<BaseItem> { Items = list.Select(Map).ToList() };
    }

    public IReadOnlyList<VirtualFolderInfo> GetVirtualFolders()
    {
        return _db.MediaItems.AsNoTracking()
            .Where(i => i.Kind == BaseItemKind.Folder)
            .Select(i => new VirtualFolderInfo
            {
                ItemId = i.Id.ToString(),
                Name = i.Name,
                CollectionType = i.CollectionType
            })
            .ToList();
    }

    public CollectionFolder GetUserRootFolder()
    {
        var folders = _db.MediaItems.AsNoTracking()
            .Where(i => i.Kind == BaseItemKind.Folder)
            .ToList()
            .Select(Map)
            .ToList();

        return new CollectionFolder
        {
            Id = Guid.Empty,
            Name = "Media",
            Children = folders
        };
    }

    public IReadOnlyList<ChapterInfo> GetChapters(Guid itemId)
    {
        return _db.MediaChapters.AsNoTracking()
            .Where(c => c.MediaItemId == itemId)
            .OrderBy(c => c.StartPositionTicks)
            .Select(c => new ChapterInfo { StartPositionTicks = c.StartPositionTicks, Name = c.Name })
            .ToList();
    }

    public BaseItem Map(MediaItem row)
    {
        BaseItem item = row.Kind switch
        {
            BaseItemKind.Episode => new Episode(),
            BaseItemKind.Movie => new Movie(),
            BaseItemKind.Series => new Series(),
            BaseItemKind.MusicVideo => new MusicVideo(),
            BaseItemKind.Audio => new Audio(),
            BaseItemKind.Playlist => new Playlist(),
            BaseItemKind.Folder => new CollectionFolder { CollectionType = row.CollectionType },
            _ => new BaseItem()
        };

        item.Id = row.Id;
        item.Name = row.Name;
        item.SortName = row.SortName;
        item.Overview = row.Overview;
        item.Path = _remap.ResolveExistingPath(row.Path) ?? row.Path;
        item.OfficialRating = row.OfficialRating;
        item.ProductionYear = row.ProductionYear;
        item.PremiereDate = row.PremiereDate;
        item.RunTimeTicks = row.RuntimeTicks;
        item.IndexNumber = row.IndexNumber;
        item.ParentIndexNumber = row.ParentIndexNumber;
        item.ParentId = row.ParentId ?? Guid.Empty;
        item.SeriesId = row.SeriesId ?? Guid.Empty;
        item.SeriesName = row.SeriesName;
        item.LibraryId = row.LibraryId;
        item.LibraryName = row.LibraryName;
        item.CollectionType = row.CollectionType;
        item.PrimaryImagePath = _remap.ResolveExistingPath(row.PrimaryImagePath) ?? row.PrimaryImagePath;
        item.Tags = ReadStringArray(row.TagsJson);
        item.Genres = ReadStringArray(row.GenresJson);
        item.Studios = ReadStringArray(row.StudiosJson);
        item.CollectionNames = ReadStringArray(row.CollectionNamesJson);
        item.Chapters = row.Chapters
            .OrderBy(c => c.StartPositionTicks)
            .Select(c => new ChapterInfo { StartPositionTicks = c.StartPositionTicks, Name = c.Name })
            .ToList();
        item.Kind = row.Kind;

        if (item is Episode episode && row.SeriesId is Guid seriesId && seriesId != Guid.Empty)
        {
            var seriesRow = _db.MediaItems.AsNoTracking().FirstOrDefault(i => i.Id == seriesId);
            if (seriesRow is not null)
            {
                episode.Series = Map(seriesRow) as Series;
            }
        }

        return item;
    }

    private static string[] ReadStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
