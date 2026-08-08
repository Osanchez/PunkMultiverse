using HarmonyLib;
using PunkMultiverse.Sync;
using UnityEngine.InputSystem;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// One reader for "what does the ship menu think is going on", shared by the `menustate`
    /// devcmd and (from here on) anything that needs to grade the screen's state.
    ///
    /// It exists because the ship menu keeps its state in four places that must agree —
    /// <c>ShipMenuToggler.isOpen</c>, which <c>PlayerInput</c> it believes owns the screen, which
    /// action map that input is on, and whether the shop's own map is live. Vanilla keeps them in
    /// step by construction: one ship, one PlayerInput. A net run has a puppet Ship per remote
    /// player, each carrying its own PlayerInput, and the four can disagree — which is invisible
    /// from inside the game and reads to the player as "the shop won't close and won't sell".
    /// </summary>
    internal static class MenuStateWatch
    {
        /// <summary>Everything the invariant table is written against, read in one pass so two
        /// consumers can never disagree about the same frame.</summary>
        internal struct Snapshot
        {
            internal bool HasToggler;
            internal bool Open;
            internal bool AtStation;
            /// <summary>Null when there is no local ship yet (loading, dead, coordinator peer).</summary>
            internal PlayerInput LocalInput;
            internal PlayerInput Owner;
            internal string MapName;
            internal bool ShopMapEnabled;
            internal bool ShipControlEnabled;

            internal bool OwnerIsLocal => Owner != null && Owner == LocalInput;
        }

        internal static Snapshot Read()
        {
            var snap = new Snapshot { MapName = "none" };
            var ship = ShipSync.LocalShip;
            var shipInput = ship != null ? ship.shipInput : null;
            if (shipInput != null)
            {
                snap.LocalInput = shipInput.PlayerInput;
                var controlMap = shipInput.ShipControlActionMap;
                snap.ShipControlEnabled = controlMap != null && controlMap.Enabled;
            }
            if (snap.LocalInput != null)
            {
                var current = snap.LocalInput.currentActionMap;
                if (current != null) snap.MapName = current.name;
                var actions = snap.LocalInput.actions;
                // FindActionMap returns null rather than throwing when the asset has no such map.
                var shopMap = actions != null ? actions.FindActionMap("Shop") : null;
                snap.ShopMapEnabled = shopMap != null && shopMap.enabled;
            }

            var toggler = ServiceLocator.Get<ShipMenuToggler>();
            if (toggler == null) return snap;
            snap.HasToggler = true;
            var view = Traverse.Create(toggler);
            snap.Open = view.Field("isOpen").GetValue<bool>();
            snap.Owner = view.Field("playerInputInControl").GetValue<PlayerInput>();
            // Unity's fake-null: a destroyed station must read as "not at a station".
            var station = view.Field("currentStation").GetValue<Station>();
            snap.AtStation = station != null;
            return snap;
        }

        /// <summary>Harness-parsable one-liner. Keep the key=value shape — scenarios grep it.</summary>
        internal static string Describe(Snapshot snap)
        {
            if (!snap.HasToggler) return "menustate: no ShipMenuToggler";
            string owner = snap.Owner == null ? "null" : (snap.OwnerIsLocal ? "local" : "other");
            return $"menustate: open={snap.Open} owner={owner} map={snap.MapName} " +
                   $"shopmap={snap.ShopMapEnabled} shipcontrol={snap.ShipControlEnabled} station={snap.AtStation}";
        }
    }
}
