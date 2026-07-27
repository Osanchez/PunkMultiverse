using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>Per-puppet interpolation delay derived from sender cadence and arrival jitter.
    /// Stable streams render close to one snapshot behind; bursty streams gain enough headroom
    /// to avoid alternating extrapolation/correction. This changes presentation only.</summary>
    internal sealed class AdaptiveSnapshotTiming
    {
        // PERCENTILE PLAYOUT TARGETING (NetEQ-style, 2026-07-24): the old formula padded the
        // delay with jitter*2.5 — an EMA heuristic whose safety multiplier overshoots for the
        // common case. The principled version keeps a window of observed per-snapshot LATENESS
        // (arrival spacing minus sender spacing, positive = arrived late) and targets the delay
        // at its p98: exactly enough headroom to cover 98% of observed deliveries, re-derived
        // continuously from real traffic instead of guessed multipliers. Underrun pressure
        // still nudges upward when the tail misbehaves (the remaining 2%).
        private const int LateWindow = 64; // ~3s of samples at 20Hz

        private readonly float _minimum;
        private readonly float _maximum;
        private readonly float[] _lateness = new float[LateWindow];
        private readonly float[] _sortScratch = new float[LateWindow];
        private int _lateCount;
        private int _lateNext;
        private float _latenessP98;
        private bool _initialized;
        private float _lastSenderTime;
        private float _lastArrivalTime;
        private float _interval;
        private float _gapPeak;
        private float _jitter; // retained for instrumentation continuity ([SnapshotLatency] jitterAvg)
        private float _pressure;

        // Ship puppets report their samples separately as well as into the pooled average: a
        // player's ship is the only puppet whose staleness a human is aiming at, and it is
        // drowned out by the hundreds of entity puppets sharing the pooled counters.
        private readonly bool _isShip;

        internal AdaptiveSnapshotTiming(float minimum, float maximum, float initialInterval,
            bool isShip = false)
        {
            _minimum = minimum;
            _maximum = maximum;
            _interval = initialInterval;
            _isShip = isShip;
        }

        /// <summary>Current interpolation delay in ms — how far in the past this puppet is drawn.</summary>
        internal float DelayMs => Delay * 1000f;

        /// <summary>True when the delay is pinned at its ceiling, i.e. the formula wants MORE
        /// headroom than the puppet kind allows. Sustained saturation means visible skipping,
        /// because the buffer is being asked to cover more lateness than it is permitted to.</summary>
        internal bool Saturated => Delay >= _maximum - 0.0005f;

        // The delay must clear the WORST recent sender gap, not the mean: the priority
        // accumulator legitimately alternates an entity's cadence (e.g. 33/66ms for a mid
        // weight). gapPeak covers sender-side cadence; the p98 lateness term covers the
        // network's actual delivery distribution.
        internal float Delay => Mathf.Clamp(
            Mathf.Max(_interval * 1.35f, _gapPeak * 1.2f) + _latenessP98 * 1.15f + _pressure,
            _minimum, _maximum);

        internal void Reset()
        {
            _initialized = false;
            _gapPeak = 0f;
            _pressure = 0f;
            _lateCount = 0;
            _lateNext = 0;
            _latenessP98 = 0f;
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

                // Lateness sample -> ring -> p98. Sorting 64 floats per snapshot is cheap and
                // allocation-free (persistent scratch); early samples use what's filled.
                _lateness[_lateNext] = Mathf.Max(0f, arrivalDelta - senderDelta);
                _lateNext = (_lateNext + 1) % LateWindow;
                if (_lateCount < LateWindow) _lateCount++;
                System.Array.Copy(_lateness, _sortScratch, _lateCount);
                System.Array.Sort(_sortScratch, 0, _lateCount);
                _latenessP98 = _sortScratch[Mathf.Clamp((int)(_lateCount * 0.98f), 0, _lateCount - 1)];
            }
            else
            {
                _initialized = true;
            }
            _lastSenderTime = senderTime;
            _lastArrivalTime = arrival;
            InstrumentationCounters.AdaptiveTimingSample(Delay, _jitter, _isShip);
            if (_isShip && Saturated) InstrumentationCounters.ShipDelaySaturated();
        }

        internal void NoteUnderrun()
        {
            _pressure = Mathf.Min(_maximum * 0.4f, _pressure + 0.004f);
            InstrumentationCounters.InterpolationUnderrun(_isShip);
        }
    }
}
