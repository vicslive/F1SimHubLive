using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Fallback championship standings source used when MultiViewer's local
/// ChampionshipPrediction endpoint returns no data — which happens through
/// every non-points session (FP1/FP2/FP3, qualifying) because that endpoint
/// is a race-projection feature, not a season-totals feature.
///
/// We hit the Jolpica F1 API (the modern, maintained replacement for the
/// retired Ergast public API — same schema):
///   https://api.jolpi.ca/ergast/f1/current/constructorstandings.json
///   https://api.jolpi.ca/ergast/f1/current/driverstandings.json
///
/// The picker then orders drivers by real constructors' championship position
/// (Mercedes-Antonelli-leading-the-grid order during a Monaco FP2, not
/// alphabetical-team-name order). One-hour in-memory cache keeps the API
/// untouched within a session; standings only ever change after a Sunday race.
///
/// No network → return empty dicts and the caller falls back further (which
/// in <see cref="MultiViewerDriverListClient"/> is alphabetical team grouping).
/// </summary>
internal sealed class JolpicaStandingsClient
{
    private const string ConstructorsUrl = "https://api.jolpi.ca/ergast/f1/current/constructorstandings.json";
    private const string DriversUrl      = "https://api.jolpi.ca/ergast/f1/current/driverstandings.json";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static StandingsCache? _cache;

    private readonly HttpClient _http;

    public JolpicaStandingsClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // Public API; identify ourselves so the operators can correlate traffic
        // if they need to. Falls back silently to no user-agent on any header
        // rejection (some intermediates reject the empty product version).
        try { _http.DefaultRequestHeaders.UserAgent.ParseAdd("F1SimHubLive-Picker/1.3.0 (+github.com/vicslive/F1SimHubLive)"); }
        catch { }
    }

    /// <summary>
    /// Returns standings as (teamPos, teamPts, driverPts) dictionaries —
    /// same shape as MultiViewer's ChampionshipPrediction parser, so the
    /// caller can drop these into the same sort pipeline. Dictionary keys
    /// are canonical team keys (see <see cref="TeamNameAliaser.TeamKey"/>);
    /// driver keys are race numbers (string).
    ///
    /// On any failure (no network, API down, schema drift), returns empty
    /// dictionaries — never throws. Caller falls back to its next strategy.
    /// </summary>
    public async Task<(Dictionary<string, int> teamPos,
                       Dictionary<string, int> teamPts,
                       Dictionary<string, int> driverPts)>
        FetchAsync(CancellationToken ct = default)
    {
        // In-memory cache: standings only change once per Sunday race weekend,
        // so a one-hour TTL is generous. Without this the picker would hit
        // Jolpica every 5 seconds (the picker poll cadence).
        await CacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache != null && DateTime.UtcNow - _cache.FetchedAt < CacheTtl)
            {
                return (_cache.TeamPos, _cache.TeamPts, _cache.DriverPts);
            }

            var fresh = await FetchUncachedAsync(ct).ConfigureAwait(false);
            // Only cache non-empty results so a single transient API failure
            // doesn't poison the cache for the next hour.
            if (fresh.teamPos.Count > 0)
            {
                _cache = new StandingsCache(DateTime.UtcNow, fresh.teamPos, fresh.teamPts, fresh.driverPts);
            }
            return fresh;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private async Task<(Dictionary<string, int> teamPos,
                        Dictionary<string, int> teamPts,
                        Dictionary<string, int> driverPts)>
        FetchUncachedAsync(CancellationToken ct)
    {
        var teamPos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var teamPts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var driverPts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string? constructorsJson = await TryGetStringAsync(ConstructorsUrl, ct).ConfigureAwait(false);
        string? driversJson      = await TryGetStringAsync(DriversUrl, ct).ConfigureAwait(false);

        ParseConstructors(constructorsJson, teamPos, teamPts);
        ParseDrivers(driversJson, driverPts);

        return (teamPos, teamPts, driverPts);
    }

    private async Task<string?> TryGetStringAsync(string url, CancellationToken ct)
    {
        try { return await _http.GetStringAsync(url, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private static void ParseConstructors(
        string? json,
        Dictionary<string, int> teamPos,
        Dictionary<string, int> teamPts)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            // Schema: MRData.StandingsTable.StandingsLists[0].ConstructorStandings[]
            //   { position, points, Constructor: { name } }
            using var doc = JsonDocument.Parse(json);
            if (!TryDescend(doc.RootElement,
                    new[] { "MRData", "StandingsTable", "StandingsLists" }, out var lists))
                return;
            if (lists.ValueKind != JsonValueKind.Array || lists.GetArrayLength() == 0) return;

            var list = lists[0];
            if (!list.TryGetProperty("ConstructorStandings", out var standings)) return;
            if (standings.ValueKind != JsonValueKind.Array) return;

            foreach (var entry in standings.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("Constructor", out var ctor)) continue;
                string name = GetString(ctor, "name");
                if (string.IsNullOrEmpty(name)) continue;
                string key = TeamNameAliaser.TeamKey(name);
                if (TryGetIntString(entry, "position", out var pos)) teamPos[key] = pos;
                if (TryGetIntString(entry, "points",   out var pts)) teamPts[key] = pts;
            }
        }
        catch
        {
            // Schema drift or malformed payload — fall through with whatever
            // partial dictionaries we built. Caller has its own fallback.
        }
    }

    private static void ParseDrivers(string? json, Dictionary<string, int> driverPts)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            // Schema: MRData.StandingsTable.StandingsLists[0].DriverStandings[]
            //   { points, Driver: { permanentNumber, code } }
            using var doc = JsonDocument.Parse(json);
            if (!TryDescend(doc.RootElement,
                    new[] { "MRData", "StandingsTable", "StandingsLists" }, out var lists))
                return;
            if (lists.ValueKind != JsonValueKind.Array || lists.GetArrayLength() == 0) return;

            var list = lists[0];
            if (!list.TryGetProperty("DriverStandings", out var standings)) return;
            if (standings.ValueKind != JsonValueKind.Array) return;

            foreach (var entry in standings.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("Driver", out var drv)) continue;
                string num = GetString(drv, "permanentNumber");
                if (string.IsNullOrEmpty(num)) continue;
                if (TryGetIntString(entry, "points", out var pts)) driverPts[num] = pts;
            }
        }
        catch
        {
            // Same fallback strategy as constructors.
        }
    }

    private static bool TryDescend(JsonElement root, string[] path, out JsonElement result)
    {
        result = root;
        foreach (var key in path)
        {
            if (result.ValueKind != JsonValueKind.Object) return false;
            if (!result.TryGetProperty(key, out var next)) return false;
            result = next;
        }
        return true;
    }

    private static string GetString(JsonElement obj, string key)
    {
        if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    private static bool TryGetIntString(JsonElement obj, string key, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(key, out var v)) return false;
        // Ergast/Jolpica returns numbers as JSON strings (legacy schema quirk).
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var si))
        { value = si; return true; }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
        { value = i; return true; }
        return false;
    }

    private sealed record StandingsCache(
        DateTime FetchedAt,
        Dictionary<string, int> TeamPos,
        Dictionary<string, int> TeamPts,
        Dictionary<string, int> DriverPts);
}

