using System.Collections.Generic;
using Jellyfin.Plugin.TreasureMaps;
using Jellyfin.Plugin.TreasureMaps.Listing;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class DownloadOverlayTests
{
    [Fact]
    public void TryParseDownloadId_ReadsNzo()
    {
        Assert.True(DownloadOverlay.TryParseDownloadId("DL::SABnzbd_nzo_1::abc", out var nzo, out _));
        Assert.Equal("SABnzbd_nzo_1", nzo);
        Assert.False(DownloadOverlay.TryParseDownloadId("GRP::x", out _, out _));
    }

    [Fact]
    public void Apply_UpdatesProgressWithoutChangingTitle()
    {
        var item = new Folder
        {
            Name = "Dune",
            ExternalId = "DL::nzo1::x",
            Overview = "stale"
        };
        var status = new List<SabnzbdClient.SabDownloadStatus>
        {
            new()
            {
                Id = "nzo1",
                Name = "Dune.2024.1080p",
                Status = "Downloading",
                Percent = 42,
                TimeLeft = "0:10:00"
            }
        };

        DownloadOverlay.Apply(
            [item],
            status,
            "2.0M",
            (_, _) => new GrabRecord { Title = "Dune", Quality = "1080p" });

        Assert.Equal("Dune", item.Name);
        Assert.Contains("42%", item.Overview, System.StringComparison.Ordinal);
        Assert.Contains("1080p", item.Overview, System.StringComparison.Ordinal);
        Assert.StartsWith("0-", item.ForcedSortName, System.StringComparison.Ordinal);
    }

    [Fact]
    public void FormatOverview_CompletedAndFailed()
    {
        Assert.Contains(
            "Download complete",
            DownloadOverlay.FormatOverview(new SabnzbdClient.SabDownloadStatus { Status = "Completed" }, null, null),
            System.StringComparison.Ordinal);
        Assert.Contains(
            "boom",
            DownloadOverlay.FormatOverview(new SabnzbdClient.SabDownloadStatus { Status = "Failed", FailMessage = "boom" }, null, "4K"),
            System.StringComparison.Ordinal);
    }
}
