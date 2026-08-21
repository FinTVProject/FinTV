using FinTv.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.Data;

public class DatabaseInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IServiceScopeFactory scopeFactory, ILogger<DatabaseInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureNewsColumnsAsync(db, cancellationToken);
        await EnsureChannelColumnsAsync(db, cancellationToken);
        await EnsureMediaItemColumnsAsync(db, cancellationToken);

        if (!await db.AppSettings.AnyAsync(cancellationToken))
        {
            db.AppSettings.Add(new Domain.AppSettingsRow
            {
                Id = 1,
                Json = Domain.FinTvJson.Serialize(new Configuration.PluginConfiguration())
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        if (!await db.CommercialPresets.AnyAsync(cancellationToken))
        {
            db.CommercialPresets.Add(new Domain.CommercialPreset
            {
                Name = "Default",
                BreakMode = Domain.CommercialBreakMode.ChaptersThenTimer,
                TimerIntervalMinutes = 12,
                PostRollCount = 2
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        var runtime = scope.ServiceProvider.GetRequiredService<FinTvRuntime>();
        await runtime.LoadAsync(cancellationToken);
        FinTvRuntime.Current = runtime;

        _logger.LogInformation("FinTV database initialized");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureNewsColumnsAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "ShowHeader" boolean NOT NULL DEFAULT TRUE""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "ReadHeadlinesOnly" boolean NOT NULL DEFAULT FALSE""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "IntroText" text NULL""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "OutroText" text NULL""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "RefreshMinutes" integer NOT NULL DEFAULT 10"""
        };

        foreach (var sql in statements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "News schema ensure skipped for {Sql}", sql);
            }
        }
    }

    private async Task EnsureChannelColumnsAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "Channels" ADD COLUMN IF NOT EXISTS "CommercialSearchPlaylistIdsJson" text NULL""",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Channel schema ensure skipped");
        }
    }

    private async Task EnsureMediaItemColumnsAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "CommunityRating" real NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "CriticRating" real NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "Runtime" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "Album" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "MediaType" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "SeasonId" uuid NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "SeasonName" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "PeopleJson" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "ProviderIdsJson" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "ArtistsJson" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "AlbumArtistsJson" text NULL"""
        };

        foreach (var sql in statements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MediaItem schema ensure skipped for {Sql}", sql);
            }
        }
    }
}
