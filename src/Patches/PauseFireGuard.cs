using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Clicking a button on the co-op pause overlay also pulls the trigger — reported 2026-08-07:
    /// "to leave the menu I have to press CONTINUE, and the click fires my rockets into myself".
    ///
    /// Both halves of the cause are ours, and both are deliberate:
    ///   * <see cref="PausePolicy"/> suppresses the world-freeze, because one player's menu must
    ///     not stop everyone's game.
    ///   * <c>GuardPatches.NetRunPauseButtons.KeepShipControllableWhilePaused</c> re-enables the
    ///     local <c>ShipControlActionMap</c> that <c>UIScreen.Open</c> had switched off, because a
    ///     live world plus dead controls and no crosshair is worse than either.
    /// The consequence nobody spelled out: the UI map and ShipControl are now enabled *at the same
    /// time*, and LMB is bound in both — as <c>UI/Click</c> and as primary fire. One press walks
    /// both paths, so <c>ShipInput.HandleAction</c> reaches <c>Shooter.SetShooting(true)</c> on the
    /// very click that presses the button.
    ///
    /// It lands on the player because aim is not gated by the action map at all: <c>ShipInput.
    /// Update</c> feeds <c>Aimer</c> the cursor's world position every frame regardless. The cursor
    /// is on a menu button in the middle of the screen — i.e. roughly on top of the ship — so a
    /// rocket spawns at zero range and detonates on its owner. Vanilla never sees any of this:
    /// solo pause freezes time AND drops the ship map.
    ///
    /// Fix: while the overlay is up, hold the two input-driven shooters through the game's own
    /// blocker API (the one <c>ShooterBlocker</c> uses) — <c>Shooter.Update</c> then returns before
    /// warmup, sound and <c>Shoot()</c>. Flying, dodging and the crosshair are untouched, which was
    /// the point of keeping the map enabled. Only the local player's primary/secondary shooters are
    /// held: enemies, other players' puppets and this ship's own auto-turret modules keep firing,
    /// because the overlay is a local screen and must not change the shared simulation.
    /// </summary>
    internal static class PauseFireGuard
    {
        // Resolved through AccessTools rather than Traverse so gamescan records them in the
        // dependency contract — a game update that renames either field then shows up as a named
        // GameGuard warning instead of this quietly reverting to the bug.
        private static readonly FieldInfo PrimaryShooter = AccessTools.Field(typeof(ShipInput), "primaryShooter");
        private static readonly FieldInfo SecondaryShooter = AccessTools.Field(typeof(ShipInput), "secondaryShooter");
        private static readonly FieldInfo PauseIsOpen = AccessTools.Field(typeof(PauseScreen), "isOpen");

        /// <summary>Identity token for Shooter.Block/Unblock — the game keys blockers by
        /// reference, so one shared instance is exactly one block.</summary>
        private static readonly object Blocker = new object();

        /// <summary>Shooters currently held, or null. Never spans a run: a scene reload destroys
        /// them, and Hold() releases whatever it is holding before taking a new set.</summary>
        private static List<Shooter> held;

        private static void Hold()
        {
            Release();   // never stack a second block on a stale set
            var ship = ShipSync.LocalShip;
            if (ship == null) return;
            var list = new List<Shooter>(2);
            foreach (var shipInput in ship.GetComponentsInChildren<ShipInput>(true))
            {
                // The two shooters LMB/RMB drive. Read off ShipInput rather than collecting every
                // Shooter on the ship, which would silence auto-turret modules as well.
                foreach (var field in new[] { PrimaryShooter, SecondaryShooter })
                {
                    var shooter = field?.GetValue(shipInput) as Shooter;
                    if (shooter == null) continue;
                    shooter.Block(Blocker);
                    // The press that opened the menu can leave the trigger latched on; the block
                    // stops the shots, this stops the warmup/continuous-fire sound hanging on.
                    shooter.SetShooting(false);
                    list.Add(shooter);
                }
            }
            if (list.Count == 0) return;
            held = list;
            Plugin.Log.LogDebug($"[Pause] local fire held for the overlay ({list.Count} shooter(s))");
        }

        private static void Release()
        {
            if (held == null) return;
            foreach (var shooter in held)
            {
                if (shooter == null) continue;   // destroyed with the scene
                shooter.Unblock(Blocker);
                // Clear the latch too: lifting the block while the trigger still reads "pulled"
                // would fire the shot the block just prevented.
                shooter.SetShooting(false);
            }
            held = null;
        }

        [HarmonyPatch(typeof(PauseScreen), "Open")]
        internal static class HoldFireWhileOverlayOpen
        {
            private static void Postfix(PauseScreen __instance)
            {
                if (!NetSession.Active) return;   // solo pause is frozen and unchanged
                // NetRunPauseButtons' prefix skips the open body for a redundant re-open or one
                // stacked on the item wheel, and a postfix runs either way — isOpen is what the
                // real body sets, so it is the only honest "did this actually open" signal here.
                // If the field ever goes missing, hold anyway: Close always releases, so the cost
                // of being wrong is a brief block, not a ship that cannot shoot.
                if (PauseIsOpen != null && !(bool)PauseIsOpen.GetValue(__instance)) return;
                Hold();
            }
        }

        [HarmonyPatch(typeof(PauseScreen), "Close")]
        internal static class ReleaseFireOnClose
        {
            // Unconditional on purpose: every exit from the overlay (CONTINUE, the back action,
            // RESTART, EXIT) routes through Close, and releasing a block we never took is a no-op.
            private static void Postfix() => Release();
        }
    }
}
