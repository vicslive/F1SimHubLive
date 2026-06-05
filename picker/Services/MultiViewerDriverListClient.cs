using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using F1SimHubLive.Picker.Models;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Reads MultiViewer's local DriverList and ChampionshipPrediction endpoints
/// and returns the active grid sorted by constructors' championship order
/// (teammates grouped together, then teams ranked 1st -> last), with driver
/// points used as the tiebreaker within a team.
///
/// DriverList schema (verified, Sao Paulo 2025):
///   { "44": { "RacingNumber":"44", "Tla":"HAM", "LastName":"Hamilton",
///             "FirstName":"Lewis", "TeamName":"Ferrari", "TeamColour":"E80020" }, ... }
///
/// ChampionshipPrediction schema (verified, Sao Paulo 2025):
///   {
///     "Drivers": { "1": { "RacingNumber":"1", "CurrentPosition":3, "CurrentPoints":99 }, ... },
///     "Teams":   { "McLaren Mercedes": { "TeamName":"McLaren", "CurrentPosition":1, "CurrentPoints":246 }, ... }
///   }
/// Note the Teams dict key includes the engine ("McLaren Mercedes") but the
/// inner TeamName field is the short name ("McLaren") which matches what
/// DriverList returns — so we join on the inner field.
///
/// If ChampionshipPrediction is unavailable (404, pre-season, no live timing),
/// we fall back to ordering by race number, which is still better than random.
/// </summary>
internal sealed class MultiViewerDriverListClient
{
    private readonly HttpClient _http;
    private readonly string _driverListUrl;
    private readonly string _standingsUrl;
    private readonly JolpicaStandingsClient _jolpica;

