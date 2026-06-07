using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Atomic write of F1SimHubLive.Settings.json. Preserves every untouched
/// field exactly. Writes to a sibling temp file and renames into place so
/// the plugin's FileSystemWatcher never sees a partial JSON document.
/// </summary>
internal static class SettingsFileWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string? ReadCurrentDriverNumber(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!doc.RootElement.TryGetProperty("DriverNumber", out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the shift-light RPM range from the settings file. Falls back to
    /// the plugin's defaults (3500 / 13000) if the file is missing,
    /// malformed, or the fields aren't present.
    /// </summary>
    public static (int startRpm, int endRpm) ReadShiftLightRange(string settingsPath)
    {
        const int defaultStart = 3500;
        const int defaultEnd = 13000;
        try
        {
            if (!File.Exists(settingsPath)) return (defaultStart, defaultEnd);
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            int start = ReadInt(doc.RootElement, "RpmShiftLightStartRpm", defaultStart);
            int end = ReadInt(doc.RootElement, "RpmShiftLightEndRpm", defaultEnd);
            return (start, end);
        }
        catch
        {
            return (defaultStart, defaultEnd);
        }
    }

    /// <summary>
    /// Reads the MultiViewer base URL from the settings file so the picker polls
    /// the same MultiViewer instance the plugin polls. Returns <c>null</c> when
    /// the file is missing, malformed, the field is absent, or the stored value
    /// is not an http loopback URL (matches <see cref="Settings.Validate"/> on
    /// the plugin side). Caller falls back to the picker's own default in that
    /// case.
    ///
    /// <para>Why we need this: prior to v1.5.3 the picker hardcoded
    /// <c>http://localhost:10101</c> and ignored the settings file entirely.
    /// If MultiViewer was bound to a non-default URL the plugin still worked
    /// (it reads from settings) but the picker's LED-preview bar stayed dim
    /// because its HTTP polls hit nothing.</para>
    /// </summary>
    public static string? ReadMultiViewerBaseUrl(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!doc.RootElement.TryGetProperty("MultiViewerBaseUrl", out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) return null;
            string? raw = v.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp) return null;
            if (!uri.IsLoopback) return null;
            return raw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces DriverNumber in the settings file with the given value, atomically.
    /// Throws on IO / permission failures so the UI can surface them.
    /// </summary>
    public static void WriteDriverNumber(string settingsPath, string driverNumber)
    {
        WriteField(settingsPath, obj => obj["DriverNumber"] = driverNumber);
    }

    /// <summary>
    /// Replaces RpmShiftLightStartRpm and RpmShiftLightEndRpm in the settings
    /// file, atomically. The plugin hot-reloads via FileSystemWatcher so the
    /// wheel LEDs re-map within ~250 ms of the call returning.
    /// </summary>
    public static void WriteShiftLightRange(string settingsPath, int startRpm, int endRpm)
    {
        // Clamp to sane racing-engine bounds so a stray slider drag can't
        // poison the plugin with garbage that disables the RpmShiftPercent
        // formula entirely.
        startRpm = Math.Clamp(startRpm, 1000, 19000);
        endRpm = Math.Clamp(endRpm, 1000, 20000);
        if (endRpm <= startRpm) endRpm = startRpm + 100;

        WriteField(settingsPath, obj =>
        {
            obj["RpmShiftLightStartRpm"] = startRpm;
            obj["RpmShiftLightEndRpm"] = endRpm;
        });
    }

    /// <summary>
    /// Reads <c>AutoLaunchPicker</c> from the settings file. Returns false
    /// (matching <see cref="F1SimHubLive.Settings.AutoLaunchPicker"/>'s
    /// default) when the file is missing, malformed, or the field is unset.
    /// </summary>
    public static bool ReadAutoLaunchPicker(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!doc.RootElement.TryGetProperty("AutoLaunchPicker", out var v)) return false;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes <c>AutoLaunchPicker</c> to the settings file, atomically. The
    /// plugin reads this once during <c>Init()</c> to decide whether to spawn
    /// the picker, so the change takes effect on the next SimHub launch.
    /// </summary>
    public static void WriteAutoLaunchPicker(string settingsPath, bool value)
    {
        WriteField(settingsPath, obj => obj["AutoLaunchPicker"] = value);
    }

    private static void WriteField(string settingsPath, Action<JsonObject> mutate)
    {
        JsonNode root;
        if (File.Exists(settingsPath))
        {
            string raw = File.ReadAllText(settingsPath);
            root = JsonNode.Parse(raw) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }
        if (root is not JsonObject obj)
        {
            throw new InvalidDataException(
                $"settings file root is not a JSON object: {settingsPath}");
        }
        mutate(obj);

        string tmp = settingsPath + ".picker.tmp";
        File.WriteAllText(tmp, obj.ToJsonString(Indented));
        // File.Move with overwrite is the closest WinAPI gives us to atomic
        // replace on the same volume. The plugin's FileSystemWatcher fires on
        // the rename and we get a clean reload.
        File.Move(tmp, settingsPath, overwrite: true);
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return fallback;
    }
}

