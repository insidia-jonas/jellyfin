using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// The Treasure-Maps plugin. Surfaces a Treasure-Maps (Newznab-style) indexer inside Jellyfin
/// as a browsable channel, with a configuration page and an optional grab-to-folder action.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the singleton instance of the plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override Guid Id => new Guid("e2c9a6f4-8b1d-4f3a-9c2e-7a5b6d4c3e21");

    /// <inheritdoc />
    public override string Name => "Treasure-Maps";

    /// <inheritdoc />
    public override string Description => "Browse a Treasure-Maps indexer inside Jellyfin and grab movie releases as NZBs.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
        };

        yield return new PluginPageInfo
        {
            Name = "TreasureMapsBrowse",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.browse.html"
        };
    }
}
