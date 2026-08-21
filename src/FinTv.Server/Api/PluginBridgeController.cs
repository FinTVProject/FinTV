using System.Text.Json;
using FinTv.Data;
using FinTv.Domain;
using FinTv.News;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Api;

[ApiController]
[Route("api/plugin")]
public class PluginBridgeController : ControllerBase
{
    private readonly FinTvDbContext _db;

    public PluginBridgeController(FinTvDbContext db)
    {
        _db = db;
    }

    [HttpPost("catalog")]
    public async Task<IActionResult> SyncCatalog([FromBody] CatalogSyncRequest request, CancellationToken cancellationToken)
    {
        if (request.Items is null)
        {
            return BadRequest(new { message = "Items are required." });
        }

        var incomingIds = request.Items.Select(i => i.Id).ToHashSet();
        if (request.ReplaceAll)
        {
            var stale = await _db.MediaItems.Where(i => !incomingIds.Contains(i.Id)).ToListAsync(cancellationToken);
            _db.MediaItems.RemoveRange(stale);
        }

        foreach (var item in request.Items)
        {
            var row = await _db.MediaItems.Include(i => i.Chapters).FirstOrDefaultAsync(i => i.Id == item.Id, cancellationToken);
            if (row is null)
            {
                row = new MediaItem { Id = item.Id };
                _db.MediaItems.Add(row);
            }

            row.Name = item.Name ?? string.Empty;
            row.SortName = item.SortName;
            row.Overview = item.Overview;
            row.Kind = item.Kind;
            row.Path = item.Path;
            row.ParentId = item.ParentId;
            row.SeriesId = item.SeriesId;
            row.SeriesName = item.SeriesName;
            row.ProductionYear = item.ProductionYear;
            row.PremiereDate = item.PremiereDate;
            row.OfficialRating = item.OfficialRating;
            row.RuntimeTicks = item.RuntimeTicks;
            row.IndexNumber = item.IndexNumber;
            row.ParentIndexNumber = item.ParentIndexNumber;
            row.LibraryId = item.LibraryId;
            row.LibraryName = item.LibraryName;
            row.CollectionType = item.CollectionType;
            row.PrimaryImagePath = item.PrimaryImagePath;
            row.GenresJson = JsonSerializer.Serialize(item.Genres ?? []);
            row.TagsJson = JsonSerializer.Serialize(item.Tags ?? []);
            row.StudiosJson = JsonSerializer.Serialize(item.Studios ?? []);
            row.CollectionNamesJson = JsonSerializer.Serialize(item.CollectionNames ?? []);
            row.SyncedAt = DateTime.UtcNow;

            _db.MediaChapters.RemoveRange(row.Chapters);
            row.Chapters.Clear();
            if (item.Chapters is { Count: > 0 })
            {
                foreach (var chapter in item.Chapters)
                {
                    row.Chapters.Add(new MediaChapter
                    {
                        MediaItemId = row.Id,
                        StartPositionTicks = chapter.StartPositionTicks,
                        Name = chapter.Name
                    });
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { count = request.Items.Count });
    }

    [HttpPatch("catalog/{itemId:guid}/chapters")]
    public async Task<IActionResult> PatchChapters(
        Guid itemId,
        [FromBody] List<CatalogChapterDto> chapters,
        CancellationToken cancellationToken)
    {
        var row = await _db.MediaItems.Include(i => i.Chapters).FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        _db.MediaChapters.RemoveRange(row.Chapters);
        row.Chapters.Clear();
        foreach (var chapter in chapters ?? [])
        {
            row.Chapters.Add(new MediaChapter
            {
                MediaItemId = row.Id,
                StartPositionTicks = chapter.StartPositionTicks,
                Name = chapter.Name
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { count = row.Chapters.Count });
    }

    [HttpGet("live-tv-urls")]
    public ActionResult<object> LiveTvUrls()
    {
        var baseUrl = FinTvRuntime.Current?.Configuration.PublicBaseUrl?.TrimEnd('/') ?? "http://FinTV-Server:8097";
        var apiKey = Environment.GetEnvironmentVariable("FINTV_API_KEY");
        var query = string.IsNullOrWhiteSpace(apiKey) ? string.Empty : "?apiKey=" + Uri.EscapeDataString(apiKey);
        return Ok(new
        {
            m3u = $"{baseUrl}/iptv/channels.m3u{query}",
            epg = $"{baseUrl}/iptv/epg.xml{query}"
        });
    }
}

[ApiController]
[Route("api/settings")]
[Authorize(Policy = "admin")]
public class PathMappingController : ControllerBase
{
    private readonly PathRemapService _remap;

    public PathMappingController(PathRemapService remap)
    {
        _remap = remap;
    }

    [HttpGet("path-mappings")]
    public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
        => Ok(await _remap.GetAllAsync(cancellationToken));

    [HttpPut("path-mappings")]
    public async Task<IActionResult> Put([FromBody] List<PathMapping> mappings, CancellationToken cancellationToken)
    {
        await _remap.ReplaceAllAsync(mappings ?? [], cancellationToken);
        return Ok(await _remap.GetAllAsync(cancellationToken));
    }

    [HttpPost("path-mappings/test")]
    public async Task<ActionResult<object>> Test([FromQuery] int sample = 50, CancellationToken cancellationToken = default)
        => Ok(await _remap.TestAsync(sample, cancellationToken));
}

[ApiController]
[Route("api/news")]
[Authorize(Policy = "admin")]
public class NewsController : ControllerBase
{
    private readonly FinTvDbContext _db;
    private readonly JellyfinCatalogService _catalog;
    private readonly NewsHeadlineService _headlines;

    public NewsController(FinTvDbContext db, JellyfinCatalogService catalog, NewsHeadlineService headlines)
    {
        _db = db;
        _catalog = catalog;
        _headlines = headlines;
    }

    [HttpGet("feeds")]
    public async Task<ActionResult<object>> GetFeeds(CancellationToken cancellationToken)
        => Ok(await _db.NewsFeeds.AsNoTracking().OrderBy(f => f.SortOrder).ToListAsync(cancellationToken));

    [HttpPut("feeds")]
    public async Task<IActionResult> PutFeeds([FromBody] List<NewsFeed> feeds, CancellationToken cancellationToken)
    {
        var existing = await _db.NewsFeeds.ToListAsync(cancellationToken);
        _db.NewsFeeds.RemoveRange(existing);
        var order = 0;
        foreach (var feed in feeds ?? [])
        {
            if (string.IsNullOrWhiteSpace(feed.Url))
            {
                continue;
            }

            _db.NewsFeeds.Add(new NewsFeed
            {
                Url = feed.Url.Trim(),
                Name = feed.Name,
                Enabled = feed.Enabled,
                SortOrder = order++
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(await _db.NewsFeeds.AsNoTracking().OrderBy(f => f.SortOrder).ToListAsync(cancellationToken));
    }

    [HttpGet("settings")]
    public async Task<ActionResult<object>> GetSettings(CancellationToken cancellationToken)
    {
        var settings = await _db.NewsSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? new NewsSettings();
        return Ok(new
        {
            settings.HeaderText,
            settings.ArticleCount,
            settings.TtsEnabled,
            settings.Voice,
            settings.MusicLibraryId,
            settings.MusicLibraryName,
            settings.ShowHeader,
            settings.ReadHeadlinesOnly,
            settings.IntroText,
            settings.OutroText,
            settings.RefreshMinutes,
            musicLibraries = _catalog.GetMusicLibraries().Select(l => new { id = l.Id, name = l.Name })
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> PutSettings([FromBody] NewsSettings settings, CancellationToken cancellationToken)
    {
        var row = await _db.NewsSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new NewsSettings();
            _db.NewsSettings.Add(row);
        }

        row.HeaderText = settings.HeaderText;
        row.ArticleCount = Math.Clamp(settings.ArticleCount, 1, 30);
        row.TtsEnabled = settings.TtsEnabled;
        row.Voice = string.IsNullOrWhiteSpace(settings.Voice) ? "en-US" : settings.Voice.Trim();
        row.MusicLibraryId = settings.MusicLibraryId;
        row.MusicLibraryName = settings.MusicLibraryName;
        row.ShowHeader = settings.ShowHeader;
        row.ReadHeadlinesOnly = settings.ReadHeadlinesOnly;
        row.IntroText = settings.IntroText;
        row.OutroText = settings.OutroText;
        row.RefreshMinutes = Math.Clamp(settings.RefreshMinutes <= 0 ? 10 : settings.RefreshMinutes, 2, 120);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(row);
    }

    [HttpGet("preview")]
    public async Task<ActionResult<object>> Preview([FromQuery] bool force, CancellationToken cancellationToken)
    {
        var articles = await _headlines.GetAsync(force, cancellationToken);
        return Ok(new
        {
            fetchedAt = _headlines.FetchedAt == DateTime.MinValue ? (DateTime?)null : _headlines.FetchedAt,
            articles
        });
    }
}

public class CatalogSyncRequest
{
    public bool ReplaceAll { get; set; }

    public List<CatalogItemDto> Items { get; set; } = [];
}

public class CatalogItemDto
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public string? SortName { get; set; }

    public string? Overview { get; set; }

    public BaseItemKind Kind { get; set; }

    public string? Path { get; set; }

    public Guid? ParentId { get; set; }

    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? ProductionYear { get; set; }

    public DateTime? PremiereDate { get; set; }

    public string? OfficialRating { get; set; }

    public long? RuntimeTicks { get; set; }

    public int? IndexNumber { get; set; }

    public int? ParentIndexNumber { get; set; }

    public Guid? LibraryId { get; set; }

    public string? LibraryName { get; set; }

    public string? CollectionType { get; set; }

    public string? PrimaryImagePath { get; set; }

    public List<string>? Genres { get; set; }

    public List<string>? Tags { get; set; }

    public List<string>? Studios { get; set; }

    public List<string>? CollectionNames { get; set; }

    public List<CatalogChapterDto>? Chapters { get; set; }
}

public class CatalogChapterDto
{
    public long StartPositionTicks { get; set; }

    public string? Name { get; set; }
}
