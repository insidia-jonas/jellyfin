using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Listing;

namespace Jellyfin.Plugin.TreasureMaps;

public sealed partial class TreasureMapsApiClient
{
    private static string NewznabUrl(IndexerSource source, Dictionary<string, string?> parameters)
    {
        var root = source.Url.TrimEnd('/');
        if (!root.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) { root += "/api"; }
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["apikey"] = source.ApiKey;
        query["o"] = "xml";
        foreach (var (key, value) in parameters) { if (!string.IsNullOrWhiteSpace(value)) { query[key] = value; } }
        return root + "?" + query;
    }

    private async Task<T?> FetchNewznabAsync<T>(IndexerSource source, string path, Dictionary<string, string?>? parameters, TimeSpan ttl, CancellationToken ct)
        where T : class
    {
        var key = SourceIdentity(source) + path + System.Text.Json.JsonSerializer.Serialize(parameters);
        if (ttl <= TimeSpan.Zero) { return await ReadNewznabAsync<T>(source, path, parameters, ct).ConfigureAwait(false); }
        return await _listingCache.GetOrFetchAsync<T>(key, ttl, async (_, token) =>
        {
            var value = await ReadNewznabAsync<T>(source, path, parameters, token).ConfigureAwait(false);
            return value is null || TreasureMapsListingCache.IsEmptyListing(value) ? ListingFetch<T>.DoNotStore(value) : ListingFetch<T>.Store(value);
        }, ct).ConfigureAwait(false);
    }