    public MultiViewerDriverListClient(string baseUrl)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string b = baseUrl.TrimEnd('/');
        _driverListUrl = b + "/api/v1/live-timing/DriverList";
        _standingsUrl = b + "/api/v1/live-timing/ChampionshipPrediction";
        _jolpica = new JolpicaStandingsClient();
    }

    public async Task<IReadOnlyList<DriverEntry>> FetchAsync(CancellationToken ct = default)
    {
        // Three-tier standings resolution:
        //   1) Jolpica (Ergast successor) — public season standings. Always
        //      reflects post-last-race totals; doesn't care whether the local
        //      session is a practice / quali / race. The right answer for the
        //      "Antonelli is leading the championship, put Mercedes at top
        //      during Monaco FP2" requirement.
        //   2) MultiViewer ChampionshipPrediction — local API. Only populated
        //      during points-bearing sessions, but works without internet.
        //   3) Alphabetical team grouping (final fallback in the sort below).
        // Fire driver list + both standings sources in parallel so the
        // additional fallback doesn't add latency to the common path.
        var driverTask = _http.GetStringAsync(_driverListUrl, ct);
        var jolpicaTask = TryFetchJolpicaAsync(ct);
        var mvStandingsTask = TryGetStandingsAsync(ct);

        string driverJson = await driverTask.ConfigureAwait(false);
        var (jTeamPos, jTeamPts, jDriverPts) = await jolpicaTask.ConfigureAwait(false);
        string? mvStandingsJson = await mvStandingsTask.ConfigureAwait(false);

        Dictionary<string, int> teamPos, teamPts, driverPts;
        if (jTeamPos.Count > 0)
        {
            teamPos = jTeamPos; teamPts = jTeamPts; driverPts = jDriverPts;
        }
        else
        {
            (teamPos, teamPts, driverPts) = ParseStandings(mvStandingsJson);
        }

        return Parse(driverJson, teamPos, teamPts, driverPts);
    }

    private async Task<(Dictionary<string, int>, Dictionary<string, int>, Dictionary<string, int>)>
        TryFetchJolpicaAsync(CancellationToken ct)
    {
        try { return await _jolpica.FetchAsync(ct).ConfigureAwait(false); }
        catch
        {
            return (new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>());
        }
    }

    private async Task<string?> TryGetStandingsAsync(CancellationToken ct)
    {
        try { return await _http.GetStringAsync(_standingsUrl, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    internal static IReadOnlyList<DriverEntry> Parse(string driverListJson, string? standingsJson)
    {
        // Back-compat overload (no Jolpica) — kept so MV-only consumers still
        // get team-ordered output when ChampionshipPrediction is populated.
        var (teamPos, teamPts, driverPts) = ParseStandings(standingsJson);
        return Parse(driverListJson, teamPos, teamPts, driverPts);
    }

    internal static IReadOnlyList<DriverEntry> Parse(
        string driverListJson,
        Dictionary<string, int> teamPos,
        Dictionary<string, int> teamPts,
        Dictionary<string, int> driverPts)
    {
        var list = new List<DriverEntry>();
        using var doc = JsonDocument.Parse(driverListJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            string number = entry.Name;
            string racing = GetString(entry.Value, "RacingNumber");
            if (!string.IsNullOrEmpty(racing)) number = racing;

            if (!int.TryParse(number, out var raceSort)) raceSort = int.MaxValue;

            string team = GetString(entry.Value, "TeamName");
            // Canonicalize the team name so a DriverList "Red Bull Racing"
            // matches a Jolpica "Red Bull" in the standings dictionaries.
            string teamKey = TeamNameAliaser.TeamKey(team);
            int tPos = teamPos.TryGetValue(teamKey, out var tp) ? tp : int.MaxValue;
            int tPts = teamPts.TryGetValue(teamKey, out var tpts) ? tpts : 0;
            int dPts = driverPts.TryGetValue(number, out var dpts) ? dpts : 0;

            list.Add(new DriverEntry
            {
                Number = number,
                Tla = GetString(entry.Value, "Tla"),
                LastName = GetString(entry.Value, "LastName"),
                FirstName = GetString(entry.Value, "FirstName"),
                TeamName = team,
                TeamColour = GetString(entry.Value, "TeamColour"),
                RacingNumberSort = raceSort,
                TeamPosition = tPos,
                TeamPoints = tPts,
                DriverPoints = dPts,
            });
        }

        // Sort key:
        //   1) Constructors' position (1 = championship leader, top of list).
        //   2) Team name (groups teammates together even when standings are
        //      unavailable — e.g. during a practice/quali session where every
        //      driver gets TeamPosition = int.MaxValue, this key becomes the
        //      dominant grouping. When standings DO exist, teammates share
        //      the same TeamPosition AND TeamName, so this key is a no-op and
        //      the points tiebreak below still picks the lead driver first).
        //   3) Driver points DESC within team (lead driver appears first).
        //   4) Race number for a deterministic tiebreak when points tie.
        return list
            .OrderBy(d => d.TeamPosition)
            .ThenBy(d => d.TeamName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(d => d.DriverPoints)
            .ThenBy(d => d.RacingNumberSort)
            .ToList();
    }

    private static (Dictionary<string, int> teamPos,
                    Dictionary<string, int> teamPts,
                    Dictionary<string, int> driverPts)
        ParseStandings(string? json)
    {
        var teamPos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var teamPts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var driverPts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return (teamPos, teamPts, driverPts);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (teamPos, teamPts, driverPts);

            if (doc.RootElement.TryGetProperty("Teams", out var teams)
                && teams.ValueKind == JsonValueKind.Object)
            {
                foreach (var t in teams.EnumerateObject())
                {
                    if (t.Value.ValueKind != JsonValueKind.Object) continue;
                    // Match drivers via the short TeamName (e.g. "McLaren"),
                    // which is what DriverList returns. The dict key includes
                    // the engine partner ("McLaren Mercedes") and won't match.
                    string shortName = GetString(t.Value, "TeamName");
                    if (string.IsNullOrEmpty(shortName)) continue;
                    string key = TeamNameAliaser.TeamKey(shortName);
                    if (TryGetInt(t.Value, "CurrentPosition", out var pos))
                        teamPos[key] = pos;
                    if (TryGetInt(t.Value, "CurrentPoints", out var pts))
                        teamPts[key] = pts;
                }
            }

            if (doc.RootElement.TryGetProperty("Drivers", out var drivers)
                && drivers.ValueKind == JsonValueKind.Object)
            {
                foreach (var d in drivers.EnumerateObject())
                {
                    if (d.Value.ValueKind != JsonValueKind.Object) continue;
                    string num = GetString(d.Value, "RacingNumber");
                    if (string.IsNullOrEmpty(num)) num = d.Name;
                    if (TryGetInt(d.Value, "CurrentPoints", out var pts))
                        driverPts[num] = pts;
                }
            }
        }
        catch
        {
            // Bad JSON or schema drift — fall through with whatever partial
            // dictionaries we built. The picker still renders in race-number
            // order in that case.
        }
        return (teamPos, teamPts, driverPts);
    }

    private static string GetString(JsonElement obj, string key)
    {
        if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    private static bool TryGetInt(JsonElement obj, string key, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) { value = i; return true; }
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var si)) { value = si; return true; }
        return false;
    }
}
