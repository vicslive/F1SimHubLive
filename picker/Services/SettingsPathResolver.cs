using System;
using System.IO;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Single source of truth for where <c>F1SimHubLive.Settings.json</c> lives at
/// runtime. As of v1.3.0 the file is per-user under
/// <c>%APPDATA%\F1SimHubLive\</c> — the Windows-correct location for per-user
/// configuration.
///
/// <para>Why the move (from <c>C:\Program Files (x86)\SimHub\</c>):</para>
/// <list type="bullet">
///   <item>Writing into <c>Program Files</c> requires admin → the picker used
///         to ship with a <c>requireAdministrator</c> manifest, triggering a
///         UAC prompt every single launch. Other Windows apps (MultiViewer,
///         Discord, Steam, etc.) put per-user config in APPDATA precisely to
///         avoid this. Doing the same here lets the picker run as
///         <c>asInvoker</c> — no UAC, ever.</item>
///   <item>It also makes <c>AutoLaunchPicker=true</c> actually usable. Before
///         v1.3, that flag triggered a UAC prompt on every SimHub start;
///         now it's a quiet auto-launch.</item>
/// </list>
///
/// <para>Migration story for existing installs (v1.2.x and earlier):</para>
/// <list type="number">
///   <item>Per-user file exists at the new path → use it directly.</item>
///   <item>Per-user file missing, but the installer dropped a seed at
///         <c>%PROGRAMDATA%\F1SimHubLive\F1SimHubLive.Settings.json</c> →
///         copy seed to per-user path, then use per-user path.</item>
///   <item>Per-user file missing, no seed, but a legacy file exists at
///         <c>Program Files (x86)\SimHub\F1SimHubLive.Settings.json</c> →
///         copy legacy → per-user path. The legacy file is left in place
///         (we can't delete it without admin, and that's fine — we never
///         touch it again).</item>
///   <item>None of the above → return the per-user path; caller is responsible
///         for creating the file with defaults on first write.</item>
/// </list>
///
/// <para>Idempotent: calling <see cref="Resolve"/> repeatedly never duplicates
/// migration work because the per-user file is the first thing checked.</para>
/// </summary>
internal static class SettingsPathResolver
{
    private const string SettingsFileName = "F1SimHubLive.Settings.json";
    private const string AppFolderName = "F1SimHubLive";

    /// <summary>
    /// Returns the absolute path the rest of the app should read from / write
    /// to. Performs one-shot migration from legacy locations on first call
    /// after upgrade. Always returns the per-user path even if migration
    /// failed (caller's read/write call will then surface the real error).
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

    /// <summary>The canonical per-user path. Exposed for diagnostics / docs.</summary>
    public static string UserPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, AppFolderName, SettingsFileName);
    }

    /// <summary>
    /// Migration source paths, in priority order. Public so the plugin / tests
    /// can introspect what migration paths are in play.
    /// </summary>
    public static System.Collections.Generic.IEnumerable<string> SeedCandidates()
    {
        // 1) Installer-dropped seed in machine-wide PROGRAMDATA. Written by
        //    the installer (which runs as admin), readable by all users.
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(programData, AppFolderName, SettingsFileName);

        // 2) Legacy in-place location used by v1.2.x and earlier.
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(pf86, "SimHub", SettingsFileName);
    }
}
