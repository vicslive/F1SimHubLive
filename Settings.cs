using System;
using System.IO;
using Newtonsoft.Json;

namespace F1SimHubLive
{
    public sealed class Settings
    {
        private const string DefaultMultiViewerBaseUrl = "http://localhost:10101";

        public string DriverNumber { get; set; } = "44";
        public int OutputHz { get; set; } = 60;
        public int RenderDelayMs { get; set; } = 200;

        /// <summary>
        /// Extra latency (milliseconds) the LIVE telemetry is held back before it
        /// reaches the wheel/dash, so the data lines up with a delayed broadcast
        /// you're watching on a separate screen (e.g. Apple TV / F1 TV 4K, which
        /// runs several seconds behind the live timing feed). 0 (default) =
        /// today's behaviour, no extra hold. Only applied to the live sources
        /// (<c>F1Live</c> / <c>MultiViewer</c>); the <c>F1Replay</c> source ignores
        /// it because replay is anchored to the video manually. The picker exposes
        /// this as the "Live video delay" slider and hot-reloads it.
        /// </summary>
        public int BroadcastDelayMs { get; set; } = 0;

        public string Source { get; set; } = "F1Live";

        /// <summary>
        /// Static-archive session path for the <c>F1Replay</c> source, e.g.
        /// <c>2024/2024-09-01_Italian_Grand_Prix/2024-09-01_Race/</c>. Set by the
        /// picker's archive browser (written to the shared settings file) and read
        /// by <c>CreateClient</c> / <c>EnterReplay</c> when <c>Source</c> is
        /// <c>F1Replay</c>. Empty until a session is chosen.
        /// </summary>
        public string ReplaySessionPath { get; set; } = "";

        /// <summary>
        /// RPM at which <c>RpmShiftPercent</c> reads 0%. Calibrated to real F1
        /// wheel LED bars where the first green LED comes on while rolling out
        /// of the pit lane. Default 3500 (v1.5.2) = visible greens during
        /// out-laps and slow corners. Pre-1.5.2 default of 5500 was too high
        /// and pinned the bar to redline through most of a normal racing lap.
        /// </summary>
        public int RpmShiftLightStartRpm { get; set; } = 3500;

        /// <summary>
        /// RPM at which <c>RpmShiftPercent</c> reads 100% (full LED bar).
        /// Default 13000 (v1.5.2) — modern F1 V6 hybrid PUs routinely rev
        /// 12-14k on DRS straights, so the prior 11500 default saturated the
        /// bar to full redline almost constantly and the LEDs looked stuck on.
        /// The regulation ceiling is 15000 RPM but cars rarely sustain that.
        /// </summary>
        public int RpmShiftLightEndRpm { get; set; } = 13000;

        public string MultiViewerBaseUrl { get; set; } = DefaultMultiViewerBaseUrl;
        public int MultiViewerPollMs { get; set; } = 250;
        public int MultiViewerTimingPollMs { get; set; } = 1000;

        /// <summary>
        /// When true, the plugin launches F1SimHubLive-Picker.exe (deployed next
        /// to the plugin DLL) on Init so the driver-picker window pops up with
        /// SimHub. As of v1.3.0 the picker runs as <c>asInvoker</c> (no UAC
        /// prompt) and writes config to <c>%APPDATA%\F1SimHubLive\</c>, so
        /// auto-launch is now safe to leave on without nagging the user on
        /// every SimHub start.
        /// </summary>
        public bool AutoLaunchPicker { get; set; } = false;

        public static Settings Default => new();

        public static Settings Load(string path, Action<string>? log = null)
        {
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        loaded.Validate(log);
                        return loaded;
                    }
                    log?.Invoke($"settings load: parsed null from {path}; using defaults");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"settings load failed for {path}: {ex.Message}; using defaults");
            }
            return Default;
        }

        public void Save(string path)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        /// <summary>
        /// In-place sanity check after deserialization. Bad values get reset to
        /// safe defaults with a logged warning so a malformed or attacker-edited
        /// settings file can't redirect outbound HTTP to an arbitrary host.
        /// </summary>
        private void Validate(Action<string>? log)
        {
            if (!IsLoopbackHttpUrl(MultiViewerBaseUrl))
            {
                log?.Invoke(
                    $"settings: MultiViewerBaseUrl '{MultiViewerBaseUrl}' is not an http loopback URL; reverting to default '{DefaultMultiViewerBaseUrl}'");
                MultiViewerBaseUrl = DefaultMultiViewerBaseUrl;
            }
        }

        private static bool IsLoopbackHttpUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp) return false;
            return u.IsLoopback;
        }
    }
}
