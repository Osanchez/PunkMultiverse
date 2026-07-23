using HarmonyLib;
using PunkMultiverse.Core;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Tracks whether the co-op pause overlay and the item wheel are open, and enforces that they
    /// are never open at the same time — the fix for the "pause + item wheel = softlock" report
    /// (2026-07-23). In single-player, frozen time makes the two menus mutually exclusive for free;
    /// co-op keeps the world live (PausePolicy) so nothing serialized them, and their two separate
    /// input-ownership mechanisms corrupted each other's action-map state.
    ///
    /// See GuardPatches.NetRunPauseButtons for the pause side (it refuses to open over an open
    /// wheel, and refuses redundant re-opens). Here we refuse to open the wheel while paused, and
    /// keep the two flags current.
    /// </summary>
    internal static class MenuMutex
    {
        internal static bool PauseOpen;
        internal static bool WheelOpen;

        // Cleared on session end (DevTools.Reset) so a flag can't stick true across runs.
        internal static void Reset()
        {
            PauseOpen = false;
            WheelOpen = false;
        }
    }

    internal static class PauseWheelGuard
    {
        // Pause overlay closed -> clear the flag so the wheel works again.
        [HarmonyPatch(typeof(PauseScreen), "Close")]
        internal static class TrackPauseClosed
        {
            private static void Postfix() => MenuMutex.PauseOpen = false;
        }

        // Refuse to open the item wheel while the pause overlay is up. OpenWheel is the action
        // handler that sets activeShipInput and calls Open(); skipping it leaves no partial state.
        [HarmonyPatch(typeof(ConsumableWheel), "OpenWheel")]
        internal static class BlockWheelWhilePaused
        {
            private static bool Prefix()
            {
                if (!NetSession.Active) return true;
                if (MenuMutex.PauseOpen)
                {
                    Plugin.Log.LogDebug("[Pause] item wheel suppressed while pause overlay open (net run)");
                    return false;
                }
                return true;
            }
        }

        // The wheel actually opened (Open runs only when OpenWheel wasn't blocked) -> set the flag
        // so a pause press can't stack over it.
        [HarmonyPatch(typeof(ConsumableWheel), "Open")]
        internal static class TrackWheelOpen
        {
            private static void Postfix()
            {
                if (NetSession.Active) MenuMutex.WheelOpen = true;
            }
        }

        [HarmonyPatch(typeof(ConsumableWheel), "Close")]
        internal static class TrackWheelClosed
        {
            private static void Prefix() => MenuMutex.WheelOpen = false;
        }
    }
}
