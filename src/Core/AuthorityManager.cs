using System.Collections.Generic;
using PunkMultiverse.Protocol;
using PunkMultiverse.Sync;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Host-side proximity-authority registrar (NEW-style). Every 0.5s: the closest alive player
    /// within AuthorityRadius owns an entity; ownership hands off when the owner drifts past
    /// TransferRadius and someone else is 25% closer; entities outside everyone's InterestRadius
    /// fall back to the host (dormant — the host only streams what's spawned near it). Clients
    /// never self-assign; only these AUTH_ASSIGN batches change ownership.
    /// </summary>
    internal static class AuthorityManager
    {
        private const float ScanInterval = 0.5f;
        private const float HysteresisFactor = 0.75f; // challenger must be at least 25% closer
        private const float HoldSeconds = 3f;         // min time between handoffs per entity
        private const float ReleaseDenySeconds = 8f;  // released-by cooldown (see OnReleased)
        private const float CombatDeferSeconds = 2.5f; // no handoffs mid-fight (see NoteCombat)
        private const float AggroStickSeconds = 4f;    // recent victim keeps simulation priority
        private const int MaxOptimizationChangesPerScan = 8; // spread puppet churn across scans

        private static float _nextScanAt;
        private static readonly Dictionary<int, float> HoldUntil = new Dictionary<int, float>();
        // netId -> (slot that just released it, until). Assigning an entity straight back to
        // the player who just said "I can't simulate this" (segment not streamed in on their
        // machine — routine while flying fast) created an assign->release->assign loop every
        // scan: puppet churn on both machines, enemies snapping between two simulations, and
        // client frame drops. Deny that slot for a while; anyone else can still take over.
        private static readonly Dictionary<int, (byte slot, float until)> DeniedSlot
            = new Dictionary<int, (byte, float)>();
        // Host-side combat knowledge, fed by the fire/damage traffic it already sees. A handoff
        // tears down and rebuilds the enemy's AI mid-telegraph — exactly when players are
        // looking — so entities that fought recently keep their owner (rescues excepted).
        private static readonly Dictionary<int, float> LastCombatAt = new Dictionary<int, float>();
        // netId -> (player it last fired at, when). The player being chased gets the tightest
        // simulation of the thing chasing them; small distance deltas must not steal it away.
        private static readonly Dictionary<int, (byte slot, float at)> LastAggro
            = new Dictionary<int, (byte, float)>();
        private static readonly NetWriter Writer = new NetWriter(2048);

        public static void Reset()
        {
            _nextScanAt = 0;
            HoldUntil.Clear();
            DeniedSlot.Clear();
            LastCombatAt.Clear();
            LastAggro.Clear();
        }

        /// <summary>A peer's machine is gone: drop every stability gate (hold, deny) on the
        /// entities it owned and scan now. Those gates exist to protect a live owner's
        /// simulation — honoring them for a vanished machine leaves enemies frozen for up to
        /// their remaining window (3 s hold / 8 s deny).</summary>
        public static void OnPeerLost(byte slot)
        {
            foreach (var kv in EnemySync.Owners)
            {
                if (kv.Value != slot) continue;
                HoldUntil.Remove(kv.Key);
                DeniedSlot.Remove(kv.Key);
            }
            _nextScanAt = 0; // rescue on the next Update, not up to half a second later
        }

        /// <summary>An entity attacked or took damage — defer optimization handoffs briefly.</summary>
        public static void NoteCombat(int netId) => LastCombatAt[netId] = Time.unscaledTime;

        /// <summary>An entity fired at a player — that player is its preferred authority.</summary>
        public static void NoteAggro(int netId, byte targetSlot)
        {
            LastCombatAt[netId] = Time.unscaledTime;
            if (targetSlot != 255) LastAggro[netId] = (targetSlot, Time.unscaledTime);
        }

        /// <summary>An owner reported it can't simulate this entity. Keep it host-dormant for a
        /// beat and don't hand it back to the same player until the deny window passes.</summary>
        public static void OnReleased(int netId, byte releasingSlot)
        {
            HoldUntil[netId] = Time.unscaledTime + HoldSeconds;
            DeniedSlot[netId] = (releasingSlot, Time.unscaledTime + ReleaseDenySeconds);
        }

        /// <summary>Called from NetSession.Update on the host while InGame.</summary>
        public static void Tick(NetSession session)
        {
            if (!session.IsHost || !NetIds.ManifestComplete || Time.unscaledTime < _nextScanAt) return;
            _nextScanAt = Time.unscaledTime + ScanInterval;

            EntityManager em;
            try { em = ServiceLocator.Get<EntityManager>(); } catch { return; }

            // Player positions by slot (local ship + puppets are all current via ShipState).
            var playerPos = new List<(byte slot, Vector2 pos)>(4);
            foreach (var p in session.Players)
            {
                if (p == null || !p.Connected) continue; // dropped players lose authority immediately
                if (!ShipSync.ShipsBySlot.TryGetValue(p.Slot, out var ship) || ship == null || ship.IsDead) continue;
                playerPos.Add((p.Slot, ship.transform.position));
            }
            if (playerPos.Count == 0) return;

            float authority = NetConfig.AuthorityRadius.Value;
            float transfer = NetConfig.TransferRadius.Value;
            float interest = NetConfig.InterestRadius.Value;

            var changes = new List<(int netId, byte owner)>();
            var seen = new HashSet<int>();
            int optimizationChanges = 0;
            foreach (var entity in em.GetAllEntities())
            {
                if (entity == null || entity.entityId == "Ship") continue;
                if (!NetIds.TryGetNetId(entity.instanceId, out int netId)) continue;
                if (!seen.Add(netId)) continue; // one decision per netId per scan, even if aliased
                if (EnemySync.FixedOwners.Contains(netId)) continue; // minions: fixed owner-authority
                // Handoff cooldown: every flip tears down and rebuilds the puppet's whole AI
                // stack on two machines, and a kill landing mid-flip is lost. Stay put.
                if (HoldUntil.TryGetValue(netId, out float holdUntil) && Time.unscaledTime < holdUntil) continue;

                Vector2 pos = entity.position;
                byte current = EnemySync.OwnerOf(netId);
                (byte slot, float dist) closest = (0, float.MaxValue);
                float ownerDist = float.MaxValue;
                foreach (var (slot, ppos) in playerPos)
                {
                    float d = Vector2.Distance(ppos, pos);
                    if (d < closest.dist) closest = (slot, d);
                    if (slot == current) ownerDist = d;
                }

                byte hostSlot = session.HostSlot;
                byte desired = current;
                if (closest.dist > interest)
                {
                    desired = hostSlot; // dormant — host fallback
                }
                else if (current == hostSlot)
                {
                    if (closest.dist <= authority && closest.slot != hostSlot) desired = closest.slot;
                }
                else if (ownerDist > authority * 1.15f) // release hysteresis: no flip-flop at the radius edge
                {
                    if (closest.dist <= authority) desired = closest.slot;
                    else if (ownerDist > interest) desired = hostSlot; // out of everyone's range entirely
                    // Owner in the release..interest band with nobody clearly closer: keep them.
                    // Bouncing to the host and back every scan is worse than a stretched radius.
                }
                else if (ownerDist > transfer && closest.slot != current && closest.dist < ownerDist * HysteresisFactor)
                {
                    desired = closest.slot; // handoff to a clearly-closer player
                }

                // Aggro stickiness: an enemy that recently fired at a player belongs to that
                // player while they're in range — a teammate drifting 25% closer must not
                // steal the thing chasing you.
                if (LastAggro.TryGetValue(netId, out var aggro))
                {
                    if (Time.unscaledTime - aggro.at >= AggroStickSeconds) LastAggro.Remove(netId);
                    else foreach (var (slot, ppos) in playerPos)
                        if (slot == aggro.slot)
                        {
                            if (Vector2.Distance(ppos, pos) <= authority) desired = aggro.slot;
                            break;
                        }
                }

                // Combat deferral: stability beats optimality mid-fight — a handoff restarts
                // telegraphs and attack state right when players are watching. Rescues are
                // exempt: an owner with no live ship stays MaxValue-distant and moves now.
                if (desired != current && ownerDist != float.MaxValue
                    && LastCombatAt.TryGetValue(netId, out float fought)
                    && Time.unscaledTime - fought < CombatDeferSeconds)
                    desired = current;

                // Respect the release deny window: whoever just gave this entity up can't
                // receive it again until the window passes (their machine likely still hasn't
                // streamed the segment in). It stays with its current owner instead.
                if (desired != current && DeniedSlot.TryGetValue(netId, out var denied))
                {
                    if (Time.unscaledTime >= denied.until) DeniedSlot.Remove(netId);
                    else if (desired == denied.slot) desired = current;
                }

                if (desired != current)
                {
                    // Cap optimization flips per scan — each one is a component-walk on two
                    // machines, and batches spike frames. Rescues always go through.
                    bool rescue = ownerDist == float.MaxValue && current != hostSlot;
                    if (!rescue && optimizationChanges >= MaxOptimizationChangesPerScan) continue;
                    if (!rescue) optimizationChanges++;
                    changes.Add((netId, desired));
                    EnemySync.Owners[netId] = desired;
                    HoldUntil[netId] = Time.unscaledTime + HoldSeconds;
                }
            }

            if (changes.Count == 0) return;
            NetStats.AuthFlips += changes.Count;
            Plugin.Log.LogInfo($"[Auth] {changes.Count} ownership change(s): " +
                string.Join(", ", changes.GetRange(0, Mathf.Min(5, changes.Count)).ConvertAll(c => $"#{c.netId}->P{c.owner + 1}")) +
                (changes.Count > 5 ? " …" : ""));

            // Apply locally (host) and broadcast in batches.
            EnemySync.ApplyAuthAssign(new AuthAssignMsg { Entries = changes });
            for (int start = 0; start < changes.Count; start += 64)
            {
                int count = Mathf.Min(64, changes.Count - start);
                Writer.Reset();
                new AuthAssignMsg { Entries = changes.GetRange(start, count) }.Write(Writer);
                session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
            }
        }
    }
}
