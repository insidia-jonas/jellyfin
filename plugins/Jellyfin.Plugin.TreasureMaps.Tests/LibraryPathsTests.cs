using System.IO;
using Jellyfin.Plugin.TreasureMaps;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class LibraryPathsTests
{
    [Fact]
    public void Resolve_AbsolutePath_IsKept()
    {
        Assert.Equal("/srv/media/movies", LibraryPaths.Resolve("/srv/media/movies", "movies", "/downloads/complete"));
    }

    [Fact]
    public void Resolve_Relative_JoinsCompleteDir()
    {
        Assert.Equal(Path.Combine("/downloads/complete", "movies"), LibraryPaths.Resolve("movies", "movies", "/downloads/complete"));
    }

    [Fact]
    public void Resolve_EmptyWithoutCompleteDir_ReturnsNull()
    {
        Assert.Null(LibraryPaths.Resolve("movies", "movies", null));
    }

    [Fact]
    public void IsLibraryVideo_RejectsSampleAndNzb()
    {
        Assert.True(LibraryPaths.IsLibraryVideo("/data/The.Green.Mile.1999.1080p.mkv"));
        Assert.False(LibraryPaths.IsLibraryVideo("/data/The.Green.Mile.1999.SAMPLE.mkv"));
        Assert.False(LibraryPaths.IsLibraryVideo("/data/The.Green.Mile.1999.nzb"));
    }

    [Fact]
    public void CountVideos_CountsOnlyRealVideos()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-libpaths-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Movie.2024.1080p.mkv"), "x");
            File.WriteAllText(Path.Combine(dir, "Movie.2024.sample.mkv"), "x");
            File.WriteAllText(Path.Combine(dir, "Movie.2024.nzb"), "x");
            Assert.Equal(1, LibraryPaths.CountVideos(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LibraryRootFromCompletedPath_UsesCategoryParent()
    {
        Assert.Equal(
            "/downloads/complete/movies",
            LibraryPaths.LibraryRootFromCompletedPath("/downloads/complete/movies/The.End.of.Oak.Street.2024"));
    }

    [Fact]
    public void LibraryRootFromCompletedPath_PrefersConfiguredRoot_WhenStorageIsUnderIt()
    {
        Assert.Equal(
            "/srv/media/movies",
            LibraryPaths.LibraryRootFromCompletedPath("/srv/media/movies/The End of Oak Street/file.mkv", "/srv/media/movies"));
    }

    [Fact]
    public void LibraryRootFromCompletedPath_VideoFile_WalksUpToCategory()
    {
        Assert.Equal(
            "/data/movies",
            LibraryPaths.LibraryRootFromCompletedPath("/data/movies/Title/Title.2024.1080p.mkv"));
    }
}
