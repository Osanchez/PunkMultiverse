using System.Collections.Generic;
using PunkMultiverse.Protocol;
using PunkMultiverse.Sync;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Host-authoritative entity ownership (the model Noita Entangled Worlds proved out): the
    /// host simulates every entity it has spawned and streams it to everyone as a muted puppet.
    /// There is NO continuous "closest player owns it" re-optimization — that was the source of
    /// the ownership thrash (entities flipping owners every scan, dual-simulation, teleporting
    /// enemies, duplicated fire). A client only takes authority in the one case the host cannot
    /// cover an entity: when the client is off exploring a region the host has not streamed in.
    /// That grab is sticky — held until the client clearly leaves — never re-decided by small
    /// distance deltas.
    ///
    /// Per entity, every ScanInterval, the host picks the owner by this ladder:
    ///   1. A client already owns it and is still within KeepRadius → leave it (sticky grab).
    ///   2. The host has the entity spawned → the host owns it (authoritative default).
    ///   3. The host can't reach it and a client is within the tight ClaimRadius → that client
    ///      grabs it; otherwise it stays host-nominal (dormant, nobody near).
    /// Clients never self-assign; only these AUTH_ASSIGN batches change ownership.
    /// </summary>
    internal static class AuthorityManager
    {
        private const float ScanInterval = 0.5f;
        private const float HoldSeconds = 1.5f;        // settle time after a change (anti-oscillation)
        private const float ReleaseDenySeconds = 5f;   // released-by cooldown (see OnReleased)
        private const float ClaimFactor = 0.5f;        // grab radius = ClaimFactor * AuthorityRadius —
                                                       // tight, so a client is only ever given entities
                                                       // it can definitely stream in and simulate.
        private const int MaxGrabsPerScan = 16;        // spread a client's entry-into-a-region claim wave

        private static float _nextScanAt;
        private static readonly Dictionary<int, float> HoldUntil = new Dictionary<int, float>();
        // netId -> (slot that just released it, until). A client that said "I can't simulate this"
        // must not be handed it straight back, or we recreate the assign->release->assign loop.
        private static readonly Dictionary<int, (byte slot, float until)> DeniedSlot
            = new Dictionary<int, (byte, float)>();
        private static readonly NetWriter Writer = new NetWriter(2048);

        public static void Reset()
        {
            _nextScanAt = 0;
            HoldUntil.Clear();
            DeniedSlot.Clear();
        }

        /// <summary>A peer's machine is gone: drop the stability gates on the entities it owned and
        /// scan now, so its enemies are re-homed immediately instead of freezing for a window.</summary>
        public static void OnPeerLost(byte slot)
        {
            foreach (var kv in EnemySync.Owners)
            {
                if (kv.Value != slot) continue;
                HoldUntil.Remove(kv.Key);
                DeniedSlot.Remove(kv.Key);
            }
            _nextScanAt = 0;
        }

        // Ownership no longer follows combat/aggro (that flipped authority every time an enemy
        // retargeted). Kept as no-ops so the fire/damage call sites don't need to change.
        public static void NoteCombat(int netId) { }
        public static void NoteAggro(int netId, byte targetSlot) { }

        /// <summary>An owner reported it can't simulate this entity. Settle it and don't hand it
        /// back to the same slot until the deny window passes.</summary>
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
            EntityGameObjectManager egm;
            try { egm = ServiceLocator.Get<EntityGameObjectManager>(); } catch { return; }

            byte hostSlot = session.HostSlot;

            // Connected CLIENT ships (host excluded — the host claims nothing by distance; it owns
            // whatever it has spawned). A client can only hold entities it can actually simulate.
            var clients = new List<(byte slot, Vector2 pos)>(4);
            foreach (var p in session.Players)
            {
                if (p == null || !p.Connected || p.Slot == hostSlot) continue;
                if (!ShipSync.ShipsBySlot.TryGetValue(p.Slot, out var ship) || ship == null || ship.IsDead) continue;
                clients.Add((p.Slot, ship.transform.position));
            }

            float claimRadius = NetConfig.AuthorityRadius.Value * ClaimFactor;
            float keepRadius = NetConfig.InterestRadius.Value;

            var changes = new List<(int netId, byte owner)>();
            var seen = new HashSet<int>();
            int grabBudget = MaxGrabsPerScan;
            float now = Time.unscaledTime;

            foreach (var entity in em.GetAllEntities())
            {
                if (entity == null || entity.entityId == "Ship") continue;
                if (!NetIds.TryGetNetId(entity.instanceId, out int netId)) continue;
                if (!seen.Add(netId)) continue;
                if (EnemySync.FixedOwners.Contains(netId)) continue; // minions: fixed owner-authority
                if (EnemySync.IsKilled(netId)) continue;             // dead everywhere

                byte current = EnemySync.OwnerOf(netId);
                Vector2 pos = entity.position;
                bool hostHas = IsSpawnedOnHost(egm, netId);

                byte desired;
                string reason;
                // 1. A client already owns it and is still close enough to keep simulating it.
                if (current != hostSlot && !Denied(netId, current, now)
                    && TryClientDist(clients, current, pos, out float curDist) && curDist <= keepRadius)
                {
                    desired = current;
                    reason = null;
                }
                // 2. The host has it spawned → the host simulates it (authoritative default).
                else if (hostHas)
                {
                    desired = hostSlot;
                    reason = "host owns (has it spawned)";
                }
                // 3. The host can't reach it → the nearest close-enough client grabs it, else dormant.
                else
                {
                    desired = NearestClient(clients, pos, claimRadius, netId, now, out float cd);
                    reason = desired != hostSlot
                        ? $"host can't reach it — {NetDiag.Owner(desired)} grabs ({cd:0}u)"
                        : "host can't reach it, no client near — dormant";
                }

                if (desired == current) continue;
                if (HoldUntil.TryGetValue(netId, out float hu) && now < hu) continue; // settling

                // A client grabbing a region claims a wave — spread it. Reverting to the host
                // (a client left, or disconnected) is never capped: those entities would freeze.
                bool grab = desired != hostSlot;
                if (grab)
                {
                    if (grabBudget <= 0) continue;
                    grabBudget--;
                }

                changes.Add((netId, desired));
                EnemySync.Owners[netId] = desired;
                HoldUntil[netId] = now + HoldSeconds;
                if (NetDiag.Enabled)
                    NetDiag.Log("Auth", $"{NetDiag.Describe(netId)} {NetDiag.Owner(current)} -> {NetDiag.Owner(desired)}" +
                        (reason != null ? $" — {reason}" : ""));
            }

            if (changes.Count == 0) return;
            NetStats.AuthFlips += changes.Count;
            Plugin.Log.LogInfo($"[Auth] {changes.Count} ownership change(s): " +
                string.Join(", ", changes.GetRange(0, Mathf.Min(5, changes.Count)).ConvertAll(c => $"#{c.netId}->P{c.owner + 1}")) +
                (changes.Count > 5 ? " …" : ""));

            EnemySync.ApplyAuthAssign(new AuthAssignMsg { Entries = changes });
            for (int start = 0; start < changes.Count; start += 64)
            {
                int count = Mathf.Min(64, changes.Count - start);
                Writer.Reset();
                new AuthAssignMsg { Entries = changes.GetRange(start, count) }.Write(Writer);
                session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
            }
        }

        private static bool IsSpawnedOnHost(EntityGameObjectManager egm, int netId)
        {
            return NetIds.TryGetInstanceId(netId, out int instanceId)
                   && egm.TryGetSavableEntity(instanceId, out var se) && se != null;
        }

        private static bool TryClientDist(List<(byte slot, Vector2 pos)> clients, byte slot, Vector2 pos, out float dist)
        {
            foreach (var (s, ppos) in clients)
                if (s == slot) { dist = Vector2.Distance(ppos, pos); return true; }
            dist = float.MaxValue;
            return false; // slot isn't a live connected client (disconnected / dead)
        }

        private static byte NearestClient(List<(byte slot, Vector2 pos)> clients, Vector2 pos,
            float maxDist, int netId, float now, out float bestDist)
        {
            byte best = 255;
            bestDist = maxDist;
            foreach (var (slot, ppos) in clients)
            {
                if (Denied(netId, slot, now)) continue;
                float d = Vector2.Distance(ppos, pos);
                if (d <= bestDist) { bestDist = d; best = slot; }
            }
            return best == 255 ? HostSlotFallback() : best;
        }

        // The host slot for "no client took it" — read from the session so migration-promoted
        // hosts still fall back to themselves, not a hardcoded slot 0.
        private static byte HostSlotFallback() => NetSession.Instance != null ? NetSession.Instance.HostSlot : (byte)0;

        private static bool Denied(int netId, byte slot, float now)
        {
            if (!DeniedSlot.TryGetValue(netId, out var d)) return false;
            if (now >= d.until) { DeniedSlot.Remove(netId); return false; }
            return d.slot == slot;
        }
    }
}
