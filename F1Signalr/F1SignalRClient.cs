using System;
using System.Threading.Tasks;
using F1SimHubLive.MultiViewer;
using F1SimHubLive.Telemetry;
using Microsoft.AspNet.SignalR.Client;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.F1Signalr
{
    internal sealed class F1SignalRClient : F1SimHubLive.Telemetry.ITelemetrySource
    {
        private const string HubUrl = "https://livetiming.formula1.com/signalr";
        private const string HubName = "Streaming";

        private volatile string _driverNumber;
        private readonly Action<string> _log;
        private HubConnection? _connection;
        private IHubProxy? _proxy;
        private bool _driverInfoEmitted;

        // Session-level state cached from feed topics. The SignalR feed pushes
        // each topic independently (TrackStatus changes a few times a session,
        // DriverList is delivered once + deltas), so we hold the last known
        // value and re-emit a complete SessionSnapshot whenever any field
        // changes. OnFeed is single-threaded by the SignalR client, so no lock.
        private int _trackStatusCode;
        private string _trackStatusMessage = "";
        private int _totalDrivers;

        // Cache of the initial DriverList snapshot received with the Subscribe
        // response. We use this on driver-switch (picker click) to resolve the
        // new driver's identity *immediately* instead of waiting for the next
        // DriverList feed event — feed events on this topic are deltas (only
        // changed drivers) and during practice can be minutes apart, leaving
        // the wheel showing "F1 LIVE" instead of the new driver's name until
        // a delta happens to include them. Driver identity (Tla, names, team,
        // colour) is static for the whole weekend so the initial snapshot is
        // sufficient — no need to merge deltas in.
        private string? _driverListSnapshotJson;

        public event Action<DriverSnapshot>? OnSnapshot;
#pragma warning disable CS0067 // Timing/Weather events reserved for future SignalR parsing
        public event Action<TimingSnapshot>? OnTimingSnapshot;
        public event Action<WeatherSnapshot>? OnWeatherSnapshot;
#pragma warning restore CS0067
        public event Action<SessionSnapshot>? OnSessionSnapshot;
        public event Action<DriverInfoSnapshot>? OnDriverInfoSnapshot;
        public event Action<string>? OnStatus;

        public F1SignalRClient(string driverNumber, Action<string> log)
        {
            _driverNumber = driverNumber;
            _log = log;
        }

        public async Task StartAsync()
        {
            _connection = new HubConnection(HubUrl);
            // F1's edge requires these — empirically observed from existing community clients.
            _connection.Headers["User-Agent"] = "BestHTTP";
            _connection.Headers["Accept-Encoding"] = "gzip, identity";

            _proxy = _connection.CreateHubProxy(HubName);
            _proxy.On<string, JToken, string>("feed", OnFeed);

            _connection.Closed += () => OnStatus?.Invoke("Closed");
            _connection.Error += ex => { _log("conn error: " + ex.Message); OnStatus?.Invoke("Error"); };
            _connection.Reconnecting += () => OnStatus?.Invoke("Reconnecting");
            _connection.Reconnected += () =>
            {
                OnStatus?.Invoke("Reconnected");
                // Fire-and-forget by design (Reconnected handler can't await), but
                // attach a continuation so a truly unobserved exception still surfaces.
                // ResubscribeAsync's own catch handles the normal Invoke-failed path.
                _ = ResubscribeAsync().ContinueWith(t =>
                {
                    _log("unhandled in resubscribe: " + t.Exception?.GetBaseException().Message);
                    OnStatus?.Invoke("ResubscribeFailed");
                }, TaskContinuationOptions.OnlyOnFaulted);
            };

            try
            {
                await _connection.Start();
                OnStatus?.Invoke("Connected");
                await ResubscribeAsync();
            }
            catch (Exception ex)
            {
                _log("start failed: " + ex.Message);
                OnStatus?.Invoke("StartFailed");
            }
        }

        private async Task ResubscribeAsync()
        {
            if (_proxy == null) return;
            try
            {
                var initial = await _proxy.Invoke<JObject>("Subscribe", new object[] { TopicNames.AllSubscribed });
                _log("Subscribed: " + string.Join(", ", TopicNames.AllSubscribed));
                // initial state for CarData is included in the subscription result keyed by topic
                if (initial.TryGetValue(TopicNames.CarData, out var carDataInitial)
                    && carDataInitial.Type == JTokenType.String)
                {
                    EmitFromCarData((string)carDataInitial!);
                }
                // Initial DriverList snapshot rides along with the subscribe response.
                if (initial.TryGetValue(TopicNames.DriverList, out var dlInitial)
                    && dlInitial.Type == JTokenType.Object)
                {
                    EmitFromDriverList(dlInitial.ToString());
                }
                // Initial TrackStatus snapshot — same shape as the MultiViewer
                // endpoint: { "Status":"1", "Message":"AllClear" }. Without this,
                // a session already under VSC/SC/Red when the plugin connects
                // would show "AllClear" until the next status change.
                if (initial.TryGetValue(TopicNames.TrackStatus, out var tsInitial)
                    && tsInitial.Type == JTokenType.Object)
                {
                    EmitFromTrackStatus(tsInitial.ToString());
                }
            }
            catch (Exception ex)
            {
                _log("subscribe failed: " + ex.Message);
                // Surface to consumers — otherwise UI shows "Reconnected" while
                // no telemetry actually flows (silent data loss after a blip).
                OnStatus?.Invoke("ResubscribeFailed");
            }
        }

        private void OnFeed(string topic, JToken data, string timestamp)
        {
            try
            {
                if (topic == TopicNames.CarData && data.Type == JTokenType.String)
                {
                    EmitFromCarData((string)data!);
                }
                else if (topic == TopicNames.DriverList && data.Type == JTokenType.Object)
                {
                    EmitFromDriverList(data.ToString());
                }
                else if (topic == TopicNames.TrackStatus && data.Type == JTokenType.Object)
                {
                    EmitFromTrackStatus(data.ToString());
                }
                // Future hooks: TimingAppData (ERS), LapCount, ExtrapolatedClock, etc.
            }
            catch (Exception ex)
            {
                _log($"feed parse error ({topic}): {ex.Message}");
            }
        }

        private void EmitFromCarData(string base64Deflate)
        {
            foreach (var snap in CarDataDecoder.ParseCarData(base64Deflate, _driverNumber))
            {
                OnSnapshot?.Invoke(snap);
            }
        }

        private void EmitFromDriverList(string json)
        {
            // TotalDrivers feeds the "P 14/22" display. The first DriverList
            // payload (initial subscribe response) is a full snapshot; later
            // feed events for DriverList are typically deltas containing only
            // the changed drivers. CountDrivers on a delta would shrink the
            // total to whatever's in the delta (e.g. 1), so we only ever raise
            // the cached value — never let a delta lower it.
            int n = DriverListDecoder.CountDrivers(json);
            if (n > _totalDrivers)
            {
                _totalDrivers = n;
                EmitSessionSnapshot();
                // Treat a snapshot that grows the known-driver count as the
                // authoritative "richer" picture and cache it for picker-driven
                // driver switches. The very first call (initial Subscribe
                // response) always lands here because _totalDrivers starts at
                // 0, so the full snapshot is what we cache. Subsequent deltas
                // typically have n=1 and are skipped — which is what we want,
                // since deltas don't contain identity for unchanged drivers.
                _driverListSnapshotJson = json;
            }

            if (_driverInfoEmitted) return;
            var info = DriverListDecoder.ParseDriverInfo(json, _driverNumber);
            if (info == null) return;
            if (info.LastName.Length == 0 && info.Tla.Length == 0) return;
            _driverInfoEmitted = true;
            _log($"DriverList resolved #{_driverNumber}: {info.Tla} {info.BroadcastName} ({info.TeamName})");
            OnDriverInfoSnapshot?.Invoke(info);
        }

        private void EmitFromTrackStatus(string json)
        {
            var (code, msg) = TrackStatusDecoder.Parse(json);
            // Decoder returns (0, "") on malformed input. Treat code 0 as a
            // sentinel for "parse failed / unknown" and don't clobber a valid
            // VSC/SC/Red state with it — the next valid feed will refresh us.
            if (code <= 0) return;
            if (code == _trackStatusCode && msg == _trackStatusMessage) return;
            _trackStatusCode = code;
            _trackStatusMessage = msg ?? "";
            _log($"TrackStatus: {code} ({_trackStatusMessage})");
            EmitSessionSnapshot();
        }

        private void EmitSessionSnapshot()
        {
            // The plugin's OnSessionSnapshot handler writes all SessionSnapshot
            // fields to SimHub properties. Lap/SessionTimeRemaining aren't yet
            // parsed from the SignalR feed (no LapCount/ExtrapolatedClock
            // topic handling), so they ride at their default empties — same as
            // they were before this method existed.
            OnSessionSnapshot?.Invoke(new SessionSnapshot
            {
                Utc = DateTime.UtcNow,
                TrackStatusCode = _trackStatusCode,
                TrackStatusMessage = _trackStatusMessage,
                TotalDrivers = _totalDrivers,
            });
        }

        public void SetDriverNumber(string driverNumber)
        {
            if (string.IsNullOrWhiteSpace(driverNumber)) return;
            string normalized = driverNumber.Trim();
            if (normalized == _driverNumber) return;
            string previous = _driverNumber;
            _driverNumber = normalized;
            // The SignalR subscription is broadcast (all drivers). On the next
            // CarData feed message we'll filter to the new number. Reset the
            // DriverInfo gate so the new driver's identity is re-resolved.
            _driverInfoEmitted = false;
            _log($"driver switch {previous} -> {normalized}");

            // Try to resolve the new driver's identity immediately from the
            // cached initial DriverList snapshot. Without this, the wheel
            // shows the "F1 LIVE" fallback until the next DriverList delta
            // happens to include the new driver — minutes-to-never during a
            // quiet practice session.
            var cached = _driverListSnapshotJson;
            if (cached != null) EmitFromDriverList(cached);
        }

        public void Dispose()
        {
            try { _connection?.Stop(); } catch { }
            _connection?.Dispose();
        }
    }
}
