using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using F1SimHubLive.MultiViewer;
using Newtonsoft.Json.Linq;

namespace F1SimHubLive.F1Replay
{
    internal enum ReplayTopic
    {
        CarData,
        DriverList,
        TrackStatus,
        Weather,
        LapCount,
    }

    /// <summary>One event on the merged replay timeline.</summary>
    internal readonly struct ReplayEvent
    {
        public readonly TimeSpan Offset;
        public readonly ReplayTopic Topic;
        public readonly JToken Payload;
        public ReplayEvent(TimeSpan offset, ReplayTopic topic, JToken payload)
        {
            Offset = offset;
            Topic = topic;
            Payload = payload;
        }
    }

    /// <summary>
    /// A whole session's recorded feed, downloaded from the archive and merged
    /// into one offset-ordered timeline that <see cref="F1ReplayClient"/> plays
    /// back through a virtual clock. Reuses the existing MultiViewer decoders —
    /// the archive payloads are byte-identical to the local-API JSON shapes.
    /// </summary>
    internal sealed class ReplayTimeline
    {
        // Topic file names under a session path. LapCount is fetched purely so
        // seek-to-lap works (and to fill CurrentLap/TotalLaps); it is tiny and
        // 404s harmlessly on practice/quali sessions that have no lap count.
        private static readonly (ReplayTopic Topic, string File)[] TopicFiles =
        {
            (ReplayTopic.CarData,     "CarData.z.jsonStream"),
            (ReplayTopic.DriverList,  "DriverList.jsonStream"),
            (ReplayTopic.TrackStatus, "TrackStatus.jsonStream"),
            (ReplayTopic.Weather,     "WeatherData.jsonStream"),
            (ReplayTopic.LapCount,    "LapCount.jsonStream"),
        };

        public IReadOnlyList<ReplayEvent> Events { get; private set; } = Array.Empty<ReplayEvent>();
        public TimeSpan TotalDuration { get; private set; } = TimeSpan.Zero;
        public int TotalLaps { get; private set; }

        /// <summary>
        /// All DriverList lines merged into one complete snapshot. The archive's
        /// first line is a minimal stub; team name/colour and first/last names
        /// arrive in later deltas, so we deep-merge them here to resolve full
        /// driver identity immediately on load / driver-switch.
        /// </summary>
        public string FirstDriverListJson { get; private set; } = "";

        private static readonly JsonMergeSettings MergeReplace =
            new() { MergeArrayHandling = MergeArrayHandling.Replace, MergeNullValueHandling = MergeNullValueHandling.Ignore };

        // lap number -> session offset at which that lap began (sorted).
        private readonly SortedDictionary<int, TimeSpan> _lapStarts = new();

        public static async Task<ReplayTimeline> LoadAsync(
            ArchiveClient archive, string sessionPath, Action<string> log, CancellationToken ct = default)
        {
            var timeline = new ReplayTimeline();

            var streams = await Task.WhenAll(TopicFiles.Select(async tf =>
            {
                string text = await archive.GetTopicStreamAsync(sessionPath, tf.File, ct).ConfigureAwait(false);
                return (tf.Topic, Lines: JsonStreamParser.Parse(text));
            })).ConfigureAwait(false);

            var merged = new List<ReplayEvent>();
            var driverListMerged = new JObject();
            foreach (var (topic, lines) in streams)
            {
                if (lines.Count == 0)
                {
                    log($"replay timeline: {topic} empty/missing");
                    continue;
                }

                foreach (var rl in lines)
                {
                    merged.Add(new ReplayEvent(rl.Offset, topic, rl.Payload));
                    if (topic == ReplayTopic.LapCount)
                        timeline.IndexLap(rl.Offset, rl.Payload);
                    else if (topic == ReplayTopic.DriverList && rl.Payload is JObject dl)
                        driverListMerged.Merge(dl, MergeReplace);
                }
            }

            // The archive's first DriverList line is a minimal stub (TLA + name);
            // TeamName / TeamColour / first+last name arrive in later delta lines.
            // Merge them all so the wheel resolves full identity — including the
            // team accent colour the dashboard styles on — the instant we load.
            timeline.FirstDriverListJson = driverListMerged.ToString();

            // Stable sort by offset so same-instant events keep file order.
            timeline.Events = merged.OrderBy(e => e.Offset).ToList();
            timeline.TotalDuration = timeline.Events.Count > 0
                ? timeline.Events[timeline.Events.Count - 1].Offset
                : TimeSpan.Zero;

            log($"replay timeline loaded: {timeline.Events.Count} events, " +
                $"duration {timeline.TotalDuration:hh\\:mm\\:ss}, laps {timeline.TotalLaps}");
            return timeline;
        }

        private void IndexLap(TimeSpan offset, JToken payload)
        {
            var (current, total) = LapCountDecoder.Parse(payload.ToString());
            if (total > TotalLaps) TotalLaps = total;
            if (current > 0 && !_lapStarts.ContainsKey(current))
                _lapStarts[current] = offset;
        }

        /// <summary>
        /// Session offset at which a given lap began. Falls back to the nearest
        /// known lap at or below the request, then to zero, so seek-to-lap is
        /// always well-defined even with a sparse LapCount stream.
        /// </summary>
        public TimeSpan OffsetForLap(int lap)
        {
            if (lap <= 1 || _lapStarts.Count == 0) return TimeSpan.Zero;
            if (_lapStarts.TryGetValue(lap, out var exact)) return exact;
            TimeSpan best = TimeSpan.Zero;
            foreach (var kv in _lapStarts)
            {
                if (kv.Key <= lap) best = kv.Value;
                else break;
            }
            return best;
        }

        /// <summary>Index of the first event at or after <paramref name="position"/> (binary search).</summary>
        public int IndexAtOrAfter(TimeSpan position)
        {
            int lo = 0, hi = Events.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Events[mid].Offset < position) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }
}
