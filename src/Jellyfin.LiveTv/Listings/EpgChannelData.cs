#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.LiveTv.Listings
{
    internal class EpgChannelData
    {
        private readonly Dictionary<string, ChannelInfo> _channelsById;

        private readonly Dictionary<string, ChannelInfo> _channelsByNumber;

        private readonly Dictionary<string, ChannelInfo> _channelsByName;

        public EpgChannelData(IEnumerable<ChannelInfo> channels)
        {
            _channelsById = new Dictionary<string, ChannelInfo>(StringComparer.OrdinalIgnoreCase);
            _channelsByNumber = new Dictionary<string, ChannelInfo>(StringComparer.OrdinalIgnoreCase);
            _channelsByName = new Dictionary<string, ChannelInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var channel in channels)
            {
                _channelsById[channel.Id] = channel;
                if (!string.IsNullOrEmpty(channel.CallSign))
                {
                    _channelsById.TryAdd(channel.CallSign, channel);
                }

                if (!string.IsNullOrEmpty(channel.TvgName))
                {
                    _channelsById.TryAdd(channel.TvgName, channel);
                }

                if (!string.IsNullOrEmpty(channel.Number))
                {
                    _channelsByNumber[channel.Number] = channel;
                }

                foreach (var name in NameKeys(channel.Name))
                {
                    _channelsByName.TryAdd(name, channel);
                }

                foreach (var name in NameKeys(channel.TvgName))
                {
                    _channelsByName.TryAdd(name, channel);
                }
            }
        }

        public ChannelInfo? GetChannelById(string id)
            => _channelsById.GetValueOrDefault(id);

        public ChannelInfo? GetChannelByNumber(string number)
            => _channelsByNumber.GetValueOrDefault(number);

        public ChannelInfo? GetChannelByName(string name)
        {
            foreach (var key in NameKeys(name))
            {
                if (_channelsByName.TryGetValue(key, out var channel))
                {
                    return channel;
                }
            }

            return null;
        }

        public static string NormalizeName(string value)
        {
            return StripQualitySuffix(
                value.Replace(" ", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal)
                    .Replace("_", string.Empty, StringComparison.Ordinal));
        }

        private static IEnumerable<string> NameKeys(string? name)
        {
            var normalized = NormalizeName(name ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                yield break;
            }

            yield return normalized;
            var stripped = StripQualitySuffix(normalized);
            if (!string.Equals(stripped, normalized, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(stripped))
            {
                yield return stripped;
            }
        }

        private static string StripQualitySuffix(string value)
        {
            foreach (var suffix in new[] { "UHD", "FHD", "HEVC", "HDR", "HD", "SD" })
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && value.Length > suffix.Length)
                {
                    return value[..^suffix.Length];
                }
            }

            return value;
        }
    }
}
