using System.Collections.Generic;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class LanguageFlagsTests
{
    [Fact]
    public void LanguageFlags_FromSceneName_German()
    {
        var release = new Release { Guid = "g1", Title = "Housemaid.2026.German.DL.1080p.BluRay.AVC-ELEMENTAL" };

        Assert.Equal("\U0001F1E9\U0001F1EA", ReleaseMapper.LanguageFlags(release));
    }

    [Fact]
    public void LanguageFlags_FromAudioLanguages_CodesAndNames_Deduplicated()
    {
        var release = new Release
        {
            Guid = "g2",
            Title = "Some.Movie.2026.GERMAN.1080p.WEB.h264-GRP",
            AudioLanguages = new List<string> { "de", "en" }
        };

        Assert.Equal("\U0001F1E9\U0001F1EA\U0001F1EC\U0001F1E7", ReleaseMapper.LanguageFlags(release));
    }

    [Fact]
    public void LanguageFlags_Unknown_ReturnsEmpty()
    {
        var release = new Release { Guid = "g3", Title = "Mystery.Item.1080p.WEB-GRP" };

        Assert.Equal(string.Empty, ReleaseMapper.LanguageFlags(release));
    }
}
