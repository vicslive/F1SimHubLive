using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace F1SimHubLive.Picker.Models;

/// <summary>
/// Bindable model for the session header bar at the top of the picker.
/// Mirrors what MultiViewer Live Timing shows above its driver list:
/// country flag, race name+type, session clock, lap counter (race only),
/// and the live track status pill (Clear / Yellow / SC / VSC / Red).
/// </summary>
public sealed class SessionHeaderModel : INotifyPropertyChanged
{
    private ImageSource? _countryFlagImage;
    private string _raceName = "";
    private string _lapText = "";
    private string _timeText = "";
    private string _trackStatusText = "";
    private string _trackStatusBackground = "#3A3A44";
    private string _trackStatusForeground = "#FFFFFF";
    private bool _hasSession;

    /// <summary>
    /// Rendered country flag PNG (loaded from <c>Assets/Flags/&lt;iso2&gt;.png</c>).
    /// Null when the country code is unknown or before the first SessionInfo
    /// poll lands.
    /// </summary>
    public ImageSource? CountryFlagImage
    {
        get => _countryFlagImage;
        set { if (!ReferenceEquals(_countryFlagImage, value)) { _countryFlagImage = value; Raise(); } }
    }

    public string RaceName
    {
        get => _raceName;
        set { if (_raceName != value) { _raceName = value; Raise(); } }
    }

    public string LapText
    {
        get => _lapText;
        set { if (_lapText != value) { _lapText = value; Raise(); } }
    }

    public string TimeText
    {
        get => _timeText;
        set { if (_timeText != value) { _timeText = value; Raise(); } }
    }

    public string TrackStatusText
    {
        get => _trackStatusText;
        set { if (_trackStatusText != value) { _trackStatusText = value; Raise(); } }
    }

    public string TrackStatusBackground
    {
        get => _trackStatusBackground;
        set { if (_trackStatusBackground != value) { _trackStatusBackground = value; Raise(); } }
    }

    public string TrackStatusForeground
    {
        get => _trackStatusForeground;
        set { if (_trackStatusForeground != value) { _trackStatusForeground = value; Raise(); } }
    }

    /// <summary>
    /// True once we've received at least one successful SessionInfo response.
    /// Bound to header bar Visibility so it doesn't render an empty strip
    /// before MV is ready.
    /// </summary>
    public bool HasSession
    {
        get => _hasSession;
        set { if (_hasSession != value) { _hasSession = value; Raise(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
