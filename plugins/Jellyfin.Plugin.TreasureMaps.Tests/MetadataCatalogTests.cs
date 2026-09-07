using System.Collections.Generic;
using Jellyfin.Plugin.TreasureMaps.Metadata;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class MetadataCatalogTests
{
    [Fact]
    public void UpgradeArtwork_ScalesItunesPoster()
    {
        Assert.Equal(
            "https://is1-ssl.mzstatic.com/image/thumb/x/600x600bb.jpg",
            MetadataCatalog.UpgradeArtwork("https://is1-ssl.mzstatic.com/image/thumb/x/100x100bb.jpg"));
    }

    [Fact]
    public void PickBest_PrefersExactTitleAndYear()
    {
        var hits = new[]
        {
            new CatalogHit { Title = "The Matrix Reloaded", Year = 2003 },
            new CatalogHit { Title = "The Matrix", Year = 1999, Plot = "A hacker.", Cover = "https://img/matrix.jpg" },
            new CatalogHit { Title = "The Matrix Resurrections", Year = 2021 }
        };

        var best = MetadataCatalog.PickBest("The Matrix", 1999, hits);
        Assert.NotNull(best);
        Assert.Equal("The Matrix", best!.Title);
        Assert.Equal(1999, best.Year);
    }

    [Fact]
    public void PickBest_RejectsUnrelatedTitles()
    {
        var hits = new[]
        {
            new CatalogHit { Title = "Finding Nemo", Year = 2003 }
        };

        Assert.Null(MetadataCatalog.PickBest("Inception", 2010, hits));
    }
}
