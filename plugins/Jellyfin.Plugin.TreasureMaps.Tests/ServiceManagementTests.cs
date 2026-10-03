using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Management;
using MediaBrowser.Controller.Security;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class ServiceManagementTests
{
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:secret@localhost:8080")]
    [InlineData("https://example.test?apikey=secret")]
    [InlineData("javascript:alert(1)")]
    public void ServiceUrlsRejectEmbeddedCredentialsAndNonHttpSchemes(string url)
        => Assert.Throws<ArgumentException>(() => ServiceManagement.ValidateUrl(url));

    [Fact]
    public async Task PlanningNeverWritesAndApplyingTwiceDoesNotDuplicateConnections()
    {
        using var fixture = new Fixture();
        using var services = new ServiceManagement(fixture, fixture, () => fixture.Configuration);
        var plan = await services.ConnectionsAsync(false, TestContext.Current.CancellationToken);
        Assert.Contains(plan, p => p.State == "missing");
        Assert.Equal(0, fixture.Writes);
        Assert.Equal(0, fixture.KeyCreations);
        var first = await services.ConnectionsAsync(true, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(first, p => p.State is "error" or "missing");
        Assert.Equal(6, fixture.Resources.Values.Sum(a => a.Count));
        Assert.Equal(1, fixture.KeyCreations);
        var writes = fixture.Writes;
        var second = await services.ConnectionsAsync(true, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(second, p => p.Changed || p.State is "error" or "missing");
        Assert.Equal(writes, fixture.Writes);
        Assert.Equal(12, fixture.Tests);
        Assert.Equal(1, fixture.KeyCreations);
    }

    [Fact]
    public async Task MaskedArrKeysAreTestedWithoutRewritingWorkingConnections()
    {
        using var fixture = new Fixture();
        using var services = new ServiceManagement(fixture, fixture, () => fixture.Configuration);
        await services.ConnectionsAsync(true, TestContext.Current.CancellationToken);
        fixture.MaskKeys = true;
        var writes = fixture.Writes;
        var result = await services.ConnectionsAsync(true, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(result, p => p.Changed || p.State is "error" or "missing");
        Assert.Equal(writes, fixture.Writes);
        Assert.Equal(12, fixture.Tests);
    }

    [Fact]
    public async Task ExistingCategoriesAndIndexerFiltersSurviveReconciliation()
    {
        using var fixture = new Fixture();
        using var services = new ServiceManagement(fixture, fixture, () => fixture.Configuration);
        await services.ConnectionsAsync(true, TestContext.Current.CancellationToken);
        var client = fixture.Resources["radarr/downloadclient"][0]!;
        SetField(client, "movieCategory", JsonValue.Create("custom-movies")!);
        fixture.Categories.Add("custom-movies");
        var indexer = fixture.Resources["radarr/indexer"][0]!;
        SetField(indexer, "categories", new JsonArray(2100, 2040));
        var writes = fixture.Writes;
        await services.ConnectionsAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(writes, fixture.Writes);
        Assert.Equal("custom-movies", Field(client, "movieCategory")!.ToString());
        Assert.Equal("[2100,2040]", Field(indexer, "categories")!.ToJsonString());
    }

    [Fact]
    public async Task FailedConnectionTestNeverPersistsTheRejectedResource()
    {
        using var fixture = new Fixture { RejectNotifications = true };
        using var services = new ServiceManagement(fixture, fixture, () => fixture.Configuration);
        var result = await services.ConnectionsAsync(true, TestContext.Current.CancellationToken);
        Assert.Contains(result, p => p.State == "error");
        Assert.Equal(0, fixture.Resources.Values.Sum(a => a.Count));
    }

    [Fact]
    public async Task UnavailableServiceDoesNotHideHealthyServiceStatus()
    {
        using var fixture = new Fixture { FailRadarr = true };
        using var services = new ServiceManagement(fixture, fixture, () => fixture.Configuration);
        var states = await Task.WhenAll(services.StatusAsync("radarr", TestContext.Current.CancellationToken), services.StatusAsync("sonarr", TestContext.Current.CancellationToken));
        Assert.Equal(2, states.Length);
        Assert.False(JsonSerializerNode(states[0])["online"]!.GetValue<bool>());
        Assert.True(JsonSerializerNode(states[1])["online"]!.GetValue<bool>());
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(states), StringComparison.Ordinal);
    }

    private static JsonNode JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value)!;

    private static JsonNode? Field(JsonNode resource, string name) => resource["fields"]!.AsArray().First(f => f!["name"]!.ToString() == name)!["value"];
    private static void SetField(JsonNode resource, string name, JsonNode value) => resource["fields"]!.AsArray().First(f => f!["name"]!.ToString() == name)!["value"] = value;

    private sealed class Fixture : HttpMessageHandler, IHttpClientFactory, IAuthenticationManager
    {
        public PluginConfiguration Configuration { get; } = new()
        {
            RadarrUrl = "http://radarr.test", RadarrApiKey = "radarr-secret", RadarrQualityProfileId = 2, RadarrRootFolder = "/movies",
            SonarrUrl = "http://sonarr.test", SonarrApiKey = "sonarr-secret", SonarrQualityProfileId = 2, SonarrRootFolder = "/series",
            SabnzbdUrl = "http://sab.test:8080", SabnzbdApiKey = "sab-secret", BaseUrl = "https://indexer.test", ApiKey = "indexer-secret"
        };
        public Dictionary<string, JsonArray> Resources { get; } = new();
        public HashSet<string> Categories { get; } = new();
        private readonly List<AuthenticationInfo> _keys = new();
        public int Writes { get; private set; }
        public int Tests { get; private set; }
        public int KeyCreations { get; private set; }
        public bool RejectNotifications { get; init; }
        public bool FailRadarr { get; init; }
        public bool MaskKeys { get; set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        public Task<IReadOnlyList<AuthenticationInfo>> GetApiKeys() => Task.FromResult<IReadOnlyList<AuthenticationInfo>>(_keys);
        public Task DeleteApiKey(string accessToken) => throw new NotSupportedException();
        public Task CreateApiKey(string name)
        {
            KeyCreations++;
            _keys.Add(new AuthenticationInfo { AppName = name, AccessToken = "jellyfin-secret" });
            return Task.CompletedTask;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (FailRadarr && uri.Host == "radarr.test") { return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
            if (uri.Host == "sab.test")
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                if (query["mode"] == "set_config") { Categories.Add(query["name"]!); Writes++; return Reply(new JsonObject { ["status"] = true }); }
                return Reply(new JsonObject { ["config"] = new JsonObject { ["categories"] = new JsonArray(Categories.Select(c => (JsonNode)new JsonObject { ["name"] = c }).ToArray()) } });
            }

            var service = uri.Host.Split('.')[0];
            var route = uri.AbsolutePath.Replace("/api/v3/", string.Empty, StringComparison.Ordinal);
            if (route == "qualityprofile") { return Reply(new JsonArray(new JsonObject { ["id"] = 2, ["name"] = "Existing profile" })); }
            if (route == "rootfolder") { return Reply(new JsonArray(new JsonObject { ["path"] = service == "radarr" ? "/movies" : "/series" })); }
            if (route == "system/status") { return Reply(new JsonObject { ["version"] = "1.0" }); }
            var resource = route.Split('/')[0];
            var key = service + "/" + resource;
            if (!Resources.TryGetValue(key, out var resources)) { resources = new JsonArray(); Resources[key] = resources; }
            if (route.EndsWith("/schema", StringComparison.Ordinal))
            {
                var implementation = resource switch { "notification" => "MediaBrowser", "downloadclient" => "Sabnzbd", _ => "Newznab" };
                var fields = resource switch
                {
                    "notification" => new[] { "host", "port", "useSsl", "urlBase", "apiKey", "notify", "updateLibrary" },
                    "downloadclient" => new[] { "host", "port", "useSsl", "urlBase", "apiKey", "movieCategory", "tvCategory" },
                    _ => new[] { "baseUrl", "apiKey", "categories" }
                };
                return Reply(new JsonArray(new JsonObject { ["implementation"] = implementation, ["fields"] = new JsonArray(fields.Select(f => (JsonNode)new JsonObject { ["name"] = f }).ToArray()) }));
            }

            if (request.Method == HttpMethod.Get)
            {
                var copy = resources.DeepClone();
                if (MaskKeys) { foreach (var item in copy.AsArray()) { SetField(item!, "apiKey", JsonValue.Create("********")!); } }
                return Reply(copy);
            }
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            if (route.EndsWith("/test", StringComparison.Ordinal))
            {
                Tests++;
                if (RejectNotifications && resource == "notification") { return new HttpResponseMessage(HttpStatusCode.BadRequest); }
                if (resource == "downloadclient") { Assert.Contains(Field(body, service == "radarr" ? "movieCategory" : "tvCategory")!.ToString(), Categories); }
                return Reply(new JsonArray());
            }

            Writes++;
            body["id"] = resources.Count + 1;
            resources.Add(body);
            return Reply(body);
        }

        private static HttpResponseMessage Reply(JsonNode json) => new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }
}
