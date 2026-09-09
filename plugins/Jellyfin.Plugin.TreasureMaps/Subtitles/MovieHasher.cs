using System;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>
/// Computes the OpenSubtitles / OSDB "moviehash" (64-bit checksum of the file size plus the first
/// and last 64 KiB), used for exact file-to-subtitle matching.
/// </summary>
public static class MovieHasher
{
    private const int ChunkSize = 64 * 1024; // 64 KiB

    /// <summary>
    /// Computes the moviehash for a file.
    /// </summary>
    /// <param name="path">The video file path.</param>
    /// <returns>The 16-character lowercase hex hash, or null when it cannot be computed.</returns>
    public static string? ComputeHash(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return ComputeHash(stream, stream.Length);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Computes the moviehash for a stream of a known length.
    /// </summary>
    /// <param name="stream">A readable, seekable stream.</param>
    /// <param name="length">The total length in bytes.</param>
    /// <returns>The 16-character lowercase hex hash, or null when the file is too small.</returns>
    public static string? ComputeHash(Stream stream, long length)
    {
        if (length < ChunkSize)
        {
            return null;
        }

        ulong hash = (ulong)length;

        stream.Seek(0, SeekOrigin.Begin);
        hash += SumChunk(stream);

        stream.Seek(-ChunkSize, SeekOrigin.End);
        hash += SumChunk(stream);

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static ulong SumChunk(Stream stream)
    {
        var buffer = new byte[ChunkSize];
        var read = 0;
        while (read < ChunkSize)
        {
            var n = stream.Read(buffer, read, ChunkSize - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        ulong sum = 0;
        for (var i = 0; i + 8 <= ChunkSize; i += 8)
        {
            sum += BitConverter.ToUInt64(buffer, i); // little-endian on all supported platforms
        }

        return sum;
    }
}
