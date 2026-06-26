using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.F1Replay
{
    /// <summary>
    /// Read-only HTTP access to F1's public live-timing static archive at
    /// <c>livetiming.formula1.com/static/</c>. This is the same recorded
    /// timing/telemetry feed the free Live Timing app and the community
    /// tooling (FastF1 et al.) read — no F1 TV subscription, no MultiViewer,
    /// no authentication. It carries DATA only (no video).
    ///
    /// Two responsibilities:
    ///   1. <see cref="GetSeasonIndexAsync"/> — the per-year Index.json listing
    ///      every meeting + session with a relative Path.
    ///   2. <see cref="GetTopicStreamAsync"/> — a single .jsonStream / .json
    ///      topic file for one session, returned as raw text for the parser.
    ///
    /// All responses are served with a UTF-8 BOM that breaks naive JSON
    /// parsing, so <see cref="StripBom"/> runs on every body.
    /// </summary>
    internal sealed class ArchiveClient : IDisposable
    {
        private const string Root = "https://livetiming.formula1.com/static/";

        private readonly HttpClient _http;
        private readonly Action<string> _log;

        public ArchiveClient(Action<string> log)
        {
            _log = log ?? (_ => { });
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // F1's edge rejects/empties responses for unknown agents; BestHTTP
            // is the agent the live SignalR client also pins (see F1SignalRClient).
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("BestHTTP");
        }

        /// <summary>
        /// Builds the absolute URL for a topic file under a session path.
        /// <paramref name="sessionPath"/> is the index's relative Path, e.g.
        /// <c>2024/2024-09-01_Italian_Grand_Prix/2024-09-01_Race/</c>.
        /// </summary>
        public static string TopicUrl(string sessionPath, string topicFile)
            => Root + sessionPath.TrimEnd('/') + "/" + topicFile;

        public async Task<SeasonIndex> GetSeasonIndexAsync(int year, CancellationToken ct = default)
        {
            string url = $"{Root}{year}/Index.json";
            string body = await GetStringAsync(url, ct).ConfigureAwait(false);
            var idx = JsonConvert.DeserializeObject<SeasonIndex>(body)
                      ?? new SeasonIndex { Year = year, Meetings = new List<MeetingInfo>() };
            idx.Meetings ??= new List<MeetingInfo>();
            return idx;
        }

        public Task<string> GetTopicStreamAsync(string sessionPath, string topicFile, CancellationToken ct = default)
            => GetStringAsync(TopicUrl(sessionPath, topicFile), ct);

        /// <summary>
        /// GETs a URL and returns the BOM-stripped body. Returns empty string
        /// on any failure (a missing optional topic must not abort a load).
        /// </summary>
        public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log($"archive GET {url} -> HTTP {(int)resp.StatusCode}");
                    return "";
                }
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return StripBom(body);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"archive GET {url} failed: {ex.Message}");
                return "";
            }
        }

        internal static string StripBom(string s)
        {
            if (!string.IsNullOrEmpty(s) && s[0] == '\uFEFF') return s.Substring(1);
            return s;
        }

        public void Dispose() => _http.Dispose();
    }

    // ----- Index.json shapes (only the fields we use) -----------------------

    internal sealed class SeasonIndex
    {
        public int Year { get; set; }
        public List<MeetingInfo>? Meetings { get; set; }
    }

    internal sealed class MeetingInfo
    {
        public int Key { get; set; }
        public string Name { get; set; } = "";
        public string OfficialName { get; set; } = "";
        public string Location { get; set; } = "";
        public string Country { get; set; } = "";
        public List<SessionInfo>? Sessions { get; set; }
    }

    internal sealed class SessionInfo
    {
        public int Key { get; set; }
        public string Type { get; set; } = "";
        public int Number { get; set; }
        public string Name { get; set; } = "";
        public string StartDate { get; set; } = "";
        public string EndDate { get; set; } = "";
        public string GmtOffset { get; set; } = "";
        public string Path { get; set; } = "";
    }
}
