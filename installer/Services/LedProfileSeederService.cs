using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F1SimHubLive.Installer.Services;

/// <summary>
/// Result of seeding F1 Live LED profiles into one SimHub device's settings.json.
/// </summary>
public sealed class LedProfileSeedChange
{
    public required string InstanceId { get; init; }
    public required string DisplayName { get; init; }
    public required string SettingsFile { get; init; }
    public required bool Matched { get; init; }       // device type matched a supported wheel
    public int ProfilesInserted { get; init; }        // number of {leds,buttons,raw} profiles freshly added
    public int ProfilesAlreadyPresent { get; init; }  // number already there by Name (idempotent skip)
    public int SectionsActivated { get; init; }       // sections where we flipped activeProfileId to ours
    public int SectionsSwitchingModeFixed { get; init; } // sections where we forced ProfileSwitchingMode=1 (Disabled)
    public bool Modified { get; init; }
    public string? BackupFile { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Seeds the three custom <c>F1SimHubLive</c> LED profiles into every supported SimHub
/// wheel's <c>PluginsData\Common\Devices\&lt;guid&gt;\settings.json</c>.
///
/// Why this exists: the F1RaceSim_GSIFPEV2 dashboard (LCD area) drives the screen,
/// but the wheel's actual LEDs are configured in a completely separate section of
/// the device's settings.json (<c>Settings.LEDS.{leds,buttons,raw}.Profiles</c>).
/// SimHub ships with a single <c>Default Profile</c> per section that only animates
/// while a Game is running — and F1SimHubLive deliberately has no game running
/// (telemetry flows in via the plugin from F1 MultiViewer's local API or F1's
/// live-timing SignalR). On a fresh install, the LEDs area shows only "Default
/// Profile" and the wheel stays dark.
///
/// The three seeded profiles fire their LedContainers on
/// <c>if([F1SimHubLive.MultiViewerRunning] = 1, 1, 0)</c> — i.e. when MultiViewer
/// is running on the machine, signalling the user is in F1-viewing mode. When
/// MultiViewer is closed (gaming, idle, anything else), the trigger evaluates
/// false and the LEDs go dark, leaving the wheel free for other SimHub profiles.
///
/// Section-by-section:
///   - <c>leds</c>    "F1SimHubLive - Telemetry"          — RPM shift-light bar
///   - <c>buttons</c> "F1SimHubLive"                      — button static colors
///   - <c>raw</c>     "F1SimHubLive - Prime Gradient"     — individual-LED gradient
///
/// This service is idempotent: it matches existing profiles by <c>Name</c> and
/// skips re-insertion. It also generates a fresh <c>ProfileId</c> on insert so
/// the dev-box GUID never leaks to multiple installs (which would risk SimHub's
/// internal cache treating two physically different profiles as the same one).
///
/// <para>
/// <b>activeProfileId safety:</b> we only flip the section's <c>activeProfileId</c>
/// to ours when the current selection is empty or points to SimHub's built-in
/// "Default Profile". If the user has selected their own racing profile (Forza,
/// iRacing, AC, etc.) we leave it alone — the user manually picks F1SimHubLive
/// when they want to use it. This prevents the installer from overwriting an
/// existing gaming setup.
/// </para>
/// </summary>
/// Supported wheel: GSI Formula Pro Elite V2 only (<see cref="GsiFpeV2DeviceTypeId"/>).
/// Other wheels are skipped with a log message; adding support requires capturing
/// the equivalent profile shape from a working install of that wheel.
/// </summary>
public sealed class LedProfileSeederService
{
    /// <summary>
    /// SimHub's static device-type identifier for the GSI Formula Pro Elite V2.
    /// Same on every install of this wheel model (in contrast to <c>InstanceId</c>
    /// which is per-install).
    /// </summary>
    public const string GsiFpeV2DeviceTypeId = "EFC17674-559A-44DB-8D24-C6CFD203384D";

    private const string ProfileIdPlaceholder = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// Per-section seed plan: which section in <c>Settings.LEDS</c>, which embedded
    /// resource to load, and the canonical <c>Name</c> we match on for idempotency.
    /// </summary>
    private static readonly (string Section, string AssetFileName, string ProfileName)[] Seeds =
    {
        ("leds",    "leds-F1SimHubLive-Telemetry.json",      "F1SimHubLive - Telemetry"),
        ("buttons", "buttons-F1SimHubLive.json",             "F1SimHubLive"),
        ("raw",     "raw-F1SimHubLive-PrimeGradient.json",   "F1SimHubLive - Prime Gradient"),
    };

    /// <summary>
    /// Scans every SimHub device's <c>settings.json</c>. For each device that matches
    /// a supported wheel, inserts any missing F1 Live profiles into the corresponding
    /// section and sets <c>activeProfileId</c> to our profile so the wheel lights up
    /// without the user having to pick it manually. Writes a timestamped backup
    /// before mutating a file.
    /// </summary>
    public List<LedProfileSeedChange> SeedEverywhere(
        string simHubInstallDir,
        Action<string>? log = null)
    {
        var changes = new List<LedProfileSeedChange>();
        var devicesRoot = Path.Combine(simHubInstallDir, "PluginsData", "Common", "Devices");
        if (!Directory.Exists(devicesRoot))
        {
            log?.Invoke("No SimHub devices directory found - skipping LED profile seed.");
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
                var root = JsonNode.Parse(raw) as JsonObject;
                if (root == null) continue;

                var deviceTypeId = root["DeviceTypeID"]?.GetValue<string>();
                displayName = root["CustomName"]?.GetValue<string>()
                              ?? root["AutomaticName"]?.GetValue<string>()
                              ?? root["DeviceTypeName"]?.GetValue<string>()
                              ?? instanceId;

                if (!string.Equals(deviceTypeId, GsiFpeV2DeviceTypeId, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"Device '{displayName}': not a supported wheel (DeviceTypeID={deviceTypeId ?? "(null)"}) - skipping LED profile seed.");
                    changes.Add(new LedProfileSeedChange
                    {
                        InstanceId = instanceId,
                        DisplayName = displayName,
                        SettingsFile = settingsPath,
                        Matched = false,
                        Modified = false,
                    });
                    continue;
                }

                if (root["Settings"] is not JsonObject settings || settings["LEDS"] is not JsonObject ledsRoot)
                {
                    log?.Invoke($"Device '{displayName}': no Settings.LEDS section - skipping.");
                    changes.Add(new LedProfileSeedChange
                    {
                        InstanceId = instanceId,
                        DisplayName = displayName,
                        SettingsFile = settingsPath,
                        Matched = true,
                        Modified = false,
                        Error = "no Settings.LEDS section",
                    });
                    continue;
                }

                int inserted = 0, already = 0, activated = 0, switchingModeFixed = 0;
                foreach (var (section, assetName, profileName) in Seeds)
                {
                    if (ledsRoot[section] is not JsonObject sectionObj)
                    {
                        log?.Invoke($"Device '{displayName}': section '{section}' missing or null - skipping that section.");
                        continue;
                    }

                    if (sectionObj["Profiles"] is not JsonArray profiles)
                    {
                        profiles = new JsonArray();
                        sectionObj["Profiles"] = profiles;
                    }

                    var existing = FindByName(profiles, profileName);
                    string targetProfileId;
                    if (existing != null)
                    {
                        already++;
                        targetProfileId = existing["ProfileId"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
                        log?.Invoke($"Device '{displayName}' / section '{section}': profile '{profileName}' already present - skipping insert.");
                    }
                    else
                    {
                        var fresh = LoadSeedProfile(assetName);
                        targetProfileId = Guid.NewGuid().ToString();
                        fresh["ProfileId"] = targetProfileId;
                        profiles.Add(fresh);
                        inserted++;
                        log?.Invoke($"Device '{displayName}' / section '{section}': inserted profile '{profileName}' (ProfileId={targetProfileId}).");
                    }

                    var currentActive = sectionObj["activeProfileId"]?.GetValue<string>();
                    // Safety: do NOT clobber a user's existing racing profile selection.
                    // Only flip activeProfileId to ours if the current selection is:
                    //   - null/empty (fresh install), OR
                    //   - the SimHub built-in "Default Profile" (user has never customized), OR
                    //   - a GUID that doesn't resolve to anything in the Profiles array
                    //     (orphaned reference — SimHub sometimes leaves activeProfileId
                    //     pointing at an implicit/built-in default that doesn't enumerate;
                    //     symptom on Media PC: every fresh install left the wheel stuck on
                    //     "Default" until the user manually re-picked F1SimHubLive).
                    // Otherwise the user has consciously picked a profile (Forza, AC, iRacing, etc.)
                    // and our IDLE-mode F1 LEDs would overwrite their racing setup — bad.
                    // Users with their own profile can manually switch to ours via SimHub UI.
                    bool safeToActivate = string.IsNullOrEmpty(currentActive);
                    string? currentActiveName = null;
                    if (!safeToActivate)
                    {
                        var currentProfile = FindById(profiles, currentActive!);
                        currentActiveName = currentProfile?["Name"]?.GetValue<string>();
                        if (currentProfile == null)
                        {
                            // GUID points to nothing the user can possibly have selected on
                            // purpose — treat as orphan/built-in default, safe to overwrite.
                            safeToActivate = true;
                            log?.Invoke($"Device '{displayName}' / section '{section}': activeProfileId '{currentActive}' is not in Profiles[] (orphan or built-in Default) — treating as safe to activate.");
                        }
                        else if (currentActiveName != null && currentActiveName.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
                        {
                            safeToActivate = true;
                        }
                    }

                    if (string.Equals(currentActive, targetProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        // Already active — but still enforce ProfileSwitchingMode=1 below.
                    }
                    else if (safeToActivate)
                    {
                        sectionObj["activeProfileId"] = targetProfileId;
                        activated++;
                        log?.Invoke($"Device '{displayName}' / section '{section}': activeProfileId set to '{targetProfileId}' ('{profileName}').");
                    }
                    else
                    {
                        log?.Invoke($"Device '{displayName}' / section '{section}': existing active profile '{currentActiveName ?? currentActive}' preserved. F1SimHubLive profile installed but not auto-activated. To use it, open SimHub > Devices > LEDs > '{section}' and select '{profileName}'.");
                        // User has their own racing profile selected — don't touch their
                        // ProfileSwitchingMode either. Their per-game switching is intentional.
                        continue;
                    }

                    // v1.6.0: When OUR profile is the active one in this section, force
                    // ProfileSwitchingMode = 1 ("Disabled" in the SimHub UI's "Automatic
                    // profile switching" radio group). In Mode 2 ("Last selected profile,
                    // per game") SimHub IGNORES activeProfileId and uses LastGameProfiles
                    // [currentGame] instead — which on most installs maps to "Default Profile".
                    // Symptom on Vic's Media PC pre-v1.6.0: every SimHub restart showed
                    // Default Profile in the dropdown despite activeProfileId pointing
                    // at F1SimHubLive. Dev box was fine because it was already on Mode 1.
                    if (EnsureSwitchingModeDisabled(sectionObj))
                    {
                        switchingModeFixed++;
                        log?.Invoke($"Device '{displayName}' / section '{section}': set ProfileSwitchingMode=1 (Disabled) so SimHub respects activeProfileId across restarts.");
                    }
                }

                if (inserted == 0 && activated == 0 && switchingModeFixed == 0)
                {
                    log?.Invoke($"Device '{displayName}': all F1 Live profiles already present, active, and ProfileSwitchingMode=Disabled - no change.");
                    changes.Add(new LedProfileSeedChange
                    {
                        InstanceId = instanceId,
                        DisplayName = displayName,
                        SettingsFile = settingsPath,
                        Matched = true,
                        ProfilesInserted = 0,
                        ProfilesAlreadyPresent = already,
                        SectionsActivated = 0,
                        SectionsSwitchingModeFixed = 0,
                        Modified = false,
                    });
                    continue;
                }

                var backup = $"{settingsPath}.preLedProfileSeed-{stamp}";
                File.Copy(settingsPath, backup, overwrite: false);
                log?.Invoke($"Device '{displayName}': backed up settings.json -> {Path.GetFileName(backup)}");

                var serialized = root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = false,
                });
                File.WriteAllText(settingsPath, serialized, new UTF8Encoding(false));

                // v1.5.9 defensive: ensure BUILTIN\Users has Modify rights on the
                // settings.json. If the installer was run elevated, the file may
                // inherit tighter ACLs that exclude the regular user account that
                // SimHub runs as — SimHub then silently fails to persist UI picks
                // on close, and on next start re-reads the stale activeProfileId.
                // (Vic's Media PC symptom: pick F1 profile, close SimHub, reopen,
                // back to GSI default. Dev box was fine because its file had Users:FullControl
                // from an earlier non-elevated install path.)
                TryEnsureUsersCanWrite(settingsPath, log);

                log?.Invoke(
                    $"Device '{displayName}': LED profile seed complete - inserted={inserted}, already-present={already}, activated={activated}, switching-mode-fixed={switchingModeFixed}.");

                changes.Add(new LedProfileSeedChange
                {
                    InstanceId = instanceId,
                    DisplayName = displayName,
                    SettingsFile = settingsPath,
                    Matched = true,
                    ProfilesInserted = inserted,
                    ProfilesAlreadyPresent = already,
                    SectionsActivated = activated,
                    SectionsSwitchingModeFixed = switchingModeFixed,
                    Modified = true,
                    BackupFile = backup,
                });
            }
            catch (Exception ex)
            {
                log?.Invoke($"Device '{displayName}': LED profile seed failed - {ex.Message}");
                changes.Add(new LedProfileSeedChange
                {
                    InstanceId = instanceId,
                    DisplayName = displayName,
                    SettingsFile = settingsPath,
                    Matched = true,
                    Modified = false,
                    Error = ex.Message,
                });
            }
        }

        return changes;
    }

    /// <summary>
    /// v1.5.9: ensures the local <c>BUILTIN\Users</c> SID has Modify rights on the
    /// target file and clears the ReadOnly attribute. Called after every successful
    /// settings.json write. Best-effort — failures are logged but never throw,
    /// because on some locked-down boxes (group-policy controlled, SeBackupPrivilege
    /// denied, file on a network share with no ACL support) the SetAccessControl
    /// call can fail and we still want the install to complete.
    /// <para>
    /// Root cause this addresses: when the installer is run elevated (UAC prompt
    /// accepted), the OS may persist tighter ACLs on newly written files than what
    /// the SimHub-running user account has. SimHub runs as the regular user, so
    /// after picking a new LED profile in its UI, the in-memory state updates fine
    /// but the on-close serialization to settings.json fails silently. Next SimHub
    /// start reads the old activeProfileId and the user sees their pick reverted.
    /// </para>
    /// <para>
    /// This was specifically the Media-PC bug Vic hit after every install: pick
    /// F1SimHubLive, close SimHub, reopen, back to default GSI FPE V2 profile.
    /// The dev box happened to inherit Users:FullControl from an earlier non-elevated
    /// touch, which is why the dev box didn't show the symptom.
    /// </para>
    /// </summary>
    private static void TryEnsureUsersCanWrite(string filePath, Action<string>? log)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            // Clear ReadOnly attribute if set.
            var attrs = File.GetAttributes(filePath);
            if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
            }

            // BUILTIN\Users SID is S-1-5-32-545, locale-independent.
            var usersSid = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);

