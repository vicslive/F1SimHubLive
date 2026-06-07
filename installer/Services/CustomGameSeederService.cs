using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace F1SimHubLive.Installer.Services;

/// <summary>
/// Result of seeding the F1SimHubLive custom game into SimHub's
/// <c>PluginsData\CustomGames.json</c>.
/// </summary>
public sealed class CustomGameSeedResult
{
    public required string SettingsFile { get; init; }

    /// <summary>True if we appended a new entry to <c>CustomGames.json</c>.</summary>
    public bool Inserted { get; init; }

    /// <summary>True if a matching entry was already present and we left it untouched.</summary>
    public bool AlreadyPresent { get; init; }

    /// <summary>
    /// Path SimHub will use for the "Launch Game" button. <c>null</c> if MultiViewer
    /// was not found in any standard install location at seed time (the custom game
    /// still works for process detection — only the Launch button is degraded).
    /// </summary>
    public string? MultiViewerExePath { get; init; }

    /// <summary>True when a settings.json backup file was written before modification.</summary>
    public string? BackupFile { get; init; }

    /// <summary>
    /// True when <see cref="Inserted"/> AND we set <c>UseAutomaticDetection = true</c>.
    /// Cosmetic field for log surfacing.
    /// </summary>
    public bool AutomaticSwitchingEnabled { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Creates SimHub's "F1SimHubLive" custom game entry in
/// <c>PluginsData\CustomGames.json</c> so SimHub auto-switches the active game
/// to F1SimHubLive whenever the MultiViewer for F1 process is detected.
///
/// <para>
/// Why this exists: SimHub has no native concept of MultiViewer — it's just an
/// Electron app feeding F1 telemetry through our plugin. Without a custom game,
/// SimHub stays attached to whatever real game it last saw (often Forza, AC,
/// iRacing), so any per-game LED / dashboard / motion settings the user has tied
/// to those titles bleed into F1-viewing sessions. With a custom game tied to
/// the <c>MultiViewer</c> process, SimHub has a clean "F1SimHubLive is active
/// now" pointer that other features can bind to.
/// </para>
///
/// <para>
/// SimHub stores custom games as an array in
/// <c>&lt;install&gt;\PluginsData\CustomGames.json</c>. The file holds zero or
/// more entries; SimHub-generated <c>Code</c> fields look like
/// <c>Custom_&lt;guid&gt;</c> and are referenced by the
/// <c>LastGame</c> field in <c>ContextSimhubSettings.json</c> and by
/// <c>LastGameProfiles</c> entries on each device's LED settings.
/// </para>
///
/// <para>
/// <b>Idempotency contract:</b> this service inspects existing entries
/// and matches on the <c>Name</c> field (case-insensitive). If a custom game
/// named "F1SimHubLive" is already present — whether seeded by us or hand-created
/// by the user — we DO NOT touch it. This preserves any user customizations
/// (alternate process names, custom <c>StartPath</c>, motion mappings, manual
/// flips to <c>UseProcessDetectionToActivateGame</c>, etc.).
/// </para>
///
/// <para>
/// <b>Deterministic Code GUID:</b> the seeded entry always uses
/// <c>Custom_</c><see cref="DeterministicCode"/>. This means every F1SimHubLive
/// install on every machine references the same custom game identifier — useful
/// later if we ever ship a defaults file that pre-binds the F1SimHubLive
/// custom game to our LED / dashboard profiles via SimHub's per-game settings.
/// </para>
///
/// <para>
/// <b>MultiViewer auto-detect:</b> we look for MultiViewer.exe in standard install
/// locations (<c>%LOCALAPPDATA%\multiviewer</c> and registry uninstall entries).
/// If found, <c>StartPath</c> is populated so the SimHub "Launch Game" button works.
/// If not found we leave <c>StartPath</c> null — process detection still works the
/// moment the user launches MultiViewer manually.
/// </para>
///
/// <para>
/// <b>SimHub must be closed during seed:</b> SimHub buffers
/// <c>CustomGames.json</c> writes in memory and only flushes them on clean
/// shutdown. The installer already stops SimHub before deploy (see
/// <c>Deployer.MaybeStopSimHub</c>), so any change we make here will be the
/// canonical state when SimHub restarts.
/// </para>
/// </summary>
public sealed class CustomGameSeederService
{
    /// <summary>The display name shown in SimHub's title bar and Games tab.</summary>
    public const string TargetGameName = "F1SimHubLive";

    /// <summary>
    /// The process name SimHub watches to auto-switch into our custom game.
    /// No <c>.exe</c> extension. Single name (no semicolon) — MultiViewer is the
    /// only relevant process. Users who want a fallback (e.g. a second MV-style
    /// app) can extend this in the UI; we won't overwrite their edit on re-install.
    /// </summary>
    public const string MultiViewerProcessName = "MultiViewer";

    /// <summary>
    /// Deterministic GUID portion of the custom game's <c>Code</c> field. Every
    /// install of F1SimHubLive uses this same identifier so cross-machine
    /// configuration and per-game bindings stay stable.
    /// </summary>
    public const string DeterministicCode = "f15ec0de-f1f1-f1f1-f1f1-f15ecf15ecf1";

    /// <summary>
    /// Reads <c>&lt;install&gt;\PluginsData\CustomGames.json</c>, appends an
    /// F1SimHubLive entry if not already present, and writes the file back.
    /// Idempotent — safe to call on every install or upgrade.
    /// </summary>
    public CustomGameSeedResult SeedCustomGame(string simHubInstallDir, Action<string>? log = null)
    {
        var pluginsData = Path.Combine(simHubInstallDir, "PluginsData");
        var settingsPath = Path.Combine(pluginsData, "CustomGames.json");

        try
        {
            // Make sure PluginsData exists. SimHub creates this on first run so it
            // should always be present on a real install, but if the user pointed us
            // at a fresh checkout we should not crash.
            if (!Directory.Exists(pluginsData))
            {
                Directory.CreateDirectory(pluginsData);
            }

            JsonArray games;
            if (File.Exists(settingsPath))
            {
                var raw = File.ReadAllText(settingsPath);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    games = new JsonArray();
                }
                else
                {
                    var node = JsonNode.Parse(raw);
                    if (node is JsonArray arr)
                    {
                        games = arr;
                    }
                    else
                    {
                        // Unexpected shape — log and bail rather than overwrite a file
                        // we don't understand. The user can manually create the custom
                        // game from the SimHub UI.
                        log?.Invoke($"Custom game seed: {settingsPath} is not a JSON array (root is {node?.GetType().Name ?? "null"}); skipping to avoid clobbering unknown shape.");
                        return new CustomGameSeedResult
                        {
                            SettingsFile = settingsPath,
                            Error = "CustomGames.json root is not a JSON array",
                        };
                    }
                }
            }
            else
            {
                games = new JsonArray();
            }

            // Idempotency check — match on Name case-insensitively. If anyone
            // (us or the user) has already created an "F1SimHubLive" entry,
            // leave it alone.
            foreach (var entry in games)
            {
                if (entry is not JsonObject obj) continue;
                var name = obj["Name"]?.GetValue<string>();
                if (string.Equals(name, TargetGameName, StringComparison.OrdinalIgnoreCase))
                {
                    var existingStart = obj["StartPath"]?.GetValue<string>();
                    log?.Invoke($"Custom game seed: '{TargetGameName}' already present in {Path.GetFileName(settingsPath)} - leaving user configuration intact.");
                    return new CustomGameSeedResult
                    {
                        SettingsFile = settingsPath,
                        AlreadyPresent = true,
                        MultiViewerExePath = existingStart,
                    };
                }
            }

            var multiViewerPath = FindMultiViewerExe();
            if (multiViewerPath != null)
            {
                log?.Invoke($"Custom game seed: MultiViewer detected at '{multiViewerPath}' - SimHub Launch Game button will work.");
            }
            else
            {
                log?.Invoke("Custom game seed: MultiViewer not found in standard install locations - StartPath will be null (process detection still works the moment you launch MultiViewer manually).");
            }

            var newEntry = BuildEntry(multiViewerPath);
            games.Add(newEntry);

            string? backup = null;
            if (File.Exists(settingsPath))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                backup = $"{settingsPath}.preCustomGameSeed-{stamp}";
                File.Copy(settingsPath, backup, overwrite: false);
                log?.Invoke($"Custom game seed: backed up CustomGames.json -> {Path.GetFileName(backup)}");
            }

            var serialized = games.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(settingsPath, serialized, new UTF8Encoding(false));

            log?.Invoke($"Custom game seed: appended '{TargetGameName}' (Code=Custom_{DeterministicCode}, ProcessNames={MultiViewerProcessName}, UseAutomaticDetection=true) to CustomGames.json.");

            return new CustomGameSeedResult
            {
                SettingsFile = settingsPath,
                Inserted = true,
                MultiViewerExePath = multiViewerPath,
                AutomaticSwitchingEnabled = true,
                BackupFile = backup,
            };
        }
        catch (Exception ex)
        {
            log?.Invoke($"Custom game seed: failed - {ex.Message}");
            return new CustomGameSeedResult
            {
                SettingsFile = settingsPath,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// Builds the JSON object SimHub expects for a custom game entry. Shape and
    /// field set reverse-engineered from a manually-created entry on Dev box
    /// (2026-06-07). All <c>InputsToTelemetrySettings</c> values match SimHub's
    /// defaults for a fresh custom game.
    /// </summary>
    private static JsonObject BuildEntry(string? startPath)
    {
        var entry = new JsonObject
        {
            ["Name"] = TargetGameName,
            ["Code"] = $"Custom_{DeterministicCode}",
            ["StartPath"] = startPath, // may be null - SimHub accepts that
            ["Arguments"] = null,
            ["WorkingDirectory"] = null,
            ["UseAutomaticDetection"] = true,
            ["UseProcessDetectionToActivateGame"] = false,
            ["ProcessNames"] = MultiViewerProcessName,
            ["InputsToTelemetrySettings"] = BuildDefaultMotionSettings(),
        };
        return entry;
    }

    /// <summary>
    /// SimHub appears to require the <c>InputsToTelemetrySettings</c> block to be
    /// fully present (not just <c>{}</c>) even when no motion mapping is wired.
    /// This is the all-nulls / defaults shape captured from a freshly-created
    /// custom game on Dev box.
    /// </summary>
    private static JsonObject BuildDefaultMotionSettings()
    {
        static JsonObject EmptyAxis() => new()
        {
            ["AxisName"] = null,
            ["AxisMovement"] = 0,
        };

        return new JsonObject
        {
            ["ActionGearShift"] = new JsonObject(),
            ["Down"] = EmptyAxis(),
            ["Front"] = EmptyAxis(),
            ["Rear"] = EmptyAxis(),
            ["SurgeFront"] = EmptyAxis(),
            ["SurgeRear"] = EmptyAxis(),
            ["FrontRearSmoothing"] = 300.0,
            ["SurgeFrontRearSmoothing"] = 300.0,
            ["FrontRearToPitchDegrees"] = 0.0,
            ["FrontRearToSurgeMS"] = 10.0,
            ["LeftRightToSwayMS"] = 10.0,
            ["LeftRightSmoothing"] = 100.0,
            ["SwayLeftRightSmoothing"] = 100.0,
            ["LeftRightToRollDegrees"] = 0.0,
            ["TLDegrees"] = 10.0,
            ["ReverseLeftRight"] = false,
            ["ReverseLeftRightSway"] = false,
            ["Left"] = EmptyAxis(),
            ["Right"] = EmptyAxis(),
            ["SwayLeft"] = EmptyAxis(),
            ["SwayRight"] = EmptyAxis(),
            ["Throttle"] = EmptyAxis(),
            ["Up"] = EmptyAxis(),
            ["TLLeft"] = EmptyAxis(),
            ["TLRight"] = EmptyAxis(),
            ["YawLeft"] = EmptyAxis(),
            ["YawRight"] = EmptyAxis(),
            ["YawSmoothing"] = 300.0,
            ["TLSmoothing"] = 300.0,
            ["UpDownSmoothing"] = 300.0,
            ["UpDownToHeaveMS"] = 10.0,
            ["ReverseTL"] = false,
            ["SeparateSway"] = false,
            ["SeparateSurge"] = false,
            ["YawSpeed"] = 5.0,
        };
    }

    /// <summary>
    /// Returns the full path to <c>MultiViewer.exe</c> if a standard install is
    /// detected, or <c>null</c>. Scan order:
    /// <list type="number">
    ///   <item><c>%LOCALAPPDATA%\multiviewer\MultiViewer.exe</c> — Squirrel/Electron default</item>
    ///   <item>Windows Uninstall registry entries with <c>DisplayName</c> matching "MultiViewer"</item>
    ///   <item><c>%ProgramFiles%\MultiViewer\MultiViewer.exe</c> — system-wide fallback</item>
    /// </list>
    /// </summary>
    private static string? FindMultiViewerExe()
    {
        try
        {
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "multiviewer",
                "MultiViewer.exe");
            if (File.Exists(local)) return local;

            var registryPath = TryGetMultiViewerPathFromRegistry();
            if (registryPath != null && File.Exists(registryPath)) return registryPath;

            var progFiles = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "MultiViewer",
                "MultiViewer.exe");
            if (File.Exists(progFiles)) return progFiles;
        }
        catch
        {
            // Detection is best-effort. If anything throws (permissions on a
            // registry hive, missing folder, etc.) we treat MultiViewer as
            // not detected — the custom game still seeds with null StartPath
            // and process detection still works at runtime.
        }
        return null;
    }

    /// <summary>
    /// Walks the standard Uninstall registry hives looking for MultiViewer's
    /// install location, then returns <c>&lt;InstallLocation&gt;\MultiViewer.exe</c>
    /// if it can be constructed. Returns <c>null</c> if not found in registry.
    /// </summary>
    private static string? TryGetMultiViewerPathFromRegistry()
    {
        var hives = new (RegistryHive Hive, RegistryView View, string Path)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, view, subPath) in hives)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(subPath);
                if (uninstallKey == null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var sub = uninstallKey.OpenSubKey(subKeyName);
                    if (sub == null) continue;
                    var displayName = sub.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(displayName)) continue;
                    if (!displayName.Equals("MultiViewer", StringComparison.OrdinalIgnoreCase)) continue;

                    var installLocation = sub.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(installLocation))
                    {
                        var candidate = Path.Combine(installLocation, "MultiViewer.exe");
                        if (File.Exists(candidate)) return candidate;
                    }

                    var displayIcon = sub.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrEmpty(displayIcon))
                    {
                        // DisplayIcon often points at app.ico in the install root
                        var iconDir = Path.GetDirectoryName(displayIcon);
                        if (!string.IsNullOrEmpty(iconDir))
                        {
                            var candidate = Path.Combine(iconDir, "MultiViewer.exe");
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
            }
            catch
            {
                // Skip this hive on any access error and try the next one.
            }
        }

        return null;
    }
}