/// <summary>
/// Maps the many different ways an F1 team gets named across data sources
/// (MultiViewer DriverList, MultiViewer ChampionshipPrediction, Jolpica/Ergast
/// public API) to a single canonical lowercase key so dictionaries built from
/// one source can be looked up using strings from another source.
///
/// Unknown teams fall through with their lowercased original — conservative
/// behavior (won't mis-map a future team into an existing one).
/// </summary>
internal static class TeamNameAliaser
{
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // canonical key on the right; observed names on the left
            { "mercedes",                       "mercedes" },
            { "mercedes amg petronas",          "mercedes" },

            { "ferrari",                        "ferrari" },
            { "scuderia ferrari",               "ferrari" },

            { "mclaren",                        "mclaren" },
            { "mclaren mercedes",               "mclaren" },
            { "mclaren f1 team",                "mclaren" },

            { "red bull",                       "redbull" },
            { "red bull racing",                "redbull" },
            { "oracle red bull racing",         "redbull" },
            { "red bull racing honda rbpt",     "redbull" },

            { "alpine",                         "alpine" },
            { "alpine f1 team",                 "alpine" },
            { "alpine renault",                 "alpine" },
            { "bwt alpine f1 team",             "alpine" },

            { "rb",                             "rb" },
            { "rb f1 team",                     "rb" },
            { "racing bulls",                   "rb" },
            { "visa cash app rb",               "rb" },
            { "visa cash app racing bulls f1 team", "rb" },
            { "vcarb",                          "rb" },

            { "haas",                           "haas" },
            { "haas f1 team",                   "haas" },
            { "haas ferrari",                   "haas" },
            { "moneygram haas f1 team",         "haas" },

            { "williams",                       "williams" },
            { "williams mercedes",              "williams" },
            { "williams racing",                "williams" },
            { "atlassian williams racing",      "williams" },

            { "audi",                           "audi" },
            { "audi f1 team",                   "audi" },
            { "sauber",                         "audi" }, // pre-2026 rebrand
            { "kick sauber",                    "audi" }, // pre-2026 rebrand
            { "stake f1 team kick sauber",      "audi" }, // pre-2026 rebrand

            { "cadillac",                       "cadillac" },
            { "cadillac f1 team",               "cadillac" },

            { "aston martin",                   "aston" },
            { "aston martin aramco",            "aston" },
            { "aston martin aramco mercedes",   "aston" },
            { "aston martin aramco f1 team",    "aston" },
        };

    public static string TeamKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string n = raw.Trim().ToLowerInvariant();
        return Aliases.TryGetValue(n, out var key) ? key : n;
    }
}
