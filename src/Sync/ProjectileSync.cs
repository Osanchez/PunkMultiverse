using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Weapon-fire replication. The local player's shots are captured at WeaponBase.DoShoot (one
    /// call per actual burst tick, sync — never patch the async Fire) and replayed on peers through
    /// the puppet ship's own weapon via a FakeBarrel. Replayed projectiles are VISUAL ONLY: real
    /// damage happens exactly once, on the simulating owner's machine (routed by DamageSync).
    /// </summary>
    internal static class ProjectileSync
    {
        private static readonly HashSet<int> VisualProjectiles = new HashSet<int>();
        private static int _replayDepth;
        private static readonly NetWriter Writer = new NetWriter(64);

        // weapon -> owning net entity, for enemy/boss fire capture (enemy weapons are stable
        // per instance, unlike ship weapons which rebuild on grid refresh).
        private static readonly Dictionary<WeaponBase, (SavableEntity se, int netId)> EntityWeapons
            = new Dictionary<WeaponBase, (SavableEntity, int)>();

        public static bool IsReplaying => _replayDepth > 0;

        public static void Reset()
        {
            VisualProjectiles.Clear();
            EntityWeapons.Clear();
            _replayDepth = 0;
        }

        // ---------------------------------------------------------------- capture

        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        internal static class CaptureFire
        {
            private static void Postfix(WeaponBase __instance, IBarrel __0)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _replayDepth > 0) return;
                try
                {
                    int holder = FindLocalHolder(__instance);
                    if (holder >= 0)
                    {
                        var msg = new FireEventMsg
                        {
                            Slot = (byte)session.LocalSlot,
                            Holder = (byte)holder,
                            Pos = __0.Position,
                            Dir = __0.Direction,
                        };
                        Writer.Reset();
                        msg.Write(Writer);
                        session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
                        return;
                    }
                    TryCaptureEntityFire(session, __instance, __0);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"[Fire] capture failed: {e.Message}");
                }
            }
        }

        private static int FindLocalHolder(WeaponBase weapon)
        {
            var ship = ShipSync.LocalShip;
            if (ship == null) return -1;
            if (GetHolderWeapon(ship, 0) == weapon) return 0;
            if (GetHolderWeapon(ship, 1) == weapon) return 1;
            return -1;
        }

        private static WeaponBase GetHolderWeapon(Ship ship, int holder)
        {
            try
            {
                var field = holder == 0 ? "primaryWeaponHolder" : "secondaryWeaponHolder";
                var wh = Traverse.Create(ship).Field(field).GetValue() as WeaponHolder;
                return wh != null ? wh.Weapon : null;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- entity (enemy/boss) fire

        private static void TryCaptureEntityFire(NetSession session, WeaponBase weapon, IBarrel barrel)
        {
            if (!EntityWeapons.TryGetValue(weapon, out var owner))
            {
                owner = ResolveEntityWeapon(weapon);
                EntityWeapons[weapon] = owner; // caches failures as (null, 0) too
            }
            if (owner.se == null) return;
            // Only the entity's simulating authority announces its shots.
            bool mine = EnemySync.IsLocallyOwned(owner.netId)
                        || (session.IsHost && !EnemySync.Owners.ContainsKey(owner.netId));
            if (!mine || owner.se.GetComponent<RemoteEntityPuppet>() != null) return;

            var msg = new EntityFireMsg { NetId = owner.netId, Pos = barrel.Position, Dir = barrel.Direction };
            Writer.Reset();
            msg.Write(Writer);
            session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
        }

        private static (SavableEntity, int) ResolveEntityWeapon(WeaponBase weapon)
        {
            foreach (var shooter in Object.FindObjectsByType<Shooter>(FindObjectsSortMode.None))
            {
                var w = Traverse.Create(shooter).Field("weapon").GetValue() as WeaponBase;
                if (w != weapon) continue;
                var se = shooter.GetComponentInParent<SavableEntity>();
                if (se != null && se.EntityData != null && Core.NetIds.TryGetNetId(se.EntityData.instanceId, out int netId))
                    return (se, netId);
                break;
            }
            return (null, 0);
        }

        public static void ReplayEntityFire(EntityFireMsg msg)
        {
            if (!Core.NetIds.TryGetInstanceId(msg.NetId, out int instanceId)) return;
            try
            {
                var egm = ServiceLocator.Get<EntityGameObjectManager>();
                if (!egm.TryGetSavableEntity(instanceId, out var se) || se == null) return;
                if (se.GetComponent<RemoteEntityPuppet>() == null) return; // we own it — our own echo
                var shooter = se.GetComponentInChildren<Shooter>(true);
                var weapon = shooter != null ? Traverse.Create(shooter).Field("weapon").GetValue() as WeaponBase : null;
                if (weapon == null) return;
                _replayDepth++;
                try
                {
                    AccessTools.Method(typeof(WeaponBase), "DoShoot")
                        .Invoke(weapon, new object[] { new FakeBarrel(msg.Pos, msg.Dir) });
                }
                finally { _replayDepth--; }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Fire] entity replay failed: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- replay

        public static void ReplayFire(FireEventMsg msg)
        {
            if (!ShipSync.ShipsBySlot.TryGetValue(msg.Slot, out var ship) || ship == null) return;
            if (ship.GetComponent<RemotePuppet>() == null) return; // our own echo
            var weapon = GetHolderWeapon(ship, msg.Holder);
            if (weapon == null) return;

            _replayDepth++;
            try
            {
                var barrel = new FakeBarrel(msg.Pos, msg.Dir);
                AccessTools.Method(typeof(WeaponBase), "DoShoot").Invoke(weapon, new object[] { barrel });
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Fire] replay failed: {e.Message}");
            }
            finally
            {
                _replayDepth--;
            }
        }

        // Anything spawned while replaying is visual-only.
        [HarmonyPatch(typeof(Projectile), "Shoot")]
        internal static class MarkVisualProjectile
        {
            private static void Prefix(Projectile __instance)
            {
                if (_replayDepth > 0) VisualProjectiles.Add(__instance.gameObject.GetInstanceID());
            }
        }

        // ---------------------------------------------------------------- damage suppression

        /// <summary>True when this projectile must not deal damage on this machine: it was spawned
        /// by a fire-event replay, or it belongs to a puppet's unit (its real twin lives on the
        /// owner's machine, which routes damage authoritatively).</summary>
        public static bool IsVisual(object projectile)
        {
            if (projectile is Component c)
            {
                if (VisualProjectiles.Contains(c.gameObject.GetInstanceID())) return true;
                try
                {
                    var owner = Traverse.Create(projectile).Property("Owner").GetValue() as Unit;
                    if (owner == null)
                        owner = Traverse.Create(projectile).Field("owner").GetValue() as Unit;
                    if (owner != null && (owner.GetComponent<RemotePuppet>() != null
                                          || owner.GetComponent<RemoteEntityPuppet>() != null)) return true;
                }
                catch { }
            }
            return false;
        }

        [HarmonyPatch(typeof(HealthBase), "ProjectileCollided")]
        internal static class SuppressVisualProjectileDamage
        {
            private static bool Prefix(HealthBase __instance, object __0)
            {
                if (!NetSession.Active) return true;
                if (IsVisual(__0)) return false;
                // Environmental (ownerless) projectiles fire independently on every client — only
                // the victim's own authority applies their damage, or shared enemies eat it twice.
                if (VictimIsRemote(__instance) && IsOwnerless(__0)) return false;
                return true;
            }
        }

        private static bool VictimIsRemote(Component victim)
        {
            return victim != null && (victim.GetComponent<RemotePuppet>() != null
                                      || victim.GetComponent<RemoteEntityPuppet>() != null);
        }

        private static bool IsOwnerless(object projectile)
        {
            try
            {
                var owner = Traverse.Create(projectile).Property("Owner").GetValue() as Unit;
                if (owner == null)
                    owner = Traverse.Create(projectile).Field("owner").GetValue() as Unit;
                return owner == null;
            }
            catch { return false; }
        }

        // Replayed hitscan shots do a REAL raycast — their hits must not deal damage here
        // (the simulating machine already routed it). Covers both the synchronous replay scope
        // and any beam owned by a remote-simulated unit.
        [HarmonyPatch(typeof(HealthBase), "OnHitByHitscanWeapon")]
        internal static class SuppressVisualHitscanDamage
        {
            private static bool Prefix(HealthBase __instance, object __0)
            {
                if (!NetSession.Active) return true;
                if (_replayDepth > 0) return false;
                if (OwnerIsRemote(__0)) return false;
                if (VictimIsRemote(__instance) && IsOwnerless(__0)) return false;
                return true;
            }
        }

        private static bool OwnerIsRemote(object weaponOrProjectile)
        {
            try
            {
                var owner = Traverse.Create(weaponOrProjectile).Property("Owner").GetValue() as Unit;
                if (owner == null)
                    owner = Traverse.Create(weaponOrProjectile).Field("owner").GetValue() as Unit;
                return owner != null && (owner.GetComponent<RemotePuppet>() != null
                                         || owner.GetComponent<RemoteEntityPuppet>() != null);
            }
            catch { return false; }
        }

        // Visual projectiles must not spawn REAL explosions (area damage + cell destruction would
        // double up with the simulating machine's authoritative versions — terrain truth arrives
        // via CELL_DIFF). Instead we spawn a sanitized copy with all damage/push/burn zeroed so
        // peers still see the boom. If sanitizing fails for a weapon, fall back to no explosion.
        [HarmonyPatch]
        internal static class SuppressVisualExplosions
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var typeName in new[] { "Projectile", "PhysicsProjectile" })
                {
                    var t = AccessTools.TypeByName(typeName);
                    var m = t != null ? AccessTools.Method(t, "SpawnExplosion") : null;
                    if (m != null) yield return m;
                }
            }

            private static bool Prefix(object __instance)
            {
                if (!NetSession.Active) return true;
                if (!IsVisual(__instance)) return true;
                TrySpawnHarmlessExplosion(__instance);
                return false;
            }
        }

        private static void TrySpawnHarmlessExplosion(object projectile)
        {
            try
            {
                var component = projectile as Component;
                object boxed = Traverse.Create(projectile).Field("explosion").GetValue();
                if (component == null || boxed == null) return;

                // Zero every damaging aspect of the struct copy, keep radius/types for the visuals.
                foreach (var field in boxed.GetType().GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    if (field.FieldType == typeof(float)
                        && field.Name.IndexOf("radius", System.StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        field.SetValue(boxed, 0f);
                    }
                    else if (typeof(System.Collections.IList).IsAssignableFrom(field.FieldType))
                    {
                        if (field.GetValue(boxed) is System.Collections.IList list)
                            for (int i = 0; i < list.Count; i++)
                                if (list[i] is Damage damage)
                                {
                                    var type = Traverse.Create(damage).Field("damageType").GetValue() as Resource;
                                    list[i] = new Damage(0f, type);
                                }
                    }
                }

                var manager = ServiceLocator.Get<ExplosionManager>();
                AccessTools.Method(typeof(ExplosionManager), "SpawnExplosion")
                    .Invoke(manager, new object[] { (Vector2)component.transform.position, boxed });
            }
            catch
            {
                // Visual-only nicety; correctness path (skip) already happened.
            }
        }
    }
}
