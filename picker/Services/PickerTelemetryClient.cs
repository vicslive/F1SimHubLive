using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// One driver's instantaneous control inputs harvested from a single
/// CarData entry. Channel sources documented on
/// <see cref="PickerTelemetryClient.TryParseLatestNJson(string, string, out double, out int, out double, out DateTime, out Dictionary{string, int}, out Dictionary{string, DriverInputs}, out string)"/>.
/// </summary>
public readonly record struct DriverInputs(int Gear, double Throttle, double Rpm);

/// <summary>
/// Polls F1 MultiViewer's local CarData endpoint and reports the most
/// recent RPM value for the active driver. Used by the picker's LED
/// preview bar so the user sees the same shift-light curve the wheel is
/// rendering, even when SimHub isn't running (the picker becomes a
/// standalone shift-light tuner).
///
/// CarData JSON shape (see F1Signalr/CarDataDecoder.cs for the
/// canonical plugin parser):
///   {
///     "Entries": [
///       {
///         "Utc": "2026-06-05T18:00:00.000Z",
///         "Cars": {
///           "44": { "Channels": { "0": 11250, "2": 312, "3": 7, ... } }
///         }
///       }, ...
///     ]
///   }
///
/// Channel "0" is RPM, "2" is km/h (verified against the plugin's
/// CarDataDecoder).
///
/// <para>v1.5.4: switched parser from System.Text.Json to Newtonsoft.Json
/// to match the plugin. Vic's Media PC reproduced a scenario where the
/// plugin (NJSON on net48) successfully parsed CarData and lit the wheel
/// while the picker (STJ on net8) silently failed to parse the same
/// response, leaving the LED preview bar dim and every driver's speed
/// stuck at 0 km/h. STJ's default options are stricter than NJSON's
/// (trailing commas, comments, large numbers, duplicate keys) and the
/// picker was swallowing whichever schema-drift exception MV's payload
/// triggered. Using NJSON here removes that asymmetry entirely.</para>
///
/// <para>v1.5.4 also exposes <see cref="LastSuccessfulParseUtc"/>,
/// <see cref="LastParseErrorMessage"/>, and the
/// <see cref="OnParseError"/> event so MainWindow can surface telemetry
/// health in the UI instead of failing silently as previously.</para>
/// </summary>
internal sealed class PickerTelemetryClient : IDisposable
{
    private const string CarDataPath = "/api/v1/live-timing/CarData";

    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private volatile string _driverNumber = "";
    private CancellationTokenSource? _cts;
    private DateTime _lastEmittedUtc = DateTime.MinValue;
    private DateTime _latestFrameUtc = DateTime.MinValue;
    private int _parseFailureCount;
    private bool _rawDumpWritten;

    public event Action<double>? OnRpm;
    public event Action<string>? OnStatus;
    /// <summary>
    /// Fires every fresh CarData entry with the selected driver's current
    /// gear (channel "3"). 0 = neutral, -1 = reverse, 1..8 = gear number.
    /// Added in v1.7.4 to drive the picker's broadcast-style input cluster.
    /// </summary>
    public event Action<int>? OnGear;
    /// <summary>
    /// Fires every fresh CarData entry with the selected driver's current
    /// throttle position (channel "4"), 0–100 (percent). Added in v1.7.4
    /// to drive the picker's broadcast-style input cluster.
    /// </summary>
    public event Action<double>? OnThrottle;
    /// <summary>
    /// Fires every poll with the latest speed in km/h for every driver
    /// that appeared in the freshest CarData entry. Empty dictionary if
    /// CarData wasn't parseable. Keyed by racing number (e.g. "44").
    /// </summary>
    public event Action<IReadOnlyDictionary<string, int>>? OnSpeedsBatch;
    /// <summary>
    /// Fires every poll with the latest inputs (gear + throttle + RPM) for
    /// every driver in the freshest CarData entry. Added in v1.7.4 so the
    /// per-row driver list can render F1-Live-Timing-style per-driver
    /// clusters (gear letter + RPM number). Empty dictionary if CarData
    /// wasn't parseable. Keyed by racing number (e.g. "44").
    /// </summary>
    public event Action<IReadOnlyDictionary<string, DriverInputs>>? OnInputsBatch;

