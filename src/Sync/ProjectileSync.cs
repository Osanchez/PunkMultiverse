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

        public static void Reset()
        {
            VisualProjectiles.Clear();
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
                int holder = FindLocalHolder(__instance);
                if (holder < 0) return; // not the local player's ship weapon (enemy, active, consumable)
                try
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
            private static bool Prefix(object __0)
            {
                if (!NetSession.Active) return true;
                return !IsVisual(__0);
            }
        }
    }
}
