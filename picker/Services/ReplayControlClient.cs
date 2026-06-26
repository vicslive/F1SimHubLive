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
    public ReplayStatus? ReadStatus()
    {
        try
        {
            if (!File.Exists(_statusPath)) return null;
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
            };
        }
        catch
        {
            return null;
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
}

public sealed class ReplaySessionPref
{
    /// <summary>Last data position the user left this session at, so reload resumes aligned.</summary>
    public int LastPositionSec { get; set; }
    public double Speed { get; set; } = 1.0;
}