    /// <summary>
    /// Fires once when the parser first throws on a CarData response.
    /// Carries the exception message and a snippet of the raw response
    /// so the UI can surface "MV CarData unparseable" with detail.
    /// Subsequent failures are silent — the snapshot of the first
    /// failure is enough to diagnose schema drift.
    /// </summary>
    public event Action<string, string>? OnParseError; // (message, jsonSnippet)

    /// <summary>UTC of the most recent CarData entry we parsed successfully. MinValue if never.</summary>
    public DateTime LastSuccessfulParseUtc { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// Session-timeline UTC of the freshest CarData frame we parsed, updated on
    /// EVERY successful parse regardless of direction (unlike <c>_lastEmittedUtc</c>,
    /// which is forward-only for the wheel dedup). This is the video playhead:
    /// it advances at 1x while playing, freezes when paused (no new frames) and
    /// jumps on a seek. The session header uses it as "now". MinValue if never.
    /// </summary>
    public DateTime LatestCarDataUtc => _latestFrameUtc;

    /// <summary>Message from the first parse error since startup, or null if no failures yet.</summary>
    public string? LastParseErrorMessage { get; private set; }

    public PickerTelemetryClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        // v1.5.5: 5s timeout (was 2s) + automatic gzip/deflate decompression.
        // CarData is the largest MV endpoint — a full-grid telemetry frame
        // can be 30-80 KB and on a weaker box (e.g. Vic's Media PC) the
        // 2s budget was tripping on body download + JSON parse, leaving
        // the LED bar dim and every driver at 0 km/h while the plugin
        // (3s timeout, running in SimHub) kept the wheel lit. Bumping
        // matches LiveTimingClient (3s) with extra headroom and matches
        // what HttpClient does for the wheel's plugin-side polling.
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
        // Identify ourselves so MV's logs are diagnosable if Vic ever
        // needs to compare wheel-side vs picker-side request streams.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("F1SimHubLive-Picker/1.5.5");
    }

    public void SetDriverNumber(string driverNumber)
    {
        if (string.IsNullOrWhiteSpace(driverNumber)) return;
        string trimmed = driverNumber.Trim();
        if (trimmed == _driverNumber) return;
        _driverNumber = trimmed;
        // Reset the dedup filter so the new driver's frames are accepted
        // starting from the next poll.
        _lastEmittedUtc = DateTime.MinValue;
    }

    public void Start(int pollIntervalMs = 200)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(Math.Max(50, pollIntervalMs), _cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }

