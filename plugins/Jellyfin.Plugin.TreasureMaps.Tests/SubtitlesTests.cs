using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.TreasureMaps.Subtitles;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class SubtitlesTests
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MovieHasher_IsDeterministic_AndSizeSensitive()
    {
        // Build two 256 KiB streams that differ by one byte.
        var a = new byte[256 * 1024];
        for (var i = 0; i < a.Length; i++)
        {
            a[i] = (byte)(i % 251);
        }

        var b = (byte[])a.Clone();
        b[100] ^= 0xFF;

        using var sa = new MemoryStream(a);
        using var sb = new MemoryStream(b);

        var ha1 = MovieHasher.ComputeHash(sa, a.Length);
        sa.Position = 0;
        var ha2 = MovieHasher.ComputeHash(sa, a.Length);
        var hb = MovieHasher.ComputeHash(sb, b.Length);

        Assert.NotNull(ha1);
        Assert.Equal(16, ha1!.Length);       // 16 hex chars = 64-bit
        Assert.Equal(ha1, ha2);              // deterministic
        Assert.NotEqual(ha1, hb);            // content in first 64KiB affects the hash
    }

    [Fact]
    public void MovieHasher_TooSmall_ReturnsNull()
    {
        using var s = new MemoryStream(new byte[1024]);
        Assert.Null(MovieHasher.ComputeHash(s, 1024));
    }

    [Fact]
    public void SearchResponse_Deserializes()
    {
        const string json = """
        {
          "data": [
            {
              "id": "12345",
              "attributes": {
                "language": "de",
                "download_count": 4210,
                "ratings": 8.5,
                "hearing_impaired": false,
                "ai_translated": false,
                "machine_translated": false,
                "moviehash_match": true,
                "release": "Dune.2021.1080p.BluRay",
                "files": [ { "file_id": 998877, "file_name": "Dune.srt" } ]
              }
            }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<OsSearchResponse>(json, _options);

        Assert.NotNull(response);
        var sub = Assert.Single(response!.Data);
        Assert.Equal("de", sub.Attributes!.Language);
        Assert.Equal(4210, sub.Attributes.DownloadCount);
        Assert.True(sub.Attributes.MoviehashMatch);
        Assert.Equal(998877, sub.Attributes.Files[0].FileId);
    }
}
