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
        private const double Alpha = 0.05;     // EMA weight per sample at the state rate
        private const double ReanchorAt = 1.0; // seconds of deviation that mean a step, not jitter

        private static readonly Dictionary<byte, double> Offset = new Dictionary<byte, double>();

        public static void Reset() => Offset.Clear();

        /// <summary>Sender time (ms of their unscaled clock) -> local timeline.</summary>
        public static float ToLocalTime(byte senderSlot, uint senderMs)
        {
            double sender = senderMs / 1000.0;
            double sample = Time.unscaledTimeAsDouble - sender;
            if (Offset.TryGetValue(senderSlot, out double offset) && System.Math.Abs(sample - offset) < ReanchorAt)
                offset += Alpha * (sample - offset);
            else
                offset = sample;
            Offset[senderSlot] = offset;
            return (float)(sender + offset);
        }
    }
}
