using Jellyfin.Data.Enums;

namespace MediaBrowser.Controller.Channels;

/// <summary>
/// Helpers so native search keeps channel title cards visible.
/// Treasure-Maps titles are <see cref="BaseItemKind.BoxSet"/>; Fire TV and iOS often request only Movie/Series.
/// </summary>
public static class ChannelTitleCards
{
    /// <summary>
    /// Adds <see cref="BaseItemKind.BoxSet"/> when the client asked for movies or series.
    /// </summary>
    /// <param name="includeItemTypes">The types the client requested.</param>
    /// <returns>The original array, or a copy that also includes BoxSet.</returns>
    public static BaseItemKind[] IncludeIn(BaseItemKind[] includeItemTypes)
    {
        if (includeItemTypes is null || includeItemTypes.Length == 0)
        {
            return includeItemTypes ?? [];
        }

        var hasBoxSet = false;
        var wantsTitles = false;
        foreach (var kind in includeItemTypes)
        {
            if (kind == BaseItemKind.BoxSet)
            {
                hasBoxSet = true;
            }
            else if (kind is BaseItemKind.Movie or BaseItemKind.Series)
            {
                wantsTitles = true;
            }
        }

        if (hasBoxSet || !wantsTitles)
        {
            return includeItemTypes;
        }

        var expanded = new BaseItemKind[includeItemTypes.Length + 1];
        includeItemTypes.CopyTo(expanded, 0);
        expanded[includeItemTypes.Length] = BaseItemKind.BoxSet;
        return expanded;
    }
}
