using System;
using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using UnityEngine.InputSystem;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// <c>ShipMenuToggler</c> — the map/grid/consumables screen, and the station shop — was written
    /// for exactly one <c>Ship</c> carrying exactly one <c>PlayerInput</c>. A net run has a puppet
    /// Ship per remote player, each with its own (neutered) PlayerInput, and every place the screen
    /// says "the player" turns into a lottery.
    ///
    /// The guards here do not reimplement any of it. They normalise the state vanilla already
    /// keeps, so vanilla's own checks start passing instead of being bypassed.
    ///
    /// Harmony parameters are injected BY INDEX (<c>__0</c>, <c>__1</c>, …) rather than by name:
    /// a name that stops matching after a game update throws at patch time and takes the whole
    /// mod's boot with it, and this file is not worth that risk.
    /// </summary>
    internal static class ShipMenuGuards
    {
        /// <summary>Null when there is no local ship yet — loading, dead, or a coordinator peer.</summary>
        private static PlayerInput LocalPlayerInput()
        {
            var ship = ShipSync.LocalShip;
            if (ship == null || ship.shipInput == null) return null;
            return ship.shipInput.PlayerInput;
        }

        /// <summary>
        /// Vanilla fills <c>playerInputs</c> once, at GameStarted, from <c>gameController.Ships</c> —
        /// which in a net run also holds this client's puppets of everyone else (ShipSync appends
        /// them to ShipManager.ships). Every consumer of that list is wrong with a puppet in it:
        /// <c>OnActionTriggered</c> resolves the acting player out of it, and Open/Close switch
        /// action maps across all of it.
        ///
        /// Pin it to the local ship. Idempotent, and re-run whenever the screen opens, because the
        /// local ship is not immortal — a rejoin or a station respawn hands us a new one, and a
        /// list still pointing at the old object breaks the menu exactly like a puppet does.
        /// </summary>
        private static void EnsurePinned(ShipMenuToggler toggler)
        {
            var local = LocalPlayerInput();
            if (toggler == null || local == null) return;
            var list = Traverse.Create(toggler).Field("playerInputs").GetValue<List<PlayerInput>>();
            if (list == null) return;
            if (list.Count == 1 && list[0] == local) return;   // already pinned

            // Vanilla subscribed OnActionTriggered on every ship it found. Drop the ones we are
            // removing: a fresh delegate over the same target+method compares equal, so -= finds it.
            Action<InputAction.CallbackContext> handler = null;
            try
            {
                handler = (Action<InputAction.CallbackContext>)AccessTools
                    .Method(typeof(ShipMenuToggler), "OnActionTriggered")
                    .CreateDelegate(typeof(Action<InputAction.CallbackContext>), toggler);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ShipMenu] could not build the unsubscribe delegate: {e.Message}");
            }

            int dropped = 0;
            foreach (var input in list)
            {
                if (input == null || input == local) continue;
                if (handler != null) input.onActionTriggered -= handler;
                dropped++;
            }
            bool hadLocal = list.Contains(local);
            list.Clear();
            list.Add(local);
            if (!hadLocal) local.onActionTriggered += handler;   // a ship that arrived after GameStarted
            Plugin.Log.LogInfo($"[ShipMenu] input list pinned to the local ship " +
                               $"(dropped {dropped}, local was {(hadLocal ? "present" : "MISSING")})");
        }

        [HarmonyPatch(typeof(ShipMenuToggler), "OnGameStarted")]
        internal static class PinInputListAtStart
        {
            private static void Postfix(ShipMenuToggler __instance)
            {
                if (!NetSession.Active) return;
                try { EnsurePinned(__instance); }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShipMenu] pin at start failed: {e.Message}"); }
            }
        }

        /// <summary>
        /// The two ways the screen opens disagree about who owns it. <c>OpenShop</c> passes the
        /// INTERACTING ship's PlayerInput; the Tab path passes whatever <c>OnActionTriggered</c>
        /// resolved out of the (puppet-polluted) list. Every later keypress is checked against
        /// <c>playerInputInControl</c> — so when the two disagree, close, back, tab switching and
        /// the active tab's own input are all dropped, and the menu cannot be left at all.
        ///
        /// Name the local player in both paths and the disagreement cannot exist.
        /// </summary>
        [HarmonyPatch(typeof(ShipMenuToggler), "Open")]
        internal static class OwnerIsAlwaysLocal
        {
            private static void Prefix(ShipMenuToggler __instance, ref PlayerInput __0)
            {
                if (!NetSession.Active) return;
                try
                {
                    EnsurePinned(__instance);
                    var local = LocalPlayerInput();
                    if (local == null || __0 == local) return;
                    Plugin.Log.LogInfo("[ShipMenu] open arrived with a non-local PlayerInput — retargeted to the local ship");
                    __0 = local;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShipMenu] owner retarget failed: {e.Message}"); }
            }
        }

        /// <summary>
        /// <c>Open()</c> has no <c>isOpen</c> guard, so re-entering it while open re-pauses, re-points
        /// the owner and re-runs the action-map switch. Vanilla gets away with it because the ship
        /// map is OFF while the screen is up, which makes a second open impossible — a protection
        /// this mod deliberately removes so a co-op player is not helpless in a live world.
        ///
        /// At a station that turns the exit press into a re-entry: Close() hands control back, the
        /// same press reaches Interactor -> Station.OnUseActivated -> Shop.StartShopping, and the
        /// shop is open again. This is the same defect already fixed one screen over, for the pause
        /// overlay (GuardPatches.NetRunPauseButtons).
        ///
        /// Re-entry may still change the TAB — that is how the game moves between map and grid —
        /// but it must not re-run the input contract.
        /// </summary>
        [HarmonyPatch(typeof(ShipMenuToggler), "Open")]
        internal static class NoReentrantOpen
        {
            private static bool Prefix(ShipMenuToggler __instance, int __1, Station __2)
            {
                if (!NetSession.Active) return true;
                try
                {
                    var view = Traverse.Create(__instance);
                    if (!view.Field("isOpen").GetValue<bool>()) return true;

                    int current = view.Field("currentTabIndex").GetValue<int>();
                    if (current != __1)
                    {
                        view.Field("currentStation").SetValue(__2);
                        __instance.ShowTab(__1);
                        Plugin.Log.LogDebug($"[ShipMenu] re-entrant open -> tab switch only ({current} -> {__1})");
                    }
                    else
                    {
                        Plugin.Log.LogDebug("[ShipMenu] suppressed redundant open");
                    }
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[ShipMenu] re-entrancy guard failed, letting vanilla run: {e.Message}");
                    return true;
                }
            }
        }

        // ---------------------------------------------------------------- leaving stays left

        /// <summary>Unscaled time of the last ship-menu close; -99 = never.</summary>
        private static float _lastCloseAt = -99f;

        /// <summary>How long a station stays deaf to "open the shop" after the menu closed. Long
        /// enough to outlive the press that closed it, short enough that a player who deliberately
        /// re-opens never notices.</summary>
        private const float ReopenBlockSeconds = 0.35f;

        internal static void Reset() => _lastCloseAt = -99f;

        [HarmonyPatch(typeof(ShipMenuToggler), "Close")]
        internal static class StampClose
        {
            private static void Postfix()
            {
                if (NetSession.Active) _lastCloseAt = UnityEngine.Time.unscaledTime;
            }
        }

        /// <summary>
        /// The other half of the re-entry race, at its source. The interact action lives on the
        /// ship map, which this mod keeps alive in a net run, so the press that closes the shop is
        /// still a live "use the station" the moment control comes back.
        ///
        /// Only SHOP opens are refused. A locked station's use press buys the unlock — it opens no
        /// menu, so it cannot be part of this race, and swallowing it would cost the player an
        /// interaction for nothing.
        ///
        /// Vanilla has the same idea in the same place: <c>Ship.LastTimeExitShipMenu</c> plus
        /// <c>ModuleActivator.minDelayAfterLeavingShipMenu</c> exist so leaving a menu does not
        /// immediately fire an ability.
        /// </summary>
        [HarmonyPatch(typeof(Station), "OnUseActivated")]
        internal static class NoInstantReopen
        {
            private static bool Prefix(Station __instance)
            {
                if (!NetSession.Active) return true;
                try
                {
                    if (__instance == null || __instance.ComponentData == null) return true;
                    if (!__instance.ComponentData.IsUnlocked) return true;   // unlock press, not a shop open
                    if (UnityEngine.Time.unscaledTime - _lastCloseAt >= ReopenBlockSeconds) return true;
                    Plugin.Log.LogDebug("[ShipMenu] station use suppressed — the ship menu just closed");
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[ShipMenu] re-open guard failed, letting vanilla run: {e.Message}");
                    return true;
                }
            }
        }

        /// <summary>
        /// The item wheel picks the wrong ship, and the player watches their fuel generator land
        /// under their teammate (field report 2026-08-09).
        ///
        /// <code>
        /// for (int j = 0; j &lt; shipManager.Ships.Count; j++)
        ///     shipManager.Ships[j].shipInput.ShipControlActionMap.OpenItemWheel += OpenWheel;
        ///
        /// private void OpenWheel(ShipInput shipInput) { activeShipInput = shipInput; ... }
        /// consumable.Use(shipManager.Ships.FirstOrDefault(s =&gt; s.shipInput == activeShipInput));
        /// // SpawnPrefabConsumable.Use: Instantiate(prefab, ship.transform.position, ...)
        /// </code>
        ///
        /// Every ship in the game is subscribed — puppets included — and the wheel keeps whichever
        /// ShipInput fired LAST. Puppets sit after the local ship in <c>ShipManager.Ships</c>, so
        /// the teammate wins every time, deterministically: the consumable spawns at their feet.
        ///
        /// Same family as the ship menu above, same fix: name the local player. Rewriting the
        /// argument here also corrects everything derived from it — which ship's item-wheel map is
        /// enabled, whose stick the selection is read from, and where the thing is placed.
        /// </summary>
        [HarmonyPatch(typeof(ConsumableWheel), "OpenWheel")]
        internal static class WheelBelongsToTheLocalPlayer
        {
            private static void Prefix(ref ShipInput __0)
            {
                if (!NetSession.Active) return;
                try
                {
                    var ship = ShipSync.LocalShip;
                    var local = ship != null ? ship.shipInput : null;
                    if (local == null || __0 == local) return;
                    Plugin.Log.LogInfo("[Wheel] opened by a non-local ShipInput (a puppet's subscription " +
                                       "fired) — retargeted to the local ship, so consumables land under YOU");
                    __0 = local;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Wheel] retarget failed: {e.Message}"); }
            }
        }

        /// <summary>
        /// <c>Open()</c> calls <c>ShowTab</c> BEFORE it switches action maps and disables ship
        /// control. A tab that throws therefore takes the input contract with it: <c>isOpen</c>
        /// stays true, the canvas stays up, the ship map stays live and the shop map is never
        /// registered — which is the field report, symptom for symptom.
        ///
        /// The tab has plenty to throw over in a net run: <c>ModuleGridScreen.OnOpened</c> touches
        /// the Ship, the Station, the Shop and every ShipHud in the scene, and a puppet supplies
        /// none of those the way a local ship does.
        ///
        /// Contain it, and let Open finish. A broken tab is a broken tab; a broken tab that also
        /// strands the player in an unleavable screen is a lost run. LogError, not LogDebug: this
        /// line is the only trace left of a swallowed exception.
        /// </summary>
        [HarmonyPatch(typeof(ShipMenuToggler), "ShowTab")]
        internal static class ContainThrowingTab
        {
            private static Exception Finalizer(Exception __exception, int __0)
            {
                if (__exception == null) return null;
                if (!NetSession.Active) return __exception;   // single-player keeps vanilla behaviour
                Plugin.Log.LogError($"[ShipMenu] tab {__0} threw — contained so the screen stays closable: {__exception}");
                return null;
            }
        }
    }
}
