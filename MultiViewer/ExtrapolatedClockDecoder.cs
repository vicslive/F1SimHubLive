using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.MultiViewer
{
    /// <summary>
    /// Parses F1 MultiViewer's ExtrapolatedClock endpoint, which exposes the
    /// session/race time remaining as a baseline + UTC timestamp + extrapolating flag.
    /// Live remaining = Remaining - (now - Utc) when Extrapolating == true.
    /// Sample payload:
    ///   {"Utc":"2026-05-24T20:09:48.004Z","Remaining":"01:59:59","Extrapolating":true}
    /// </summary>
    internal static class ExtrapolatedClockDecoder
    {
        public readonly struct Clock
        {
            public Clock(TimeSpan remaining, DateTime utcBaseline, bool extrapolating)
            {
                Remaining = remaining;
                UtcBaseline = utcBaseline;
                Extrapolating = extrapolating;
            }

            public TimeSpan Remaining { get; }
            public DateTime UtcBaseline { get; }
            public bool Extrapolating { get; }
            public bool IsValid => UtcBaseline != DateTime.MinValue;
        }

        public static Clock Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            try
            {
                var root = JObject.Parse(json);

                TimeSpan remaining = TimeSpan.Zero;
                var remTok = root["Remaining"];
                if (remTok != null && remTok.Type == JTokenType.String)
                {
                    var s = remTok.Value<string>();
                    if (!string.IsNullOrEmpty(s) && TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts))
                        remaining = ts;
                }

                DateTime utc = DateTime.MinValue;
                var utcTok = root["Utc"];
                if (utcTok != null && utcTok.Type == JTokenType.String)
                {
                    var s = utcTok.Value<string>();
                    if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                        utc = dt;
                }

                bool extrapolating = false;
                var extTok = root["Extrapolating"];
                if (extTok != null && extTok.Type == JTokenType.Boolean)
                    extrapolating = extTok.Value<bool>();

                return new Clock(remaining, utc, extrapolating);
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// Project the baseline forward to "now". When Extrapolating==true subtract elapsed
        /// since the baseline UTC. Returns TimeSpan.Zero floor (never negative).
        /// </summary>
        public static TimeSpan LiveRemaining(Clock clock, DateTime nowUtc)
        {
            if (!clock.IsValid) return TimeSpan.Zero;
            if (!clock.Extrapolating) return clock.Remaining;
            var elapsed = nowUtc - clock.UtcBaseline;
            if (elapsed <= TimeSpan.Zero) return clock.Remaining;
            var live = clock.Remaining - elapsed;
            return live < TimeSpan.Zero ? TimeSpan.Zero : live;
        }

        public static string Format(TimeSpan ts)
        {
            if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
            // Races run ~2h so show H:MM:SS; practice / qualifying are always
            // under an hour, so drop the leading "0:" and show MM:SS to match
            // F1 TV, the video, and MV's live timing.
            if (ts.TotalHours >= 1)
                return string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}",
                    (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}",
                ts.Minutes, ts.Seconds);
        }
    }
}