            var fi = new FileInfo(filePath);
            var acl = fi.GetAccessControl();

            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                usersSid,
                System.Security.AccessControl.FileSystemRights.Modify | System.Security.AccessControl.FileSystemRights.Synchronize,
                System.Security.AccessControl.AccessControlType.Allow);

            acl.AddAccessRule(rule);
            fi.SetAccessControl(acl);
        }
        catch (Exception ex)
        {
            log?.Invoke($"WARN: could not ensure Users:Modify on '{filePath}': {ex.Message}. SimHub may fail to persist LED profile picks on close.");
        }
    }

    /// <summary>
    /// v1.6.0: Force a section's <c>ProfileSwitchingMode</c> to <c>1</c> ("Disabled"
    /// in the SimHub UI's "Automatic profile switching" radio group). Returns
    /// <c>true</c> if a change was made.
    ///
    /// Mode 1 = Disabled (SimHub respects <c>activeProfileId</c> as the single static pick).
    /// Mode 2 = "Last selected profile, per game" (uses <c>LastGameProfiles[currentGame]</c>;
    ///          IGNORES activeProfileId — this caused Vic's Media PC bug where every
    ///          SimHub restart fell back to "Default Profile" despite our seeder
    ///          correctly writing F1SimHubLive's GUID to activeProfileId).
    /// Mode 3 = Automatic (best-matching, rule-driven).
    ///
    /// We only force this when we're activating our own profile in the section.
    /// If the user has their own racing profile selected, the seeder skips activation
    /// (safety check) and also skips this mode change — their per-game switching
    /// preference is preserved.
    /// </summary>
    private static bool EnsureSwitchingModeDisabled(JsonObject sectionObj)
    {
        var node = sectionObj["ProfileSwitchingMode"];
        int? current = null;
        if (node != null)
        {
            try { current = node.GetValue<int>(); }
            catch
            {
                try
                {
                    var s = node.GetValue<string>();
                    if (int.TryParse(s, out var v)) current = v;
                }
                catch { /* unknown shape — fall through and overwrite */ }
            }
        }

        if (current == 1) return false;
        sectionObj["ProfileSwitchingMode"] = 1;
        return true;
    }

    private static JsonObject? FindByName(JsonArray profiles, string name)
    {
        foreach (var node in profiles)
        {
            if (node is not JsonObject obj) continue;
            var n = obj["Name"]?.GetValue<string>();
            if (string.Equals(n, name, StringComparison.Ordinal)) return obj;
        }
        return null;
    }

    private static JsonObject? FindById(JsonArray profiles, string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return null;
        foreach (var node in profiles)
        {
            if (node is not JsonObject obj) continue;
            var id = obj["ProfileId"]?.GetValue<string>();
            if (string.Equals(id, profileId, StringComparison.OrdinalIgnoreCase)) return obj;
        }
        return null;
    }

    /// <summary>
    /// Loads one of the embedded F1 Live profile JSON assets as a fresh JsonObject.
    /// Each call returns an independent clone so the same asset can be seeded into
    /// multiple devices without sharing references.
    /// </summary>
    private static JsonObject LoadSeedProfile(string assetFileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(assetFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded LED profile asset not found: {assetFileName}");
        using var stream = asm.GetManifestResourceStream(resName)!;
        var node = JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidDataException($"Embedded LED profile asset {assetFileName} is not a JSON object.");

        // Sanity-check the placeholder is what we expect - the C# layer is the only
        // place that mints real ProfileIds, so the asset file should never carry a real one.
        var pid = node["ProfileId"]?.GetValue<string>();
        if (!string.Equals(pid, ProfileIdPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Embedded LED profile asset {assetFileName} has unexpected ProfileId='{pid}'. " +
                $"Expected placeholder '{ProfileIdPlaceholder}'. " +
                $"Re-run extract_profiles.py to regenerate the asset cleanly.");
        }
        return node;
    }
}
