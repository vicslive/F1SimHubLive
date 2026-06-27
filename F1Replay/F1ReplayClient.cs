using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using F1SimHubLive.F1Signalr;
using F1SimHubLive.MultiViewer;
using F1SimHubLive.Telemetry;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.F1Replay
{
    /// <summary>
    /// One driver's line in the replay grid snapshot the plugin publishes for
    /// the picker (identity + live car telemetry). No timing fields — the replay
    /// topic set carries CarData + DriverList only, so positions / gaps / sectors
    /// / lap times / tyres are intentionally absent in Phase 1.
    /// </summary>
    internal sealed class ReplayGridRow
    {
        public string Num = "";
        public string Tla = "";
        public string LastName = "";
        public string TeamName = "";
        public string TeamColour = "";
        public int Rpm;
        public int Speed;
        public int Gear;
        public int Throttle;
    }

    /// <summary>
    /// On-demand replay telemetry source. Plays a past session's recorded feed
    /// from F1's public live-timing static archive — no MultiViewer, no F1 TV
    /// subscription, no live session required. Drives the exact same
    /// <see cref="ITelemetrySource"/> events the live SignalR and MultiViewer
    /// sources do, so the wheel + dashboard are unchanged.
    ///
    /// A virtual clock advances <see cref="Position"/> by wall-elapsed × speed
    /// while playing, emitting every timeline event it crosses. Emitted
    /// <see cref="DriverSnapshot"/>s are stamped with the current wall clock so
    /// the 60 Hz interpolator brackets them exactly as it does for a live feed.
    /// </summary>
    internal sealed class F1ReplayClient : ITelemetrySource
    {
        private const int TickMs = 16; // ~60 Hz clock

        private readonly ArchiveClient _archive;
        private readonly bool _ownsArchive;
        private readonly string _sessionPath;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();

        private volatile string _driverNumber;
        private ReplayTimeline? _timeline;
        private Thread? _clockThread;

        // Clock state (guarded by _gate).
        private double _positionMs;
        private double _speed = 1.0;
        private bool _playing;
        private int _emitIndex;

        // Cached session state re-emitted whenever a field changes.
        private int _trackStatusCode;
        private string _trackStatusMessage = "";
        private int _totalDrivers;
        private int _currentLap;
        private int _totalLaps;
        private bool _driverInfoEmitted;
        private bool _firstSnapshotSent;

        // ----- Replay grid (all drivers, for the picker) ---------------------
        // identity is populated from DriverList; telemetry from CarData. Both
        // guarded by _gridGate because the clock thread writes while the plugin's
        // status-publisher thread reads via GetGrid().
        private readonly object _gridGate = new();
        private readonly Dictionary<string, DriverInfoSnapshot> _gridIdentity = new();
        private readonly Dictionary<string, DriverSnapshot> _gridTelemetry = new();

        public event Action<DriverSnapshot>? OnSnapshot;
#pragma warning disable CS0067 // Timing snapshots are not produced in the MVP topic set.
        public event Action<TimingSnapshot>? OnTimingSnapshot;
#pragma warning restore CS0067
        public event Action<SessionSnapshot>? OnSessionSnapshot;
        public event Action<WeatherSnapshot>? OnWeatherSnapshot;
        public event Action<DriverInfoSnapshot>? OnDriverInfoSnapshot;
        public event Action<string>? OnStatus;

        public F1ReplayClient(string driverNumber, string sessionPath, Action<string> log, ArchiveClient? archive = null)
        {
            _driverNumber = (driverNumber ?? "").Trim();
            _sessionPath = sessionPath ?? "";
            _log = log ?? (_ => { });
            _ownsArchive = archive == null;
            _archive = archive ?? new ArchiveClient(_log);
        }

        // ----- Transport state (read by the plugin to publish status) --------
        public bool IsLoaded => _timeline != null;
        public bool IsPlaying { get { lock (_gate) return _playing; } }
        public double Speed { get { lock (_gate) return _speed; } }
        public TimeSpan Position { get { lock (_gate) return TimeSpan.FromMilliseconds(_positionMs); } }
        public TimeSpan Duration => _timeline?.TotalDuration ?? TimeSpan.Zero;
        public int CurrentLap { get { lock (_gate) return _currentLap; } }
        public int TotalLaps => _timeline?.TotalLaps ?? 0;

        public async Task StartAsync()
        {
            OnStatus?.Invoke("Loading");
            try
            {
                _timeline = await ReplayTimeline.LoadAsync(_archive, _sessionPath, _log, _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log($"replay load failed: {ex.Message}");
                OnStatus?.Invoke("LoadFailed");
                return;
            }

            if (_timeline.Events.Count == 0)
            {
                OnStatus?.Invoke("Empty");
                return;
            }

            // Resolve the picked driver's identity up front from the full
            // DriverList snapshot so the wheel shows the right name immediately.
            ResolveDriverIdentity();

            lock (_gate)
            {
                _positionMs = 0;
                _emitIndex = 0;
                _playing = true; // auto-play on load; picker can pause instantly
            }
            OnStatus?.Invoke("Playing");

            _clockThread = new Thread(ClockLoop) { IsBackground = true, Name = "F1ReplayClock" };
            _clockThread.Start();
        }

        // ----- Transport API (invoked by the plugin's command channel) -------

        public void Play()
        {
            lock (_gate) { if (_playing) return; _playing = true; }
            OnStatus?.Invoke("Playing");
        }

        public void Pause()
        {
            lock (_gate) { if (!_playing) return; _playing = false; }
            OnStatus?.Invoke("Paused");
        }

        public void TogglePlay()
        {
            bool nowPlaying;
            lock (_gate) { _playing = !_playing; nowPlaying = _playing; }
            OnStatus?.Invoke(nowPlaying ? "Playing" : "Paused");
        }

        public void SetSpeed(double speed)
        {
            if (speed <= 0) return;
            lock (_gate) _speed = Math.Min(16.0, speed);
        }

        public void Seek(TimeSpan position)
        {
            var dur = Duration;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > dur) position = dur;
            lock (_gate)
            {
                _positionMs = position.TotalMilliseconds;
                _emitIndex = _timeline?.IndexAtOrAfter(position) ?? 0;
            }
            PrimeStateAt(position);
            _log($"replay seek -> {position:hh\\:mm\\:ss}");
        }

        public void SeekToLap(int lap)
        {
            if (_timeline == null) return;
            Seek(_timeline.OffsetForLap(lap));
        }

        /// <summary>
        /// Anchor the data to the on-screen session clock (time remaining).
        /// No-op when the session has no clock topic.
        /// </summary>
        public void SeekToRemaining(TimeSpan remaining)
        {
            var off = _timeline?.OffsetForRemaining(remaining);
            if (off.HasValue) Seek(off.Value);
        }

        /// <summary>True when the loaded session carries an ExtrapolatedClock.</summary>
        public bool HasSessionClock => _timeline?.HasSessionClock ?? false;

        /// <summary>Current official session clock (time remaining), or null.</summary>
        public TimeSpan? SessionRemaining => _timeline?.RemainingAt(Position);

        public void SetDriverNumber(string driverNumber)
        {
            if (string.IsNullOrWhiteSpace(driverNumber)) return;
            string normalized = driverNumber.Trim();
            if (normalized == _driverNumber) return;
            _driverNumber = normalized;
            _driverInfoEmitted = false;
            ResolveDriverIdentity();
            _log($"replay driver switch -> {normalized}");
        }

        // ----- Clock loop ----------------------------------------------------

        private void ClockLoop()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastTicks = sw.ElapsedMilliseconds;

            while (!_cts.IsCancellationRequested)
            {
                long now = sw.ElapsedMilliseconds;
                double wallDelta = now - lastTicks;
                lastTicks = now;

                double from, to;
                int startIndex;
                bool playing;
                lock (_gate)
                {
                    playing = _playing;
                    if (playing)
                    {
                        from = _positionMs;
                        _positionMs += wallDelta * _speed;
                        var durMs = Duration.TotalMilliseconds;
                        if (_positionMs >= durMs)
                        {
                            _positionMs = durMs;
                            _playing = false;
                        }
                        to = _positionMs;
                        startIndex = _emitIndex;
                    }
                    else { from = to = _positionMs; startIndex = _emitIndex; }
                }

                if (playing && to > from)
                    EmitRange(startIndex, TimeSpan.FromMilliseconds(to));

                if (!playing && Position >= Duration && Duration > TimeSpan.Zero)
                    OnStatus?.Invoke("Ended");

                try { Task.Delay(TickMs, _cts.Token).Wait(_cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (AggregateException) { break; }
            }
        }

        private void EmitRange(int startIndex, TimeSpan upTo)
        {
            var tl = _timeline;
            if (tl == null) return;
            int i = startIndex;
            int end = tl.IndexAtOrAfter(upTo);
            for (; i < end && i < tl.Events.Count; i++)
                Dispatch(tl.Events[i]);
            lock (_gate) _emitIndex = i;
        }

        private void Dispatch(ReplayEvent ev)
        {
            try
            {
                switch (ev.Topic)
                {
                    case ReplayTopic.CarData:
                        if (ev.Payload.Type == JTokenType.String)
                            EmitCarData((string)ev.Payload!);
                        break;
                    case ReplayTopic.DriverList:
                        ApplyDriverList(ev.Payload.ToString());
                        break;
                    case ReplayTopic.TrackStatus:
                        ApplyTrackStatus(ev.Payload.ToString());
                        break;
                    case ReplayTopic.Weather:
                        ApplyWeather(ev.Payload.ToString());
                        break;
                    case ReplayTopic.LapCount:
                        ApplyLapCount(ev.Payload.ToString());
                        break;
                }
            }
            catch (Exception ex)
            {
                _log($"replay dispatch error ({ev.Topic}): {ex.Message}");
            }
        }

        private void EmitCarData(string base64Deflate)
        {
            string json = CarDataDecoder.Inflate(base64Deflate);

            // Selected driver -> the wheel / dashboard (unchanged behaviour).
            foreach (var snap in CarDataDecoder.ParseCarDataJson(json, _driverNumber))
            {
                // Stamp wall-clock so the interpolator brackets prev/curr the
                // same way it does for the live feed (recorded timestamps live
                // in the past and would clamp the interpolation to "latest").
                snap.Utc = DateTime.UtcNow;
                OnSnapshot?.Invoke(snap);
                if (!_firstSnapshotSent)
                {
                    _firstSnapshotSent = true;
                    OnStatus?.Invoke("Connected");
                }
            }

            // All drivers -> the replay grid the picker renders.
            var latest = CarDataDecoder.ParseAllLatestJson(json);
            if (latest.Count > 0)
            {
                lock (_gridGate)
                {
                    foreach (var kv in latest) _gridTelemetry[kv.Key] = kv.Value;
                }
            }
        }

        private void ApplyDriverList(string json)
        {
            int n = DriverListDecoder.CountDrivers(json);
            if (n > _totalDrivers)
            {
                _totalDrivers = n;
                EmitSessionSnapshot();
            }
            if (_driverInfoEmitted) return;
            var info = DriverListDecoder.ParseDriverInfo(json, _driverNumber);
            if (info == null || (info.LastName.Length == 0 && info.Tla.Length == 0)) return;
            _driverInfoEmitted = true;
            OnDriverInfoSnapshot?.Invoke(info);
        }

        /// <summary>
        /// Snapshot of every driver in the field at the current replay position:
        /// identity (TLA / last name / team colour) merged with live car telemetry
        /// (RPM / speed / gear / throttle). Ordered by racing number. The plugin
        /// serialises this to <c>F1SimHubLive.ReplayGrid.json</c> for the picker.
        /// </summary>
        public IReadOnlyList<ReplayGridRow> GetGrid()
        {
            var rows = new List<ReplayGridRow>();
            lock (_gridGate)
            {
                var nums = new HashSet<string>(_gridIdentity.Keys);
                foreach (var k in _gridTelemetry.Keys) nums.Add(k);
                foreach (var num in nums)
                {
                    _gridIdentity.TryGetValue(num, out var id);
                    _gridTelemetry.TryGetValue(num, out var t);
                    rows.Add(new ReplayGridRow
                    {
                        Num = num,
                        Tla = id?.Tla ?? "",
                        LastName = id?.LastName ?? "",
                        TeamName = id?.TeamName ?? "",
                        TeamColour = id?.TeamColour ?? "",
                        Rpm = (int)Math.Round(t?.Rpm ?? 0),
                        Speed = (int)Math.Round(t?.Speed ?? 0),
                        Gear = t?.Gear ?? 0,
                        Throttle = (int)Math.Round(t?.Throttle ?? 0),
                    });
                }
            }
            rows.Sort((a, b) =>
            {
                int ai = int.TryParse(a.Num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : int.MaxValue;
                int bi = int.TryParse(b.Num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : int.MaxValue;
                return ai.CompareTo(bi);
            });
            return rows;
        }

        private void ApplyTrackStatus(string json)
        {
            var (code, msg) = TrackStatusDecoder.Parse(json);
            if (code <= 0) return;
            if (code == _trackStatusCode && msg == _trackStatusMessage) return;
            _trackStatusCode = code;
            _trackStatusMessage = msg ?? "";
            EmitSessionSnapshot();
        }

        private void ApplyWeather(string json)
        {
            var w = WeatherDataDecoder.Parse(json);
            if (w == null) return;
            w.Utc = DateTime.UtcNow;
            OnWeatherSnapshot?.Invoke(w);
        }

        private void ApplyLapCount(string json)
        {
            var (current, total) = LapCountDecoder.Parse(json);
            bool changed = false;
            if (current > 0 && current != _currentLap) { _currentLap = current; changed = true; }
            if (total > 0 && total != _totalLaps) { _totalLaps = total; changed = true; }
            if (changed) EmitSessionSnapshot();
        }

        private void EmitSessionSnapshot()
        {
            OnSessionSnapshot?.Invoke(new SessionSnapshot
            {
                Utc = DateTime.UtcNow,
                CurrentLap = _currentLap,
                TotalLaps = _totalLaps,
                TrackStatusCode = _trackStatusCode,
                TrackStatusMessage = _trackStatusMessage,
                TotalDrivers = _totalDrivers,
            });
        }

        private void ResolveDriverIdentity()
        {
            var json = _timeline?.FirstDriverListJson;
            if (string.IsNullOrEmpty(json)) return;

            // Populate the replay grid's identity for ALL drivers from the full
            // merged DriverList snapshot. We deliberately do this here (and on a
            // driver switch) rather than in ApplyDriverList: F1's in-session
            // DriverList deltas carry only line/position updates and omit
            // Tla/TeamName/TeamColour, so upserting them per-delta would blank
            // the field. FirstDriverListJson is the complete merged snapshot.
            var all = DriverListDecoder.ParseAllDrivers(json!);
            if (all.Count > 0)
            {
                lock (_gridGate)
                {
                    foreach (var kv in all) _gridIdentity[kv.Key] = kv.Value;
                }
            }

            ApplyDriverList(json!);
        }

        /// <summary>
        /// After a seek, replay the latest state of each non-CarData topic at or
        /// before the new position so flags / weather / lap count reflect the
        /// jumped-to moment instead of staying frozen on the pre-seek state.
        /// </summary>
        private void PrimeStateAt(TimeSpan position)
        {
            var tl = _timeline;
            if (tl == null) return;
            int idx = tl.IndexAtOrAfter(position);
            bool track = false, weather = false, lap = false, drivers = false;
            for (int i = idx - 1; i >= 0 && !(track && weather && lap && drivers); i--)
            {
                var ev = tl.Events[i];
                switch (ev.Topic)
                {
                    case ReplayTopic.TrackStatus when !track:
                        track = true; ApplyTrackStatus(ev.Payload.ToString()); break;
                    case ReplayTopic.Weather when !weather:
                        weather = true; ApplyWeather(ev.Payload.ToString()); break;
                    case ReplayTopic.LapCount when !lap:
                        lap = true; ApplyLapCount(ev.Payload.ToString()); break;
                    case ReplayTopic.DriverList when !drivers:
                        drivers = true; ApplyDriverList(ev.Payload.ToString()); break;
                }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _clockThread?.Join(500); } catch { }
            if (_ownsArchive) _archive.Dispose();
            _cts.Dispose();
        }
    }
}