    private async Task LoopAsync(int pollIntervalMs, CancellationToken ct)
    {
        string url = _baseUrl + CarDataPath;
        int consecutiveFailures = 0;
        bool everConnected = false;

        while (!ct.IsCancellationRequested)
        {
            string driver = _driverNumber;
            if (string.IsNullOrEmpty(driver))
            {
                await SafeDelayAsync(pollIntervalMs, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                string json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

                // Parse once, harvest both: per-driver speeds (every car in
                // the freshest entry), per-driver inputs (gear/throttle/rpm),
                // AND the selected driver's RPM/gear/throttle convenience values.
                if (TryParseLatestNJson(json, driver, out double rpm, out int gear, out double throttle, out DateTime utc, out var speeds, out var inputsBatch, out string? parseError))
                {
                    // Publish the playhead on every parse — including backward
                    // seeks — so the session-header countdown tracks the video.
                    if (utc != DateTime.MinValue) _latestFrameUtc = utc;

                    if (speeds.Count > 0)
                    {
                        OnSpeedsBatch?.Invoke(speeds);
                    }
                    if (inputsBatch.Count > 0)
                    {
                        OnInputsBatch?.Invoke(inputsBatch);
                    }

                    if (utc > _lastEmittedUtc)
                    {
                        _lastEmittedUtc = utc;
                        LastSuccessfulParseUtc = DateTime.UtcNow;
                        OnRpm?.Invoke(rpm);
                        OnGear?.Invoke(gear);
                        OnThrottle?.Invoke(throttle);
                        if (!everConnected)
                        {
                            everConnected = true;
                            OnStatus?.Invoke("Live telemetry connected");
                        }
                        consecutiveFailures = 0;
                    }
                }
                else if (parseError != null)
                {
                    // HTTP succeeded, JSON arrived, parser blew up.
                    // Record it loudly so the UI can surface this.
                    if (LastParseErrorMessage == null)
                    {
                        LastParseErrorMessage = parseError;
                        TryWriteRawDumpOnce(json, parseError);
                        string snippet = json.Length > 800 ? json[..800] + "..." : json;
                        OnParseError?.Invoke(parseError, snippet);
                    }
                    _parseFailureCount++;
                }
                // else: HTTP succeeded but Entries was empty or had no Cars —
                // not an error, just MV hasn't broadcast a frame yet.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                // v1.5.5: surface the problem immediately if we've never
                // connected. Before this, the user waited ~15s (3 fails
                // × 5s timeout) for "Waiting for MultiViewer telemetry"
                // to appear — long enough to think the picker was just
                // broken with no signal. After first connect, keep the
                // 3-fail buffer because brief CarData drops at session
                // boundaries are normal.
                if (consecutiveFailures == 3 || (!everConnected && consecutiveFailures == 1))
                {
                    OnStatus?.Invoke(everConnected
                        ? "Telemetry disconnected: " + ex.GetType().Name
                        : "Waiting for MultiViewer telemetry (" + ex.GetType().Name + ")");
                }
            }

            await SafeDelayAsync(pollIntervalMs, ct).ConfigureAwait(false);
        }
    }

    private static async Task SafeDelayAsync(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Parses CarData JSON with Newtonsoft.Json, mirroring the plugin's
    /// <see cref="F1Signalr.CarDataDecoder"/> behavior so the picker
    /// succeeds wherever the plugin succeeds.
    /// </summary>
    /// <returns>
    /// true if we extracted at least one entry's worth of data (speeds and/or RPM).
    /// false if the JSON had no Entries with Cars (not an error).
    /// On hard parser failure, returns false AND sets <paramref name="parseError"/> non-null.
    /// </returns>
    internal static bool TryParseLatestNJson(
        string json,
        string driverNumber,
        out double rpm,
        out DateTime utc,
        out Dictionary<string, int> speeds,
        out string? parseError)
        => TryParseLatestNJson(json, driverNumber, out rpm, out _, out _, out utc, out speeds, out _, out parseError);

    /// <summary>
    /// Extended overload (v1.7.4): also harvests gear (channel "3") and
    /// throttle (channel "4") for the selected driver from the freshest
    /// CarData entry, AND batches per-driver inputs for every car in the
    /// entry so the picker can render a per-row cluster like F1 Live Timing.
    /// </summary>
    /// <param name="rpm">Selected driver's RPM (channel 0).</param>
    /// <param name="gear">Selected driver's gear (channel 3). 0 if missing.</param>
    /// <param name="throttle">Selected driver's throttle 0–100 (channel 4). 0 if missing.</param>
    /// <param name="speeds">Speed in km/h for every car in the entry (channel 2).</param>
    /// <param name="inputs">Per-driver inputs (Gear/Throttle/Rpm) for every car in the entry that has channels 0/3/4 visible.</param>
    internal static bool TryParseLatestNJson(
        string json,
        string driverNumber,
        out double rpm,
        out int gear,
        out double throttle,
        out DateTime utc,
        out Dictionary<string, int> speeds,
        out Dictionary<string, DriverInputs> inputs,
        out string? parseError)
    {
        rpm = 0;
        gear = 0;
        throttle = 0;
        utc = DateTime.MinValue;
        speeds = new Dictionary<string, int>();
        inputs = new Dictionary<string, DriverInputs>();
        parseError = null;

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (JsonReaderException ex)
        {
            parseError = $"JSON parse failed: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            parseError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (root["Entries"] is not JArray entries || entries.Count == 0)
            return false;

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry?["Cars"] is not JObject cars) continue;

            foreach (var carProp in cars.Properties())
            {
                if (carProp.Value?["Channels"] is not JObject ch) continue;
                var speedTok = ch["2"];
                if (speedTok != null)
                {
                    double spdKmh = TryReadDouble(speedTok);
                    speeds[carProp.Name] = (int)Math.Round(spdKmh);
                }
                // Per-driver gear / throttle / rpm — fold into the batch
                // dict so MainWindow can push them into the matching
                // DriverTimingRow. Missing channels degrade silently to 0.
                int g = ch["3"] is JToken gTok ? (int)TryReadDouble(gTok) : 0;
                double t = ch["4"] is JToken tTok ? TryReadDouble(tTok) : 0;
                double r = ch["0"] is JToken rTok ? TryReadDouble(rTok) : 0;
                inputs[carProp.Name] = new DriverInputs(g, t, r);
            }

            // Selected-driver convenience values (drive the header cluster
            // + LED preview). Reuse the batch entry so we don't re-walk JSON.
            if (inputs.TryGetValue(driverNumber, out var sel))
            {
                rpm = sel.Rpm;
                gear = sel.Gear;
                throttle = sel.Throttle;
            }

            // Read the frame UTC. MV always sends a "...Z" timestamp, so read it
            // as the UTC instant Newtonsoft already parsed (Kind=Utc). Do NOT
            // round-trip through utcTok.ToString() + DateTime.TryParse: Newtonsoft's
            // ToString() drops the Z, TryParse yields Kind=Unspecified, and the
            // subsequent ToUniversalTime() then double-applies the local offset
            // (e.g. +5 h), which silently shifts the playhead past session end.
            var utcTok = entry["Utc"];
            if (utcTok != null && (utcTok.Type == JTokenType.Date || utcTok.Type == JTokenType.String))
            {
                var dt = utcTok.Value<DateTime>();
                utc = dt.Kind switch
                {
                    DateTimeKind.Utc => dt,
                    DateTimeKind.Local => dt.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                };
            }
            else
            {
                utc = DateTime.UtcNow;
            }

            return true;
        }
        return false;
    }

    private static double TryReadDouble(JToken tok)
    {
        switch (tok.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                return (double)tok;
            case JTokenType.String:
                return double.TryParse(tok.ToString(), out var d) ? d : 0.0;
            default:
                return 0.0;
        }
    }

    /// <summary>
    /// On the very first parse failure since startup, dump the raw JSON
    /// response and the exception message to a log file under APPDATA so
    /// the user can ship it back for diagnosis. Subsequent failures are
    /// not logged (first sample is enough; logging every poll would fill
    /// the disk with identical payloads).
    /// </summary>
    private void TryWriteRawDumpOnce(string json, string parseError)
    {
        if (_rawDumpWritten) return;
        _rawDumpWritten = true;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "F1SimHubLive",
                "Diagnostics");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = Path.Combine(dir, $"picker-cardata-failed-{stamp}.json");
            var sb = new StringBuilder();
            sb.AppendLine("// F1SimHubLive picker — first CarData parse failure since startup");
            sb.AppendLine("// Generated at: " + DateTime.Now.ToString("O"));
            sb.AppendLine("// Driver: " + _driverNumber);
            sb.AppendLine("// Base URL: " + _baseUrl);
            sb.AppendLine("// Parser error: " + parseError);
            sb.AppendLine("// -- raw response below this line --");
            sb.AppendLine(json);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Diagnostics are best-effort — if disk is full or path is
            // protected, silently skip. We've still reported the error
            // via OnParseError / LastParseErrorMessage.
        }
    }
}
