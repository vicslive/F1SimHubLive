using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using F1SimHubLive.Picker.Models;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Polls MultiViewer's session / track-status / lap-count / extrapolated-clock /
/// heartbeat endpoints once a second and pushes the parsed result into a
/// <see cref="SessionHeaderModel"/> on the UI dispatcher.
///
/// <para>Race countdown is computed as <c>SessionEndUtc - Heartbeat.Utc</c>
/// because MV's <c>ExtrapolatedClock.Remaining</c> sticks at 1:59:59 during
/// a race (it's a Practice/Qualifying-style counter). The <c>Heartbeat</c>
/// endpoint emits the simulated stream time which, combined with
/// SessionInfo's <c>EndDate</c>+<c>GmtOffset</c>, yields a clock that ticks
/// down in lockstep with MV's own header.</para>
/// </summary>
public sealed class SessionInfoClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _baseUrl;
    private readonly Dispatcher _dispatcher;
    private readonly SessionHeaderModel _model;
    private readonly DispatcherTimer _tickTimer;
    private readonly Dictionary<string, ImageSource?> _flagCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    // Last successful ExtrapolatedClock fetch. _clockAnchorUtc is the clock's
    // OWN Utc baseline (lights-out for a race, session start otherwise) — the
    // session/race countdown is _lastRemaining − (playhead − _clockAnchorUtc).
    private TimeSpan _lastRemaining = TimeSpan.Zero;
    private DateTime _lastRemainingFetchUtc = DateTime.MinValue;
    private DateTime? _clockAnchorUtc;
    private bool _extrapolating;

    // Last successful Heartbeat fetch — used to extrapolate the race clock
    // (which is SessionEndUtc - simulated-now).
    private DateTime? _sessionEndUtc;
    private DateTime? _lastHeartbeatUtc;
    private DateTime _lastHeartbeatFetchedAt = DateTime.MinValue;
    private bool _isRaceSession;

    // MV serves the freshest CarData frame ~2s ahead of the painted video /
    // live-timing panel (decode buffer), so the raw playhead leads on-screen
    // time. Subtract this from the playhead so the header countdown matches MV.
    // Keep identical to the plugin's PlaybackLead (see docs/CLOCKS.md).
    private static readonly TimeSpan PlaybackLead = TimeSpan.FromSeconds(2);

    public SessionHeaderModel Model => _model;

    /// <summary>
    /// Supplies the current CarData playhead UTC (the session-timeline timestamp
    /// of the freshest telemetry frame). MV's <c>ExtrapolatedClock.Remaining</c>
    /// is a STATIC anchor during VODs and the <c>Heartbeat</c> only ticks every
    /// ~10 s, so the only signal that tracks the video frame-for-frame is the
    /// CarData frame UTC. The header countdown is therefore
    /// <c>SessionEndUtc - playhead</c>: it advances at 1x while playing, freezes
    /// when paused (frames stop) and jumps on a seek (frame UTC jumps) — exactly
    /// matching MV's own clock and our wheel dashboard.
    /// </summary>
    public Func<DateTime>? PlayheadProvider { get; set; }

    public SessionInfoClient(Dispatcher dispatcher, string baseUrl)
    {
        _dispatcher = dispatcher;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = new SessionHeaderModel();
        // Ticks every 250 ms to update the extrapolated clock display
        // without re-hitting the HTTP endpoint. Cheap.
        _tickTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _tickTimer.Tick += (_, _) => TickClock();
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => PollLoop(token), token);
        _tickTimer.Start();
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _tickTimer.Stop();
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnce(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { /* MV down or transient; retry next tick */ }

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnce(CancellationToken ct)
    {
        // Fire all five in parallel; they're independent.
        var t1 = Get("SessionInfo", ct);
        var t2 = Get("TrackStatus", ct);
        var t3 = Get("LapCount", ct);
        var t4 = Get("ExtrapolatedClock", ct);
        var t5 = Get("Heartbeat", ct);
        await Task.WhenAll(t1, t2, t3, t4, t5).ConfigureAwait(false);
        var sessionInfoJson = t1.Result;
        var trackStatusJson = t2.Result;
        var lapCountJson = t3.Result;
        var clockJson = t4.Result;
        var heartbeatJson = t5.Result;

        // Parse everything off the UI thread, push results in one dispatcher hop.
        string? raceName = null, countryCode3 = null, sessionType = null, sessionLabel = null;
        string? statusCode = null, statusMessage = null;
        int? currentLap = null, totalLaps = null;
        TimeSpan? remaining = null;
        bool extrapolating = false;
        DateTime? clockAnchorUtc = null;
        DateTime? sessionEndUtc = null;
        DateTime? heartbeatUtc = null;

        if (!string.IsNullOrEmpty(sessionInfoJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(sessionInfoJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("Meeting", out var meeting))
                {
                    if (meeting.TryGetProperty("Name", out var n)) raceName = n.GetString();
                    if (meeting.TryGetProperty("Country", out var c) &&
                        c.TryGetProperty("Code", out var cc)) countryCode3 = cc.GetString();
                }
                if (root.TryGetProperty("Type", out var t)) sessionType = t.GetString();
                // The root "Name" carries the full label MV shows — "Practice 2",
                // "Sprint Qualifying", "Qualifying", "Race" — including the number
                // that "Type" alone ("Practice") drops. Prefer it for display;
                // keep "Type" for the race-detection logic below.
                if (root.TryGetProperty("Name", out var sn2)) sessionLabel = sn2.GetString();
                if (string.IsNullOrEmpty(sessionType)) sessionType = sessionLabel;

                // EndDate is the session's local end time (ISO format,
                // no offset); GmtOffset gives the local→UTC delta. We
                // need UTC so the countdown stays correct regardless of
                // the picker's local time zone or DST.
                string? endDateStr = root.TryGetProperty("EndDate", out var ed) ? ed.GetString() : null;
                string? gmtOffsetStr = root.TryGetProperty("GmtOffset", out var go) ? go.GetString() : null;
                if (!string.IsNullOrEmpty(endDateStr) && !string.IsNullOrEmpty(gmtOffsetStr) &&
                    DateTime.TryParse(endDateStr, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var endLocal) &&
                    TimeSpan.TryParse(gmtOffsetStr, CultureInfo.InvariantCulture, out var gmtOffset))
                {
                    sessionEndUtc = new DateTimeOffset(
                        DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified),
                        gmtOffset).UtcDateTime;
                }
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(trackStatusJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(trackStatusJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("Status", out var s)) statusCode = s.GetString();
                if (root.TryGetProperty("Message", out var m)) statusMessage = m.GetString();
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(lapCountJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(lapCountJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("CurrentLap", out var c) && c.TryGetInt32(out var cl)) currentLap = cl;
                if (root.TryGetProperty("TotalLaps", out var t) && t.TryGetInt32(out var tl)) totalLaps = tl;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(clockJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(clockJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("Remaining", out var r) && TryParseHms(r.GetString(), out var rem))
                    remaining = rem;
                if (root.TryGetProperty("Extrapolating", out var ex) &&
                    ex.ValueKind == JsonValueKind.True) extrapolating = true;
                // The clock's own Utc baseline — for a race this is lights-out,
                // pushed when the red lights go off, so it bakes in the formation
                // lap + pre-race delay. AssumeUniversal|AdjustToUniversal keeps it
                // UTC regardless of the trailing Z (see docs/CLOCKS.md trap #1).
                if (root.TryGetProperty("Utc", out var cu) &&
                    DateTime.TryParse(cu.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ca))
                    clockAnchorUtc = DateTime.SpecifyKind(ca, DateTimeKind.Utc);
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(heartbeatJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(heartbeatJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("Utc", out var u) &&
                    DateTime.TryParse(u.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var hb))
                {
                    heartbeatUtc = DateTime.SpecifyKind(hb, DateTimeKind.Utc);
                }
            }
            catch { }
        }

        // Cache extrapolation seeds.
        if (remaining.HasValue)
        {
            _lastRemaining = remaining.Value;
            _lastRemainingFetchUtc = DateTime.UtcNow;
            _extrapolating = extrapolating;
        }
        if (clockAnchorUtc.HasValue) _clockAnchorUtc = clockAnchorUtc;
        if (sessionEndUtc.HasValue) _sessionEndUtc = sessionEndUtc;
        if (heartbeatUtc.HasValue)
        {
            _lastHeartbeatUtc = heartbeatUtc;
            _lastHeartbeatFetchedAt = DateTime.UtcNow;
        }

        bool isRace = !string.IsNullOrEmpty(sessionType) &&
                      sessionType.Equals("Race", StringComparison.OrdinalIgnoreCase);
        _isRaceSession = isRace;

        var (statusText, bg, fg) = MapTrackStatus(statusCode, statusMessage);
        ImageSource? flagImg = LoadFlagImage(countryCode3);
        string headerName = BuildHeaderName(raceName, sessionLabel ?? sessionType);
        string lapText = BuildLapText(currentLap, totalLaps, isRace);

        _ = _dispatcher.BeginInvoke(new Action(() =>
        {
            _model.HasSession = !string.IsNullOrEmpty(raceName);
            _model.CountryFlagImage = flagImg;
            _model.RaceName = headerName;
            _model.LapText = lapText;
            _model.TrackStatusText = statusText;
            _model.TrackStatusBackground = bg;
            _model.TrackStatusForeground = fg;
            // TimeText is updated on every TickClock so the seconds tick smoothly.
        }));
    }

    /// <summary>
    /// Recomputes the displayed remaining-time string between 1 Hz polls.
    /// The countdown is MV's <c>ExtrapolatedClock</c> anchor extrapolated to the
    /// playhead: <c>Remaining - (playhead - anchorUtc)</c>, where the playhead is
    /// the freshest CarData frame UTC supplied by <see cref="PlayheadProvider"/>.
    /// For a race the anchor Utc is lights-out (pushed when the red lights go
    /// off), so this bakes in the formation lap + pre-race delay and matches MV
    /// Live Timing to the second; for practice/qualifying the anchor is the
    /// session start, so the same formula is correct. The frame UTC advances at
    /// 1x while playing, freezes when paused and jumps on a seek, so the header
    /// tracks the video frame-for-frame. <c>SessionEndUtc - playhead</c> is a
    /// FALLBACK only (scheduled end; reads a few minutes ahead during a race
    /// start). See docs/CLOCKS.md before changing this.
    /// </summary>
    private void TickClock()
    {
        DateTime playhead = PlayheadProvider?.Invoke() ?? DateTime.MinValue;
        bool hasPlayhead = playhead != DateTime.MinValue;
        // Guard the subtraction: at startup (before the first CarData frame) the
        // playhead is DateTime.MinValue, and MinValue - PlaybackLead underflows,
        // throwing ArgumentOutOfRangeException that kills the picker on its first
        // tick. pos is only consumed by the two branches below that already
        // require a real playhead, so leave it at MinValue when none exists.
        DateTime pos = hasPlayhead ? playhead - PlaybackLead : DateTime.MinValue;

        // Primary: official ExtrapolatedClock anchor extrapolated to the playback
        // position. While Extrapolating==false (e.g. formation lap) MV freezes
        // Remaining, so we show it as-is to match the pre-start clock.
        if (_clockAnchorUtc.HasValue && playhead != DateTime.MinValue)
        {
            TimeSpan rem0 = _extrapolating
                ? _lastRemaining - (pos - _clockAnchorUtc.Value)
                : _lastRemaining;
            _model.TimeText = FormatHms(rem0);
            return;
        }

        // Fallback: scheduled session end minus the playhead. Approximate during
        // a race start until the ExtrapolatedClock anchor arrives.
        if (_sessionEndUtc.HasValue && playhead != DateTime.MinValue)
        {
            _model.TimeText = FormatHms(_sessionEndUtc.Value - pos);
            return;
        }

        // Fallback A (race, no telemetry yet): SessionEnd - simulated-now from
        // the Heartbeat plus elapsed wall-clock since we fetched it.
        if (_isRaceSession && _sessionEndUtc.HasValue && _lastHeartbeatUtc.HasValue)
        {
            var simNow = _lastHeartbeatUtc.Value + (DateTime.UtcNow - _lastHeartbeatFetchedAt);
            _model.TimeText = FormatHms(_sessionEndUtc.Value - simNow);
            return;
        }

        if (_lastRemainingFetchUtc == DateTime.MinValue)
        {
            _model.TimeText = "";
            return;
        }

        // Fallback B (practice / qualifying, no telemetry yet): tick the last
        // ExtrapolatedClock value down on wall-clock when it's extrapolating.
        TimeSpan rem = _lastRemaining;
        if (_extrapolating)
        {
            var elapsed = DateTime.UtcNow - _lastRemainingFetchUtc;
            rem = _lastRemaining - elapsed;
            if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
        }
        _model.TimeText = FormatHms(rem);
    }

    private async Task<string?> Get(string path, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v1/live-timing/{path}");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// header line. Strips "Grand Prix" to "GP" to match MV's compact label.
    /// </summary>
    private static string BuildHeaderName(string? raceName, string? sessionType)
    {
        if (string.IsNullOrEmpty(raceName)) return "";
        string name = raceName.Replace("Grand Prix", "GP", StringComparison.OrdinalIgnoreCase).Trim();
        if (string.IsNullOrEmpty(sessionType)) return name;
        return $"{name}: {sessionType}";
    }

    private static string BuildLapText(int? current, int? total, bool isRace)
    {
        if (!isRace || !current.HasValue || !total.HasValue) return "";
        int left = total.Value - current.Value;
        if (left < 0) left = 0;
        return $"Lap {current.Value}/{total.Value}  ({left} left)";
    }

    /// <summary>
    /// MV TrackStatus.Status codes (verified against live & replay sessions):
    ///   1 = AllClear (green)
    ///   2 = Yellow
    ///   3 = (unused, sometimes "SCDeployed" forerunner)
    ///   4 = SCDeployed (full Safety Car)
    ///   5 = Red
    ///   6 = VSCDeployed
    ///   7 = VSCEnding
    /// </summary>
    private static (string text, string bg, string fg) MapTrackStatus(string? code, string? msg)
    {
        return code switch
        {
            "1" => ("Track Clear", "#2BA84A", "#FFFFFF"),
            "2" => ("Yellow Flag", "#F5C04C", "#0F0F12"),
            "4" => ("Safety Car", "#F5C04C", "#0F0F12"),
            "5" => ("Red Flag", "#E83A3A", "#FFFFFF"),
            "6" => ("Virtual SC", "#F5C04C", "#0F0F12"),
            "7" => ("VSC Ending", "#F5C04C", "#0F0F12"),
            _ => (string.IsNullOrEmpty(msg) ? "—" : msg!, "#3A3A44", "#E8E8EE"),
        };
    }

    private static readonly Dictionary<string, string> Iso3ToIso2 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AUS"] = "AU", ["AUT"] = "AT", ["AZE"] = "AZ", ["BHR"] = "BH",
        ["BEL"] = "BE", ["BRA"] = "BR", ["CAN"] = "CA", ["CHN"] = "CN",
        ["ESP"] = "ES", ["FRA"] = "FR", ["GBR"] = "GB", ["GER"] = "DE",
        ["HUN"] = "HU", ["IND"] = "IN", ["ITA"] = "IT", ["JPN"] = "JP",
        ["KOR"] = "KR", ["MAL"] = "MY", ["MEX"] = "MX", ["MON"] = "MC",
        ["NED"] = "NL", ["POR"] = "PT", ["QAT"] = "QA", ["RUS"] = "RU",
        ["SAU"] = "SA", ["SGP"] = "SG", ["TUR"] = "TR", ["UAE"] = "AE",
        ["ARE"] = "AE", ["USA"] = "US", ["ZAF"] = "ZA", ["ARG"] = "AR",
    };

    /// <summary>
    /// Loads the country flag PNG from <c>Assets/Flags/&lt;iso2&gt;.png</c>
    /// via a pack URI. Results are cached and frozen so the same
    /// <see cref="ImageSource"/> can be safely handed to the UI thread.
    /// Returns null when the country code is unknown or the PNG is missing.
    /// </summary>
    private ImageSource? LoadFlagImage(string? iso3)
    {
        if (string.IsNullOrEmpty(iso3)) return null;
        if (!Iso3ToIso2.TryGetValue(iso3, out var iso2)) return null;

        string key = iso2.ToLowerInvariant();
        if (_flagCache.TryGetValue(key, out var cached)) return cached;

        ImageSource? img = null;
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Flags/{key}.png", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            img = bmp;
        }
        catch (IOException) { /* PNG not bundled — leave img null */ }
        catch (Exception) { /* malformed PNG or pack URI; leave null */ }

        _flagCache[key] = img;
        return img;
    }

    private static bool TryParseHms(string? s, out TimeSpan ts)
    {
        ts = TimeSpan.Zero;
        if (string.IsNullOrEmpty(s)) return false;
        // MV format is "HH:mm:ss" (e.g. "01:59:59"); also accept "H:mm:ss".
        return TimeSpan.TryParse(s, out ts);
    }

    private static string FormatHms(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }
}
