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
            /// <summary>Which tab the screen is showing; -1 while closed. The shop's action map
            /// only exists while the GRID tab is up, so the map tab at a station is not a fault.</summary>
            internal int TabIndex;
            internal int GridTabIndex;
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
            snap.TabIndex = view.Field("currentTabIndex").GetValue<int>();
            snap.GridTabIndex = view.Field("tabIndexGrid").GetValue<int>();
            // Unity's fake-null: a destroyed station must read as "not at a station".
            var station = view.Field("currentStation").GetValue<Station>();
            snap.AtStation = station != null;
            return snap;
        }

        /// <summary>
        /// The contract, as four invariants. Returns "" when they all hold, otherwise the first
        /// break, formatted for the log and for `menustate`.
        ///
        /// The PAUSE overlay is deliberately excluded: in a net run it keeps the ship controllable
        /// in a live world on purpose (GuardPatches.KeepShipControllableWhilePaused), so its action
        /// maps legitimately contradict everything below. Same for the item wheel, which owns the
        /// maps through its own raw Enable/Disable.
        /// </summary>
        internal static string Evaluate(Snapshot snap)
        {
            if (!snap.HasToggler || snap.LocalInput == null) return "";
            if (Patches.MenuMutex.PauseOpen || Patches.MenuMutex.WheelOpen) return "";

            if (snap.Open)
            {
                // I1 — the screen must be owned by the player whose keypresses reach it. When it is
                // not, ShipMenuToggler.OnActionTriggered drops close, back, tab switching and the
                // active tab's own input, and the menu becomes unleavable.
                if (!snap.OwnerIsLocal)
                    return $"I1 owner-not-local: owner={(snap.Owner == null ? "null" : "other")} tab={snap.TabIndex}";

                // I2 — an open ship menu means the menu action map. Still on ShipControl means
                // Open() never reached its input switch.
                if (snap.MapName != "MapControl")
                    return $"I2 map-not-menu: map={snap.MapName} tab={snap.TabIndex}";

                // I3 — the grid tab at a station is the shop; its own action map carries selection
                // and purchase. Without it the shop is visible and inert.
                if (snap.AtStation && snap.TabIndex == snap.GridTabIndex && !snap.ShopMapEnabled)
                    return $"I3 shop-map-off: tab={snap.TabIndex} station=True";

                return "";
            }

            // I4 — no menu, but still on the menu map: the close path ran without switching back.
            // Diagnostic only; there is no menu to close, so the backstop must not touch it.
            if (snap.MapName == "MapControl")
                return $"I4 stranded-on-menu-map: shipcontrol={snap.ShipControlEnabled}";

            return "";
        }

        /// <summary>Harness-parsable one-liner. Keep the key=value shape — scenarios grep it.</summary>
        internal static string Describe(Snapshot snap)
        {
            if (!snap.HasToggler) return "menustate: no ShipMenuToggler";
            string owner = snap.Owner == null ? "null" : (snap.OwnerIsLocal ? "local" : "other");
            string live = Evaluate(snap);
            return $"menustate: open={snap.Open} owner={owner} map={snap.MapName} " +
                   $"shopmap={snap.ShopMapEnabled} shipcontrol={snap.ShipControlEnabled} station={snap.AtStation} " +
                   $"tab={snap.TabIndex} violation={(live.Length == 0 ? "none" : live)}";
        }

        /// <summary>The violation currently being reported, "" while the table holds. Set only
        /// after the break survives the debounce, so a mid-animation frame never shows up here.</summary>
        internal static string LastViolation = "";

        internal static void Reset() => LastViolation = "";

        /// <summary>
        /// Watches the table every frame and reports the first break that SURVIVES DebounceFrames.
        /// The debounce is not politeness: vanilla is legitimately inconsistent for a few frames at
        /// a time — UIScreen.CloseCoroutine yields, AnimatedScreen animates, ShowTab closes one tab
        /// before opening the next — and a watcher without it would cry on every menu interaction.
        /// </summary>
        internal sealed class Ticker : UnityEngine.MonoBehaviour
        {
            private const int DebounceFrames = 30;   // ~0.5s at 60fps

            private string _pending = "";
            private int _pendingFrames;

            private void Update()
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame) { Clear(); return; }

                string violation = "";
                try { violation = Evaluate(Read()); }
                catch { return; }   // a half-built scene is not a fault worth reporting

                if (violation.Length == 0) { Clear(); return; }
                if (violation != _pending) { _pending = violation; _pendingFrames = 0; return; }

                _pendingFrames++;
                if (_pendingFrames != DebounceFrames) return;   // report once per episode
                LastViolation = violation;
                Plugin.Log.LogWarning($"[MenuState] broken {violation}");
            }

            private void Clear()
            {
                if (LastViolation.Length != 0) Plugin.Log.LogInfo("[MenuState] cleared");
                _pending = "";
                _pendingFrames = 0;
                LastViolation = "";
            }
        }
    }
}
