using FinTv;
using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Api;

/// <summary>
/// Jellyfin library search helpers for the FinTV admin UI.
/// </summary>
[ApiController]
[Route("api/catalog")]
[Authorize(Policy = "admin")]
public class CatalogController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly JellyfinCatalogService _catalog;
    private readonly FinTvDbContext _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="catalog">FinTV catalog service.</param>
    /// <param name="db">Database context.</param>
    public CatalogController(ILibraryManager libraryManager, JellyfinCatalogService catalog, FinTvDbContext db)
    {
        _libraryManager = libraryManager;
        _catalog = catalog;
        _db = db;
    }

    /// <summary>
    /// Searches Jellyfin library items for lineup slot assignment.
    /// </summary>
    /// <param name="q">Search text.</param>
    /// <param name="contentType">Optional channel content type filter.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching library items.</returns>
    [HttpGet("search")]
    public ActionResult<IEnumerable<object>> Search(
        [FromQuery] string q,
        [FromQuery] ChannelContentType? contentType,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            return Ok(Array.Empty<object>());
        }

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            SearchTerm = q.Trim(),
            Limit = Math.Clamp(limit, 1, 50),
            IncludeItemTypes = contentType.HasValue
                ? GetItemTypes(contentType.Value)
                : new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Audio },
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
        };

        var items = _libraryManager.GetItemsResult(query).Items;
        return Ok(items.Select(MapSearchResult));
    }

    /// <summary>
    /// Resolves display metadata for Jellyfin item identifiers.
    /// </summary>
    /// <param name="request">Lookup request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved item metadata.</returns>
    [HttpPost("lookup")]
    public ActionResult<IEnumerable<object>> Lookup([FromBody] CatalogLookupRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Ids is not { Count: > 0 })
        {
            return Ok(Array.Empty<object>());
        }

        var results = new List<object>();
        foreach (var id in request.Ids.Distinct())
        {
            var item = _libraryManager.GetItemById(id);
            if (item is not null)
            {
                results.Add(MapSearchResult(item));
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Browses library items by tag for AI lineup generation.
    /// </summary>
    /// <param name="tag">Library tag filter.</param>
    /// <param name="contentType">Optional channel content type.</param>
    /// <param name="catalogMode">Optional catalog mode override.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching library items.</returns>
    [HttpGet("browse")]
    public ActionResult<object> Browse(
        [FromQuery] string? tag,
        [FromQuery] ChannelContentType? contentType,
        [FromQuery] ChannelCatalogMode? catalogMode,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var channel = new Channel
        {
            ContentType = contentType ?? ChannelContentType.TvShow,
            FilterJson = string.IsNullOrWhiteSpace(tag)
                ? null
                : FinTvJson.Serialize(new { tags = new[] { tag } }),
            CatalogMode = catalogMode
        };

        var mode = JellyfinCatalogService.ResolveCatalogMode(channel);
        var items = _catalog.BrowseForAiManifest(channel, mode, Math.Clamp(limit, 1, 500));
        return Ok(new
        {
            catalogMode = mode.ToString(),
            total = items.Count,
            items = items.Select(MapSearchResult)
        });
    }

    /// <summary>
    /// Lists Jellyfin libraries from the synced catalog and the current sync selection.
    /// </summary>
    [HttpGet("libraries")]
    public async Task<ActionResult<object>> GetLibraries(CancellationToken cancellationToken)
    {
        var settings = FinTvRuntime.Current?.Configuration.JellyfinLibraries ?? new JellyfinLibrarySettings();
        var libraries = await ListSyncedLibrariesAsync(cancellationToken);
        return Ok(new
        {
            libraries,
            tvLibraryIds = settings.TvLibraryIds,
            movieLibraryIds = settings.MovieLibraryIds,
            musicLibraryIds = settings.MusicLibraryIds,
            musicVideoLibraryIds = settings.MusicVideoLibraryIds
        });
    }

    /// <summary>
    /// Saves which Jellyfin libraries FinTV should use for TV, movies, music, and music videos.
    /// </summary>
    [HttpPut("libraries")]
    public IActionResult UpdateLibraries([FromBody] JellyfinLibrarySettingsRequest? request)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        plugin.Configuration.JellyfinLibraries = new JellyfinLibrarySettings
        {
            TvLibraryIds = JellyfinLibrarySettings.Normalize(request?.TvLibraryIds),
            MovieLibraryIds = JellyfinLibrarySettings.Normalize(request?.MovieLibraryIds),
            MusicLibraryIds = JellyfinLibrarySettings.Normalize(request?.MusicLibraryIds),
            MusicVideoLibraryIds = JellyfinLibrarySettings.Normalize(request?.MusicVideoLibraryIds)
        };
        plugin.SaveConfiguration();
        return Ok(plugin.Configuration.JellyfinLibraries);
    }

    private async Task<List<object>> ListSyncedLibrariesAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.MediaItems.AsNoTracking()
            .Select(item => new LibraryScanRow(
                item.Id,
                item.Name,
                item.Kind,
                item.ParentId,
                item.SeriesId,
                item.LibraryId,
                item.LibraryName,
                item.CollectionType))
            .ToListAsync(cancellationToken);

        var itemsById = rows.ToDictionary(row => row.Id);
        var libraries = new Dictionary<Guid, LibraryListRow>();

        foreach (var folder in rows.Where(row => IsJellyfinLibraryFolder(row)))
        {
            var library = GetOrAddLibrary(libraries, folder.Id, folder.Name, folder.CollectionType);
            library.MemberIds.Add(folder.Id);
        }

        foreach (var row in rows)
        {
            if (row.Kind is BaseItemKind.Folder or BaseItemKind.Playlist)
            {
                continue;
            }

            var resolvedId = ResolveLibraryId(row, itemsById);
            if (resolvedId is null)
            {
                continue;
            }

            itemsById.TryGetValue(resolvedId.Value, out var folder);
            var name = FirstRealName(folder?.Name, row.LibraryName);
            var collectionType = FirstRealName(folder?.CollectionType, row.CollectionType);
            var library = GetOrAddLibrary(libraries, resolvedId.Value, name, collectionType);
            library.ItemCount++;
            library.Kinds.Add(row.Kind);
            if (row.LibraryId is Guid storedId && storedId != Guid.Empty)
            {
                library.MemberIds.Add(storedId);
            }

            library.MemberIds.Add(resolvedId.Value);
        }

        foreach (var library in libraries.Values)
        {
            if (string.IsNullOrWhiteSpace(library.CollectionType))
            {
                library.CollectionType = InferCollectionType(library.Kinds);
            }

            library.MemberIds.Add(library.Id);
        }

        return libraries.Values
            .Select(library =>
            {
                var groups = LibraryGroupsFor(library.CollectionType, library.Kinds);
                return new { library, groups };
            })
            .Where(row => row.groups.Length > 0 && !IsPlaceholderName(row.library.Name))
            .OrderBy(row => row.library.Name, StringComparer.OrdinalIgnoreCase)
            .Select(row => (object)new
            {
                id = row.library.Id,
                ids = row.library.MemberIds.OrderBy(id => id).ToArray(),
                name = row.library.Name,
                collectionType = row.library.CollectionType,
                groups = row.groups,
                itemCount = row.library.ItemCount
            })
            .ToList();
    }

    private static LibraryListRow GetOrAddLibrary(
        Dictionary<Guid, LibraryListRow> byId,
        Guid id,
        string? name,
        string? collectionType)
    {
        if (!byId.TryGetValue(id, out var library))
        {
            library = new LibraryListRow { Id = id };
            byId[id] = library;
        }

        ApplyLibraryName(library, name);
        ApplyCollectionType(library, collectionType);
        return library;
    }

    private static void ApplyLibraryName(LibraryListRow library, string? name)
    {
        if (IsPlaceholderName(library.Name) && !IsPlaceholderName(name))
        {
            library.Name = name!.Trim();
        }
    }

    private static void ApplyCollectionType(LibraryListRow library, string? collectionType)
    {
        if (string.IsNullOrWhiteSpace(library.CollectionType) && !string.IsNullOrWhiteSpace(collectionType))
        {
            library.CollectionType = collectionType.Trim();
        }
    }

    private static bool IsPlaceholderName(string? name)
        => string.IsNullOrWhiteSpace(name)
            || name.Equals("Library", StringComparison.OrdinalIgnoreCase);

    private static string? FirstRealName(params string?[] values)
        => values.FirstOrDefault(value => !IsPlaceholderName(value))?.Trim();

    private static bool IsJellyfinLibraryFolder(LibraryScanRow row)
    {
        if (row.Kind != BaseItemKind.Folder || IsPlaceholderName(row.Name))
        {
            return false;
        }

        return IsKnownLibraryType(row.CollectionType)
            || row.ParentId is null
            || row.ParentId == Guid.Empty;
    }

    private static Guid? ResolveLibraryId(LibraryScanRow row, IReadOnlyDictionary<Guid, LibraryScanRow> itemsById)
    {
        return WalkToLibraryFolder(row.LibraryId, itemsById)
            ?? WalkToLibraryFolder(row.ParentId, itemsById)
            ?? WalkToLibraryFolder(row.SeriesId, itemsById)
            ?? (row.LibraryId is Guid libraryId && libraryId != Guid.Empty ? libraryId : null);
    }

    private static Guid? WalkToLibraryFolder(Guid? start, IReadOnlyDictionary<Guid, LibraryScanRow> itemsById)
    {
        var current = start;
        Guid? lastFolder = null;
        var seen = new HashSet<Guid>();
        while (current is Guid id && id != Guid.Empty && seen.Add(id))
        {
            if (!itemsById.TryGetValue(id, out var node))
            {
                break;
            }

            if (node.Kind == BaseItemKind.Folder)
            {
                lastFolder = node.Id;
                if (IsJellyfinLibraryFolder(node))
                {
                    return node.Id;
                }
            }

            current = node.ParentId is Guid parent && parent != Guid.Empty
                ? parent
                : node.SeriesId;
        }

        return lastFolder;
    }

    private static bool IsKnownLibraryType(string? collectionType)
        => LibraryGroupForType(collectionType) is not null;

    private static string? LibraryGroupForType(string? collectionType)
    {
        var type = (collectionType ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty);
        return type switch
        {
            "tvshows" or "tvshow" or "tv" or "series" or "shows" => "tv",
            "movies" or "movie" => "movies",
            "music" or "audio" => "music",
            "musicvideos" or "musicvideo" => "musicvideos",
            _ => null
        };
    }

    private static string[] LibraryGroupsFor(string? collectionType, HashSet<BaseItemKind> kinds)
    {
        var fromType = LibraryGroupForType(collectionType);
        if (fromType is not null)
        {
            return [fromType];
        }

        var groups = new List<string>();
        if (kinds.Any(kind => kind is BaseItemKind.Series or BaseItemKind.Episode))
        {
            groups.Add("tv");
        }

        if (kinds.Any(kind => kind is BaseItemKind.Movie or BaseItemKind.Video))
        {
            groups.Add("movies");
        }

        if (kinds.Contains(BaseItemKind.Audio))
        {
            groups.Add("music");
        }

        if (kinds.Contains(BaseItemKind.MusicVideo))
        {
            groups.Add("musicvideos");
        }

        return groups.ToArray();
    }

    private static string? InferCollectionType(HashSet<BaseItemKind> kinds)
    {
        var groups = LibraryGroupsFor(null, kinds);
        return groups.Length switch
        {
            1 when groups[0] == "tv" => "tvshows",
            1 when groups[0] == "movies" => "movies",
            1 when groups[0] == "music" => "music",
            1 when groups[0] == "musicvideos" => "musicvideos",
            _ => null
        };
    }

    private static object MapSearchResult(BaseItem item)
    {
        var runtime = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
            : (TimeSpan?)null;

        return new
        {
            id = item.Id,
            name = item.Name,
            type = item.GetBaseItemKind().ToString(),
            runtimeMinutes = runtime.HasValue ? (int)Math.Round(runtime.Value.TotalMinutes) : (int?)null,
            year = item.ProductionYear
        };
    }

    private static BaseItemKind[] GetItemTypes(ChannelContentType contentType)
    {
        return contentType switch
        {
            ChannelContentType.TvShow => new[] { BaseItemKind.Episode },
            ChannelContentType.Movie => new[] { BaseItemKind.Movie },
            ChannelContentType.MusicVideo => new[] { BaseItemKind.MusicVideo },
            ChannelContentType.Music => new[] { BaseItemKind.Audio },
            _ => new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Audio }
        };
    }

    private sealed class LibraryListRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? CollectionType { get; set; }

        public int ItemCount { get; set; }

        public HashSet<BaseItemKind> Kinds { get; } = new();

        public HashSet<Guid> MemberIds { get; } = new();
    }

    private sealed record LibraryScanRow(
        Guid Id,
        string? Name,
        BaseItemKind Kind,
        Guid? ParentId,
        Guid? SeriesId,
        Guid? LibraryId,
        string? LibraryName,
        string? CollectionType);
}

/// <summary>
/// Request body for catalog item lookup.
/// </summary>
public class CatalogLookupRequest
{
    /// <summary>
    /// Gets or sets Jellyfin item identifiers to resolve.
    /// </summary>
    public List<Guid> Ids { get; set; } = new();
}

/// <summary>
/// Selected Jellyfin libraries for each catalog type.
/// </summary>
public class JellyfinLibrarySettingsRequest
{
    public List<Guid>? TvLibraryIds { get; set; }

    public List<Guid>? MovieLibraryIds { get; set; }

    public List<Guid>? MusicLibraryIds { get; set; }

    public List<Guid>? MusicVideoLibraryIds { get; set; }
}
