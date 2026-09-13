using System;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Model.LiveTv
{
    /// <summary>
    /// How far the current Live TV program has progressed (Jetzt), from start/end vs UTC now.
    /// </summary>
    public static class LiveTvProgress
    {
        /// <summary>
        /// Computes 0–100 percent of the current show. Returns <c>null</c> when times are missing or invalid.
        /// </summary>
        /// <param name="startUtc">Program start (UTC).</param>
        /// <param name="endUtc">Program end (UTC).</param>
        /// <param name="utcNow">The current UTC instant.</param>
        /// <returns>Percent in [0, 100], or <c>null</c>.</returns>
        public static double? GetPercent(DateTime? startUtc, DateTime? endUtc, DateTime utcNow)
        {
            if (startUtc is null || endUtc is null)
            {
                return null;
            }

            var start = AsUtc(startUtc.Value);
            var end = AsUtc(endUtc.Value);
            if (end <= start)
            {
                return null;
            }

            var now = AsUtc(utcNow);
            if (now <= start)
            {
                return 0;
            }

            if (now >= end)
            {
                return 100;
            }

            return Math.Round(100d * (now - start).TotalMilliseconds / (end - start).TotalMilliseconds, 1, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Applies start/end and <see cref="BaseItemDto.CompletionPercentage"/> onto a DTO.
        /// </summary>
        /// <param name="dto">The item or current-program DTO.</param>
        /// <param name="startUtc">Program start (UTC).</param>
        /// <param name="endUtc">Program end (UTC).</param>
        /// <param name="utcNow">The current UTC instant.</param>
        public static void Apply(BaseItemDto dto, DateTime? startUtc, DateTime? endUtc, DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(dto);

            if (startUtc is not null)
            {
                dto.StartDate = startUtc;
            }

            if (endUtc is not null)
            {
                dto.EndDate = endUtc;
            }

            var percent = GetPercent(startUtc ?? dto.StartDate, endUtc ?? dto.EndDate, utcNow);
            if (percent is not null)
            {
                dto.CompletionPercentage = percent;
            }
        }

        private static DateTime AsUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
        }
    }
}
