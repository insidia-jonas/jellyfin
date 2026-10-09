using Jellyfin.Data;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Listing;
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
        serviceCollection.AddSingleton<TreasureMapsListingCache>();
        serviceCollection.AddSingleton<TreasureMapsApiClient>();
        serviceCollection.AddHttpClient("Evolution.Indexers")
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false });
        serviceCollection.AddSingleton<Management.ServiceManagement>();
        serviceCollection.AddSingleton<SabnzbdClient>();
        serviceCollection.AddSingleton<Recommendations.AiRecommender>();
        serviceCollection.AddSingleton<Metadata.MetadataCatalog>();
        serviceCollection.AddSingleton<GrabService>();
        serviceCollection.AddHttpClient("TreasureMaps.Arr");
        serviceCollection.AddSingleton<Arr.ArrClient>();
        serviceCollection.AddSingleton<Xrel.XrelClient>();
        serviceCollection.AddSingleton<Subtitles.OpenSubtitlesClient>();
        serviceCollection.AddSingleton<Subtitles.OpenSubtitlesProvider>();
        serviceCollection.AddSingleton<Subtitles.AiSubtitleService>();
        serviceCollection.AddSingleton<Subtitles.SubtitleSyncService>();
        serviceCollection.AddSingleton(sp => new Subtitles.SubtitleJobQueue(
            System.IO.Path.Combine(sp.GetRequiredService<MediaBrowser.Common.Configuration.IApplicationPaths>().DataPath, "evolution", "subtitle-jobs.json"),
            async (job, progress, ct) =>
            {
                var item = sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>().GetItemById(job.ItemId);
                var user = sp.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>().GetUserById(job.Owner);
                if (item is null || user is null || !item.IsVisible(user)
                    || (!user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator)
                        && !user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.EnableContentDownloading))
                    || Subtitles.SubtitleFiles.ResolveMediaPath(item) != job.Quote.Path)
                    throw new System.InvalidOperationException("Subtitle media access changed.");
                if (job.Kind == "sync")
                {
                    await sp.GetRequiredService<Subtitles.SubtitleSyncService>().RunAsync(item, job, progress, ct).ConfigureAwait(false);
                    await item.RefreshMetadata(ct).ConfigureAwait(false);
                }
                else await sp.GetRequiredService<Subtitles.AiSubtitleService>().GenerateAsync(job.Quote, ct, job.Force, progress).ConfigureAwait(false);
                item.ChangedExternally();
            }, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Subtitles.SubtitleJobQueue>>()));
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Subtitles.SubtitleJobQueue>());
        serviceCollection.AddSingleton<Subtitles.AiSubtitleProvider>();
        serviceCollection.AddSingleton<IChannel, TreasureMapsChannel>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Subtitles.ISubtitleProvider>(sp => sp.GetRequiredService<Subtitles.OpenSubtitlesProvider>());
        serviceCollection.AddSingleton<MediaBrowser.Controller.Subtitles.ISubtitleProvider>(sp => sp.GetRequiredService<Subtitles.AiSubtitleProvider>());
        serviceCollection.AddHostedService<GrabOnFavoriteService>();
        serviceCollection.AddHostedService<TreasureMapsListingWarmupHost>();
        serviceCollection.AddHostedService<WebScriptInjector>();
        serviceCollection.AddHostedService<PeopleImageService>();
        serviceCollection.AddSingleton<CompletedDownloadImporter>();
        serviceCollection.AddSingleton<LibraryRefreshService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<LibraryRefreshService>());
    }
}
