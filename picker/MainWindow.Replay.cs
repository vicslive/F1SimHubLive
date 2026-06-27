using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using F1SimHubLive.Picker.Models;
using F1SimHubLive.Picker.Services;

namespace F1SimHubLive.Picker;

/// <summary>
/// On-demand Replay panel: browses F1's public live-timing static archive and
/// drives the plugin's <c>F1Replay</c> source over the file-based command/status
/// channel (<see cref="ReplayControlClient"/>). DATA only — the 4K video is owned
/// by Apple TV / MultiViewer. Sync to the video is manual: anchor once to the
/// on-screen lap, fine-nudge ±0.5 s, then both run at 1× (negligible drift).
/// </summary>
public partial class MainWindow
{
    private ArchiveIndexClient? _archive;
    private ReplayControlClient? _replayControl;
    private DispatcherTimer? _replayStatusTimer;

    private IReadOnlyList<ArchiveSession> _archiveSessions = Array.Empty<ArchiveSession>();
    private bool _archiveLoaded;            // session list fetched at least once
    private bool _replaySettingScrubber;    // true while a status tick assigns the scrubber Value
    private bool _replayScrubbing;          // true while the user drags the scrubber thumb

    private string _currentReplaySessionPath = "";
    private int _pendingPrefSeekSec = -1;   // >=0 = apply after the session reports Loaded
    private double _pendingPrefSpeed = 1.0;
    private bool _replayWasLoaded;          // edge-detect Loaded transitions
    private DateTime _lastPrefSave = DateTime.MinValue;

    private DispatcherTimer? _broadcastDelayWriteTimer;
    private bool _suppressBroadcastDelayWrite;  // true while seeding the slider from settings

    // ----- replay grid (all drivers; replaces the MV-fed grid while in replay)
    private readonly ObservableCollection<DriverTimingRow> _replayRows = new();
    private readonly Dictionary<string, DriverTimingRow> _replayRowMap = new();
    private bool _replayGridMode;   // true while the grid is bound to _replayRows

    private void InitReplay()
    {
        _archive = new ArchiveIndexClient();
        _replayControl = new ReplayControlClient(_settingsPath);

        // Year dropdown: current season back to 2018 (first season in the
        // static archive). Newest first.
        int thisYear = DateTime.UtcNow.Year;
        for (int y = thisYear; y >= 2018; y--)
            ReplayYearCombo.Items.Add(y);
        ReplayYearCombo.SelectedIndex = 0;

        // Wire scrubber ValueChanged in code-behind so it doesn't fire during
        // InitializeComponent before our fields exist.
        ReplayScrubber.ValueChanged += ReplayScrubber_ValueChanged;

        // Live-video-delay slider: seed from settings, then write (debounced)
        // on change. Seeding under the suppress flag so the seed doesn't echo
        // straight back to disk.
        _broadcastDelayWriteTimer = new DispatcherTimer { Interval = SliderWriteDelay };
        _broadcastDelayWriteTimer.Tick += BroadcastDelayWriteTimer_Tick;
        _suppressBroadcastDelayWrite = true;
        try
        {
            int ms = SettingsFileWriter.ReadBroadcastDelayMs(_settingsPath);
            ReplayBroadcastDelaySlider.Value = Math.Clamp(ms / 1000.0, 0, 30);
            ReplayBroadcastDelayText.Text = $"{(int)Math.Round(ReplayBroadcastDelaySlider.Value)} s";
        }
        finally
        {
            _suppressBroadcastDelayWrite = false;
        }
        ReplayBroadcastDelaySlider.ValueChanged += ReplayBroadcastDelaySlider_ValueChanged;

        _replayStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _replayStatusTimer.Tick += ReplayStatusTimer_Tick;
        _replayStatusTimer.Start();

        Closed += (_, _) =>
        {
            _replayStatusTimer?.Stop();
            _broadcastDelayWriteTimer?.Stop();
        };
    }

    // ----- panel visibility / lazy load ------------------------------------

