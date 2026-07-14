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
        private float _jitter;
        private float _pressure;

        internal AdaptiveSnapshotTiming(float minimum, float maximum, float initialInterval)
        {
            _minimum = minimum;
            _maximum = maximum;
            _interval = initialInterval;
        }

        internal float Delay => Mathf.Clamp(_interval * 1.35f + _jitter * 2.5f + _pressure,
            _minimum, _maximum);

        internal void Reset()
        {
            _initialized = false;
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
