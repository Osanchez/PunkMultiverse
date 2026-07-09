using System;
using System.Collections;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Runtime entity spawn replication. TWO layers:
    /// 1. GENERIC — every EntityGameObjectManager.CreateEntity on this machine mid-run (spawner
    ///    enemies, boss adds, spawned props) broadcasts ENTITY_SPAWNED so all worlds stay
    ///    identical; these join the normal proximity-authority pool.
    /// 2. MINIONS — Unit.SetOwner marks a spawn as a player's minion: fixed owner-authority
    ///    forever (they orbit their owner; skipping handoff removes a failure class).
    /// Replays instantiate the same prefab from SavablesCollection, muted as RemoteEntityPuppets.
    /// </summary>
    internal static class MinionSync
    {
        public const int RuntimeIdBase = 1 << 12;

        private static int _counter;
        private static bool _applyingRemote;
        private static readonly NetWriter Writer = new NetWriter(128);

        internal static bool Replicating => _applyingRemote;

        public static void Reset()
        {
            _counter = 0;
            _applyingRemote = false;
        }

        private static int AllocateNetId(NetSession session) => RuntimeIdBase * (session.LocalSlot + 1) + _counter++;

        // ---------------------------------------------------------------- generic runtime spawns

        [HarmonyPatch(typeof(EntityGameObjectManager), "CreateEntity")]
        internal static class CaptureRuntimeSpawn
        {
            private static void Postfix(object __result)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
                try
                {
                    int instanceId = ExtractInstanceId(__result);
                    if (instanceId == 0 || NetIds.TryGetNetId(instanceId, out _)) return;
                    var em = ServiceLocator.Get<EntityManager>();
                    var data = em.GetEntity(instanceId);
                    if (data == null || data.entityId == "Ship") return;

                    int netId = AllocateNetId(session);
                    NetIds.RegisterRuntime(netId, instanceId);
                    EnemySync.Owners[netId] = (byte)session.LocalSlot; // we simulate it until handoff

                    var msg = new EntitySpawnedMsg
                    {
                        NetId = netId,
                        OwnerSlot = (byte)session.LocalSlot,
                        EntityId = data.entityId,
                        Pos = data.position,
                    };
                    Writer.Reset();
                    msg.Write(Writer);
                    session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                    Plugin.Log.LogInfo($"[Spawns] runtime spawn '{msg.EntityId}' -> netId {netId}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Spawns] capture failed: {e.Message}");
                }
            }
        }

        public static void ApplyEntitySpawned(EntitySpawnedMsg msg)
        {
            var session = NetSession.Instance;
            if (msg.OwnerSlot == session.LocalSlot) return; // our own echo
            _applyingRemote = true;
            try
            {
                SpawnReplica(msg.NetId, msg.OwnerSlot, msg.EntityId, msg.Pos, wireOwnerShip: false);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        // ---------------------------------------------------------------- minions (fixed owner)

        [HarmonyPatch(typeof(Unit), "SetOwner")]
        internal static class CaptureMinionSpawn
        {
            private static void Postfix(Unit __instance, object __0)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
                try
                {
                    // Only minions whose new owner is OUR ship.
                    if (ShipSync.LocalShip == null || !(__0 is Component ownerComponent)) return;
                    if (ownerComponent.gameObject != ShipSync.LocalShip.gameObject) return;
                    var se = __instance.GetComponentInParent<SavableEntity>();
                    if (se == null || se.EntityData == null) return;

                    // The generic CreateEntity capture usually registered this already — reuse its id.
                    if (!NetIds.TryGetNetId(se.EntityData.instanceId, out int netId))
                    {
                        netId = AllocateNetId(session);
                        NetIds.RegisterRuntime(netId, se.EntityData.instanceId);
                    }
                    EnemySync.Owners[netId] = (byte)session.LocalSlot;
                    EnemySync.FixedOwners.Add(netId); // minions never hand off

                    var msg = new MinionSpawnedMsg
                    {
                        NetId = netId,
                        OwnerSlot = (byte)session.LocalSlot,
                        EntityId = se.EntityData.entityId,
                        Pos = se.transform.position,
                    };
                    Writer.Reset();
                    msg.Write(Writer);
                    session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                    Plugin.Log.LogInfo($"[Minions] local minion '{msg.EntityId}' -> netId {netId}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Minions] capture failed: {e.Message}");
                }
            }
        }

        // ---------------------------------------------------------------- replay (peer side)

        public static void ApplyMinionSpawned(MinionSpawnedMsg msg)
        {
            var session = NetSession.Instance;
            EnemySync.FixedOwners.Add(msg.NetId);
            if (msg.OwnerSlot == session.LocalSlot) return; // our own echo
            _applyingRemote = true;
            try
            {
                // The generic ENTITY_SPAWNED may have created the replica already — just re-own it.
                if (NetIds.TryGetInstanceId(msg.NetId, out _))
                {
                    EnemySync.Owners[msg.NetId] = msg.OwnerSlot;
                    WireMinionOwner(msg.NetId, msg.OwnerSlot);
                    return;
                }
                SpawnReplica(msg.NetId, msg.OwnerSlot, msg.EntityId, msg.Pos, wireOwnerShip: true);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        // ---------------------------------------------------------------- shared replica spawning

        private static void SpawnReplica(int netId, byte ownerSlot, string entityId, Vector2 pos, bool wireOwnerShip)
        {
            try
            {
                var prefab = FindPrefab(entityId);
                if (prefab == null)
                {
                    Plugin.Log.LogWarning($"[Spawns] no prefab for '{entityId}'");
                    return;
                }
                var egm = ServiceLocator.Get<EntityGameObjectManager>();
                var created = AccessTools.Method(typeof(EntityGameObjectManager), "CreateEntity")
                    .Invoke(egm, new object[] { prefab, pos });

                int instanceId = ExtractInstanceId(created);
                if (instanceId == 0)
                {
                    Plugin.Log.LogWarning($"[Spawns] could not resolve spawned instance for '{entityId}'");
                    return;
                }
                NetIds.RegisterRuntime(netId, instanceId);
                EnemySync.Owners[netId] = ownerSlot;

                if (egm.TryGetSavableEntity(instanceId, out var se) && se != null && se.GetComponent<Unit>() != null)
                {
                    var puppet = se.gameObject.AddComponent<RemoteEntityPuppet>();
                    puppet.NetId = netId;
                }
                if (wireOwnerShip) WireMinionOwner(netId, ownerSlot);
                Plugin.Log.LogInfo($"[Spawns] replica '{entityId}' netId {netId} (P{ownerSlot + 1})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Spawns] replica failed: {e.Message}");
            }
        }

        /// <summary>Faction/HUD wiring: a minion replica belongs to the remote player's puppet ship.</summary>
        private static void WireMinionOwner(int netId, byte ownerSlot)
        {
            try
            {
                if (!NetIds.TryGetInstanceId(netId, out int instanceId)) return;
                var egm = ServiceLocator.Get<EntityGameObjectManager>();
                if (!egm.TryGetSavableEntity(instanceId, out var se) || se == null) return;
                if (!ShipSync.ShipsBySlot.TryGetValue(ownerSlot, out var ownerShip) || ownerShip == null) return;
                var ownerUnit = ownerShip.GetComponent<Unit>();
                var minionUnit = se.GetComponent<Unit>();
                if (ownerUnit == null || minionUnit == null) return;
                var setOwner = AccessTools.Method(typeof(Unit), "SetOwner");
                var connectionType = setOwner.GetParameters()[1].ParameterType;
                setOwner.Invoke(minionUnit, new object[] { ownerUnit, Enum.ToObject(connectionType, 0) });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Minions] owner wiring failed: {e.Message}");
            }
        }

        private static int ExtractInstanceId(object created)
        {
            switch (created)
            {
                case EntityData data: return data.instanceId;
                case SavableEntity se when se.EntityData != null: return se.EntityData.instanceId;
                case Component c:
                {
                    var se2 = c.GetComponentInParent<SavableEntity>();
                    return se2 != null && se2.EntityData != null ? se2.EntityData.instanceId : 0;
                }
                default: return 0;
            }
        }

        private static SavableEntity FindPrefab(string entityId)
        {
            SavablesCollection collection = null;
            try { collection = ServiceLocator.Get<SavablesCollection>(); } catch { }
            if (collection == null)
            {
                var all = Resources.FindObjectsOfTypeAll<SavablesCollection>();
                if (all.Length > 0) collection = all[0];
            }
            if (collection == null) return null;

            // The collection holds a list of {entityId, prefab} items; walk it reflectively.
            foreach (var fieldName in new[] { "entities", "entityPrefabs", "prefabs", "items" })
            {
                var list = Traverse.Create(collection).Field(fieldName).GetValue() as IEnumerable;
                if (list == null) continue;
                foreach (var item in list)
                {
                    var id = Traverse.Create(item).Field("entityId").GetValue() as string;
                    if (id != entityId) continue;
                    return Traverse.Create(item).Field("prefab").GetValue() as SavableEntity;
                }
            }
            return null;
        }
    }
}
