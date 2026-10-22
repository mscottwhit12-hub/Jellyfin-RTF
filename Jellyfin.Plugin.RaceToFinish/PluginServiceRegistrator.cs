using Jellyfin.Plugin.RaceToFinish.Data;
using Jellyfin.Plugin.RaceToFinish.Tracking;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RaceToFinish;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<HistoryDatabase>();
        serviceCollection.AddSingleton<ItemSnapshotBuilder>();
        serviceCollection.AddSingleton<UserAccess>();
        serviceCollection.AddSingleton<WatchTracker>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<WatchTracker>());
        serviceCollection.AddSingleton<HistoryImporter>();
        serviceCollection.AddHostedService<FirstRunImport>();
    }
}
