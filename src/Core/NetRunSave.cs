using System;
using System.Collections.Generic;
using System.IO;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Persistent net-run state: seed, game settings, checkpoint, and the full catch-up
    /// ledgers (terrain cells, kills, upgrades, instruments, scanners). Saved periodically
    /// and at session end on EVERY machine — whoever hosts the resume replays it through the
    /// normal rejoin machinery, and each player's build/gold returns via their own seed-keyed
    /// EconomyStash. One save slot ("last run"); a new run overwrites it.
    /// </summary>
    internal static class NetRunSave
    {
        private const int FormatVersion = 1;
        private const float SaveInterval = 60f; // ledgers can reach map scale — don't write 20 MB every 20 s

        private static float _nextSaveAt;
        private static volatile bool _writing;

        public sealed class Data
        {
            public int Seed;
            public bool FriendlyFire;
            public bool HpScaling;
            public int LatestStationNetId;
            public List<(int index, byte type)> Cells = new List<(int, byte)>();
            public List<int> Kills = new List<int>();
            public List<(int netId, uint hash)> Upgrades = new List<(int, uint)>();
            public List<(int netId, uint hash)> Instruments = new List<(int, uint)>();
            public List<int> Scanners = new List<int>();
        }

        private static string PathFor() => Path.Combine(ModFolder.Dir, "lastrun.bin");

        public static bool Exists()
        {
            try { return File.Exists(PathFor()); }
            catch { return false; }
        }

        public static void Reset() => _nextSaveAt = 0;

        /// <summary>A wiped run is not resumable — remove the save.</summary>
        public static void Delete()
        {
            try { if (File.Exists(PathFor())) File.Delete(PathFor()); }
            catch (Exception e) { Plugin.Log.LogWarning($"[RunSave] delete failed: {e.Message}"); }
        }

        /// <summary>Called per frame while InGame; also call directly at session end.</summary>
        public static void Tick(NetSession session)
        {
            if (UnityEngine.Time.unscaledTime < _nextSaveAt) return;
            _nextSaveAt = UnityEngine.Time.unscaledTime + SaveInterval;
            Save(session);
        }

        public static void Save(NetSession session)
        {
            try
            {
                if (session == null || session.CurrentRunSeed == 0 || _writing) return;

                // Snapshot on the main thread (the Snapshot() helpers return copies), then
                // serialize on a worker — the terrain ledger can be tens of MB.
                int seed = session.CurrentRunSeed;
                bool ff = session.FriendlyFire;
                bool hp = session.HpScaling;
                int station = Sync.ProgressionSync.LatestStationNetId;
                var cells = Sync.WorldSync.LedgerSnapshot();
                var kills = Sync.EnemySync.KilledSnapshot();
                var upgrades = Sync.ProgressionSync.UpgradeSnapshot();
                var instruments = Sync.ProgressionSync.InstrumentSnapshot();
                var scanners = Sync.ProgressionSync.ScannerSnapshot();

                _writing = true;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        string tmp = PathFor() + ".tmp";
                        using (var fs = new FileStream(tmp, FileMode.Create))
                        using (var bw = new BinaryWriter(fs))
                        {
                            bw.Write(FormatVersion);
                            bw.Write(seed);
                            bw.Write(ff);
                            bw.Write(hp);
                            bw.Write(station);
                            bw.Write(cells.Count);
                            foreach (var (index, type) in cells) { bw.Write(index); bw.Write(type); }
                            bw.Write(kills.Count);
                            foreach (var id in kills) bw.Write(id);
                            bw.Write(upgrades.Count);
                            foreach (var (id, hash) in upgrades) { bw.Write(id); bw.Write(hash); }
                            bw.Write(instruments.Count);
                            foreach (var (id, hash) in instruments) { bw.Write(id); bw.Write(hash); }
                            bw.Write(scanners.Count);
                            foreach (var id in scanners) bw.Write(id);
                        }
                        if (File.Exists(PathFor())) File.Delete(PathFor());
                        File.Move(tmp, PathFor());
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"[RunSave] save failed: {e.Message}");
                    }
                    finally
                    {
                        _writing = false;
                    }
                });
            }
            catch (Exception e)
            {
                _writing = false;
                Plugin.Log.LogWarning($"[RunSave] save failed: {e.Message}");
            }
        }

        public static Data Load()
        {
            try
            {
                if (!Exists()) return null;
                using (var fs = new FileStream(PathFor(), FileMode.Open))
                using (var br = new BinaryReader(fs))
                {
                    if (br.ReadInt32() != FormatVersion) return null;
                    var d = new Data
                    {
                        Seed = br.ReadInt32(),
                        FriendlyFire = br.ReadBoolean(),
                        HpScaling = br.ReadBoolean(),
                        LatestStationNetId = br.ReadInt32(),
                    };
                    int n = br.ReadInt32();
                    for (int i = 0; i < n; i++) d.Cells.Add((br.ReadInt32(), br.ReadByte()));
                    n = br.ReadInt32();
                    for (int i = 0; i < n; i++) d.Kills.Add(br.ReadInt32());
                    n = br.ReadInt32();
                    for (int i = 0; i < n; i++) d.Upgrades.Add((br.ReadInt32(), br.ReadUInt32()));
                    n = br.ReadInt32();
                    for (int i = 0; i < n; i++) d.Instruments.Add((br.ReadInt32(), br.ReadUInt32()));
                    n = br.ReadInt32();
                    for (int i = 0; i < n; i++) d.Scanners.Add(br.ReadInt32());
                    return d;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RunSave] load failed: {e.Message}");
                return null;
            }
        }
    }
}
