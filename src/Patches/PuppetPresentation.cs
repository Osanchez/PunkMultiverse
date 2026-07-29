using HarmonyLib;
using PunkMultiverse.Sync;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Another player's ship must not narrate itself on my machine.
    ///
    /// The distinction this file draws: a remote ship's WORLD sounds are correct and deliberate —
    /// engines, boost, dashes, weapons all come from a position in the world, and RemotePuppet feeds
    /// them on purpose so other players feel present. A ship's COCKPIT alerts are the opposite: they
    /// are addressed to the pilot, they are not positional, and they carry no information the
    /// listener can act on.
    /// </summary>
    internal static class PuppetPresentation
    {
        /// <summary>
        /// Fuel warnings belong to the pilot only (Omar, 2026-07-29: "I'm hearing other players'
        /// indicators when they are low on fuel, I should only be hearing my own").
        ///
        /// <c>Ship.Update</c> calls <c>UpdateFuelLevel</c> every frame, and <c>Ship</c> is NOT one of
        /// the components <c>RemotePuppet.Neuter</c> disables — it cannot be, because the same Update
        /// drives presentation the puppet does need. Ship sync replicates fuel faithfully, so each
        /// puppet correctly observed its owner running low and then dutifully fired its own alarm:
        /// the animator bools that drive the warning light and its sound, plus a "Fuel low" line on
        /// that ship's log output. With three teammates low on fuel you hear three alarms for fuel
        /// that is not yours and cannot be.
        ///
        /// Suppressed for puppets only. The owner's own machine still runs this exactly as vanilla
        /// does, so every player gets their own warning and only their own.
        /// </summary>
        [HarmonyPatch(typeof(Ship), "UpdateFuelLevel")]
        internal static class FuelWarningsArePilotOnly
        {
            private static bool _logged;

            private static bool Prefix(Ship __instance)
            {
                if (__instance == null || __instance.GetComponent<RemotePuppet>() == null) return true;
                // Once per session: an audio bug is invisible in a log otherwise, and this is the
                // only evidence a headless test run can produce that the suppression is live.
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log.LogInfo("[Puppet] suppressing another player's fuel warning " +
                        "(cockpit alerts are pilot-only; their engines and weapons still sound)");
                }
                return false;
            }

            internal static void Reset() => _logged = false;
        }
    }
}
