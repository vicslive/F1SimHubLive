using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace F1SimHubLive.Installer.Services;

public sealed class DeployOptions
{
    public required string SimHubInstallDir { get; init; }
    public required int DriverNumber { get; init; }
    public required string Source { get; init; } // "F1Live" or "MultiViewer"
    public string MultiViewerBaseUrl { get; init; } = "http://localhost:10101";
    public int MultiViewerPollMs { get; init; } = 250;
    public int MultiViewerTimingPollMs { get; init; } = 1000;
    public bool RestartSimHub { get; init; } = true;

    /// <summary>
    /// When true, the deployer extracts F1SimHubLive-Picker.exe next to the plugin
    /// DLL and creates a Start Menu shortcut so the user can launch the driver
    /// picker with one click during a race.
    /// </summary>
    public bool InstallPicker { get; init; } = true;

    /// <summary>
    /// Written to settings.json as <c>AutoLaunchPicker</c>. The plugin reads this
    /// at Init and spawns the picker on every SimHub start when true. Default
    /// is off, but as of v1.3.0 the picker runs as <c>asInvoker</c> (no UAC)
    /// and reads/writes its config from <c>%APPDATA%\F1SimHubLive\</c>, so
    /// turning this on is fully unattended — no UAC prompt on SimHub launch.
    /// </summary>
    public bool AutoLaunchPicker { get; init; } = false;

    /// <summary>
    /// When true, the deployer flips every SimHub device's
    /// <c>CurrentIdleDashboard</c> to F1RaceSim_GSIFPEV2 (with a timestamped backup).
    /// When false, the deployer leaves the user's idle dashboard alone and the
    /// Done page surfaces a manual-setup warning.
    /// </summary>
    public bool SetIdleDashboard { get; init; } = true;
}

public sealed class Deployer
{
    public event Action<string>? Log;
    public event Action<int>? Progress;

    private readonly IdleDashboardService _idle = new();
    private readonly LedConfigRewireService _ledRewire = new();
    private readonly LedProfileSeederService _ledSeeder = new();
    private readonly AppDataSettingsMigrationService _appDataMigration = new();
    private readonly CustomGameSeederService _customGameSeeder = new();

    /// <summary>
    /// Per-device idle-dashboard changes recorded during the last deploy. Empty when
    /// <see cref="DeployOptions.SetIdleDashboard"/> is false.
    /// </summary>
    public List<IdleDashboardChange> LastIdleDashboardChanges { get; private set; } = new();

    /// <summary>
    /// Per-device LED-config plugin-name rewire results recorded during the last deploy.
    /// Empty list means no devices were scanned; entries with <c>Modified=false</c> and
    /// <c>OccurrencesReplaced=0</c> mean the device was already clean.
    /// </summary>
    public List<LedRewireChange> LastLedRewireChanges { get; private set; } = new();

    /// <summary>
    /// Per-device LED-profile seed results recorded during the last deploy. Empty
    /// when no SimHub devices were found; entries with <c>Matched=false</c> are
    /// devices that aren't on the supported-wheels list.
    /// </summary>
    public List<LedProfileSeedChange> LastLedProfileSeedChanges { get; private set; } = new();

    /// <summary>
    /// Per-user APPDATA settings.json migration results recorded during the last deploy
    /// (v1.5.3+). Empty when no APPDATA settings file exists on any user profile.
    /// </summary>
    public List<AppDataSettingsMigrationChange> LastAppDataMigrationChanges { get; private set; } = new();

    /// <summary>
    /// Result of seeding SimHub's <c>PluginsData\CustomGames.json</c> with our
    /// F1SimHubLive custom game (v1.7.0+). <c>null</c> until <see cref="DeployAsync"/>
    /// runs. <see cref="CustomGameSeedResult.AlreadyPresent"/> is true on every install
    /// after the first one.
    /// </summary>
    public CustomGameSeedResult? LastCustomGameSeedResult { get; private set; }

    private void L(string msg) => Log?.Invoke(msg);
    private void P(int pct) => Progress?.Invoke(pct);

