using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly string _settingsPath;
    private readonly MultiViewerDriverListClient _mv;
    private readonly DispatcherTimer _pollTimer;
    private readonly FileSystemWatcher? _settingsWatcher;
    private readonly object _watcherLock = new();
    private DateTime _lastWatcherFire = DateTime.MinValue;

    private CancellationTokenSource _ctsLifetime = new();
    private IReadOnlyList<DriverEntry> _lastDrivers = Array.Empty<DriverEntry>();
    private string _currentDriverNumber = "";

    public MainWindow()
    {
        InitializeComponent();

        var (settingsPath, mvUrl) = ParseArgs(Environment.GetCommandLineArgs());
        _settingsPath = settingsPath ?? DefaultSettingsPath();
        _mv = new MultiViewerDriverListClient(mvUrl ?? DefaultMvUrl);

        SettingsPathText.Text = _settingsPath;
        VersionText.Text = $"v{GetDisplayVersion()}";
        VersionText.ToolTip =
            $"F1SimHubLive Driver Picker v{GetDisplayVersion()}\n\n" +
            $"Settings file:\n{_settingsPath}\n\n" +
            $"MultiViewer:\n{mvUrl ?? DefaultMvUrl}";

        _currentDriverNumber = SettingsFileWriter.ReadCurrentDriverNumber(_settingsPath) ?? "";
        UpdateCurrentDriverText();

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

        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += async (_, _) => await PollDriverListAsync();

        Loaded += async (_, _) =>
        {
            await PollDriverListAsync();
            _pollTimer.Start();
            _ = CheckForUpdateAsync(); // fire-and-forget; UI updates if newer
        };

        Closed += (_, _) =>
        {
            _pollTimer.Stop();
            _settingsWatcher?.Dispose();
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
        // SimHub default install lives in Program Files (x86)\SimHub. The plugin
        // computes its settings path the same way (assembly directory), so as
        // long as we're hitting the default install we match.
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return Path.Combine(pf86, "SimHub", "F1SimHubLive.Settings.json");
    }

    private async Task PollDriverListAsync()
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ctsLifetime.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var drivers = await _mv.FetchAsync(cts.Token);
            RenderDrivers(drivers);
            StatusText.Text = $"MultiViewer: {drivers.Count} drivers · refresh {PollInterval.TotalSeconds:0}s";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"MultiViewer unavailable: {ex.GetType().Name}. Retrying every {PollInterval.TotalSeconds:0}s…";
        }
    }

    private void RenderDrivers(IReadOnlyList<DriverEntry> drivers)
    {
        // Re-mark IsCurrent based on the latest settings read so external edits
        // (reinstaller, manual edit) also light up the right row.
        var withCurrent = drivers.Select(d => new DriverEntry
        {
            Number = d.Number,
            Tla = d.Tla,
            LastName = d.LastName,
            FirstName = d.FirstName,
            TeamName = d.TeamName,
            TeamColour = d.TeamColour,
            RacingNumberSort = d.RacingNumberSort,
            TeamPosition = d.TeamPosition,
            TeamPoints = d.TeamPoints,
            DriverPoints = d.DriverPoints,
            IsCurrent = !string.IsNullOrEmpty(_currentDriverNumber)
                        && d.Number == _currentDriverNumber,
        }).ToList();
        _lastDrivers = withCurrent;
        DriverList.ItemsSource = withCurrent;
        UpdateCurrentDriverText();
    }

    private void UpdateCurrentDriverText()
    {
        if (string.IsNullOrEmpty(_currentDriverNumber))
        {
            CurrentDriverText.Text = "—";
            return;
        }
        var match = _lastDrivers.FirstOrDefault(d => d.Number == _currentDriverNumber);
        if (match != null && !string.IsNullOrEmpty(match.Tla))
            CurrentDriverText.Text = $"{match.Tla}  #{match.Number}";
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
            // Re-render so the highlight moves immediately; the next poll will
            // also confirm via DriverList refresh.
            RenderDrivers(_lastDrivers);
            StatusText.Text = $"Switched to #{number} — plugin will reload within ~250 ms.";
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                "Could not write the settings file — access denied.\n\n" +
                "Make sure the picker is running as administrator (right-click → Run as administrator).\n\n" +
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
                    RenderDrivers(_lastDrivers);
                }
            }
            catch { /* file mid-write — wait for next event */ }
        }));
    }

    private void TopmostCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb) Topmost = cb.IsChecked == true;
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
}
