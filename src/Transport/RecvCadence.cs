using System.Diagnostics;
using System.Threading;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// Client half of the send-vs-receive test (see <see cref="SendCadence"/>). Records the
    /// spacing between ShipState packets as they land on the SOCKET thread — before the event
    /// queue, before the drain, before anything in the game loop can colour the number. Measuring
    /// after the drain would fold the client's own frame pacing in and could not tell "arrived
    /// late" from "processed late".
    ///
    /// Compare `gapMax`/`over250ms` here against the server's [SendCadence] for the same run:
    /// matching holes mean the server froze; holes here with none there mean the wire.
    /// </summary>
    internal static class RecvCadence
    {
        private static long _lastTicks;
        private static long _count, _gapTicksTotal, _gapTicksMax, _over100, _over250;

        internal static void NoteArrival()
        {
            long now = Stopwatch.GetTimestamp();
            long prev = Interlocked.Exchange(ref _lastTicks, now);
            if (prev == 0) return;
            long gap = now - prev;
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _gapTicksTotal, gap);
            long seen = Interlocked.Read(ref _gapTicksMax);
            while (gap > seen)
            {
                long was = Interlocked.CompareExchange(ref _gapTicksMax, gap, seen);
                if (was == seen) break;
                seen = was;
            }
            double ms = gap * 1000.0 / Stopwatch.Frequency;
            if (ms > 250.0) Interlocked.Increment(ref _over250);
            else if (ms > 100.0) Interlocked.Increment(ref _over100);
        }

        internal static string DrainReport()
        {
            long n = Interlocked.Exchange(ref _count, 0);
            long total = Interlocked.Exchange(ref _gapTicksTotal, 0);
            long max = Interlocked.Exchange(ref _gapTicksMax, 0);
            long over100 = Interlocked.Exchange(ref _over100, 0);
            long over250 = Interlocked.Exchange(ref _over250, 0);
            if (n <= 0) return null;
            double toMs = 1000.0 / Stopwatch.Frequency;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[RecvCadence] shipArrivals={0} gapAvg={1:0.0}ms gapMax={2:0.0}ms over100ms={3} over250ms={4}",
                n, total * toMs / n, max * toMs, over100, over250);
        }
    }
}
