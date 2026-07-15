using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using Unity.Collections;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Shared map exploration — the first code to actually honor NetConfig.ShareMapExploration.
    ///
    /// The map screen's discovered fill is a purely LOCAL accumulation inside the game's
    /// MapDrawer: RenderFoW blits a reveal brush into fowTexture at Camera.main every frame,
    /// and the colored map texture fills per segment the camera enters. Nothing about it was
    /// ever synced (the old FogSync diffed Level.fogLevels — the gas simulation, a different
    /// system entirely), so a region only a teammate visited never filled in, alive or
    /// spectating.
    ///
    /// Remote ship positions already replicate at ShipStateHz, so no protocol is needed:
    /// after the game's own camera reveal, repeat the exact same reveal around every remote
    /// player's ship. Spectator mode is covered for free — the reveal no longer depends on
    /// where the local camera happens to be.
    /// </summary>
    [HarmonyPatch(typeof(MapDrawer), "RenderFoW")]
    internal static class MapShareSync
    {
        private const float MaskAndSegmentInterval = 0.25f; // icon mask + segment fill cadence

        private static readonly int ScaleOffsetId = Shader.PropertyToID("_MainTex_ScaleOffset");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private static MapDrawer _cachedDrawer;
        private static Level _level;
        private static Texture2D _brush;
        private static RenderTexture _fowTexture;
        private static Material _material;
        private static Queue<Vector2Int> _positionsToDraw;
        private static HashSet<Vector2Int> _drawnPositions;
        private static readonly Dictionary<int, Vector2Int> _lastSegmentBySlot = new Dictionary<int, Vector2Int>();
        private static readonly Dictionary<int, float> _nextMaskAtBySlot = new Dictionary<int, float>();
        private static bool _warned;

        internal static void Reset()
        {
            _cachedDrawer = null;
            _level = null;
            _brush = null;
            _fowTexture = null;
            _material = null;
            _positionsToDraw = null;
            _drawnPositions = null;
            _lastSegmentBySlot.Clear();
            _nextMaskAtBySlot.Clear();
        }

        // RenderFoW only runs while gameController.IsGameStarted — piggybacking the postfix
        // inherits that gate and the once-per-LateUpdate cadence.
        private static void Postfix(MapDrawer __instance)
        {
            try
            {
                if (!NetSession.Active
                    || NetConfig.ShareMapExploration == null || !NetConfig.ShareMapExploration.Value) return;
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame) return;
                if (!EnsureCache(__instance)) return;

                foreach (var kv in ShipSync.ShipsBySlot)
                {
                    var ship = kv.Value;
                    if (ship == null || ship.IsDead) continue;
                    if (ship.GetComponent<RemotePuppet>() == null) continue; // local ship: vanilla path
                    RevealAt(__instance, ship.transform.position, kv.Key);
                }
            }
            catch (System.Exception e)
            {
                if (_warned) return;
                _warned = true;
                Plugin.Log.LogWarning($"[Map] shared reveal failed (disabled for this run): {e}");
                _cachedDrawer = null;
            }
        }

        private static bool EnsureCache(MapDrawer drawer)
        {
            if (_cachedDrawer == drawer && _level != null) return true;
            var walker = Traverse.Create(drawer);
            _level = walker.Field("level").GetValue() as Level;
            _brush = walker.Field("fowBrush").GetValue() as Texture2D;
            _fowTexture = walker.Field("fowTexture").GetValue() as RenderTexture;
            _material = walker.Field("fowDrawMaterial").GetValue() as Material;
            _positionsToDraw = walker.Field("positionsToDraw").GetValue() as Queue<Vector2Int>;
            _drawnPositions = walker.Field("drawnPositons").GetValue() as HashSet<Vector2Int>;
            if (_level == null || _brush == null || _fowTexture == null || _material == null) return false;
            _cachedDrawer = drawer;
            _lastSegmentBySlot.Clear();
            _nextMaskAtBySlot.Clear();
            return true;
        }

        private static void RevealAt(MapDrawer drawer, Vector2 pos, int slot)
        {
            // The FoW blit — RenderFoW's own math with the camera position swapped for the
            // ship's. `level.Width / fowBrush.width` is integer division in the original;
            // matched exactly so both reveals paint identical footprints.
            float scale = _level.Width / _brush.width;
            float ox = scale * pos.x / _level.Width - 0.5f;
            float oy = scale * pos.y / _level.Height - 0.5f;
            _material.SetVector(ScaleOffsetId, new Vector4(scale, scale, 0f - ox, 0f - oy));
            _material.SetColor(ColorId, Color.red);
            Graphics.Blit(_brush, _fowTexture, _material);

            // The icon-unhide mask and the colored segment fill move at ship speed, not frame
            // rate — a gentler cadence keeps the mask loop and segment queue cheap.
            if (_nextMaskAtBySlot.TryGetValue(slot, out float next) && Time.unscaledTime < next) return;
            _nextMaskAtBySlot[slot] = Time.unscaledTime + MaskAndSegmentInterval;

            RevealIconMask(drawer, pos);
            RevealSegments(pos, slot);
        }

        /// <summary>Replicates MapIconMaskUpdaterJob for one position: a filled circle of
        /// brush-radius cells. The NativeArray is re-fetched per call (cheap at this cadence)
        /// because MapDrawer recreates it on level generation and a stale struct copy would
        /// write into a disposed buffer.</summary>
        private static void RevealIconMask(MapDrawer drawer, Vector2 pos)
        {
            var mask = Traverse.Create(drawer).Field("FoWMaskForIcons").GetValue<NativeArray<bool>>();
            int width = _level.Width, height = _level.Height;
            if (!mask.IsCreated || mask.Length < width * height) return;
            int cx = Mathf.RoundToInt(pos.x), cy = Mathf.RoundToInt(pos.y);
            int radius = _brush.width / 2;
            int r2 = radius * radius;
            int minY = Mathf.Max(0, cy - radius), maxY = Mathf.Min(height - 1, cy + radius);
            int minX = Mathf.Max(0, cx - radius), maxX = Mathf.Min(width - 1, cx + radius);
            for (int y = minY; y <= maxY; y++)
            {
                int dy = y - cy;
                int row = y * width;
                for (int x = minX; x <= maxX; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dy * dy <= r2) mask[row + x] = true;
                }
            }
        }

        /// <summary>Replicates OnCurrentSegmentChanged for a remote ship: color the segment
        /// (and ring-1 neighbours) it entered. MapDrawer.Update drains the queue one segment
        /// per frame, exactly as it does for the local camera.</summary>
        private static void RevealSegments(Vector2 pos, int slot)
        {
            if (_positionsToDraw == null || _drawnPositions == null) return;
            var segment = new Vector2Int(
                Mathf.FloorToInt(pos.x / Level.SegmentSize),
                Mathf.FloorToInt(pos.y / Level.SegmentSize));
            if (_lastSegmentBySlot.TryGetValue(slot, out var last) && last == segment) return;
            _lastSegmentBySlot[slot] = segment;
            foreach (var neighbour in _level.GetNeighbourSegments(segment, 1))
                if (!_drawnPositions.Contains(neighbour))
                    _positionsToDraw.Enqueue(neighbour);
        }
    }
}
