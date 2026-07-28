using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Attributes the cost of <c>LevelChangeBuffer.Update</c> to the individual handlers it fans
    /// out to.
    ///
    /// <c>simprof</c> profiles vanilla per-frame methods, so a cost that lives in an EVENT HANDLER
    /// shows up as its publisher: profiling a Battle Royale coordinator mid-closure reported
    /// <c>LevelChangeBuffer.Update</c> at 99.3% of all frame time (979ms per call) — which is
    /// true and useless, because that method's whole body is
    /// <c>CellsChanged?.Invoke(changedCells)</c>. Eight different systems subscribe, several of
    /// them once PER ENTITY, and they have wildly different scaling: some are O(changes), some are
    /// O(subscribers x changes), and at least one rebuilds a structure sized by the BOUNDING BOX of
    /// everything that changed — which for a closing ring is the whole map.
    ///
    /// This closes that gap: same measurement, one level deeper. Off by default; `cellfanout on`
    /// arms it and it reports a per-handler breakdown every 10 seconds.
    /// </summary>
    internal static class CellFanoutProfiler
    {
        internal static bool Enabled;

        private sealed class Stat
        {
            internal double Ms;
            internal int Calls;
            internal double WorstMs;
        }

        private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>();
        private static float _nextReportAt;
        private static int _cellsThisWindow;

        internal static void SetEnabled(bool on)
        {
            Enabled = on;
            Stats.Clear();
            _cellsThisWindow = 0;
            _nextReportAt = Time.unscaledTime + 10f;
        }

        internal static void NoteCells(int count) { if (Enabled) _cellsThisWindow += count; }

        private static void Note(string who, double ms)
        {
            if (!Stats.TryGetValue(who, out var stat)) Stats[who] = stat = new Stat();
            stat.Ms += ms;
            stat.Calls++;
            if (ms > stat.WorstMs) stat.WorstMs = ms;
        }

        internal static void Report()
        {
            if (!Enabled || Time.unscaledTime < _nextReportAt) return;
            _nextReportAt = Time.unscaledTime + 10f;
            if (Stats.Count == 0) return;

            double total = 0.0;
            foreach (var s in Stats.Values) total += s.Ms;
            Plugin.Log.LogInfo($"[CellFanout] === {total:0}ms over 10s across {_cellsThisWindow} cell changes ===");
            var ordered = new List<KeyValuePair<string, Stat>>(Stats);
            ordered.Sort((a, b) => b.Value.Ms.CompareTo(a.Value.Ms));
            foreach (var kv in ordered)
                Plugin.Log.LogInfo(string.Format(
                    "[CellFanout] {0,-34} total={1,8:0.0}ms calls={2,7} avg={3,7:0.000}ms worst={4,7:0.0}ms",
                    kv.Key, kv.Value.Ms, kv.Value.Calls, kv.Value.Ms / Mathf.Max(1, kv.Value.Calls),
                    kv.Value.WorstMs));
            Stats.Clear();
            _cellsThisWindow = 0;
        }

        /// <summary>Every subscriber to <c>LevelChangeBuffer.CellsChanged</c>. Handlers that do not
        /// exist on a given build are skipped rather than throwing — this is diagnostics, it must
        /// never be the reason the mod fails to load.</summary>
        [HarmonyPatch]
        internal static class TimeHandlers
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var wanted = new[]
                {
                    ("LevelSegmentComponent", "OnCellsChanged"),
                    ("MapDrawer", "OnCellsChanged"),
                    ("LightmapGenerator", "OnCellsChanged"),
                    ("NavigationManager", "OnCellsChanged"),
                    ("PlantDestructor", "OnCellsChanged"),
                    ("GroundTilemapUpdater", "OnCellsChanged"),
                    ("EntityPlant", "OnCellsChanged"),
                    ("CrawlerLeg", "OnLevelChanged"),
                };
                foreach (var (typeName, method) in wanted)
                {
                    var t = AccessTools.TypeByName(typeName);
                    var m = t != null ? AccessTools.Method(t, method) : null;
                    if (m != null) yield return m;
                }
            }

            private static void Prefix(out long __state) => __state = Enabled
                ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            private static void Postfix(object __instance, long __state)
            {
                if (!Enabled || __state == 0L) return;
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - __state)
                    * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Note(__instance != null ? __instance.GetType().Name : "?", ms);
            }
        }

        /// <summary>Report from the publisher, so the breakdown lands next to the frame it explains
        /// and we also learn how many cells produced it.</summary>
        [HarmonyPatch(typeof(LevelChangeBuffer), "Update")]
        internal static class ReportAfterFanout
        {
            private static System.Reflection.FieldInfo _changedCells;

            private static void Prefix(LevelChangeBuffer __instance)
            {
                if (!Enabled) return;
                try
                {
                    if (_changedCells == null)
                        _changedCells = AccessTools.Field(typeof(LevelChangeBuffer), "changedCells");
                    if (_changedCells?.GetValue(__instance) is List<Level.CellChange> list)
                        NoteCells(list.Count);
                }
                catch { }
            }

            private static void Postfix() => Report();
        }
    }
}
