using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Authority-routed damage for player ships. A ship's HP truth lives on its owner's machine:
    /// local hits on a puppet become DAMAGE_REQUESTs to the owner (via host relay); the owner runs
    /// the full vanilla TakeDamage pipeline (shields, damage matrix, i-frames) exactly once.
    /// Death/resurrection replicate as reliable events.
    /// </summary>
    internal static class DamageSync
    {
        private static readonly NetWriter Writer = new NetWriter(64);
        private static Dictionary<uint, Resource> _resourcesByHash;
        private static bool _applyingRemote;
        internal static bool IsApplyingRemote => _applyingRemote;

        public static void Reset()
        {
            _resourcesByHash = null;
            _applyingRemote = false;
        }

        public static uint HashName(string name)
        {
            uint hash = 2166136261;
            foreach (char c in name)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }

        private static Resource ResolveType(uint hash)
        {
            if (hash == 0) return null;
            if (_resourcesByHash == null)
            {
                _resourcesByHash = new Dictionary<uint, Resource>();
                foreach (var res in Resources.FindObjectsOfTypeAll<Resource>())
                    _resourcesByHash[HashName(res.name)] = res;
            }
            return _resourcesByHash.TryGetValue(hash, out var r) ? r : null;
        }

        // ---------------------------------------------------------------- interception

        /// <summary>Non-null routing target when this victim is simulated elsewhere.</summary>
        private static bool TryGetRemoteTarget(DamagableResource dr, out bool isEntity, out byte slot, out int netId)
        {
            isEntity = false;
            slot = 0;
            netId = 0;
            var shipPuppet = dr.GetComponent<RemotePuppet>();
            if (shipPuppet != null)
            {
                slot = shipPuppet.Slot;
                return true;
            }
            var entityPuppet = dr.GetComponent<RemoteEntityPuppet>();
            if (entityPuppet != null)
            {
                if (entityPuppet.NetId < 0) return false; // muted orphan — damage applies locally
                isEntity = true;
                netId = entityPuppet.NetId;
                return true;
            }
            return false;
        }

        // Pre-shield chokepoint: remote-simulated victims never take damage locally.
        [HarmonyPatch(typeof(DamagableResource), "TakeDamage", typeof(Damage))]
        internal static class RouteTakeDamage
        {
            private static bool Prefix(DamagableResource __instance, Damage __0)
            {
                if (!NetSession.Active || _applyingRemote) return true;
                if (!TryGetRemoteTarget(__instance, out bool isEntity, out byte slot, out int netId)) return true;
                if (ProjectileSync.FriendlyExplosionBlocked(__instance)) return false; // FF off: my AoE spares teammates
                SendDamageRequest(isEntity, slot, netId, __0);
                UnitStatus.PlayDamageFlash(__instance); // instant local feedback; HP truth arrives later
                return false;
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "TakeDamage", typeof(IReadOnlyList<Damage>))]
        internal static class RouteTakeDamageList
        {
            private static bool Prefix(DamagableResource __instance, IReadOnlyList<Damage> __0)
            {
                if (!NetSession.Active || _applyingRemote) return true;
                if (!TryGetRemoteTarget(__instance, out bool isEntity, out byte slot, out int netId)) return true;
                if (ProjectileSync.FriendlyExplosionBlocked(__instance)) return false; // FF off: my AoE spares teammates
                foreach (var damage in __0) SendDamageRequest(isEntity, slot, netId, damage);
                UnitStatus.PlayDamageFlash(__instance); // instant local feedback; HP truth arrives later
                return false;
            }
        }

        // World-sourced contact damage (cells, hazards, electricity, rams) fires from local physics
        // on BOTH simulations of an entity. Only the victim's authority may apply it; the local
        // duplicate against a remote-simulated victim is dropped before it reaches TakeDamage.
        [HarmonyPatch]
        internal static class DropWorldDamageOnRemoteVictims
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var name in new[] { "OnCellCollision", "OnHazardTouched", "OnHitByElectricity" })
                {
                    var m = AccessTools.Method(typeof(HealthBase), name);
                    if (m != null) yield return m;
                }
            }

            private static bool Prefix(HealthBase __instance)
            {
                if (!NetSession.Active) return true;
                return __instance.GetComponent<RemotePuppet>() == null
                       && __instance.GetComponent<RemoteEntityPuppet>() == null;
            }
        }

        // Final chokepoint (burn ticks, direct calls): puppets' HP is written only by snapshots.
        [HarmonyPatch(typeof(DamagableResource), "Damage", typeof(float))]
        internal static class BlockPuppetDamage
        {
            private static bool Prefix(DamagableResource __instance)
            {
                if (!NetSession.Active || _applyingRemote) return true;
                return __instance.GetComponent<RemotePuppet>() == null
                    && __instance.GetComponent<RemoteEntityPuppet>() == null;
            }
        }

        private static void SendDamageRequest(bool isEntity, byte targetSlot, int targetNetId, Damage damage)
        {
            var session = NetSession.Instance;
            float amount;
            Resource type;
            try
            {
                amount = Traverse.Create(damage).Field("amount").GetValue<float>();
                type = Traverse.Create(damage).Field("damageType").GetValue() as Resource;
            }
            catch
            {
                return;
            }
            if (amount <= 0) return;
            // Host shooting a client-simulated entity never re-enters Dispatch — mark here.
            if (isEntity && session.IsHost) Core.AuthorityManager.NoteCombat(targetNetId);
            var msg = new DamageRequestMsg
            {
                IsEntity = isEntity,
                TargetSlot = targetSlot,
                TargetNetId = targetNetId,
                Amount = amount,
                TypeHash = type != null ? HashName(type.name) : 0,
            };
            Writer.Reset();
            msg.Write(Writer);
            session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
        }

        // ---------------------------------------------------------------- application (on owner)

        public static void ApplyDamageRequest(DamageRequestMsg msg)
        {
            var session = NetSession.Instance;
            DamagableResource dr = null;
            if (msg.IsEntity)
            {
                // OwnerOf defaults unassigned entities to the current host's slot, so this
                // single check covers both assigned and host-fallback ownership.
                if (!EnemySync.IsLocallyOwned(msg.TargetNetId)) return;
                if (Core.NetIds.TryGetInstanceId(msg.TargetNetId, out int instanceId))
                {
                    try
                    {
                        var egm = ServiceLocator.Get<EntityGameObjectManager>();
                        if (egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                            dr = se.GetComponent<DamagableResource>();
                    }
                    catch { }
                }
            }
            else
            {
                if (msg.TargetSlot != session.LocalSlot) return; // host routing delivers only ours here
                dr = ShipSync.LocalShip != null ? ShipSync.LocalShip.GetComponent<DamagableResource>() : null;
            }
            if (dr == null) return;

            _applyingRemote = true;
            try
            {
                var type = ResolveType(msg.TypeHash);
                if (type != null)
                    dr.TakeDamage(new Damage(msg.Amount, type)); // full pipeline: shields, matrix, i-frames
                else
                    dr.Damage(msg.Amount); // untyped fallback
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Damage] apply failed: {e.Message}");
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        // Puppets may only resurrect via their owner's SHIP_RESURRECTED event — vanilla systems
        // (e.g. the station-unlock respawn) must not revive them locally.
        [HarmonyPatch(typeof(Ship), "Resurrect")]
        internal static class GatePuppetResurrect
        {
            private static bool Prefix(Ship __instance)
            {
                if (!NetSession.Active || _applyingRemote) return true;
                return __instance.GetComponent<RemotePuppet>() == null;
            }
        }

        // ---------------------------------------------------------------- death / resurrection

        // Organic deaths never call Ship.Die() — the chain is DamagableResource.Die ->
        // onDeath (UnityEvent) -> Ship.OnDeath. Hooking OnDeath catches both organic and
        // scripted deaths; without it a damage death was never broadcast and the victim's
        // puppet stayed visible, frozen, on everyone else's screen.
        [HarmonyPatch(typeof(Ship), "OnDeath")]
        internal static class BroadcastDeath
        {
            private static void Postfix(Ship __instance)
            {
                SendLifeEvent(__instance, died: true);
            }
        }

        [HarmonyPatch(typeof(Ship), "Resurrect")]
        internal static class BroadcastResurrect
        {
            private static void Postfix(Ship __instance)
            {
                SendLifeEvent(__instance, died: false);
            }
        }

        private static void SendLifeEvent(Ship ship, bool died)
        {
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
            if (ship != ShipSync.LocalShip) return; // only our own ship's fate is ours to announce
            Writer.Reset();
            new ShipLifeMsg { Slot = (byte)session.LocalSlot }.Write(Writer, died);
            session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
            if (died) NetStats.AddDeath(session.LocalSlot);
            Plugin.Log.LogInfo($"[Damage] local ship {(died ? "died" : "resurrected")} — broadcast");
        }

        public static void ApplyLifeEvent(ShipLifeMsg msg, bool died)
        {
            if (!ShipSync.ShipsBySlot.TryGetValue(msg.Slot, out var ship) || ship == null) return;
            if (ship.GetComponent<RemotePuppet>() == null) return; // our own echo
            if (died) NetStats.AddDeath(msg.Slot);
            _applyingRemote = true;
            try
            {
                if (died)
                {
                    // Ship.Die routes through DamagableResource.Damage, which i-frames or
                    // invincibility can silently swallow on a puppet — the ship then never
                    // deactivates and its station keeps the shop pose. DamagableResource.Die
                    // is unconditional (same approach the enemy kill path uses).
                    var dr = ship.GetComponent<DamagableResource>();
                    if (dr != null && !dr.IsDead) dr.Die();
                    else ship.Die();
                    RemotePuppet.ScrubInteractions(ship);
                }
                else
                {
                    ship.Resurrect();
                }
                Plugin.Log.LogInfo($"[Damage] applied remote {(died ? "death" : "resurrect")} for slot {msg.Slot}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Damage] life event failed: {e.Message}");
            }
            finally
            {
                _applyingRemote = false;
            }
        }
    }
}
