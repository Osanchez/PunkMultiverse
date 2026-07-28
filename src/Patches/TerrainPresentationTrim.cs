using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Stops terrain PRESENTATION work being done for cells nobody is looking at.
    ///
    /// MEASURED, not guessed (2026-07-28, `cellfanout` on a Battle Royale coordinator mid-closure).
    /// The host fell from 120fps to 0.1fps as the ring converted ground, and `simprof` blamed
    /// <c>LevelChangeBuffer.Update</c> — which is just the publisher. Timing its eight subscribers
    /// individually gave the real answer, and it was not any of the three systems that looked
    /// guilty from reading the source:
    ///
    ///     GroundTilemapUpdater   9527.4ms   avg 397ms/call   worst 1449ms    &lt;- 94%
    ///     LightmapGenerator       610.8ms   avg  76ms/call
    ///     MapDrawer                 4.6ms
    ///     LevelSegmentComponent     1.4ms
    ///     NavigationManager         0.6ms
    ///
    /// <c>GroundTilemapUpdater.OnCellsChanged</c> calls <c>Refresh</c> for every changed cell, and
    /// each Refresh issues four Unity Tilemap calls (SetTile, SetTransformMatrix, SetTileFlags,
    /// SetColor) — about 0.6ms per cell, paid once per tilemap layer. At 15,000 changed cells per
    /// ten seconds that is ~9.5 seconds of tilemap writes per ten seconds of wall clock. Single
    /// player never hit it because you break a handful of cells at a time; a closing lava ring
    /// changes thousands per frame for minutes.
    ///
    /// TWO FIXES, both of which lose nothing:
    ///
    /// 1. A COORDINATOR draws nothing at all — no camera, no renderer, `-nographics`. Every tile
    ///    write and every lightmap segment it queues is pure waste. Skipped outright.
    ///
    /// 2. A PLAYER only needs tiles for cells they can see. <c>TilemapUpdater</c> already
    ///    subscribes to <c>UnityTilemapRenderer.CellBecameVisible</c> and refreshes a cell when it
    ///    scrolls into view, so refreshing an off-screen cell now is work that will simply be
    ///    redone on entry. Filtered to cells the renderer currently reports visible — which is the
    ///    same set the vanilla visibility path uses, so the two agree by construction.
    ///
    /// This is not Battle Royale specific. Any bulk terrain change pays it: a big explosion, a
    /// terrain repair chunk, a rejoining client's catch-up diff. BR is simply the first thing that
    /// changed enough cells to make it fatal.
    /// </summary>
    internal static class TerrainPresentationTrim
    {
        private static readonly List<Level.CellChange> Visible = new List<Level.CellChange>(256);
        private static System.Reflection.FieldInfo _rendererField;

        /// <summary>Tile refresh for changed cells. On a coordinator: skipped. On a player's
        /// machine: narrowed to what is actually on screen.</summary>
        [HarmonyPatch(typeof(GroundTilemapUpdater), "OnCellsChanged")]
        internal static class RefreshOnlyVisibleTiles
        {
            private static bool Prefix(GroundTilemapUpdater __instance,
                ref IEnumerable<Level.CellChange> __0)
            {
                if (!NetConfig.TrimTerrainPresentation.Value) return true;
                if (NetConfig.IsCoordinator) return false; // headless: nothing renders, ever
                try
                {
                    if (_rendererField == null)
                        _rendererField = AccessTools.Field(typeof(TilemapUpdater), "unityTilemapRenderer");
                    if (!(_rendererField?.GetValue(__instance) is UnityTilemapRenderer renderer))
                        return true;

                    Visible.Clear();
                    foreach (var change in __0)
                        if (renderer.IsCellVisible(change.position)) Visible.Add(change);
                    // Nothing on screen changed: the whole handler is a no-op, skip the iteration
                    // (and the per-call LocalConfig lookup) entirely.
                    if (Visible.Count == 0) return false;
                    __0 = Visible;
                }
                catch (System.Exception e)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Plugin.Log.LogWarning($"[Trim] tile visibility gate disabled: {e.Message}");
                    }
                }
                return true;
            }

            private static bool _warned;
        }

        /// <summary>Lightmap regeneration for changed cells — coordinator only. A player's machine
        /// keeps vanilla behaviour: unlike tiles, lighting has no visibility-driven catch-up path
        /// to lean on, and at 6% of the fanout it is not worth risking dark terrain over.</summary>
        [HarmonyPatch(typeof(LightmapGenerator), "OnCellsChanged")]
        internal static class NoLightmapOnCoordinator
        {
            private static bool Prefix()
                => !NetConfig.TrimTerrainPresentation.Value || !NetConfig.IsCoordinator;
        }
    }
}
