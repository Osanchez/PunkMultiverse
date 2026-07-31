using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// THE INFINITE ITEM FAUCET, root-caused for real this time (Omar, 2026-07-30: "I just had the
    /// same infinite pickup error occur for a matchbox drop"). His Player.log names it exactly —
    /// 1035 consecutive copies of one stack:
    ///
    ///     ArgumentOutOfRangeException: Index was out of range
    ///       at ConsumableWheel.OnConsumableAmountChanged (Int32 index)
    ///       at Vault.Add (Consumable consumable, Int32 amount)
    ///       at ConsumablePickup.OnPickedUp (Ship ship)
    ///       at InteractiblePickup`1.Update ()
    ///
    /// Look at what vanilla's grant actually is, in BOTH pickup shapes:
    ///
    ///     Pickup.GetPickedUp:            OnPickedUp(unit);  Object.Destroy(gameObject);
    ///     InteractiblePickup&lt;T&gt;.Update:  PlaySfx(); OnPickedUp(ship);  Object.Destroy(gameObject);
    ///
    /// The destroy is the LAST statement. Anything that throws before it — a UI listener on the
    /// vault event, a missing sfx, a null tank — skips the destroy while the grant has already
    /// landed. Unity logs the exception and calls Update (or FixedUpdate) again next frame; the
    /// ship is still within pickup distance, so the whole thing repeats at frame rate. One dropped
    /// matchbox becomes an item faucet that only stops when the player leaves the match.
    ///
    /// That is a property of the SHAPE, not of any one item: it applies identically to coins,
    /// ingredients, consumables and modules, and to every drop source (enemy deaths, containers,
    /// terrain cells, plant fruits, shop spawns, care packages). So the guard is placed on the
    /// shape. A pickup may never outlive its own grant: if the grant throws, we log it once with
    /// the full exception and destroy the pickup ourselves, then swallow. The worst case becomes
    /// "one item was granted and its pickup vanished", which is what the player already saw
    /// happen — instead of an unbounded stream of them.
    ///
    /// This is a net, not a cure. The specific throw behind Omar's report is fixed at source in
    /// <see cref="ConsumableWheelHeal"/>; this exists so the NEXT unlucky listener is a logged
    /// one-item glitch rather than another field report.
    /// </summary>
    internal static class PickupGrantGuard
    {
        // One line per distinct failure, not one per frame: the failure mode this exists to stop
        // is a per-frame loop, and the log is the evidence trail for the next root-cause pass.
        private static readonly HashSet<string> Reported = new HashSet<string>();

        internal static void Reset() => Reported.Clear();

        private static void Escaped(Component pickup, string site, Exception e)
        {
            string what = pickup != null ? pickup.GetType().Name : "<destroyed>";
            string key = site + "|" + what + "|" + (e != null ? e.GetType().Name + ":" + e.Message : "?");
            if (Reported.Add(key))
            {
                Plugin.Log.LogError(
                    $"[Pickup] {what} threw out of {site} BEFORE its own Destroy — the pickup would " +
                    $"have re-granted every frame (the infinite-item faucet). Destroyed it instead. " +
                    $"Fix the thrower:\n{e}");
            }
            if (pickup != null && pickup.gameObject != null)
                UnityEngine.Object.Destroy(pickup.gameObject);
        }

        /// <summary>Magnet pickups: coins/resources. <c>GetPickedUp</c> is the whole grant.</summary>
        [HarmonyPatch(typeof(Pickup), nameof(Pickup.GetPickedUp))]
        internal static class GuardMagnetGrant
        {
            private static Exception Finalizer(Pickup __instance, Exception __exception)
            {
                if (__exception == null) return null;
                Escaped(__instance, "Pickup.GetPickedUp", __exception);
                return null; // swallowed: the object is gone, so there is nothing left to retry
            }
        }

        /// <summary>Interact-to-collect pickups (ingredient / consumable / module). Their grant
        /// lives at the tail of <c>InteractiblePickup&lt;T&gt;.Update</c>, a CLOSED GENERIC method —
        /// same patching corner as <see cref="Modes.BattleRoyaleLoot.ApplyGenericPatches"/>, so it
        /// is applied by hand for the same reason: a throw inside PatchAll takes the whole mod down
        /// at load, and the worst case here is one pickup type left unguarded, logged.</summary>
        internal static void ApplyGenericPatches(Harmony harmony)
        {
            var finalizer = new HarmonyMethod(
                AccessTools.Method(typeof(PickupGrantGuard), nameof(InteractGrantFinalizer)));
            foreach (var name in new[] { "IngredientPickup", "ConsumablePickup", "ModulePickup" })
            {
                try
                {
                    var t = AccessTools.TypeByName(name);
                    var m = t != null ? AccessTools.Method(t, "Update") : null;
                    if (m == null)
                    {
                        Plugin.Log.LogWarning($"[Pickup] no {name}.Update to guard — a throwing grant " +
                            "on that type can still become an infinite pickup");
                        continue;
                    }
                    harmony.Patch(m, finalizer: finalizer);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Pickup] could not guard {name}: {e.Message} — a throwing " +
                        "grant on that type can still become an infinite pickup");
                }
            }
        }

        private static Exception InteractGrantFinalizer(object __instance, Exception __exception)
        {
            if (__exception == null) return null;
            Escaped(__instance as Component, "InteractiblePickup.Update", __exception);
            return null;
        }
    }
}
