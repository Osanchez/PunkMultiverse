using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Routes terrain changes to the segment they belong to instead of broadcasting all of them to
    /// every segment.
    ///
    /// THE DEFECT (vanilla, measured 2026-07-28). <c>LevelChangeBuffer.Update</c> fires one
    /// <c>CellsChanged</c> event per frame carrying EVERY cell that changed, and every live
    /// <c>LevelSegmentComponent</c> subscribes to it. Each segment then walks the whole list and
    /// discards the ~99% of changes that are not inside its own 25x25 rect:
    ///
    ///     foreach (var change in destroyedCells) {
    ///         if (!Rect.Contains(change.position)) continue;   // <- the entire cost
    ///
    /// So the work per frame is (loaded segments) x (changed cells), and the discarded majority
    /// still pays two <c>ServiceLocator.Get</c> calls and a rect test each. Single-player never
    /// noticed: you break a handful of cells at a time.
    ///
    /// Battle Royale breaks that assumption by three orders of magnitude — the lava ring converts
    /// the whole playable disc, thousands of cells per frame for minutes.
    ///
    /// HONEST SCOPE: this is a real inefficiency and worth removing, but it is NOT what was killing
    /// the host. It was written first, on the strength of reading the source, and changed nothing —
    /// the coordinator still fell to 0.2fps with it active. Per-handler timing (the `cellfanout`
    /// devcmd, written afterwards for exactly that reason) put <c>LevelSegmentComponent</c> at
    /// **1.5ms** of a 13,619ms window; the 94% was <c>GroundTilemapUpdater</c>, fixed in
    /// <see cref="TerrainPresentationTrim"/>. Kept because O(changes) beats O(segments x changes)
    /// on its own merits and it costs one bucketing pass — not because it fixed the stall.
    ///
    /// THE FIX. Bucket the frame's changes by segment ONCE, then hand each segment only its own.
    /// O(segments x changes) becomes O(changes). Behaviour is identical by construction: the rect
    /// test was the first statement in the loop, so a segment sees exactly the cells it would have
    /// kept, in the same order. Everything downstream — collider rebuilds, destroy particles, cell
    /// loot drops — runs exactly as before.
    ///
    /// This is not BR-specific. Any large simultaneous change (a big explosion, a terrain repair
    /// chunk, a rejoining client's catch-up diff) pays the same quadratic in normal co-op; BR is
    /// just the first thing that made it impossible to miss.
    /// </summary>
    internal static class SegmentChangeRouting
    {
        // The frame's changes, bucketed by segment. Rebuilt when a new batch arrives — identified
        // by frame number AND list identity, because one frame can legitimately fire the event more
        // than once (a nested SetCell during a subscriber's own handling).
        private static readonly Dictionary<int, List<Level.CellChange>> Buckets
            = new Dictionary<int, List<Level.CellChange>>();
        private static readonly List<Level.CellChange> Empty = new List<Level.CellChange>();
        private static object _batchIdentity;
        private static int _batchFrame = -1;
        private static int _batchCount;

        internal static int LastBatchCells => _batchCount;

        /// <summary>Every segment asks for the same list, so the buckets are built for the first
        /// caller and reused by the rest of that frame's subscribers.</summary>
        private static void EnsureBuckets(IEnumerable<Level.CellChange> changes, int segmentSize)
        {
            if (ReferenceEquals(_batchIdentity, changes) && _batchFrame == Time.frameCount) return;
            _batchIdentity = changes;
            _batchFrame = Time.frameCount;
            _batchCount = 0;
            foreach (var bucket in Buckets.Values) bucket.Clear();

            foreach (var change in changes)
            {
                _batchCount++;
                // RectInt.Contains is min-inclusive / max-exclusive and the rect is
                // SegmentPosition * SegmentSize, so integer division IS the same test.
                int sx = FloorDiv(change.position.x, segmentSize);
                int sy = FloorDiv(change.position.y, segmentSize);
                int key = Key(sx, sy);
                if (!Buckets.TryGetValue(key, out var list))
                    Buckets[key] = list = new List<Level.CellChange>(64);
                list.Add(change);
            }
        }

        // Segment coordinates are never negative in practice, but a cell at -1 must not fold into
        // segment 0 and be handed to the wrong component.
        private static int FloorDiv(int value, int size)
            => value >= 0 ? value / size : ((value + 1) / size) - 1;

        private static int Key(int sx, int sy) => (sx << 16) ^ (sy & 0xFFFF);

        [HarmonyPatch(typeof(LevelSegmentComponent), "OnCellsChanged")]
        internal static class RouteToOwningSegment
        {
            private static bool Prefix(LevelSegmentComponent __instance,
                ref IEnumerable<Level.CellChange> __0)
            {
                if (!NetConfig.SegmentChangeRouting.Value) return true;
                try
                {
                    int size = Level.SegmentSize;
                    if (size <= 0) return true;
                    EnsureBuckets(__0, size);
                    var pos = __instance.SegmentPosition;
                    __0 = Buckets.TryGetValue(Key(pos.x, pos.y), out var mine) ? mine : Empty;
                }
                catch (System.Exception e)
                {
                    // Never let an optimisation be the thing that stops terrain updating. Fall back
                    // to the vanilla whole-list behaviour, loudly, once.
                    if (!_warned)
                    {
                        _warned = true;
                        Plugin.Log.LogWarning($"[Segments] change routing disabled: {e.Message}");
                    }
                }
                return true;
            }

            private static bool _warned;
        }
    }
}
