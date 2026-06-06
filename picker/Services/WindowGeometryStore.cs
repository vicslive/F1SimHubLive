using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Persists the picker window's position, size, and maximized state across
/// launches so the window comes back exactly where the user left it.
/// Windows itself does NOT track per-app window geometry — every WPF app
/// has to save and restore its own. We store ours in a small JSON file
/// next to the main settings file (same per-user folder under %APPDATA%,
/// same UAC-free write story).
///
/// <para>Why a SEPARATE file from <c>F1SimHubLive.Settings.json</c>:</para>
/// The plugin watches the settings file with a FileSystemWatcher and reloads
/// on every write. Geometry changes (drag, resize) happen frequently and
/// have zero meaning to the plugin — dumping them into the shared settings
/// file would trigger pointless plugin reloads. Keeping them in their own
/// file means resizing the picker window never wakes the plugin up.
///
/// <para>Why we save continuously (not just on close):</para>
/// Originally we only saved in <see cref="Window.Closed"/>. That fails in
/// several real-world close paths: SimHub terminating the child picker on
/// shutdown, Task Manager kill, a crash in another handler running before
/// ours. With continuous (throttled) save, the latest geometry is always
/// on disk after a ~500 ms quiet period — even an abrupt termination loses
/// at most the last half-second of movement, which is invisible to the user.
///
/// <para>Multi-monitor safety:</para>
/// On <see cref="Apply"/>, we sanity-check that the saved rect overlaps the
/// current virtual screen by at least 120x80 pixels. If the user unplugs
/// the monitor the picker was last on, the saved rect would otherwise place
/// the window off-screen with no easy way to drag it back. When the sanity
/// check fails we silently fall through to WPF's default placement (the
/// XAML-declared Width/Height + WindowStartupLocation behaviour).
///
/// <para>Maximized state:</para>
/// When the window is maximized, <c>Left/Top/Width/Height</c> report the
/// maximized rectangle (the monitor's working area). We instead read
/// <see cref="Window.RestoreBounds"/>, which is the underlying "normal"
/// rect — that way un-maximizing on the next launch reveals the right size,
/// and the saved WindowState restores the maximized presentation.
/// </summary>
internal static class WindowGeometryStore
{
    private const string GeometryFileName = "F1SimHubLive.PickerWindow.json";
    private const double MinVisiblePixels = 120;
    private const double MinVisiblePixelsTall = 80;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class Geometry
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public WindowState WindowState { get; set; } = WindowState.Normal;
    }

    /// <summary>
    /// Restore window placement from disk. Safe to call from the
    /// <see cref="Window"/> constructor after <c>InitializeComponent()</c>
    /// but before the window is shown. Silently no-ops if the file is
    /// missing, malformed, or describes a rect that wouldn't be visible
    /// on any currently-attached monitor.
    /// </summary>
    public static void Apply(Window window)
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var geom = JsonSerializer.Deserialize<Geometry>(json, JsonOptions);
            if (geom == null) return;
            if (geom.Width < 1 || geom.Height < 1) return;
            if (!IsOnscreen(geom.Left, geom.Top, geom.Width, geom.Height)) return;

            window.Left = geom.Left;
            window.Top = geom.Top;
            window.Width = geom.Width;
            window.Height = geom.Height;

            if (geom.WindowState == WindowState.Maximized)
            {
                window.WindowState = WindowState.Maximized;
            }
        }
        catch
        {
            // Geometry persistence is best-effort. A corrupt file should never
            // prevent the picker from launching — fall through to defaults.
        }
    }

    /// <summary>
    /// Wire up continuous, debounced geometry persistence. Subscribes to
    /// <see cref="Window.LocationChanged"/>, <see cref="FrameworkElement.SizeChanged"/>,
    /// <see cref="Window.StateChanged"/>, and <see cref="Window.Closing"/>.
    /// Every event schedules a save 500 ms in the future; if more events
    /// arrive in that window, the timer resets — so a continuous drag
    /// becomes a single write at the end of the gesture.
    /// </summary>
    public static void Attach(Window window)
    {
        DispatcherTimer? timer = null;

        void ScheduleSave()
        {
            if (timer == null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
                {
                    Interval = SaveDebounce
                };
                timer.Tick += (_, _) =>
                {
                    timer!.Stop();
                    Save(window);
                };
            }
            timer.Stop();
            timer.Start();
        }

        window.LocationChanged += (_, _) => ScheduleSave();
        window.SizeChanged += (_, _) => ScheduleSave();
        window.StateChanged += (_, _) => ScheduleSave();

        // Closing fires before Closed and before any disposables in user
        // handlers run, so it's the most reliable point to flush. We bypass
        // the timer here and write synchronously to guarantee the file lands
        // before the process exits.
        window.Closing += (_, _) =>
        {
            timer?.Stop();
            Save(window);
        };
    }

    /// <summary>
    /// Capture the current window placement and persist it. Uses
    /// <see cref="Window.RestoreBounds"/> when valid (so a maximized window
    /// saves its underlying normal rect, not the monitor working area).
    /// </summary>
    public static void Save(Window window)
    {
        try
        {
            // Prefer RestoreBounds (the "normal" rect regardless of state).
            // It can be Rect.Empty before the window has ever been shown —
            // in that case Empty.Left is +Infinity, so we fall back to the
            // current Left/Top/Width/Height which are always valid post-show.
            var bounds = window.RestoreBounds;
            if (bounds.IsEmpty
                || double.IsNaN(bounds.Left) || double.IsInfinity(bounds.Left)
                || bounds.Width < 1 || bounds.Height < 1)
            {
                bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            }

            if (double.IsNaN(bounds.Left) || double.IsInfinity(bounds.Left)) return;
            if (bounds.Width < 1 || bounds.Height < 1) return;

            var geom = new Geometry
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                WindowState = window.WindowState == WindowState.Minimized
                    ? WindowState.Normal
                    : window.WindowState
            };

            var path = GetPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(geom, JsonOptions));
        }
        catch
        {
            // Same rationale as Apply — never let geometry persistence fail
            // the close path. The user will just get default placement next
            // time, which is acceptable.
        }
    }

    private static string GetPath()
    {
        var settingsPath = SettingsPathResolver.UserPath();
        var dir = Path.GetDirectoryName(settingsPath)!;
        return Path.Combine(dir, GeometryFileName);
    }

    /// <summary>
    /// True if the proposed rect would land enough on-screen for the user
    /// to grab it. We measure against the virtual screen (the union of all
    /// monitors' bounds) so multi-monitor setups behave correctly.
    /// A window that overlaps the virtual screen by at least
    /// <see cref="MinVisiblePixels"/> wide and <see cref="MinVisiblePixelsTall"/>
    /// tall is considered safe.
    /// </summary>
    private static bool IsOnscreen(double left, double top, double width, double height)
    {
        var vsLeft = SystemParameters.VirtualScreenLeft;
        var vsTop = SystemParameters.VirtualScreenTop;
        var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

        var overlapLeft = Math.Max(left, vsLeft);
        var overlapTop = Math.Max(top, vsTop);
        var overlapRight = Math.Min(left + width, vsRight);
        var overlapBottom = Math.Min(top + height, vsBottom);

        var overlapWidth = overlapRight - overlapLeft;
        var overlapHeight = overlapBottom - overlapTop;

        return overlapWidth >= MinVisiblePixels && overlapHeight >= MinVisiblePixelsTall;
    }
}
