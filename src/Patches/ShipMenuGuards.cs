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
    }
}
