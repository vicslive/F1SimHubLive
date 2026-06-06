using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Polls F1 MultiViewer's local CarData endpoint and reports the most
/// recent RPM value for the active driver. Used by the picker's LED
/// preview bar so the user sees the same shift-light curve the wheel is
/// rendering, even when SimHub isn't running (the picker becomes a
/// standalone shift-light tuner).
///
/// CarData JSON shape (see MultiViewer\MultiViewerHttpClient.cs for the
/// full plugin parser):
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
/// Channel "0" is RPM (verified against the plugin's CarDataDecoder).
/// </summary>
internal sealed class PickerTelemetryClient : IDisposable
{
    private const string CarDataPath = "/api/v1/live-timing/CarData";

    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private volatile string _driverNumber = "";
    private CancellationTokenSource? _cts;
    private DateTime _lastEmittedUtc = DateTime.MinValue;

    public event Action<double>? OnRpm;
    public event Action<string>? OnStatus;

    public PickerTelemetryClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
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
                if (TryParseLatestRpm(json, driver, out double rpm, out DateTime utc)
                    && utc > _lastEmittedUtc)
                {
                    _lastEmittedUtc = utc;
                    OnRpm?.Invoke(rpm);
                    if (!everConnected)
                    {
                        everConnected = true;
                        OnStatus?.Invoke("Live telemetry connected");
                    }
                    consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                consecutiveFailures++;
                // Stay quiet until 3 in a row — MultiViewer's CarData is
                // briefly absent at session boundaries and that's normal.
                if (consecutiveFailures == 3)
                {
                    OnStatus?.Invoke(everConnected
                        ? "Telemetry disconnected"
                        : "Waiting for MultiViewer telemetry");
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

    internal static bool TryParseLatestRpm(string json, string driverNumber, out double rpm, out DateTime utc)
    {
        rpm = 0;
        utc = DateTime.MinValue;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
                return false;

            // Walk in reverse to find the newest entry that has data for our
            // driver. Most CarData payloads are time-ordered ascending and
            // contain ~5 entries; the last one is the freshest.
            for (int i = entries.GetArrayLength() - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (!entry.TryGetProperty("Cars", out var cars)
                    || cars.ValueKind != JsonValueKind.Object)
                    continue;
                if (!cars.TryGetProperty(driverNumber, out var car)
                    || !car.TryGetProperty("Channels", out var channels)
                    || !channels.TryGetProperty("0", out var rpmEl))
                    continue;

                if (rpmEl.ValueKind == JsonValueKind.Number)
                    rpm = rpmEl.GetDouble();
                else if (rpmEl.ValueKind == JsonValueKind.String
                         && double.TryParse(rpmEl.GetString(), out var parsed))
                    rpm = parsed;
                else
                    continue;

                if (entry.TryGetProperty("Utc", out var utcEl)
                    && utcEl.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(utcEl.GetString(), out var parsedUtc))
                {
                    utc = parsedUtc.ToUniversalTime();
                }
                else
                {
                    utc = DateTime.UtcNow;
                }
                return true;
            }
        }
        catch
        {
            // Schema drift or partial payload — caller treats as "no value".
        }
        return false;
    }
}
