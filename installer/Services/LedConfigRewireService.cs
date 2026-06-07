using System.IO;
using System.Text;

namespace F1SimHubLive.Installer.Services;

/// <summary>
/// Result of rewiring legacy plugin-name references in one SimHub device's settings.json.
/// </summary>
public sealed class LedRewireChange
{
    public required string InstanceId { get; init; }
    public required string DisplayName { get; init; }
    public required string SettingsFile { get; init; }
    public int OccurrencesReplaced { get; init; }
    public required bool Modified { get; init; }
    public string? BackupFile { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Rewires stale legacy-plugin-name references inside each SimHub device's
/// <c>settings.json</c> (in <c>PluginsData\Common\Devices\&lt;guid&gt;\</c>).
///
/// Background: the F1 plugin was renamed twice during development:
///   <c>F1SimSubGSIPlugin</c> -> <c>F1SimHubGSIPlugin</c> -> <c>F1SimHubLivePlugin</c>.
/// Dashboard files were repointed during each rename, but per-device LED configurations
/// stored under <c>PluginsData\Common\Devices\&lt;guid&gt;\settings.json</c> were NOT
/// touched. When the old plugin DLLs are uninstalled, the LED enable/value formulas that
/// still reference <c>F1SimSubGSIPlugin.RpmPercent</c> (etc.) silently evaluate to 0,
/// and the wheel LEDs blink white with no RPM gradient.
///
/// This service finds those stale references and rewrites them to
/// <c>F1SimHubLivePlugin.</c>, writing a timestamped backup before mutating each file.
/// It is idempotent: if no legacy references are found in a device's settings.json,
/// the file is left untouched and no backup is created.
///
/// Editing strategy: a token-level string replace on the raw file bytes. The legacy
/// plugin-name strings only appear inside NCalc expressions that are stored as JSON
/// string values (never as JSON keys), so collision with structural JSON syntax is
/// impossible and a JSON round-trip is unnecessary.
/// </summary>
public sealed class LedConfigRewireService
{
    public const string TargetPluginPrefix = "F1SimHubLivePlugin.";

    /// <summary>Legacy plugin-name prefixes to rewrite, in order of historical age.</summary>
    public static readonly string[] LegacyPluginPrefixes =
    {
        "F1SimSubGSIPlugin.",
        "F1SimHubGSIPlugin.",
    };

    /// <summary>
    /// Surgical (old, new) rewrites applied after prefix rewrites. Use for narrow,
    /// known-buggy bindings where a broad prefix replace would over-match.
    /// </summary>
    /// <remarks>
    /// Background: seed assets shipped in v1.4.0 / v1.4.1 / v1.5.0 carried a typo in the
    /// Telemetry and Prime Gradient master-gate triggers — <c>F1SimHubLive.MultiViewerRunning</c>
    /// instead of the correct <c>F1SimHubLivePlugin.MultiViewerRunning</c>. Properties in
    /// SimHub are exposed under the plugin <em>class name</em> (<c>F1SimHubLivePlugin</c>),
    /// not the <c>[PluginName]</c> attribute value (<c>F1SimHubLive</c>). The typo killed
    /// the master gate on every fresh install of those versions; all child LED containers
    /// cascaded dark. Fixed in seeds v1.5.1+; this rewrite heals existing installs in place.
    /// A broad <c>F1SimHubLive.</c> prefix replace is intentionally avoided here because
    /// it could collide with hypothetical future settings keys (defensive).
    /// </remarks>
    public static readonly (string From, string To)[] SpecificRewrites =
    {
        ("F1SimHubLive.MultiViewerRunning", "F1SimHubLivePlugin.MultiViewerRunning"),
    };

    /// <summary>
    /// Scans every SimHub device's <c>settings.json</c> under
    /// <c>&lt;simHubInstallDir&gt;\PluginsData\Common\Devices\</c>, replaces any legacy
    /// plugin-name references with <see cref="TargetPluginPrefix"/>, and writes a
    /// timestamped backup before mutating each touched file.
    /// </summary>
    /// <param name="simHubInstallDir">SimHub install directory.</param>
    /// <param name="log">Optional log callback so the installer UI can surface progress.</param>
    public List<LedRewireChange> RewireEverywhere(
        string simHubInstallDir,
        Action<string>? log = null)
    {
        var changes = new List<LedRewireChange>();
        var devicesRoot = Path.Combine(simHubInstallDir, "PluginsData", "Common", "Devices");
        if (!Directory.Exists(devicesRoot))
        {
            log?.Invoke("No SimHub devices directory found — skipping LED config rewire.");
            return changes;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        foreach (var dir in Directory.EnumerateDirectories(devicesRoot))
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            if (!File.Exists(settingsPath)) continue;

            var instanceId = Path.GetFileName(dir);
            var displayName = instanceId;
            try
            {
                var raw = File.ReadAllText(settingsPath);

                // Pull a friendlier display name out of the file when available.
                displayName = TryReadDisplayName(raw, instanceId);

                var totalCount = 0;
                var patched = raw;
                foreach (var legacy in LegacyPluginPrefixes)
                {
                    var c = CountOccurrences(patched, legacy);
                    if (c == 0) continue;
                    totalCount += c;
                    patched = patched.Replace(legacy, TargetPluginPrefix);
                }
                foreach (var (from, to) in SpecificRewrites)
                {
                    var c = CountOccurrences(patched, from);
                    if (c == 0) continue;
                    totalCount += c;
                    patched = patched.Replace(from, to);
                }

                if (totalCount == 0)
                {
                    log?.Invoke($"Device '{displayName}': no legacy plugin references in LED config — no change.");
                    changes.Add(new LedRewireChange
                    {
                        InstanceId = instanceId,
                        DisplayName = displayName,
                        SettingsFile = settingsPath,
                        OccurrencesReplaced = 0,
                        Modified = false,
                    });
                    continue;
                }

                var backup = $"{settingsPath}.preLedRewire-{stamp}";
                File.Copy(settingsPath, backup, overwrite: false);
                log?.Invoke($"Device '{displayName}': backed up settings.json -> {Path.GetFileName(backup)}");

                File.WriteAllText(settingsPath, patched, new UTF8Encoding(false));

                log?.Invoke(
                    $"Device '{displayName}': rewired {totalCount} legacy plugin reference(s) -> {TargetPluginPrefix}*.");

                changes.Add(new LedRewireChange
                {
                    InstanceId = instanceId,
                    DisplayName = displayName,
                    SettingsFile = settingsPath,
                    OccurrencesReplaced = totalCount,
                    Modified = true,
                    BackupFile = backup,
                });
            }
            catch (Exception ex)
            {
                log?.Invoke($"Device '{displayName}': LED config rewire failed — {ex.Message}");
                changes.Add(new LedRewireChange
                {
                    InstanceId = instanceId,
                    DisplayName = displayName,
                    SettingsFile = settingsPath,
                    OccurrencesReplaced = 0,
                    Modified = false,
                    Error = ex.Message,
                });
            }
        }

        return changes;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// Best-effort name extraction from a settings.json blob without parsing the whole
    /// file as JSON. Falls back to <paramref name="fallback"/> if no name field is found.
    /// </summary>
    private static string TryReadDisplayName(string raw, string fallback)
    {
        foreach (var key in new[] { "\"CustomName\"", "\"AutomaticName\"", "\"DeviceTypeName\"" })
        {
            var idx = raw.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) continue;
            var colon = raw.IndexOf(':', idx + key.Length);
            if (colon < 0) continue;
            var openQuote = raw.IndexOf('"', colon + 1);
            if (openQuote < 0) continue;
            var closeQuote = raw.IndexOf('"', openQuote + 1);
            if (closeQuote < 0) continue;
            var name = raw.Substring(openQuote + 1, closeQuote - openQuote - 1);
            if (!string.IsNullOrWhiteSpace(name) && name != "null") return name;
        }
        return fallback;
    }
}
