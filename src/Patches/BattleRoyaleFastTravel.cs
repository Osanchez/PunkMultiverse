using HarmonyLib;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Battle Royale turns station-to-station fast travel off.
    ///
    /// In the co-op game fast travel is a convenience: you have already cleared a station, so
    /// hopping back to it costs you nothing anyone else cares about. In a battle royale it is an
    /// escape hatch that beats the entire mode — the ring's whole job is to force players into the
    /// same shrinking space, and BR unlocks every station on the map at match start (so the ring is
    /// something you fight over, not unlock progression). Together those two facts would hand every
    /// player an instant, unlimited teleport out of any fight and out of the closing zone.
    ///
    /// Blocked in two places on purpose: the map's travel buttons never light up (so the option is
    /// not offered and then refused, which reads as a bug), and the click handler itself is a no-op
    /// in case any other path — a gamepad submit, a future UI — reaches it anyway.
    ///
    /// Deliberately NOT blocked: <c>FastTravelManager.TravelTo</c> and <c>TravelToEntity</c>. Those
    /// are shared plumbing — the free/debug camera teleport and entity-locator paths use them — and
    /// blanket-blocking them would break the debug menu during live testing without making BR any
    /// more honest. The mode only needs the station-to-station route closed.
    /// </summary>
    internal static class BattleRoyaleFastTravel
    {
        // Every call site funnels here (map opened near a station, and the two explicit
        // enable/disable calls around the shop menu), so one prefix covers them all.
        [HarmonyPatch(typeof(MapMover), "SetFastTravelEnabled")]
        internal static class NoTravelButtons
        {
            private static void Prefix(ref bool isEnabled)
            {
                if (Modes.BattleRoyale.Active) isEnabled = false;
            }
        }

        [HarmonyPatch(typeof(MapMover), "OnTravelButtonClicked")]
        internal static class NoTravelOnClick
        {
            private static bool Prefix() => !Modes.BattleRoyale.Active;
        }
    }
}
