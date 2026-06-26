using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using F1SimHubLive.Picker.Models;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Read-only access to F1's public live-timing static archive index
/// (<c>livetiming.formula1.com/static/&lt;year&gt;/Index.json</c>) for the
/// picker's session browser. No F1 TV subscription, no MultiViewer, no auth —
/// this is the same recorded data feed the plugin's <c>F1Replay</c> source
/// reads. DATA only, never video.
///
/// <para>Every archive response carries a UTF-8 BOM that breaks naive JSON
/// parsing, so <see cref="StripBom"/> runs on each body. F1's edge empties
/// responses for unknown agents, so we pin the same <c>BestHTTP</c> User-Agent
/// the live SignalR client uses.</para>
/// </summary>
public sealed class ArchiveIndexClient : IDisposable
{
    private const string Root = "https://livetiming.formula1.com/static/";

    private readonly HttpClient _http;

    public ArchiveIndexClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BestHTTP");
    }

    /// <summary>
    /// Fetches the season index and flattens it to a session list ready for a
    /// dropdown, newest meeting first. Each session carries its owning meeting
    /// name for display. Sessions with no archive <c>Path</c> are dropped —
    /// they have no recorded data to replay.
    /// </summary>
    public async Task<IReadOnlyList<ArchiveSession>> GetSessionsAsync(int year, CancellationToken ct = default)
    {
        string url = $"{Root}{year}/Index.json";
        string body = await GetStringAsync(url, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<ArchiveSession>();

        var idx = JsonConvert.DeserializeObject<ArchiveSeasonIndex>(body);
        if (idx?.Meetings == null) return Array.Empty<ArchiveSession>();

        var list = new List<ArchiveSession>();
        foreach (var m in idx.Meetings)
        {
            if (m.Sessions == null) continue;
            foreach (var s in m.Sessions)
            {
                if (string.IsNullOrWhiteSpace(s.Path)) continue;
                s.MeetingName = string.IsNullOrWhiteSpace(m.Name) ? m.OfficialName : m.Name;
                list.Add(s);
            }
        }

        // Index.json is chronological; show most-recent meetings/sessions first.
        list.Reverse();
        return list;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return "";
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return StripBom(body);
    }

    internal static string StripBom(string s)
        => !string.IsNullOrEmpty(s) && s[0] == '\uFEFF' ? s.Substring(1) : s;

    public void Dispose() => _http.Dispose();
}
