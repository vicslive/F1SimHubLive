using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Persists the picker window's user-toggleable UI preferences across launches.
/// Currently stores the <c>Topmost</c> ("📌 Pin") checkbox state; designed to
/// hold additional toggle prefs (LED strip visibility, future settings…) by
/// adding nullable properties to <see cref="Preferences"/>.
///
/// <para>Why a SEPARATE file from <see cref="WindowGeometryStore"/>:</para>
/// Geometry changes fire dozens of times per drag/resize and are debounced
/// in their own file. Toggle changes fire once when the user clicks, so we
/// can save synchronously on each change without a debounce timer. Keeping
/// them in different files also means a corrupt geometry file can't wipe
/// out the user's UI preferences (and vice-versa).
///
/// <para>Why a SEPARATE file from <c>F1SimHubLive.Settings.json</c>:</para>
/// The plugin watches the settings file with a FileSystemWatcher and reloads
/// the driver number on every write. UI preferences are picker-process-local
/// and have zero meaning to the plugin — putting them in the shared settings
/// file would trigger pointless plugin reloads (and a momentary driver swap
/// glitch) every time the user toggles a checkbox.
///
/// <para>First-launch behaviour:</para>
/// When the preferences file is missing or the field is null, <see cref="Apply"/>
/// leaves the XAML-declared default in place (currently <c>Topmost="True"</c>
/// and <c>IsChecked="True"</c> on the Pin checkbox). Once the user toggles
/// the checkbox at least once we save the explicit choice, and from then on
/// every launch respects what they last chose.
/// </summary>
internal static class WindowPreferencesStore
{
    private const string FileName = "F1SimHubLive.PickerPreferences.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class Preferences
    {
        // Nullable so we can distinguish "user hasn't expressed a preference
        // yet" (leave the XAML default in place) from "user explicitly
        // unchecked it" (apply false on next launch).
        public bool? Topmost { get; set; }
    }

    private static Preferences _cache = new();
    private static bool _loaded;

    // Becomes true the first time Apply() finishes. Save() no-ops until then.
    // This guards against the XAML default IsChecked="True" firing the
    // CheckBox.Checked event during InitializeComponent and clobbering the
    // user's saved preference *before* Apply has had a chance to read it
    // from disk. Without this guard, every cold launch would overwrite the
    // saved value with the XAML default and the persistence would be useless.
    private static bool _ready;

    /// <summary>
    /// Read the preferences file from disk and apply any explicit values to
    /// the corresponding window properties / controls. Silently no-ops if
    /// the file is missing or unparseable — the XAML defaults will apply.
    /// Safe to call from the <see cref="System.Windows.Window"/> constructor
    /// after <c>InitializeComponent()</c>.
    /// </summary>
    public static void Apply(System.Windows.Window window, System.Windows.Controls.CheckBox topmostCheck)
    {
        Load();

        if (_cache.Topmost is bool pinned)
        {
            // Set the checkbox first so the Checked/Unchecked handlers fire
            // their side-effect (Topmost = cb.IsChecked == true). Doing it
            // this way means we don't need to also push window.Topmost here —
            // the existing handler is the single source of truth for that.
            topmostCheck.IsChecked = pinned;

            // Defensive: also sync Window.Topmost directly in case the value
            // we just set matches what XAML already had (no event fires when
            // the value doesn't change, so the handler's Topmost sync would
            // be skipped). E.g. saved=true, XAML default=true → no Checked
            // event → Topmost stays whatever XAML said (already true), so
            // this is a no-op. saved=false, XAML default=true → Unchecked
            // fires → handler sets Topmost=false → this is also a no-op.
            // Belt-and-suspenders.
            window.Topmost = pinned;
        }

        // From this point on, real user clicks should persist to disk. Any
        // Save() calls that fired during XAML init (before this point) were
        // silently dropped.
        _ready = true;
    }

    /// <summary>
    /// Persist the current value of every tracked preference. Called from
    /// the Checked/Unchecked handlers on each toggle. Synchronous write —
    /// these events are rare (a user click), debounce overhead would be
    /// pointless complexity.
    /// </summary>
    public static void Save(System.Windows.Controls.CheckBox topmostCheck)
    {
        // Drop saves that happen before Apply() has finished its initial
        // load — those are typically the XAML default IsChecked="True"
        // firing its Checked event during InitializeComponent, which would
        // otherwise clobber the user's saved preference before we get a
        // chance to load it.
        if (!_ready) return;

        try
        {
            _cache.Topmost = topmostCheck.IsChecked == true;

            var path = GetPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_cache, JsonOptions));
        }
        catch
        {
            // Best-effort persistence — never let a save failure surface to
            // the user. They'll just get the XAML default next launch if the
            // file is unwritable.
        }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var path = GetPath();
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var prefs = JsonSerializer.Deserialize<Preferences>(json, JsonOptions);
            if (prefs != null) _cache = prefs;
        }
        catch
        {
            // Corrupt file → fall through with empty _cache. XAML defaults
            // will apply. The next Save() will overwrite the corrupt file.
        }
    }

    private static string GetPath()
    {
        var settingsPath = SettingsPathResolver.UserPath();
        var dir = Path.GetDirectoryName(settingsPath)!;
        return Path.Combine(dir, FileName);
    }
}
