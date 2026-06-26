using System;
using System.Threading;

namespace F1SimHubLive.Telemetry
{
    internal sealed class Interpolator : IDisposable
    {
        private readonly TelemetryBuffer _buffer;
        private readonly int _hz;
        private readonly int _renderDelayMs;
        private Timer? _timer;

        public DriverSnapshot? Latest { get; private set; }

        /// <summary>
        /// Extra hold (ms) applied on top of <see cref="_renderDelayMs"/> so the
        /// rendered data lags a delayed video feed. 0 = no extra delay (the
        /// classic prev/curr fast path). Settable at runtime (the plugin updates
        /// it from settings hot-reload and zeroes it while in replay). Plain int
        /// read/write is atomic on the CLR, so no lock is needed for the flag.
        /// </summary>
        public int BroadcastDelayMs { get; set; }

        public Interpolator(TelemetryBuffer buffer, int hz, int renderDelayMs)
        {
            _buffer = buffer;
            _hz = hz < 1 ? 1 : hz;
            _renderDelayMs = renderDelayMs < 0 ? 0 : renderDelayMs;
        }

        public void Start()
        {
            int periodMs = System.Math.Max(1, 1000 / _hz);
            _timer = new Timer(_ => Tick(), null, 0, periodMs);
        }

        private void Tick()
        {
            int broadcast = BroadcastDelayMs;
            var (prev, curr) = broadcast > 0
                ? _buffer.PairAt(DateTime.UtcNow.AddMilliseconds(-(_renderDelayMs + broadcast)))
                : _buffer.Snapshot();
            if (curr == null) return;
            if (prev == null)
            {
                Latest = curr;
                return;
            }

            // Render slightly in the past so prev/curr usually bracket our render time.
            // At 4 Hz samples (~250 ms apart), a 200 ms render delay keeps us inside the window.
            // A configured broadcast delay extends that lag so the data lines up with a
            // delayed video feed (e.g. F1 TV 4K on Apple TV).
            DateTime renderTime = DateTime.UtcNow.AddMilliseconds(-(_renderDelayMs + broadcast));
            double dtMs = (curr.Utc - prev.Utc).TotalMilliseconds;
            if (dtMs <= 0)
            {
                Latest = curr;
                return;
            }

            double u = (renderTime - prev.Utc).TotalMilliseconds / dtMs;
            if (u < 0) u = 0;
            if (u > 1.0) u = 1.0;

            Latest = new DriverSnapshot
            {
                Utc = renderTime,
                DriverNumber = curr.DriverNumber,
                Rpm = Lerp(prev.Rpm, curr.Rpm, u),
                Speed = Lerp(prev.Speed, curr.Speed, u),
                Throttle = Lerp(prev.Throttle, curr.Throttle, u),
                Brake = Lerp(prev.Brake, curr.Brake, u),
                Gear = u < 0.5 ? prev.Gear : curr.Gear,
                Drs = u < 0.5 ? prev.Drs : curr.Drs,
            };
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        public void Dispose() => _timer?.Dispose();
    }
}
