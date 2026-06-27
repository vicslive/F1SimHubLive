using System;
using System.Collections.Generic;
using F1SimHubLive.Telemetry;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.MultiViewer
{
    /// <summary>
    /// Decoder for F1 MultiViewer's /api/v1/live-timing/DriverList endpoint (and the matching
    /// F1 SignalR DriverList topic — same JSON shape). Returns a JSON object whose top-level
    /// keys are driver-number strings ("1", "44", ...), each mapped to a driver-info object
    /// with TLA, FullName, BroadcastName, FirstName, LastName, TeamName, TeamColour, etc.
    /// </summary>
    internal static class DriverListDecoder
    {
        public static int CountDrivers(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return 0;
            try
            {
                var root = JObject.Parse(json);
                int n = 0;
                foreach (var prop in root.Properties())
                {
                    if (IsAllDigits(prop.Name)) n++;
                }
                return n;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Pulls identity fields for a specific racing number out of a DriverList JSON
        /// payload. Returns null when the payload is empty, malformed, or doesn't contain
        /// an entry for the requested number.
        /// </summary>
        public static DriverInfoSnapshot? ParseDriverInfo(string json, string driverNumber)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(driverNumber))
                return null;
            try
            {
                var root = JObject.Parse(json);
                var entry = root[driverNumber] as JObject;
                if (entry == null) return null;

                var info = new DriverInfoSnapshot
                {
                    Utc = DateTime.UtcNow,
                    DriverNumber = driverNumber,
                    Tla = Str(entry, "Tla"),
                    FirstName = Str(entry, "FirstName"),
                    LastName = Str(entry, "LastName"),
                    FullName = Str(entry, "FullName"),
                    BroadcastName = Str(entry, "BroadcastName"),
                    TeamName = Str(entry, "TeamName"),
                    TeamColour = Str(entry, "TeamColour"),
                };

                // Synthesize BroadcastName ("F LASTNAME") when feed omits it.
                if (string.IsNullOrEmpty(info.BroadcastName)
                    && info.FirstName.Length > 0 && info.LastName.Length > 0)
                {
                    info.BroadcastName = string.Concat(
                        char.ToUpperInvariant(info.FirstName[0]), " ",
                        info.LastName.ToUpperInvariant());
                }
                return info;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Parses identity for <b>every</b> driver in a DriverList payload, keyed
        /// by racing-number string. Used by the replay grid so the picker can show
        /// the whole field (TLA / last name / team colour) at once.
        /// </summary>
        public static Dictionary<string, DriverInfoSnapshot> ParseAllDrivers(string json)
        {
            var map = new Dictionary<string, DriverInfoSnapshot>();
            if (string.IsNullOrWhiteSpace(json)) return map;
            try
            {
                var root = JObject.Parse(json);
                foreach (var prop in root.Properties())
                {
                    if (!IsAllDigits(prop.Name)) continue;
                    var info = ParseDriverInfo(json, prop.Name);
                    if (info != null) map[prop.Name] = info;
                }
            }
            catch { /* return whatever parsed */ }
            return map;
        }

        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9') return false;
            }
            return true;
        }

        private static string Str(JObject obj, string key)
        {
            var t = obj[key];
            if (t == null || t.Type == JTokenType.Null) return "";
            return t.ToString();
        }
    }
}

