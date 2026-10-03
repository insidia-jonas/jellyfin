using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class M3uPlaylistHealthScheduledTaskTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task FailedListingSwitchesWithoutLosingStreamOrigins(bool separateIngest, bool legacyActiveListing)
    {
        const string original = "http://listing.example/old.m3u";
        const string alternate = "https://listing.example/new.m3u";
        const string ingest = "http://primary.example";
        const string backup = "http://backup.example";
        var tuner = new TunerHostInfo
        {
            Id = "tuner",
            Type = "m3u",
            Url = original,
            ActiveUrl = separateIngest && !legacyActiveListing ? ingest : original,
            AlternateUrls = separateIngest ? [ingest, backup, alternate] : [alternate]
        };
        var options = new LiveTvOptions { TunerHosts = [tuner] };
        var config = new Mock<IConfigurationManager>();
        config.Setup(c => c.GetConfiguration("livetv")).Returns(options);
        var manager = new Mock<ITunerHostManager>();
        manager.SetupGet(m => m.TunerHosts).Returns([]);
        using var handler = new ListingHandler();
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        var checker = new M3uPlaylistHealthChecker(factory.Object, NullLogger<M3uPlaylistHealthChecker>.Instance);
        var task = new M3uPlaylistHealthScheduledTask(config.Object, manager.Object, checker, NullLogger<M3uPlaylistHealthScheduledTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        Assert.Equal([original, alternate], handler.Requests);
        Assert.Equal(alternate, tuner.Url);
        Assert.Equal(alternate, M3uUrlFailover.GetPlaylistUrl(tuner));
        Assert.Contains(original, tuner.AlternateUrls);
        Assert.Equal(separateIngest ? ingest : alternate, tuner.ActiveUrl);
        if (separateIngest)
        {
            Assert.Equal([ingest, backup], M3uUrlFailover.GetHealthCandidates(tuner));
        }

        config.Verify(c => c.SaveConfiguration("livetv", options), Times.Once);
    }

    private sealed class ListingHandler : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(request.RequestUri.AbsolutePath == "/old.m3u"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:-1,Channel\nhttp://primary.example/live.ts\n") });
        }
    }
}
