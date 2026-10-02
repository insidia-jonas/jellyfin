using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class SharedHttpStreamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenOffersDirectPlayOnlyForPublishedNonLoopingProxy(bool looping)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var configuration = new Mock<IConfigurationManager>();
            configuration.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions { TranscodingTempPath = directory.FullName });
            configuration.SetupGet(c => c.CommonApplicationPaths).Returns(Mock.Of<IApplicationPaths>());
            var host = new Mock<IServerApplicationHost>();
            host.Setup(h => h.GetApiUrlForLocalAccess(null, true)).Returns("http://127.0.0.1:8096");
            var http = new Mock<IHttpClientFactory>(MockBehavior.Strict);
            var source = new MediaSourceInfo { Path = "http://provider.example/live.ts", Protocol = MediaProtocol.Http, SupportsDirectPlay = false, RequiresLooping = looping };
            using var stream = new SharedHttpStream(source, new TunerHostInfo(), "channel", Mock.Of<IFileSystem>(), http.Object, NullLogger.Instance, configuration.Object, host.Object, Mock.Of<IStreamHelper>());

            await stream.Open(TestContext.Current.CancellationToken);

            Assert.StartsWith("http://127.0.0.1:8096/LiveTv/LiveStreamFiles/", source.Path, StringComparison.Ordinal);
            Assert.Equal(!looping, source.SupportsDirectPlay);
            http.VerifyNoOtherCalls();
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
