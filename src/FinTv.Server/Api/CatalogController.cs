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
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.Kind,
                item.LibraryId,
                item.LibraryName,
                item.CollectionType
            })
            .ToListAsync(cancellationToken);

        var byId = new Dictionary<Guid, LibraryListRow>();
        foreach (var row in rows)
        {
            if (row.Kind == BaseItemKind.Folder)
            {
                var folder = GetOrAddLibrary(byId, row.Id, row.Name, row.CollectionType);
                folder.Name = string.IsNullOrWhiteSpace(row.Name) ? folder.Name : row.Name;
                if (!string.IsNullOrWhiteSpace(row.CollectionType))
                {
                    folder.CollectionType = row.CollectionType;
                }
            }

            if (row.LibraryId is Guid libraryId)
            {
                var library = GetOrAddLibrary(byId, libraryId, row.LibraryName, row.CollectionType);
                if (row.Kind != BaseItemKind.Folder)
                {
                    library.ItemCount++;
                    library.Kinds.Add(row.Kind);
                }
            }
        }

        foreach (var library in byId.Values)
        {
            if (string.IsNullOrWhiteSpace(library.CollectionType))
            {
                library.CollectionType = InferCollectionType(library.Kinds);
            }
        }

        return byId.Values
            .OrderBy(library => library.Name, StringComparer.OrdinalIgnoreCase)
            .Select(library => (object)new
            {
                id = library.Id,
                name = library.Name,
                collectionType = library.CollectionType,
                groups = LibraryGroupsFor(library.CollectionType),
                itemCount = library.ItemCount
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

        if (string.IsNullOrWhiteSpace(library.Name) && !string.IsNullOrWhiteSpace(name))
        {
            library.Name = name.Trim();
        }

        if (string.IsNullOrWhiteSpace(library.CollectionType) && !string.IsNullOrWhiteSpace(collectionType))
        {
            library.CollectionType = collectionType;
        }

        return library;
    }

    private static string[] LibraryGroupsFor(string? collectionType)
    {
        var type = (collectionType ?? string.Empty).Trim().ToLowerInvariant();
        return type switch
        {
            "tvshows" or "tv" or "series" => new[] { "tv" },
            "movies" or "movie" => new[] { "movies" },
            "music" => new[] { "music" },
            "musicvideos" or "musicvideo" => new[] { "musicvideos" },
            _ => new[] { "tv", "movies", "music", "musicvideos" }
        };
    }

    private static string? InferCollectionType(HashSet<BaseItemKind> kinds)
    {
        var content = kinds.Where(kind => kind is not BaseItemKind.Folder and not BaseItemKind.Playlist).ToHashSet();
        if (content.Count == 0)
        {
            return null;
        }

        if (content.All(kind => kind is BaseItemKind.Series or BaseItemKind.Episode))
        {
            return "tvshows";
        }

        if (content.All(kind => kind == BaseItemKind.Movie))
        {
            return "movies";
        }

        if (content.All(kind => kind == BaseItemKind.Audio))
        {
            return "music";
        }

        if (content.All(kind => kind == BaseItemKind.MusicVideo))
        {
            return "musicvideos";
        }

        return null;
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

        public string Name { get; set; } = "Library";

        public string? CollectionType { get; set; }

        public int ItemCount { get; set; }

        public HashSet<BaseItemKind> Kinds { get; } = new();
    }
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
