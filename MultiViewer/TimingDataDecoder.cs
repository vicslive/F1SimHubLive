using System;
using System.Linq;
using F1SimHubLive.Telemetry;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.MultiViewer
{
    /// <summary>
    /// Parses MultiViewer's /api/v1/live-timing/TimingData JSON for a single driver.
    /// Shape (top-level): { Lines: { "44": { Position, NumberOfLaps, BestLapTime: {Value, Lap},
    /// LastLapTime: {Value}, GapToLeader, IntervalToPositionAhead: {Value}, InPit, PitOut } } }
    /// Returns null when the driver entry is missing or unparseable (replay just started, etc.).
    /// </summary>
    internal static class TimingDataDecoder
    {
        public static TimingSnapshot? Parse(string json, string driverNumber)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            JObject root;
            try { root = JObject.Parse(json); }
            catch { return null; }

            var lines = root["Lines"] as JObject;
            if (lines == null) return null;
            var driver = lines[driverNumber] as JObject;
            if (driver == null) return null;

            var snap = new TimingSnapshot
            {
                Utc = DateTime.UtcNow,
                DriverNumber = driverNumber,
                Lap = (int?)driver["NumberOfLaps"] ?? 0,
                Position = (string?)driver["Position"] ?? "",
                GapToLeader = (string?)driver["GapToLeader"] ?? "",
                InPit = (bool?)driver["InPit"] ?? false,
                PitOut = (bool?)driver["PitOut"] ?? false,
            };

            if (driver["BestLapTime"] is JObject best)
                snap.BestLapTime = (string?)best["Value"] ?? "";
            if (driver["LastLapTime"] is JObject last)
                snap.LastLapTime = (string?)last["Value"] ?? "";
            if (driver["IntervalToPositionAhead"] is JObject iv)
                snap.IntervalToAhead = (string?)iv["Value"] ?? "";

            // Race / replay shape: GapToLeader is a top-level string and
            // IntervalToPositionAhead is { Value: "+0.401" }. Practice / Qualifying
            // LIVE shape: those fields are absent and gaps live in Stats[0] as
            // TimeDiffToFastest (= LDR) and TimeDifftoPositionAhead (= INT).
            // Note MV's typo: lowercase 't' in "TimeDif*f*to*P*ositionAhead".
            // Replays of non-race sessions reconstruct the race-shape fields,
            // which is why this only manifests on live FP / Q sessions.
            if (string.IsNullOrEmpty(snap.GapToLeader) || string.IsNullOrEmpty(snap.IntervalToAhead))
            {
                var stats0 = (driver["Stats"] as JArray)?.FirstOrDefault() as JObject;
                if (stats0 != null)
                {
                    if (string.IsNullOrEmpty(snap.GapToLeader))
                        snap.GapToLeader = (string?)stats0["TimeDiffToFastest"] ?? "";
                    if (string.IsNullOrEmpty(snap.IntervalToAhead))
                        snap.IntervalToAhead = (string?)stats0["TimeDifftoPositionAhead"] ?? "";
                }
            }

            if (driver["Sectors"] is JArray sectors)
            {
                if (sectors.Count > 0 && sectors[0] is JObject s1)
                {
                    snap.Sector1Time = (string?)s1["Value"] ?? "";
                    snap.Sector1IsPersonalBest = (bool?)s1["PersonalFastest"] ?? false;
                    snap.Sector1IsOverallBest = (bool?)s1["OverallFastest"] ?? false;
                }
                if (sectors.Count > 1 && sectors[1] is JObject s2)
                {
                    snap.Sector2Time = (string?)s2["Value"] ?? "";
                    snap.Sector2IsPersonalBest = (bool?)s2["PersonalFastest"] ?? false;
                    snap.Sector2IsOverallBest = (bool?)s2["OverallFastest"] ?? false;
                }
                if (sectors.Count > 2 && sectors[2] is JObject s3)
                {
                    snap.Sector3Time = (string?)s3["Value"] ?? "";
                    snap.Sector3IsPersonalBest = (bool?)s3["PersonalFastest"] ?? false;
                    snap.Sector3IsOverallBest = (bool?)s3["OverallFastest"] ?? false;
                }
            }

            // Identify the car immediately ahead (Position = our Position - 1) and the leader
            // (Position = 1). Pull their sector times into the same snapshot so the dashboard
            // can render INT/LDR sector rows alongside our driver's.
            int ourPos = ParsePos(snap.Position);
            if (ourPos > 0)
            {
                JObject? aheadDriver = null, leaderDriver = null;
                string aheadNum = "", leaderNum = "";
                foreach (var kv in lines)
                {
                    if (kv.Key == driverNumber) continue;
                    if (kv.Value is not JObject d) continue;
                    int p = ParsePos((string?)d["Position"] ?? "");
                    if (p <= 0) continue;
                    if (ourPos > 1 && p == ourPos - 1) { aheadDriver = d; aheadNum = kv.Key; }
                    if (p == 1) { leaderDriver = d; leaderNum = kv.Key; }
                }
                snap.AheadCarNumber = aheadNum;
                snap.LeaderCarNumber = leaderNum;
                if (aheadDriver != null) FillAheadSectors(snap, aheadDriver);
                if (leaderDriver != null) FillLeaderSectors(snap, leaderDriver);
            }

            return snap;
        }

        private static int ParsePos(string s) => int.TryParse(s, out var n) ? n : 0;

        private static void FillAheadSectors(TimingSnapshot snap, JObject d)
        {
            if (d["LastLapTime"] is JObject aheadLast)
                snap.AheadLastLapTime = (string?)aheadLast["Value"] ?? "";
            if (d["BestLapTime"] is JObject aheadBest)
                snap.AheadBestLapTime = (string?)aheadBest["Value"] ?? "";
            snap.AheadInPit = (bool?)d["InPit"] ?? false;
            if (d["Sectors"] is not JArray sectors) return;
            if (sectors.Count > 0 && sectors[0] is JObject s1)
            {
                snap.AheadSector1Time = (string?)s1["Value"] ?? "";
                snap.AheadSector1IsPersonalBest = (bool?)s1["PersonalFastest"] ?? false;
                snap.AheadSector1IsOverallBest = (bool?)s1["OverallFastest"] ?? false;
            }
            if (sectors.Count > 1 && sectors[1] is JObject s2)
            {
                snap.AheadSector2Time = (string?)s2["Value"] ?? "";
                snap.AheadSector2IsPersonalBest = (bool?)s2["PersonalFastest"] ?? false;
                snap.AheadSector2IsOverallBest = (bool?)s2["OverallFastest"] ?? false;
            }
            if (sectors.Count > 2 && sectors[2] is JObject s3)
            {
                snap.AheadSector3Time = (string?)s3["Value"] ?? "";
                snap.AheadSector3IsPersonalBest = (bool?)s3["PersonalFastest"] ?? false;
                snap.AheadSector3IsOverallBest = (bool?)s3["OverallFastest"] ?? false;
            }
        }

        private static void FillLeaderSectors(TimingSnapshot snap, JObject d)
        {
            if (d["LastLapTime"] is JObject leaderLast)
                snap.LeaderLastLapTime = (string?)leaderLast["Value"] ?? "";
            if (d["BestLapTime"] is JObject leaderBest)
                snap.LeaderBestLapTime = (string?)leaderBest["Value"] ?? "";
            snap.LeaderInPit = (bool?)d["InPit"] ?? false;
            if (d["Sectors"] is not JArray sectors) return;
            if (sectors.Count > 0 && sectors[0] is JObject s1)
            {
                snap.LeaderSector1Time = (string?)s1["Value"] ?? "";
                snap.LeaderSector1IsPersonalBest = (bool?)s1["PersonalFastest"] ?? false;
                snap.LeaderSector1IsOverallBest = (bool?)s1["OverallFastest"] ?? false;
            }
            if (sectors.Count > 1 && sectors[1] is JObject s2)
            {
                snap.LeaderSector2Time = (string?)s2["Value"] ?? "";
                snap.LeaderSector2IsPersonalBest = (bool?)s2["PersonalFastest"] ?? false;
                snap.LeaderSector2IsOverallBest = (bool?)s2["OverallFastest"] ?? false;
            }
            if (sectors.Count > 2 && sectors[2] is JObject s3)
            {
                snap.LeaderSector3Time = (string?)s3["Value"] ?? "";
                snap.LeaderSector3IsPersonalBest = (bool?)s3["PersonalFastest"] ?? false;
                snap.LeaderSector3IsOverallBest = (bool?)s3["OverallFastest"] ?? false;
            }
        }
    }
}
