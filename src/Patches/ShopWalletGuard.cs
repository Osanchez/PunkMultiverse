using System;
using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// "Everything in the shop is red and I have the money" (field report 2026-08-08).
    ///
    /// A price is coloured by <c>PriceWidget.Setup(price, unit, vault)</c>, and a resource price
    /// asks that <c>Unit</c> what it holds:
    ///
    /// <code>
    /// public float GetResource(Resource resource)
    /// {
    ///     if (!HasTank(resource)) return 0f;   // no tank = broke, silently
    ///     return GetTank(resource).Value;
    /// }
    /// </code>
    ///
    /// The party's shared tanks are attached to a ship in exactly ONE place in vanilla —
    /// <c>ShipManager.Spawn</c> — because vanilla only ever creates ships there. A net run creates
    /// them elsewhere too (puppets, rejoin reclaim), and a ship that missed that wiring reads zero
    /// for every shared resource: every price red, every purchase refused, and not one exception
    /// in the log to say why.
    ///
    /// So at the moment the shop opens: say out loud which ship it opened for, and give that ship
    /// any shared tank it is missing. The log line is the point as much as the repair — it is the
    /// difference between "the shop is broken" and "the shop is asking the wrong wallet".
    /// </summary>
    internal static class ShopWalletGuard
    {
        /// <summary>Ships already reported on, so the line is one per ship per run, not per frame.</summary>
        private static readonly HashSet<int> Reported = new HashSet<int>();

        internal static void Reset() => Reported.Clear();

        [HarmonyPatch(typeof(Shop), "StartShopping")]
        internal static class WalletBelongsToTheShopper
        {
            private static void Prefix(ref Ship __0)
            {
                if (!NetSession.Active) return;
                try
                {
                    var ship = __0;
                    if (ship == null) return;

                    // A puppet is another player's body: its Unit is a replica, its wallet is not
                    // ours to spend. Vanilla can only reach here through a local Interactor, so
                    // this should be unreachable — if it ever fires, the log says so and the shop
                    // still opens for the right ship instead of a silently unaffordable one.
                    if (ship.GetComponent<RemotePuppet>() != null)
                    {
                        var local = ShipSync.LocalShip;
                        Plugin.Log.LogWarning("[Shop] opened by a PUPPET ship — " +
                            (local != null ? "retargeted to the local ship" : "no local ship to retarget to"));
                        if (local != null) { __0 = local; ship = local; }
                    }

                    EnsureSharedTanks(ship);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Shop] wallet guard failed: {e.Message}"); }
            }
        }

        private static void EnsureSharedTanks(Ship ship)
        {
            var unit = ship.Unit;
            if (unit == null) return;
            var runData = ServiceLocator.Get<RunData>();
            if (runData == null) return;

            int missing = 0, present = 0;
            foreach (var tank in runData.SharedResourceTanks)
            {
                if (tank == null || tank.resource == null) continue;
                if (unit.HasTank(tank.resource)) { present++; continue; }
                unit.ComponentData.AddSharedTank(tank);   // the same call ShipManager.Spawn makes
                missing++;
            }

            int key = ship.GetInstanceID();
            if (missing > 0)
            {
                Plugin.Log.LogWarning($"[Shop] ship was missing {missing} shared tank(s) — attached before opening " +
                                      $"(had {present}). Prices were reading zero for those resources.");
                Reported.Add(key);
                return;
            }
            if (Reported.Add(key))
                Plugin.Log.LogInfo($"[Shop] opened for the local ship, {present} shared tank(s) present");
        }
    }
}
