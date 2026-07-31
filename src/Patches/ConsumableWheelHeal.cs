using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// The throw behind the infinite matchbox, fixed at source.
    ///
    /// Vanilla builds the item wheel's slots exactly once, from a single event:
    ///
    ///     ConsumableWheel.OnEnable   -> GameController.GameStarted += OnGameStarted
    ///                                -> vault.ConsumableAmountChanged += OnConsumableAmountChanged
    ///     ConsumableWheel.OnGameStarted -> builds `items` (one per vault slot)
    ///                                   -> wires OpenItemWheel on every ship's input
    ///     ConsumableWheel.OnConsumableAmountChanged(i) -> items[i].Show(...)
    ///
    /// Both subscriptions happen in the SAME OnEnable, but only one of them can be missed. A wheel
    /// that becomes active AFTER <c>GameStarted</c> has already fired never receives it — so
    /// `items` stays EMPTY forever — while it is perfectly subscribed to the vault, so the first
    /// consumable a player picks up indexes an empty list and throws. Vanilla never re-checks, and
    /// the index is always in range for the VAULT (8 fixed slots), so the only way this throws is
    /// the wheel having fewer slots than the vault: proof the build never ran.
    ///
    /// Two consequences, both field-reported as one bug:
    ///   1. The throw lands between the grant and the pickup's own Destroy, which is what turned
    ///      one dropped consumable into an endless stream of them (see PickupGrantGuard).
    ///   2. `OnGameStarted` is also where the wheel wires itself to the OPEN button — so a wheel
    ///      that missed it cannot be opened at all. Every consumable collected in such a run is
    ///      unusable. The heal below fixes that too.
    ///
    /// Battle Royale is where this bites: the mod holds vanilla's StartGame until GO_LIVE and the
    /// drop screen owns the first seconds of the run, so "the HUD was not up yet when the game
    /// started" is a routine ordering there rather than an impossible one.
    ///
    /// The repair uses the game's OWN builder rather than reconstructing slots by hand, and only
    /// at the moment vanilla would otherwise throw — never pre-emptively, so it can never race
    /// vanilla's build and leave a double-length wheel. Every firing logs the evidence.
    /// </summary>
    internal static class ConsumableWheelHeal
    {
        private static readonly HashSet<int> Healed = new HashSet<int>();
        private static bool _reportedGiveUp;

        internal static void Reset()
        {
            Healed.Clear();
            _reportedGiveUp = false;
        }

        private static List<ConsumableWheelItem> ItemsOf(ConsumableWheel wheel)
        {
            try
            {
                return AccessTools.Field(typeof(ConsumableWheel), "items")?.GetValue(wheel)
                    as List<ConsumableWheelItem>;
            }
            catch { return null; }
        }

        /// <summary>Run the wheel's own <c>OnGameStarted</c> once, if and only if it plainly never
        /// ran (no slots at all) and the game really is live. Returns true if slots exist after.</summary>
        private static bool TryBuild(ConsumableWheel wheel, int index)
        {
            var items = ItemsOf(wheel);
            if (items == null) return false;
            if (items.Count > 0) return index >= 0 && index < items.Count; // built already; not ours to touch

            int id = wheel.GetInstanceID();
            if (!Healed.Add(id)) return false; // one attempt per wheel — never loop on a broken build

            bool started = false;
            try
            {
                var gc = UnityEngine.Object.FindFirstObjectByType<GameController>();
                started = gc != null && gc.IsGameStarted;
            }
            catch { }
            if (!started)
            {
                Plugin.Log.LogWarning("[Wheel] item wheel has no slots and the game is not started — " +
                    "leaving it alone; the consumable change was dropped rather than thrown.");
                return false;
            }

            try
            {
                AccessTools.Method(typeof(ConsumableWheel), "OnGameStarted")?.Invoke(wheel, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Wheel] rebuild failed: {e.Message}");
                return false;
            }

            items = ItemsOf(wheel);
            int built = items != null ? items.Count : 0;
            Plugin.Log.LogWarning($"[Wheel] item wheel had 0 slots when a consumable arrived — rebuilt " +
                $"{built}. It missed GameController.GameStarted (active too late), which also left the " +
                "OPEN button unwired: consumables were being collected and could not be used. If this " +
                "line appears every match, the HUD is being enabled after StartGame and that ordering " +
                "is the bug to fix.");
            return built > 0 && index >= 0 && index < built;
        }

        /// <summary>The exact frame vanilla would throw. Heal, or skip — never throw, because this
        /// call sits between a pickup's grant and its Destroy (Patches/PickupGrantGuard.cs).</summary>
        [HarmonyPatch(typeof(ConsumableWheel), "OnConsumableAmountChanged")]
        internal static class GuardAmountChanged
        {
            private static bool Prefix(ConsumableWheel __instance, int __0)
            {
                var items = ItemsOf(__instance);
                if (items != null && __0 >= 0 && __0 < items.Count) return true; // healthy: vanilla runs
                if (TryBuild(__instance, __0)) return true;                      // healed: vanilla runs
                if (!_reportedGiveUp)
                {
                    _reportedGiveUp = true;
                    Plugin.Log.LogWarning($"[Wheel] consumable slot {__0} has no wheel item " +
                        $"({(items == null ? "no items list" : items.Count + " slots")}) — skipping the " +
                        "visual update. The item is still in the vault; this only means the wheel " +
                        "cannot draw it.");
                }
                return false;
            }
        }

        /// <summary>Same shape, same consequence: the shop's consumable screen indexes its own
        /// wheel-item list from the same vault event. It builds its list on open, so a change that
        /// arrives before the first open has nothing to draw — skip rather than throw.</summary>
        [HarmonyPatch(typeof(ConsumablesScreen), "OnConsumableAmountChanged")]
        internal static class GuardShopScreen
        {
            private static bool _reported;

            private static bool Prefix(ConsumablesScreen __instance, int __0)
            {
                List<ConsumableWheelItem> items = null;
                try
                {
                    items = AccessTools.Field(typeof(ConsumablesScreen), "wheelItems")?.GetValue(__instance)
                        as List<ConsumableWheelItem>;
                }
                catch { }
                if (items != null && __0 >= 0 && __0 < items.Count) return true;
                if (!_reported)
                {
                    _reported = true;
                    Plugin.Log.LogWarning($"[Wheel] shop consumable screen has no widget for slot {__0} " +
                        $"({(items == null ? "no list" : items.Count + " widgets")}) — skipping its " +
                        "visual update rather than throwing into a pickup grant.");
                }
                return false;
            }
        }
    }
}
