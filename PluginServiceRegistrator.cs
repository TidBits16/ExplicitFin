using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ExplicitTagger;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ExplicitEngine>();
        serviceCollection.AddSingleton<DeezerExplicitClient>();
        serviceCollection.AddSingleton<MusicBrainzExplicitClient>();
        serviceCollection.AddSingleton<AppleMusicExplicitClient>();
        serviceCollection.AddSingleton<HttpCache>();
    }
}
