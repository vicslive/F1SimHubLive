using System;
using System.Collections.Generic;
using System.IO;

namespace F1SimHubLive;

/// <summary>
/// Single source of truth for where <c>F1SimHubLive.Settings.json</c> lives at
/// runtime. As of v1.3.0 the file is per-user under
/// <c>%APPDATA%\F1SimHubLive\</c>.
///
/// <para>This is the plugin-side mirror of
/// <c>F1SimHubLive.Picker.Services.SettingsPathResolver</c>. Both must stay in
/// sync. We intentionally duplicate the ~80 lines instead of introducing a
/// shared project for a single file — the plugin and picker have completely
/// different dependency trees (one is a SimHub plugin DLL targeting net48,
/// the other is a WPF app targeting net8.0-windows).</para>
///
/// <para>Migration story (legacy → per-user) on first call:</para>
/// <list type="number">
///   <item>If per-user file exists, return it.</item>
///   <item>Otherwise copy from the first existing seed candidate:
///         <c>%PROGRAMDATA%\F1SimHubLive\F1SimHubLive.Settings.json</c>
///         (installer seed), then
///         <c>Program Files (x86)\SimHub\F1SimHubLive.Settings.json</c>
///         (v1.2.x and earlier).</item>
///   <item>The legacy file is left in place — we never delete it (no admin).
///         The plugin runs once per SimHub session, so re-doing the path
///         resolution every load is fine.</item>
/// </list>
/// </summary>
internal static class SettingsPathResolver
{
    private const string SettingsFileName = "F1SimHubLive.Settings.json";
    private const string AppFolderName = "F1SimHubLive";

    /// <summary>
    /// Returns the absolute path the plugin should read from / write to.
    /// Performs one-shot migration from legacy locations on first call after
    /// upgrade. Always returns the per-user path even if migration failed.
    /// </summary>
    /// <param name="log">Optional sink for migration breadcrumbs. Null = silent.</param>
    public static string Resolve(Action<string>? log = null)
    {
        string userPath = UserPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
        }
        catch (Exception ex)
        {
            log?.Invoke($"SettingsPathResolver: could not ensure {Path.GetDirectoryName(userPath)} exists: {ex.GetType().Name}: {ex.Message}");
        }

        if (File.Exists(userPath))
        {
            return userPath;
        }

        foreach (var seed in SeedCandidates())
        {
            if (!File.Exists(seed)) continue;
            try
            {
                File.Copy(seed, userPath, overwrite: false);
                log?.Invoke($"SettingsPathResolver: migrated settings from '{seed}' to '{userPath}'. The source file is no longer used and can be deleted manually if desired.");
                return userPath;
            }
            catch (Exception ex)
            {
                log?.Invoke($"SettingsPathResolver: migration from '{seed}' failed ({ex.GetType().Name}: {ex.Message}); trying next candidate.");
            }
        }

        return userPath;
    }

    /// <summary>The canonical per-user path.</summary>
    public static string UserPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, AppFolderName, SettingsFileName);
    }

    /// <summary>Migration source paths, in priority order.</summary>
    public static IEnumerable<string> SeedCandidates()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(programData, AppFolderName, SettingsFileName);

        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(pf86, "SimHub", SettingsFileName);
    }
}
