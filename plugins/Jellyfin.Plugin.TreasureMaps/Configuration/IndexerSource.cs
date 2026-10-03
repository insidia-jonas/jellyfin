using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TreasureMaps.Configuration;

/// <summary>An independently configured search and download source.</summary>
public sealed class IndexerSource
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = "newznab";
    public string Url { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string MovieCategories { get; set; } = "2000";
    public string TvCategories { get; set; } = "5000";

    public static IReadOnlyList<IndexerSource> Sources(PluginConfiguration config, bool includeDisabled = false)
    {
        // Old configuration and release IDs continue to work without a destructive migration.
        var sources = config.Indexers is { Count: > 0 } ? config.Indexers : new List<IndexerSource>
        {
            new() { Id = "legacy", Name = "Indexer 1", Protocol = "treasuremaps", Url = config.BaseUrl, ApiKey = config.ApiKey }
        };
        return sources.Where(s => s != null && (includeDisabled || s.Enabled) && !string.IsNullOrWhiteSpace(s.Url)
            && !string.IsNullOrWhiteSpace(s.ApiKey)).Take(8).ToArray();
    }

    public void Validate()
    {
        if (!Regex.IsMatch(Id, "^[a-zA-Z0-9_-]{1,40}$")) { throw new ArgumentException("Ungültige Indexer-ID."); }
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80) { throw new ArgumentException("Indexer-Namen angeben (maximal 80 Zeichen)."); }
        if (Protocol is not ("newznab" or "treasuremaps")) { throw new ArgumentException("Unbekanntes Indexer-Protokoll."); }
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        { throw new ArgumentException("HTTP(S)-Adresse ohne Zugangsdaten oder Abfrageparameter angeben."); }
        if (string.IsNullOrWhiteSpace(ApiKey)) { throw new ArgumentException("API-Schlüssel fehlt."); }
        foreach (var categories in new[] { MovieCategories, TvCategories })
        {
            if (!Regex.IsMatch(categories, "^[0-9]{1,6}(,[0-9]{1,6}){0,15}$")) { throw new ArgumentException("Kategorien als kommagetrennte Nummern angeben."); }
        }
    }

    public string Scope(string guid) => Id == "legacy" ? guid : "ix~" + Id + "~" + Convert.ToBase64String(Encoding.UTF8.GetBytes(guid)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static (string Source, string Guid) Unscope(string guid)
    {
        if (!guid.StartsWith("ix~", StringComparison.Ordinal)) { return ("legacy", guid); }
        var parts = guid.Split('~');
        if (parts.Length != 3) { throw new ArgumentException("Ungültige Veröffentlichungs-ID."); }
        try
        {
            var encoded = parts[2].Replace('-', '+').Replace('_', '/');
            return (parts[1], Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))));
        }
        catch (FormatException) { throw new ArgumentException("Ungültige Veröffentlichungs-ID."); }
    }
}
