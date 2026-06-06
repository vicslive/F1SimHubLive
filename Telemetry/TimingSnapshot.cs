using System;

namespace F1SimHubLive.Telemetry
{
    /// <summary>
    /// Lap-level timing info for a single driver. Updated at ~1 Hz vs DriverSnapshot's ~10-30 Hz.
    /// Strings are kept as the wire format (e.g. "1:16.982", "+2.546") so the dashboard renders
    /// them exactly as F1 broadcasts them.
    /// </summary>
    public sealed class TimingSnapshot
    {
        public DateTime Utc { get; set; }
        public string DriverNumber { get; set; } = "";

        public int Lap { get; set; }
        public string Position { get; set; } = "";
        public string BestLapTime { get; set; } = "";
        public string LastLapTime { get; set; } = "";
        public string GapToLeader { get; set; } = "";
        public string IntervalToAhead { get; set; } = "";
        public bool InPit { get; set; }
        public bool PitOut { get; set; }

        public string TyreCompound { get; set; } = "";
        public int TyreAge { get; set; }
        public int PitStopCount { get; set; }

        public string TopSpeed { get; set; } = "";
        public int TopSpeedRank { get; set; }

        // Session OVT system is enabled (race-wide); Hamilton-specific availability requires
        // IntervalToAhead < 1.0s as well, computed in the client.
        public bool OvertakeSystemEnabled { get; set; }
        public bool OvertakeAvailable { get; set; }
        public string FlagText { get; set; } = "";

        public string Sector1Time { get; set; } = "";
        public string Sector2Time { get; set; } = "";
        public string Sector3Time { get; set; } = "";
        public bool Sector1IsPersonalBest { get; set; }
        public bool Sector2IsPersonalBest { get; set; }
        public bool Sector3IsPersonalBest { get; set; }
        public bool Sector1IsOverallBest { get; set; }
        public bool Sector2IsOverallBest { get; set; }
        public bool Sector3IsOverallBest { get; set; }

        public string AheadCarNumber { get; set; } = "";
        public string LeaderCarNumber { get; set; } = "";

        // Lap times for the car immediately ahead (INT) and the leader (LDR). These are the
        // "actual lap time" of that other driver, not a gap. The wheel HUD displays them in
        // the LDR/INT panel centers so the driver can compare pace against the leader and
        // the car ahead at a glance, while the colored badge to the right of each panel
        // shows the gap separately (so we don't duplicate gap info).
        public string AheadLastLapTime { get; set; } = "";
        public string AheadBestLapTime { get; set; } = "";
        public string LeaderLastLapTime { get; set; } = "";
        public string LeaderBestLapTime { get; set; } = "";

        public string AheadSector1Time { get; set; } = "";
        public string AheadSector2Time { get; set; } = "";
        public string AheadSector3Time { get; set; } = "";
        public bool AheadSector1IsPersonalBest { get; set; }
        public bool AheadSector2IsPersonalBest { get; set; }
        public bool AheadSector3IsPersonalBest { get; set; }
        public bool AheadSector1IsOverallBest { get; set; }
        public bool AheadSector2IsOverallBest { get; set; }
        public bool AheadSector3IsOverallBest { get; set; }

        public string LeaderSector1Time { get; set; } = "";
        public string LeaderSector2Time { get; set; } = "";
        public string LeaderSector3Time { get; set; } = "";
        public bool LeaderSector1IsPersonalBest { get; set; }
        public bool LeaderSector2IsPersonalBest { get; set; }
        public bool LeaderSector3IsPersonalBest { get; set; }
        public bool LeaderSector1IsOverallBest { get; set; }
        public bool LeaderSector2IsOverallBest { get; set; }
        public bool LeaderSector3IsOverallBest { get; set; }
    }
}
