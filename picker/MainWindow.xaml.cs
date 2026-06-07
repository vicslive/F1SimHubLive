using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using F1SimHubLive.Picker.Models;
using F1SimHubLive.Picker.Services;

namespace F1SimHubLive.Picker;

public partial class MainWindow : Window
{
    // CLI args:
    //   --settings <path>   override the F1SimHubLive.Settings.json location
    //   --mv-url <url>      override MultiViewer base URL
    private const string DefaultMvUrl = "http://localhost:10101";
    private const int LedCount = 14;
    // Slider-to-disk debounce: drag events fire fast (50–60 Hz), so wait for
    // the user to settle for ~250 ms before writing the settings file. The
    // plugin reloads via FileSystemWatcher within ~250 ms of the write, so
    // total perceived latency is "drag stops → ~half a second → wheel LEDs
    // update". Matches the existing driver-switch latency budget.
    private static readonly TimeSpan SliderWriteDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _settingsPath;
    private readonly string _mvUrl;
    private readonly LiveTimingClient _liveTimingClient;
    private readonly SessionInfoClient _sessionInfoClient;
    private readonly FileSystemWatcher? _settingsWatcher;
    private readonly object _watcherLock = new();
    private DateTime _lastWatcherFire = DateTime.MinValue;

    // LED preview state
    private readonly PickerTelemetryClient _telemetry;
    private readonly ObservableCollection<Brush> _ledBrushes = new();
    private readonly DispatcherTimer _sliderWriteTimer;
    private static readonly Brush DimLedBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x20));
    private static readonly Brush GreenLedBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD0, 0x6A));
    private static readonly Brush BlueLedBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x9C, 0xF0));
    private static readonly Brush RedLedBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x3A, 0x3A));
    private static readonly Brush WhiteLedBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xFA));
    private int _startRpm = 5500;
    private int _endRpm = 14000;
    private double _lastRpm;
    private bool _suppressSliderWrite; // true while loading from settings
    private bool _suppressAutoLaunchWrite; // true while seeding checkbox from settings

    private CancellationTokenSource _ctsLifetime = new();
    private string _currentDriverNumber = "";

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);

        // Restore window position / size / maximized state from the previous
        // session before the window is shown, and wire up continuous save on
        // move/resize/state-change/close. We do this in two steps because
        // some close paths (SimHub shutting down a child picker, Task
        // Manager kill, crashes in other Closed handlers) never reach a
        // save-on-Closed callback — the continuous (debounced) save in
        // Attach guarantees the latest geometry is always on disk.
        WindowGeometryStore.Apply(this);
        WindowGeometryStore.Attach(this);

        // Restore user UI preferences (Pin checkbox state) before the window
        // is shown. Apply happens AFTER Geometry so the topmost-checkbox
        // handler that fires from the apply has the right Topmost source-of-
        // truth wired up already. See WindowPreferencesStore for rationale.
        WindowPreferencesStore.Apply(this, TopmostCheck);

        var (settingsPath, mvUrl) = ParseArgs(Environment.GetCommandLineArgs());
        _settingsPath = settingsPath ?? DefaultSettingsPath();
        _mvUrl = mvUrl ?? DefaultMvUrl;
        _liveTimingClient = new LiveTimingClient(Dispatcher, _mvUrl);
        _sessionInfoClient = new SessionInfoClient(Dispatcher, _mvUrl);
        SessionHeaderBar.DataContext = _sessionInfoClient.Model;
        _telemetry = new PickerTelemetryClient(_mvUrl);

        SettingsPathText.Text = _settingsPath;
        VersionText.Text = $"v{GetDisplayVersion()}";
        VersionText.ToolTip =
            $"F1SimHubLive Driver Picker v{GetDisplayVersion()}\n\n" +
            $"Settings file:\n{_settingsPath}\n\n" +
            $"MultiViewer:\n{_mvUrl}";

        _currentDriverNumber = SettingsFileWriter.ReadCurrentDriverNumber(_settingsPath) ?? "";
        UpdateCurrentDriverText();

        // Seed the Auto-launch checkbox from the current settings.json value
        // BEFORE the window is shown so the user never sees a flicker from
        // the XAML default (unchecked) to the persisted state. The suppress
        // flag stops the resulting Checked/Unchecked event from looping back
        // and writing the value we just read.
        _suppressAutoLaunchWrite = true;
        try
        {
            AutoLaunchCheck.IsChecked = SettingsFileWriter.ReadAutoLaunchPicker(_settingsPath);
        }
        finally
        {
            _suppressAutoLaunchWrite = false;
        }

        InitializeLedStrip();
        LoadSliderRangeFromSettings();
        ApplyLedsForRpm(_lastRpm); // paints the strip dim at startup

        // Wire slider handlers in code-behind (not XAML) because Slider.ValueChanged
        // fires during InitializeComponent when Minimum/Maximum get set, and at that
        // point the fields the handler touches (_sliderWriteTimer, RangeSummary, etc.)
        // may not exist yet. Attaching after LoadSliderRangeFromSettings guarantees
        // a fully-initialized state.
        StartRpmSlider.ValueChanged += StartRpmSlider_ValueChanged;
        EndRpmSlider.ValueChanged += EndRpmSlider_ValueChanged;

        // Telemetry wiring: every CarData frame for the active driver becomes
        // a brush refresh + RPM readout. Marshalled to the UI thread because
        // the HTTP loop runs on a worker.
        _telemetry.OnRpm += rpm => Dispatcher.BeginInvoke(new Action(() =>
        {
            _lastRpm = rpm;
            ApplyLedsForRpm(rpm);
            RpmReadout.Text = ((int)Math.Round(rpm)).ToString();
        }));
        _telemetry.OnStatus += s => Dispatcher.BeginInvoke(new Action(() =>
        {
            // Telemetry status is informational; the live-timing client owns
            // StatusText. Surface "telemetry disconnected" later if needed;
            // for now the LED strip going dim is its own signal.
            _ = s;
        }));
        // Per-driver speed batch (km/h) — pushed into the matching
        // DriverTimingRow so each row can render the current car speed.
        _telemetry.OnSpeedsBatch += speeds => Dispatcher.BeginInvoke(new Action(() =>
        {
            var rows = _liveTimingClient.Rows;
            foreach (var row in rows)
            {
                if (speeds.TryGetValue(row.RacingNumber, out var spd))
                {
                    row.SpeedKmh = spd;
                }
            }
        }));
        if (!string.IsNullOrEmpty(_currentDriverNumber))
            _telemetry.SetDriverNumber(_currentDriverNumber);
        _telemetry.Start(pollIntervalMs: 200);

        _sliderWriteTimer = new DispatcherTimer { Interval = SliderWriteDelay };
        _sliderWriteTimer.Tick += SliderWriteTimer_Tick;

        // Wire the live-timing client and bind its rows to the ItemsControl.
        // Rows is an ObservableCollection that LiveTimingClient mutates in
        // place on the UI thread (via Dispatcher), so we set ItemsSource
        // exactly once and let WPF react to CollectionChanged.
        DriverList.ItemsSource = _liveTimingClient.Rows;
        _liveTimingClient.SetCurrentDriverNumber(
            string.IsNullOrEmpty(_currentDriverNumber) ? null : _currentDriverNumber);
        _liveTimingClient.OnStatus += msg => Dispatcher.BeginInvoke(new Action(() =>
        {
            StatusText.Text = msg;
        }));
        _liveTimingClient.Rows.CollectionChanged += (_, _) =>
        {
            // Cheap: the count is the only thing that affects the status
            // bar; per-row property changes don't bubble up here.
            StatusText.Text = _liveTimingClient.Rows.Count == 0
                ? "Waiting for MultiViewer session…"
                : $"MultiViewer: {_liveTimingClient.Rows.Count} drivers · live (500 ms)";
            UpdateCurrentDriverText();
        };

        try
        {
            string dir = Path.GetDirectoryName(_settingsPath) ?? "";
            string file = Path.GetFileName(_settingsPath);
            if (Directory.Exists(dir))
            {
                _settingsWatcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _settingsWatcher.Changed += OnSettingsFileChanged;
                _settingsWatcher.Renamed += OnSettingsFileChanged;
            }
        }
        catch
        {
            // Watcher is a nice-to-have; we still poll on a timer.
        }

        Loaded += async (_, _) =>
        {
            _liveTimingClient.Start();
            _sessionInfoClient.Start();
            _ = CheckForUpdateAsync(); // fire-and-forget; UI updates if newer
            await Task.CompletedTask;
        };

        Closed += (_, _) =>
        {
            _liveTimingClient.Dispose();
            _sessionInfoClient.Dispose();
            _sliderWriteTimer.Stop();
            _settingsWatcher?.Dispose();
            _telemetry.Dispose();
            _ctsLifetime.Cancel();
        };
    }

    private static (string? settings, string? mvUrl) ParseArgs(string[] args)
    {
        string? s = null, u = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--settings", StringComparison.OrdinalIgnoreCase)) s = args[i + 1];
            else if (args[i].Equals("--mv-url", StringComparison.OrdinalIgnoreCase)) u = args[i + 1];
        }
        return (s, u);
    }

    private static string DefaultSettingsPath()
    {
        // v1.3.0+: per-user settings live in %APPDATA%\F1SimHubLive\. Resolver
        // handles one-shot migration from legacy locations (PROGRAMDATA seed
        // written by the installer, or Program Files (x86)\SimHub\ from older
        // versions). The plugin uses the same resolver.
        return SettingsPathResolver.Resolve(msg => System.Diagnostics.Debug.WriteLine(msg));
    }

    private void UpdateCurrentDriverText()
    {
        if (string.IsNullOrEmpty(_currentDriverNumber))
        {
            CurrentDriverText.Text = "—";
            return;
        }
        var match = _liveTimingClient.Rows.FirstOrDefault(r => r.RacingNumber == _currentDriverNumber);
        if (match != null && !string.IsNullOrEmpty(match.Tla))
            CurrentDriverText.Text = $"{match.Tla}  #{match.RacingNumber}";
        else
            CurrentDriverText.Text = $"#{_currentDriverNumber}";
    }

    private void DriverButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string? number = btn.Tag as string;
        if (string.IsNullOrEmpty(number)) return;

        try
        {
            SettingsFileWriter.WriteDriverNumber(_settingsPath, number);
            _currentDriverNumber = number;
            _telemetry.SetDriverNumber(number);
            _liveTimingClient.SetCurrentDriverNumber(number);
            UpdateCurrentDriverText();
            StatusText.Text = $"Switched to #{number} — plugin will reload within ~250 ms.";
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                "Could not write the settings file — access denied.\n\n" +
                "F1SimHubLive's settings file lives in your per-user AppData " +
                "folder. If something is blocking writes there (antivirus, " +
                "Controlled Folder Access, sync engine), the picker can't " +
                "switch drivers.\n\n" +
                $"Path:\n{_settingsPath}",
                "F1SimHubLive — Driver Picker",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Failed to update settings:\n\n{ex.Message}\n\nPath:\n{_settingsPath}",
                "F1SimHubLive — Driver Picker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
    {
        // Coalesce the burst of events Windows fires per write.
        lock (_watcherLock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastWatcherFire).TotalMilliseconds < 200) return;
            _lastWatcherFire = now;
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var n = SettingsFileWriter.ReadCurrentDriverNumber(_settingsPath);
                if (!string.IsNullOrEmpty(n) && n != _currentDriverNumber)
                {
                    _currentDriverNumber = n!;
                    _telemetry.SetDriverNumber(n!);
                    _liveTimingClient.SetCurrentDriverNumber(n!);
                    UpdateCurrentDriverText();
                }
                // Also re-read the shift range — covers external edits and
                // round-trips after we wrote it ourselves (cheap idempotent
                // update; the suppress flag stops the resulting slider
                // ValueChanged from looping back to disk).
                LoadSliderRangeFromSettings();
                ApplyLedsForRpm(_lastRpm);
            }
            catch { /* file mid-write — wait for next event */ }
        }));
    }

    private void TopmostCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            Topmost = cb.IsChecked == true;
            WindowPreferencesStore.Save(cb);
        }
    }

    private void AutoLaunchCheck_Changed(object sender, RoutedEventArgs e)
    {
        // Suppressed during ctor when we seed the checkbox from settings.json —
        // otherwise we'd write the value we just read.
        if (_suppressAutoLaunchWrite) return;
        if (sender is not CheckBox cb) return;
        bool value = cb.IsChecked == true;
        try
        {
            SettingsFileWriter.WriteAutoLaunchPicker(_settingsPath, value);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not save Auto-launch preference to:\n{_settingsPath}\n\n{ex.GetType().Name}: {ex.Message}",
                "F1SimHubLive Driver Picker",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void LedToggle_Changed(object sender, RoutedEventArgs e)
    {
        // Show / hide the shift-light range strip without resizing the
        // window — the DockPanel reclaims the space automatically.
        if (SliderStrip != null && sender is ToggleButton tb)
        {
            SliderStrip.Visibility = tb.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private static string GetDisplayVersion()
    {
        // Prefer InformationalVersion (settable to "1.1.4" without the
        // trailing ".0" the runtime forces onto AssemblyVersion).
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip "+commitsha" suffix that the .NET SDK appends in some
            // build configurations.
            int plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
        var v = asm.GetName().Version;
        if (v is null) return "0.0.0";
        return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            var checker = new UpdateChecker(current);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ctsLifetime.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await checker.CheckAsync(cts.Token).ConfigureAwait(true);
            ApplyUpdateResult(result);
        }
        catch
        {
            // Best-effort: any failure leaves the UI in its default "no update"
            // state. The version label is still visible.
        }
    }

    private void ApplyUpdateResult(UpdateCheckResult result)
    {
        if (!result.IsUpdateAvailable || result.LatestTag is null)
        {
            UpdateText.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateRun.Text = $"▲ {result.LatestTag} available — update";
        if (!string.IsNullOrWhiteSpace(result.HtmlUrl))
        {
            UpdateLink.NavigateUri = new Uri(result.HtmlUrl);
        }
        UpdateText.ToolTip =
            $"You are running v{GetDisplayVersion()}.\n" +
            $"Latest GitHub release: {result.LatestTag}.\n\n" +
            "Click to open the release page in your browser.";
        UpdateText.Visibility = Visibility.Visible;
    }

    private void UpdateLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            // ProcessStartInfo with UseShellExecute is the canonical way to
            // open a URL in the user's default browser from a WPF app.
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
            e.Handled = true;
        }
        catch
        {
            // Fall back silently — the URL is still visible in the link text.
        }
    }

    // -------- LED preview + slider plumbing --------

    private void InitializeLedStrip()
    {
        // Horizontal LED bar in the header: brush index 0 = LED 1 on the
        // left (lights first at low RPM), index LedCount-1 = redline LED
        // on the right (lights last). UniformGrid renders left-to-right.
        _ledBrushes.Clear();
        for (int i = 0; i < LedCount; i++) _ledBrushes.Add(DimLedBrush);
        LedStrip.ItemsSource = _ledBrushes;
    }

    private void LoadSliderRangeFromSettings()
    {
        var (start, end) = SettingsFileWriter.ReadShiftLightRange(_settingsPath);
        if (start == _startRpm && end == _endRpm
            && Math.Abs(StartRpmSlider.Value - start) < 0.5
            && Math.Abs(EndRpmSlider.Value - end) < 0.5)
        {
            return; // nothing changed
        }
        _startRpm = start;
        _endRpm = end;
        _suppressSliderWrite = true;
        try
        {
            StartRpmSlider.Value = start;
            EndRpmSlider.Value = end;
            StartRpmText.Text = start.ToString();
            EndRpmText.Text = end.ToString();
            RangeSummary.Text = $"{start} → {end} RPM";
        }
        finally
        {
            _suppressSliderWrite = false;
        }
    }

    private void StartRpmSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)Math.Round(e.NewValue);
        _startRpm = v;
        StartRpmText.Text = v.ToString();
        RangeSummary.Text = $"{_startRpm} → {_endRpm} RPM";
        ApplyLedsForRpm(_lastRpm);
        ScheduleSliderWrite();
    }

    private void EndRpmSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)Math.Round(e.NewValue);
        _endRpm = v;
        EndRpmText.Text = v.ToString();
        RangeSummary.Text = $"{_startRpm} → {_endRpm} RPM";
        ApplyLedsForRpm(_lastRpm);
        ScheduleSliderWrite();
    }

    private void ScheduleSliderWrite()
    {
        if (_suppressSliderWrite) return;
        // Reset the timer on every drag tick — the actual disk write only
        // fires once the user has stopped dragging for SliderWriteDelay.
        _sliderWriteTimer.Stop();
        _sliderWriteTimer.Start();
    }

    private void SliderWriteTimer_Tick(object? sender, EventArgs e)
    {
        _sliderWriteTimer.Stop();
        try
        {
            SettingsFileWriter.WriteShiftLightRange(_settingsPath, _startRpm, _endRpm);
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = "Could not save shift-light range — run picker as administrator.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to save shift-light range: {ex.Message}";
        }
    }

    /// <summary>
    /// Per-LED lighting thresholds expressed as a percentage of the
    /// shift-light range (start RPM → end RPM, i.e. <c>RpmShiftPercent</c>).
    /// These are deliberately NOT a uniform 1/<see cref="LedCount"/> spread:
    /// they mirror the actual three-segment gradient configured on the F1
    /// steering-wheel device in SimHub so the picker's bar lights up at
    /// the same RPM as the physical wheel.
    ///
    /// <para>Wheel config (from
    /// <c>SimHub\PluginsData\Common\Devices\&lt;wheel&gt;\settings.json</c>,
    /// three <c>CustomGradient</c> rules driven by
    /// <c>[F1SimHubLivePlugin.RpmShiftPercent]</c>):</para>
    /// <list type="bullet">
    ///   <item>LEDs 1–5 (green): range 0–30%, 5 LEDs → 6% per LED</item>
    ///   <item>LEDs 6–10 (blue): range 30–63%, 5 LEDs → 6.6% per LED</item>
    ///   <item>LEDs 11–14 (red): range 63–93%, 4 LEDs → 7.5% per LED</item>
    /// </list>
    /// LED 1's threshold is a hair above 0 (not exactly 0) so the strip
    /// renders dim while idling at the configured start RPM, matching the
    /// wheel's <c>EnabledFormula: RpmShiftPercent &gt; 0</c> gate.
    ///
    /// <para>Why it matters: with a uniform 1/14 spread the picker was
    /// consistently ~1 LED behind the wheel at any given RPM, because the
    /// wheel reaches its full 14-LED bar at ~85% but uniform spread requires
    /// ~93%. That ~1-LED offset is visible in real driving when comparing
    /// the on-screen bar to the wheel.</para>
    /// </summary>
    private static readonly double[] LedLightThresholdsPercent =
    {
        0.001,                                              // LED  1 (green) — same gate as wheel: > 0
        6.0, 12.0, 18.0, 24.0,                              // LEDs 2-5 (green)
        30.0, 36.6, 43.2, 49.8, 56.4,                       // LEDs 6-10 (blue)
        63.0, 70.5, 78.0, 85.5                              // LEDs 11-14 (red)
    };

    private void ApplyLedsForRpm(double rpm)
    {
        if (_ledBrushes.Count != LedCount) return;
        double range = Math.Max(1, _endRpm - _startRpm);
        double percent = Math.Clamp((rpm - _startRpm) / range * 100.0, 0, 100);
        for (int i = 0; i < LedCount; i++)
        {
            bool lit = percent >= LedLightThresholdsPercent[i];
            // Horizontal LED bar in the header renders left-to-right
            // (UniformGrid Rows=1), so brush index = logical LED index —
            // green LEDs on the left, blue middle, red redline on the right,
            // matching how the wheel actually lights up.
            _ledBrushes[i] = lit ? LedColorAt(i) : DimLedBrush;
        }
    }

    /// <summary>
    /// Color for LED at logical index i (0 = bottom-most, LedCount-1 = top).
    /// Mirrors the physical wheel: 5 green (1-5), 5 blue (6-10), 4 red (11-14).
    /// No white redline — the wheel does not have one.
    /// </summary>
    private static Brush LedColorAt(int logicalIndex)
    {
        if (logicalIndex < 5) return GreenLedBrush;
        if (logicalIndex < 10) return BlueLedBrush;
        return RedLedBrush;
    }
}
