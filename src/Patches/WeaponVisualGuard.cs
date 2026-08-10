using System;
using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// The per-frame NullReferenceException storm out of <c>Shooter.Update</c> (field report
    /// 2026-08-08, hundreds of megabytes of Player.log in minutes):
    ///
    /// <code>
    /// Component.get_gameObject()
    /// HitscanWeaponVisual.set_WarmingUp(bool)
    /// HitscanWeapon.OnBarrelMoved(Vector2, Vector2)
    /// Shooter.Update()
    /// </code>
    ///
    /// Two vanilla halves make it. <c>HitscanWeapon.Dispose</c> destroys every visual's GameObject
    /// but never empties <c>visualsInstances</c>, and <c>OnBarrelMoved</c> writes straight into
    /// that list — so a weapon that was disposed while its Shooter still drives it pokes destroyed
    /// objects forever. <c>HitscanWeaponVisual.WarmingUp</c> then dereferences its sprite renderer
    /// with no null guard (its sibling <c>Firing</c> has one), and that is the throw.
    ///
    /// A weapon is disposed when the module grid is rebuilt — which is what buying or moving a
    /// weapon module in the shop does, and what applying another player's grid does in a net run.
    ///
    /// The fix is the pair: empty the list when the weapon is disposed, and refuse to drive
    /// visuals that are gone. Cosmetic loss at worst (no beam sprite for a weapon that should not
    /// be drawing one); the alternative is a storm that costs frames and gigabytes.
    /// </summary>
    internal static class WeaponVisualGuard
    {
        private static readonly HashSet<int> Reported = new HashSet<int>();

        internal static void Reset() => Reported.Clear();

        private static List<HitscanWeaponVisual> Visuals(HitscanWeapon weapon)
        {
            return Traverse.Create(weapon).Field("visualsInstances").GetValue<List<HitscanWeaponVisual>>();
        }

        /// <summary>Dispose destroys the visuals; make it forget them too, so nothing can index
        /// into a list of corpses afterwards.</summary>
        [HarmonyPatch(typeof(HitscanWeapon), "Dispose")]
        internal static class ForgetDisposedVisuals
        {
            private static void Postfix(HitscanWeapon __instance)
            {
                if (!NetSession.Active) return;
                try { Visuals(__instance)?.Clear(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Weapon] could not clear disposed visuals: {e.Message}"); }
            }
        }

        /// <summary>...and the other side of the same coin: a live Shooter must not drive visuals
        /// that no longer exist. Skipping is safe here — every branch of the original body only
        /// writes to those visuals.</summary>
        [HarmonyPatch(typeof(HitscanWeapon), "OnBarrelMoved")]
        internal static class SkipDeadVisuals
        {
            private static bool Prefix(HitscanWeapon __instance)
            {
                if (!NetSession.Active) return true;
                try
                {
                    var visuals = Visuals(__instance);
                    if (visuals == null) return true;

                    bool dead = visuals.Count == 0;
                    if (!dead)
                        foreach (var visual in visuals)
                            if (visual == null) { dead = true; break; }   // Unity fake-null: destroyed
                    if (!dead) return true;

                    int key = __instance.GetHashCode();
                    if (Reported.Add(key))
                        Plugin.Log.LogWarning("[Weapon] hitscan weapon is still being driven after its visuals " +
                                              "were destroyed (disposed weapon on a live Shooter) — skipping its visual update");
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Weapon] visual guard failed, letting vanilla run: {e.Message}");
                    return true;
                }
            }
        }
    }
}
