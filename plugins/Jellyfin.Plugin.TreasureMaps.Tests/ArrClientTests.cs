using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Arr;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class ArrClientTests
{
    private sealed class Server : HttpMessageHandler, IHttpClientFactory
    {
        public List<(string Method, string Path, JsonNode? Body)> Calls { get; } = [];
        public Func<string, string, JsonNode?, string> Reply { get; set; } = (_, _, _) => "[]";
        public HttpStatusCode Code { get; set; } = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Contains("secret", request.Headers.GetValues("X-Api-Key"));
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
            var path = request.RequestUri!.PathAndQuery;
            Calls.Add((request.Method.Method, path, body));
            return new(Code) { Content = new StringContent(Reply(request.Method.Method, path, body), Encoding.UTF8, "application/json") };
        }
    }

    private static PluginConfiguration Config() => new()
    {
        RadarrUrl = "http://arr.test", RadarrApiKey = "secret", RadarrQualityProfileId = 4, RadarrRootFolder = "/movies",
        SonarrUrl = "http://arr.test", SonarrApiKey = "secret", SonarrQualityProfileId = 4, SonarrRootFolder = "/series"
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestsExactIdentityWithConfiguredProfileAndManagedSearch(bool series)
    {
        using var server = new Server();
        server.Reply = (method, path, body) => method == "POST" ? "{\"id\":12,\"monitored\":true}"
            : path.Contains("lookup", StringComparison.Ordinal) ? "[{\"imdbId\":\"tt123\",\"tmdbId\":23,\"tvdbId\":45,\"title\":\"Correct\",\"seasons\":[{\"seasonNumber\":0},{\"seasonNumber\":1}]}]" : "[]";
        using var client = new ArrClient(server, Config);
        var status = await client.RequestAsync(new("Correct", series, "tt123", 2024), TestContext.Current.CancellationToken);
        var post = Assert.Single(server.Calls, x => x.Method == "POST");
        Assert.Equal(series ? "/api/v3/series" : "/api/v3/movie", post.Path);
        Assert.Equal(4, post.Body!["qualityProfileId"]!.GetValue<int>());
        Assert.Equal(series ? "/series" : "/movies", post.Body["rootFolderPath"]!.GetValue<string>());
        Assert.True(post.Body["addOptions"]![series ? "searchForMissingEpisodes" : "searchForMovie"]!.GetValue<bool>());
        if (series)
        {
            Assert.False(post.Body["seasons"]![0]!["monitored"]!.GetValue<bool>());
            Assert.True(post.Body["seasons"]![1]!["monitored"]!.GetValue<bool>());
        }

        Assert.True(status.Monitored);
    }

    [Fact]
    public async Task ExistingMonitoredTitleIsNotAddedOrSearchedAgain()
    {
        using var server = new Server { Reply = (_, _, _) => "[{\"id\":2,\"imdbId\":\"tt123\",\"monitored\":true,\"qualityProfileId\":9}]" };
        using var client = new ArrClient(server, Config);
        await Task.WhenAll(client.RequestAsync(new("Movie", false, "tt123", null), TestContext.Current.CancellationToken), client.RequestAsync(new("Movie", false, "tt123", null), TestContext.Current.CancellationToken));
        Assert.All(server.Calls, x => Assert.Equal("GET", x.Method));
    }

    [Fact]
    public async Task DoesNotChooseAMismatchedLookupResult()
    {
        using var server = new Server { Reply = (_, path, _) => path.Contains("lookup", StringComparison.Ordinal) ? "[{\"imdbId\":\"tt999\",\"tmdbId\":99}]" : "[]" };
        using var client = new ArrClient(server, Config);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync(new("Movie", false, "tt123", null), TestContext.Current.CancellationToken));
        Assert.All(server.Calls, x => Assert.Equal("GET", x.Method));
    }

    [Fact]
    public async Task MonitoringExistingTitlePreservesItsProfileAndPath()
    {
        using var server = new Server { Reply = (method, _, _) => method == "GET" ? "[{\"id\":2,\"imdbId\":\"tt123\",\"monitored\":false,\"qualityProfileId\":9,\"path\":\"/custom/movie\"}]" : "{}" };
        using var client = new ArrClient(server, Config);
        await client.RequestAsync(new("Movie", false, "tt123", null), TestContext.Current.CancellationToken);
        var put = Assert.Single(server.Calls, x => x.Method == "PUT");
        Assert.Equal(9, put.Body!["qualityProfileId"]!.GetValue<int>());
        Assert.Equal("/custom/movie", put.Body["path"]!.GetValue<string>());
        Assert.Single(server.Calls, x => x.Path == "/api/v3/command");
    }

    [Fact]
    public async Task StatusIncludesOnlyMatchingQueueProgress()
    {
        using var server = new Server { Reply = (_, path, _) => path.Contains("queue", StringComparison.Ordinal)
            ? "{\"records\":[{\"movieId\":2,\"size\":100,\"sizeleft\":25},{\"movieId\":3,\"size\":1000,\"sizeleft\":900}]}"
            : "[{\"id\":2,\"imdbId\":\"tt123\",\"monitored\":true}]" };
        using var client = new ArrClient(server, Config);
        var status = await client.GetStatusAsync(new("Movie", false, "tt123", null), TestContext.Current.CancellationToken);
        Assert.Equal(75d, status.Percent);
    }

    [Fact]
    public async Task MissingIdentityOrConfigurationDoesNotSendARequest()
    {
        using var server = new Server();
        using var client = new ArrClient(server, () => new());
        Assert.False((await client.GetStatusAsync(new("Movie", false, "tt123", null), TestContext.Current.CancellationToken)).Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync(new("Movie", false, "", null), TestContext.Current.CancellationToken));
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task StatusWithoutIdentityCannotMatchAnUnidentifiedLibraryTitle()
    {
        using var server = new Server { Reply = (_, _, _) => "[{\"id\":2,\"imdbId\":\"\",\"monitored\":true}]" };
        using var client = new ArrClient(server, Config);
        var status = await client.GetStatusAsync(new("Movie", false, "", null), TestContext.Current.CancellationToken);
        Assert.False(status.Enabled);
        Assert.Null(status.Id);
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task ErrorsNeverExposeTheRemoteResponseOrApiKey()
    {
        using var server = new Server { Code = HttpStatusCode.Unauthorized, Reply = (_, _, _) => "secret private details" };
        using var client = new ArrClient(server, Config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync(new("Movie", false, "tt123", null), TestContext.Current.CancellationToken));
        Assert.Equal("Radarr meldet HTTP 401.", error.Message);
    }

    [Fact]
    public async Task CompletedSeriesUsesExistingFolderIdentityAndNeverSearchesOrChangesMonitoring()
    {
        var root = Path.Combine(Path.GetTempPath(), "arr-series");
        var folder = Path.Combine(root, "Monster");
        using var server = new Server { Reply = (_, path, _) => path.EndsWith("/series", StringComparison.Ordinal)
            ? new JsonArray(new JsonObject { ["id"] = 7, ["path"] = folder, ["imdbId"] = "tt123", ["monitored"] = false }).ToJsonString() : "{}" };
        using var client = new ArrClient(server, () => { var c = Config(); c.SonarrRootFolder = root; return c; });
        Assert.True(await client.SyncCompletedAsync(true, Path.Combine(folder, "Season 4"), new("Wrong metadata", true, "tt999", 2022), TestContext.Current.CancellationToken));
        var post = Assert.Single(server.Calls, x => x.Method != "GET");
        Assert.Equal("RescanSeries", post.Body!["name"]!.ToString());
        Assert.Equal(7, post.Body["seriesId"]!.GetValue<int>());
    }

    [Fact]
    public async Task CompletedTitleOutsideLibraryNeverSendsRequests()
    {
        var root = Path.Combine(Path.GetTempPath(), "arr-series");
        using var server = new Server();
        using var client = new ArrClient(server, () => { var c = Config(); c.SonarrRootFolder = root; return c; });
        Assert.False(await client.SyncCompletedAsync(true, root + "-other/show", null, TestContext.Current.CancellationToken));
        Assert.False(await client.SyncCompletedAsync(true, root, null, TestContext.Current.CancellationToken));
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task NewCompletedSeriesWaitsForIdentityAndAddsFutureMonitoringWithoutBackfill()
    {
        var root = Path.Combine(Path.GetTempPath(), "arr-series");
        using var server = new Server { Reply = (method, path, body) => method == "POST" ? "{\"id\":7}"
            : path.Contains("lookup", StringComparison.Ordinal) ? "[{\"tvdbId\":44,\"imdbId\":\"tt123\",\"title\":\"Show\"}]" : "[]" };
        using var client = new ArrClient(server, () => { var c = Config(); c.SonarrRootFolder = root; return c; });
        var storage = Path.Combine(root, "Show", "Season 1");
        Assert.False(await client.SyncCompletedAsync(true, storage, null, TestContext.Current.CancellationToken));
        Assert.True(await client.SyncCompletedAsync(true, storage, new("Show", true, "tt123", 2024), TestContext.Current.CancellationToken));
        var add = Assert.Single(server.Calls, x => x.Method == "POST" && x.Path.EndsWith("/series", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(root, "Show"), add.Body!["path"]!.ToString());
        Assert.Equal("future", add.Body["addOptions"]!["monitor"]!.ToString());
        Assert.False(add.Body["addOptions"]!["searchForMissingEpisodes"]!.GetValue<bool>());
        Assert.DoesNotContain(server.Calls, x => x.Body?["name"]?.ToString()?.Contains("Search", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task UnassignedMovieUsesOnlyOneApprovedFileWithinItsOwnFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "arr-movies");
        var folder = Path.Combine(root, "Movie");
        using var server = new Server { Reply = (_, path, _) => path.Contains("manualimport", StringComparison.Ordinal)
            ? new JsonArray(new JsonObject { ["path"] = Path.Combine(folder, "obfuscated.mkv"), ["movie"] = new JsonObject { ["id"] = 2 }, ["rejections"] = new JsonArray() }).ToJsonString()
            : path.EndsWith("/movie", StringComparison.Ordinal) ? new JsonArray(new JsonObject { ["id"] = 2, ["path"] = folder, ["hasFile"] = false }).ToJsonString() : "{}" };
        using var client = new ArrClient(server, () => { var c = Config(); c.RadarrRootFolder = root; return c; });
        Assert.True(await client.SyncCompletedAsync(false, folder, null, TestContext.Current.CancellationToken));
        var post = Assert.Single(server.Calls, x => x.Method == "POST");
        Assert.Equal("ManualImport", post.Body!["name"]!.ToString());
        Assert.Equal("copy", post.Body["importMode"]!.ToString());
        Assert.Equal(2, post.Body["files"]![0]!["movieId"]!.GetValue<int>());
    }
}
