using System;
using System.IO;
using HarmonyLib;
using PunkMultiverse.Sync;
using Sirenix.Serialization;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// The rejoiner's lifeline: every minute of a net run, the local player's Vault, RunData and
    /// module grid are snapshotted (the game's own Odin mementos) into the plugin folder, keyed by
    /// run seed. When that player rejoins the same run, the stash restores their economy and build
    /// instead of dumping them back to a fresh loadout.
    /// </summary>
    internal static class EconomyStash
    {
        private const float SaveInterval = 60f;
        private static float _nextSaveAt;

        private static string PathFor() => System.IO.Path.Combine(ModFolder.Dir, "netstash.bin");

        public static void Reset() => _nextSaveAt = 0;

        public static void Tick(NetSession session)
        {
            if (UnityEngine.Time.unscaledTime < _nextSaveAt) return;
            _nextSaveAt = UnityEngine.Time.unscaledTime + SaveInterval;
            try
            {
                Save(session.CurrentRunSeed);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Stash] save failed: {e.Message}");
            }
        }

        private static byte[] MementoOf(object originator)
        {
            if (originator == null) return null;
            var create = AccessTools.Method(originator.GetType(), "CreateMemento");
            var memento = create?.Invoke(originator, null);
            return memento == null ? null : SerializationUtility.SerializeValueWeak(memento, DataFormat.Binary);
        }

        private static void RestoreTo(object originator, byte[] bytes)
        {
            if (originator == null || bytes == null || bytes.Length == 0) return;
            var memento = SerializationUtility.DeserializeValueWeak(bytes, DataFormat.Binary);
            var restore = AccessTools.Method(originator.GetType(), "RestoreFromMemento");
            if (memento != null && restore != null) restore.Invoke(originator, new[] { memento });
        }

        private static object GridData()
        {
            var ship = ShipSync.LocalShip;
            var gridOwner = ship != null ? Traverse.Create(ship).Field("moduleGridOwner").GetValue() : null;
            return gridOwner != null ? Traverse.Create(gridOwner).Property("Data").GetValue() : null;
        }

        public static void Save(int seed)
        {
            object vault = null, runData = null;
            try { vault = ServiceLocator.Get<Vault>(); } catch { }
            try { runData = ServiceLocator.Get<RunData>(); } catch { }

            var vaultBytes = MementoOf(vault) ?? Array.Empty<byte>();
            var runBytes = MementoOf(runData) ?? Array.Empty<byte>();
            var gridBytes = MementoOf(GridData()) ?? Array.Empty<byte>();
            if (vaultBytes.Length == 0 && runBytes.Length == 0 && gridBytes.Length == 0) return;

            using (var fs = new FileStream(PathFor(), FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(seed);
                foreach (var blob in new[] { vaultBytes, runBytes, gridBytes })
                {
                    bw.Write(blob.Length);
                    bw.Write(blob);
                }
            }
            // Diagnostic (inventory/stash-loss investigation 2026-07-23): confirms a snapshot was
            // written for THIS run seed, so a later "skipping restore" seed-mismatch is attributable.
            Plugin.Log.LogInfo($"[Stash] saved run {seed} (vault {vaultBytes.Length}B, run {runBytes.Length}B, grid {gridBytes.Length}B)");
        }

        /// <summary>Called after a rejoin goes live; restores only when the stash matches the run.</summary>
        public static void TryRestore(int seed)
        {
            try
            {
                var path = PathFor();
                if (!File.Exists(path))
                {
                    Plugin.Log.LogInfo($"[Stash] no stash file at {path} — nothing to restore for run {seed}");
                    return;
                }
                byte[] vaultBytes, runBytes, gridBytes;
                using (var fs = new FileStream(path, FileMode.Open))
                using (var br = new BinaryReader(fs))
                {
                    int storedSeed = br.ReadInt32();
                    if (storedSeed != seed)
                    {
                        // Inventory/stash-loss investigation 2026-07-23: this exact line is the
                        // symptom — a leave->migrate->rejoin that lands here means the run seed the
                        // rejoining player was given (current) does not match the seed their leave
                        // snapshot was saved under (stored). Log both so the cause is unambiguous.
                        Plugin.Log.LogWarning($"[Stash] seed mismatch — stored={storedSeed} current={seed}; skipping restore (inventory/stash will appear lost)");
                        return;
                    }
                    vaultBytes = br.ReadBytes(br.ReadInt32());
                    runBytes = br.ReadBytes(br.ReadInt32());
                    gridBytes = br.ReadBytes(br.ReadInt32());
                }

                object vault = null, runData = null;
                try { vault = ServiceLocator.Get<Vault>(); } catch { }
                try { runData = ServiceLocator.Get<RunData>(); } catch { }
                RestoreTo(vault, vaultBytes);
                RestoreTo(runData, runBytes);
                RestoreTo(GridData(), gridBytes);
                Plugin.Log.LogInfo("[Stash] rejoin restore applied (vault, run data, module grid)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Stash] restore failed: {e.Message}");
            }
        }
    }
}
