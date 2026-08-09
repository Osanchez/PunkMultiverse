using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// "An enemy vanished for one of us." The mod already notices some of that — `[RosterAudit]`
    /// prints a divergence, `[Residency]` warns that an owner holds authority with no simulator —
    /// but by the time anything notices, the object is gone, and a report that says "it is not
    /// here now" names no cause. Two things are missing, and this is both of them.
    ///
    /// **Memory.** A small ring of lifecycle events (spawn, unload, destroy, ownership change) so
    /// a dump can answer "what happened to this netId in the last half minute", not just "what is
    /// true this frame".
    ///
    /// **The other machine's view.** One peer's account of a disappearance is an absence. The pair
    /// — "gone here / alive there, still streaming" versus "gone here / gone there too, nobody
    /// killed it" — is a diagnosis. So a detection asks every peer to print its own view of the
    /// same netId under the same mark number.
    ///
    /// Deliberately quiet: a healthy run logs nothing at all. Every dump is capped (once per
    /// entity per run, and a ceiling per minute), because the one thing worse than no diagnostics
    /// during a desync is a log flooded by the diagnostics of a desync.
    /// </summary>
    internal static class EntityForensics
    {
        // ------------------------------------------------------------------ the ring

        internal enum Kind : byte { Spawn, Unload, Destroy, OwnerChange, Kill, Mark }

        private struct Record
        {
            public float T;
            public int NetId;
            public Kind K;
            public string Note;
        }

        private const int RingSize = 512;
        private static readonly Record[] Ring = new Record[RingSize];
        private static int _next;

        /// <summary>How far back a dump reaches. Long enough to cover "it was fine, then I flew
        /// past it and it went"; short enough that the ring is never the memory story.</summary>
        private const float WindowSeconds = 30f;

        internal static void Note(int netId, Kind kind, string note = null)
        {
            if (netId == 0) return;
            Ring[_next] = new Record { T = Time.unscaledTime, NetId = netId, K = kind, Note = note };
            _next = (_next + 1) % RingSize;
        }

        /// <summary>Convenience for call sites that hold an instanceId rather than a netId.</summary>
        internal static void NoteInstance(int instanceId, Kind kind, string note = null)
        {
            if (NetIds.TryGetNetId(instanceId, out int netId)) Note(netId, kind, note);
        }

        // ------------------------------------------------------------------ budget

        private static readonly HashSet<int> Dumped = new HashSet<int>();
        private static readonly Queue<float> RecentDumps = new Queue<float>();
        private const int MaxDumpsPerMinute = 6;
        private static byte _mark;

        internal static void Reset()
        {
            Array.Clear(Ring, 0, Ring.Length);
            _next = 0;
            Dumped.Clear();
            RecentDumps.Clear();
            _mark = 0;
            _live.Clear();
            _owners.Clear();
            _lastPos.Clear();
            _reportedStarving.Clear();
        }

        private static bool BudgetAllows(int netId)
        {
            if (!Dumped.Add(netId)) return false;               // one story per entity per run
            float now = Time.unscaledTime;
            while (RecentDumps.Count > 0 && now - RecentDumps.Peek() > 60f) RecentDumps.Dequeue();
            if (RecentDumps.Count >= MaxDumpsPerMinute) return false;
            RecentDumps.Enqueue(now);
            return true;
        }

        // ------------------------------------------------------------------ detection

        private static readonly HashSet<int> _live = new HashSet<int>();
        private static readonly Dictionary<int, byte> _owners = new Dictionary<int, byte>();
        private static readonly Dictionary<int, Vector2> _lastPos = new Dictionary<int, Vector2>();
        private static readonly HashSet<int> _reportedStarving = new HashSet<int>();
        private static readonly List<int> _scratch = new List<int>();

        /// <summary>
        /// The gate that makes this feature usable rather than noisy: entities are SUPPOSED to
        /// disappear when you fly away from them — that is the streaming working. Only a
        /// disappearance close enough for the player to have been looking at it is a report.
        ///
        /// (Found by testing the first version, which would have logged every normal stream-out.)
        /// </summary>
        private const float ReportRadius = 45f;

        private static bool NearLocalPlayer(int netId)
        {
            var ship = ShipSync.LocalShip;
            if (ship == null) return false;
            if (!_lastPos.TryGetValue(netId, out var pos)) return false;
            return Vector2.Distance(pos, (Vector2)ship.transform.position) <= ReportRadius;
        }

        /// <summary>Snapshots with no owner update for this long, while the owner is still in the
        /// session, mean the entity is frozen here even though it is still someone's to simulate.
        /// Two seconds is the mod's own "fed puppet" threshold; six keeps this out of normal jitter.</summary>
        private const float StarvedSeconds = 6f;

        private static void Tick(NetSession session)
        {
            var view = EnemySync.LiveView;
            if (view == null) return;

            // --- ownership changes, straight into the ring
            foreach (var kv in EnemySync.Owners)
            {
                if (_owners.TryGetValue(kv.Key, out byte was))
                {
                    if (was != kv.Value)
                    {
                        _owners[kv.Key] = kv.Value;
                        Note(kv.Key, Kind.OwnerChange, $"P{was + 1} -> P{kv.Value + 1}");
                    }
                }
                else _owners[kv.Key] = kv.Value;
            }

            // --- who is live right now, and where
            _scratch.Clear();
            foreach (var kv in view)
            {
                if (kv.Value == null) continue;
                _scratch.Add(kv.Key);
                _lastPos[kv.Key] = kv.Value.transform.position;
            }

            // --- gone since last tick, and nobody killed it
            foreach (int netId in _live)
            {
                if (view.TryGetValue(netId, out var still) && still != null) continue;
                if (EnemySync.IsKilled(netId)) continue;          // a death is not a disappearance
                Note(netId, Kind.Unload, "no longer live here");
                if (!IsRemotelyOwned(session, netId, out byte owner)) continue;  // our own despawn
                if (!NearLocalPlayer(netId)) continue;            // streamed out behind us: normal
                Report(session, netId, 0,
                    $"VANISHED here while in view — owner P{owner + 1}, no kill recorded");
            }

            _live.Clear();
            foreach (int netId in _scratch) _live.Add(netId);
            if (_lastPos.Count > 4096) _lastPos.Clear();          // the map is a hint, not a record

            // --- live, someone else's, and starving
            foreach (int netId in _scratch)
            {
                if (_reportedStarving.Contains(netId)) continue;
                if (!IsRemotelyOwned(session, netId, out byte owner)) continue;
                if (!view.TryGetValue(netId, out var se) || se == null) continue;
                if (!NearLocalPlayer(netId)) continue;   // far away and quiet is dormancy, not a fault
                var puppet = se.GetComponent<RemoteEntityPuppet>();
                if (puppet == null || puppet.SnapshotAge <= StarvedSeconds) continue;
                _reportedStarving.Add(netId);
                Report(session, netId, 1,
                    $"STARVED — owner P{owner + 1} sent nothing for {puppet.SnapshotAge:0.0}s, but it is still live here");
            }
        }

        private static bool IsRemotelyOwned(NetSession session, int netId, out byte owner)
        {
            owner = 0;
            if (!EnemySync.Owners.TryGetValue(netId, out owner)) return false;
            return owner != (byte)session.LocalSlot;
        }

        // ------------------------------------------------------------------ reporting

        private static void Report(NetSession session, int netId, byte reason, string headline)
        {
            if (!NetConfig.EntityForensics.Value || !BudgetAllows(netId)) return;
            byte mark = ++_mark;
            Plugin.Log.LogWarning($"[Forensics] mark #{mark}: #{netId} {headline}");
            DumpRing(netId);
            try
            {
                var writer = new NetWriter(16);
                new DiagMarkMsg { NetId = netId, Slot = (byte)session.LocalSlot, Mark = mark, Reason = reason }
                    .Write(writer);
                session.SendToAll(NetChannel.Control, writer.ToSegment(), reliable: true);
                Plugin.Log.LogInfo($"[Forensics] mark #{mark}: asked the other players what they see for #{netId}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Forensics] could not send mark: {e.Message}"); }
        }

        /// <summary>Everything the ring remembers about one entity, oldest first.</summary>
        private static void DumpRing(int netId)
        {
            float now = Time.unscaledTime;
            var sb = new StringBuilder();
            for (int i = 0; i < RingSize; i++)
            {
                var rec = Ring[(_next + i) % RingSize];   // oldest slot first
                if (rec.NetId != netId || rec.T <= 0f || now - rec.T > WindowSeconds) continue;
                sb.Append($"\n           -{now - rec.T:0.0}s {rec.K}{(rec.Note != null ? " " + rec.Note : "")}");
            }
            Plugin.Log.LogInfo(sb.Length == 0
                ? $"[Forensics]   #{netId}: nothing in the last {WindowSeconds:0}s — it went quietly"
                : $"[Forensics]   #{netId} history:{sb}");
        }

        /// <summary>A peer lost sight of an entity: print what THIS machine sees for it. The two
        /// halves together are the diagnosis.</summary>
        internal static void ApplyMark(DiagMarkMsg msg)
        {
            try
            {
                string reason = msg.Reason == 0 ? "vanished there" : msg.Reason == 1 ? "starved there" : "asked by hand";
                var view = EnemySync.LiveView;
                bool live = view != null && view.TryGetValue(msg.NetId, out var se) && se != null;
                string detail = "not live here";
                if (live)
                {
                    view.TryGetValue(msg.NetId, out var obj);
                    var puppet = obj.GetComponent<RemoteEntityPuppet>();
                    EnemySync.Owners.TryGetValue(msg.NetId, out byte owner);
                    detail = $"live here, owner P{owner + 1}, " +
                             (puppet != null ? $"puppet, snapshotAge={puppet.SnapshotAge:0.0}s" : "simulated by me") +
                             $", pos=({obj.transform.position.x:0},{obj.transform.position.y:0})";
                }
                else if (EnemySync.IsKilled(msg.NetId)) detail = "killed here (in the kill ledger)";

                Plugin.Log.LogWarning($"[Forensics] mark #{msg.Mark} from P{msg.Slot + 1} " +
                                      $"(#{msg.NetId} {reason}) — {detail}");
                DumpRing(msg.NetId);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Forensics] mark apply failed: {e.Message}"); }
        }

        /// <summary>The `forensics [netId]` devcmd, and anything else that wants a dump on demand.</summary>
        internal static void DumpOnDemand(NetSession session, int netId)
        {
            Dumped.Remove(netId);   // a hand-driven ask is never rate-limited away
            Report(session, netId, 2, "asked for by hand");
        }

        // ------------------------------------------------------------------ wiring

        /// <summary>Vanilla's single door for removing an entity's object — the best possible place
        /// to learn that one went away, and it needs no edit inside EnemySync.</summary>
        [HarmonyPatch(typeof(EntityGameObjectManager), "UnloadEntity")]
        internal static class NoteUnload
        {
            private static void Prefix(int __0)
            {
                if (!NetSession.Active) return;
                NoteInstance(__0, Kind.Unload, "UnloadEntity");
            }
        }

        [HarmonyPatch(typeof(EntityGameObjectManager), "SpawnObjectForEntity")]
        internal static class NoteSpawn
        {
            private static void Postfix(EntityData __0)
            {
                if (!NetSession.Active || __0 == null) return;
                NoteInstance(__0.instanceId, Kind.Spawn, "SpawnObjectForEntity");
            }
        }

        internal sealed class Ticker : MonoBehaviour
        {
            private float _next;

            private void Update()
            {
                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 1f;
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame) return;
                if (!NetConfig.EntityForensics.Value) return;
                try { Tick(session); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Forensics] tick failed: {e.Message}"); }
            }
        }
    }
}
