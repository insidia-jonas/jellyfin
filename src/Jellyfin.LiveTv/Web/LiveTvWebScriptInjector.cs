using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Web;

/// <summary>
/// Copies the Live TV overview script into jellyfin-web and injects it into index.html
/// so desktop web and the in-repo Fire TV web host share the same list/table UI.
/// </summary>
public sealed class LiveTvWebScriptInjector : IHostedService
{
    internal const string ScriptVersion = "2";

    private const string ScriptMarker = "plugin=\"LiveTvOverview\"";

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<LiveTvWebScriptInjector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvWebScriptInjector"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public LiveTvWebScriptInjector(IApplicationPaths appPaths, ILogger<LiveTvWebScriptInjector> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Gets the script tag written into index.html.
    /// </summary>
    public static string ScriptTag =>
        "<script " + ScriptMarker + " defer src=\"livetv-overview.js?v=" + ScriptVersion + "\"></script>";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var webPath = _appPaths.WebPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(webPath) || !Directory.Exists(webPath))
            {
                _logger.LogDebug("Web client folder is missing; Live TV overview script not injected");
                return;
            }

            var scriptPath = Path.Combine(webPath, "livetv-overview.js");
            var script = LoadEmbeddedScript();
            await File.WriteAllTextAsync(scriptPath, script, cancellationToken).ConfigureAwait(false);

            var indexPath = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexPath))
            {
                _logger.LogDebug("Web client index.html not found at {Path}; Live TV overview script copied only", indexPath);
                return;
            }

            var html = await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false);
            var updated = ApplyScriptTag(html);
            if (updated is null || string.Equals(updated, html, StringComparison.Ordinal))
            {
                return;
            }

            await File.WriteAllTextAsync(indexPath, updated, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Injected the Live TV overview script into {Path}", indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject the Live TV overview script");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Inserts or replaces the Live TV overview script tag.
    /// </summary>
    /// <param name="html">The index.html contents.</param>
    /// <returns>The updated HTML, or null when it cannot be patched.</returns>
    public static string? ApplyScriptTag(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        if (html.Contains(ScriptTag, StringComparison.Ordinal))
        {
            return html;
        }

        var existing = Regex.Match(
            html,
            "<script[^>]*" + ScriptMarker + "[^>]*>\\s*</script>",
            RegexOptions.IgnoreCase);
        if (existing.Success)
        {
            return html[..existing.Index] + ScriptTag + html[(existing.Index + existing.Length)..];
        }

        var closing = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closing < 0)
        {
            return null;
        }

        return html[..closing] + ScriptTag + html[closing..];
    }

    private static string LoadEmbeddedScript()
    {
        var assembly = typeof(LiveTvWebScriptInjector).Assembly;
        var name = "Jellyfin.LiveTv.Web.livetv-overview.js";
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Embedded Live TV overview script is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