    public async Task DeployAsync(DeployOptions opts)
    {
        L($"Target SimHub directory: {opts.SimHubInstallDir}");

        await Task.Run(() => MaybeStopSimHub()).ConfigureAwait(false);
        P(10);

        var dashDir = Path.Combine(opts.SimHubInstallDir, "DashTemplates", "F1RaceSim_GSIFPEV2");
        Directory.CreateDirectory(dashDir);

        var pluginDest = Path.Combine(opts.SimHubInstallDir, "F1SimHubLive.dll");
        ReportExistingPluginVersion(pluginDest);

        L("Copying plugin DLLs...");
        ExtractResourceTo("F1SimHubLive.dll", pluginDest);
        ExtractResourceTo("Microsoft.AspNet.SignalR.Client.dll", Path.Combine(opts.SimHubInstallDir, "Microsoft.AspNet.SignalR.Client.dll"));
        ReportNewlyInstalledPluginVersion(pluginDest);
        P(40);

        if (opts.InstallPicker)
        {
            L("Copying Driver Picker...");
            string pickerDest = Path.Combine(opts.SimHubInstallDir, "F1SimHubLive-Picker.exe");
            TryExtractResourceTo("F1SimHubLive-Picker.exe", pickerDest);
            if (File.Exists(pickerDest))
            {
                CreatePickerShortcut(pickerDest);
            }
        }
        else
        {
            L("Driver Picker install skipped (user opted out).");
        }
        P(50);

        L("Copying F1RaceSim_GSIFPEV2 dashboard files...");
        ExtractResourceTo("F1RaceSim_GSIFPEV2.djson", Path.Combine(dashDir, "F1RaceSim_GSIFPEV2.djson"));
        ExtractResourceTo("F1RaceSim_GSIFPEV2.djson.ressources", Path.Combine(dashDir, "F1RaceSim_GSIFPEV2.djson.ressources"));
        ExtractResourceTo("F1RaceSim_GSIFPEV2.djson.metadata", Path.Combine(dashDir, "F1RaceSim_GSIFPEV2.djson.metadata"));
        ExtractResourceTo("F1RaceSim_GSIFPEV2.djson.png", Path.Combine(dashDir, "F1RaceSim_GSIFPEV2.djson.png"));
        ExtractResourceTo("F1RaceSim_GSIFPEV2.djson.00.png", Path.Combine(dashDir, "F1RaceSim_GSIFPEV2.djson.00.png"));
        P(75);

        L($"Writing Settings.json for driver #{opts.DriverNumber} (source: {opts.Source})...");
        WriteSettings(opts);
        P(85);

        // v1.5.3: WriteSettings only updates the PROGRAMDATA seed. The plugin and picker
        // actually read from per-user APPDATA, which the resolver only seeds from PROGRAMDATA
        // on FIRST launch -- after that it sticks. So every user upgrading from v1.4.x / v1.5.0
        // / v1.5.1 had old pre-1.5.2 defaults baked into their APPDATA and the v1.5.2 "tuned
        // defaults" never reached them. This walks every user profile's APPDATA and migrates
        // any pre-1.5.2 RpmShiftLight default pair in-place.
        L("");
        L("Migrating per-user APPDATA settings (v1.5.2 RpmShiftLight defaults)...");
        LastAppDataMigrationChanges = _appDataMigration.MigrateAllUserProfiles(L);
        int patchedProfiles = LastAppDataMigrationChanges.Count(c => c.Modified);
        if (patchedProfiles > 0)
        {
            L($"APPDATA migration: patched RpmShiftLight defaults in {patchedProfiles} user profile(s). "
                + $"Wheel LEDs will stop saturating to white-flash redline on next plugin reload.");
        }
        else if (LastAppDataMigrationChanges.Count == 0)
        {
            L("APPDATA migration: no per-user settings files found (fresh install, nothing to migrate).");
        }
        else
        {
            L("APPDATA migration: scanned per-user settings files, no pre-1.5.2 default pairs found.");
        }

        L("");
        L("Scanning per-device LED configurations for stale plugin-name references...");
        LastLedRewireChanges = _ledRewire.RewireEverywhere(opts.SimHubInstallDir, L);
        var rewiredDevices = 0;
        var rewiredTotal = 0;
        foreach (var c in LastLedRewireChanges)
        {
            if (!c.Modified) continue;
            rewiredDevices++;
            rewiredTotal += c.OccurrencesReplaced;
        }
        if (rewiredDevices > 0)
        {
            L($"LED config rewire: patched {rewiredTotal} reference(s) across {rewiredDevices} device(s).");
        }
        else
        {
            L("LED config rewire: no stale references found.");
        }

        L("");
        L("Seeding F1 Live LED profiles (Telemetry / Buttons / Individual) on supported wheels...");
        LastLedProfileSeedChanges = _ledSeeder.SeedEverywhere(opts.SimHubInstallDir, L);
        var seededDevices = 0;
        var seededProfiles = 0;
        var seededActivated = 0;
        foreach (var c in LastLedProfileSeedChanges)
        {
            if (!c.Modified) continue;
            seededDevices++;
            seededProfiles += c.ProfilesInserted;
            seededActivated += c.SectionsActivated;
        }
        if (seededDevices > 0)
        {
            L($"LED profile seed: inserted {seededProfiles} profile(s) and activated {seededActivated} section(s) across {seededDevices} device(s).");
        }
        else if (LastLedProfileSeedChanges.Any(c => c.Matched))
        {
            L("LED profile seed: every supported wheel already has the F1 Live profiles installed and active.");
        }
        else
        {
            L("LED profile seed: no supported wheel found (the dashboard LCD area will work, but the wheel LEDs will only show Default Profile).");
        }
        P(90);

        L("");
        L("Seeding F1SimHubLive custom game in SimHub (so SimHub auto-switches to F1SimHubLive when MultiViewer launches)...");
        LastCustomGameSeedResult = _customGameSeeder.SeedCustomGame(opts.SimHubInstallDir, L);
        if (LastCustomGameSeedResult.Inserted)
        {
            var mvNote = LastCustomGameSeedResult.MultiViewerExePath != null
                ? "MultiViewer install detected - SimHub Launch Game button will work."
                : "MultiViewer not detected in standard install locations - launch MultiViewer manually and process detection will still flip SimHub to F1SimHubLive automatically.";
            L($"Custom game seed: F1SimHubLive added to SimHub Settings > Custom games. {mvNote}");
        }
        else if (LastCustomGameSeedResult.AlreadyPresent)
        {
            L("Custom game seed: F1SimHubLive custom game already present - left untouched (your edits are safe).");
        }
        else if (LastCustomGameSeedResult.Error != null)
        {
            L($"Custom game seed: skipped - {LastCustomGameSeedResult.Error}");
        }

        if (opts.SetIdleDashboard)
        {
            L("");
            L("Setting F1RaceSim_GSIFPEV2 as the SimHub idle dashboard on every connected device...");
            LastIdleDashboardChanges = _idle.SetIdleDashboardEverywhere(
                opts.SimHubInstallDir,
                IdleDashboardService.TargetDashboardName,
                L);
        }
        else
        {
            L("");
            L("Idle-dashboard change skipped (user opted out).");
            L("You must open SimHub > Dash Studio > select your device > pick 'F1RaceSim_GSIFPEV2' as the idle dashboard for the dash to show up automatically.");
            LastIdleDashboardChanges = new List<IdleDashboardChange>();
        }
        P(95);

        if (opts.RestartSimHub)
        {
            L("Starting SimHub...");
            StartSimHub(opts.SimHubInstallDir);
        }
        P(100);
        L("Deployment complete.");
    }

