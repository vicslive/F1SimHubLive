using System.Collections.Generic;
using Newtonsoft.Json;

namespace F1SimHubLive.Picker.Models;

// Shapes for F1's live-timing static archive Index.json
// (livetiming.formula1.com/static/<year>/Index.json). Only the fields the
// picker's session browser needs. Mirrors the plugin-side ArchiveClient POCOs
// (F1Replay/ArchiveClient.cs) so both sides agree on the wire format.

public sealed class ArchiveSeasonIndex
{
    public int Year { get; set; }
    public List<ArchiveMeeting>? Meetings { get; set; }
}

public sealed class ArchiveMeeting
{
    public int Key { get; set; }
    public string Name { get; set; } = "";
    public string OfficialName { get; set; } = "";
    public string Location { get; set; } = "";
    public List<ArchiveSession>? Sessions { get; set; }
}

public sealed class ArchiveSession
{
    public int Key { get; set; }
    public string Type { get; set; } = "";
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public string GmtOffset { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>Owning meeting name, set after parse for flat list display.</summary>
    [JsonIgnore]
    public string MeetingName { get; set; } = "";

    /// <summary>"Italian Grand Prix — Race" style label for the session dropdown.</summary>
    [JsonIgnore]
    public string DisplayLabel =>
        string.IsNullOrEmpty(MeetingName) ? FriendlyType : $"{MeetingName} — {FriendlyType}";

    private string FriendlyType => string.IsNullOrWhiteSpace(Name) ? Type : Name;

    // Both the dropdown items and the closed selection box render via ToString(),
    // so the label shows without depending on DisplayMemberPath (which the custom
    // dark ComboBox template doesn't propagate to the selection box).
    public override string ToString() => DisplayLabel;
}
