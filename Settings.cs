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

        public string Source { get; set; } = "F1Live";

        /// <summary>
        /// RPM at which <c>RpmShiftPercent</c> reads 0%. Calibrated to real F1
        /// wheel LED bars where the first green LED comes on while rolling out
        /// of the pit lane. Default 5500 = visible greens during out-laps.
        /// </summary>
        public int RpmShiftLightStartRpm { get; set; } = 5500;

        /// <summary>
        /// RPM at which <c>RpmShiftPercent</c> reads 100% (full LED bar).
        /// Default 11500 = a typical fast-corner / DRS-straight peak rather
        /// than the regulation 15,000 ceiling that real cars rarely reach.
        /// </summary>
        public int RpmShiftLightEndRpm { get; set; } = 11500;

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
