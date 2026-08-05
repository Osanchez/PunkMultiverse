using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// Paints the Battle Royale ring onto the game's own minimap, so players can see where the
    /// safe ground is going and pick a route to it — the whole point of a closing zone is that you
    /// can plan against it.
    ///
    /// The minimap regenerates its texture from terrain cells on a timer; this rides a postfix on
    /// that refresh and stamps extra pixels into the same texture before it is shown again. It is
    /// drawn, not generated as an asset, because the minimap is 1 pixel per world cell — a circle
    /// of pixels IS the art at this scale, and it matches the game's 8-bit look by construction
    /// (hard pixels, no smoothing; the texture is FilterMode.Point).
    ///
    /// Colors follow the game's own palette roles: hazard-amber for the incoming ring (the same
    /// warning language as lava), and a bright cyan diamond for supply drops.
    /// </summary>
    internal static class RingMapOverlay
    {
        // The zone you must reach: a dashed amber circle, dashed so it reads as a boundary marker
        // rather than terrain, and so it never fully hides the ground underneath it.
        private static readonly Color32 IncomingRing = new Color32(255, 170, 40, 255);
        private static readonly Color32 CurrentRing = new Color32(255, 70, 30, 255);
        private static readonly Color32 DropMarker = new Color32(90, 255, 210, 255);

        [HarmonyPatch(typeof(Minimap), "Refresh")]
        internal static class PaintRing
        {
            private static void Postfix(Minimap __instance)
            {
                // Note: NOT gated on RingKnown. Hiding the other players has to hold from the very
                // first refresh — before the first ring broadcast arrives is exactly when everyone
                // is looking at the map deciding where to go.
                if (!Modes.BattleRoyale.Active) return;
                try { Paint(__instance); }
                catch { /* the minimap must never be the thing that breaks a match */ }
            }
        }

        private static void Paint(Minimap map)
        {
            var t = Traverse.Create(map);
            var texture = t.Field("texture").GetValue<Texture2D>();
            if (texture == null) return;
            Vector2Int resolution = t.Field("resolution").GetValue<Vector2Int>();
            var shipManager = t.Field("shipManager").GetValue<ShipManager>();
            var cam = t.Field("mainCamera").GetValue<Camera>();

            // Same window the minimap itself just used, or the overlay would sit off by the
            // difference and look like a bug.
            Vector2 center;
            bool isCoop = false;
            try { isCoop = ServiceLocator.Get<GameController>().IsCoop; } catch { }
            if (!isCoop && shipManager != null && shipManager.Ships.Count > 0)
                center = shipManager.Ships.First().transform.position;
            else if (cam != null) center = cam.transform.position;
            else return;
            Vector2Int bottomLeft = Vector2Int.RoundToInt(center - (Vector2)resolution * 0.5f);

            var pixels = texture.GetRawTextureData<Color32>();

            HideOtherPlayers(map, shipManager, pixels, resolution, bottomLeft, center);

            // Hiding other players still happens above and from the very first refresh; only the
            // ring GEOMETRY waits for the first closure (see BattleRoyale.RingVisible).
            if (!Modes.BattleRoyale.RingVisible) { texture.Apply(); return; }
            var ring = Modes.BattleRoyale.Ring;
            var ringCenter = Modes.BattleRoyale.RingCenter;

            // The lava wall is real terrain and already draws itself; this outlines the CURRENT
            // boundary so it stays legible even where the wall is thin or off-window.
            float safe = Modes.BattleRoyale.RingRadius;
            if (safe > 1f)
                StampCircle(pixels, resolution, bottomLeft, ringCenter, safe, CurrentRing, dash: 6);

            // The ground you need to be standing on after this closure — same run-up-only rule the
            // map screen uses, so the two surfaces never disagree about whether a warning is live.
            // Drawn on the TARGET centre, which the drift has moved: the offset between the two
            // circles is the whole message, and stamping both on one centre would erase it.
            if (ring.TargetRadius > 1f && Modes.BattleRoyale.NextRingVisible)
                StampCircle(pixels, resolution, bottomLeft, Modes.BattleRoyale.RingTargetCenter,
                    ring.TargetRadius, IncomingRing, dash: 3);

            foreach (var kv in Modes.BattleRoyale.CarePackages)
                StampDiamond(pixels, resolution, bottomLeft, kv.Value, DropMarker);

            texture.Apply();
        }

        /// <summary>Battle Royale hides where everyone is — the screen-edge trackers already stand
        /// down (UI/PlayerTracker.cs), but the game's own minimap was still handing out everyone's
        /// position for free: <c>Minimap.Refresh</c> stamps a ship-coloured pixel for every entry in
        /// <c>ShipManager.Ships</c>, and in a net run that list holds the remote players' puppets
        /// too. Single-player never had a second ship in it, so vanilla never had to ask.
        ///
        /// Rather than fight the vanilla loop, undo it: the minimap is one pixel per world cell and
        /// <c>Minimap.GetColor</c> is the public function that says what a cell should look like, so
        /// repainting each remote ship's pixel with its own terrain colour restores exactly what was
        /// underneath. The local ship is re-stamped afterwards in case a remote was standing on the
        /// same pixel — you must never lose track of yourself.</summary>
        private static void HideOtherPlayers(Minimap map, ShipManager shipManager,
            Unity.Collections.NativeArray<Color32> pixels, Vector2Int res, Vector2Int bottomLeft,
            Vector2 windowCenter)
        {
            if (shipManager == null) return;
            var local = Core.NetSession.Instance != null ? Sync.ShipSync.LocalShip : null;
            bool erasedUnderLocal = false;

            foreach (var ship in shipManager.Ships)
            {
                if (ship == null || ship == local) continue;
                Vector2 p = ship.transform.position;
                // Same window test the vanilla loop used, or we would "erase" a pixel it never drew.
                if (Mathf.Abs(p.x - windowCenter.x) > res.x / 2f) continue;
                if (Mathf.Abs(p.y - windowCenter.y) > res.y / 2f) continue;
                int wx = Mathf.RoundToInt(p.x), wy = Mathf.RoundToInt(p.y);
                Vector2Int px = new Vector2Int(wx, wy) - bottomLeft;
                Plot(pixels, res, px.x, px.y, map.GetColor(wx, wy));
                if (local != null
                    && Vector2Int.RoundToInt(local.transform.position) == new Vector2Int(wx, wy))
                    erasedUnderLocal = true;
            }

            if (!erasedUnderLocal || local == null) return;
            try
            {
                var shipColor = Traverse.Create(map).Field("shipColor").GetValue<Color>();
                Vector2 lp = local.transform.position;
                Vector2Int lpx = Vector2Int.RoundToInt(lp) - bottomLeft;
                Plot(pixels, res, lpx.x, lpx.y, shipColor);
            }
            catch { }
        }

        /// <summary>Walk the circle by arc length so spacing is even at any radius, and skip pixels
        /// to dash it. Only pixels inside the minimap window are written.</summary>
        private static void StampCircle(Unity.Collections.NativeArray<Color32> pixels, Vector2Int res,
            Vector2Int bottomLeft, Vector2 worldCenter, float radius, Color32 color, int dash)
        {
            int steps = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * radius), 32, 8192);
            for (int i = 0; i < steps; i++)
            {
                if (dash > 1 && (i / dash) % 2 == 1) continue; // dashed
                float a = i / (float)steps * Mathf.PI * 2f;
                int wx = Mathf.RoundToInt(worldCenter.x + Mathf.Cos(a) * radius);
                int wy = Mathf.RoundToInt(worldCenter.y + Mathf.Sin(a) * radius);
                Plot(pixels, res, wx - bottomLeft.x, wy - bottomLeft.y, color);
            }
        }

        /// <summary>A small diamond — readable at one pixel per cell, and distinct from the round
        /// ring lines at a glance.</summary>
        private static void StampDiamond(Unity.Collections.NativeArray<Color32> pixels, Vector2Int res,
            Vector2Int bottomLeft, Vector2 world, Color32 color)
        {
            int cx = Mathf.RoundToInt(world.x) - bottomLeft.x;
            int cy = Mathf.RoundToInt(world.y) - bottomLeft.y;
            const int r = 3;
            for (int dy = -r; dy <= r; dy++)
            {
                int span = r - Mathf.Abs(dy);
                for (int dx = -span; dx <= span; dx++)
                {
                    // Hollow: outline only, so it marks a spot without masking it.
                    if (Mathf.Abs(dx) + Mathf.Abs(dy) < r) continue;
                    Plot(pixels, res, cx + dx, cy + dy, color);
                }
            }
        }

        private static void Plot(Unity.Collections.NativeArray<Color32> pixels, Vector2Int res,
            int px, int py, Color32 color)
        {
            if (px < 0 || py < 0 || px >= res.x || py >= res.y) return;
            int idx = py * res.x + px;
            if (idx < 0 || idx >= pixels.Length) return;
            pixels[idx] = color;
        }
    }
}
