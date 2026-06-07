using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F1SimHubLive.Installer.Services;

/// <summary>
/// Describes a single SimHub Dash Studio device and what dashboard it's currently
/// showing when no game is running (the "idle dashboard").
/// </summary>
public sealed class SimHubDevice
{
    public required string InstanceId { get; init; }
    public required string SettingsFile { get; init; }
    public string? DeviceTypeName { get; init; }
    public string? AutomaticName { get; init; }
    public string? CustomName { get; init; }
    public bool Enabled { get; init; }
    public string? CurrentIdleDashboard { get; init; }
    public string? CurrentDashboard { get; init; }
    public bool HasLcdDisplaySection { get; init; }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName!;
            if (!string.IsNullOrWhiteSpace(AutomaticName)) return AutomaticName!;
            if (!string.IsNullOrWhiteSpace(DeviceTypeName)) return DeviceTypeName!;
            return InstanceId;
        }
    }
}

/// <summary>
/// Result of attempting to set the idle dashboard on a single device.
/// </summary>
public sealed class IdleDashboardChange
{
    public required string InstanceId { get; init; }
    public required string DisplayName { get; init; }
    public string? Before { get; init; }
    public required string After { get; init; }
    public required bool Modified { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Reads and writes SimHub's per-device "idle dashboard" setting at
/// <c>PluginsData\Common\Devices\&lt;guid&gt;\settings.json :: Settings.LCD.display.CurrentIdleDashboard</c>.
///
/// This is the dashboard SimHub shows on a connected screen when no game is running.
/// We touch ONLY <c>CurrentIdleDashboard</c> — we do not modify the playlist defaults
/// (<c>DefaultIdleDash</c>) because those are part of an optional cycling feature most
/// users don't use.
/// </summary>
public sealed class IdleDashboardService
{
    public const string TargetDashboardName = "F1RaceSim_GSIFPEV2";

    /// <summary>
    /// Enumerates all SimHub devices in the install (each one has its own settings.json
    /// under <c>PluginsData\Common\Devices\&lt;guid&gt;</c>). Devices without an
    /// <c>LCD.display</c> section are still returned with <see cref="SimHubDevice.HasLcdDisplaySection"/> = false.
    /// </summary>
    public List<SimHubDevice> EnumerateDevices(string simHubInstallDir)
    {
        var devicesRoot = Path.Combine(simHubInstallDir, "PluginsData", "Common", "Devices");
        if (!Directory.Exists(devicesRoot)) return new List<SimHubDevice>();

        var result = new List<SimHubDevice>();
        foreach (var dir in Directory.EnumerateDirectories(devicesRoot))
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            if (!File.Exists(settingsPath)) continue;

            try
            {
                var raw = File.ReadAllText(settingsPath);
                var root = JsonNode.Parse(raw) as JsonObject;
                if (root == null) continue;

                var instanceId = root["InstanceId"]?.GetValue<string>() ?? Path.GetFileName(dir);
                var deviceTypeName = root["DeviceTypeName"]?.GetValue<string>();
                var automaticName = root["AutomaticName"]?.GetValue<string>();
                var customName = root["CustomName"]?.GetValue<string>();
                var enabled = root["Enabled"]?.GetValue<bool>() ?? false;

                var display = TryGetDisplayNode(root);
                var hasDisplay = display != null;
                var currentIdle = display?["CurrentIdleDashboard"]?.GetValue<string>();
                var currentMain = display?["CurrentDashboard"]?.GetValue<string>();

                result.Add(new SimHubDevice
                {
                    InstanceId = instanceId,
                    SettingsFile = settingsPath,
                    DeviceTypeName = deviceTypeName,
                    AutomaticName = automaticName,
                    CustomName = customName,
                    Enabled = enabled,
                    CurrentIdleDashboard = currentIdle,
                    CurrentDashboard = currentMain,
                    HasLcdDisplaySection = hasDisplay,
                });
            }
            catch
            {
                // Malformed settings.json — skip this device but keep going.
            }
        }
        return result;
    }

    /// <summary>
    /// For every SimHub device with an <c>LCD.display</c> section, set
    /// <c>CurrentIdleDashboard</c> to <paramref name="dashboardName"/>. Writes a
    /// timestamped backup of each device's <c>settings.json</c> before mutating it.
    /// </summary>
    /// <param name="log">Optional log callback so the installer's log panel can show progress.</param>
    public List<IdleDashboardChange> SetIdleDashboardEverywhere(
        string simHubInstallDir,
        string dashboardName,
        Action<string>? log = null)
    {
        var changes = new List<IdleDashboardChange>();
        var devices = EnumerateDevices(simHubInstallDir);
        if (devices.Count == 0)
        {
            log?.Invoke("No SimHub devices found — skipping idle-dashboard configuration.");
            return changes;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        foreach (var d in devices)
        {
            if (!d.HasLcdDisplaySection)
            {
                log?.Invoke($"Device '{d.DisplayName}' has no LCD display section — skipping.");
                changes.Add(new IdleDashboardChange
                {
                    InstanceId = d.InstanceId,
                    DisplayName = d.DisplayName,
                    Before = null,
                    After = d.CurrentIdleDashboard ?? "",
                    Modified = false,
                    Error = "no LCD display section",
                });
                continue;
            }

            try
            {
                var raw = File.ReadAllText(d.SettingsFile);
                var root = JsonNode.Parse(raw) as JsonObject;
                var display = root != null ? TryGetDisplayNode(root) : null;
                if (display == null)
                {
                    log?.Invoke($"Device '{d.DisplayName}': LCD display section vanished — skipping.");
                    continue;
                }

                var before = display["CurrentIdleDashboard"]?.GetValue<string>();
                if (string.Equals(before, dashboardName, StringComparison.Ordinal))
                {
                    log?.Invoke($"Device '{d.DisplayName}': idle dashboard already '{dashboardName}' — no change.");
                    changes.Add(new IdleDashboardChange
                    {
                        InstanceId = d.InstanceId,
                        DisplayName = d.DisplayName,
                        Before = before,
                        After = dashboardName,
                        Modified = false,
                    });
                    continue;
                }

                // v1.5.0 safety: do NOT clobber a user's existing IDLE dashboard selection
                // unless it looks like a SimHub default or one of our own prior selections.
                // Mirrors the v1.4.0 LED activeProfileId safety logic.
                bool safeToOverwrite =
                    string.IsNullOrWhiteSpace(before)
                    || before!.StartsWith("Default", StringComparison.OrdinalIgnoreCase)
                    || before.StartsWith("SimHub", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(before, dashboardName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(before, "F1RaceSim", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(before, "F1RaceSim_HyperP1", StringComparison.OrdinalIgnoreCase);

                if (!safeToOverwrite)
                {
                    log?.Invoke(
                        $"Device '{d.DisplayName}': existing IDLE dashboard '{before}' preserved. " +
                        $"F1SimHubLive dashboard '{dashboardName}' installed but not auto-activated. " +
                        $"To use it, open SimHub > Devices > '{d.DisplayName}' > LCD and pick '{dashboardName}' for the IDLE screen.");
                    changes.Add(new IdleDashboardChange
                    {
                        InstanceId = d.InstanceId,
                        DisplayName = d.DisplayName,
                        Before = before,
                        After = before ?? "",
                        Modified = false,
                    });
                    continue;
                }

                var backup = $"{d.SettingsFile}.preF1SimHubLive-{stamp}";
                File.Copy(d.SettingsFile, backup, overwrite: false);
                log?.Invoke($"Device '{d.DisplayName}': backed up settings.json -> {Path.GetFileName(backup)}");

                display["CurrentIdleDashboard"] = dashboardName;

                var serialized = root!.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = false,
                });
                File.WriteAllText(d.SettingsFile, serialized, new UTF8Encoding(false));

                log?.Invoke(
                    $"Device '{d.DisplayName}': idle dashboard changed " +
                    $"'{before ?? "(unset)"}' -> '{dashboardName}'.");

                changes.Add(new IdleDashboardChange
                {
                    InstanceId = d.InstanceId,
                    DisplayName = d.DisplayName,
                    Before = before,
                    After = dashboardName,
                    Modified = true,
                });
            }
            catch (Exception ex)
            {
                log?.Invoke($"Device '{d.DisplayName}': failed to update idle dashboard — {ex.Message}");
                changes.Add(new IdleDashboardChange
                {
                    InstanceId = d.InstanceId,
                    DisplayName = d.DisplayName,
                    Before = d.CurrentIdleDashboard,
                    After = d.CurrentIdleDashboard ?? "",
                    Modified = false,
                    Error = ex.Message,
                });
            }
        }

        return changes;
    }

    /// <summary>
    /// Returns the <c>Settings.LCD.display</c> node for a device settings root, or null
    /// if any path segment is missing or not an object.
    /// </summary>
    private static JsonObject? TryGetDisplayNode(JsonObject root)
    {
        if (root["Settings"] is not JsonObject settings) return null;
        if (settings["LCD"] is not JsonObject lcd) return null;
        if (lcd["display"] is not JsonObject display) return null;
        return display;
    }
}
