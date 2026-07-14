using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>Generation-time fingerprint of identity, placement, and composite plant data.</summary>
    internal static class DeterminismAudit
    {
        internal struct Snapshot
        {
            internal int EntityCount;
            internal ulong EntityDigest;
            internal int PlantCount;
            internal ulong PlantDigest;
        }

        internal struct VisualSnapshot
        {
            internal int RendererCount;
            internal int VariantCount;
            internal ulong Digest;
        }

        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
        private static readonly FieldInfo CellVariantsField = typeof(UnityTilemapRenderer)
            .GetField("cellVariants", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static Snapshot Capture()
        {
            var snapshot = new Snapshot { EntityDigest = Offset, PlantDigest = Offset };
            EntityManager manager;
            try { manager = ServiceLocator.Get<EntityManager>(); }
            catch { return snapshot; }

            var entities = manager.GetAllEntities()
                .Where(e => e != null && e.entityId != "Ship")
                .OrderBy(e => e.instanceId).ToList();
            snapshot.EntityCount = entities.Count;
            foreach (var entity in entities)
            {
                Add(ref snapshot.EntityDigest, entity.instanceId);
                Add(ref snapshot.EntityDigest, entity.entityId);
                Add(ref snapshot.EntityDigest, entity.position);
                Add(ref snapshot.EntityDigest, entity.rotation);
                if (!entity.TryGetComponent<EntityPlant.Data>(out var plant) || plant == null) continue;
                snapshot.PlantCount++;
                Add(ref snapshot.PlantDigest, entity.instanceId);
                Add(ref snapshot.PlantDigest, plant.plantData != null ? plant.plantData.id : 0);
                AddBranch(ref snapshot.PlantDigest, plant.rootBranch);
                var fruits = plant.fruits ?? new List<EntityPlant.Data.Fruit>();
                Add(ref snapshot.PlantDigest, fruits.Count);
                foreach (var fruit in fruits.OrderBy(f => f.id))
                {
                    Add(ref snapshot.PlantDigest, fruit.id);
                    Add(ref snapshot.PlantDigest, fruit.branchId);
                }
            }
            Plugin.Log.LogInfo($"[Determinism] entities={snapshot.EntityCount}/{snapshot.EntityDigest:X16} " +
                $"plants={snapshot.PlantCount}/{snapshot.PlantDigest:X16}");
            return snapshot;
        }

        /// <summary>Hashes the actual byte variants consumed by every TilemapUpdater when it
        /// chooses sprites. Gameplay terrain can be identical while this render-only table
        /// differs, so it is deliberately a separate generation barrier fingerprint.</summary>
        internal static VisualSnapshot CaptureVisual(bool log = true)
        {
            var snapshot = new VisualSnapshot { Digest = Offset };
            var rendererDigests = new List<(int count, ulong digest)>();
            if (CellVariantsField != null)
            {
                foreach (var renderer in Resources.FindObjectsOfTypeAll<UnityTilemapRenderer>())
                {
                    if (renderer == null) continue;
                    byte[] variants;
                    try { variants = CellVariantsField.GetValue(renderer) as byte[]; }
                    catch { continue; }
                    if (variants == null || variants.Length == 0) continue;
                    ulong digest = Offset;
                    Add(ref digest, variants.Length);
                    foreach (byte value in variants) { digest ^= value; digest *= Prime; }
                    rendererDigests.Add((variants.Length, digest));
                }
            }
            rendererDigests.Sort((a, b) => a.digest != b.digest
                ? a.digest.CompareTo(b.digest) : a.count.CompareTo(b.count));
            snapshot.RendererCount = rendererDigests.Count;
            foreach (var renderer in rendererDigests)
            {
                snapshot.VariantCount += renderer.count;
                Add(ref snapshot.Digest, renderer.count);
                Add(ref snapshot.Digest, renderer.digest);
            }
            if (log)
                Plugin.Log.LogInfo($"[Determinism] visuals={snapshot.VariantCount}/{snapshot.Digest:X16} " +
                    $"renderers={snapshot.RendererCount}");
            return snapshot;
        }

        private static void AddBranch(ref ulong hash, EntityPlant.Data.Branch branch)
        {
            if (branch == null) { Add(ref hash, -1); return; }
            Add(ref hash, branch.id);
            Add(ref hash, branch.position);
            Add(ref hash, branch.direction);
            Add(ref hash, branch.end);
            Add(ref hash, branch.length);
            Add(ref hash, branch.lifeForce);
            Add(ref hash, Mathf.RoundToInt(branch.curveAngle * 4096f));
            var variations = branch.variations ?? new int[0];
            Add(ref hash, variations.Length);
            foreach (int variation in variations) Add(ref hash, variation);
            var children = branch.children ?? new List<EntityPlant.Data.Branch>();
            Add(ref hash, children.Count);
            foreach (var child in children) AddBranch(ref hash, child);
        }

        private static void Add(ref ulong hash, string value)
        {
            if (value == null) { Add(ref hash, -1); return; }
            Add(ref hash, value.Length);
            foreach (char c in value) { hash ^= (byte)c; hash *= Prime; hash ^= (byte)(c >> 8); hash *= Prime; }
        }

        private static void Add(ref ulong hash, Vector3 value)
        {
            Add(ref hash, Mathf.RoundToInt(value.x * 4096f));
            Add(ref hash, Mathf.RoundToInt(value.y * 4096f));
            Add(ref hash, Mathf.RoundToInt(value.z * 4096f));
        }

        private static void Add(ref ulong hash, Vector2 value)
        {
            Add(ref hash, Mathf.RoundToInt(value.x * 4096f));
            Add(ref hash, Mathf.RoundToInt(value.y * 4096f));
        }

        private static void Add(ref ulong hash, Quaternion value)
        {
            Add(ref hash, Mathf.RoundToInt(value.x * 32768f));
            Add(ref hash, Mathf.RoundToInt(value.y * 32768f));
            Add(ref hash, Mathf.RoundToInt(value.z * 32768f));
            Add(ref hash, Mathf.RoundToInt(value.w * 32768f));
        }

        private static void Add(ref ulong hash, int value)
        {
            unchecked
            {
                uint v = (uint)value;
                for (int i = 0; i < 4; i++) { hash ^= (byte)(v >> (i * 8)); hash *= Prime; }
            }
        }

        private static void Add(ref ulong hash, ulong value)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++) { hash ^= (byte)(value >> (i * 8)); hash *= Prime; }
            }
        }
    }
}
