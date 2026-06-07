using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive
{
    /// <summary>
    /// Runtime LED profile switcher. When MultiViewer starts, snapshots the user's
    /// current SimHub LED <c>activeProfileId</c> for each section ("leds", "buttons",
    /// "raw") of each supported device, then flips activeProfileId to our seeded
    /// F1SimHubLive profile. When MultiViewer stops, restores the snapshot — but
    /// only if the current selection is still ours, so a manual change made by the
    /// user during the MV session is preserved.
    ///
    /// <para>
    /// This eliminates the v1.4.x manual step where the user had to open
    /// SimHub &gt; Devices &gt; LEDs and pick "F1SimHubLive" for each section. With
    /// v1.5.0 runtime switching, the moment MV is detected the LEDs flip on their
    /// own (within ~5 seconds — one MvProcessPollMs cycle) and revert when MV closes.
    /// </para>
    ///
    /// <para>
    /// Writes are atomic (temp file + <see cref="File.Move(string,string)"/> overwrite)
    /// so SimHub's FileSystemWatcher sees a clean state every time. Same pattern the
    /// picker uses for our own settings file. Multi-device safe: iterates every
    /// device directory under <c>PluginsData\Common\Devices\</c> and skips any device
    /// that's not the supported GSI FPE V2.
    /// </para>
    ///
    /// <para>
    /// Snapshot persistence: kept in-memory only. SimHub restart while MV is up means
    /// the v1.5.0 plugin will see "MV is up, our profile already active" and do nothing
    /// on first poll — but the previous selection is lost. Acceptable trade-off; cross-
    /// restart snapshot persistence is a v1.5.x or later improvement.
    /// </para>
    /// </summary>
    internal sealed class LedRuntimeSwitcher
    {
        /// <summary>
        /// SimHub's stable DeviceTypeID for the GSI Formula Pro Elite V2 — the only
        /// wheel F1SimHubLive ships seeded profiles for. Must match the installer's
        /// <c>LedProfileSeederService.GsiFpeV2DeviceTypeId</c>.
        /// </summary>
        private const string GsiFpeV2DeviceTypeId = "EFC17674-559A-44DB-8D24-C6CFD203384D";

        /// <summary>
        /// LED section name → canonical profile Name we look up in that section.
        /// MUST stay in sync with the installer's <c>LedProfileSeederService.Seeds</c>.
        /// </summary>
        private static readonly (string Section, string ProfileName)[] Sections =
        {
            ("leds",    "F1SimHubLive - Telemetry"),
            ("buttons", "F1SimHubLive"),
            ("raw",     "F1SimHubLive - Prime Gradient"),
        };

        private readonly string _devicesRoot;
        private readonly Action<string> _log;

        // Per-(device, section) snapshot of the user's activeProfileId before we
        // flipped to ours. Key format: "<deviceInstanceId>|<section>".
        private readonly Dictionary<string, string?> _snapshot = new();

        public LedRuntimeSwitcher(string simhubInstallDir, Action<string> log)
        {
            _devicesRoot = Path.Combine(simhubInstallDir, "PluginsData", "Common", "Devices");
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Called by the plugin on every detected transition of MultiViewerRunning.
        /// True = MV just came up (snapshot + activate ours). False = MV just went
        /// away (restore snapshot if our profile is still active).
        /// </summary>
        public void OnMultiViewerRunningChanged(bool running)
        {
            try
            {
                if (!Directory.Exists(_devicesRoot))
                {
                    _log($"LedRuntimeSwitcher: devices root not found at {_devicesRoot} — skipping.");
                    return;
                }

                foreach (var dir in Directory.EnumerateDirectories(_devicesRoot))
                {
                    var settingsFile = Path.Combine(dir, "settings.json");
                    if (!File.Exists(settingsFile)) continue;

                    try
                    {
                        ProcessDevice(settingsFile, running);
                    }
                    catch (Exception ex)
                    {
                        _log($"LedRuntimeSwitcher: device '{Path.GetFileName(dir)}' failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"LedRuntimeSwitcher: unexpected error: {ex.Message}");
            }
        }

        /// <summary>
        /// v1.5.8 startup-time guarantee: walks every supported device's settings.json
        /// at plugin Init and force-activates our F1SimHubLive profile whenever the
        /// current selection is one of: empty, "Default*", orphan GUID (points to no
        /// profile in the array), or already ours. If the user has manually picked
        /// a real third-party profile, we leave it alone.
        /// <para>
        /// Why this exists: SimHub doesn't reload device settings.json while running
        /// (no FileSystemWatcher on that path), so the v1.5.0 runtime switcher's
        /// transition-on-MV-up write only takes effect on the *next* SimHub start.
        /// On Vic's Media PC, every fresh install left the wheel stuck on "Default"
        /// because the seeder's pre-v1.5.8 safety check incorrectly preserved an
        /// orphan activeProfileId pointing at SimHub's built-in default (which lives
        /// outside the enumerated Profiles[] array). This Init pass closes that hole
        /// at the plugin level too, so even a runtime install (without re-running
        /// the installer's seeder) recovers on the next SimHub start.
        /// </para>
        /// <para>
        /// Same atomic write pattern as <see cref="OnMultiViewerRunningChanged(bool)"/>
        /// — SimHub picks up the new value when it cold-reads on next start.
        /// </para>
        /// </summary>
        public void EnsureActiveOnStartup()
        {
            try
            {
                if (!Directory.Exists(_devicesRoot))
                {
                    _log($"LedRuntimeSwitcher: devices root not found at {_devicesRoot} — startup pass skipped.");
                    return;
                }

                foreach (var dir in Directory.EnumerateDirectories(_devicesRoot))
                {
                    var settingsFile = Path.Combine(dir, "settings.json");
                    if (!File.Exists(settingsFile)) continue;

                    try
                    {
                        EnsureActiveForDevice(settingsFile);
                    }
                    catch (Exception ex)
                    {
                        _log($"LedRuntimeSwitcher (startup): device '{Path.GetFileName(dir)}' failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"LedRuntimeSwitcher (startup): unexpected error: {ex.Message}");
            }
        }

        private void EnsureActiveForDevice(string settingsFile)
        {
            var raw = File.ReadAllText(settingsFile);
            var root = JObject.Parse(raw);

            var deviceTypeId = root.Value<string>("DeviceTypeID");
            if (!string.Equals(deviceTypeId, GsiFpeV2DeviceTypeId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var instanceId = root.Value<string>("InstanceId") ?? Path.GetFileName(Path.GetDirectoryName(settingsFile)) ?? "(unknown)";
            var ledsRoot = root["Settings"]?["LEDS"] as JObject;
            if (ledsRoot == null) return;

            bool changed = false;
            foreach (var (section, profileName) in Sections)
            {
                if (ledsRoot[section] is not JObject sectionObj) continue;
                if (sectionObj["Profiles"] is not JArray profiles) continue;

                var ourProfile = FindByName(profiles, profileName);
                if (ourProfile == null)
                {
                    // Installer hasn't seeded this section yet — nothing to switch to.
                    continue;
                }

                var ourProfileId = ourProfile.Value<string>("ProfileId");
                if (string.IsNullOrEmpty(ourProfileId)) continue;

                var currentActive = sectionObj.Value<string>("activeProfileId");

                // Already ours — no-op (and DON'T touch the file, so we don't trigger
                // a spurious SimHub-side reload on next start).
                if (string.Equals(currentActive, ourProfileId, StringComparison.OrdinalIgnoreCase)) continue;

                // Apply same safety check as the installer seeder: empty, orphan, or
                // Default* → safe to overwrite. Real user-chosen profile → leave alone.
                bool safeToActivate = string.IsNullOrEmpty(currentActive);
                string? currentActiveName = null;
                if (!safeToActivate)
                {
                    var currentProfile = FindById(profiles, currentActive!);
                    if (currentProfile == null)
                    {
                        safeToActivate = true; // orphan GUID — overwrite is safe
                    }
                    else
                    {
                        currentActiveName = currentProfile.Value<string>("Name");
                        if (currentActiveName != null && currentActiveName.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
                        {
                            safeToActivate = true;
                        }
                    }
                }

                if (!safeToActivate)
                {
                    _log($"LedRuntimeSwitcher (startup): device '{instanceId}' / section '{section}': preserving user's '{currentActiveName ?? currentActive}'.");
                    continue;
                }

                sectionObj["activeProfileId"] = ourProfileId;
                changed = true;
                _log($"LedRuntimeSwitcher (startup): device '{instanceId}' / section '{section}': activated F1SimHubLive (was '{currentActiveName ?? currentActive ?? "(unset)"}'). Takes effect on next SimHub start.");
            }

            if (changed)
            {
                WriteAtomic(settingsFile, root);
            }
        }

        private void ProcessDevice(string settingsFile, bool running)
        {
            var raw = File.ReadAllText(settingsFile);
            var root = JObject.Parse(raw);

            var deviceTypeId = root.Value<string>("DeviceTypeID");
            if (!string.Equals(deviceTypeId, GsiFpeV2DeviceTypeId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var instanceId = root.Value<string>("InstanceId") ?? Path.GetFileName(Path.GetDirectoryName(settingsFile)) ?? "(unknown)";
            var ledsRoot = root["Settings"]?["LEDS"] as JObject;
            if (ledsRoot == null)
            {
                _log($"LedRuntimeSwitcher: device '{instanceId}' has no Settings.LEDS — skipping.");
                return;
            }

            bool changed = false;
            foreach (var (section, profileName) in Sections)
            {
                if (ledsRoot[section] is not JObject sectionObj) continue;
                if (sectionObj["Profiles"] is not JArray profiles) continue;

                var ourProfile = FindByName(profiles, profileName);
                if (ourProfile == null)
                {
                    // Installer hasn't seeded this section yet (or user deleted ours).
                    // Skip silently; nothing to switch to.
                    continue;
                }

                var ourProfileId = ourProfile.Value<string>("ProfileId");
                if (string.IsNullOrEmpty(ourProfileId)) continue;

                var currentActive = sectionObj.Value<string>("activeProfileId");
                var snapshotKey = $"{instanceId}|{section}";

                if (running)
                {
                    // MV just came up: snapshot user's selection (only if it isn't already
                    // ours — don't snapshot ours and accidentally "restore" to ours later),
                    // then activate ours.
                    if (!string.Equals(currentActive, ourProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        _snapshot[snapshotKey] = currentActive;
                        sectionObj["activeProfileId"] = ourProfileId;
                        changed = true;
                        _log($"LedRuntimeSwitcher: device '{instanceId}' / section '{section}': MV up — snapshotted '{currentActive ?? "(unset)"}', activated F1SimHubLive.");
                    }
                }
                else
                {
                    // MV just went away: restore the snapshot, BUT only if the current
                    // selection is still ours. If the user manually picked a different
                    // profile during the MV session, leave their choice alone.
                    if (!string.Equals(currentActive, ourProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        _log($"LedRuntimeSwitcher: device '{instanceId}' / section '{section}': MV down — current '{currentActive}' is not ours, leaving user's manual choice alone.");
                        _snapshot.Remove(snapshotKey);
                        continue;
                    }

                    if (!_snapshot.TryGetValue(snapshotKey, out var previousId))
                    {
                        // No snapshot recorded — this can happen if SimHub restarted
                        // while MV was up. Best-effort: leave ours active. User can
                        // pick something else in SimHub UI when they want.
                        _log($"LedRuntimeSwitcher: device '{instanceId}' / section '{section}': MV down — no snapshot to restore, leaving F1SimHubLive active.");
                        continue;
                    }

                    sectionObj["activeProfileId"] = string.IsNullOrEmpty(previousId) ? null : (JToken)previousId!;
                    changed = true;
                    _snapshot.Remove(snapshotKey);
                    _log($"LedRuntimeSwitcher: device '{instanceId}' / section '{section}': MV down — restored '{previousId ?? "(unset)"}'.");
                }
            }

            if (changed)
            {
                WriteAtomic(settingsFile, root);
            }
        }

        private static JObject? FindByName(JArray profiles, string profileName)
        {
            foreach (var p in profiles)
            {
                if (p is JObject obj && string.Equals(obj.Value<string>("Name"), profileName, StringComparison.Ordinal))
                {
                    return obj;
                }
            }
            return null;
        }

        private static JObject? FindById(JArray profiles, string profileId)
        {
            foreach (var p in profiles)
            {
                if (p is JObject obj && string.Equals(obj.Value<string>("ProfileId"), profileId, StringComparison.OrdinalIgnoreCase))
                {
                    return obj;
                }
            }
            return null;
        }

        /// <summary>
        /// Same atomic-write pattern the picker uses for our settings file: write to
        /// a sibling temp file then File.Move overwrite. SimHub's FileSystemWatcher
        /// sees one clean atomic update, no half-written intermediate states.
        /// </summary>
        private static void WriteAtomic(string targetPath, JObject content)
        {
            var dir = Path.GetDirectoryName(targetPath) ?? ".";
            var tmp = Path.Combine(dir, Path.GetFileName(targetPath) + ".f1simhublive-tmp");
            var serialized = content.ToString(Formatting.None);
            File.WriteAllText(tmp, serialized, new UTF8Encoding(false));
            try
            {
                if (File.Exists(targetPath))
                {
                    File.Replace(tmp, targetPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmp, targetPath);
                }
            }
            catch
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { /* best effort cleanup */ }
                }
                throw;
            }
        }
    }
}
