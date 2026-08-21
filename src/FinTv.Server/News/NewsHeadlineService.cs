using System.Net;
using System.Text;
using System.Xml.Linq;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed record NewsArticle(string Title, string Summary, string? FeedName);

public sealed class NewsHeadlineService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NewsHeadlineService> _logger;
    private readonly object _gate = new();
    private IReadOnlyList<NewsArticle> _articles = [];
    private DateTime _fetchedAt = DateTime.MinValue;

    public NewsHeadlineService(
        IServiceScopeFactory scopes,
        IHttpClientFactory http,
        ILogger<NewsHeadlineService> logger)
    {
        _scopes = scopes;
        _http = http;
        _logger = logger;
    }

    public IReadOnlyList<NewsArticle> Cached => _articles;

    public DateTime FetchedAt => _fetchedAt;

    public async Task<IReadOnlyList<NewsArticle>> GetAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force)
        {
            lock (_gate)
            {
                if (_articles.Count > 0 && DateTime.UtcNow - _fetchedAt < TimeSpan.FromMinutes(2))
                {
                    return _articles;
                }
            }
        }

        return await RefreshAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NewsArticle>> RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var settings = await db.NewsSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? new NewsSettings();
        var feeds = await db.NewsFeeds.AsNoTracking()
            .Where(f => f.Enabled)
            .OrderBy(f => f.SortOrder)
            .ToListAsync(cancellationToken);

        var articles = await FetchAsync(feeds, Math.Max(1, settings.ArticleCount), cancellationToken);
        lock (_gate)
        {
            _articles = articles;
            _fetchedAt = DateTime.UtcNow;
        }

        return articles;
    }

    private async Task<List<NewsArticle>> FetchAsync(
        IReadOnlyList<NewsFeed> feeds,
        int limit,
        CancellationToken cancellationToken)
    {
        var articles = new List<NewsArticle>();
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/0.0.3 (news)");
        }

        foreach (var feed in feeds)
        {
            try
            {
                using var stream = await client.GetStreamAsync(feed.Url, cancellationToken);
                var doc = XDocument.Load(stream);
                var items = doc.Descendants("item").Concat(doc.Descendants().Where(e => e.Name.LocalName == "entry"));
                foreach (var item in items)
                {
                    var title = Clean(item.Element("title")?.Value
                        ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value);
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    var summary = Clean(item.Element("description")?.Value
                        ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "summary")?.Value
                        ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Value);
                    articles.Add(new NewsArticle(title, summary, feed.Name));
                    if (articles.Count >= limit)
                    {
                        return articles;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load RSS feed {Url}", feed.Url);
            }
        }

        return articles;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(StripTags(value)).Trim();
        return decoded.Replace("..", ".").Replace('\u00a0', ' ').Replace('\u2019', '\'');
    }

    private static string StripTags(string html)
    {
        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var ch in html)
        {
            if (ch == '<')
            {
                inTag = true;
                continue;
            }

            if (ch == '>')
            {
                inTag = false;
                continue;
            }

            if (!inTag)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
