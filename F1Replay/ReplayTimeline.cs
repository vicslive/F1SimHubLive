using System;
using System.Collections.Generic;
using System.Globalization;
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
        ExtrapolatedClock,
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
            // The on-screen session clock. Lets the picker anchor data to the
            // video by the official "P2 59:20"-style countdown — the only sync
            // reference shared with a DRM video where lap count doesn't exist
            // (practice / qualifying).
            (ReplayTopic.ExtrapolatedClock, "ExtrapolatedClock.jsonStream"),
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

        // Session-clock samples (offset, remaining-seconds, extrapolating), in
        // offset order. Built by merging the ExtrapolatedClock delta stream.
        private readonly List<(TimeSpan Offset, double RemainingSec, bool Extrap)> _clockSamples = new();
        private double _clockRemSec = double.NaN;
        private bool _clockExtrap;

        /// <summary>True when this session carries an ExtrapolatedClock topic.</summary>
        public bool HasSessionClock => _clockSamples.Count > 0;

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
                    else if (topic == ReplayTopic.ExtrapolatedClock)
                        timeline.IndexClock(rl.Offset, rl.Payload);
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

        // Merge an ExtrapolatedClock delta and record a sample whenever the
        // remaining time or the extrapolating flag changes. Deltas carry only
        // changed fields, so we track running state across the stream.
        private void IndexClock(TimeSpan offset, JToken payload)
        {
            if (payload is not JObject o) return;
            bool changed = false;
            var rem = o["Remaining"];
            if (rem != null && rem.Type == JTokenType.String)
            {
                var parsed = ParseClock((string)rem!);
                if (parsed.HasValue) { _clockRemSec = parsed.Value; changed = true; }
            }
            var ex = o["Extrapolating"];
            if (ex != null && ex.Type == JTokenType.Boolean)
            {
                _clockExtrap = ex.Value<bool>();
                changed = true;
            }
            if (changed && !double.IsNaN(_clockRemSec))
                _clockSamples.Add((offset, _clockRemSec, _clockExtrap));
        }

        private static double? ParseClock(string s)
        {
            var p = s.Split(':');
            if (p.Length != 3) return null;
            if (!int.TryParse(p[0], out var h)) return null;
            if (!int.TryParse(p[1], out var m)) return null;
            if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return null;
            return h * 3600 + m * 60 + sec;
        }

        /// <summary>
        /// Maps an on-screen session-clock value (time remaining) to the data
        /// offset where the feed showed that value. Returns null when the session
        /// has no clock topic. While the clock is running it counts down 1:1 with
        /// real time, so within a running segment the offset is linear; frozen
        /// segments (red flags) hold a constant value.
        /// </summary>
        public TimeSpan? OffsetForRemaining(TimeSpan remaining)
        {
            if (_clockSamples.Count == 0) return null;
            double target = remaining.TotalSeconds;
            for (int i = 0; i < _clockSamples.Count - 1; i++)
            {
                var a = _clockSamples[i];
                var b = _clockSamples[i + 1];
                if (a.Extrap)
                {
                    double remAtA = a.RemainingSec;
                    double remAtB = a.RemainingSec - (b.Offset - a.Offset).TotalSeconds;
                    if (target <= remAtA + 0.5 && target >= remAtB - 0.5)
                        return a.Offset + TimeSpan.FromSeconds(remAtA - target);
                }
                else if (Math.Abs(a.RemainingSec - target) < 0.5)
                {
                    return a.Offset;
                }
            }
            var last = _clockSamples[_clockSamples.Count - 1];
            if (last.Extrap)
            {
                double off = last.Offset.TotalSeconds + (last.RemainingSec - target);
                return TimeSpan.FromSeconds(Math.Max(0, off));
            }
            return last.Offset;
        }

        /// <summary>
        /// The session clock (time remaining) at a given data offset — the inverse
        /// of <see cref="OffsetForRemaining"/>. Lets the picker show the live
        /// official clock so the user can confirm the anchor. Null when no clock.
        /// </summary>
        public TimeSpan? RemainingAt(TimeSpan position)
        {
            if (_clockSamples.Count == 0) return null;
            // Last sample at or before the position.
            int idx = -1;
            for (int i = 0; i < _clockSamples.Count; i++)
            {
                if (_clockSamples[i].Offset <= position) idx = i;
                else break;
            }
            if (idx < 0) idx = 0;
            var s = _clockSamples[idx];
            double rem = s.Extrap
                ? s.RemainingSec - (position - s.Offset).TotalSeconds
                : s.RemainingSec;
            if (rem < 0) rem = 0;
            return TimeSpan.FromSeconds(rem);
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
