using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ExplicitTagShelf;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp =>
            new HttpCache(sp.GetRequiredService<IApplicationPaths>(), "explicittagshelf", "explicitfin"));
        serviceCollection.AddSingleton<ExplicitEngine>();
        serviceCollection.AddSingleton<DeezerExplicitClient>();
        serviceCollection.AddSingleton<MusicBrainzExplicitClient>();
    }
}
