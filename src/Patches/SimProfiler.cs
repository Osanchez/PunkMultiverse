using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Diagnostic: time EVERY vanilla per-frame method and report the biggest consumers.
    ///
    /// Why this exists. The dedicated coordinator spikes to ~550ms frames two or three times every
    /// three seconds, and the hitch watchdog can only say `phase=Unity.BetweenUpdates` — i.e. "the
    /// time went somewhere inside the engine loop", which names nothing. The usual next step,
    /// sampling the main thread's stack, is unavailable: Wine's Mono cannot walk another thread's
    /// stack (the [Hitch] watchdog already reports main-stack-failed there). So the only way to get
    /// EVIDENCE rather than another hypothesis is to instrument the candidates directly — and since
    /// guessing which of the 178 Update/LateUpdate/FixedUpdate methods matters has already produced
    /// two wrong answers this session, this times all of them and lets the numbers choose.
    ///
    /// Deliberately opt-in and time-boxed (`simprof <secs>` devcmd): it patches ~178 methods and
    /// adds two timestamp reads per call, which is cheap but not free. It unpatches itself when the
    /// window closes, so a profiled server returns to normal speed without a restart.
    /// </summary>
    internal static class SimProfiler
    {
        private sealed class Acc
        {
            public long Ticks;
            public long Calls;
            public long MaxTicks;
            public string Name;
        }

        // Keyed by method handle: the postfix receives __originalMethod, and MethodBase identity is
        // stable for the life of the process.
        private static readonly Dictionary<MethodBase, Acc> Accs = new Dictionary<MethodBase, Acc>();
        private static Harmony _harmony;
        private static float _until;
        private static int _patched;

        internal static bool Active { get; private set; }

        internal static void Start(float seconds)
        {
            if (Active) { Plugin.Log.LogInfo("[SimProf] already running"); return; }
            Accs.Clear();
            _patched = 0;
            _harmony = new Harmony("com.osanchez.punkmultiverse.simprof");
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(SimProfiler), nameof(Pre)));
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(SimProfiler), nameof(Post)));

            // BOTH assemblies. Profiling only the game's types left a blind spot exactly where the
            // residual stalls live: the mod's own MonoBehaviours (RemoteEntityPuppet, PropPuppet,
            // RemotePuppet, the HUD/diagnostic components) tick in Unity's loop, outside every
            // SetPhase bracket — so they are invisible to the phase profiler AND were invisible
            // here, while the hitch watchdog reported them only as "Unity.BetweenUpdates".
            Assembly[] assemblies;
            try { assemblies = new[] { typeof(Ship).Assembly, typeof(Plugin).Assembly }; }
            catch (Exception e) { Plugin.Log.LogWarning($"[SimProf] cannot reach the assemblies: {e.Message}"); return; }

            foreach (var type in SafeTypesOf(assemblies))
            {
                if (type == null || type.IsAbstract || type.IsGenericTypeDefinition) continue;
                if (!typeof(MonoBehaviour).IsAssignableFrom(type)) continue;
                foreach (string name in new[] { "Update", "LateUpdate", "FixedUpdate" })
                {
                    // DeclaredOnly: patching an inherited method through a subclass would double-count.
                    var m = type.GetMethod(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                    if (m == null || m.IsAbstract || m.ContainsGenericParameters) continue;
                    // Never patch the profiler's own plumbing.
                    if (type == typeof(SimProfiler)) continue;
                    try
                    {
                        _harmony.Patch(m, prefix, postfix);
                        // Mark mod-side components so the report shows at a glance whether the
                        // time is the game's or ours.
                        bool mine = type.Assembly == typeof(Plugin).Assembly;
                        Accs[m] = new Acc { Name = (mine ? "*" : "") + type.Name + "." + name };
                        _patched++;
                    }
                    catch { /* a method Harmony cannot wrap is not worth failing the run over */ }
                }
            }

            Active = true;
            _until = Time.unscaledTime + Mathf.Clamp(seconds, 3f, 120f);
            Plugin.Log.LogInfo($"[SimProf] profiling {_patched} vanilla per-frame methods for " +
                $"{Mathf.Clamp(seconds, 3f, 120f):0}s");
        }

        private static IEnumerable<Type> SafeTypesOf(Assembly[] assemblies)
        {
            var all = new List<Type>();
            foreach (var a in assemblies)
            {
                try { all.AddRange(a.GetTypes()); }
                catch (ReflectionTypeLoadException e) { all.AddRange(e.Types.Where(t => t != null)); }
                catch { }
            }
            return all;
        }

        private static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Post(MethodBase __originalMethod, long __state)
        {
            long dt = Stopwatch.GetTimestamp() - __state;
            if (!Accs.TryGetValue(__originalMethod, out var acc)) return;
            acc.Ticks += dt;
            acc.Calls++;
            if (dt > acc.MaxTicks) acc.MaxTicks = dt;
        }

        /// <summary>Called from the session tick; closes the window and reports.</summary>
        internal static void Tick()
        {
            if (!Active || Time.unscaledTime < _until) return;
            Active = false;
            Report();
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
        }

        private static void Report()
        {
            double toMs = 1000.0 / Stopwatch.Frequency;
            var ranked = Accs.Values.Where(a => a.Calls > 0).OrderByDescending(a => a.Ticks).Take(14).ToList();
            double totalMs = Accs.Values.Sum(a => a.Ticks) * toMs;
            Plugin.Log.LogInfo($"[SimProf] === top vanilla per-frame cost ({_patched} methods, " +
                $"{totalMs:0}ms total in window) ===");
            foreach (var a in ranked)
                Plugin.Log.LogInfo(string.Format(
                    "[SimProf] {0,-42} total={1,8:0.0}ms calls={2,6} avg={3,7:0.000}ms max={4,7:0.0}ms",
                    a.Name, a.Ticks * toMs, a.Calls, a.Ticks * toMs / a.Calls, a.MaxTicks * toMs));
            Plugin.Log.LogInfo("[SimProf] === end ===");
        }
    }
}
