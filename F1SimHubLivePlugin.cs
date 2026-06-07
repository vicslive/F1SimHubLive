using System;
using System.IO;
using System.Reflection;
using F1SimHubLive.F1Signalr;
using F1SimHubLive.MultiViewer;
using F1SimHubLive.Telemetry;
using GameReaderCommon;
using log4net;
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
            Register("PitStopCount", 0);
            Register("TopSpeed", "");
            Register("TopSpeedRank", 0);
            Register("OvertakeSystemEnabled", false);
            Register("OvertakeAvailable", false);
            Register("FlagText", "");
            Register("Status", "Initializing");
            Register("MultiViewerRunning", false);

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

            _client = CreateClient();
            _client.OnSnapshot += s => _buffer.Push(s);
            _client.OnTimingSnapshot += t =>
            {
                SetProp("Lap", t.Lap);
                SetProp("Position", t.Position);
                SetProp("BestLapTime", t.BestLapTime);
                SetProp("LastLapTime", t.LastLapTime);
                SetProp("GapToLeader", t.GapToLeader);
                SetProp("IntervalToAhead", t.IntervalToAhead);
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
                SetProp("PitStopCount", t.PitStopCount);
                UpdateTopSpeedFromTimingStats(t.TopSpeed);
                SetProp("TopSpeedRank", t.TopSpeedRank);
                SetProp("OvertakeSystemEnabled", t.OvertakeSystemEnabled);
                SetProp("OvertakeAvailable", t.OvertakeAvailable);
                SetProp("FlagText", t.FlagText);
            };
            _client.OnSessionSnapshot += sess =>
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
            _client.OnWeatherSnapshot += w =>
            {
                SetProp("AirTemp", w.AirTemp);
                SetProp("TrackTemp", w.TrackTemp);
                SetProp("Humidity", w.Humidity);
                SetProp("Rainfall", w.Rainfall);
                SetProp("WindSpeedKph", w.WindSpeedKph);
            };
            _client.OnStatus += s => SetProp("Status", s);
            _client.OnDriverInfoSnapshot += info =>
            {
                SetProp("DriverTla", info.Tla ?? "");
                SetProp("DriverFirstName", info.FirstName ?? "");
                SetProp("DriverLastName", info.LastName ?? "");
                SetProp("DriverFullName", info.FullName ?? "");
                SetProp("DriverBroadcastName", info.BroadcastName ?? "");
                SetProp("TeamName", info.TeamName ?? "");
                SetProp("TeamColour", info.TeamColour ?? "");
            };
            _ = _client.StartAsync().ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    Log("unhandled in StartAsync: " + t.Exception.GetBaseException().Message);
                }
            }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

            StartSettingsWatcher();
            MaybeLaunchPicker();

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
            Log("using F1 Live SignalR source");
            return new F1SignalRClient(_settings.DriverNumber, Log);
        }

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
