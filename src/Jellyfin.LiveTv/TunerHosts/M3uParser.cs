#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts
{
    public partial class M3uParser
    {
        private const string ExtInfPrefix = "#EXTINF:";

        private readonly ILogger _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public M3uParser(ILogger logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        [GeneratedRegex(@"([a-z0-9\-_]+)=\""([^""]+)\""", RegexOptions.IgnoreCase, "en-US")]
        private static partial Regex KeyValueRegex();

        [GeneratedRegex(@"([a-z0-9\-_]+)=([^\s"",]+)", RegexOptions.IgnoreCase, "en-US")]
        private static partial Regex UnquotedKeyValueRegex();

        public async Task<List<ChannelInfo>> Parse(TunerHostInfo info, string channelIdPrefix, CancellationToken cancellationToken)
        {
            var playlist = await ParsePlaylist(info, channelIdPrefix, cancellationToken).ConfigureAwait(false);
            return playlist.Channels;
        }

        internal async Task<M3uPlaylist> ParsePlaylist(TunerHostInfo info, string channelIdPrefix, CancellationToken cancellationToken)
        {
            using (var reader = new StreamReader(await GetListingsStream(info, cancellationToken).ConfigureAwait(false)))
            {
                return await GetPlaylistAsync(reader, channelIdPrefix, info.Id).ConfigureAwait(false);
            }
        }

        public async Task<Stream> GetListingsStream(TunerHostInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);

            var listingsUrl = M3uUrlFailover.GetPlaylistUrl(info);
            if (!listingsUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return AsyncFile.OpenRead(listingsUrl);
            }

            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, listingsUrl);
            if (!string.IsNullOrEmpty(info.UserAgent))
            {
                requestMessage.Headers.UserAgent.TryParseAdd(info.UserAgent);
            }

            // Set HttpCompletionOption.ResponseHeadersRead to prevent timeouts on larger files
            var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<M3uPlaylist> GetPlaylistAsync(TextReader reader, string channelIdPrefix, string tunerHostId)
        {
            var playlist = new M3uPlaylist();
            string extInf = string.Empty;
            string extGrp = string.Empty;
            var pendingHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await foreach (var line in reader.ReadAllLinesAsync().ConfigureAwait(false))
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    continue;
                }

                if (trimmedLine.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyExtM3U(playlist, trimmedLine);
                    continue;
                }

                if (trimmedLine.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyVlcOpt(
                        string.IsNullOrWhiteSpace(extInf) ? playlist : null,
                        string.IsNullOrWhiteSpace(extInf) ? null : pendingHeaders,
                        trimmedLine["#EXTVLCOPT:".Length..]);
                    continue;
                }

                if (trimmedLine.StartsWith("#KODIPROP:", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyKodiProp(
                        string.IsNullOrWhiteSpace(extInf) ? playlist : null,
                        string.IsNullOrWhiteSpace(extInf) ? null : pendingHeaders,
                        trimmedLine["#KODIPROP:".Length..]);
                    continue;
                }

                if (trimmedLine.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
                {
                    extGrp = FirstGroupName(trimmedLine["#EXTGRP:".Length..]);
                    continue;
                }

                if (trimmedLine.StartsWith(ExtInfPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    extInf = trimmedLine.Substring(ExtInfPrefix.Length).Trim();
                    pendingHeaders.Clear();
                }
                else if (!string.IsNullOrWhiteSpace(extInf) && !trimmedLine.StartsWith('#'))
                {
                    var (url, urlHeaders) = M3uStreamUrl.Split(trimmedLine);
                    if (!IsValidChannelUrl(url))
                    {
                        _logger.LogWarning("Skipping M3U channel entry with non-HTTP path: {Path}", trimmedLine);
                        extInf = string.Empty;
                        pendingHeaders.Clear();
                        continue;
                    }

                    var channel = GetChannelInfo(extInf, tunerHostId, url);
                    if (string.IsNullOrWhiteSpace(channel.ChannelGroup) && !string.IsNullOrWhiteSpace(extGrp))
                    {
                        channel.ChannelGroup = extGrp;
                    }

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    CopyPlaylistIdentity(headers, playlist);
                    foreach (var header in pendingHeaders)
                    {
                        headers[header.Key] = header.Value;
                    }

                    foreach (var header in urlHeaders)
                    {
                        headers[header.Key] = header.Value;
                    }

                    if (headers.Count > 0)
                    {
                        channel.RequiredHttpHeaders = headers;
                    }

                    var stableKey = string.IsNullOrWhiteSpace(channel.TunerChannelId)
                        ? M3uUrlFailover.StableStreamKey(url)
                        : channel.TunerChannelId;
                    channel.Id = channelIdPrefix + stableKey.GetMD5().ToString("N", CultureInfo.InvariantCulture);

                    channel.Path = url;
                    playlist.Channels.Add(channel);
                    _logger.LogDebug("Parsed channel: {ChannelName}", channel.Name);
                    extInf = string.Empty;
                    pendingHeaders.Clear();
                }
            }

            return playlist;
        }

        private static void ApplyExtM3U(M3uPlaylist playlist, string line)
        {
            var attributes = ParseExtInf(line, out _);

            playlist.EpgUrl ??= SelectFirstHttpUrl(
                GetAttribute(attributes, "url-tvg")
                ?? GetAttribute(attributes, "x-tvg-url")
                ?? GetAttribute(attributes, "tvg-url"));
        }

        private static void ApplyVlcOpt(M3uPlaylist playlist, Dictionary<string, string> channelHeaders, string option)
        {
            if (!TrySplitOption(option, out var key, out var value))
            {
                return;
            }

            if (playlist is not null)
            {
                ApplyPlaylistIdentity(playlist, key, value);
            }

            if (channelHeaders is not null)
            {
                M3uStreamUrl.ApplyIdentityHeader(channelHeaders, key, value);
            }
        }

        private static void ApplyKodiProp(M3uPlaylist playlist, Dictionary<string, string> channelHeaders, string option)
        {
            if (!TrySplitOption(option, out var key, out var value))
            {
                return;
            }

            if (key.Equals("inputstream.adaptive.stream_headers", StringComparison.OrdinalIgnoreCase)
                || key.Equals("inputstream.ffmpegdirect.http-header", StringComparison.OrdinalIgnoreCase))
            {
                if (playlist is not null)
                {
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    M3uStreamUrl.ApplyHeaderPairs(headers, value);
                    foreach (var header in headers)
                    {
                        ApplyPlaylistIdentity(playlist, header.Key, header.Value);
                    }
                }

                if (channelHeaders is not null)
                {
                    M3uStreamUrl.ApplyHeaderPairs(channelHeaders, value);
                }

                return;
            }

            ApplyVlcOpt(playlist, channelHeaders, option);
        }

        private static bool TrySplitOption(string option, out string key, out string value)
        {
            key = string.Empty;
            value = string.Empty;
            var trimmed = option.Trim();
            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            key = trimmed[..separator].Trim();
            value = trimmed[(separator + 1)..].Trim().Trim('"');
            return !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value);
        }

        private static void ApplyPlaylistIdentity(M3uPlaylist playlist, string key, string value)
        {
            if (key.Equals("http-user-agent", StringComparison.OrdinalIgnoreCase)
                || key.Equals("user-agent", StringComparison.OrdinalIgnoreCase)
                || key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                playlist.UserAgent ??= value;
            }
            else if (key.Equals("http-referrer", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("http-referer", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("referrer", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("referer", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            {
                playlist.Referrer ??= value;
            }
        }

        private static void CopyPlaylistIdentity(Dictionary<string, string> headers, M3uPlaylist playlist)
        {
            if (!string.IsNullOrWhiteSpace(playlist.UserAgent))
            {
                headers["User-Agent"] = playlist.UserAgent;
            }

            if (!string.IsNullOrWhiteSpace(playlist.Referrer))
            {
                headers["Referer"] = playlist.Referrer;
            }
        }

        private static string FirstGroupName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var parts = raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : string.Empty;
        }

        private static string GetAttribute(Dictionary<string, string> attributes, string key)
        {
            return attributes.TryGetValue(key, out var value) ? value : null;
        }

        private static string SelectFirstHttpUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Uri.TryCreate(part, UriKind.Absolute, out var uri)
                    && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    return part;
                }
            }

            return null;
        }

        private ChannelInfo GetChannelInfo(string extInf, string tunerHostId, string mediaUrl)
        {
            var channel = new ChannelInfo()
            {
                TunerHostId = tunerHostId
            };

            extInf = extInf.Trim();

            var attributes = ParseExtInf(extInf, out string remaining);
            extInf = remaining;

            if (attributes.TryGetValue("tvg-logo", out string tvgLogo))
            {
                channel.ImageUrl = tvgLogo;
            }
            else if (attributes.TryGetValue("logo", out string logo))
            {
                channel.ImageUrl = logo;
            }

            if (attributes.TryGetValue("group-title", out string groupTitle))
            {
                channel.ChannelGroup = FirstGroupName(groupTitle);
            }

            if (attributes.TryGetValue("tvg-name", out string tvgName))
            {
                channel.TvgName = tvgName;
            }

            if (attributes.TryGetValue("radio", out string radio)
                && radio.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                channel.ChannelType = ChannelType.Radio;
            }

            channel.Name = GetChannelName(extInf, attributes);
            channel.Number = GetChannelNumber(extInf, attributes, mediaUrl);

            attributes.TryGetValue("tvg-id", out string tvgId);

            attributes.TryGetValue("channel-id", out string channelId);

            channel.TunerChannelId = string.IsNullOrWhiteSpace(tvgId) ? channelId : tvgId;
            channel.CallSign = channel.TunerChannelId;

            var channelIdValues = new List<string>();
            if (!string.IsNullOrWhiteSpace(channelId))
            {
                channelIdValues.Add(channelId);
            }

            if (!string.IsNullOrWhiteSpace(tvgId))
            {
                channelIdValues.Add(tvgId);
            }

            if (channelIdValues.Count > 0)
            {
                channel.Id = string.Join('_', channelIdValues);
            }

            return channel;
        }

        private string GetChannelNumber(string extInf, Dictionary<string, string> attributes, string mediaUrl)
        {
            var nameParts = extInf.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var nameInExtInf = nameParts.Length > 1 ? nameParts[^1].AsSpan().Trim() : ReadOnlySpan<char>.Empty;

            string numberString = null;

            if (attributes.TryGetValue("tvg-chno", out var attributeValue)
                && double.TryParse(attributeValue, CultureInfo.InvariantCulture, out _))
            {
                numberString = attributeValue;
            }

            if (!IsValidChannelNumber(numberString))
            {
                if (attributes.TryGetValue("tvg-id", out attributeValue))
                {
                    if (double.TryParse(attributeValue, CultureInfo.InvariantCulture, out _))
                    {
                        numberString = attributeValue;
                    }
                    else if (attributes.TryGetValue("channel-id", out attributeValue)
                        && double.TryParse(attributeValue, CultureInfo.InvariantCulture, out _))
                    {
                        numberString = attributeValue;
                    }
                }

                if (string.IsNullOrWhiteSpace(numberString))
                {
                    // Using this as a fallback now as this leads to Problems with channels like "5 USA"
                    // where 5 isn't meant to be the channel number
                    // Check for channel number with the format from SatIp
                    // #EXTINF:0,84. VOX Schweiz
                    // #EXTINF:0,84.0 - VOX Schweiz
                    if (!nameInExtInf.IsEmpty && !nameInExtInf.IsWhiteSpace())
                    {
                        var numberIndex = nameInExtInf.IndexOf(' ');
                        if (numberIndex > 0)
                        {
                            var numberPart = nameInExtInf[..numberIndex].Trim(stackalloc[] { ' ', '.' });
                            if (double.TryParse(numberPart, CultureInfo.InvariantCulture, out _))
                            {
                                numberString = numberPart.ToString();
                            }
                        }
                    }
                }
            }

            if (!IsValidChannelNumber(numberString))
            {
                numberString = null;
            }

            if (!string.IsNullOrWhiteSpace(numberString))
            {
                numberString = numberString.Trim();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(mediaUrl))
                {
                    numberString = null;
                }
                else
                {
                    try
                    {
                        numberString = Path.GetFileNameWithoutExtension(mediaUrl.AsSpan().RightPart('/')).ToString();

                        if (!IsValidChannelNumber(numberString))
                        {
                            numberString = null;
                        }
                    }
                    catch
                    {
                        // Seeing occasional argument exception here
                        numberString = null;
                    }
                }
            }

            return numberString;
        }

        private static bool IsValidChannelUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "rtp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "udp", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsValidChannelNumber(string numberString)
        {
            if (string.IsNullOrWhiteSpace(numberString)
                || string.Equals(numberString, "-1", StringComparison.Ordinal)
                || string.Equals(numberString, "0", StringComparison.Ordinal))
            {
                return false;
            }

            return double.TryParse(numberString, CultureInfo.InvariantCulture, out _);
        }

        private static string GetChannelName(string extInf, Dictionary<string, string> attributes)
        {
            var nameParts = extInf.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var nameInExtInf = nameParts.Length > 1 ? nameParts[^1].Trim() : null;

            // Check for channel number with the format from SatIp
            // #EXTINF:0,84. VOX Schweiz
            // #EXTINF:0,84.0 - VOX Schweiz
            if (!string.IsNullOrWhiteSpace(nameInExtInf))
            {
                var numberIndex = nameInExtInf.IndexOf(' ', StringComparison.Ordinal);
                if (numberIndex > 0)
                {
                    var numberPart = nameInExtInf.AsSpan(0, numberIndex).Trim(stackalloc[] { ' ', '.' });

                    if (double.TryParse(numberPart, CultureInfo.InvariantCulture, out _))
                    {
                        // channel.Number = number.ToString();
                        nameInExtInf = nameInExtInf.AsSpan(numberIndex + 1).Trim(stackalloc[] { ' ', '-' }).ToString();
                    }
                }
            }

            string name = nameInExtInf;

            if (string.IsNullOrWhiteSpace(name))
            {
                attributes.TryGetValue("tvg-name", out name);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                attributes.TryGetValue("tvg-id", out name);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = null;
            }

            return name;
        }

        private static Dictionary<string, string> ParseExtInf(string line, out string remaining)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var matches = KeyValueRegex().Matches(line);

            remaining = line;

            foreach (Match match in matches)
            {
                var key = match.Groups[1].Value;
                var value = match.Groups[2].Value;

                dict[key] = value;
                remaining = remaining.Replace(key + "=\"" + value + "\"", string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            foreach (Match match in UnquotedKeyValueRegex().Matches(remaining))
            {
                var key = match.Groups[1].Value;
                if (dict.ContainsKey(key))
                {
                    continue;
                }

                dict[key] = match.Groups[2].Value;
            }

            return dict;
        }
    }
}
