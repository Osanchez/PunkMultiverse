using System;
using System.Collections;
using System.Collections.Generic;
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
        // Runtime ids must clear every manifest id (manifest netIds are 0..N-1 and real worlds
        // exceed 6,500 entities — 1<<12 collided mid-manifest, aliasing a pickup and an enemy
        // under one netId). 1<<20 per slot keeps them disjoint unless a world has >1M entities.
        public const int RuntimeIdBase = 1 << 20;

        private static int _counter;
        private static bool _applyingRemote;
        private static readonly NetWriter Writer = new NetWriter(128);

        internal static bool Replicating => _applyingRemote;

        public static void Reset()
        {
            _counter = 0;
            _applyingRemote = false;
            _prefabsById = null; // prefab assets can unload between runs
            _egmPrefabDictionary = null; // the EGM (and its dictionary) is scene-scoped
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
                    // Per-player economy: loot drops spawn locally on every client by design —
                    // replicating them would duplicate loot and drag pickups into the authority
                    // pool. Gate by call-site (the loot factories) with an id-suffix fallback.
                    if (MarkLootSpawns.Depth > 0 || IsPerPlayerLoot(data.entityId)) return;

                    int netId = AllocateNetId(session);
                    uint lifetime = NetIds.NextRuntimeLifetime(netId);
                    NetIds.RegisterRuntime(netId, instanceId, lifetime);
                    EnemySync.Owners[netId] = (byte)session.LocalSlot; // we simulate it until handoff
                    EnemySync.OnEntitySpawned.Align(data); // the spawn hook ran pre-registration — redo it

                    // "We simulate it until handoff" was never actually wired: Owners[] is only
                    // consulted for FixedOwners, so a spawn landing in a segment leased to ANOTHER
                    // player (or dormant) instantly became a puppet on its own spawner while the
                    // lease holder also treated it as a replica — a mutual-puppet zombie nobody
                    // simulates (observed live: host's own grunt spawned as owner=P2 PUPPET).
                    // Pull the lease to the spawner — the same mechanism dormant wake-on-hit uses.
                    if (Core.AuthorityManager.OwnerOf(netId) != session.LocalSlot)
                    {
                        if (session.IsHost)
                            Core.AuthorityManager.OnDormantHit(netId, (byte)session.LocalSlot);
                        else
                        {
                            Writer.Reset();
                            new EntityStarvedRequestMsg { NetId = netId }.Write(Writer);
                            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                        }
                        Plugin.Log.LogInfo($"[Spawns] spawn #{netId} landed in a foreign/dormant segment — pulling the lease to P{session.LocalSlot + 1}");
                    }

                    var msg = new EntitySpawnedMsg
                    {
                        NetId = netId,
                        OwnerSlot = (byte)session.LocalSlot,
                        Lifetime = lifetime,
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
                if (NetIds.TryGetInstanceId(msg.NetId, out _) && NetIds.LifetimeMatches(msg.NetId, msg.Lifetime)) return;
                SpawnReplica(msg.NetId, msg.Lifetime, msg.OwnerSlot, msg.EntityId, msg.Pos, wireOwnerShip: false);
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
                        uint newLifetime = NetIds.NextRuntimeLifetime(netId);
                        NetIds.RegisterRuntime(netId, se.EntityData.instanceId, newLifetime);
                    }
                    EnemySync.Owners[netId] = (byte)session.LocalSlot;
                    EnemySync.FixedOwners.Add(netId); // minions never hand off

                    var msg = new MinionSpawnedMsg
                    {
                        NetId = netId,
                        OwnerSlot = (byte)session.LocalSlot,
                        Lifetime = NetIds.LifetimeOf(netId),
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
                if (NetIds.TryGetInstanceId(msg.NetId, out int minst) && NetIds.LifetimeMatches(msg.NetId, msg.Lifetime))
                {
                    EnemySync.Owners[msg.NetId] = msg.OwnerSlot;
                    // ENTITY_SPAWNED created+HP-scaled the replica before we knew it was an allied
                    // minion (FixedOwners is only set just above). Undo that eager scaling.
                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (egm != null && egm.TryGetSavableEntity(minst, out var mse) && mse != null)
                        UnitStatus.RevertEnemyHpScale(mse, minst);
                    WireMinionOwner(msg.NetId, msg.OwnerSlot);
                    return;
                }
                SpawnReplica(msg.NetId, msg.Lifetime, msg.OwnerSlot, msg.EntityId, msg.Pos, wireOwnerShip: true);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        // Replicas are instantiated INACTIVE (below) so prefab-active actions can't burst-fire
        // before muting — but that means Awake hasn't run when Bind executes. EntityMapItem.Bind
        // dereferences its Awake-cached MapIconManager and NREd, silently killing EVERY enemy or
        // prop replica (minion prefabs carry no map icon, which is why minions still worked).
        [HarmonyPatch(typeof(EntityMapItem), "Bind")]
        internal static class HealInactiveMapItemBind
        {
            private static readonly System.Reflection.FieldInfo MimField =
                AccessTools.Field(typeof(EntityMapItem), "mapIconManager");

            private static void Prefix(EntityMapItem __instance)
            {
                try
                {
                    if (MimField != null && MimField.GetValue(__instance) == null)
                        MimField.SetValue(__instance, ServiceLocator.Get<MapIconManager>());
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- shared replica spawning

        /// <summary>Divergence heal: this machine's world lost (or never created) the entity data
        /// behind a netId another peer still simulates. A baseline/audit entry carries everything
        /// a runtime spawn does (type, position, lifetime) — recreate it through the same replica
        /// machinery instead of NACKing the segment forever. Returns true when the identity maps
        /// to concrete local entity data afterwards.</summary>
        internal static bool TryRespawnFromBaseline(int netId, uint lifetime, byte ownerSlot,
            string entityId, Vector2 pos)
        {
            _applyingRemote = true; // the CreateEntity below must not re-capture as a new spawn
            try { SpawnReplica(netId, lifetime, ownerSlot, entityId, pos, wireOwnerShip: false); }
            finally { _applyingRemote = false; }
            if (!NetIds.TryGetInstanceId(netId, out int instanceId)) return false;
            try { return ServiceLocator.Get<EntityManager>()?.GetEntity(instanceId) != null; }
            catch { return false; }
        }

        private static void SpawnReplica(int netId, uint lifetime, byte ownerSlot, string entityId, Vector2 pos, bool wireOwnerShip)
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
                // Instantiate the replica INACTIVE so prefab-active actions (ShootComplexAction,
                // ProjectileDispenser) can't fire from OnEnable before the puppet mutes them —
                // their bursts only cancel on destroy, and anything they spawn here is an
                // unsynced local orphan.
                var prefabGo = prefab.gameObject;
                bool prefabWasActive = prefabGo.activeSelf;
                object created;
                try
                {
                    prefabGo.SetActive(false);
                    created = AccessTools.Method(typeof(EntityGameObjectManager), "CreateEntity")
                        .Invoke(egm, new object[] { prefab, pos });
                }
                catch (Exception bindFailure)
                {
                    // Some components can only Bind AFTER Awake ran (Shield.Setup NREs on
                    // inactive instantiation — caught live: Enemy_Turret_Seeker existed on the
                    // owner and NOWHERE else). The vanilla spawn order is active-then-Bind, so
                    // retry the vanilla way and mute immediately after — a one-frame action
                    // window is recoverable, a missing entity is a permanent divergence.
                    prefabGo.SetActive(prefabWasActive);
                    Plugin.Log.LogWarning($"[Spawns] inactive replica bind failed for '{entityId}' " +
                        $"({(bindFailure.InnerException ?? bindFailure).GetType().Name}) — retrying active (fire suppressed)");
                    // The active instantiation runs OnEnable with prefab-active shooters live; suppress
                    // any burst they fire in the one-frame window before MuteNow() (WS2.4).
                    ProjectileSync.BeginReplicaBindSuppression();
                    try
                    {
                        created = AccessTools.Method(typeof(EntityGameObjectManager), "CreateEntity")
                            .Invoke(egm, new object[] { prefab, pos });
                    }
                    finally { ProjectileSync.EndReplicaBindSuppression(); }
                }
                finally
                {
                    prefabGo.SetActive(prefabWasActive);
                }

                int instanceId = ExtractInstanceId(created);
                if (instanceId == 0)
                {
                    Plugin.Log.LogWarning($"[Spawns] could not resolve spawned instance for '{entityId}'");
                    return;
                }
                NetIds.RegisterRuntime(netId, instanceId, lifetime);
                EnemySync.Owners[netId] = ownerSlot;

                if (egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                {
                    // Every replica gets the puppet — non-Unit spawned props carry auto-firers
                    // (ProjectileDispenser) that must be muted too, not just enemy AI.
                    var puppet = se.gameObject.AddComponent<RemoteEntityPuppet>();
                    puppet.NetId = netId;
                    puppet.MuteNow();
                    se.gameObject.SetActive(true);
                    UnitStatus.ApplyEnemyHpScale(se, instanceId, netId);
                    // The game's spawn hook ran during CreateEntity, BEFORE RegisterRuntime — the
                    // same race the spawner side heals with Align. Without it the replica never
                    // enters LiveEntities, and the receive path has nowhere to push snapshots:
                    // the puppet stays frozen at its spawn pose forever (observed with minions).
                    if (se.EntityData != null) EnemySync.OnEntitySpawned.Align(se.EntityData);
                }
                if (wireOwnerShip) WireMinionOwner(netId, ownerSlot);
                Plugin.Log.LogInfo($"[Spawns] replica '{entityId}' netId {netId} (P{ownerSlot + 1})");
            }
            catch (Exception e)
            {
                // CreateEntity is invoked via reflection — unwrap, or the real error is invisible.
                var real = e is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException : e;
                Plugin.Log.LogWarning($"[Spawns] replica failed for '{entityId}': {real}");
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
                // SetOwner's first parameter is the owner's SavableEntity — passing the Unit threw
                // an ArgumentException on every wiring, so no minion replica ever had an owner.
                var ownerEntity = ownerShip.GetComponent<SavableEntity>();
                var minionUnit = se.GetComponent<Unit>();
                if (ownerEntity == null || minionUnit == null) return;
                var setOwner = AccessTools.Method(typeof(Unit), "SetOwner");
                var connectionType = setOwner.GetParameters()[1].ParameterType;
                // Connection type 0 on purpose: the puppet ship's own SpawnMinionModule culls
                // excess minions by matching connection type — replicas must never match it.
                setOwner.Invoke(minionUnit, new object[] { ownerEntity, Enum.ToObject(connectionType, 0) });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Minions] owner wiring failed: {e.Message}");
            }
        }

        private static bool IsPerPlayerLoot(string entityId) =>
            entityId != null && entityId.EndsWith("_pickup", StringComparison.OrdinalIgnoreCase);

        // Every loot/pickup factory Create() runs on each machine for the same logical event
        // (cell-destruction drops, death drops) — anything they spawn is per-player and must
        // never broadcast, whatever its entityId.
        [HarmonyPatch]
        internal static class MarkLootSpawns
        {
            internal static int Depth;

            private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var typeName in new[]
                         { "LootFactory", "ModulePickupFactory", "IngredientPickupFactory", "ConsumablePickupFactory" })
                {
                    var t = AccessTools.TypeByName(typeName);
                    if (t == null) continue;
                    foreach (var m in AccessTools.GetDeclaredMethods(t))
                        if (m.Name == "Create")
                            yield return m;
                }
            }

            private static void Prefix() => Depth++;
            private static void Finalizer() => Depth--;
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

        private static Dictionary<string, SavableEntity> _prefabsById;
        private static Dictionary<string, SavableEntity> _egmPrefabDictionary;

        public static void ResetPrefabCache() => _egmPrefabDictionary = null;

        internal static SavableEntity FindPrefab(string entityId)
        {
            // The game's own stream-in resolver: EntityGameObjectManager.entityPrefabDictionary
            // (entityId -> prefab, built at boot from SavablesCollection.savableObjectInfos).
            // This is what SpawnObjectForEntity uses, so it covers every spawnable entity.
            // Cache the dictionary REFERENCE: baseline roster builds call this per entity, and
            // a Traverse reflection walk per call showed up in the frame profiler.
            try
            {
                if (_egmPrefabDictionary == null)
                {
                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    _egmPrefabDictionary = Traverse.Create(egm).Field("entityPrefabDictionary").GetValue()
                        as Dictionary<string, SavableEntity>;
                }
                if (_egmPrefabDictionary != null
                    && _egmPrefabDictionary.TryGetValue(entityId, out var prefab) && prefab != null)
                    return prefab;
            }
            catch { }

            // Fallback: every SavableEntity prefab asset carries its entityId publicly.
            if (_prefabsById == null)
            {
                _prefabsById = new Dictionary<string, SavableEntity>();
                foreach (var se in Resources.FindObjectsOfTypeAll<SavableEntity>())
                    if (se != null && !se.gameObject.scene.IsValid() && !string.IsNullOrEmpty(se.entityId))
                        _prefabsById[se.entityId] = se;
            }
            return _prefabsById.TryGetValue(entityId, out var fallback) ? fallback : null;
        }
    }
}
