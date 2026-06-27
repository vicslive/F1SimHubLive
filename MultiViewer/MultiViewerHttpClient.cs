using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using F1SimHubLive.F1Signalr;
using F1SimHubLive.Telemetry;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.MultiViewer
{
    /// <summary>
    /// Polls F1 MultiViewer's local /api/v1/live-timing/CarData endpoint and emits
    /// DriverSnapshots for the configured driver. Works equally for live sessions
    /// and synced replays (MultiViewer streams the same JSON shape either way).
    /// </summary>
    internal sealed class MultiViewerHttpClient : ITelemetrySource
    {
        private volatile string _driverNumber;
        private readonly string _baseUrl;
        private readonly int _pollIntervalMs;
        private readonly int _timingPollIntervalMs;
        private readonly Action<string> _log;
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _cts = new();

        private DateTime _lastEmittedUtc = DateTime.MinValue;
        // Driver-independent playback position for the session clock. Set on
        // every CarData response from the freshest frame across ALL cars, and
        // never reset on a driver switch, so the wheel clock keeps ticking even
        // when the selected driver has no frames in a batch or just after a
        // switch (unlike _lastEmittedUtc, which is per-driver and forward-only).
        private DateTime _playheadUtc = DateTime.MinValue;
        // Session end in UTC, parsed once from SessionInfo (EndDate + GmtOffset).
        // The wheel countdown is SessionEndUtc - playhead, identical to the
        // picker header clock, which is the proven formula.
        private DateTime? _sessionEndUtc;
        // Latest valid ExtrapolatedClock anchor ({Utc, Remaining, Extrapolating}).
        // The session clock is this anchor extrapolated to the current CarData
        // playback position — no hard-coded duration, no race-start dependency.
        private ExtrapolatedClockDecoder.Clock _lastClock;
        private int _totalDrivers;
        private bool _driverInfoEmitted;
        private bool _everConnected;
        private int _consecutiveFailures;

        public event Action<DriverSnapshot>? OnSnapshot;
        public event Action<TimingSnapshot>? OnTimingSnapshot;
        public event Action<SessionSnapshot>? OnSessionSnapshot;
        public event Action<WeatherSnapshot>? OnWeatherSnapshot;
        public event Action<DriverInfoSnapshot>? OnDriverInfoSnapshot;
        public event Action<string>? OnStatus;

        public MultiViewerHttpClient(string driverNumber, string baseUrl, int pollIntervalMs, int timingPollIntervalMs, Action<string> log)
        {
            _driverNumber = driverNumber;
            _baseUrl = baseUrl.TrimEnd('/');
            _pollIntervalMs = Math.Max(100, pollIntervalMs);
            _timingPollIntervalMs = Math.Max(500, timingPollIntervalMs);
            _log = log;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        }

        public Task StartAsync()
        {
            OnStatus?.Invoke("Connecting");
            _ = Task.Run(() => CarDataLoopAsync(_cts.Token));
            _ = Task.Run(() => TimingDataLoopAsync(_cts.Token));
            _ = Task.Run(() => SessionDataLoopAsync(_cts.Token));
            _ = Task.Run(() => WeatherDataLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        private async Task CarDataLoopAsync(CancellationToken ct)
        {
            string url = $"{_baseUrl}/api/v1/live-timing/CarData";
            _log("MultiViewer polling CarData " + url + $" every {_pollIntervalMs} ms (driver #{_driverNumber})");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string json = await _http.GetStringAsync(url).ConfigureAwait(false);
                    HandleCarDataResponse(json);
                    _consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures == 1 || _consecutiveFailures % 10 == 0)
                    {
                        _log($"MultiViewer CarData poll failed ({_consecutiveFailures}): {ex.Message}");
                    }
                    if (_consecutiveFailures >= 3)
                    {
                        OnStatus?.Invoke(_everConnected ? "Disconnected" : "WaitingForMultiViewer");
                    }
                }

                try { await Task.Delay(_pollIntervalMs, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
        }

        private async Task TimingDataLoopAsync(CancellationToken ct)
        {
            string tdUrl = $"{_baseUrl}/api/v1/live-timing/TimingData";
            string appUrl = $"{_baseUrl}/api/v1/live-timing/TimingAppData";
            string statsUrl = $"{_baseUrl}/api/v1/live-timing/TimingStats";
            string rcUrl = $"{_baseUrl}/api/v1/live-timing/RaceControlMessages";
            _log("MultiViewer polling TimingData+TimingAppData+TimingStats+RaceControl every " + _timingPollIntervalMs + " ms");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var tdTask = _http.GetStringAsync(tdUrl);
                    var appTask = _http.GetStringAsync(appUrl);
                    var statsTask = _http.GetStringAsync(statsUrl);
                    var rcTask = _http.GetStringAsync(rcUrl);
                    await Task.WhenAll(tdTask, appTask, statsTask, rcTask).ConfigureAwait(false);

                    var snap = TimingDataDecoder.Parse(tdTask.Result, _driverNumber);
                    if (snap != null)
                    {
                        var (compound, age, pitStops) = TimingAppDataDecoder.Parse(appTask.Result, _driverNumber);
                        snap.TyreCompound = compound;
                        snap.TyreAge = age;
                        snap.PitStopCount = pitStops;

                        var (topSpeed, topSpeedRank) = TimingStatsDecoder.Parse(statsTask.Result, _driverNumber);
                        snap.TopSpeed = topSpeed;
                        snap.TopSpeedRank = topSpeedRank;

                        var (ovtEnabled, flagText) = RaceControlDecoder.Parse(rcTask.Result);
                        snap.OvertakeSystemEnabled = ovtEnabled;
                        snap.FlagText = flagText;
                        // Hamilton can use OVT only if system enabled AND he is within 1.0s of car ahead.
                        snap.OvertakeAvailable = ovtEnabled && IsWithinOneSecond(snap.IntervalToAhead);

                        OnTimingSnapshot?.Invoke(snap);
                    }
                }
                catch (Exception ex)
                {
                    // Timing failures are tracked but don't drive the Status banner; CarData does.
                    if (_consecutiveFailures == 0)
                    {
                        _log("MultiViewer Timing/AppData/Stats/RC poll failed: " + ex.Message);
                    }
                }

                try { await Task.Delay(_timingPollIntervalMs, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
        }

        private static bool IsWithinOneSecond(string interval)
        {
            if (string.IsNullOrEmpty(interval)) return false;
            // Interval shape: "+0.424" (seconds), "+1L" / "+2L" (laps - never within 1s),
            // or "" when leading. Reject lap-based gaps and parse the seconds form.
            if (interval.IndexOf('L') >= 0) return false;
            string trimmed = interval.TrimStart('+');
            if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double sec))
            {
                return sec > 0.0 && sec < 1.0;
            }
            return false;
        }

        private async Task SessionDataLoopAsync(CancellationToken ct)
        {
            string lapUrl = $"{_baseUrl}/api/v1/live-timing/LapCount";
            string statusUrl = $"{_baseUrl}/api/v1/live-timing/TrackStatus";
            string clockUrl = $"{_baseUrl}/api/v1/live-timing/ExtrapolatedClock";
            string sessionUrl = $"{_baseUrl}/api/v1/live-timing/SessionInfo";
            string driverListUrl = $"{_baseUrl}/api/v1/live-timing/DriverList";
            _log("MultiViewer polling LapCount+TrackStatus+ExtrapolatedClock+SessionInfo every " + _timingPollIntervalMs + " ms");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Fire all 4 timing endpoints in parallel. Each is awaited
                    // individually via SafeGetString so a 404 on ONE endpoint
                    // (e.g. LapCount during practice — MV returns 404 "No data
                    // found, do you have live timing running?" because practice
                    // sessions have no lap count) doesn't poison the rest of
                    // the loop. Before this guard, a single Task.WhenAll on
                    // four tasks would throw on the first failed task and skip
                    // DriverList fetch, TrackStatus decoding, and the whole
                    // SessionSnapshot emission — leaving the wheel showing
                    // "F1 LIVE" indefinitely during any practice/quali
                    // session that doesn't expose LapCount.
                    var lapTask = _http.GetStringAsync(lapUrl);
                    var statusTask = _http.GetStringAsync(statusUrl);
                    var clockTask = _http.GetStringAsync(clockUrl);
                    var sessionTask = _http.GetStringAsync(sessionUrl);
                    string lapJson = await SafeGetString(lapTask, "LapCount").ConfigureAwait(false);
                    string statusJson = await SafeGetString(statusTask, "TrackStatus").ConfigureAwait(false);
                    string clockJson = await SafeGetString(clockTask, "ExtrapolatedClock").ConfigureAwait(false);
                    // SessionInfo carries EndDate + GmtOffset → cache session end
                    // in UTC. Parsed every iteration (cheap) but only changes
                    // once per session; drives the wheel countdown below.
                    string sessionInfoJson = await SafeGetString(sessionTask, "SessionInfo").ConfigureAwait(false);
                    if (sessionInfoJson.Length > 0) TryUpdateSessionEnd(sessionInfoJson);

                    // DriverList: fetched once (field size doesn't change mid-race). Retry each
                    // iteration until we get a non-zero count, and until we resolve identity
                    // fields (TLA / last name / team) for the configured driver number.
                    if (_totalDrivers == 0 || !_driverInfoEmitted)
                    {
                        try
                        {
                            string dlJson = await _http.GetStringAsync(driverListUrl).ConfigureAwait(false);
                            if (_totalDrivers == 0)
                            {
                                int n = DriverListDecoder.CountDrivers(dlJson);
                                if (n > 0) _totalDrivers = n;
                            }
                            if (!_driverInfoEmitted)
                            {
                                var info = DriverListDecoder.ParseDriverInfo(dlJson, _driverNumber);
                                if (info != null && (info.LastName.Length > 0 || info.Tla.Length > 0))
                                {
                                    _driverInfoEmitted = true;
                                    _log($"MultiViewer DriverList resolved #{_driverNumber}: " +
                                         $"{info.Tla} {info.BroadcastName} ({info.TeamName})");
                                    OnDriverInfoSnapshot?.Invoke(info);
                                }
                            }
                        }
                        catch { /* try again next tick */ }
                    }

                    // Decoders are already tolerant of empty/malformed JSON
                    // (TrackStatusDecoder returns (0,""), LapCountDecoder
                    // returns (0,0), etc.) so an empty string from a 404'd
                    // endpoint just leaves that piece of the snapshot blank
                    // instead of killing the whole emit.
                    var (currentLap, totalLaps) = LapCountDecoder.Parse(lapJson);
                    var (code, msg) = TrackStatusDecoder.Parse(statusJson);

                    // Cache the latest valid ExtrapolatedClock anchor. MV serves
                    // {Utc, Remaining} as a self-consistent baseline ("at Utc,
                    // Remaining was left") — static during replays, decrementing
                    // live — so it never needs a hard-coded session length.
                    if (clockJson.Length > 0)
                    {
                        var clock = ExtrapolatedClockDecoder.Parse(clockJson);
                        if (clock.IsValid) _lastClock = clock;
                    }

                    // Session remaining = anchor Remaining extrapolated to the
                    // current CarData playback position (the replay "now"). This
                    // uses only the ExtrapolatedClock's own Utc baseline + the
                    // CarData frame Utc, so it can't drift to a phantom +1h from
                    // a stale duration guess (the old 2h-default bug).
                    // Wheel countdown — identical formula to the picker header
                    // clock: remaining = SessionEnd(UTC) - playback position.
                    // The playhead is the driver-independent CarData frame UTC,
                    // so it advances with the video and survives driver switches.
                    // Fall back to the ExtrapolatedClock anchor only when
                    // SessionInfo hasn't yielded an end time yet.
                    string remainingText = "";
                    if (_sessionEndUtc.HasValue && _playheadUtc != DateTime.MinValue)
                    {
                        remainingText = FormatRemaining(_sessionEndUtc.Value - _playheadUtc);
                    }
                    else if (_lastClock.IsValid && _playheadUtc != DateTime.MinValue)
                    {
                        var live = ExtrapolatedClockDecoder.LiveRemaining(_lastClock, _playheadUtc);
                        remainingText = FormatRemaining(live);
                    }

                    OnSessionSnapshot?.Invoke(new SessionSnapshot
                    {
                        Utc = DateTime.UtcNow,
                        CurrentLap = currentLap,
                        TotalLaps = totalLaps,
                        TrackStatusCode = code,
                        TrackStatusMessage = msg,
                        SessionTimeRemaining = remainingText,
                        TotalDrivers = _totalDrivers
                    });
                }
                catch (Exception ex)
                {
                    if (_consecutiveFailures == 0)
                    {
                        _log("MultiViewer Session poll failed: " + ex.Message);
                    }
                }

                try { await Task.Delay(_timingPollIntervalMs, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
        }

        // Awaits an HTTP GET, returning empty string on any failure. Lets
        // SessionDataLoopAsync fan out multiple endpoints in parallel without
        // a single 404 (e.g. LapCount during practice) cascading into a loop-
        // wide skip. Logs throttled by name so a chronic 404 doesn't spam the
        // log every iteration.
        private async Task<string> SafeGetString(Task<string> task, string endpointName)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_consecutiveFailures == 0)
                {
                    _log($"MultiViewer {endpointName} poll failed: {ex.Message}");
                }
                return "";
            }
        }

        private async Task WeatherDataLoopAsync(CancellationToken ct)
        {
            string url = $"{_baseUrl}/api/v1/live-timing/WeatherData";
            const int weatherIntervalMs = 5000;
            _log("MultiViewer polling WeatherData every " + weatherIntervalMs + " ms");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string json = await _http.GetStringAsync(url).ConfigureAwait(false);
                    var w = WeatherDataDecoder.Parse(json);
                    if (w != null)
                    {
                        w.Utc = DateTime.UtcNow;
                        OnWeatherSnapshot?.Invoke(w);
                    }
                }
                catch (Exception ex)
                {
                    if (_consecutiveFailures == 0)
                    {
                        _log("MultiViewer Weather poll failed: " + ex.Message);
                    }
                }

                try { await Task.Delay(weatherIntervalMs, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
        }

        // Parses SessionInfo's EndDate (session-local, no offset) + GmtOffset
        // (local→UTC delta) into an absolute UTC end time, exactly like the
        // picker. Cached in _sessionEndUtc; tolerant of missing/garbage fields.
        private void TryUpdateSessionEnd(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                string? endDateStr = root["EndDate"]?.Value<string>();
                string? gmtOffsetStr = root["GmtOffset"]?.Value<string>();
                if (!string.IsNullOrEmpty(endDateStr) && !string.IsNullOrEmpty(gmtOffsetStr) &&
                    DateTime.TryParse(endDateStr, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var endLocal) &&
                    TimeSpan.TryParse(gmtOffsetStr, CultureInfo.InvariantCulture, out var gmtOffset))
                {
                    _sessionEndUtc = new DateTimeOffset(
                        DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified),
                        gmtOffset).UtcDateTime;
                }
            }
            catch { }
        }

        // Matches the picker's FormatHms: H:MM:SS for races, M:SS for sub-hour
        // practice/qualifying (no leading zero on minutes, no phantom hour).
        private static string FormatRemaining(TimeSpan ts)
        {
            if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private void HandleCarDataResponse(string json)
        {
            // Advance the driver-independent session-clock playhead from the
            // freshest frame across all cars. Tracks the replay position (incl.
            // backward seeks) and survives driver switches, so the wheel clock
            // never blanks just because the selected driver lacks frames here.
            var playhead = CarDataDecoder.LatestFrameUtc(json);
            if (playhead != DateTime.MinValue) _playheadUtc = playhead;

            int emitted = 0;
            foreach (var snap in CarDataDecoder.ParseCarDataJson(json, _driverNumber))
            {
                if (snap.Utc <= _lastEmittedUtc) continue;
                _lastEmittedUtc = snap.Utc;
                OnSnapshot?.Invoke(snap);
                emitted++;
            }
            if (emitted > 0 && !_everConnected)
            {
                _everConnected = true;
                OnStatus?.Invoke("Connected");
                _log("MultiViewer first snapshot received");
            }
            else if (emitted > 0 && _consecutiveFailures > 0)
            {
                OnStatus?.Invoke("Connected");
            }
        }

        public void SetDriverNumber(string driverNumber)
        {
            if (string.IsNullOrWhiteSpace(driverNumber)) return;
            string normalized = driverNumber.Trim();
            if (normalized == _driverNumber) return;
            string previous = _driverNumber;
            _driverNumber = normalized;
            // Reset per-driver filter state so the new driver's frames are
            // accepted starting now, and DriverInfo (TLA / team / name) is
            // re-resolved from the next DriverList poll.
            _lastEmittedUtc = DateTime.MinValue;
            _driverInfoEmitted = false;
            _log($"driver switch {previous} -> {normalized}");
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            _http.Dispose();
        }
    }
}
