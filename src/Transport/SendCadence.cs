using System.Diagnostics;
using System.Threading;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// Send-vs-receive discriminator. Closes the one caveat left in the stall investigation: the
    /// server demonstrably freezes for 340-670ms roughly every 0.95s, and the client demonstrably
    /// sees ship snapshots arrive in clumps with ~450ms silences — but "the freeze causes the
    /// clumps" was inference, not measurement. Nothing so far distinguishes a freeze INSIDE the
    /// server process from a smooth server whose packets are clumped by the wire, the host network
    /// stack, or an intermediate hop.
    ///
    /// This records, on the relay thread at the exact moment of the send:
    ///   * gap  — high-resolution interval between consecutive ShipState sends
    ///   * call — how long the Send call itself took
    /// The client records the matching arrival spacing (see NetStats/[RecvCadence]).
    ///
    /// Reading the result:
    ///   send gaps show ~500ms holes  -> the freeze is inside the server process, and because this
    ///                                   runs on the RELAY thread (not the main thread), a hole
    ///                                   here means the pause stops ALL threads — which points at
    ///                                   a stop-the-world event or host-level descheduling rather
    ///                                   than anything in the game loop.
    ///   send gaps smooth, client clumped -> the server is innocent; the problem is the wire, the
    ///                                   host network stack, or a hop in between.
    ///
    /// Stopwatch is used deliberately: it is the high-resolution monotonic clock on this runtime
    /// (QueryPerformanceCounter under Wine), unaffected by wall-clock changes, and readable from a
    /// non-Unity thread — Time.* is not.
    /// </summary>
    internal static class SendCadence
    {
        private static long _lastSendTicks;
        private static long _count, _gapTicksTotal, _gapTicksMax, _callTicksTotal, _callTicksMax;
        private static long _gapsOver100, _gapsOver250;

        internal static long Mark() => Stopwatch.GetTimestamp();

        internal static void NoteSend(long sendStartTicks)
        {
            long now = Stopwatch.GetTimestamp();
            long call = now - sendStartTicks;
            Interlocked.Add(ref _callTicksTotal, call);
            Max(ref _callTicksMax, call);

            long prev = Interlocked.Exchange(ref _lastSendTicks, now);
            if (prev != 0)
            {
                long gap = now - prev;
                Interlocked.Increment(ref _count);
                Interlocked.Add(ref _gapTicksTotal, gap);
                Max(ref _gapTicksMax, gap);
                double ms = gap * 1000.0 / Stopwatch.Frequency;
                if (ms > 250.0) Interlocked.Increment(ref _gapsOver250);
                else if (ms > 100.0) Interlocked.Increment(ref _gapsOver100);
            }
        }

        private static void Max(ref long target, long value)
        {
            long seen = Interlocked.Read(ref target);
            while (value > seen)
            {
                long was = Interlocked.CompareExchange(ref target, value, seen);
                if (was == seen) break;
                seen = was;
            }
        }

        /// <summary>Drains the window and formats it; returns null when nothing was sent.</summary>
        internal static string DrainReport()
        {
            long n = Interlocked.Exchange(ref _count, 0);
            long gapTotal = Interlocked.Exchange(ref _gapTicksTotal, 0);
            long gapMax = Interlocked.Exchange(ref _gapTicksMax, 0);
            long callTotal = Interlocked.Exchange(ref _callTicksTotal, 0);
            long callMax = Interlocked.Exchange(ref _callTicksMax, 0);
            long over100 = Interlocked.Exchange(ref _gapsOver100, 0);
            long over250 = Interlocked.Exchange(ref _gapsOver250, 0);
            if (n <= 0) return null;
            double toMs = 1000.0 / Stopwatch.Frequency;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[SendCadence] shipSends={0} gapAvg={1:0.0}ms gapMax={2:0.0}ms over100ms={3} " +
                "over250ms={4} sendCallAvg={5:0.000}ms sendCallMax={6:0.0}ms",
                n, gapTotal * toMs / n, gapMax * toMs, over100, over250,
                callTotal * toMs / n, callMax * toMs);
        }
    }
}
