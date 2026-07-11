using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Fog (Level.fogLevels) is a global cellular-automaton gas simulation, not per-object
    /// state — a whole-map Burst job that diffuses/decays every cell and, on threshold
    /// crossings, calls level.SetCell to convert real terrain. It is host-authoritative in a
    /// net run: only the host runs the simulation, and the terrain conversions it produces
    /// replicate through WorldSync's existing SetCell capture. Clients render fog purely from
    /// synced cell types (FogManager.RefreshMask reads GetCellTypeId, never fogLevels), so
    /// suppressing their simulation is invisible.
    ///
    /// Without this, both machines ran independent fog sims AND FogSync diffed fogLevels every
    /// few seconds — a self-reinforcing ping-pong (each side "corrects" the other, whose next
    /// sim tick immediately re-diverges) that flooded the reliable channel and timed the Steam
    /// connection out. See also the gutted FogSync.
    /// </summary>
    [HarmonyPatch(typeof(FogManager), "Refresh")]
    internal static class FogHostAuthority
    {
        private static bool Prefix()
        {
            // Single-player and the host simulate fog as vanilla; only net-run clients skip.
            if (!NetSession.Active) return true;
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame || session.IsHost) return true;
            return false; // client: don't schedule FogUpdateJob — fog terrain arrives via WorldSync
        }

        /// <summary>Host-migration handoff: a promoted machine was a client, so it never ran the
        /// fog sim — its <c>Level.fogLevels</c> is frozen at the gen-time seed while its cell
        /// types are fully current (WorldSync tracked every conversion). Resuming the sim on that
        /// stale array makes the first tick "correct" fog back toward its generation layout: it
        /// strips fog that spread and re-adds fog that decayed, rewriting shared terrain and
        /// spiking traffic at the worst moment. Reconcile fogLevels with the CURRENT cell types
        /// first — zero it, then let the game's own <see cref="FogManager.FillInitialFogLevels"/>
        /// re-seed every live fog cell to startingFogLevel (the same invariant level-gen relies
        /// on). Sub-threshold intensity gradients are lost (a half-decayed cloud restarts full),
        /// which is imperceptible next to a whole-map fog snap. Called from NetSession.BecomeHost.</summary>
        public static void ReseedFromTerrain()
        {
            try
            {
                var level = ServiceLocator.Get<Level>();
                var fog = level != null ? level.fogLevels : default;
                if (!fog.IsCreated) return;
                var mgr = Object.FindAnyObjectByType<FogManager>();
                if (mgr == null) return;
                for (int i = 0; i < fog.Length; i++) fog[i] = 0;
                mgr.FillInitialFogLevels(); // re-seeds live fog cells from current cellTypes
                Plugin.Log.LogInfo($"[Fog] reseeded fogLevels from terrain on host promotion ({fog.Length} cells)");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Fog] fogLevels reseed on promotion failed: {e.Message}");
            }
        }
    }
}
