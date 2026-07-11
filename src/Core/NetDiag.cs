using System;
using System.Collections.Generic;
using System.Text;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Gated, categorized sync/authority diagnostics. Everything here is a no-op unless
    /// <see cref="Enabled"/> (NetConfig.SyncDiagnostics, toggleable live from the F11 overlay),
    /// so it's safe to leave the instrumentation in the hot paths. Messages are tagged
    /// <c>[Diag:&lt;category&gt;]</c> so a diagnosis session greps cleanly:
    ///   Auth   — host authority decisions (assign/handoff/rescue/blocked)
    ///   Assign — receiver applying an ownership change (became local / became puppet)
    ///   Release— an owner giving an entity up, and the host taking it back
    ///   State  — entity-state re-baselines (sender/owner changed — the teleport signal)
    ///   Dual   — dual-ownership conflict (two machines simulating one entity)
    ///   Fire   — enemy fire announced / replayed (the double-shot signal)
    ///   Ids    — identity resolution (unresolved orphans)
    ///   Own    — ownership-table dumps
    /// Hot-path callers must gate on <see cref="Enabled"/> themselves before building strings.
    /// </summary>
    internal static class NetDiag
    {
        public static bool Enabled => NetConfig.SyncDiagnostics != null && NetConfig.SyncDiagnostics.Value;

        private static readonly Dictionary<string, float> Throttle = new Dictionary<string, float>();
        private static float _nextDumpAt;

        public static void Reset()
        {
            Throttle.Clear();
            _nextDumpAt = 0f;
        }

        public static void Log(string category, string message)
        {
            if (!Enabled) return;
            Plugin.Log.LogInfo($"[Diag:{category}] {message}");
        }

        /// <summary>Conflicts and anomalies — logged as warnings so they stand out in the trace.</summary>
        public static void Warn(string category, string message)
        {
            if (!Enabled) return;
            Plugin.Log.LogWarning($"[Diag:{category}] {message}");
        }

        /// <summary>Log at most once per <paramref name="interval"/>s per <paramref name="key"/>.
        /// For per-entity hot paths (state, fire) that would otherwise storm the log.</summary>
        public static void Throttled(string key, float interval, string category, Func<string> message)
        {
            if (!Enabled) return;
            float now = Time.unscaledTime;
            if (Throttle.TryGetValue(key, out float next) && now < next) return;
            Throttle[key] = now + interval;
            Plugin.Log.LogInfo($"[Diag:{category}] {message()}");
        }

        /// <summary>"P1", or "P1(host)" for the current session host slot.</summary>
        public static string Owner(byte slot)
        {
            var s = NetSession.Instance;
            return (s != null && s.HostSlot == slot) ? $"P{slot + 1}(host)" : $"P{slot + 1}";
        }

        /// <summary>Human-readable entity label: "#85 EnemyDrone" when resolvable, else "#85".</summary>
        public static string Describe(int netId)
        {
            try
            {
                if (NetIds.TryGetInstanceId(netId, out int instanceId))
                {
                    var em = ServiceLocator.Get<EntityManager>();
                    var data = em?.GetEntity(instanceId);
                    if (data != null && !string.IsNullOrEmpty(data.entityId))
                        return $"#{netId} {data.entityId}";
                }
            }
            catch { }
            return $"#{netId}";
        }

        /// <summary>Periodic ownership dump when configured — called each frame from NetSession.</summary>
        public static void TickPeriodic()
        {
            if (!Enabled) return;
            float interval = NetConfig.DiagOwnershipDumpInterval != null ? NetConfig.DiagOwnershipDumpInterval.Value : 0f;
            if (interval <= 0f) return;
            if (Time.unscaledTime < _nextDumpAt) return;
            _nextDumpAt = Time.unscaledTime + interval;
            DumpOwnership();
        }

        /// <summary>One-line ownership summary (per-slot counts + fixed/killed/orphans) — also
        /// shown on the F11 overlay.</summary>
        public static string OwnershipSummary()
        {
            var session = NetSession.Instance;
            if (session == null) return "no session";
            var perSlot = new int[NetSession.MaxPlayers];
            int explicitCount = 0;
            foreach (var (netId, owner) in EnemySync.OwnersSnapshot())
            {
                explicitCount++;
                if (owner >= 0 && owner < perSlot.Length) perSlot[owner]++;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < NetSession.MaxPlayers; i++)
                if (perSlot[i] > 0) sb.Append($"{Owner((byte)i)}={perSlot[i]}  ");
            sb.Append($"| explicit={explicitCount} fixed={EnemySync.FixedOwners.Count} " +
                      $"killed={EnemySync.KilledCount} orphans={NetIds.OrphanCount}");
            return sb.ToString();
        }

        /// <summary>Dump the ownership table to the log: a summary line plus a capped per-entity
        /// detail of every EXPLICITLY-owned entity (the interesting set — host-default entities
        /// are omitted), each with its live/puppet state and any local inconsistency flagged.</summary>
        public static void DumpOwnership()
        {
            var session = NetSession.Instance;
            if (session == null) { Plugin.Log.LogInfo("[Diag:Own] no session"); return; }
            Plugin.Log.LogInfo($"[Diag:Own] {OwnershipSummary()} (localSlot P{session.LocalSlot + 1}, host P{session.HostSlot + 1})");

            EntityGameObjectManager egm = null;
            try { egm = ServiceLocator.Get<EntityGameObjectManager>(); } catch { }

            const int cap = 40;
            int shown = 0, total = 0;
            foreach (var (netId, owner) in EnemySync.OwnersSnapshot())
            {
                total++;
                if (shown >= cap) continue;
                shown++;
                string live = "?";
                string flag = "";
                try
                {
                    if (egm != null && NetIds.TryGetInstanceId(netId, out int instanceId)
                        && egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                    {
                        bool puppet = se.GetComponent<RemoteEntityPuppet>() != null;
                        live = puppet ? "puppet" : "live";
                        bool mine = owner == session.LocalSlot;
                        if (mine && puppet) flag = "  <!> I OWN but have a PUPPET (stale)";
                        else if (!mine && !puppet) flag = "  <!> NOT mine but NO puppet (running local AI?)";
                    }
                    else live = "unspawned";
                }
                catch { }
                Plugin.Log.LogInfo($"[Diag:Own]   {Describe(netId)} -> {Owner(owner)} [{live}]{flag}");
            }
            if (total > cap) Plugin.Log.LogInfo($"[Diag:Own]   … {total - cap} more (cap {cap})");
        }
    }
}
