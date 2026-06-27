using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Picker → plugin replay transport channel. Writes
/// <c>F1SimHubLive.ReplayCommand.json</c> (atomic tmp+rename, monotonic
/// <c>Seq</c>) next to the shared settings file, and reads the plugin's
/// <c>F1SimHubLive.ReplayStatus.json</c> back for the live scrubber/state.
///
/// <para>The command schema mirrors <c>F1SimHubLivePlugin.DispatchReplayCommand</c>
/// exactly — commands: <c>load play pause toggle speed seek seeklap stop golive</c>.
/// A "nudge" is just an absolute <c>seek</c> to <c>PositionSec ± delta</c>, so the
/// plugin needs no relative-seek command.</para>
///
/// <para><c>Seq</c> seeds from Unix-ms so it always exceeds whatever the plugin
/// last saw, even across picker restarts (the plugin ignores any
/// <c>Seq &lt;= lastSeen</c>). Per-session sync offset / last position is persisted
/// to <c>F1SimHubLive.ReplayPrefs.json</c> so the user doesn't re-sync on reload.</para>
/// </summary>
public sealed class ReplayControlClient
{
    private readonly string _commandPath;
    private readonly string _statusPath;
    private readonly string _prefsPath;

    private long _seq;

    public ReplayControlClient(string settingsPath)
    {
        string dir = Path.GetDirectoryName(settingsPath) ?? ".";
        _commandPath = Path.Combine(dir, "F1SimHubLive.ReplayCommand.json");
        _statusPath = Path.Combine(dir, "F1SimHubLive.ReplayStatus.json");
        _gridPath = Path.Combine(dir, "F1SimHubLive.ReplayGrid.json");
        _prefsPath = Path.Combine(dir, "F1SimHubLive.ReplayPrefs.json");
        _seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // ----- commands ---------------------------------------------------------

    public void Load(string sessionPath, string sessionName) =>
        Write("load", o => { o["SessionPath"] = sessionPath; o["SessionName"] = sessionName; });

    public void Play() => Write("play");
    public void Pause() => Write("pause");
    public void TogglePlay() => Write("toggle");
    public void GoLive() => Write("golive");

    public void SetSpeed(double speed) => Write("speed", o => o["Speed"] = speed);

    /// <summary>Absolute seek (also used for ±nudge — caller passes new absolute seconds).</summary>
    public void Seek(double seconds) => Write("seek", o => o["SeekSeconds"] = Math.Max(0, seconds));

    public void SeekToLap(int lap) => Write("seeklap", o => o["SeekLap"] = Math.Max(1, lap));

    /// <summary>Anchor the data to the on-screen session clock (time remaining).</summary>
    public void SeekToClock(TimeSpan remaining) =>
        Write("seekclock", o => o["RemainingSec"] = Math.Max(0, remaining.TotalSeconds));

    private void Write(string command, Action<JObject>? extra = null)
    {
        var o = new JObject
        {
            ["Seq"] = NextSeq(),
            ["Command"] = command,
        };
        extra?.Invoke(o);

        string tmp = _commandPath + ".picker.tmp";
        File.WriteAllText(tmp, o.ToString(Formatting.Indented));
        File.Move(tmp, _commandPath, overwrite: true);
    }

    private long NextSeq()
    {
        // Monotonic and never below wall-clock ms, so a fast burst of commands
        // and any post-restart command both still increase past the plugin's
        // last-seen value.
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long next = Math.Max(Interlocked.Increment(ref _seq), now);
        Interlocked.Exchange(ref _seq, next);
        return next;
    }

    // ----- status -----------------------------------------------------------

    /// <summary>Reads the plugin's status file. Returns null when absent/stale/unparseable.</summary>
    /// <summary>
    /// Max age of the plugin's status file before we treat replay as inactive.
    /// The plugin rewrites it ~3 Hz while a replay is loaded, so anything older
    /// than this means the writing instance is gone (SimHub restarted / switched
    /// to live / MultiViewer). Prevents the picker locking onto a frozen, stale
    /// grid from a previous session.
    /// </summary>
    private static readonly TimeSpan StatusStaleAfter = TimeSpan.FromSeconds(5);

    public ReplayStatus? ReadStatus()
    {
        try
        {
            if (!File.Exists(_statusPath)) return null;
            // A status file older than a few seconds is a leftover from a dead
            // plugin instance — ignore it so the picker leaves replay-grid mode.
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(_statusPath) > StatusStaleAfter)
                return null;
            var o = JObject.Parse(File.ReadAllText(_statusPath));
            return new ReplayStatus
            {
                Loaded = o.Value<bool?>("Loaded") ?? false,
                Playing = o.Value<bool?>("Playing") ?? false,
                Speed = o.Value<double?>("Speed") ?? 1.0,
                PositionSec = o.Value<int?>("PositionSec") ?? 0,
                DurationSec = o.Value<int?>("DurationSec") ?? 0,
                CurrentLap = o.Value<int?>("CurrentLap") ?? 0,
                TotalLaps = o.Value<int?>("TotalLaps") ?? 0,
                HasClock = o.Value<bool?>("HasClock") ?? false,
                RemainingSec = o.Value<int?>("RemainingSec") ?? -1,
            };
        }
        catch
        {
            return null;
        }
    }

