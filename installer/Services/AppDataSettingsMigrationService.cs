using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F1SimHubLive.Installer.Services;

/// <summary>
/// Result of one APPDATA settings.json migration attempt.
/// </summary>
public sealed class AppDataSettingsMigrationChange
{
    public required string SettingsPath { get; init; }
    public required bool Modified { get; init; }
    public string? BackupFile { get; init; }
    public string? Reason { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Patches per-user <c>F1SimHubLive.Settings.json</c> files that the running plugin
/// and picker actually read. The installer historically only writes the machine-wide
/// PROGRAMDATA seed under the assumption that <see cref="SettingsPathResolver"/> in
/// the plugin/picker will copy that seed into APPDATA on first launch.
///
/// <para><b>The bug fixed here (v1.5.3):</b> once an APPDATA file exists, the resolver
/// uses it directly and NEVER re-seeds from PROGRAMDATA again. So when v1.5.2 changed
/// the default <c>RpmShiftLight</c> range from <c>(5500, 11500)</c> to <c>(3500, 13000)</c>
/// and added in-installer migration logic, the installer correctly produced a new
/// PROGRAMDATA seed -- but every user who already had an APPDATA file from a v1.5.x
/// install kept reading the old saturated <c>(5500, 11500)</c> values. The wheel LEDs
/// stayed pinned to white-flash redline through normal racing RPM.</para>
///
/// <para>This service runs after <c>WriteSettings()</c> and walks every user profile's
/// APPDATA settings file directly. The installer is elevated, so it has the permissions
/// to write into other users' profiles. Files are mutated in-place with a timestamped
/// backup; only the two <c>RpmShiftLight*</c> values are touched and only when they
/// match the exact pre-1.5.2 default pair -- any other value pair is treated as an
/// intentional customization and preserved.</para>
/// </summary>
public sealed class AppDataSettingsMigrationService
{
    private const string AppFolderName = "F1SimHubLive";
    private const string SettingsFileName = "F1SimHubLive.Settings.json";

    /// <summary>Pre-1.5.2 default pair. Only this exact pair gets rewritten.</summary>
    public const int OldDefaultStartRpm = 5500;
    public const int OldDefaultEndRpm = 11500;

    /// <summary>v1.5.2 tuned defaults (Vic's hand-tuned values, shipped to all users).</summary>
    public const int NewDefaultStartRpm = 3500;
    public const int NewDefaultEndRpm = 13000;

    /// <summary>
    /// Enumerate every per-user APPDATA settings file on this machine and apply the
    /// v1.5.2 RpmShiftLight migration to each.
    /// </summary>
    /// <param name="log">Optional log callback so the installer UI can surface progress.</param>
    /// <returns>One entry per candidate file the service looked at.</returns>
    public List<AppDataSettingsMigrationChange> MigrateAllUserProfiles(Action<string>? log = null)
    {
        var changes = new List<AppDataSettingsMigrationChange>();

        string? usersRoot = ResolveUsersRoot();
        if (usersRoot == null || !Directory.Exists(usersRoot))
        {
            log?.Invoke($"APPDATA migration: could not resolve user-profiles root (looked for '{usersRoot ?? "(null)"}'); skipping.");
            return changes;
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        IEnumerable<string> profileDirs;
        try
        {
            profileDirs = Directory.EnumerateDirectories(usersRoot);
        }
        catch (Exception ex)
        {
            log?.Invoke($"APPDATA migration: enumerating '{usersRoot}' failed ({ex.GetType().Name}: {ex.Message}); skipping.");
            return changes;
        }

        foreach (var profile in profileDirs)
        {
            string profileName = Path.GetFileName(profile);
            // Skip well-known service / pseudo profiles. These won't have an F1SimHubLive
            // install under them and probing them just generates Access Denied noise.
            if (IsSystemProfile(profileName)) continue;

            string settingsPath = Path.Combine(profile, "AppData", "Roaming", AppFolderName, SettingsFileName);
            if (!File.Exists(settingsPath)) continue;

            try
            {
                var change = MigrateOne(settingsPath, stamp, log);
                changes.Add(change);
            }
            catch (Exception ex)
            {
                log?.Invoke($"APPDATA migration: '{settingsPath}' failed ({ex.GetType().Name}: {ex.Message}).");
                changes.Add(new AppDataSettingsMigrationChange
                {
                    SettingsPath = settingsPath,
                    Modified = false,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                });
            }
        }

        return changes;
    }

    private AppDataSettingsMigrationChange MigrateOne(string settingsPath, string stamp, Action<string>? log)
    {
        string raw = File.ReadAllText(settingsPath);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return new AppDataSettingsMigrationChange
            {
                SettingsPath = settingsPath,
                Modified = false,
                Reason = "file did not parse as JSON",
            };
        }

        if (node is not JsonObject obj)
        {
            return new AppDataSettingsMigrationChange
            {
                SettingsPath = settingsPath,
                Modified = false,
                Reason = "JSON root was not an object",
            };
        }

        // Read existing RpmShiftLight values. Treat missing or non-numeric as not-old-default
        // so we never accidentally rewrite a file that doesn't explicitly carry the old pair.
        if (!TryReadInt(obj, "RpmShiftLightStartRpm", out int existingStart)
            || !TryReadInt(obj, "RpmShiftLightEndRpm", out int existingEnd))
        {
            return new AppDataSettingsMigrationChange
            {
                SettingsPath = settingsPath,
                Modified = false,
                Reason = "RpmShiftLight fields missing or non-numeric (leaving for plugin default fallback)",
            };
        }

        if (existingStart != OldDefaultStartRpm || existingEnd != OldDefaultEndRpm)
        {
            return new AppDataSettingsMigrationChange
            {
                SettingsPath = settingsPath,
                Modified = false,
                Reason = $"existing values ({existingStart}, {existingEnd}) are not the pre-1.5.2 default pair — preserved as intentional customization",
            };
        }

        // Patch — atomic via sibling temp file + replace, with timestamped backup.
        string backup = $"{settingsPath}.preAppDataMigration-{stamp}";
        File.Copy(settingsPath, backup, overwrite: false);

        obj["RpmShiftLightStartRpm"] = NewDefaultStartRpm;
        obj["RpmShiftLightEndRpm"] = NewDefaultEndRpm;

        string updated = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string tempPath = settingsPath + ".tmp";
        File.WriteAllText(tempPath, updated, new UTF8Encoding(false));
        File.Move(tempPath, settingsPath, overwrite: true);

        log?.Invoke(
            $"APPDATA migration: patched '{settingsPath}' "
            + $"RpmShiftLight ({OldDefaultStartRpm}, {OldDefaultEndRpm}) -> ({NewDefaultStartRpm}, {NewDefaultEndRpm}) "
            + $"[backup: {Path.GetFileName(backup)}]");

        return new AppDataSettingsMigrationChange
        {
            SettingsPath = settingsPath,
            Modified = true,
            BackupFile = backup,
            Reason = $"upgraded ({OldDefaultStartRpm}, {OldDefaultEndRpm}) -> ({NewDefaultStartRpm}, {NewDefaultEndRpm})",
        };
    }

    private static bool TryReadInt(JsonObject obj, string key, out int value)
    {
        value = 0;
        if (!obj.TryGetPropertyValue(key, out var node) || node == null) return false;
        if (node is JsonValue v && v.TryGetValue(out int parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the absolute path to the directory containing all user profile folders
    /// (typically <c>C:\Users</c>). Resolves via SystemDrive so non-default OS installs
    /// on D:\ still work.
    /// </summary>
    private static string? ResolveUsersRoot()
    {
        string? sysDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrWhiteSpace(sysDrive)) return null;
        return Path.Combine(sysDrive + Path.DirectorySeparatorChar, "Users");
    }

    private static bool IsSystemProfile(string name)
    {
        // Skip the obvious built-in / synthetic profiles. Comparison is case-insensitive
        // because Windows itself is.
        string[] reserved =
        {
            "Default",
            "Default User",
            "Public",
            "All Users",
            "WDAGUtilityAccount",
            "defaultuser0",
            "Administrator", // very rarely has personal F1SimHubLive data; skipping is safe
        };
        foreach (var r in reserved)
        {
            if (string.Equals(name, r, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // Hidden / dotted directories
        if (name.StartsWith(".", StringComparison.Ordinal)) return true;
        return false;
    }
}
