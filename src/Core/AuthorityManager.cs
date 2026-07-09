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

        private static float _nextScanAt;
        private static readonly NetWriter Writer = new NetWriter(2048);

        public static void Reset() => _nextScanAt = 0;

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
            foreach (var entity in em.GetAllEntities())
            {
                if (entity == null || entity.entityId == "Ship") continue;
                if (!NetIds.TryGetNetId(entity.instanceId, out int netId)) continue;
                if (netId >= MinionSync.RuntimeIdBase) continue; // minions: fixed owner-authority

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

                byte desired = current;
                if (closest.dist > interest)
                {
                    desired = 0; // dormant — host fallback
                }
                else if (current == 0)
                {
                    if (closest.dist <= authority && closest.slot != 0) desired = closest.slot;
                }
                else if (ownerDist > authority * 1.15f) // release hysteresis: no flip-flop at the radius edge
                {
                    desired = closest.dist <= authority ? closest.slot : (byte)0; // owner left entirely
                }
                else if (ownerDist > transfer && closest.slot != current && closest.dist < ownerDist * HysteresisFactor)
                {
                    desired = closest.slot; // handoff to a clearly-closer player
                }

                if (desired != current)
                {
                    changes.Add((netId, desired));
                    EnemySync.Owners[netId] = desired;
                    if (NetIds.TryGetInstanceId(netId, out _)) { }
                }
            }

            if (changes.Count == 0) return;
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
