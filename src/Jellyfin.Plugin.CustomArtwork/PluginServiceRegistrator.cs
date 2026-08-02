using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<StalePluginVersionCleanupService>();
        serviceCollection.AddSingleton<ArtworkIndex>();
        serviceCollection.AddSingleton<ArtworkMediaWriter>();
        serviceCollection.AddSingleton<ArtworkLibraryConfigurator>();
        serviceCollection.AddSingleton<CollectionArtworkRetryTracker>();
    }
}
