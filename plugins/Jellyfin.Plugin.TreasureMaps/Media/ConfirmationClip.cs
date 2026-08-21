using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Media;

/// <summary>
/// Extracts the bundled "Download started" confirmation clip (played by the play-to-download
/// flow) from the plugin assembly to a local file the server can stream.
/// </summary>
public static class ConfirmationClip
{
    private static readonly object _lock = new();
    private static string? _path;

    /// <summary>
    /// Gets the local file path of the confirmation clip, extracting it on first use.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <returns>The clip path.</returns>
    public static string GetPath(ILogger logger)
    {
        lock (_lock)
        {
            if (_path is not null && File.Exists(_path))
            {
                return _path;
            }

            var dir = Path.Combine(Path.GetTempPath(), "treasuremaps");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "download_started.mp4");

            using var stream = typeof(ConfirmationClip).Assembly
                .GetManifestResourceStream("Jellyfin.Plugin.TreasureMaps.Media.download_started.mp4");
            if (stream is null)
            {
                logger.LogWarning("Bundled confirmation clip resource not found");
                return target;
            }

            using (var file = File.Create(target))
            {
                stream.CopyTo(file);
            }

            _path = target;
            return target;
        }
    }
}
