using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Evidence for one question: on a dedicated coordinator, WHO actually needs an entity to exist
    /// as a live GameObject?
    ///
    /// The measurement that raised it: with two players the server held `canonical=170 units=37
    /// props=133` live objects while reporting `owners=0 fixed=0 leases=190` — it instantiates and
    /// ticks 170 entities it has no authority over, because the game's segment streamer builds
    /// every segment within 3 of a ship and instantiates everything inside. Frame time tracks that
    /// count directly: 0 live entities = 55-66fps, 170 = 15-18fps. That is the 24-player wall.
    ///
    /// The obvious fix — don't instantiate on the server — is only safe if nothing depends on those
    /// objects. That is exactly the kind of claim this session has already got wrong twice by
    /// reasoning instead of measuring, so nothing here assumes: this counts every real resolution of
    /// entity-data to live-object during a live match and attributes it to the calling method,
    /// vanilla callers included. A call site that never fires does not need live objects. One that
    /// fires constantly does, and has to be answered before anything is cut.
    ///
    /// Off by default (`livedemand on|off|report`) — capturing a stack frame per call is far too
    /// expensive to leave running. Note this walks the CURRENT thread's stack, which works fine
    /// under Wine; it is walking ANOTHER thread's stack that fails (see the [Hitch] watchdog).
    /// </summary>
    internal static class LiveObjectDemand
    {
        private static readonly Dictionary<string, long> Hits = new Dictionary<string, long>();
        private static long _resolveCalls, _spawnCalls, _misses;
        private static float _startedAt;

        internal static bool Active { get; private set; }

        internal static string Toggle(string arg)
        {
            if (string.Equals(arg, "report", StringComparison.OrdinalIgnoreCase)) return Report();
            bool on = string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase);
            if (on == Active) return Active ? "already on" : "already off";
            Active = on;
            if (on)
            {
                Hits.Clear();
                _resolveCalls = _spawnCalls = _misses = 0;
                _startedAt = Time.unscaledTime;
                return "on — counting live-object demand by caller";
            }
            return Report();
        }

        private static void Note(MethodBase caller)
        {
            string key = caller == null
                ? "<unknown>"
                : (caller.DeclaringType != null ? caller.DeclaringType.Name : "?") + "." + caller.Name;
            Hits.TryGetValue(key, out long n);
            Hits[key] = n + 1;
        }

        /// <summary>Immediate caller of the patched method. Frame 0 is the prefix, 1 is the patched
        /// method's stub, so 2 is the real caller.</summary>
        private static MethodBase Caller()
        {
            try { return new StackTrace(2, false).GetFrame(0)?.GetMethod(); }
            catch { return null; }
        }

        [HarmonyPatch(typeof(EntityGameObjectManager), "TryGetSavableEntity")]
        internal static class CountResolves
        {
            private static void Postfix(bool __result)
            {
                if (!Active || !NetConfig.IsCoordinator) return;
                _resolveCalls++;
                if (!__result) { _misses++; return; } // asked for an object that is not live
                Note(Caller());
            }
        }

        [HarmonyPatch(typeof(EntityGameObjectManager), "SpawnObjectForEntity")]
        internal static class CountSpawns
        {
            private static void Postfix()
            {
                if (!Active || !NetConfig.IsCoordinator) return;
                _spawnCalls++;
                Note(Caller());
            }
        }

        private static string Report()
        {
            float secs = Mathf.Max(0.1f, Time.unscaledTime - _startedAt);
            Plugin.Log.LogInfo($"[LiveDemand] === {secs:0}s: resolves={_resolveCalls} " +
                $"(misses={_misses}) spawns={_spawnCalls} ===");
            var ranked = new List<KeyValuePair<string, long>>(Hits);
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < ranked.Count && i < 20; i++)
                Plugin.Log.LogInfo(string.Format("[LiveDemand] {0,-48} {1,8} ({2,7:0.0}/s)",
                    ranked[i].Key, ranked[i].Value, ranked[i].Value / secs));
            Plugin.Log.LogInfo("[LiveDemand] === end ===");
            return $"reported {ranked.Count} call sites over {secs:0}s";
        }
    }
}
