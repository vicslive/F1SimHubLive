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
            // LIVE shape: those fields are absent. We used to fall back to Stats[0]
            // (TimeDiffToFastest / TimeDifftoPositionAhead) but Stats[N] freezes per
            // Q segment — in Q3, Stats[0] = Q1 (ancient), Stats[1] = Q2 (frozen),
            // Stats[2] = current Q3 (empty until the driver sets a lap). Reading
            // Stats[0] gives us Q1 numbers forever, which is what bit the wheel
            // HUD all of v1.3.x. MV's cockpit instead shows a personal-best
            // differential in Q (myPB - aheadPB / myPB - leaderPB). We compute
            // that ourselves below in TryComputeQGapsFromBests once FillAhead /
            // FillLeader have populated AheadBestLapTime and LeaderBestLapTime.

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
                JObject? aheadDriver = null, leaderDriver = null, behindDriver = null;
                string aheadNum = "", leaderNum = "", behindNum = "";
                foreach (var kv in lines)
                {
                    if (kv.Key == driverNumber) continue;
                    if (kv.Value is not JObject d) continue;
                    int p = ParsePos((string?)d["Position"] ?? "");
                    if (p <= 0) continue;
                    if (ourPos > 1 && p == ourPos - 1) { aheadDriver = d; aheadNum = kv.Key; }
                    if (p == ourPos + 1) { behindDriver = d; behindNum = kv.Key; }
                    if (p == 1) { leaderDriver = d; leaderNum = kv.Key; }
                }
                snap.AheadCarNumber = aheadNum;
                snap.LeaderCarNumber = leaderNum;
                snap.BehindCarNumber = behindNum;
                if (aheadDriver != null) FillAheadSectors(snap, aheadDriver);
                if (leaderDriver != null) FillLeaderSectors(snap, leaderDriver);
                if (behindDriver != null) FillBehindSectors(snap, behindDriver);

                // The gap to the car behind is that car's own IntervalToPositionAhead —
                // i.e. how far it trails the car directly ahead of it, which is us.
                if (behindDriver?["IntervalToPositionAhead"] is JObject bv)
                    snap.IntervalToBehind = (string?)bv["Value"] ?? "";

                // Q-mode gap fix: if MV didn't give us live INT/LDR from the
                // race-shape fields, derive them from PB differential — matches
                // what MultiViewer's own cockpit overlay shows in qualifying.
                if (string.IsNullOrEmpty(snap.IntervalToAhead)
                    && !string.IsNullOrEmpty(snap.BestLapTime)
                    && !string.IsNullOrEmpty(snap.AheadBestLapTime))
                {
                    snap.IntervalToAhead = TryFormatGap(snap.BestLapTime, snap.AheadBestLapTime);
                }
                if (string.IsNullOrEmpty(snap.GapToLeader)
                    && !string.IsNullOrEmpty(snap.BestLapTime)
                    && !string.IsNullOrEmpty(snap.LeaderBestLapTime))
                {
                    snap.GapToLeader = TryFormatGap(snap.BestLapTime, snap.LeaderBestLapTime);
                }
                // Behind gap is positive (chaser is slower): behindBest - myBest.
                if (string.IsNullOrEmpty(snap.IntervalToBehind)
                    && !string.IsNullOrEmpty(snap.BestLapTime)
                    && !string.IsNullOrEmpty(snap.BehindBestLapTime))
                {
                    snap.IntervalToBehind = TryFormatGap(snap.BehindBestLapTime, snap.BestLapTime);
                }
            }

            return snap;
        }

        /// <summary>
        /// Parses two "M:SS.fff" lap-time strings and returns the differential
        /// as a signed "+X.XXX" / "-X.XXX" string suitable for INT / LDR
        /// display. Returns "" on parse failure so the dashboard formula
        /// falls back to its "---" default.
        /// </summary>
        private static string TryFormatGap(string mine, string other)
        {
            if (!TryParseLapSeconds(mine, out double m)) return "";
            if (!TryParseLapSeconds(other, out double o)) return "";
            double diff = m - o;
            // MV cockpit shows three decimals, sign included.
            return (diff >= 0 ? "+" : "") + diff.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryParseLapSeconds(string lap, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(lap)) return false;
            // Format examples: "1:13.091", "59.847", "0:59.847"
            int colon = lap.IndexOf(':');
            if (colon < 0)
            {
                return double.TryParse(lap, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds);
            }
            if (!int.TryParse(lap.Substring(0, colon), out int mins)) return false;
            if (!double.TryParse(lap.Substring(colon + 1), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double secs)) return false;
            seconds = mins * 60.0 + secs;
            return true;
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

        private static void FillBehindSectors(TimingSnapshot snap, JObject d)
        {
            if (d["LastLapTime"] is JObject behindLast)
                snap.BehindLastLapTime = (string?)behindLast["Value"] ?? "";
            if (d["BestLapTime"] is JObject behindBest)
                snap.BehindBestLapTime = (string?)behindBest["Value"] ?? "";
            snap.BehindInPit = (bool?)d["InPit"] ?? false;
            if (d["Sectors"] is not JArray sectors) return;
            if (sectors.Count > 0 && sectors[0] is JObject s1)
            {
                snap.BehindSector1Time = (string?)s1["Value"] ?? "";
                snap.BehindSector1IsPersonalBest = (bool?)s1["PersonalFastest"] ?? false;
                snap.BehindSector1IsOverallBest = (bool?)s1["OverallFastest"] ?? false;
            }
            if (sectors.Count > 1 && sectors[1] is JObject s2)
            {
                snap.BehindSector2Time = (string?)s2["Value"] ?? "";
                snap.BehindSector2IsPersonalBest = (bool?)s2["PersonalFastest"] ?? false;
                snap.BehindSector2IsOverallBest = (bool?)s2["OverallFastest"] ?? false;
            }
            if (sectors.Count > 2 && sectors[2] is JObject s3)
            {
                snap.BehindSector3Time = (string?)s3["Value"] ?? "";
                snap.BehindSector3IsPersonalBest = (bool?)s3["PersonalFastest"] ?? false;
                snap.BehindSector3IsOverallBest = (bool?)s3["OverallFastest"] ?? false;
            }
        }
    }
}
