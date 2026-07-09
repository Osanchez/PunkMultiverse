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
    /// Player minions/drones. Fixed owner-authority: whoever spawned a minion simulates it
    /// forever (they orbit their owner, so the owner is essentially always closest — skipping
    /// handoff removes an entire failure class). Spawns are captured at Unit.SetOwner (the one
    /// funnel every minion path goes through) and replayed by instantiating the same prefab from
    /// SavablesCollection, immediately muted as a RemoteEntityPuppet.
    /// </summary>
    internal static class MinionSync
    {
        public const int RuntimeIdBase = 1 << 12;

        private static int _counter;
        private static bool _applyingRemote;
        private static readonly NetWriter Writer = new NetWriter(128);

        public static void Reset()
        {
            _counter = 0;
            _applyingRemote = false;
        }

        // ---------------------------------------------------------------- capture (owner side)

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

                    int netId = RuntimeIdBase * (session.LocalSlot + 1) + _counter++;
                    NetIds.RegisterRuntime(netId, se.EntityData.instanceId);
                    EnemySync.Owners[netId] = (byte)session.LocalSlot;

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
            if (msg.OwnerSlot == session.LocalSlot) return; // our own echo
            _applyingRemote = true;
            try
            {
                var prefab = FindPrefab(msg.EntityId);
                if (prefab == null)
                {
                    Plugin.Log.LogWarning($"[Minions] no prefab for '{msg.EntityId}'");
                    return;
                }
                var egm = ServiceLocator.Get<EntityGameObjectManager>();
                var created = AccessTools.Method(typeof(EntityGameObjectManager), "CreateEntity")
                    .Invoke(egm, new object[] { prefab, msg.Pos });

                int instanceId = ExtractInstanceId(created);
                if (instanceId == 0)
                {
                    Plugin.Log.LogWarning($"[Minions] could not resolve spawned minion instance for '{msg.EntityId}'");
                    return;
                }
                NetIds.RegisterRuntime(msg.NetId, instanceId);
                EnemySync.Owners[msg.NetId] = msg.OwnerSlot;

                if (egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                {
                    var puppet = se.gameObject.AddComponent<RemoteEntityPuppet>();
                    puppet.NetId = msg.NetId;
                    // Faction/HUD wiring: the minion belongs to the remote player's puppet ship.
                    if (ShipSync.ShipsBySlot.TryGetValue(msg.OwnerSlot, out var ownerShip) && ownerShip != null)
                    {
                        var ownerUnit = ownerShip.GetComponent<Unit>();
                        var minionUnit = se.GetComponent<Unit>();
                        if (ownerUnit != null && minionUnit != null)
                        {
                            var setOwner = AccessTools.Method(typeof(Unit), "SetOwner");
                            var connectionType = setOwner.GetParameters()[1].ParameterType;
                            setOwner.Invoke(minionUnit, new object[] { ownerUnit, Enum.ToObject(connectionType, 0) });
                        }
                    }
                }
                Plugin.Log.LogInfo($"[Minions] spawned remote minion '{msg.EntityId}' netId {msg.NetId} (P{msg.OwnerSlot + 1})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Minions] replay failed: {e.Message}");
            }
            finally
            {
                _applyingRemote = false;
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
