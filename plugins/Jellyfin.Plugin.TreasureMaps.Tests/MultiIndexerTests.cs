using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Listing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class MultiIndexerTests
{
    private const string Caps = "<caps><searching><search available='yes'/><movie-search available='yes' supportedParams='q,imdbid'/><tv-search available='yes' supportedParams='q,season,ep,imdbid'/></searching></caps>";
    private const string Rss = "<rss xmlns:n='http://www.newznab.com/DTD/2010/feeds/attributes/'><channel><n:response offset='0' total='1'/><item><title>Matrix.1999.German.1080p</title><guid>https://provider.test/details/same-id</guid><enclosure url='https://provider.test/api?apikey=secret' length='1000'/><n:attr name='imdb' value='0133093'/></item></channel></rss>";
    private const string Rest = "{\"items\":[{\"guid\":\"same-id\",\"title\":\"Matrix.1999.German.1080p\",\"ids\":{\"imdb\":\"tt0133093\",\"tmdb\":\"603\"},\"movie\":{\"title\":\"Matrix\",\"year\":1999}}],\"pagination\":{\"limit\":100,\"total\":1}}";
    private static IndexerSource Source(string id, string protocol = "newznab") => new() { Id = id, Name = id, Protocol = protocol, Url = "https://" + id + ".test", ApiKey = id + "-secret" };

    [Fact]
    public async Task ConcurrentProvidersMergeTitlesAndKeepDownloadOriginEvenWhenGuidsCollide()
    {
        var config = new PluginConfiguration { Indexers = new() { Source("legacy", "treasuremaps"), Source("second") } };
        var started = new HashSet<string>(); var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new Handler(async (request, token) =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath.EndsWith("download", StringComparison.Ordinal) || query["t"] == "get") { return Response(request.RequestUri.Host, "application/x-nzb"); }
            lock (started) { started.Add(request.RequestUri.Host); if (started.Count == 2) { both.TrySetResult(); } }
            await both.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            return Response(request.RequestUri.Host == "legacy.test" ? Rest : query["t"] == "caps" ? Caps : Rss);
        });
        using var client = Client(http, config);
        var list = await client.SearchMoviesAsync("Matrix 1999", null, 100, TestContext.Current.CancellationToken);
        Assert.Equal(2, list!.Items.Count);
        Assert.Equal(2, list.Items.Select(r => r.Guid).Distinct().Count());
        Assert.Single(ReleaseGrouper.Group(list.Items));
        Assert.All(list.Items, r => Assert.Null(r.Links));
        foreach (var release in list.Items)
        {
            var nzb = Encoding.UTF8.GetString(await client.DownloadNzbAsync(release.Guid, TestContext.Current.CancellationToken));
            Assert.Equal(release.IndexerId + ".test", nzb);
        }
        var calls = http.Calls.Count;
        await client.SearchMoviesAsync("Matrix 1999", null, 100, TestContext.Current.CancellationToken);
        Assert.Equal(calls, http.Calls.Count);
    }

    [Fact]
    public async Task FederatedPaginationAdvancesEachSourceByOnePageNotTheCombinedItemCount()
    {
        var config = new PluginConfiguration { Indexers = new() { Source("one", "treasuremaps"), Source("two", "treasuremaps") } };
        using var http = new Handler((r, ct) => Task.FromResult(Response(Rest.Replace("\"total\":1", "\"total\":300", StringComparison.Ordinal))));
        using var client = Client(http, config);
        var first = await client.SearchMoviesAsync("Matrix", null, null, 100, 0, TestContext.Current.CancellationToken);
        Assert.Equal(100, first!.NextOffset); Assert.True(first.HasMore);
        await client.SearchMoviesAsync("Matrix", null, null, 100, first.NextOffset!.Value, TestContext.Current.CancellationToken);
        Assert.Equal(2, http.Calls.Count(c => HttpUtility.ParseQueryString(c.Query)["offset"] == "100"));
    }

    [Fact]
    public async Task SlowAndFailedProvidersCannotDiscardSuccessfulResults()
    {
        var config = new PluginConfiguration { Indexers = new() { Source("fast", "treasuremaps"), Source("slow", "treasuremaps"), Source("broken", "treasuremaps") } };
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new Handler(async (r, ct) =>
        {
            if (r.RequestUri!.Host == "slow.test") { try { await Task.Delay(Timeout.Infinite, ct); } finally { cancelled.TrySetResult(); } }
            return r.RequestUri.Host == "broken.test" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Response(Rest);
        });
        using var client = Client(http, config);
        var list = await client.SearchMoviesAsync("Matrix", null, 100, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
        Assert.Equal("fast", Assert.Single(list!.Items).IndexerId);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CredentialsAndDisabledSourcesAreIsolatedAndAllFailuresAreNotEmptySuccess()
    {
        var config = new PluginConfiguration { Indexers = new() { Source("one", "treasuremaps"), Source("two", "treasuremaps") } };
        config.Indexers[1].Enabled = false;
        using var http = new Handler((r, ct) => Task.FromResult(r.Headers.GetValues("X-API-Key").Single() == "one-secret" ? Response(Rest) : new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var client = Client(http, config);
        await client.SearchMoviesAsync("Matrix", null, 100, TestContext.Current.CancellationToken);
        Assert.All(http.Calls, r => Assert.Equal("one.test", r.Host));
        config.Indexers[0].ApiKey = "changed";
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchMoviesAsync("Matrix", null, 100, TestContext.Current.CancellationToken));
        Assert.Equal(2, http.Calls.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadNzbAsync("same-id", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewznabUsesSupportedConstraintsAndIndependentCategoryMapping()
    {
        var config = new PluginConfiguration { Indexers = new() { Source("one") } };
        using var http = new Handler((r, ct) => Task.FromResult(Response(HttpUtility.ParseQueryString(r.RequestUri!.Query)["t"] == "caps" ? Caps : Rss)));
        using var client = Client(http, config);
        await client.SearchTvAsync("The Bear S02E03", TreasureMapsApiClient.GermanTvCategories, 100, 100, TestContext.Current.CancellationToken);
        var query = HttpUtility.ParseQueryString(http.Calls.Last().Query);
        Assert.Equal("tvsearch", query["t"]); Assert.Equal("The Bear", query["q"]);
        Assert.Equal("2", query["season"]); Assert.Equal("3", query["ep"]); Assert.Equal("5000", query["cat"]); Assert.Equal("100", query["offset"]);
        await client.SearchMoviesAsync("tt0133093", null, 100, TestContext.Current.CancellationToken);
        query = HttpUtility.ParseQueryString(http.Calls.Last().Query);
        Assert.Equal("0133093", query["imdbid"]); Assert.Null(query["q"]);
    }

    [Fact]
    public async Task NewznabErrorAndExternalEntitiesAreRejected()
    {
        foreach (var payload in new[] { "<error code='100' description='apikey=secret'/>", "<!DOCTYPE rss [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><rss>&xxe;</rss>", "<html>login</html>" })
        {
            var config = new PluginConfiguration { Indexers = new() { Source("one") } };
            using var http = new Handler((r, ct) => Task.FromResult(Response(payload)));
            using var client = Client(http, config);
            var exception = await Assert.ThrowsAnyAsync<Exception>(() => client.TestSourceAsync(config.Indexers[0], TestContext.Current.CancellationToken));
            Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SameTitleWithConflictingIdentityOrDifferentYearStaysSeparate()
    {
        var one = new Release { Guid = "one", Movie = new() { Title = "It", Year = "1990" }, Ids = new() { Imdb = "tt0099864" } };
        var two = new Release { Guid = "two", Movie = new() { Title = "It", Year = "2017" }, Ids = new() { Imdb = "tt1396484" } };
        Assert.Equal(2, ReleaseGrouper.Group(new[] { one, two }).Count);
        foreach (var raw in new[] { "id/with?special=ä|::", "another-guid" })
        { Assert.Equal(("one", raw), IndexerSource.Unscope(Source("one").Scope(raw))); }
    }

    [Fact]
    public void IdentityBridgeCombinesProvidersRegardlessOfArrivalOrder()
    {
        var imdb = new Release { Guid = "imdb", Ids = new() { Imdb = "tt0133093" }, Movie = new() { Title = "Matrix" } };
        var tmdb = new Release { Guid = "tmdb", Ids = new() { Tmdb = "603" }, Movie = new() { Title = "The Matrix" } };
        var both = new Release { Guid = "both", Ids = new() { Imdb = "tt0133093", Tmdb = "603" }, Movie = new() { Title = "Matrix" } };
        Assert.Equal(3, Assert.Single(ReleaseGrouper.Group(new[] { imdb, tmdb, both })).Releases.Count);
        Assert.Single(ReleaseGrouper.Group(new[] { both, tmdb, imdb }));
    }

    private static TreasureMapsApiClient Client(Handler http, PluginConfiguration config) => new(http, new TreasureMapsListingCache(identity: () => "fixture"), NullLogger<TreasureMapsApiClient>.Instance, () => config);
    private static HttpResponseMessage Response(string body, string media = "application/xml") => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, body.StartsWith('{') ? "application/json" : media) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler, IHttpClientFactory
    {
        public List<Uri> Calls { get; } = new();
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { lock (Calls) { Calls.Add(request.RequestUri!); } return callback(request, ct); }
    }
}
