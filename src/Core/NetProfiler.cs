using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Frame-section profiler for our per-frame work. Silent lag (a hot tick with no log output)
    /// is invisible in a normal log — this makes it visible. NetSession.Update marks each of our
    /// subsystem ticks; every ReportInterval we log per-section avg/max ms, total tracked ms, and
    /// per-second network + ownership-churn rates (computed from NetStats deltas). A single frame
    /// whose tracked work exceeds SpikeMs also logs an immediate breakdown naming the worst
    /// section. This measures OUR work only (the Harmony ticks) — not the whole Unity frame — so a
    /// low total here with a bad in-game FPS points AWAY from us (rendering/physics/game sim) and a
    /// high total points squarely at the named section. Optimize from these numbers, not intuition.
    ///
    /// Gated on NetConfig.ProfileFrames, independent of SyncDiagnostics so we can profile without
    /// the chatty per-event logs (whose own disk I/O would distort the measurement).
    /// </summary>
    internal static class NetProfiler
    {
        private const double SpikeMs = 20.0;   // our tracked work in one frame over this = a spike

        private sealed class Section
        {
            public double SumMs;
            public double MaxMs;
            public int Samples;
            public double LastFrameMs;
        }

        private static readonly Dictionary<string, Section> Sections = new Dictionary<string, Section>();
        // Insertion-ordered names so the report reads in tick order, not hash order.
        private static readonly List<string> Order = new List<string>();
        private static readonly Stopwatch Lap = new Stopwatch();
        private static readonly Stopwatch Frame = new Stopwatch();

        private static int _frames;
        private static double _frameSumMs;
        private static double _frameMaxMs;
        private static float _nextReportAt;
        private static float _lastReportAt;

        // NetStats snapshot at the last report, for per-interval deltas.
        private static long _lastBytesIn, _lastBytesOut, _lastMsgsIn, _lastMsgsOut;
        private static int _lastAuthFlips, _lastAuthReleases;
        private static bool _baselined;

        public static bool Enabled => NetConfig.ProfileFrames != null && NetConfig.ProfileFrames.Value;

        public static void Reset()
        {
            Sections.Clear();
            Order.Clear();
            _frames = 0;
            _frameSumMs = _frameMaxMs = 0;
            _nextReportAt = 0;
            _lastReportAt = 0;
            _baselined = false;
        }

        /// <summary>Begin a frame's measurement window. Safe to call every frame.</summary>
        public static void FrameStart()
        {
            if (!Enabled) return;
            Frame.Restart();
            Lap.Restart();
            foreach (var section in Sections.Values) section.LastFrameMs = 0;
        }

        /// <summary>Record the time since the previous Mark/FrameStart against <paramref name="name"/>.</summary>
        public static void Mark(string name)
        {
            if (!Enabled) return;
            double ms = Lap.Elapsed.TotalMilliseconds;
            Lap.Restart();
            if (!Sections.TryGetValue(name, out var s))
            {
                Sections[name] = s = new Section();
                Order.Add(name);
            }
            s.SumMs += ms;
            s.MaxMs = System.Math.Max(s.MaxMs, ms);
            s.LastFrameMs += ms;
            s.Samples++;
        }

        /// <summary>Close the frame: accumulate totals, emit a spike line if this frame was heavy,
        /// and emit the interval report when due.</summary>
        public static void FrameEnd()
        {
            if (!Enabled) return;
            double total = Frame.Elapsed.TotalMilliseconds;
            _frames++;
            _frameSumMs += total;
            if (total > _frameMaxMs) _frameMaxMs = total;

            if (total >= SpikeMs)
                Plugin.Log.LogWarning($"[Profile] SPIKE mono={RuntimeInstrumentation.MonoSeconds:0.000}s {total:0.0}ms our-work this frame — {TopSectionsThisFrame()}");

            if (Time.unscaledTime >= _nextReportAt)
            {
                float interval = System.Math.Max(1f, System.Math.Min(30f, NetConfig.ProfileReportInterval.Value));
                float elapsed = _lastReportAt > 0 ? Time.unscaledTime - _lastReportAt : interval;
                _lastReportAt = Time.unscaledTime;
                _nextReportAt = Time.unscaledTime + interval;
                Report(System.Math.Max(0.001f, elapsed));
            }
        }

        // The heaviest sections in the CURRENT frame (their last lap), for the spike line.
        private static string TopSectionsThisFrame()
        {
            string worst = "?";
            double worstMs = -1;
            foreach (var name in Order)
            {
                var s = Sections[name];
                if (s.LastFrameMs > worstMs) { worstMs = s.LastFrameMs; worst = name; }
            }
            return $"worst section: {worst} ({worstMs:0.0}ms)";
        }

        private static void Report(double elapsedSeconds)
        {
            if (_frames == 0) return;

            var sb = new StringBuilder(256);
            double frameAvg = _frameSumMs / _frames;
            sb.Append($"[Profile] mono={RuntimeInstrumentation.MonoSeconds:0.000}s our-work/frame avg {frameAvg:0.0}ms max {_frameMaxMs:0.0}ms over {_frames} frames | ");

            // Sections, heaviest avg first.
            Order.Sort((a, b) =>
            {
                double aa = Sections[a].Samples > 0 ? Sections[a].SumMs / Sections[a].Samples : 0;
                double bb = Sections[b].Samples > 0 ? Sections[b].SumMs / Sections[b].Samples : 0;
                return bb.CompareTo(aa);
            });
            bool first = true;
            foreach (var name in Order)
            {
                var s = Sections[name];
                if (s.Samples == 0) continue;
                double avg = s.SumMs / s.Samples;
                if (avg < 0.05 && s.MaxMs < 1.0) continue; // hide the always-trivial sections
                if (!first) sb.Append(", ");
                first = false;
                sb.Append($"{name} {avg:0.0}/{s.MaxMs:0.0}");
            }
            sb.Append(" (avg/max ms)");

            // Network + ownership churn rates over the interval.
            if (_baselined)
            {
                double secs = elapsedSeconds;
                long dIn = NetStats.MsgsIn - _lastMsgsIn;
                long dOut = NetStats.MsgsOut - _lastMsgsOut;
                long dBin = NetStats.BytesIn - _lastBytesIn;
                long dBout = NetStats.BytesOut - _lastBytesOut;
                int dFlips = NetStats.AuthFlips - _lastAuthFlips;
                int dRel = NetStats.AuthReleases - _lastAuthReleases;
                sb.Append($" | net in {dIn / secs:0}msg/s {dBin / secs / 1024:0.0}KB/s, out {dOut / secs:0}msg/s {dBout / secs / 1024:0.0}KB/s");
                sb.Append($" | authChurn {dFlips / secs:0.0} flips/s {dRel / secs:0.0} releases/s");
            }
            _lastMsgsIn = NetStats.MsgsIn; _lastMsgsOut = NetStats.MsgsOut;
            _lastBytesIn = NetStats.BytesIn; _lastBytesOut = NetStats.BytesOut;
            _lastAuthFlips = NetStats.AuthFlips; _lastAuthReleases = NetStats.AuthReleases;
            _baselined = true;

            Plugin.Log.LogInfo(sb.ToString());

            // Reset interval aggregates.
            foreach (var s in Sections.Values) { s.SumMs = 0; s.MaxMs = 0; s.Samples = 0; }
            _frames = 0; _frameSumMs = 0; _frameMaxMs = 0;
        }
    }
}
