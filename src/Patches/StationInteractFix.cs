using HarmonyLib;
using PunkMultiverse.Core;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Fix: clients sometimes can't open a shop (tester report 2026-07-23).
    ///
    /// The game only manages a station's interaction collider (`Station.Interactable` =
    /// `interactionCollider.enabled`) inside `Station.Update`, and ONLY while the station is still
    /// locked — it tracks nearby enemies. The instant the station unlocks, `Update` stops touching
    /// the collider, freezing it at whatever value it held that frame; neither `OnUpgradeInstalled`
    /// nor `Bind` ever set it true for an unlocked station.
    ///
    /// On the host that's fine: unlock always happens via a local interaction, which is only
    /// possible when the collider was enabled. On a CLIENT the unlock arrives as a replicated event
    /// (ProgressionSync -> Data.Install), so the collider freezes at the client's momentary
    /// "enemy in range?" value — and because enemies near a client are lag-delayed puppets, a
    /// blocking enemy the host already cleared can still be showing there. When that race hits, the
    /// collider freezes DISABLED and the client's Interactor never registers the station, so Use
    /// does nothing and the shop never opens for the rest of the run. Intermittent by exactly the
    /// enemy-presence race. A station that unlocks while streamed out hits the same wall on
    /// stream-in (Bind never enables the collider for an already-unlocked station).
    ///
    /// Fix: an unlocked station is always meant to be interactable, so force the collider on
    /// whenever a station finishes unlocking (OnUpgradeInstalled) or streams in already unlocked
    /// (Bind). Net runs only — single-player never hits the race.
    /// </summary>
    internal static class StationInteractFix
    {
        private static void EnsureInteractable(Station station, Station.Data data)
        {
            if (!NetSession.Active) return;
            if (data == null || !data.IsUnlocked) return;
            try
            {
                if (!station.Interactable) station.Interactable = true;
            }
            catch { /* interactionCollider not wired on this station — nothing to do */ }
        }

        // Fires on the client when the replicated unlock is applied to a streamed-in station
        // (Data.Install -> UpgradeInstalled event -> OnUpgradeInstalled), and on the host's own
        // unlock. Re-enable the frozen collider.
        [HarmonyPatch(typeof(Station), "OnUpgradeInstalled")]
        internal static class ReenableColliderOnUnlock
        {
            private static void Postfix(Station __instance, Station.Data data) => EnsureInteractable(__instance, data);
        }

        // Fires when a station streams in. If it is already unlocked (unlocked earlier while this
        // client had it streamed out), enable the collider the game leaves at its prefab default.
        [HarmonyPatch(typeof(Station), "Bind")]
        internal static class ReenableColliderOnStreamIn
        {
            private static void Postfix(Station __instance, Station.Data data) => EnsureInteractable(__instance, data);
        }
    }
}
