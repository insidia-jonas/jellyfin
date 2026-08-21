using Jellyfin.Plugin.TreasureMaps.Channels;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Registers the plugin's services with the Jellyfin service collection.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TreasureMapsApiClient>();
        serviceCollection.AddSingleton<SabnzbdClient>();
        serviceCollection.AddSingleton<Recommendations.AiRecommender>();
        serviceCollection.AddSingleton<Xrel.XrelClient>();
        serviceCollection.AddSingleton<Subtitles.OpenSubtitlesClient>();
        serviceCollection.AddSingleton<IChannel, TreasureMapsChannel>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Subtitles.ISubtitleProvider, Subtitles.OpenSubtitlesProvider>();
        serviceCollection.AddHostedService<GrabOnFavoriteService>();
    }
}
