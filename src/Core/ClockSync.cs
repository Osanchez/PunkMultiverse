using System.Collections.Generic;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Maps a sender's snapshot timestamps onto the local clock. Snapshots carry the SENDER's
    /// time so puppets interpolate with the sender's even 20 Hz spacing — keying the buffer on
    /// receive time (the old way) replays every network jitter spike as motion stutter. The
    /// per-sender offset (clock delta + mean transit time) is EMA-smoothed; the interpolation
    /// delay absorbs what remains. A step larger than a second (pause, hitch, reconnect)
    /// re-anchors instead of chasing.
    /// </summary>
    internal static class ClockSync
    {
        // MILLS-STYLE CLOCK FILTER (NTP's core insight, 2026-07-24): each sample is
        //   offset_i = localArrival_i - senderTime_i = trueOffset + meanTransit + noise_i
        // and the noise is ONE-SIDED — a packet can be delayed, never delivered early. So the
        // MINIMUM sample over a recent window is the least-noisy estimate available, and the
        // old EMA-of-every-sample let every queueing spike wobble the mapped timeline (which
        // read as sender jitter and inflated every puppet's interpolation delay).
        //
        // Per sender: a ring of the last WindowSize samples; the published offset chases the
        // window MINIMUM through a slow slew (clamped to SlewPerSample) so drift is tracked
        // without steps; a deviation beyond ReanchorAt (pause/reconnect) re-anchors outright.
        private const int WindowSize = 32;         // ~1.6s of samples at the 20Hz state rate
        private const double SlewPerSample = 0.002; // max published-offset movement per sample (s)
        private const double ReanchorAt = 1.0;      // step, not jitter — re-anchor

        private sealed class Filter
        {
            public readonly double[] Window = new double[WindowSize];
            public int Count;      // filled entries (ring valid range)
            public int Next;       // ring write index
            public double Offset;  // published (slewed) offset
        }

        private static readonly Dictionary<byte, Filter> Filters = new Dictionary<byte, Filter>();

        public static void Reset() => Filters.Clear();

        /// <summary>Sender time (ms of their unscaled clock) -> local timeline.</summary>
        public static float ToLocalTime(byte senderSlot, uint senderMs)
        {
            double sender = senderMs / 1000.0;
            double sample = Time.unscaledTimeAsDouble - sender;

            if (!Filters.TryGetValue(senderSlot, out var f))
                Filters[senderSlot] = f = new Filter { Offset = sample };

            if (System.Math.Abs(sample - f.Offset) >= ReanchorAt)
            {
                // Step (pause, reconnect, clock jump): restart the filter at the new regime.
                f.Offset = sample;
                f.Count = 0;
                f.Next = 0;
            }

            f.Window[f.Next] = sample;
            f.Next = (f.Next + 1) % WindowSize;
            if (f.Count < WindowSize) f.Count++;

            // Window minimum = the cleanest recent packet. Chase it with a bounded slew.
            double min = double.PositiveInfinity;
            for (int i = 0; i < f.Count; i++)
                if (f.Window[i] < min) min = f.Window[i];
            double delta = min - f.Offset;
            if (delta > SlewPerSample) delta = SlewPerSample;
            else if (delta < -SlewPerSample) delta = -SlewPerSample;
            double mapped = sender + f.Offset; // map with the PRE-slew offset (stable timeline)
            f.Offset += delta;
            return (float)mapped;
        }

        /// <summary>Map an event without letting its one-off network jitter perturb snapshot clock calibration.</summary>
        public static float MapToLocalTime(byte senderSlot, uint senderMs)
        {
            double sender = senderMs / 1000.0;
            return Filters.TryGetValue(senderSlot, out var f)
                ? (float)(sender + f.Offset)
                : Time.unscaledTime;
        }
    }
}
