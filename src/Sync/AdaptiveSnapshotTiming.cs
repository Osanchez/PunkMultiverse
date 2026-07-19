using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>Per-puppet interpolation delay derived from sender cadence and arrival jitter.
    /// Stable streams render close to one snapshot behind; bursty streams gain enough headroom
    /// to avoid alternating extrapolation/correction. This changes presentation only.</summary>
    internal sealed class AdaptiveSnapshotTiming
    {
        private readonly float _minimum;
        private readonly float _maximum;
        private bool _initialized;
        private float _lastSenderTime;
        private float _lastArrivalTime;
        private float _interval;
        private float _gapPeak;
        private float _jitter;
        private float _pressure;

        internal AdaptiveSnapshotTiming(float minimum, float maximum, float initialInterval)
        {
            _minimum = minimum;
            _maximum = maximum;
            _interval = initialInterval;
        }

        // The delay must clear the WORST recent sender gap, not the mean: the priority
        // accumulator legitimately alternates an entity's cadence (e.g. 33/66ms for a mid
        // weight), and a mean-based margin (interval*1.35 = 17ms of headroom on a 50ms
        // stream) leaves render time overtaking the newest snapshot on every long gap —
        // measured as ~800 interpolation underruns/s, each one an extrapolate-then-yank
        // micro-pop. The decaying peak covers alternating cadences exactly without
        // over-delaying steady ones.
        internal float Delay => Mathf.Clamp(
            Mathf.Max(_interval * 1.35f, _gapPeak * 1.2f) + _jitter * 2.5f + _pressure,
            _minimum, _maximum);

        internal void Reset()
        {
            _initialized = false;
            _gapPeak = 0f;
            _pressure = 0f;
        }

        internal void Observe(float senderTime)
        {
            float arrival = Time.unscaledTime;
            if (_initialized)
            {
                float senderDelta = Mathf.Clamp(senderTime - _lastSenderTime, 0.001f, 0.5f);
                float arrivalDelta = Mathf.Clamp(arrival - _lastArrivalTime, 0.001f, 0.5f);
                _interval = Mathf.Lerp(_interval, senderDelta, 0.12f);
                // Decaying max: remembers the worst gap for ~2s of samples, then forgets —
                // a one-off hiccup doesn't inflate the delay forever.
                _gapPeak = Mathf.Max(senderDelta, _gapPeak * 0.985f);
                _jitter = Mathf.Lerp(_jitter, Mathf.Abs(arrivalDelta - senderDelta), 0.10f);
                _pressure = Mathf.Max(0f, _pressure - 0.0005f);
            }
            else
            {
                _initialized = true;
            }
            _lastSenderTime = senderTime;
            _lastArrivalTime = arrival;
            InstrumentationCounters.AdaptiveTimingSample(Delay, _jitter);
        }

        internal void NoteUnderrun()
        {
            _pressure = Mathf.Min(_maximum * 0.4f, _pressure + 0.004f);
            InstrumentationCounters.InterpolationUnderrun();
        }
    }
}