    // ----- replay grid (all drivers) ---------------------------------------

    private readonly string _gridPath;

    /// <summary>
    /// Reads the plugin's all-driver replay grid snapshot. Returns an empty list
    /// when the file is absent/unparseable (e.g. not in replay yet).
    /// </summary>
    public IReadOnlyList<ReplayGridDriver> ReadGrid()
    {
        try
        {
            if (!File.Exists(_gridPath)) return Array.Empty<ReplayGridDriver>();
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(_gridPath) > StatusStaleAfter)
                return Array.Empty<ReplayGridDriver>();
            var o = JObject.Parse(File.ReadAllText(_gridPath));
            if (o["Drivers"] is not JArray arr) return Array.Empty<ReplayGridDriver>();
            var list = new List<ReplayGridDriver>(arr.Count);
            foreach (var d in arr)
            {
                list.Add(new ReplayGridDriver
                {
                    Num = d.Value<string>("Num") ?? "",
                    Tla = d.Value<string>("Tla") ?? "",
                    LastName = d.Value<string>("LastName") ?? "",
                    TeamName = d.Value<string>("TeamName") ?? "",
                    TeamColour = d.Value<string>("TeamColour") ?? "",
                    Rpm = d.Value<int?>("Rpm") ?? 0,
                    Speed = d.Value<int?>("Speed") ?? 0,
                    Gear = d.Value<int?>("Gear") ?? 0,
                    Throttle = d.Value<int?>("Throttle") ?? 0,
                });
            }
            return list;
        }
        catch
        {
            return Array.Empty<ReplayGridDriver>();
        }
    }

    // ----- per-session sync prefs ------------------------------------------

    public ReplaySessionPref GetPref(string sessionPath)
    {
        try
        {
            if (File.Exists(_prefsPath))
            {
                var map = JsonConvert.DeserializeObject<Dictionary<string, ReplaySessionPref>>(
                    File.ReadAllText(_prefsPath));
                if (map != null && map.TryGetValue(sessionPath, out var p) && p != null) return p;
            }
        }
        catch { /* fall through to default */ }
        return new ReplaySessionPref();
    }

    public void SavePref(string sessionPath, int lastPositionSec, double speed)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) return;
        try
        {
            Dictionary<string, ReplaySessionPref> map;
            if (File.Exists(_prefsPath))
            {
                map = JsonConvert.DeserializeObject<Dictionary<string, ReplaySessionPref>>(
                          File.ReadAllText(_prefsPath))
                      ?? new Dictionary<string, ReplaySessionPref>();
            }
            else
            {
                map = new Dictionary<string, ReplaySessionPref>();
            }

            map[sessionPath] = new ReplaySessionPref { LastPositionSec = lastPositionSec, Speed = speed };

            string tmp = _prefsPath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(map, Formatting.Indented));
            File.Move(tmp, _prefsPath, overwrite: true);
        }
        catch { /* best effort */ }
    }
}

public sealed class ReplayStatus
{
    public bool Loaded { get; set; }
    public bool Playing { get; set; }
    public double Speed { get; set; }
    public int PositionSec { get; set; }
    public int DurationSec { get; set; }
    public int CurrentLap { get; set; }
    public int TotalLaps { get; set; }
    public bool HasClock { get; set; }
    /// <summary>Official session clock (time remaining) in seconds; -1 when none.</summary>
    public int RemainingSec { get; set; } = -1;
}

public sealed class ReplaySessionPref
{
    /// <summary>Last data position the user left this session at, so reload resumes aligned.</summary>
    public int LastPositionSec { get; set; }
    public double Speed { get; set; } = 1.0;
}

/// <summary>
/// One driver in the plugin's replay grid snapshot: identity + live car
/// telemetry. No timing fields (Phase 1 — replay carries CarData + DriverList).
/// </summary>
public sealed class ReplayGridDriver
{
    public string Num { get; set; } = "";
    public string Tla { get; set; } = "";
    public string LastName { get; set; } = "";
    public string TeamName { get; set; } = "";
    public string TeamColour { get; set; } = "";
    public int Rpm { get; set; }
    public int Speed { get; set; }
    public int Gear { get; set; }
    public int Throttle { get; set; }
}
