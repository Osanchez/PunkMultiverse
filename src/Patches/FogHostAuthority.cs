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
        /// stale array makes the first tick "correct" fog back toward its generation layout,
        /// rewriting shared terrain and spiking traffic at the worst moment.
        ///
        /// Reconcile fogLevels with the CURRENT cell types in one pass: every live fog cell gets
        /// exactly <c>fogThreshold</c> — the minimum that keeps it a fog cell, and (since the
        /// spread trigger sits above the existence threshold) BELOW the level that would make it
        /// diffuse into its neighbours. That matters on big maps: seeding to startingFogLevel (as
        /// the game does at generation) slams the whole evolved fog area to an actively-spreading
        /// value at once, and the next tick's wavefront of threshold crossings floods SetCell ->
        /// WorldSync and starves the new host's frames (observed: promoted player freezes, ship
        /// stuck while boosters animate). Threshold-seeding keeps fog present and lets it
        /// re-energise gently from its FogSources. Non-fog cells are cleared so decayed areas
        /// don't re-add. Called from NetSession.BecomeHost.</summary>
        public static void ReseedFromTerrain()
        {
            try
            {
                var level = ServiceLocator.Get<Level>();
                if (level == null || !level.fogLevels.IsCreated || !level.cellTypes.IsCreated) return;
                var mgr = Object.FindAnyObjectByType<FogManager>();
                if (mgr == null) return;

                var fogCellType = HarmonyLib.Traverse.Create(mgr).Field("fogCellType").GetValue() as CellType;
                byte fogThreshold = HarmonyLib.Traverse.Create(mgr).Field("fogThreshold").GetValue<byte>();
                if (fogCellType == null) { Plugin.Log.LogWarning("[Fog] reseed skipped — fogCellType not readable"); return; }
                byte fogId = fogCellType.id;

                var fog = level.fogLevels;
                var cells = level.cellTypes;
                int n = Mathf.Min(fog.Length, cells.Length);
                int fogCells = 0;
                for (int i = 0; i < n; i++)
                {
                    if (cells[i] == fogId) { fog[i] = fogThreshold; fogCells++; }
                    else fog[i] = 0;
                }
                Plugin.Log.LogInfo($"[Fog] reseeded fogLevels from terrain on host promotion ({fogCells} fog cells of {n}, held at threshold {fogThreshold})");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Fog] fogLevels reseed on promotion failed: {e.Message}");
            }
        }
    }
}