    private async void ReplayToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool show = ReplayToggle.IsChecked == true;
        ReplayPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && !_archiveLoaded)
            await LoadSessionsAsync();
    }

    private async void ReplayYearCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ignore the initial selection set during InitReplay (panel still hidden).
        if (!_archiveLoaded && ReplayPanel.Visibility != Visibility.Visible) return;
        await LoadSessionsAsync();
    }

    private async void ReplayRefreshButton_Click(object sender, RoutedEventArgs e)
        => await LoadSessionsAsync();

    private async Task LoadSessionsAsync()
    {
        if (_archive == null) return;
        if (ReplayYearCombo.SelectedItem is not int year) return;

        ReplayStateText.Text = $"loading {year}…";
        ReplaySessionCombo.ItemsSource = null;
        try
        {
            var sessions = await _archive.GetSessionsAsync(year);
            _archiveSessions = sessions;
            _archiveLoaded = true;
            ReplaySessionCombo.ItemsSource = sessions;
            if (sessions.Count > 0) ReplaySessionCombo.SelectedIndex = 0;
            ReplayStateText.Text = sessions.Count == 0
                ? $"{year}: no sessions"
                : $"{year}: {sessions.Count} sessions";
        }
        catch (Exception ex)
        {
            ReplayStateText.Text = "archive error";
            StatusText.Text = $"Replay archive fetch failed: {ex.Message}";
        }
    }

    // ----- load / go-live --------------------------------------------------

    private void ReplayLoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_replayControl == null) return;
        if (ReplaySessionCombo.SelectedItem is not ArchiveSession s) return;

        _currentReplaySessionPath = s.Path;
        _replayControl.Load(s.Path, s.DisplayLabel);

        // Restore the per-session anchor (last position + speed) once the
        // plugin reports the session Loaded.
        var pref = _replayControl.GetPref(s.Path);
        _pendingPrefSeekSec = pref.LastPositionSec > 0 ? pref.LastPositionSec : -1;
        _pendingPrefSpeed = pref.Speed > 0 ? pref.Speed : 1.0;
        _replayWasLoaded = false;

        ReplayStateText.Text = $"loading: {s.DisplayLabel}";
    }

    private void ReplayGoLiveButton_Click(object sender, RoutedEventArgs e)
    {
        _replayControl?.GoLive();
        _currentReplaySessionPath = "";
        _pendingPrefSeekSec = -1;
        ReplayStateText.Text = "live";
    }

    // ----- transport -------------------------------------------------------

    private void ReplayPlayPauseButton_Click(object sender, RoutedEventArgs e)
        => _replayControl?.TogglePlay();

    private void ReplaySpeed_Click(object sender, RoutedEventArgs e)
    {
        if (_replayControl == null) return;
        if (sender is Button b && b.Tag is string tag &&
            double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            _replayControl.SetSpeed(speed);
        }
    }

    // ----- scrubber --------------------------------------------------------

    private void ReplayScrubber_DragStarted(object sender, DragStartedEventArgs e)
        => _replayScrubbing = true;

    private void ReplayScrubber_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _replayScrubbing = false;
        _replayControl?.Seek(ReplayScrubber.Value);
    }

    private void ReplayScrubber_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_replaySettingScrubber) return;   // programmatic update from status tick
        if (_replayScrubbing)
        {
            // Live preview of the target time while dragging; seek fires on release.
            UpdatePositionText((int)ReplayScrubber.Value, (int)ReplayScrubber.Maximum);
            return;
        }
        // Click-to-point jump (no drag): seek immediately.
        _replayControl?.Seek(e.NewValue);
    }

    // ----- sync: lap anchor + fine nudge -----------------------------------

    private void ReplaySyncLapButton_Click(object sender, RoutedEventArgs e) => SyncToLap();

    private void ReplayLapBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SyncToLap();
    }

    private void SyncToLap()
    {
        if (_replayControl == null) return;
        if (int.TryParse(ReplayLapBox.Text?.Trim(), out var lap) && lap >= 1)
            _replayControl.SeekToLap(lap);
    }

    // ----- sync: on-screen session clock (primary anchor) ------------------

    private void ReplaySyncClockButton_Click(object sender, RoutedEventArgs e) => SyncToClock();

    private void ReplayClockBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SyncToClock();
    }

    private void SyncToClock()
    {
        if (_replayControl == null) return;
        var rem = ParseSessionClock(ReplayClockBox.Text);
        if (rem.HasValue) _replayControl.SeekToClock(rem.Value);
    }

    // Accepts "mm:ss" or "h:mm:ss" (the on-screen P-clock counts time remaining).
    private static TimeSpan? ParseSessionClock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length < 2 || parts.Length > 3) return null;
        int h = 0, m, s;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], out h) ||
                !int.TryParse(parts[1], out m) ||
                !int.TryParse(parts[2], out s)) return null;
        }
        else
        {
            if (!int.TryParse(parts[0], out m) ||
                !int.TryParse(parts[1], out s)) return null;
        }
        if (m < 0 || s < 0 || s > 59) return null;
        return new TimeSpan(h, m, s);
    }

    private void ReplayNudgeBack_Click(object sender, RoutedEventArgs e) => Nudge(-0.5);
    private void ReplayNudgeFwd_Click(object sender, RoutedEventArgs e) => Nudge(+0.5);

    private void Nudge(double deltaSeconds)
    {
        if (_replayControl == null) return;
        double target = Math.Max(0, ReplayScrubber.Value + deltaSeconds);
        _replayControl.Seek(target);
    }

    // ----- status polling (plugin -> picker) -------------------------------

    private void ReplayStatusTimer_Tick(object? sender, EventArgs e)
    {
        if (_replayControl == null) return;
        var st = _replayControl.ReadStatus();

        if (st == null || !st.Loaded)
        {
            ReplayPlayPauseButton.Content = "▶";
            if (ReplayToggle.IsChecked == true && _archiveLoaded &&
                ReplayStateText.Text.StartsWith("loading", StringComparison.OrdinalIgnoreCase) == false &&
                string.IsNullOrEmpty(_currentReplaySessionPath))
            {
                // leave whatever browse status is showing
            }
            _replayWasLoaded = false;
            ExitReplayGridMode();
            return;
        }

        // Apply persisted anchor on the rising edge of Loaded.
        if (!_replayWasLoaded)
        {
            _replayWasLoaded = true;
            if (_pendingPrefSpeed > 0 && Math.Abs(_pendingPrefSpeed - 1.0) > 0.001)
                _replayControl.SetSpeed(_pendingPrefSpeed);
            if (_pendingPrefSeekSec >= 0)
            {
                _replayControl.Seek(_pendingPrefSeekSec);
                _pendingPrefSeekSec = -1;
            }
        }

        int dur = Math.Max(1, st.DurationSec);
        if (Math.Abs(ReplayScrubber.Maximum - dur) > 0.5)
            ReplayScrubber.Maximum = dur;

        if (!_replayScrubbing)
        {
            _replaySettingScrubber = true;
            ReplayScrubber.Value = Math.Min(st.PositionSec, dur);
            _replaySettingScrubber = false;
            UpdatePositionText(st.PositionSec, dur);
        }

        ReplayPlayPauseButton.Content = st.Playing ? "⏸" : "▶";
        ReplayStateText.Text = st.TotalLaps > 0
            ? $"Lap {st.CurrentLap}/{st.TotalLaps} · {FormatSpeed(st.Speed)}"
            : $"{FormatClock(st.PositionSec)} · {FormatSpeed(st.Speed)}";

        // Live official session clock (time remaining) so the user can confirm
        // the anchor matches the on-screen P-clock.
        if (st.HasClock && st.RemainingSec >= 0)
            ReplaySessionClockText.Text = "P " + FormatClock(st.RemainingSec);
        else
            ReplaySessionClockText.Text = "";

        // Persist the per-session anchor (~every 5 s) so reload resumes here.
        if (!string.IsNullOrEmpty(_currentReplaySessionPath) &&
            (DateTime.UtcNow - _lastPrefSave) > TimeSpan.FromSeconds(5))
        {
            _lastPrefSave = DateTime.UtcNow;
            _replayControl.SavePref(_currentReplaySessionPath, st.PositionSec, st.Speed);
        }

        // All-driver grid: bind to the replay rows and refresh from the plugin's
        // grid snapshot so the user sees the whole field and can switch drivers
        // without MultiViewer.
        EnterReplayGridMode();
        UpdateReplayGrid();
    }

    // ----- replay grid feed (plugin -> picker) -----------------------------

    private void EnterReplayGridMode()
    {
        if (_replayGridMode) return;
        _replayGridMode = true;
        DriverList.ItemsSource = _replayRows;
        StatusText.Text = "Replay: all drivers · telemetry (no live timing)";
    }

    private void ExitReplayGridMode()
    {
        if (!_replayGridMode) return;
        _replayGridMode = false;
        DriverList.ItemsSource = _liveTimingClient.Rows;
        _replayRows.Clear();
        _replayRowMap.Clear();
    }

    private void UpdateReplayGrid()
    {
        if (_replayControl == null) return;
        var grid = _replayControl.ReadGrid();
        if (grid.Count == 0) return;

        // Structural change (different driver set / order) -> rebuild the rows.
        bool structureChanged = grid.Count != _replayRows.Count;
        if (!structureChanged)
        {
            for (int i = 0; i < grid.Count; i++)
            {
                if (_replayRows[i].RacingNumber != grid[i].Num) { structureChanged = true; break; }
            }
        }
        if (structureChanged)
        {
            _replayRows.Clear();
            _replayRowMap.Clear();
            foreach (var g in grid)
            {
                var row = new DriverTimingRow { RacingNumber = g.Num };
                _replayRows.Add(row);
                _replayRowMap[g.Num] = row;
            }
        }

        // Update mutable fields in place (INPC -> WPF refreshes the cells).
        foreach (var g in grid)
        {
            if (!_replayRowMap.TryGetValue(g.Num, out var row)) continue;
            row.Tla = g.Tla;
            row.LastName = g.LastName;
            row.TeamName = g.TeamName;
            row.TeamColour = g.TeamColour;
            row.Rpm = g.Rpm;
            row.SpeedKmh = g.Speed;
            row.Gear = g.Gear;
            row.Throttle = g.Throttle;
            row.IsCurrent = !string.IsNullOrEmpty(_currentDriverNumber) && g.Num == _currentDriverNumber;
        }
    }

    private void UpdatePositionText(int posSec, int durSec)
        => ReplayPositionText.Text = $"{FormatClock(posSec)} / {FormatClock(durSec)}";

    private static string FormatClock(int totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        var t = TimeSpan.FromSeconds(totalSeconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string FormatSpeed(double speed)
    {
        // 0.5× / 1× / 2× without trailing zeros.
        string s = speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return s + "×";
    }

    // ----- live video delay slider -----------------------------------------

    private void ReplayBroadcastDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int seconds = (int)Math.Round(e.NewValue);
        ReplayBroadcastDelayText.Text = $"{seconds} s";
        if (_suppressBroadcastDelayWrite) return;
        // Debounce: snapping across the slider fires many ticks; write once the
        // user settles (mirrors the shift-light slider write cadence).
        _broadcastDelayWriteTimer?.Stop();
        _broadcastDelayWriteTimer?.Start();
    }

    private void BroadcastDelayWriteTimer_Tick(object? sender, EventArgs e)
    {
        _broadcastDelayWriteTimer?.Stop();
        int seconds = (int)Math.Round(ReplayBroadcastDelaySlider.Value);
        try
        {
            SettingsFileWriter.WriteBroadcastDelayMs(_settingsPath, seconds * 1000);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save live video delay: {ex.Message}";
        }
    }
}
