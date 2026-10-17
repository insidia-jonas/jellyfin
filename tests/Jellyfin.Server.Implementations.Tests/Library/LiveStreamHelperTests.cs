using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class LiveStreamHelperTests
{
    [Fact]
    public async Task FreshLiveOpenIgnoresCachedCodecsAndPreservesActualTrackIndexes()
    {
        var directory = Directory.CreateTempSubdirectory("live-probe-tests-");
        try
        {
            var paths = Mock.Of<IApplicationPaths>(p => p.CachePath == directory.FullName);
            var encoder = new Mock<IMediaEncoder>();
            encoder.SetupSequence(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Info("ac3", true, 25))
                .ReturnsAsync(Info("aac", false, 50));
            var helper = new LiveStreamHelper(encoder.Object, NullLogger.Instance, paths);

            // Seed the old persistent per-channel cache, then open the same channel
            // after its origin changed from interlaced/AC3 to progressive/AAC.
            await helper.AddMediaInfoWithProbe(new MediaSourceInfo(), false, "same-channel", false, CancellationToken.None);
            var live = new MediaSourceInfo { IsInfiniteStream = true };
            await helper.AddMediaInfoWithProbe(live, false, "same-channel", false, CancellationToken.None);
            Assert.Equal("aac", live.MediaStreams[1].Codec);
            Assert.False(live.MediaStreams[0].IsInterlaced);
            Assert.Equal(50, live.MediaStreams[0].AverageFrameRate);
            Assert.Equal(1, live.DefaultAudioStreamIndex);
            Assert.Equal("deu", live.MediaStreams[1].Language);
            encoder.Verify(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static MediaInfo Info(string audio, bool interlaced, float rate) => new()
    {
        Container = "mpegts",
        MediaStreams = [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", IsInterlaced = interlaced, AverageFrameRate = rate },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = audio, Language = "deu" }
        ]
    };
}
