using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Fires every poll with the latest speed in km/h for every driver
    /// that appeared in the freshest CarData entry. Empty dictionary if
    /// CarData wasn't parseable. Keyed by racing number (e.g. "44").
    /// </summary>
    public event Action<IReadOnlyDictionary<string, int>>? OnSpeedsBatch;

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

                // Parse once, harvest both: per-driver speeds (every car in
                // the freshest entry) AND the selected driver's RPM.
                if (TryParseLatest(json, driver, out double rpm, out DateTime utc, out var speeds))
                {
                    if (speeds.Count > 0)
                    {
                        OnSpeedsBatch?.Invoke(speeds);
                    }

                    if (utc > _lastEmittedUtc)
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

    internal static bool TryParseLatest(
        string json,
        string driverNumber,
        out double rpm,
        out DateTime utc,
        out Dictionary<string, int> speeds)
    {
        rpm = 0;
        utc = DateTime.MinValue;
        speeds = new Dictionary<string, int>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array
                || entries.GetArrayLength() == 0)
                return false;

            // Walk in reverse to find the freshest entry that has data.
            // We harvest speeds for ALL drivers from that single entry —
            // CarData payloads typically carry the full grid per frame.
            for (int i = entries.GetArrayLength() - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (!entry.TryGetProperty("Cars", out var cars)
                    || cars.ValueKind != JsonValueKind.Object)
                    continue;

                // Speed for every driver in this entry (channel "2" = km/h).
                foreach (var carProp in cars.EnumerateObject())
                {
                    if (!carProp.Value.TryGetProperty("Channels", out var ch)
                        || ch.ValueKind != JsonValueKind.Object) continue;
                    if (!ch.TryGetProperty("2", out var speedEl)) continue;
                    if (TryReadDouble(speedEl, out var spdKmh))
                    {
                        speeds[carProp.Name] = (int)Math.Round(spdKmh);
                    }
                }

                // RPM for the selected driver (channel "0").
                if (cars.TryGetProperty(driverNumber, out var car)
                    && car.TryGetProperty("Channels", out var channels)
                    && channels.TryGetProperty("0", out var rpmEl)
                    && TryReadDouble(rpmEl, out var rpmVal))
                {
                    rpm = rpmVal;
                }

                utc = entry.TryGetProperty("Utc", out var utcEl)
                      && utcEl.ValueKind == JsonValueKind.String
                      && DateTime.TryParse(utcEl.GetString(), out var parsedUtc)
                    ? parsedUtc.ToUniversalTime()
                    : DateTime.UtcNow;
                return true;
            }
        }
        catch
        {
            // Schema drift or partial payload — caller treats as "no value".
        }
        return false;
    }

    private static bool TryReadDouble(JsonElement el, out double value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                value = el.GetDouble();
                return true;
            case JsonValueKind.String:
                return double.TryParse(el.GetString(), out value);
            default:
                value = 0;
                return false;
        }
    }
}
