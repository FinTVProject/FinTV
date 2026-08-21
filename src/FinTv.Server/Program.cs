using System.Net;
using FinTv;
using FinTv.Auth;
using FinTv.Data;
using FinTv.Domain;
using FinTv.News;
using FinTv.Services;
using FinTv.Streaming;
using FinTv.Weather;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("FinTV")
    ?? BuildPostgresConnectionString();

builder.Services.AddDbContext<FinTvDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.AllowTrailingCommas = true;
    });

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "fintv.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin", policy => policy.RequireAuthenticatedUser());
});

builder.Services.AddHttpClient(nameof(LlmClientService))
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All
    });
builder.Services.AddHttpClient(nameof(CommercialBrainzClient))
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FinTV/0.0.3 (CommercialBrainz)");
    });
builder.Services.AddHttpClient("Weather", client =>
{
    client.Timeout = TimeSpan.FromSeconds(25);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("FinTV/1.0 (https://github.com/FinTVProject/FinTV)");
});
builder.Services.AddHttpClient();

builder.Services.AddSingleton<FinTvRuntime>();
builder.Services.AddSingleton<FfmpegLocator>();
builder.Services.AddSingleton<IFfmpegLocator>(sp => sp.GetRequiredService<FfmpegLocator>());
builder.Services.AddSingleton<PublicBaseUrl>();
builder.Services.AddSingleton<IPublicBaseUrl>(sp => sp.GetRequiredService<PublicBaseUrl>());
builder.Services.AddSingleton<FfmpegEncodingService>();
builder.Services.AddSingleton<FfmpegCommandBuilder>();
builder.Services.AddSingleton<YtDlpLocator>();
builder.Services.AddSingleton<StreamService>();
builder.Services.AddSingleton<WeatherGeocoder>();
builder.Services.AddSingleton<WeatherDataClient>();
builder.Services.AddSingleton<WeatherStarAssets>();
builder.Services.AddSingleton<WeatherStarCompositor>();
builder.Services.AddSingleton<AiChannelGenerateJobService>();
builder.Services.AddSingleton<AiLineupAutoApplyTask>();
builder.Services.AddSingleton<CatalogCleanupTask>();
builder.Services.AddSingleton<PlayoutBuilderService>();
builder.Services.AddSingleton<BlackframeChapterTask>();

builder.Services.AddScoped<PathRemapService>();
builder.Services.AddScoped<CatalogLibraryManager>();
builder.Services.AddScoped<CatalogTypedStore>();
builder.Services.AddScoped<CatalogCleanupService>();
builder.Services.AddScoped<ILibraryManager>(sp => sp.GetRequiredService<CatalogLibraryManager>());
builder.Services.AddScoped<IChapterManager>(sp => sp.GetRequiredService<CatalogLibraryManager>());
builder.Services.AddScoped<ChannelService>();
builder.Services.AddScoped<ChannelPresetService>();
builder.Services.AddScoped<LineupService>();
builder.Services.AddScoped<SpecialPresentationService>();
builder.Services.AddScoped<FinTvListService>();
builder.Services.AddScoped<SmartSelectionService>();
builder.Services.AddScoped<LineupGeneratorService>();
builder.Services.AddScoped<CommercialService>();
builder.Services.AddScoped<CommercialBrainzClient>();
builder.Services.AddScoped<CommercialBrainzFilterService>();
builder.Services.AddScoped<CommercialBrainzSyncService>();
builder.Services.AddScoped<YouTubeCommercialStreamService>();
builder.Services.AddScoped<EpgService>();
builder.Services.AddScoped<GuideMetadataService>();
builder.Services.AddScoped<WeatherGuideMetadataService>();
builder.Services.AddScoped<LogoSetService>();
builder.Services.AddScoped<HolidayChannelService>();
builder.Services.AddScoped<JellyfinCatalogService>();
builder.Services.AddScoped<AiCatalogManifestBuilder>();
builder.Services.AddScoped<LlmClientService>();
builder.Services.AddScoped<AiLineupGeneratorService>();
builder.Services.AddScoped<AiChannelAutoApplyService>();
builder.Services.AddScoped<FinTvChannelTaggingService>();
builder.Services.AddScoped<EbsService>();
builder.Services.AddSingleton<NewsHeadlineService>();
builder.Services.AddSingleton<NewsTtsService>();
builder.Services.AddScoped<WeatherStarChannelService>();
builder.Services.AddScoped<NewsChannelService>();

builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlayoutBuilderService>());
builder.Services.AddHostedService<ScheduledTaskHost>();
builder.Services.AddHostedService<NewsRefreshHostedService>();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapFallbackToFile("index.html");

var port = Environment.GetEnvironmentVariable("PORT") ?? "8097";
app.Urls.Add($"http://0.0.0.0:{port}");

app.Run();

static string BuildPostgresConnectionString()
{
    var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "postgres";
    var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
    var db = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "fintv";
    var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "fintv";
    var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "fintv";
    return $"Host={host};Port={port};Database={db};Username={user};Password={password}";
}
