using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using F1SimHubLive.Picker.Models;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Polls MultiViewer's live-timing endpoints (DriverList, TimingData,
/// TimingAppData) every <see cref="PollIntervalMs"/> milliseconds and
/// merges the results into an ObservableCollection of
/// <see cref="DriverTimingRow"/> instances, sorted by Position.
///
/// The collection is owned by the UI; this service mutates row properties
/// in place via the UI dispatcher to keep WPF bindings happy without
/// rebuilding the collection on every tick (which would lose scroll
/// position and selection state).
/// </summary>
public sealed class LiveTimingClient : IDisposable
{
    private const int PollIntervalMs = 500;
    private const int DriverListRefreshMs = 30_000;

    private readonly HttpClient _http;
    private readonly Dispatcher _dispatcher;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private DateTime _lastDriverListFetch = DateTime.MinValue;
    private Dictionary<string, DriverInfo> _drivers = new();

    /// <summary>
    /// Per-driver best sector seconds (3 entries each, double.MaxValue
    /// when no time recorded yet). Used to compute session bests and to
    /// preserve a driver's PB across the session — MV's TimingData only
    /// flags PersonalFastest on the LAP that set the time, so we keep the
    /// running minimum ourselves.
    /// </summary>
    private readonly Dictionary<string, double[]> _bestSectorSeconds = new();
    private readonly Dictionary<string, string[]> _bestSectorStrings = new();

    /// <summary>Drivers keyed by RacingNumber, kept in position order.</summary>
    public ObservableCollection<DriverTimingRow> Rows { get; } = new();

    /// <summary>Raised on the UI thread whenever a poll fails.</summary>
    public event Action<string>? OnStatus;

    public LiveTimingClient(Dispatcher dispatcher, string baseUrl = "http://localhost:10101")
    {
        _dispatcher = dispatcher;
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    public void Start()
    {
        if (_loop != null) return;
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _http.Dispose();
        _cts.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        int consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // DriverList rarely changes — fetch it occasionally to pick
                // up driver replacements (test/reserve sessions).
                if ((DateTime.UtcNow - _lastDriverListFetch).TotalMilliseconds > DriverListRefreshMs)
                {
                    var driverListJson = await _http.GetStringAsync($"{_baseUrl}/api/v1/live-timing/DriverList", ct).ConfigureAwait(false);
                    _drivers = ParseDriverList(driverListJson);
                    _lastDriverListFetch = DateTime.UtcNow;
                }

                // The hot loop: timing + tire data.
                var timingTask = _http.GetStringAsync($"{_baseUrl}/api/v1/live-timing/TimingData", ct);
                var appTask = _http.GetStringAsync($"{_baseUrl}/api/v1/live-timing/TimingAppData", ct);
                await Task.WhenAll(timingTask, appTask).ConfigureAwait(false);

                var snapshot = BuildSnapshot(timingTask.Result, appTask.Result);
                consecutiveFailures = 0;

                _ = _dispatcher.BeginInvoke(new Action(() => ApplySnapshot(snapshot)));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= 3)
                {
                    var msg = $"Live timing offline: {ex.Message}";
                    _ = _dispatcher.BeginInvoke(new Action(() => OnStatus?.Invoke(msg)));
                }
            }