    private async Task<XDocument> ReadXmlAsync(IndexerSource source, Dictionary<string, string?> parameters, CancellationToken ct)
    {
        var gate = _requests.GetOrAdd(source.Id, _ => new IndexerRequestGate());
        using var slot = await gate.EnterAsync(ct).ConfigureAwait(false);
        using var client = CreateClient(source);
        using var response = await client.GetAsync(NewznabUrl(source, parameters), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) { gate.BackOff(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10)); }
        if ((int)response.StatusCode >= 500) { gate.BackOff(TimeSpan.FromSeconds(3)); }
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
        if (document.Root?.Name.LocalName == "error")
        {
            if (document.Root.Attribute("code")?.Value == "500") { gate.BackOff(TimeSpan.FromSeconds(30)); }
            throw new InvalidOperationException("Newznab hat die Anfrage abgelehnt.");
        }
        return document;
    }

    private async Task<T?> ReadNewznabAsync<T>(IndexerSource source, string path, Dictionary<string, string?>? input, CancellationToken ct)
        where T : class
    {
        // These are curated REST feeds, not standard Newznab functions. Do not invent popularity.
        if (path is "spotlight" or "trending") { return new ReleaseListResponse() as T; }
        if (path == "user")
        {
            var test = await ReadXmlAsync(source, new() { ["t"] = "search", ["limit"] = "1" }, ct).ConfigureAwait(false);
            if (test.Root?.Name.LocalName != "rss") { throw new InvalidOperationException("Ungültige Newznab-Suchantwort."); }
            return new UserInfoResponse { User = new UserInfo { Username = source.Name } } as T;
        }
        var capsKey = SourceIdentity(source) + "newznab-caps";
        var caps = await _listingCache.GetOrFetchAsync<XDocument>(capsKey, TreasureMapsListingCache.CapsFreshTtl, async (_, token) =>
        {
            var xml = await ReadXmlAsync(source, new() { ["t"] = "caps" }, token).ConfigureAwait(false);
            if (xml.Root?.Name.LocalName != "caps") { throw new InvalidOperationException("Ungültige Newznab-Fähigkeiten."); }
            return ListingFetch<XDocument>.Store(xml);
        }, ct).ConfigureAwait(false);
        if (path == "caps")
        {
            return new CapsResponse
            {
                Categories = caps!.Descendants().Where(e => e.Name.LocalName is "category" or "subcat")
                    .Select(e => new CapsNamedItem { Id = (string?)e.Attribute("id"), Name = (string?)e.Attribute("name") }).ToArray()
            } as T;
        }
        input ??= new();
        var kind = path == "tv" ? "tv" : "movie";
        var capability = caps!.Descendants().FirstOrDefault(e => e.Name.LocalName == (kind == "tv" ? "tv-search" : "movie-search"));
        var specialized = (string?)capability?.Attribute("available") == "yes";
        var supported = ((string?)capability?.Attribute("supportedParams") ?? (kind == "tv" ? "q,rid,season,ep" : "q,imdbid"))
            .Split(',').Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var text = input.GetValueOrDefault("q");
        if (text == "*") { text = null; }
        if (specialized && !supported.Contains("q") && !string.IsNullOrEmpty(text)) { specialized = false; }
        if (!string.IsNullOrWhiteSpace(input.GetValueOrDefault("genre"))) { return new ReleaseListResponse() as T; }
        var parameters = new Dictionary<string, string?>
        {
            ["t"] = specialized ? (kind == "tv" ? "tvsearch" : "movie") : "search",
            ["cat"] = kind == "tv" ? source.TvCategories : source.MovieCategories,
            ["limit"] = input.GetValueOrDefault("limit") ?? "100",
            ["offset"] = input.GetValueOrDefault("offset"), ["extended"] = "1"
        };
        foreach (var field in new[] { "imdbid", "year", "season", "ep" })
        {
            var value = input.GetValueOrDefault(field);
            if (value == null) { continue; }
            if (specialized && supported.Contains(field)) { parameters[field] = value; }
            else if (field == "imdbid")
            {
                // An unsupported exact identity query must never turn into an unfiltered latest list.
                return new ReleaseListResponse() as T;
            }
            else if (field == "year") { text = (text + " " + value).Trim(); }
        }
        if (!specialized || !supported.Contains("season"))
        {
            var season = input.GetValueOrDefault("season");
            if (season != null && int.TryParse(season, out var number))
            {
                text = (text + " S" + number.ToString("00", CultureInfo.InvariantCulture)).Trim();
                if (int.TryParse(input.GetValueOrDefault("ep"), out var episode)) { text += "E" + episode.ToString("00", CultureInfo.InvariantCulture); }
            }
        }
        parameters["q"] = text;
        var result = await ReadXmlAsync(source, parameters, ct).ConfigureAwait(false);
        var listing = ParseNewznab(result, kind, int.Parse(parameters["limit"]!, CultureInfo.InvariantCulture));
        SetOrigin(listing, source);
        return listing as T;
    }

    internal static ReleaseListResponse ParseNewznab(XDocument document, string kind, int limit)
    {
        if (document.Root?.Name.LocalName != "rss") { throw new InvalidOperationException("Ungültige Newznab-Suchantwort."); }
        var items = new List<Release>();
        foreach (var item in document.Descendants("item").Take(100))
        {
            string? Attr(string key) => item.Elements().FirstOrDefault(e => e.Name.LocalName == "attr" && (string?)e.Attribute("name") == key)?.Attribute("value")?.Value;
            var guid = Attr("guid") ?? (string?)item.Element("guid") ?? string.Empty;
            if (Uri.TryCreate(guid, UriKind.Absolute, out var uri)) { guid = HttpUtility.ParseQueryString(uri.Query)["id"] ?? uri.Segments.LastOrDefault()?.TrimEnd('/') ?? string.Empty; }
            if (string.IsNullOrWhiteSpace(guid)) { continue; }
            var imdb = Attr("imdb") ?? Attr("imdbid");
            if (!string.IsNullOrEmpty(imdb) && imdb != "0" && !imdb.StartsWith("tt", StringComparison.Ordinal)) { imdb = "tt" + imdb.PadLeft(7, '0'); }
            if (imdb == "0") { imdb = null; }
            var release = new Release
            {
                Guid = guid, Title = (string?)item.Element("title") ?? string.Empty,
                Size = long.TryParse(Attr("size") ?? (string?)item.Element("enclosure")?.Attribute("length"), out var size) ? size : 0,
                PostedAt = DateTimeOffset.TryParse((string?)item.Element("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null,
                Ids = new ReleaseIds { Imdb = imdb, Tmdb = Attr("tmdb") is { Length: > 0 } tmdb && tmdb != "0" ? tmdb : null },
                Category = new ReleaseCategory { Id = kind == "tv" ? 5000 : 2000, Name = kind == "tv" ? "TV" : "Movies" }
            };
            if (kind == "tv") { release.Tv = new ReleaseTv { Title = Attr("tvtitle") ?? ReleaseGrouper.TitleOf(release, kind), Imdb = imdb }; }
            else
            {
                var year = System.Text.RegularExpressions.Regex.Match(release.Title, @"(?:^|[. _(])((?:19|20)\d{2})(?:[. _)]|$)");
                release.Movie = new ReleaseMovie { Title = Attr("movietitle") ?? ReleaseGrouper.TitleOf(release, kind), Year = Attr("year") ?? (year.Success ? year.Groups[1].Value : null) };
            }
            items.Add(release);
        }
        var response = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "response");
        return new ReleaseListResponse { Items = items, Pagination = new Pagination
        {
            Limit = limit, Offset = int.TryParse((string?)response?.Attribute("offset"), out var offset) ? offset : 0,
            Total = int.TryParse((string?)response?.Attribute("total"), out var total) ? total : items.Count
        } };
    }
}
