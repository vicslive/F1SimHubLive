using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace F1SimHubLive.Picker.Models;

/// <summary>
/// One row in the live-timing view. Mutable + INPC because the data
/// (lap times, gaps, sectors, tire age) changes every 500ms while a
/// session is running.
/// </summary>
public sealed class DriverTimingRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- identity (set once at row creation, treated as stable) ----
    public string RacingNumber { get; init; } = "";
    public string Tla { get; init; } = "";
    public string LastName { get; init; } = "";
    public string TeamName { get; init; } = "";
    /// <summary>Hex without leading '#', e.g. "F47600".</summary>
    public string TeamColour { get; init; } = "";

    // ---- mutable state below: changes every 500ms ----

    private int _position;
    public int Position
    {
        get => _position;
        set => SetField(ref _position, value);
    }

    private string _lastLapTime = "";
    /// <summary>Last lap time formatted as "1:14.537" or "" if not set.</summary>
    public string LastLapTime
    {
        get => _lastLapTime;
        set => SetField(ref _lastLapTime, value);
    }

    private LapStatus _lastLapStatus;
    public LapStatus LastLapStatus
    {
        get => _lastLapStatus;
        set => SetField(ref _lastLapStatus, value);
    }

    private string _bestLapTime = "";
    public string BestLapTime
    {
        get => _bestLapTime;
        set => SetField(ref _bestLapTime, value);
    }

    private LapStatus _bestLapStatus;
    public LapStatus BestLapStatus
    {
        get => _bestLapStatus;
        set => SetField(ref _bestLapStatus, value);
    }

    private string _gapToLeader = "";
    /// <summary>Gap to leader (e.g. "+0.401"). "LDR" for the leader. "" if unknown.</summary>
    public string GapToLeader
    {
        get => _gapToLeader;
        set => SetField(ref _gapToLeader, value);
    }

    private string _intervalToAhead = "";
    public string IntervalToAhead
    {
        get => _intervalToAhead;
        set => SetField(ref _intervalToAhead, value);
    }

    private bool _inPit;
    public bool InPit
    {
        get => _inPit;
        set => SetField(ref _inPit, value);
    }

    private bool _retired;
    public bool Retired
    {
        get => _retired;
        set => SetField(ref _retired, value);
    }

    private string _tireCompoundLetter = "";
    /// <summary>"H" / "M" / "S" / "I" / "W" — single letter for the badge.</summary>
    public string TireCompoundLetter
    {
        get => _tireCompoundLetter;
        set => SetField(ref _tireCompoundLetter, value);
    }

    private string _tireCompoundColor = "#7F7F8A";
    /// <summary>Hex with leading '#', used as the background for the tire badge.</summary>
    public string TireCompoundColor
    {
        get => _tireCompoundColor;
        set => SetField(ref _tireCompoundColor, value);
    }

    private int _tireAge;
    /// <summary>Total laps on the current set (TotalLaps from MV stints).</summary>
    public int TireAge
    {
        get => _tireAge;
        set => SetField(ref _tireAge, value);
    }

    private int _pitStopCount;
    public int PitStopCount
    {
        get => _pitStopCount;
        set => SetField(ref _pitStopCount, value);
    }

    private bool _isCurrent;
    /// <summary>True for the driver currently written in settings.json.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetField(ref _isCurrent, value);
    }

    /// <summary>
    /// Per-sector data with segment colors. Pre-allocated to 3 entries so
    /// the XAML ItemsControl bindings stay stable across updates.
    /// </summary>
    public ObservableCollection<SectorView> Sectors { get; } = new()
    {
        new SectorView(),
        new SectorView(),
        new SectorView(),
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public enum LapStatus
{
    None,
    PersonalBest,
    SessionBest,
}

/// <summary>
/// One sector (1, 2, or 3) within a lap, with its time and segment colors.
/// </summary>
public sealed class SectorView : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _time = "";
    public string Time
    {
        get => _time;
        set => SetField(ref _time, value);
    }

    private LapStatus _status;
    public LapStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    /// <summary>
    /// One entry per mini-sector segment (typically 4–10 per sector,
    /// varies by track). Status codes follow MV's encoding:
    ///   0    = dark / no data
    ///   2048 = yellow (personal best)
    ///   2049 = purple (session best)
    ///   2051 = pit (blue)
    ///   2064 = green (improving)
    /// </summary>
    public ObservableCollection<int> Segments { get; } = new();

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
