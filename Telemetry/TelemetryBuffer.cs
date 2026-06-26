using System;
using System.Collections.Generic;

namespace F1SimHubLive.Telemetry
{
    /// <summary>
    /// Holds the most-recent telemetry snapshots for the interpolator.
    ///
    /// <para>Two access modes:</para>
    /// <list type="bullet">
    ///   <item><see cref="Snapshot"/> returns the latest <c>prev</c>/<c>curr</c>
    ///   pair — the zero-extra-delay fast path the interpolator has always
    ///   used.</item>
    ///   <item><see cref="PairAt"/> returns the pair bracketing an arbitrary
    ///   point in the recent past, which the interpolator uses when a
    ///   broadcast-sync delay is configured (so the data can be replayed a few
    ///   seconds late to match a delayed video feed). Backed by a small
    ///   time-trimmed history kept to <see cref="RetentionMs"/>.</item>
    /// </list>
    /// </summary>
    internal sealed class TelemetryBuffer
    {
        private DriverSnapshot? _prev;
        private DriverSnapshot? _curr;
        private readonly List<DriverSnapshot> _history = new();
        private readonly object _lock = new();

        /// <summary>
        /// How much snapshot history to retain (ms). Default keeps a small
        /// window (enough for the default render delay) so memory stays flat
        /// when no broadcast delay is in use. The plugin raises this when a
        /// broadcast delay is configured so <see cref="PairAt"/> can reach back
        /// far enough.
        /// </summary>
        public int RetentionMs { get; set; } = 1500;

        public void Push(DriverSnapshot snapshot)
        {
            lock (_lock)
            {
                _prev = _curr;
                _curr = snapshot;

                _history.Add(snapshot);
                TrimLocked(snapshot.Utc);
            }
        }

        public (DriverSnapshot? prev, DriverSnapshot? curr) Snapshot()
        {
            lock (_lock)
            {
                return (_prev, _curr);
            }
        }

        /// <summary>
        /// Returns the two snapshots bracketing <paramref name="target"/>
        /// (prev.Utc &lt;= target &lt; curr.Utc), for delayed playback. If
        /// <paramref name="target"/> is older than all history, returns the
        /// oldest twice; if newer than all, returns the newest twice (the
        /// interpolator then just shows the newest sample, i.e. it hasn't been
        /// delayed yet).
        /// </summary>
        public (DriverSnapshot? prev, DriverSnapshot? curr) PairAt(DateTime target)
        {
            lock (_lock)
            {
                int n = _history.Count;
                if (n == 0) return (null, null);
                if (n == 1) return (_history[0], _history[0]);

                if (target <= _history[0].Utc) return (_history[0], _history[0]);
                var last = _history[n - 1];
                if (target >= last.Utc) return (last, last);

                // Linear scan from the newest end — history is short (a few
                // seconds) so this stays cheap and avoids comparer allocation.
                for (int i = n - 1; i >= 1; i--)
                {
                    if (_history[i - 1].Utc <= target && target < _history[i].Utc)
                        return (_history[i - 1], _history[i]);
                }
                return (_history[n - 2], last);
            }
        }

        private void TrimLocked(DateTime newestUtc)
        {
            // Keep one extra sample beyond the retention window so a target at
            // the very edge still has a prev to bracket against.
            DateTime cutoff = newestUtc.AddMilliseconds(-RetentionMs);
            int drop = 0;
            while (drop + 1 < _history.Count && _history[drop + 1].Utc < cutoff)
                drop++;
            if (drop > 0)
                _history.RemoveRange(0, drop);
        }
    }
}
