using System;
using System.IO;
using System.Reflection;
using F1SimHubLive.F1Replay;
using F1SimHubLive.F1Signalr;
using F1SimHubLive.MultiViewer;
using F1SimHubLive.Telemetry;
using GameReaderCommon;
using log4net;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;

namespace F1SimHubLive
{
    [PluginDescription("Live F1 telemetry (livetiming.formula1.com SignalR or F1 MultiViewer local API) -> SimHub properties for GSI wheel binding.")]
    [PluginAuthor("Victor de Souza")]
    [PluginName("F1SimHubLive")]
    public sealed class F1SimHubLivePlugin : IDataPlugin
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(F1SimHubLivePlugin));

        public PluginManager PluginManager { get; set; } = null!;

        private Settings _settings = Settings.Default;
        private readonly TelemetryBuffer _buffer = new();
        private ITelemetrySource? _client;
        private Interpolator? _interp;

        // Active replay engine when in on-demand replay mode (Source=F1Replay or
        // a picker "load session" command). Null while a live/MultiViewer source
        // is active. Held as the concrete type so the command channel can drive
        // its transport (play/pause/seek/speed) directly.
        private F1ReplayClient? _replay;
        private readonly object _clientSwapLock = new();

        // Picker -> plugin replay command channel (F1SimHubLive.ReplayCommand.json),
        // and the plugin -> picker status channel (F1SimHubLive.ReplayStatus.json).
        private FileSystemWatcher? _replayCmdWatcher;
        private System.Threading.Timer? _replayCmdDebounce;
        private readonly object _replayCmdLock = new();
        private long _lastReplaySeq = -1;
        private System.Threading.Timer? _replayStatusTimer;

        private double _topSpeedSeen;
        private int _topSpeedSessionKey;

        private FileSystemWatcher? _settingsWatcher;
        private System.Threading.Timer? _settingsReloadDebounce;
        private readonly object _settingsReloadLock = new();

        // F1 MultiViewer process detector. Every MvProcessPollMs we check whether
        // any MultiViewer/F1MV process is running and surface it as the
        // F1SimHubLive.MultiViewerRunning bool property. LED-profile TriggerFormulas
        // gate on this so our LEDs only fire while the user is in F1-viewing mode
        // (MultiViewer is up), and stay dark when the user is gaming or just idle.
        // v1.5.0: on every transition we also drive _ledRuntimeSwitcher to flip
        // each device's LED activeProfileId to ours (and restore on MV-down) so the
        // user no longer has to manually pick F1SimHubLive in SimHub > Devices > LEDs.
        private System.Threading.Timer? _mvProcessTimer;
        private bool _mvLastSeen;
        private const int MvProcessPollMs = 5000;
        private LedRuntimeSwitcher? _ledRuntimeSwitcher;

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            _settings = Settings.Load(SettingsPath(), Log);

            Register("CurrentDriverNumber", _settings.DriverNumber);
            Register("DriverTla", "");
            Register("DriverFirstName", "");
            Register("DriverLastName", "");
            Register("DriverFullName", "");
            Register("DriverBroadcastName", "");
            Register("TeamName", "");
            Register("TeamColour", "");
            Register("Source", _settings.Source);
            // v1.5.6: expose the running plugin version so the wheel LCD can
            // bind it and Vic can confirm at a glance which release the wheel
            // is actually loading. Format "1.5.6" — no Major.Minor.Build.Revision
            // tail, no leading 'v', so the dashboard can render `Ver. 1.5.6`
            // by concatenation in its Bindings formula.
            Register("Version", GetPluginVersionString());
            Register("Rpm", 0.0);
            Register("RpmPercent", 0.0);
            Register("RpmShiftPercent", 0.0);
            Register("Gear", 0);
            Register("Speed", 0.0);
            Register("Throttle", 0.0);
            Register("Brake", 0.0);
            Register("Drs", 0);
            Register("DrsActive", false);
            Register("DrsEligible", false);
            Register("Lap", 0);
            Register("Position", "");
            Register("BestLapTime", "");
            Register("LastLapTime", "");
            Register("GapToLeader", "");
            Register("IntervalToAhead", "");
            Register("IntervalToBehind", "");
            Register("InPit", false);
            Register("TyreCompound", "");
            Register("TyreCompoundShort", "");
            Register("TyreAge", 0);
            Register("CurrentLap", 0);
            Register("TotalLaps", 0);
            Register("LapDisplay", "");
            Register("TrackStatus", "");
            Register("TrackStatusCode", 0);
            Register("SessionTimeRemaining", "");
            Register("TotalDrivers", 0);
            Register("AirTemp", 0.0);
            Register("TrackTemp", 0.0);
            Register("Humidity", 0.0);
            Register("Rainfall", false);
            Register("WindSpeedKph", 0.0);
            Register("Sector1Time", "");
            Register("Sector2Time", "");
            Register("Sector3Time", "");
            Register("Sector1IsPersonalBest", false);
            Register("Sector2IsPersonalBest", false);
            Register("Sector3IsPersonalBest", false);
            Register("Sector1IsOverallBest", false);
            Register("Sector2IsOverallBest", false);
            Register("Sector3IsOverallBest", false);
            Register("AheadSector1Time", "");
            Register("AheadCarNumber", "");
            Register("LeaderCarNumber", "");
            Register("AheadLastLapTime", "");
            Register("AheadBestLapTime", "");
            Register("LeaderLastLapTime", "");
            Register("LeaderBestLapTime", "");
            Register("AheadInPit", false);
            Register("LeaderInPit", false);
            Register("AheadSector2Time", "");
            Register("AheadSector3Time", "");
            Register("AheadSector1IsPersonalBest", false);
            Register("AheadSector2IsPersonalBest", false);
            Register("AheadSector3IsPersonalBest", false);
            Register("AheadSector1IsOverallBest", false);
            Register("AheadSector2IsOverallBest", false);
            Register("AheadSector3IsOverallBest", false);
            Register("LeaderSector1Time", "");
            Register("LeaderSector2Time", "");
            Register("LeaderSector3Time", "");
            Register("LeaderSector1IsPersonalBest", false);
            Register("LeaderSector2IsPersonalBest", false);
            Register("LeaderSector3IsPersonalBest", false);
            Register("LeaderSector1IsOverallBest", false);
            Register("LeaderSector2IsOverallBest", false);
            Register("LeaderSector3IsOverallBest", false);
            Register("BehindCarNumber", "");
            Register("BehindTla", "");
            Register("BehindLastLapTime", "");
            Register("BehindBestLapTime", "");
            Register("BehindInPit", false);
            Register("BehindSector1Time", "");
            Register("BehindSector2Time", "");
            Register("BehindSector3Time", "");
            Register("BehindSector1IsPersonalBest", false);
            Register("BehindSector2IsPersonalBest", false);
            Register("BehindSector3IsPersonalBest", false);
            Register("BehindSector1IsOverallBest", false);
            Register("BehindSector2IsOverallBest", false);
            Register("BehindSector3IsOverallBest", false);
            Register("PitStopCount", 0);
            Register("TopSpeed", "");
            Register("TopSpeedRank", 0);
            Register("OvertakeSystemEnabled", false);
            Register("OvertakeAvailable", false);
            Register("FlagText", "");
            Register("Status", "Initializing");
            Register("MultiViewerRunning", false);

            // Replay-mode status, mirrored to the wheel dashboard and the picker.
            Register("ReplayActive", false);
            Register("ReplayPlaying", false);
            Register("ReplaySpeed", 1.0);
            Register("ReplayPositionSec", 0);
            Register("ReplayDurationSec", 0);
            Register("ReplaySessionName", "");

            // v1.5.0 runtime LED switcher: locates the SimHub install root from
            // where this plugin DLL is loaded, then on MV transitions snapshots
            // and flips each device's LED activeProfileId so the user gets
            // automatic switching instead of having to pick F1SimHubLive manually.
            try
            {
                var simhubInstallDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(simhubInstallDir))
                {
                    _ledRuntimeSwitcher = new LedRuntimeSwitcher(simhubInstallDir!, Log);

                    // v1.5.8 startup pass: re-assert F1SimHubLive as active on every
                    // supported device whose current activeProfileId is empty, orphan,
                    // or Default*. Fixes the Media-PC "stuck on Default after install"
                    // bug — SimHub doesn't hot-reload device settings.json, so any
                    // write here takes effect on the NEXT SimHub start. The user can
                    // still pick a third-party racing profile and we'll leave it alone.
                    try
                    {
                        _ledRuntimeSwitcher.EnsureActiveOnStartup();
                    }
                    catch (Exception ex)
                    {
                        Log($"LedRuntimeSwitcher startup pass failed: {ex.Message}");
                    }
                }
                else
                {
                    Log("LedRuntimeSwitcher: could not resolve SimHub install directory; runtime LED switching disabled.");
                }
            }
            catch (Exception ex)
            {
                Log($"LedRuntimeSwitcher: init failed ({ex.Message}); runtime LED switching disabled.");
            }

            // Kick the MultiViewer process check immediately so the property has a
            // truthful initial value before SimHub starts evaluating LED triggers,
            // then poll every MvProcessPollMs.
            UpdateMultiViewerRunning();
            _mvProcessTimer = new System.Threading.Timer(
                _ => { try { UpdateMultiViewerRunning(); } catch (Exception ex) { Log($"MV process poll error: {ex.Message}"); } },
                null, MvProcessPollMs, MvProcessPollMs);

            _interp = new Interpolator(_buffer, _settings.OutputHz, _settings.RenderDelayMs);
            _interp.Start();

            WireAndStart(CreateClient());

            StartSettingsWatcher();
            MaybeLaunchPicker();
            StartReplayCommandWatcher();
            StartReplayStatusPublisher();

            Log($"started, source={_settings.Source}, target driver #{_settings.DriverNumber}, output {_settings.OutputHz} Hz, render delay {_settings.RenderDelayMs} ms");
        }

        // ----- Settings hot-reload (driver picker support) ----------------------
        // Watches F1SimHubLive.Settings.json and live-swaps the watched driver
        // when DriverNumber changes. Other fields (Source / URLs / poll cadence)
        // are deliberately ignored mid-session: they're load-bearing on the
        // client lifecycle and should require a full restart. Driver number is
        // the one field that can flip safely on the fly.

        private void StartSettingsWatcher()
        {
            try
            {
                string path = SettingsPath();
                string dir = Path.GetDirectoryName(path) ?? ".";
                string file = Path.GetFileName(path);
                _settingsWatcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _settingsWatcher.Changed += (_, __) => ScheduleSettingsReload();
                _settingsWatcher.Created += (_, __) => ScheduleSettingsReload();
                _settingsWatcher.Renamed += (_, __) => ScheduleSettingsReload();
                Log($"watching settings file for live driver swaps: {path}");
            }
            catch (Exception ex)
            {
                Log($"settings watcher failed to start ({ex.Message}); driver swaps will require SimHub restart");
            }
        }

        private void ScheduleSettingsReload()
        {
            // FileSystemWatcher fires multiple events per save (LastWrite + Size +
            // sometimes a rename from a temp file). Debounce so we reload once.
            _settingsReloadDebounce?.Dispose();
            _settingsReloadDebounce = new System.Threading.Timer(
                _ => TryReloadSettings(), null, 250, System.Threading.Timeout.Infinite);
        }

        private void TryReloadSettings()
        {
            lock (_settingsReloadLock)
            {
                try
                {
                    var fresh = Settings.Load(SettingsPath(), Log);
                    if (!string.IsNullOrWhiteSpace(fresh.DriverNumber)
                        && fresh.DriverNumber != _settings.DriverNumber)
                    {
                        string previous = _settings.DriverNumber;
                        _settings.DriverNumber = fresh.DriverNumber;
                        _topSpeedSeen = 0.0;
                        _topSpeedSessionKey = 0;
                        SetProp("CurrentDriverNumber", fresh.DriverNumber);
                        // Clear stale identity props so the dash doesn't keep
                        // showing the old driver's TLA / team while the new one
                        // resolves on the next DriverList poll.
                        SetProp("DriverTla", "");
                        SetProp("DriverFirstName", "");
                        SetProp("DriverLastName", "");
                        SetProp("DriverFullName", "");
                        SetProp("DriverBroadcastName", "");
                        SetProp("TeamName", "");
                        SetProp("TeamColour", "");
                        SetProp("TopSpeed", "");
                        _client?.SetDriverNumber(fresh.DriverNumber);
                        Log($"live driver swap: {previous} -> {fresh.DriverNumber}");
                    }

                    // Broadcast-sync delay hot-reload: the picker's "Live video
                    // delay" slider writes this; apply it live so the user can
                    // dial the data into a delayed video feed without restarting.
                    if (fresh.BroadcastDelayMs != _settings.BroadcastDelayMs)
                    {
                        int previousDelay = _settings.BroadcastDelayMs;
                        _settings.BroadcastDelayMs = Math.Max(0, fresh.BroadcastDelayMs);
                        ApplyBroadcastDelay();
                        Log($"broadcast-sync delay change: {previousDelay} -> {_settings.BroadcastDelayMs} ms");
                    }
                }
                catch (Exception ex)
                {
                    Log($"settings reload failed: {ex.Message}");
                }
            }
        }

        // ----- Picker auto-launch (opt-in) --------------------------------------
        // Spawns F1SimHubLive-Picker.exe if AutoLaunchPicker is true and the exe
        // sits next to the plugin DLL. Best-effort — failures are logged but
        // never block SimHub startup. The Start Menu shortcut created by the
        // installer is the recommended manual-launch path; this is just for
        // users who always run SimHub elevated and want the picker every time.
        private void MaybeLaunchPicker()
        {
            if (!_settings.AutoLaunchPicker) return;
            try
            {
                string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                string exe = Path.Combine(dllDir, "F1SimHubLive-Picker.exe");
                if (!File.Exists(exe))
                {
                    Log($"AutoLaunchPicker is on but {exe} does not exist; skipping. " +
                        "Re-run the installer to deploy the picker.");
                    return;
                }
                // Don't double-launch if the user already has the picker open
                // (e.g. SimHub was restarted while picker stayed alive).
                if (System.Diagnostics.Process.GetProcessesByName("F1SimHubLive-Picker").Length > 0)
                {
                    Log("picker already running; not spawning a duplicate");
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = dllDir,
                    UseShellExecute = true, // ShellExecute path; picker manifest is asInvoker (v1.3.0+) so no UAC fires either way
                });
                Log($"launched driver picker: {exe}");
            }
            catch (Exception ex)
            {
                Log($"picker auto-launch failed: {ex.Message}");
            }
        }

        private ITelemetrySource CreateClient()
        {
            string src = (_settings.Source ?? "F1Live").Trim();
            if (string.Equals(src, "MultiViewer", StringComparison.OrdinalIgnoreCase))
            {
                Log($"using MultiViewer source: {_settings.MultiViewerBaseUrl}, poll {_settings.MultiViewerPollMs} ms");
                return new MultiViewerHttpClient(
                    _settings.DriverNumber,
                    _settings.MultiViewerBaseUrl,
                    _settings.MultiViewerPollMs,
                    _settings.MultiViewerTimingPollMs,
                    Log);
            }
            if (string.Equals(src, "F1Replay", StringComparison.OrdinalIgnoreCase))
            {
                Log($"using F1 Replay source: {_settings.ReplaySessionPath}");
                return new F1ReplayClient(_settings.DriverNumber, _settings.ReplaySessionPath, Log);
            }
            Log("using F1 Live SignalR source");
            return new F1SignalRClient(_settings.DriverNumber, Log);
        }

        // ----- Telemetry-source wiring + runtime swap ---------------------------
        // The source can change at runtime: the picker can load a replay session
        // (live/MV -> F1Replay) or return to live. WireAndStart binds the common
        // ITelemetrySource events to SimHub properties; SwapClient tears down the
        // old source and brings up a new one without restarting SimHub.

        private void WireAndStart(ITelemetrySource client)
        {
            _client = client;
            _replay = client as F1ReplayClient;
            SetProp("ReplayActive", _replay != null);

            client.OnSnapshot += s => _buffer.Push(s);
            client.OnTimingSnapshot += t =>
            {
                SetProp("Lap", t.Lap);
                SetProp("Position", t.Position);
                SetProp("BestLapTime", t.BestLapTime);
                SetProp("LastLapTime", t.LastLapTime);
                SetProp("GapToLeader", t.GapToLeader);
                SetProp("IntervalToAhead", t.IntervalToAhead);
                SetProp("IntervalToBehind", t.IntervalToBehind);
                SetProp("InPit", t.InPit);
                SetProp("TyreCompound", t.TyreCompound ?? "");
                SetProp("TyreCompoundShort", ShortCompound(t.TyreCompound));
                SetProp("TyreAge", t.TyreAge);
                SetProp("Sector1Time", t.Sector1Time);
                SetProp("Sector2Time", t.Sector2Time);
                SetProp("Sector3Time", t.Sector3Time);
                SetProp("Sector1IsPersonalBest", t.Sector1IsPersonalBest);
                SetProp("Sector2IsPersonalBest", t.Sector2IsPersonalBest);
                SetProp("Sector3IsPersonalBest", t.Sector3IsPersonalBest);
                SetProp("Sector1IsOverallBest", t.Sector1IsOverallBest);
                SetProp("Sector2IsOverallBest", t.Sector2IsOverallBest);
                SetProp("Sector3IsOverallBest", t.Sector3IsOverallBest);
                SetProp("AheadSector1Time", t.AheadSector1Time);
                SetProp("AheadCarNumber", t.AheadCarNumber);
                SetProp("LeaderCarNumber", t.LeaderCarNumber);
                SetProp("AheadLastLapTime", t.AheadLastLapTime);
                SetProp("AheadBestLapTime", t.AheadBestLapTime);
                SetProp("LeaderLastLapTime", t.LeaderLastLapTime);
                SetProp("LeaderBestLapTime", t.LeaderBestLapTime);
                SetProp("AheadInPit", t.AheadInPit);
                SetProp("LeaderInPit", t.LeaderInPit);
                SetProp("AheadSector2Time", t.AheadSector2Time);
                SetProp("AheadSector3Time", t.AheadSector3Time);
                SetProp("AheadSector1IsPersonalBest", t.AheadSector1IsPersonalBest);
                SetProp("AheadSector2IsPersonalBest", t.AheadSector2IsPersonalBest);
                SetProp("AheadSector3IsPersonalBest", t.AheadSector3IsPersonalBest);
                SetProp("AheadSector1IsOverallBest", t.AheadSector1IsOverallBest);
                SetProp("AheadSector2IsOverallBest", t.AheadSector2IsOverallBest);
                SetProp("AheadSector3IsOverallBest", t.AheadSector3IsOverallBest);
                SetProp("LeaderSector1Time", t.LeaderSector1Time);
                SetProp("LeaderSector2Time", t.LeaderSector2Time);
                SetProp("LeaderSector3Time", t.LeaderSector3Time);
                SetProp("LeaderSector1IsPersonalBest", t.LeaderSector1IsPersonalBest);
                SetProp("LeaderSector2IsPersonalBest", t.LeaderSector2IsPersonalBest);
                SetProp("LeaderSector3IsPersonalBest", t.LeaderSector3IsPersonalBest);
                SetProp("LeaderSector1IsOverallBest", t.LeaderSector1IsOverallBest);
                SetProp("LeaderSector2IsOverallBest", t.LeaderSector2IsOverallBest);
                SetProp("LeaderSector3IsOverallBest", t.LeaderSector3IsOverallBest);
                SetProp("BehindCarNumber", t.BehindCarNumber);
                SetProp("BehindTla", t.BehindTla);
                SetProp("BehindLastLapTime", t.BehindLastLapTime);
                SetProp("BehindBestLapTime", t.BehindBestLapTime);
                SetProp("BehindInPit", t.BehindInPit);
                SetProp("BehindSector1Time", t.BehindSector1Time);
                SetProp("BehindSector2Time", t.BehindSector2Time);
                SetProp("BehindSector3Time", t.BehindSector3Time);
                SetProp("BehindSector1IsPersonalBest", t.BehindSector1IsPersonalBest);
                SetProp("BehindSector2IsPersonalBest", t.BehindSector2IsPersonalBest);
                SetProp("BehindSector3IsPersonalBest", t.BehindSector3IsPersonalBest);
                SetProp("BehindSector1IsOverallBest", t.BehindSector1IsOverallBest);
                SetProp("BehindSector2IsOverallBest", t.BehindSector2IsOverallBest);
                SetProp("BehindSector3IsOverallBest", t.BehindSector3IsOverallBest);
                SetProp("PitStopCount", t.PitStopCount);
                UpdateTopSpeedFromTimingStats(t.TopSpeed);
                SetProp("TopSpeedRank", t.TopSpeedRank);
                SetProp("OvertakeSystemEnabled", t.OvertakeSystemEnabled);
                SetProp("OvertakeAvailable", t.OvertakeAvailable);
                SetProp("FlagText", t.FlagText);
            };
            client.OnSessionSnapshot += sess =>
            {
                int key = (sess.TotalLaps << 16) ^ (sess.CurrentLap > 0 ? 1 : 0);
                if (key != _topSpeedSessionKey)
                {
                    _topSpeedSessionKey = key;
                    _topSpeedSeen = 0.0;
                }
                SetProp("CurrentLap", sess.CurrentLap);
                SetProp("TotalLaps", sess.TotalLaps);
                SetProp("LapDisplay", FormatLapDisplay(sess.CurrentLap, sess.TotalLaps));
                SetProp("TrackStatus", sess.TrackStatusMessage ?? "");
                SetProp("TrackStatusCode", sess.TrackStatusCode);
                SetProp("SessionTimeRemaining", sess.SessionTimeRemaining ?? "");
                SetProp("TotalDrivers", sess.TotalDrivers);
            };
            client.OnWeatherSnapshot += w =>
            {
                SetProp("AirTemp", w.AirTemp);
                SetProp("TrackTemp", w.TrackTemp);
                SetProp("Humidity", w.Humidity);
                SetProp("Rainfall", w.Rainfall);
                SetProp("WindSpeedKph", w.WindSpeedKph);
            };
            client.OnStatus += s => SetProp("Status", s);
            client.OnDriverInfoSnapshot += info =>
            {
                SetProp("DriverTla", info.Tla ?? "");
                SetProp("DriverFirstName", info.FirstName ?? "");
                SetProp("DriverLastName", info.LastName ?? "");
                SetProp("DriverFullName", info.FullName ?? "");
                SetProp("DriverBroadcastName", info.BroadcastName ?? "");
                SetProp("TeamName", info.TeamName ?? "");
                SetProp("TeamColour", info.TeamColour ?? "");
            };
            _ = client.StartAsync().ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    Log("unhandled in StartAsync: " + t.Exception.GetBaseException().Message);
                }
            }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

            // Re-evaluate the broadcast-sync delay against the new source: live
            // sources honour the configured delay, the replay source forces 0
            // (replay is anchored to the video manually).
            ApplyBroadcastDelay();
        }

        /// <summary>
        /// Pushes the configured broadcast-sync delay onto the interpolator and
        /// sizes the buffer's history window to match. The delay is suppressed
        /// (0) while the replay source is active. Safe to call repeatedly — it's
        /// invoked on every source swap and on settings hot-reload.
        /// </summary>
        private void ApplyBroadcastDelay()
        {
            var interp = _interp;
            if (interp == null) return;
            int delay = _replay != null ? 0 : Math.Max(0, _settings.BroadcastDelayMs);
            interp.BroadcastDelayMs = delay;
            _buffer.RetentionMs = delay > 0
                ? delay + _settings.RenderDelayMs + 1500
                : 1500;
            if (delay > 0)
                Log($"broadcast-sync delay active: holding live data {delay} ms to match a delayed video feed");
        }

        private void SwapClient(ITelemetrySource newClient)
        {
            lock (_clientSwapLock)
            {
                var old = _client;
                _client = null;
                _replay = null;
                try { old?.Dispose(); } catch (Exception ex) { Log($"old client dispose failed: {ex.Message}"); }
                WireAndStart(newClient);
            }
        }

        // ----- Replay command channel (picker -> plugin) ------------------------
        // The picker writes F1SimHubLive.ReplayCommand.json with a monotonically
        // increasing Seq; we act only on commands newer than the last seen Seq.

        private void StartReplayCommandWatcher()
        {
            try
            {
                string path = ReplayCommandPath();
                string dir = Path.GetDirectoryName(path) ?? ".";
                Directory.CreateDirectory(dir);
                string file = Path.GetFileName(path);
                _replayCmdWatcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _replayCmdWatcher.Changed += (_, __) => ScheduleReplayCmdRead();
                _replayCmdWatcher.Created += (_, __) => ScheduleReplayCmdRead();
                _replayCmdWatcher.Renamed += (_, __) => ScheduleReplayCmdRead();
                // Pick up any command already on disk from a prior picker session.
                ScheduleReplayCmdRead();
                Log($"watching replay command channel: {path}");
            }
            catch (Exception ex)
            {
                Log($"replay command watcher failed to start: {ex.Message}");
            }
        }

        private void ScheduleReplayCmdRead()
        {
            _replayCmdDebounce?.Dispose();
            _replayCmdDebounce = new System.Threading.Timer(
                _ => TryReadReplayCommand(), null, 120, System.Threading.Timeout.Infinite);
        }

        private void TryReadReplayCommand()
        {
            lock (_replayCmdLock)
            {
                try
                {
                    string path = ReplayCommandPath();
                    if (!File.Exists(path)) return;
                    var obj = JObject.Parse(File.ReadAllText(path));
                    long seq = obj.Value<long?>("Seq") ?? 0;
                    if (seq <= _lastReplaySeq) return;
                    _lastReplaySeq = seq;
                    string command = (obj.Value<string>("Command") ?? "").Trim().ToLowerInvariant();
                    DispatchReplayCommand(command, obj);
                }
                catch (Exception ex)
                {
                    Log($"replay command read failed: {ex.Message}");
                }
            }
        }

        private void DispatchReplayCommand(string command, JObject obj)
        {
            switch (command)
            {
                case "load":
                    string sessionPath = obj.Value<string>("SessionPath") ?? "";
                    string sessionName = obj.Value<string>("SessionName") ?? sessionPath;
                    if (string.IsNullOrWhiteSpace(sessionPath)) { Log("replay load: empty SessionPath"); return; }
                    EnterReplay(sessionPath, sessionName);
                    break;
                case "play":   _replay?.Play(); break;
                case "pause":  _replay?.Pause(); break;
                case "toggle": _replay?.TogglePlay(); break;
                case "speed":  if (_replay != null) _replay.SetSpeed(obj.Value<double?>("Speed") ?? 1.0); break;
                case "seek":   _replay?.Seek(TimeSpan.FromSeconds(obj.Value<double?>("SeekSeconds") ?? 0)); break;
                case "seeklap": _replay?.SeekToLap(obj.Value<int?>("SeekLap") ?? 1); break;
                case "seekclock": _replay?.SeekToRemaining(TimeSpan.FromSeconds(obj.Value<double?>("RemainingSec") ?? 0)); break;
                case "stop":
                case "golive":
                    ExitReplayToLive();
                    break;
                default:
                    Log($"replay command ignored (unknown): '{command}'");
                    break;
            }
        }

        private void EnterReplay(string sessionPath, string sessionName)
        {
            Log($"entering replay: {sessionName} [{sessionPath}]");
            _settings.Source = "F1Replay";
            _settings.ReplaySessionPath = sessionPath;
            SetProp("Source", "F1Replay");
            SetProp("ReplaySessionName", sessionName);
            SwapClient(new F1ReplayClient(_settings.DriverNumber, sessionPath, Log));
        }

        private void ExitReplayToLive()
        {
            string back = string.Equals(_settings.Source, "F1Replay", StringComparison.OrdinalIgnoreCase)
                ? "F1Live" : _settings.Source;
            Log($"exiting replay -> {back}");
            _settings.Source = back;
            SetProp("Source", back);
            SetProp("ReplaySessionName", "");
            SwapClient(back == "MultiViewer"
                ? new MultiViewerHttpClient(_settings.DriverNumber, _settings.MultiViewerBaseUrl,
                    _settings.MultiViewerPollMs, _settings.MultiViewerTimingPollMs, Log)
                : (ITelemetrySource)new F1SignalRClient(_settings.DriverNumber, Log));
        }

        // ----- Replay status channel (plugin -> picker) -------------------------
        // Publishes transport state ~3 Hz so the picker can render a live
        // scrubber + play state, and mirrors it to SimHub props for the wheel.

        private void StartReplayStatusPublisher()
        {
            _replayStatusTimer = new System.Threading.Timer(
                _ => { try { PublishReplayStatus(); } catch (Exception ex) { Log($"replay status publish error: {ex.Message}"); } },
                null, 500, 333);
        }

        private void PublishReplayStatus()
        {
            var r = _replay;
            if (r == null)
            {
                SetProp("ReplayActive", false);
                return;
            }
            int posSec = (int)r.Position.TotalSeconds;
            int durSec = (int)r.Duration.TotalSeconds;
            SetProp("ReplayActive", true);
            SetProp("ReplayPlaying", r.IsPlaying);
            SetProp("ReplaySpeed", r.Speed);
            SetProp("ReplayPositionSec", posSec);
            SetProp("ReplayDurationSec", durSec);

            var status = new JObject
            {
                ["Loaded"] = r.IsLoaded,
                ["Playing"] = r.IsPlaying,
                ["Speed"] = r.Speed,
                ["PositionSec"] = posSec,
                ["DurationSec"] = durSec,
                ["CurrentLap"] = r.CurrentLap,
                ["TotalLaps"] = r.TotalLaps,
                ["HasClock"] = r.HasSessionClock,
                ["RemainingSec"] = r.SessionRemaining.HasValue ? (int)r.SessionRemaining.Value.TotalSeconds : -1,
            };
            try
            {
                string path = ReplayStatusPath();
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, status.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { /* best effort — picker tolerates a stale/missing status file */ }

            PublishReplayGrid(r);
        }

        // All-driver grid snapshot (identity + live car telemetry) so the picker
        // can show the whole field and switch drivers while in replay — no
        // MultiViewer required. Phase 1: no timing columns (the replay topic set
        // carries CarData + DriverList only).
        private void PublishReplayGrid(F1ReplayClient r)
        {
            try
            {
                if (!r.IsLoaded) return;
                var arr = new JArray();
                foreach (var row in r.GetGrid())
                {
                    arr.Add(new JObject
                    {
                        ["Num"] = row.Num,
                        ["Tla"] = row.Tla,
                        ["LastName"] = row.LastName,
                        ["TeamName"] = row.TeamName,
                        ["TeamColour"] = row.TeamColour,
                        ["Rpm"] = row.Rpm,
                        ["Speed"] = row.Speed,
                        ["Gear"] = row.Gear,
                        ["Throttle"] = row.Throttle,
                    });
                }
                var doc = new JObject { ["Drivers"] = arr };

                string path = ReplayGridPath();
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, doc.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { /* best effort — picker tolerates a stale/missing grid file */ }
        }

        private static string ReplayCommandPath() =>
            Path.Combine(Path.GetDirectoryName(SettingsPath()) ?? ".", "F1SimHubLive.ReplayCommand.json");

        private static string ReplayStatusPath() =>
            Path.Combine(Path.GetDirectoryName(SettingsPath()) ?? ".", "F1SimHubLive.ReplayStatus.json");

        private static string ReplayGridPath() =>
            Path.Combine(Path.GetDirectoryName(SettingsPath()) ?? ".", "F1SimHubLive.ReplayGrid.json");


        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            var s = _interp?.Latest;
            if (s == null) return;
            SetProp("Rpm", s.Rpm);
            SetProp("RpmPercent", ClampPercent(s.Rpm / RpmCeiling * 100.0));
            SetProp("RpmShiftPercent", CalcShiftPercent(s.Rpm));
            SetProp("Gear", s.Gear);
            SetProp("Speed", s.Speed);
            UpdateTopSpeedFromLive(s.Speed);
            SetProp("Throttle", s.Throttle);
            SetProp("Brake", s.Brake);
            SetProp("Drs", s.Drs);
            SetProp("DrsActive", s.DrsActive);
            SetProp("DrsEligible", s.DrsEligible);
        }

        // F1 V6 turbo hybrid PU ceiling = 15,000 RPM (regulation). Race peaks ~12,500.
        // We normalize over 13,000 to give LEDs a meaningful spread without ever overflowing.
        private const double RpmCeiling = 13000.0;

        private static double ClampPercent(double v) => v < 0 ? 0 : (v > 100 ? 100 : v);

        /// <summary>
        /// Rescales raw RPM into a "shift-light" 0..100 curve bounded by the
        /// user-configurable <c>RpmShiftLightStartRpm</c> / <c>RpmShiftLightEndRpm</c>
        /// settings. Lets wheel LED configs mirror real F1 LED bars (greens visible
        /// during out-laps, full bar at fast-corner peaks) without the hard cap that
        /// <c>RpmPercent</c> imposes via the fixed 13,000 ceiling.
        /// </summary>
        private double CalcShiftPercent(double rpm)
        {
            double start = _settings.RpmShiftLightStartRpm;
            double end = _settings.RpmShiftLightEndRpm;
            if (end <= start) return ClampPercent(rpm / RpmCeiling * 100.0); // misconfig — fall back
            return ClampPercent((rpm - start) / (end - start) * 100.0);
        }

        // Update the running session top speed from live telemetry (instantaneous Speed in km/h).
        // F1 broadcast "TOP" reflects the highest speed seen by any speed trap; our live feed
        // hits genuine peaks (e.g. DRS on the longest straight) that MultiViewer's ST trap can
        // miss when the trap is positioned away from the absolute fastest point on the track.
        // We take the max of (live peak ever seen, BestSpeeds.ST from TimingStats) so we never
        // regress visually.
        private void UpdateTopSpeedFromLive(double speedKmh)
        {
            if (speedKmh > _topSpeedSeen && speedKmh < 450.0)
            {
                _topSpeedSeen = speedKmh;
                SetProp("TopSpeed", ((int)Math.Round(_topSpeedSeen)).ToString());
            }
        }

        private void UpdateTopSpeedFromTimingStats(string stValue)
        {
            if (string.IsNullOrWhiteSpace(stValue)) return;
            if (!int.TryParse(stValue, out var st)) return;
            if (st > _topSpeedSeen)
            {
                _topSpeedSeen = st;
                SetProp("TopSpeed", st.ToString());
            }
        }

        public void End(PluginManager pluginManager)
        {
            _settingsWatcher?.Dispose();
            _settingsReloadDebounce?.Dispose();
            _mvProcessTimer?.Dispose();
            _replayCmdWatcher?.Dispose();
            _replayCmdDebounce?.Dispose();
            _replayStatusTimer?.Dispose();
            _interp?.Dispose();
            _client?.Dispose();
        }

        /// <summary>
        /// Polls the live Windows process table for a running F1 MultiViewer instance
        /// (process names <c>MultiViewer</c> or <c>F1MV</c> — Electron app spawns
        /// multiple sub-processes, ANY of them present counts). Surfaces the result
        /// as the <c>F1SimHubLive.MultiViewerRunning</c> SimHub property which LED
        /// profile TriggerFormulas can gate on, e.g.
        /// <c>if([F1SimHubLive.MultiViewerRunning] = 1, 1, 0)</c>.
        /// Logs the first transition each direction so the SimHub log shows when
        /// MV comes up or goes away. Idempotent on the SetProp call (we still write
        /// every cycle so a fresh-init SimHub picks up the value regardless of
        /// dirty-checking).
        /// </summary>
        private void UpdateMultiViewerRunning()
        {
            bool running = false;
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName("MultiViewer").Length > 0
                    || System.Diagnostics.Process.GetProcessesByName("F1MV").Length > 0)
                {
                    running = true;
                }
            }
            catch (Exception ex)
            {
                Log($"MV process enumeration failed (treating as not running): {ex.Message}");
                running = false;
            }

            if (running != _mvLastSeen)
            {
                Log(running ? "MultiViewer detected (LEDs will activate)" : "MultiViewer no longer running (LEDs will deactivate)");
                _mvLastSeen = running;

                // v1.5.0: flip device-side LED activeProfileId on every transition
                // so the user doesn't need to manually pick F1SimHubLive in SimHub.
                // Done OFF the SimHub thread so file I/O can't stall the poll timer.
                if (_ledRuntimeSwitcher != null)
                {
                    var switcher = _ledRuntimeSwitcher;
                    var direction = running;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { switcher.OnMultiViewerRunningChanged(direction); }
                        catch (Exception ex) { Log($"LedRuntimeSwitcher transition failed: {ex.Message}"); }
                    });
                }
            }
            SetProp("MultiViewerRunning", running);
        }

        private void Register(string name, object initial) =>
            PluginManager.AddProperty(name, GetType(), initial);

        private void SetProp(string name, object value) =>
            PluginManager.SetPropertyValue(name, GetType(), value);

        private static void Log(string s) => _log.Info("[F1SimHubLive] " + s);

        /// <summary>
        /// Returns the plugin's assembly InformationalVersion (e.g. "1.5.6"),
        /// falling back to the AssemblyVersion if InformationalVersion isn't
        /// stamped. Trims any "+commitsha" SourceLink suffix the SDK appends
        /// in CI builds. Used by the LCD dashboard so the wheel shows which
        /// release is actually loaded.
        /// </summary>
        private static string GetPluginVersionString()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(info))
                {
                    int plus = info!.IndexOf('+');
                    return plus > 0 ? info.Substring(0, plus) : info;
                }
                var ver = asm.GetName().Version;
                return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static string ShortCompound(string? c)
        {
            if (string.IsNullOrEmpty(c)) return "";
            switch (c!.ToUpperInvariant())
            {
                case "SOFT": return "S";
                case "MEDIUM": return "M";
                case "HARD": return "H";
                case "INTERMEDIATE": return "I";
                case "WET": return "W";
                default: return c.Substring(0, 1).ToUpperInvariant();
            }
        }

        private static string FormatLapDisplay(int current, int total)
        {
            if (current <= 0 && total <= 0) return "";
            if (total <= 0) return current.ToString();
            return current + "/" + total;
        }

        private static string SettingsPath()
        {
            // v1.3.0+: per-user APPDATA. Resolver handles one-shot migration
            // from the legacy in-plugin-folder location used in v1.2.x and
            // earlier. See SettingsPathResolver.cs.
            return SettingsPathResolver.Resolve(Log);
        }
    }
}