    private void MaybeStopSimHub()
    {
        var procs = Process.GetProcessesByName("SimHubWPF");
        if (procs.Length == 0) return;
        L($"Stopping {procs.Length} running SimHub process(es)...");
        foreach (var p in procs)
        {
            try { p.CloseMainWindow(); } catch { }
        }
        Task.Delay(1500).Wait();
        foreach (var p in Process.GetProcessesByName("SimHubWPF"))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }
    }

    private void StartSimHub(string dir)
    {
        var exe = Path.Combine(dir, "SimHubWPF.exe");
        if (!File.Exists(exe)) { L("SimHubWPF.exe not found — skipping start."); return; }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            L($"Failed to start SimHub: {ex.Message}");
        }
    }

    private void WriteSettings(DeployOptions opts)
    {
        // v1.3.0+: settings file lives per-user under %APPDATA%\F1SimHubLive\,
        // but the installer runs elevated and we have no clean way to write to
        // a non-admin user's APPDATA. So the installer drops a *seed* file in
        // the machine-wide PROGRAMDATA location. The plugin and picker both
        // copy that seed into the per-user APPDATA on first launch (after which
        // the per-user copy becomes authoritative). See SettingsPathResolver.cs.
        string programDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "F1SimHubLive");
        Directory.CreateDirectory(programDataDir);
        var settingsPath = Path.Combine(programDataDir, "F1SimHubLive.Settings.json");

        // Preservation: look at every place an existing F1SimHubLive install
        // might already have settings, in order of authoritativeness:
        //   1. The elevated user's per-user APPDATA (post-v1.3.0)
        //   2. The PROGRAMDATA seed from a prior v1.3.x install
        //   3. The legacy in-SimHub-dir file (v1.2.x and earlier)
        // We use the FIRST one found to preserve user-tunable fields like
        // AutoLaunchPicker, OutputHz, RpmShiftLight*. This stops the installer
        // from blowing user customizations back to wizard defaults on upgrade.
        // See .signing-runbook.md "Action items deferred" and the 2026-06-05
        // working-memory entry.
        string[] preservationCandidates =
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "F1SimHubLive",
                "F1SimHubLive.Settings.json"),
            settingsPath,
            Path.Combine(opts.SimHubInstallDir, "F1SimHubLive.Settings.json"),
        };

        bool? existingAutoLaunch = null;
        int? existingOutputHz = null;
        int? existingRenderDelayMs = null;
        int? existingRpmShiftStart = null;
        int? existingRpmShiftEnd = null;
        foreach (var candidate in preservationCandidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                var root = doc.RootElement;
                if (root.TryGetProperty("AutoLaunchPicker", out var a))
                {
                    if (a.ValueKind == JsonValueKind.True) existingAutoLaunch = true;
                    else if (a.ValueKind == JsonValueKind.False) existingAutoLaunch = false;
                }
                if (root.TryGetProperty("OutputHz", out var o)
                    && o.ValueKind == JsonValueKind.Number
                    && o.TryGetInt32(out var hz)) existingOutputHz = hz;
                if (root.TryGetProperty("RenderDelayMs", out var r)
                    && r.ValueKind == JsonValueKind.Number
                    && r.TryGetInt32(out var delay)) existingRenderDelayMs = delay;
                if (root.TryGetProperty("RpmShiftLightStartRpm", out var ss)
                    && ss.ValueKind == JsonValueKind.Number
                    && ss.TryGetInt32(out var startRpm)) existingRpmShiftStart = startRpm;
                if (root.TryGetProperty("RpmShiftLightEndRpm", out var se)
                    && se.ValueKind == JsonValueKind.Number
                    && se.TryGetInt32(out var endRpm)) existingRpmShiftEnd = endRpm;

                // v1.5.2 migration: the pre-1.5.2 default pair was (5500, 11500),
                // which is too narrow for modern F1 V6 hybrid PUs that routinely
                // rev 12-14k RPM on DRS straights. The result was a wheel LED bar
                // pinned to full redline / white flash through most of a lap,
                // with non-sequential gradient fills because RPM exceeded the
                // ceiling. Any user who is STILL on that exact default pair
                // never tuned them through the picker -- treat them as "unset"
                // so they fall through to the new (3500, 13000) tuned defaults.
                // Any other value pair is treated as an intentional customization
                // and preserved as-is.
                if (existingRpmShiftStart == 5500 && existingRpmShiftEnd == 11500)
                {
                    L("Detected pre-1.5.2 default RpmShiftLight pair (5500, 11500) -- "
                        + "this is too narrow for modern F1 PUs and causes the LED bar "
                        + "to saturate to redline. Upgrading to v1.5.2 tuned defaults (3500, 13000). "
                        + "Use the picker to adjust if you prefer different values.");
                    existingRpmShiftStart = null;
                    existingRpmShiftEnd = null;
                }

                L($"Existing settings found at '{candidate}' — preserving "
                    + $"AutoLaunchPicker={existingAutoLaunch?.ToString() ?? "(unset)"}, "
                    + $"OutputHz={existingOutputHz?.ToString() ?? "(unset)"}, "
                    + $"RenderDelayMs={existingRenderDelayMs?.ToString() ?? "(unset)"}, "
                    + $"RpmShiftLightStartRpm={existingRpmShiftStart?.ToString() ?? "(unset)"}, "
                    + $"RpmShiftLightEndRpm={existingRpmShiftEnd?.ToString() ?? "(unset)"}");
                break;
            }
            catch (Exception ex)
            {
                L($"Existing settings at '{candidate}' could not be parsed ({ex.GetType().Name}: {ex.Message}) — trying next candidate.");
            }
        }

        var settings = new
        {
            DriverNumber = opts.DriverNumber,
            OutputHz = existingOutputHz ?? 60,
            RenderDelayMs = existingRenderDelayMs ?? 0,
            Source = opts.Source,
            MultiViewerBaseUrl = opts.MultiViewerBaseUrl,
            MultiViewerPollMs = opts.MultiViewerPollMs,
            MultiViewerTimingPollMs = opts.MultiViewerTimingPollMs,
            AutoLaunchPicker = existingAutoLaunch ?? opts.AutoLaunchPicker,
            RpmShiftLightStartRpm = existingRpmShiftStart ?? 3500,
            RpmShiftLightEndRpm = existingRpmShiftEnd ?? 13000,
        };
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsPath, json, new UTF8Encoding(false));
        L($"Wrote settings seed -> {settingsPath}");
    }

    /// <summary>
    /// Creates an All-Users Start Menu shortcut (.lnk) pointing at the deployed
    /// Driver Picker exe. Best-effort — failures are logged but never block the
    /// install. Uses WScript.Shell COM via reflection so we don't take a
    /// dependency on IWshRuntimeLibrary just for this.
    /// </summary>
    private void CreatePickerShortcut(string pickerExePath)
    {
        try
        {
            string commonStart = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            string folder = Path.Combine(commonStart, "Programs", "F1SimHubLive");
            Directory.CreateDirectory(folder);
            string shortcut = Path.Combine(folder, "F1SimHubLive Driver Picker.lnk");

            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null)
            {
                L("Could not create Start Menu shortcut: WScript.Shell unavailable.");
                return;
            }
            dynamic shell = Activator.CreateInstance(t)!;
            try
            {
                dynamic sc = shell.CreateShortcut(shortcut);
                sc.TargetPath = pickerExePath;
                sc.WorkingDirectory = Path.GetDirectoryName(pickerExePath) ?? "";
                sc.IconLocation = pickerExePath + ",0";
                sc.Description = "Switch the watched F1 driver live for F1SimHubLive";
                sc.WindowStyle = 1;
                sc.Save();
                L($"Created Start Menu shortcut: {shortcut}");
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            L($"Start Menu shortcut creation failed (non-fatal): {ex.Message}");
        }
    }

    private void ReportExistingPluginVersion(string pluginPath)
    {
        if (!File.Exists(pluginPath))
        {
            L("No prior F1SimHubLive.dll found — fresh install.");
            return;
        }
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(pluginPath);
            var existing = string.IsNullOrWhiteSpace(fvi.FileVersion) ? "unknown" : fvi.FileVersion;
            L($"Existing F1SimHubLive.dll detected — version {existing}.");
        }
        catch (Exception ex)
        {
            L($"Could not read existing plugin version: {ex.Message}");
        }
    }

    private void ReportNewlyInstalledPluginVersion(string pluginPath)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(pluginPath);
            var fresh = string.IsNullOrWhiteSpace(fvi.FileVersion) ? "unknown" : fvi.FileVersion;
            L($"Installed F1SimHubLive.dll version {fresh}.");
        }
        catch
        {
            // Non-fatal.
        }
    }

    private static void ExtractResourceTo(string assetName, string destPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(assetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded resource not found: {assetName}");
        using var src = asm.GetManifestResourceStream(resName)!;
        using var dst = File.Create(destPath);
        src.CopyTo(dst);
    }

    /// <summary>
    /// Same as <see cref="ExtractResourceTo"/> but logs and returns false on
    /// missing-resource instead of throwing. Used for optional payloads (the
    /// Driver Picker) so the installer remains usable even on a build where
    /// the picker publish step was skipped.
    /// </summary>
    private bool TryExtractResourceTo(string assetName, string destPath)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(assetName, StringComparison.OrdinalIgnoreCase));
            if (resName == null)
            {
                L($"Optional resource '{assetName}' is not embedded in this installer build; skipping.");
                return false;
            }
            using var src = asm.GetManifestResourceStream(resName)!;
            using var dst = File.Create(destPath);
            src.CopyTo(dst);
            return true;
        }
        catch (Exception ex)
        {
            L($"Could not extract '{assetName}' to '{destPath}': {ex.Message}");
            return false;
        }
    }
}
