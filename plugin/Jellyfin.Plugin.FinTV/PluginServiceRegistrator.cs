using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FinTV;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;
        serviceCollection.AddHttpClient("fintv");
        serviceCollection.AddSingleton<FinTvServerClient>();
        serviceCollection.AddSingleton<CatalogSyncTask>();
        serviceCollection.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<CatalogSyncTask>());
        serviceCollection.AddSingleton<BlackframeChapterTask>();
        serviceCollection.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<BlackframeChapterTask>());
        serviceCollection.AddHostedService<LiveTvRegistrationHostedService>();
    }
}
