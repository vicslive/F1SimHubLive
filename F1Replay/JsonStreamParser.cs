using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.F1Replay
{
    /// <summary>
    /// Parses an F1 archive <c>.jsonStream</c> file into timestamped payloads.
    ///
    /// Every non-empty line is a 12-char-ish session-relative timestamp
    /// (<c>HH:MM:SS.fff</c>) immediately followed by a JSON token:
    ///
    ///   00:00:26.141{"AirTemp":"33.2", ...}          (object — WeatherData)
    ///   00:13:12.826{"Status":"1","Message":"AllClear"}  (object — TrackStatus)
    ///   00:00:01.234"hr2H4sIA...=="                  (string — CarData.z base64)
    ///
    /// The boundary between the timestamp and the payload is the first
    /// <c>{</c>, <c>[</c> or <c>"</c> on the line — none of which can appear in
    /// the timestamp — so we split there rather than assuming a fixed width.
    /// </summary>
    internal static class JsonStreamParser
    {
        public static List<RawLine> Parse(string streamText)
        {
            var result = new List<RawLine>();
            if (string.IsNullOrEmpty(streamText)) return result;

            // Archive files are CRLF; tolerate LF-only too.
            foreach (var rawLine in streamText.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;

                int split = IndexOfPayloadStart(line);
                if (split <= 0) continue;

                string tsText = line.Substring(0, split);
                string payloadText = line.Substring(split);

                if (!TryParseOffset(tsText, out var offset)) continue;

                JToken payload;
                try { payload = JToken.Parse(payloadText); }
                catch { continue; }

                result.Add(new RawLine(offset, payload));
            }
            return result;
        }

        private static int IndexOfPayloadStart(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '{' || c == '[' || c == '"') return i;
            }
            return -1;
        }

        /// <summary>
        /// Parses the session-relative timestamp prefix. Normal form is
        /// <c>HH:MM:SS.fff</c>; some early-session lines drop the fractional
        /// part. TimeSpan.Parse handles both with the invariant culture.
        /// </summary>
        internal static bool TryParseOffset(string ts, out TimeSpan offset)
        {
            if (TimeSpan.TryParse(ts, CultureInfo.InvariantCulture, out offset))
                return true;
            offset = TimeSpan.Zero;
            return false;
        }
    }

    /// <summary>One parsed stream line: when (offset from session start) + what.</summary>
    internal readonly struct RawLine
    {
        public readonly TimeSpan Offset;
        public readonly JToken Payload;
        public RawLine(TimeSpan offset, JToken payload)
        {
            Offset = offset;
            Payload = payload;
        }
    }
}