            try { await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------

    private static Dictionary<string, DriverInfo> ParseDriverList(string json)
    {
        var dict = new Dictionary<string, DriverInfo>();
        var root = JsonNode.Parse(json)?.AsObject();
        if (root == null) return dict;
        foreach (var kv in root)
        {
            var d = kv.Value?.AsObject();
            if (d == null) continue;
            dict[kv.Key] = new DriverInfo(
                RacingNumber: kv.Key,
                Tla: d["Tla"]?.GetValue<string>() ?? "",
                LastName: d["LastName"]?.GetValue<string>() ?? "",
                TeamName: d["TeamName"]?.GetValue<string>() ?? "",
                TeamColour: d["TeamColour"]?.GetValue<string>() ?? ""
            );
        }
        return dict;
    }

    private List<RowSnapshot> BuildSnapshot(string timingJson, string appJson)
    {
        var snaps = new List<RowSnapshot>();

        var timingRoot = JsonNode.Parse(timingJson)?["Lines"]?.AsObject();
        if (timingRoot == null) return snaps;

        var appLines = JsonNode.Parse(appJson)?["Lines"]?.AsObject();

        foreach (var kv in timingRoot)
        {
            var racingNumber = kv.Key;
            var line = kv.Value?.AsObject();
            if (line == null) continue;

            if (!_drivers.TryGetValue(racingNumber, out var info))
            {
                // Skip drivers we don't have metadata for yet — they'll
                // appear once DriverList is fetched.
                continue;
            }

            int.TryParse(line["Position"]?.GetValue<string>(), out int pos);

            string lastLap = line["LastLapTime"]?["Value"]?.GetValue<string>() ?? "";
            LapStatus lastStatus = LapFlag(line["LastLapTime"]);
            string bestLap = line["BestLapTime"]?["Value"]?.GetValue<string>() ?? "";
            LapStatus bestStatus = LapFlag(line["BestLapTime"]);

            string gapToLeader = line["TimeDiffToFastest"]?.GetValue<string>() ?? "";
            string interval = line["TimeDiffToPositionAhead"]?.GetValue<string>() ?? "";

            bool inPit = line["InPit"]?.GetValue<bool>() ?? false;
            bool retired = line["Retired"]?.GetValue<bool>() ?? false;

            // Sectors: 3 entries, each with Segments[]
            var sectorData = new SectorSnapshot[3];
            var sectorsArray = line["Sectors"]?.AsArray();
            if (sectorsArray != null)
            {
                for (int i = 0; i < 3 && i < sectorsArray.Count; i++)
                {
                    var s = sectorsArray[i]?.AsObject();
                    if (s == null) continue;
                    var segments = new List<int>();
                    var segNodes = s["Segments"]?.AsArray();
                    if (segNodes != null)
                    {
                        foreach (var seg in segNodes)
                        {
                            segments.Add(seg?["Status"]?.GetValue<int>() ?? 0);
                        }
                    }
                    string timeStr = s["Value"]?.GetValue<string>() ?? s["PreviousValue"]?.GetValue<string>() ?? "";

                    // Track this driver's PB for the sector. We compare
                    // against the running min because MV only stamps
                    // PersonalFastest on the lap that sets it.
                    if (!string.IsNullOrEmpty(timeStr)
                        && double.TryParse(timeStr, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double sectorSecs)
                        && sectorSecs > 0)
                    {
                        if (!_bestSectorSeconds.TryGetValue(racingNumber, out var bestSecs))
                        {
                            bestSecs = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                            _bestSectorSeconds[racingNumber] = bestSecs;
                            _bestSectorStrings[racingNumber] = new[] { "", "", "" };
                        }
                        if (sectorSecs < bestSecs[i])
                        {
                            bestSecs[i] = sectorSecs;
                            _bestSectorStrings[racingNumber][i] = timeStr;
                        }
                    }

                    sectorData[i] = new SectorSnapshot(
                        Time: timeStr,
                        Status: SectorFlag(s),
                        Segments: segments
                    );
                }
            }

            // Tire info: current stint = last entry in Stints[]
            string tireLetter = "";
            string tireColor = "#7F7F8A";
            int tireAge = 0;
            int pitCount = 0;

            if (appLines != null && appLines.TryGetPropertyValue(racingNumber, out var appNode) && appNode is JsonObject appObj)
            {
                var stints = appObj["Stints"]?.AsArray();
                if (stints != null && stints.Count > 0)
                {
                    var current = stints[stints.Count - 1]?.AsObject();
                    if (current != null)
                    {
                        var compound = current["Compound"]?.GetValue<string>() ?? "";
                        (tireLetter, tireColor) = CompoundDisplay(compound);
                        var totalLaps = current["TotalLaps"];
                        tireAge = totalLaps switch
                        {
                            null => 0,
                            _ => totalLaps.GetValue<int>(),
                        };
                    }
                    // PitStopCount = stint count - 1 (first stint isn't a pit).
                    pitCount = Math.Max(0, stints.Count - 1);
                }
            }

            // Some sessions also expose NumberOfPitStops directly on TimingData
            var nps = line["NumberOfPitStops"];
            if (nps != null)
            {
                try { pitCount = nps.GetValue<int>(); } catch { /* keep stint-derived value */ }
            }

            snaps.Add(new RowSnapshot(
                Info: info,
                Position: pos,
                LastLapTime: lastLap,
                LastLapStatus: lastStatus,
                BestLapTime: bestLap,
                BestLapStatus: bestStatus,
                GapToLeader: gapToLeader,
                IntervalToAhead: interval,
                InPit: inPit,
                Retired: retired,
                TireCompoundLetter: tireLetter,
                TireCompoundColor: tireColor,
                TireAge: tireAge,
                PitStopCount: pitCount,
                Sectors: sectorData
            ));
        }

        // Position 0 means "no position assigned yet" — push them to the
        // bottom so the live order stays clean.
        snaps.Sort((a, b) =>
        {
            int pa = a.Position == 0 ? 99 : a.Position;
            int pb = b.Position == 0 ? 99 : b.Position;
            return pa.CompareTo(pb);
        });

        return snaps;
    }

    /// <summary>
    /// Reads PersonalFastest / OverallFastest flags from a lap-time or
    /// sector JSON object and converts them into a LapStatus.
    /// </summary>
    private static LapStatus LapFlag(JsonNode? node)
    {
        if (node is not JsonObject obj) return LapStatus.None;
        if (obj["OverallFastest"]?.GetValue<bool>() == true) return LapStatus.SessionBest;
        if (obj["PersonalFastest"]?.GetValue<bool>() == true) return LapStatus.PersonalBest;
        return LapStatus.None;
    }

    private static LapStatus SectorFlag(JsonObject sectorObj)
    {
        if (sectorObj["OverallFastest"]?.GetValue<bool>() == true) return LapStatus.SessionBest;
        if (sectorObj["PersonalFastest"]?.GetValue<bool>() == true) return LapStatus.PersonalBest;
        return LapStatus.None;
    }

    private static (string letter, string color) CompoundDisplay(string compound)
    {
        return compound?.ToUpperInvariant() switch
        {
            "SOFT" => ("S", "#E83A3A"),
            "MEDIUM" => ("M", "#F5C518"),
            "HARD" => ("H", "#F5F5FA"),
            "INTERMEDIATE" => ("I", "#3FD06A"),
            "WET" => ("W", "#3C9CF0"),
            "TEST_UNKNOWN" => ("?", "#7F7F8A"),
            _ => ("?", "#7F7F8A"),
        };
    }

    // ---------------------------------------------------------------
    // Apply (UI thread)
    // ---------------------------------------------------------------

    private string? _currentDriverNumber;

    public void SetCurrentDriverNumber(string? number)
    {
        _currentDriverNumber = number;
        // Refresh IsCurrent flags on the next tick — we do it eagerly here
        // too so the highlight responds immediately on user click.
        _dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var r in Rows)
                r.IsCurrent = r.RacingNumber == number;
        }));
    }

    private void ApplySnapshot(List<RowSnapshot> snaps)
    {
        // Build a lookup by RacingNumber for the existing rows.
        var existing = Rows.ToDictionary(r => r.RacingNumber);

        // Compute the field-wide minimum per sector so we know whose PB is
        // also the session best (purple) vs just their personal best (yellow).
        // _bestSectorSeconds is mutated by the parsing pass before us, so it
        // already includes the freshest sector times for everyone.
        var fieldMin = new double[3] { double.MaxValue, double.MaxValue, double.MaxValue };
        foreach (var kv in _bestSectorSeconds)
        {
            for (int i = 0; i < 3; i++)
            {
                if (kv.Value[i] < fieldMin[i]) fieldMin[i] = kv.Value[i];
            }
        }

        // Update / insert.
        for (int i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            if (!existing.TryGetValue(s.Info.RacingNumber, out var row))
            {
                row = new DriverTimingRow
                {
                    RacingNumber = s.Info.RacingNumber,
                    Tla = s.Info.Tla,
                    LastName = s.Info.LastName,
                    TeamName = s.Info.TeamName,
                    TeamColour = s.Info.TeamColour,
                };
                Rows.Add(row);
            }

            row.Position = s.Position;
            row.LastLapTime = s.LastLapTime;
            row.LastLapStatus = s.LastLapStatus;
            row.BestLapTime = s.BestLapTime;
            row.BestLapStatus = s.BestLapStatus;
            row.GapToLeader = s.GapToLeader;
            row.IntervalToAhead = s.IntervalToAhead;
            row.InPit = s.InPit;
            row.Retired = s.Retired;
            row.TireCompoundLetter = s.TireCompoundLetter;
            row.TireCompoundColor = s.TireCompoundColor;
            row.TireAge = s.TireAge;
            row.PitStopCount = s.PitStopCount;
            row.IsCurrent = row.RacingNumber == _currentDriverNumber;

            // Sectors are pre-allocated to 3 entries; only update what's there.
            _bestSectorStrings.TryGetValue(s.Info.RacingNumber, out var driverBestStrings);
            _bestSectorSeconds.TryGetValue(s.Info.RacingNumber, out var driverBestSecs);
            for (int sIdx = 0; sIdx < 3; sIdx++)
            {
                var snap = s.Sectors[sIdx];
                var target = row.Sectors[sIdx];

                // Best-sector row: yellow if PB only, purple if session best.
                string bestStr = driverBestStrings != null ? driverBestStrings[sIdx] : "";
                LapStatus bestStatus = LapStatus.None;
                if (!string.IsNullOrEmpty(bestStr) && driverBestSecs != null)
                {
                    bestStatus = driverBestSecs[sIdx] <= fieldMin[sIdx] + 1e-6
                        ? LapStatus.SessionBest
                        : LapStatus.PersonalBest;
                }
                target.BestTime = bestStr;
                target.BestStatus = bestStatus;

                if (snap == null)
                {
                    target.Time = "";
                    target.Status = LapStatus.None;
                    target.Segments.Clear();
                    continue;
                }
                target.Time = snap.Time;
                target.Status = snap.Status;

                // Sync segments: replace wholesale to keep the visualisation
                // simple. Segment counts change rarely (only between tracks),
                // so this is cheap.
                if (target.Segments.Count != snap.Segments.Count)
                {
                    target.Segments.Clear();
                    foreach (var seg in snap.Segments) target.Segments.Add(seg);
                }
                else
                {
                    for (int j = 0; j < snap.Segments.Count; j++)
                    {
                        if (target.Segments[j] != snap.Segments[j])
                            target.Segments[j] = snap.Segments[j];
                    }
                }
            }
        }

        // Drop rows that disappeared from the snapshot (e.g., driver
        // withdrew). Iterate by index in reverse to keep removal cheap.
        var keep = new HashSet<string>(snaps.Select(s => s.Info.RacingNumber));
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i].RacingNumber)) Rows.RemoveAt(i);
        }

        // Re-order in place: bubble each row into its position-sorted slot.
        // ObservableCollection.Move raises a single CollectionChanged event
        // per call and the ItemsControl handles it efficiently.
        var sorted = snaps.Select(s => s.Info.RacingNumber).ToList();
        for (int target = 0; target < sorted.Count; target++)
        {
            int current = -1;
            for (int j = target; j < Rows.Count; j++)
            {
                if (Rows[j].RacingNumber == sorted[target]) { current = j; break; }
            }
            if (current != -1 && current != target)
            {
                Rows.Move(current, target);
            }
        }
    }

    // ---------------------------------------------------------------
    // Internal records
    // ---------------------------------------------------------------

    private sealed record DriverInfo(
        string RacingNumber,
        string Tla,
        string LastName,
        string TeamName,
        string TeamColour);

    private sealed record SectorSnapshot(
        string Time,
        LapStatus Status,
        List<int> Segments);

    private sealed record RowSnapshot(
        DriverInfo Info,
        int Position,
        string LastLapTime,
        LapStatus LastLapStatus,
        string BestLapTime,
        LapStatus BestLapStatus,
        string GapToLeader,
        string IntervalToAhead,
        bool InPit,
        bool Retired,
        string TireCompoundLetter,
        string TireCompoundColor,
        int TireAge,
        int PitStopCount,
        SectorSnapshot[] Sectors);
}
