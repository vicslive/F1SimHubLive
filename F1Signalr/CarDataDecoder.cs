using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using F1SimHubLive.Telemetry;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.F1Signalr
{
    internal static class CarDataDecoder
    {
        private const string ChRpm = "0";
        private const string ChSpeed = "2";
        private const string ChGear = "3";
        private const string ChThrottle = "4";
        private const string ChBrake = "5";
        private const string ChDrs = "45";

        public static string Inflate(string base64Deflate)
        {
            byte[] compressed = Convert.FromBase64String(base64Deflate);
            using var ms = new MemoryStream(compressed);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress, leaveOpen: false);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        public static IEnumerable<DriverSnapshot> ParseCarData(string base64Deflate, string driverNumber)
        {
            string json = Inflate(base64Deflate);
            return ParseCarDataJson(json, driverNumber);
        }

        /// <summary>
        /// Decodes the latest channels for <b>every</b> car present in a CarData
        /// payload (already-inflated JSON), keyed by racing-number string. Used by
        /// the replay grid to show all drivers at once. The last entry in the
        /// payload wins per driver, so the returned map is the freshest snapshot
        /// at that timeline position.
        /// </summary>
        public static Dictionary<string, DriverSnapshot> ParseAllLatestJson(string json)
        {
            var map = new Dictionary<string, DriverSnapshot>();
            var root = JObject.Parse(json);
            if (root["Entries"] is not JArray entries) return map;

            foreach (var entry in entries)
            {
                DateTime utc = entry["Utc"]?.Value<DateTime>() ?? DateTime.UtcNow;
                if (entry["Cars"] is not JObject cars) continue;
                foreach (var carProp in cars.Properties())
                {
                    var channels = carProp.Value?["Channels"];
                    if (channels == null) continue;
                    map[carProp.Name] = new DriverSnapshot
                    {
                        Utc = utc,
                        DriverNumber = carProp.Name,
                        Rpm = (double?)channels[ChRpm] ?? 0,
                        Speed = (double?)channels[ChSpeed] ?? 0,
                        Gear = (int?)channels[ChGear] ?? 0,
                        Throttle = (double?)channels[ChThrottle] ?? 0,
                        Brake = (double?)channels[ChBrake] ?? 0,
                        Drs = (int?)channels[ChDrs] ?? 0,
                    };
                }
            }
            return map;
        }

        /// <summary>
        /// Returns the freshest frame timestamp in a CarData payload, across
        /// <b>all</b> cars (driver-independent). Used as the session-clock
        /// playhead so the wheel clock keeps advancing even when the selected
        /// driver has no frames in this batch and across driver switches.
        /// Returns DateTime.MinValue when the payload has no usable entries.
        /// </summary>
        public static DateTime LatestFrameUtc(string json)
        {
            if (string.IsNullOrEmpty(json)) return DateTime.MinValue;
            try
            {
                var root = JObject.Parse(json);
                if (root["Entries"] is not JArray entries) return DateTime.MinValue;
                DateTime latest = DateTime.MinValue;
                foreach (var entry in entries)
                {
                    var utc = entry["Utc"]?.Value<DateTime>();
                    if (utc.HasValue && utc.Value > latest) latest = utc.Value;
                }
                return latest;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        public static IEnumerable<DriverSnapshot> ParseCarDataJson(string json, string driverNumber)
        {
            var root = JObject.Parse(json);
            if (root["Entries"] is not JArray entries) yield break;

            foreach (var entry in entries)
            {
                DateTime utc = entry["Utc"]?.Value<DateTime>() ?? DateTime.UtcNow;
                var cars = entry["Cars"];
                if (cars == null) continue;
                var car = cars[driverNumber];
                if (car == null) continue;
                var channels = car["Channels"];
                if (channels == null) continue;

                yield return new DriverSnapshot
                {
                    Utc = utc,
                    DriverNumber = driverNumber,
                    Rpm = (double?)channels[ChRpm] ?? 0,
                    Speed = (double?)channels[ChSpeed] ?? 0,
                    Gear = (int?)channels[ChGear] ?? 0,
                    Throttle = (double?)channels[ChThrottle] ?? 0,
                    Brake = (double?)channels[ChBrake] ?? 0,
                    Drs = (int?)channels[ChDrs] ?? 0,
                };
            }
        }
    }
}
