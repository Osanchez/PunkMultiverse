using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Two long-run health detectors that piggyback the periodic instrumentation report:
    ///
    ///  1. GROWTH WATCHDOG (`[Growth]`) — the base [GC] line shows heap size, but a slow leak or an
    ///     unbounded queue only reads as noise interval-to-interval. This tracks a curated set of
    ///     collection sizes (things that should stay BOUNDED in steady state — send outbox, visual
    ///     pickup maps, captured-loot map, live replicas) plus the heap over a rolling window, and
    ///     flags any metric that climbs in most intervals AND nets meaningful growth. That answers
    ///     "what is growing over time" directly instead of guessing from a rising heap.
    ///
    ///  2. JITTER DETECTOR (`[Jitter]`) — a puppet whose interpolation target oscillates (rapid
    ///     back-and-forth) is the "enemies vibrating" artifact. RemoteEntityPuppet reports its
    ///     reversal rate; this aggregates and names the worst offenders each interval.
    ///
    /// Both are read + cleared by RuntimeInstrumentation.Report. Cheap: a dozen doubles and a small
    /// dictionary, sampled at the report cadence (seconds), never per-frame here.
    /// </summary>
    internal static class DiagWatch
    {
        // ---------------------------------------------------------------- growth watchdog

        private sealed class Metric
        {
            public string Name;
            public Func<double> Get;
            public double Floor;                 // ignore growth while below this (kills near-zero wobble)
            public readonly List<double> History = new List<double>(GrowthWindow + 1);
        }

        private const int GrowthWindow = 12;     // samples kept; window seconds = report interval * 12
        private static readonly List<Metric> Metrics = new List<Metric>();
        private static bool _registered;

        /// <summary>Register a bounded-in-steady-state size to trend. `floor` suppresses flags while
        /// the value is small (normal churn near zero must not read as a leak).</summary>
        internal static void Register(string name, Func<double> getter, double floor)
        {
            Metrics.Add(new Metric { Name = name, Get = getter, Floor = floor });
        }

        private static void EnsureRegistered()
        {
            if (_registered) return;
            _registered = true;
            // Heap is the master leak signal; the rest localize it to a subsystem.
            Register("heapMiB", () => GC.GetTotalMemory(false) / 1048576.0, floor: 40);
            try { RegisterDefaults?.Invoke(); } catch { }
        }

        /// <summary>Subsystems attach their own size getters here (wired at plugin init) so DiagWatch
        /// needs no compile-time reference to every collection.</summary>
        internal static Action RegisterDefaults;

        // A run's first ~20s is warmup — heap and streamed collections climb hard from zero as the
        // world loads, then plateau. Judging across that window flags every session-start as a
        // "leak". Skip it (and drop the warmup samples) so a [Growth] line means POST-warmup growth,
        // which is the real leak/queue signal. Re-armed at each go-live (incl. rejoin).
        private static int _warmupSkips;
        internal static void NotifyRunStarted()
        {
            _warmupSkips = 7; // * report interval (~3s) = ~21s
            foreach (var m in Metrics) m.History.Clear();
            JitterPeak.Clear();
        }

        /// <summary>Append one line per metric that shows sustained growth. Called each report.</summary>
        internal static void ReportGrowth(double mono)
        {
            EnsureRegistered();
            if (_warmupSkips > 0) { _warmupSkips--; foreach (var m in Metrics) m.History.Clear(); return; }
            foreach (var m in Metrics)
            {
                double v;
                try { v = m.Get(); } catch { continue; }
                m.History.Add(v);
                if (m.History.Count > GrowthWindow) m.History.RemoveAt(0);
                if (m.History.Count < GrowthWindow) continue;   // need a full window before judging

                double first = m.History[0], last = m.History[m.History.Count - 1];
                int ups = 0;
                for (int i = 1; i < m.History.Count; i++)
                    if (m.History[i] > m.History[i - 1]) ups++;
                double net = last - first;

                // Flag: rose in >=75% of intervals, is above its floor, and grew by a meaningful
                // fraction of where it started (so a metric parked at 1000 must add >=250, not +1).
                bool climbing = ups >= (GrowthWindow - 1) * 3 / 4;
                bool meaningful = last >= m.Floor && net > 0 && net >= Math.Max(m.Floor * 0.25, first * 0.25);
                if (climbing && meaningful)
                {
                    double perMin = net / (GrowthWindow) * (60.0 / Math.Max(1.0, ReportIntervalSeconds));
                    Plugin.Log.LogWarning(string.Format(CultureInfo.InvariantCulture,
                        "[Growth] {0}: {1:0.0} -> {2:0.0} over ~{3:0}s (+{4:0.0}/min, up {5}/{6} intervals) — possible leak/unbounded queue",
                        m.Name, first, last, GrowthWindow * ReportIntervalSeconds, perMin, ups, GrowthWindow - 1));
                }
            }
        }

        /// <summary>Report interval in seconds, set by RuntimeInstrumentation so the /min rate and the
        /// window-seconds label are accurate. Defaults to 3s until told otherwise.</summary>
        internal static double ReportIntervalSeconds = 3.0;

        // ---------------------------------------------------------------- jitter detector

        private static readonly Dictionary<int, float> JitterPeak = new Dictionary<int, float>();

        /// <summary>A puppet reports its "wasted speed" — u/s of in-place (goes-nowhere) motion.
        /// RemoteEntityPuppet already gates on its own floor; kept as the interval peak per entity.</summary>
        internal static void NoteJitter(int netId, float wastedSpeed)
        {
            if (!JitterPeak.TryGetValue(netId, out var cur) || wastedSpeed > cur) JitterPeak[netId] = wastedSpeed;
        }

        internal static void ReportJitter(double mono)
        {
            if (JitterPeak.Count == 0) return;
            int count = 0, worstId = 0;
            float worstHz = 0f;
            foreach (var kv in JitterPeak)
            {
                count++;
                if (kv.Value > worstHz) { worstHz = kv.Value; worstId = kv.Key; }
            }
            Plugin.Log.LogWarning(string.Format(CultureInfo.InvariantCulture,
                "[Jitter] {0} enemy puppet(s) vibrating in place (worst #{1} at {2:0.0}u/s wasted motion) — interpolation/snapshot instability",
                count, worstId, worstHz));
            JitterPeak.Clear();
        }

        // ---------------------------------------------------------------- per-type motion stats
        // Every puppet reports EVERY wasted-speed window sample here (not only above-floor ones), so
        // a sweep can rank ALL enemy types by their sync smoothness, not just the pathological ones.
        // Keyed by entityId; dumped and reset by the `jitterstats` devcmd (the jittersweep harness).

        private sealed class TypeStat
        {
            public int Samples;
            public double Sum;
            public float Peak;
            public int AboveFloor; // windows exceeding the [Jitter] report floor
        }

        private static readonly Dictionary<string, TypeStat> TypeStats = new Dictionary<string, TypeStat>();

        internal static void NoteMotionSample(string entityType, float wastedSpeed, bool aboveFloor)
        {
            if (string.IsNullOrEmpty(entityType)) return;
            if (!TypeStats.TryGetValue(entityType, out var s)) TypeStats[entityType] = s = new TypeStat();
            s.Samples++;
            s.Sum += wastedSpeed;
            if (wastedSpeed > s.Peak) s.Peak = wastedSpeed;
            if (aboveFloor) s.AboveFloor++;
        }

        /// <summary>Ranked per-type motion table for the jittersweep harness. wastedAvg/peak in u/s;
        /// jitter% = fraction of 0.5s windows above the report floor (the "visibly vibrating" rate).</summary>
        internal static void DumpTypeStats(Action<string> output, bool reset)
        {
            if (TypeStats.Count == 0) { output("jitterstats: no samples"); return; }
            var rows = new List<KeyValuePair<string, TypeStat>>(TypeStats);
            rows.Sort((a, b) => (b.Value.Sum / Math.Max(1, b.Value.Samples))
                .CompareTo(a.Value.Sum / Math.Max(1, a.Value.Samples)));
            output($"jitterstats: {rows.Count} type(s), worst first (wastedAvg u/s | peak | jitter% | windows)");
            foreach (var kv in rows)
            {
                var s = kv.Value;
                output(string.Format(CultureInfo.InvariantCulture,
                    "jitterstats {0}: avg={1:0.00} peak={2:0.0} jitter%={3:0.0} windows={4}",
                    kv.Key, s.Sum / Math.Max(1, s.Samples), s.Peak,
                    100.0 * s.AboveFloor / Math.Max(1, s.Samples), s.Samples));
            }
            if (reset) TypeStats.Clear();
        }

        internal static void Reset()
        {
            Metrics.Clear();
            _registered = false;
            JitterPeak.Clear();
            TypeStats.Clear();
        }
    }
}
