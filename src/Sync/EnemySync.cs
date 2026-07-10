using System;
using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Entity (enemy/minion) replication under the proximity-authority model. Exactly one machine
    /// simulates each entity (its "owner"; slot 0 / host by default), streams ENTITY_STATE at
    /// EntityStateHz for spawned entities, and announces kills. Everyone else runs the entity as a
    /// muted RemoteEntityPuppet. Ownership changes arrive as AUTH_ASSIGN batches from the host's
    /// AuthorityManager and are applied idempotently here.
    /// </summary>
    internal static class EnemySync
    {
        /// <summary>netId -> owner slot. Missing key = host (slot 0).</summary>
        public static readonly Dictionary<int, byte> Owners = new Dictionary<int, byte>();
        /// <summary>Runtime ids exempt from proximity handoff (player minions).</summary>
        public static readonly HashSet<int> FixedOwners = new HashSet<int>();
        private static readonly HashSet<int> KilledNetIds = new HashSet<int>();
        private static readonly Dictionary<int, Vector2> LastSentPos = new Dictionary<int, Vector2>();
        private static readonly NetWriter Writer = new NetWriter(2048);
        private static float _nextSendAt;
        private static bool _applyingRemote;

        public static void Reset()
        {
            Owners.Clear();
            FixedOwners.Clear();
            KilledNetIds.Clear();
            LastSentPos.Clear();
            UnitStatus.Reset();
            _nextSendAt = 0;
            _applyingRemote = false;
            _loggedFirstAssign = false;
            _loggedFirstState = false;
        }

        /// <summary>netId -> owner slot; unassigned entities belong to the CURRENT host
        /// (which is not necessarily slot 0 after a host migration).</summary>
        public static byte OwnerOf(int netId)
        {
            if (Owners.TryGetValue(netId, out var slot)) return slot;
            var session = NetSession.Instance;
            return session != null ? session.HostSlot : (byte)0;
        }

        public static List<int> KilledSnapshot() => new List<int>(KilledNetIds);

        public static List<(int netId, byte owner)> OwnersSnapshot()
        {
            var list = new List<(int, byte)>(Owners.Count);
            foreach (var kv in Owners) list.Add((kv.Key, kv.Value));
            return list;
        }

        public static bool IsLocallyOwned(int netId)
        {
            var session = NetSession.Instance;
            return session != null && OwnerOf(netId) == session.LocalSlot;
        }

        /// <summary>Kill/authority helpers need the netId of an arbitrary component's entity.</summary>
        public static bool TryGetNetId(Component c, out int netId)
        {
            netId = 0;
            var se = c != null ? c.GetComponentInParent<SavableEntity>() : null;
            if (se == null || se.EntityData == null) return false;
            return NetIds.TryGetNetId(se.EntityData.instanceId, out netId);
        }

        // ---------------------------------------------------------------- spawn hook

        // Whenever an entity GameObject streams in, align it with the current authority state.
        [HarmonyPatch(typeof(EntityGameObjectManager), "SpawnObjectForEntity")]
        internal static class OnEntitySpawned
        {
            private static void Postfix(EntityData __0)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || __0 == null) return;
                if (!NetIds.TryGetNetId(__0.instanceId, out int netId)) return;
                try { ApplyOwnership(netId, __0.instanceId); } catch { }
                try { ProgressionSync.ApplyPendingFor(netId); } catch { }
            }
        }

        private static void ApplyOwnership(int netId, int instanceId)
        {
            var session = NetSession.Instance;
            var egm = ServiceLocator.Get<EntityGameObjectManager>();
            if (!egm.TryGetSavableEntity(instanceId, out var se) || se == null) return;
            if (se.GetComponent<Unit>() == null) return; // static prop — no authority needed

            var puppet = se.GetComponent<RemoteEntityPuppet>();
            bool mine = OwnerOf(netId) == session.LocalSlot;
            if (mine && puppet != null) UnityEngine.Object.Destroy(puppet);
            if (!mine && puppet == null)
            {
                puppet = se.gameObject.AddComponent<RemoteEntityPuppet>();
                puppet.NetId = netId;
            }
        }

        // ---------------------------------------------------------------- authority messages

        private static bool _loggedFirstAssign;
        private static bool _loggedFirstState;

        public static void ApplyAuthAssign(AuthAssignMsg msg)
        {
            if (!_loggedFirstAssign)
            {
                _loggedFirstAssign = true;
                Plugin.Log.LogInfo($"[Auth] first assignment batch applied ({msg.Entries.Count} entries)");
            }
            var egm = TryGetEgm();
            foreach (var (netId, owner) in msg.Entries)
            {
                Owners[netId] = owner;
                if (egm != null && NetIds.TryGetInstanceId(netId, out int instanceId))
                {
                    try { ApplyOwnership(netId, instanceId); } catch { }
                }
            }
        }

        private static EntityGameObjectManager TryGetEgm()
        {
            try { return ServiceLocator.Get<EntityGameObjectManager>(); } catch { return null; }
        }

        // ---------------------------------------------------------------- state streaming

        /// <summary>Called from NetSession.Update while InGame: stream entities I own.</summary>
        public static void Tick(NetSession session)
        {
            if (!NetIds.ManifestComplete || Time.unscaledTime < _nextSendAt) return;
            _nextSendAt = Time.unscaledTime + 1f / Mathf.Max(1f, NetConfig.EntityStateHz.Value);

            var egm = TryGetEgm();
            if (egm == null) return;

            var entries = new List<EntityStateEntry>(32);
            foreach (var kv in Owners)
            {
                if (kv.Value != session.LocalSlot || KilledNetIds.Contains(kv.Key)) continue;
                CollectEntry(egm, kv.Key, entries);
            }
            // Host also owns every un-assigned entity; it only streams the spawned ones.
            if (session.IsHost)
            {
                foreach (var netId in HostSpawnedUnassigned(egm))
                    CollectEntry(egm, netId, entries);
            }

            for (int start = 0; start < entries.Count; start += 32)
            {
                int count = Math.Min(32, entries.Count - start);
                Writer.Reset();
                new EntityStateMsg { Entries = entries.GetRange(start, count) }.Write(Writer);
                session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
            }
        }

        private static readonly List<int> _hostScratch = new List<int>(64);

        private static List<int> HostSpawnedUnassigned(EntityGameObjectManager egm)
        {
            _hostScratch.Clear();
            // Entities near the host stream in on the host; those without an explicit owner are ours.
            foreach (var unit in UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsSortMode.None))
            {
                if (unit.GetComponent<RemotePuppet>() != null) continue;         // player ships handled by ShipSync
                if (unit.GetComponent<Ship>() != null) continue;
                if (!TryGetNetId(unit, out int netId)) continue;
                if (Owners.ContainsKey(netId) || KilledNetIds.Contains(netId)) continue;
                _hostScratch.Add(netId);
            }
            return _hostScratch;
        }

        private static void CollectEntry(EntityGameObjectManager egm, int netId, List<EntityStateEntry> entries)
        {
            if (!NetIds.TryGetInstanceId(netId, out int instanceId)) return;
            if (!egm.TryGetSavableEntity(instanceId, out var se) || se == null) return;
            if (se.GetComponent<RemoteEntityPuppet>() != null) return; // stale ownership — not actually ours
            var rb = se.GetComponent<Rigidbody2D>();
            var dr = se.GetComponent<DamagableResource>();
            Vector2 pos = rb != null ? rb.position : (Vector2)se.transform.position;
            // Non-Unit props (pushable rocks etc.) only stream while actually moving.
            if (se.GetComponent<Unit>() == null)
            {
                if (rb == null) return;
                if (LastSentPos.TryGetValue(netId, out var last) && Vector2.Distance(last, pos) < 0.05f) return;
            }
            LastSentPos[netId] = pos;
            float hp = 1f;
            try { if (dr != null && dr.MaxHealth > 0) hp = dr.CurrentHealth / dr.MaxHealth; } catch { }
            entries.Add(new EntityStateEntry
            {
                NetId = netId,
                Pos = pos,
                Vel = rb != null ? rb.linearVelocity : Vector2.zero,
                Rot = rb != null ? rb.rotation : se.transform.eulerAngles.z,
                Aim = UnitStatus.ReadAim(se),
                State = UnitStatus.ReadState(se),
                Fire = UnitStatus.ReadFireState(se),
                Ammo = UnitStatus.ReadAmmoFraction(se),
                HpFraction = hp,
                ShieldFraction = UnitStatus.ReadShieldFraction(se),
                BurnLevel = UnitStatus.ReadBurnLevel(se),
            });
        }

        public static void ApplyEntityState(EntityStateMsg msg)
        {
            if (!_loggedFirstState)
            {
                _loggedFirstState = true;
                Plugin.Log.LogInfo($"[Enemies] receiving entity states (first batch: {msg.Entries.Count})");
            }
            var egm = TryGetEgm();
            EntityManager em = null;
            try { em = ServiceLocator.Get<EntityManager>(); } catch { }
            foreach (var e in msg.Entries)
            {
                if (IsLocallyOwned(e.NetId)) continue; // our own echo via relay
                if (!NetIds.TryGetInstanceId(e.NetId, out int instanceId)) continue;

                // Keep the data-side position fresh so stream-in spawns at the right spot.
                try
                {
                    var data = em?.GetEntity(instanceId);
                    if (data != null) data.position = new Vector3(e.Pos.x, e.Pos.y, data.position.z);
                }
                catch { }

                if (egm != null && egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                {
                    var puppet = se.GetComponent<RemoteEntityPuppet>();
                    if (puppet == null && se.GetComponent<Unit>() != null)
                    {
                        puppet = se.gameObject.AddComponent<RemoteEntityPuppet>();
                        puppet.NetId = e.NetId;
                    }
                    if (puppet == null)
                    {
                        // Pushed physics prop: snap its body to the authority's position.
                        var propRb = se.GetComponent<Rigidbody2D>();
                        if (propRb != null) propRb.position = e.Pos;
                    }
                    puppet?.PushSnapshot(Time.unscaledTime, e.Pos, e.Vel, e.Rot, e.Aim);
                    if (puppet != null)
                    {
                        UnitStatus.WriteState(se, e.State);
                        UnitStatus.WriteFireState(se, e.Fire);
                        UnitStatus.WriteShieldFraction(se, e.ShieldFraction);
                        UnitStatus.WriteAmmoFraction(se, e.Ammo);
                    }
                    UnitStatus.WriteBurnLevel(se, e.BurnLevel);
                    try
                    {
                        var dr = se.GetComponent<DamagableResource>();
                        if (dr != null && dr.MaxHealth > 0)
                        {
                            float target = e.HpFraction * dr.MaxHealth;
                            if (Mathf.Abs(dr.CurrentHealth - target) > 0.5f)
                            {
                                // Someone else hurt this entity — show the hit flash observers
                                // would see in vanilla (the pipeline didn't run locally).
                                if (target < dr.CurrentHealth) UnitStatus.PlayDamageFlash(se);
                                dr.CurrentHealth = target;
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        // ---------------------------------------------------------------- kills

        [HarmonyPatch(typeof(DamagableResource), "Die")]
        internal static class BroadcastEntityDeath
        {
            private static void Postfix(DamagableResource __instance)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
                if (__instance.GetComponent<Ship>() != null) return; // ships have their own life events
                if (!TryGetNetId(__instance, out int netId)) return;
                // Announce any death WE simulated (no puppet = live local sim). Checking the
                // ownership registrar instead loses kills that land mid-handoff — the other
                // machine then keeps a live copy and HP-syncs our corpse back up (zombies).
                // A double announce after a race is harmless: receivers dedupe on KilledNetIds.
                if (__instance.GetComponentInParent<RemoteEntityPuppet>() != null) return;
                if (!KilledNetIds.Add(netId)) return;
                NetStats.AddKill(session.LocalSlot);

                Writer.Reset();
                new EntityKilledMsg { NetId = netId, KillerSlot = (byte)session.LocalSlot }.Write(Writer);
                session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
            }
        }

        public static void ApplyEntityKilled(EntityKilledMsg msg)
        {
            if (!KilledNetIds.Add(msg.NetId)) return;
            NetStats.AddKill(msg.KillerSlot);
            if (!NetIds.TryGetInstanceId(msg.NetId, out int instanceId)) return;
            _applyingRemote = true;
            try
            {
                var egm = TryGetEgm();
                if (egm != null && egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                {
                    var dr = se.GetComponent<DamagableResource>();
                    if (dr != null) { dr.Die(); return; }
                    var health = se.GetComponent<Health>();
                    if (health != null)
                    {
                        AccessTools.Method(typeof(Health), "Die")?.Invoke(health, null);
                        return;
                    }
                }
                // Not spawned here: kill the data so a later stream-in doesn't resurrect it.
                var em = ServiceLocator.Get<EntityManager>();
                var data = em.GetEntity(instanceId);
                if (data != null)
                {
                    var destroy = AccessTools.Method(data.GetType(), "Destroy");
                    if (destroy != null && destroy.GetParameters().Length == 0) destroy.Invoke(data, null);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Enemies] kill apply failed for netId {msg.NetId}: {e.Message}");
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        [HarmonyPatch(typeof(Health), "Die")]
        internal static class BroadcastPropDeath
        {
            private static void Postfix(Health __instance)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
                if (!TryGetNetId(__instance, out int netId)) return;
                if (!KilledNetIds.Add(netId)) return;
                Writer.Reset();
                new EntityKilledMsg { NetId = netId, KillerSlot = (byte)session.LocalSlot }.Write(Writer);
                session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
            }
        }

        public static bool SuppressLocalDeathEffects => _applyingRemote;
    }
}
