using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.TreasureMaps.Metadata;

/// <summary>Reuse Jellyfin's configured metadata provider, language handling and HTTP cache.</summary>
public static class LocalizedCatalog
{
    public static async Task<CatalogHit?> LookupAsync(IProviderManager? providers, ReleaseGroup group, CancellationToken ct, string providerName = "TheMovieDb")
    {
        if (providers is null) return null;
        // Only exact external identities are eligible: ambiguous title searches must not
        // attach another film's description to a release with the same name.
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(group.Imdb)) ids["Imdb"] = ReleaseMapper.NormalizeImdbId(group.Imdb);
        if (!string.IsNullOrWhiteSpace(group.Tmdb)) ids["Tmdb"] = group.Tmdb;
        if (ids.Count == 0) return null;
        if (group.Kind == "tv")
        {
            var provider = providers.GetMetadataProviders<Series>(new Series(), new LibraryOptions())
                .OfType<IRemoteMetadataProvider<Series, SeriesInfo>>().FirstOrDefault(p => p.Name == providerName);
            if (provider is null) return null;
            var data = await provider.GetMetadata(new SeriesInfo { Name = group.Title, Year = group.Year,
                ProviderIds = ids, MetadataLanguage = "de-DE", MetadataCountryCode = "DE" }, ct).ConfigureAwait(false);
            return Convert(data, providerName);
        }
        else
        {
            var provider = providers.GetMetadataProviders<Movie>(new Movie(), new LibraryOptions())
                .OfType<IRemoteMetadataProvider<Movie, MovieInfo>>().FirstOrDefault(p => p.Name == providerName);
            if (provider is null) return null;
            var data = await provider.GetMetadata(new MovieInfo { Name = group.Title, Year = group.Year,
                ProviderIds = ids, MetadataLanguage = "de-DE", MetadataCountryCode = "DE" }, ct).ConfigureAwait(false);
            return Convert(data, providerName);
        }
    }

    private static CatalogHit? Convert<T>(MetadataResult<T> data, string providerName) where T : BaseItem
    {
        if (!data.HasMetadata || data.Item is null) return null;
        var item = data.Item;
        return new CatalogHit { Title = item.Name, Plot = item.Overview, Year = item.ProductionYear,
            Rating = item.CommunityRating, RatingSource = providerName == "The Open Movie Database" ? "IMDb" : "TMDb", Language = data.ResultLanguage,
            Cover = data.RemoteImages.FirstOrDefault(i => i.Type == ImageType.Primary).Url,
            Genres = item.Genres.ToList(),
            Actors = data.People?.Where(p => p.Type == Jellyfin.Data.Enums.PersonKind.Actor).Take(8).Select(p => p.Name).ToList() ?? new() };
    }
}
