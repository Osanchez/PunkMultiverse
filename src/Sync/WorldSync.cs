using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Destructible-terrain sync. Local cell changes (any changeSource except ours) batch per
    /// frame into CELL_DIFFs; the host applies + rebroadcasts; receivers apply with our sentinel
    /// so the patch doesn't echo and regrow/auto-pop don't double-cascade. Loot from destroyed
    /// cells intentionally stays local — that's the per-player economy.
    /// </summary>
    internal static class WorldSync
    {
        public const int ChangeSourceNet = 777301;

        private static readonly Dictionary<int, byte> Pending = new Dictionary<int, byte>();
        private static readonly NetWriter Writer = new NetWriter(4096);
        private static Level _level;
        private static MethodInfo _setCellByIndex;   // SetCell(int, byte, int)
        private static MethodInfo _setCellByVec;     // SetCell(Vector2Int, byte, int)
        private static int _width = -1;
        private static bool _applying;

        public static void Reset()
        {
            Pending.Clear();
            Ledger.Clear();
            ApplyQueue.Clear();
            Streams.Clear();
            _syncToastActive = false;
            _level = null;
            _setCellByIndex = null;
            _setCellByVec = null;
            _width = -1;
            _applying = false;
        }

        private static bool ParamsMatch(MethodInfo m, params Type[] types)
        {
            var ps = m.GetParameters();
            if (ps.Length != types.Length) return false;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].ParameterType != types[i])
                    return false;
            return true;
        }

        private static Level Level
        {
            get
            {
                if (_level == null)
                {
                    try { _level = ServiceLocator.Get<Level>(); } catch { }
                    if (_level != null)
                    {
                        var methods = typeof(Level).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _setCellByIndex = methods.FirstOrDefault(m => m.Name == "SetCell" && ParamsMatch(m, typeof(int), typeof(byte), typeof(int)));
                        _setCellByVec = methods.FirstOrDefault(m => m.Name == "SetCell" && ParamsMatch(m, typeof(UnityEngine.Vector2Int), typeof(byte), typeof(int)));
                        _width = ReadWidth(_level);
                        if (_setCellByIndex == null && (_setCellByVec == null || _width <= 0))
                            Plugin.Log.LogWarning("[World] no usable SetCell overload found — cell sync disabled");
                    }
                }
                return _level;
            }
        }

        private static int ReadWidth(Level level)
        {
            foreach (var name in new[] { "Width", "width" })
            {
                try
                {
                    var t = Traverse.Create(level);
                    var prop = t.Property(name);
                    if (prop != null && prop.PropertyExists()) return prop.GetValue<int>();
                    var field = t.Field(name);
                    if (field != null && field.FieldExists()) return field.GetValue<int>();
                }
                catch { }
            }
            Plugin.Log.LogWarning("[World] Level width not found — DestroyCell capture disabled");
            return -1;
        }

        // ---------------------------------------------------------------- capture

        [HarmonyPatch]
        internal static class CaptureCellChanges
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var m in typeof(Level).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (m.Name == "SetCell" || m.Name == "DestroyCell")
                        yield return m;
            }

            private static void Postfix(Level __instance, MethodBase __originalMethod, object[] __args)
            {
                if (!NetSession.Active || _applying) return;
                var session = NetSession.Instance;
                if (session.State != SessionState.InGame) return;
                try
                {
                    var ps = __originalMethod.GetParameters();
                    // Trailing int parameter is the changeSource on both methods when present.
                    if (ps.Length >= 3 && ps[ps.Length - 1].ParameterType == typeof(int)
                        && (int)__args[ps.Length - 1] == ChangeSourceNet) return;

                    int index;
                    if (__originalMethod.Name == "SetCell")
                    {
                        if (ps.Length < 2 || ps[1].ParameterType != typeof(byte)) return;
                        if (ps[0].ParameterType == typeof(int))
                            index = (int)__args[0];
                        else if (ps[0].ParameterType == typeof(UnityEngine.Vector2Int) && _width > 0)
                        {
                            var v = (UnityEngine.Vector2Int)__args[0];
                            index = v.y * _width + v.x;
                        }
                        else return;
                        Pending[index] = (byte)__args[1];
                    }
                    else // DestroyCell(x, y, ...)
                    {
                        if (_width <= 0 && Level != null) { } // ensure width resolution attempted
                        if (_width <= 0 || ps.Length < 2 || ps[0].ParameterType != typeof(int) || ps[1].ParameterType != typeof(int)) return;
                        index = (int)__args[1] * _width + (int)__args[0];
                        Pending[index] = ReadCellType(__instance, index);
                    }
                }
                catch { }
            }
        }

        private static byte ReadCellType(Level level, int index)
        {
            try
            {
                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (cells is Unity.Collections.NativeArray<byte> native && native.IsCreated && index >= 0 && index < native.Length)
                    return native[index];
            }
            catch { }
            return 0;
        }

        // ---------------------------------------------------------------- flush / apply

        // Record of every cell changed since generation — the catch-up source for rejoins and
        // resume streaming. Kept on EVERY machine so a migration-promoted host can serve too.
        private static readonly Dictionary<int, byte> Ledger = new Dictionary<int, byte>();

        // Received cells waiting for their reflection SetCell, drained at a bounded rate so a
        // burst of catch-up chunks can never hitch a frame. Single queue = arrival order is
        // preserved, so a live diff can't be overtaken by an older chunk touching the same cell.
        private static readonly Queue<(int index, byte type)> ApplyQueue = new Queue<(int, byte)>();
        private const int MaxApplyPerFrame = 20000;

        /// <summary>Unordered copy for the run save (no consumer needs sorted cells).</summary>
        public static List<(int index, byte type)> LedgerSnapshot()
        {
            var list = new List<(int index, byte type)>(Ledger.Count);
            foreach (var kv in Ledger) list.Add((kv.Key, kv.Value));
            return list;
        }

        /// <summary>Called once per frame from NetSession.Update while InGame.</summary>
        public static void Flush(NetSession session)
        {
            if (Pending.Count == 0) return;
            var cells = Pending.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
            Pending.Clear();
            foreach (var (index, type) in cells)
                Ledger[index] = type;
            Writer.Reset();
            new CellDiffMsg { Cells = cells }.Write(Writer);
            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
        }

        public static void Apply(CellDiffMsg msg)
        {
            foreach (var (index, type) in msg.Cells)
            {
                Ledger[index] = type;
                ApplyQueue.Enqueue((index, type));
            }
        }

        /// <summary>Called once per frame from NetSession.Update while InGame: applies queued
        /// cells (bounded) and advances any catch-up streams this machine is hosting.</summary>
        public static void Tick(NetSession session)
        {
            DrainApplyQueue();
            TickStreams(session);
        }

        private static void DrainApplyQueue()
        {
            if (ApplyQueue.Count == 0) return;
            var level = Level;
            if (level == null) return; // level regenerating — cells apply once it exists
            _applying = true;
            try
            {
                int n = Math.Min(MaxApplyPerFrame, ApplyQueue.Count);
                for (int i = 0; i < n; i++)
                {
                    var (index, type) = ApplyQueue.Dequeue();
                    if (_setCellByIndex != null)
                        _setCellByIndex.Invoke(level, new object[] { index, type, ChangeSourceNet });
                    else if (_setCellByVec != null && _width > 0)
                        _setCellByVec.Invoke(level, new object[] { new UnityEngine.Vector2Int(index % _width, index / _width), type, ChangeSourceNet });
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[World] cell apply failed: {e.Message}");
            }
            finally
            {
                _applying = false;
            }
        }

        // ---------------------------------------------------------------- catch-up streaming
        //
        // The ledger can reach map scale (mass cell conversion), far too big to blast at a
        // joiner in one frame. Instead the host streams it in 64x64-cell chunks, always sending
        // the chunk nearest the target player's CURRENT position first — the world is correct
        // around the player within a second or two, and the rest of the map fills in behind a
        // per-frame byte budget with transport backpressure. Chunk payloads are built from the
        // live ledger at send time and travel as ordinary CellDiffs on the reliable, ordered
        // Events channel, so later live diffs can never be overtaken by stale chunk data.

        private const int ChunkSize = 64;
        private const int LinearChunkShift = 12;            // width unknown: 4096-cell runs
        private const int StreamBudgetPerFrame = 8 * 1024;  // per peer, ~480 KB/s at 60 fps

        private sealed class StreamState
        {
            public byte Slot;                 // player whose position drives chunk priority
            public HashSet<int> PendingChunks;
            public int SentChunks;
            public int SentCells;
            public UnityEngine.Vector2 Fallback; // target position before their ship exists
        }

        private static readonly Dictionary<ulong, StreamState> Streams = new Dictionary<ulong, StreamState>();
        private static readonly List<ulong> StreamPeersScratch = new List<ulong>();
        private static readonly NetWriter StreamWriter = new NetWriter(16 * 1024);
        private static bool _syncToastActive;

        private static int ChunkKey(int index)
        {
            if (_width <= 0) return index >> LinearChunkShift;
            int chunksPerRow = (_width + ChunkSize - 1) / ChunkSize;
            return (index / _width / ChunkSize) * chunksPerRow + (index % _width / ChunkSize);
        }

        /// <summary>Host: queue the full ledger for delivery to one peer, nearest-first around
        /// the target player. Idempotent per peer — a re-begin restarts the stream.</summary>
        public static void BeginStreamTo(NetSession session, ulong peer, byte slot, UnityEngine.Vector2 fallbackPos)
        {
            var _ = Level; // force width resolution so chunk keys are spatial, not linear
            if (Ledger.Count == 0)
            {
                Streams.Remove(peer);
                return;
            }
            var pending = new HashSet<int>();
            foreach (var index in Ledger.Keys)
                pending.Add(ChunkKey(index));
            Streams[peer] = new StreamState { Slot = slot, PendingChunks = pending, Fallback = fallbackPos };

            StreamWriter.Reset();
            new TerrainSyncMsg { Phase = TerrainSyncMsg.PhaseBegin, Chunks = pending.Count, Cells = Ledger.Count }.Write(StreamWriter);
            session.Transport.Send(peer, NetChannel.Events, StreamWriter.ToSegment(), reliable: true);
            Plugin.Log.LogInfo($"[World] streaming terrain to {peer}: {Ledger.Count} cells in {pending.Count} chunks, nearest-first");
        }

        public static void CancelStream(ulong peer) => Streams.Remove(peer);

        private static void TickStreams(NetSession session)
        {
            if (Streams.Count == 0) return;
            if (!session.IsHost) { Streams.Clear(); return; }

            StreamPeersScratch.Clear();
            StreamPeersScratch.AddRange(Streams.Keys);
            foreach (var peer in StreamPeersScratch)
            {
                var stream = Streams[peer];
                var (px, py) = TargetCell(stream);
                int budget = StreamBudgetPerFrame;
                while (budget > 0 && stream.PendingChunks.Count > 0)
                {
                    int key = NearestChunk(stream.PendingChunks, px, py);
                    var cells = CollectChunk(key);
                    if (cells.Count == 0) { stream.PendingChunks.Remove(key); continue; }
                    StreamWriter.Reset();
                    new CellDiffMsg { Cells = cells }.Write(StreamWriter);
                    var segment = StreamWriter.ToSegment();
                    if (!session.Transport.Send(peer, NetChannel.Events, segment, reliable: true))
                        break; // send buffer full — this chunk retries next frame
                    stream.PendingChunks.Remove(key);
                    stream.SentChunks++;
                    stream.SentCells += cells.Count;
                    budget -= segment.Count;
                }
                if (stream.PendingChunks.Count == 0)
                {
                    StreamWriter.Reset();
                    new TerrainSyncMsg { Phase = TerrainSyncMsg.PhaseEnd, Chunks = stream.SentChunks, Cells = stream.SentCells }.Write(StreamWriter);
                    if (session.Transport.Send(peer, NetChannel.Events, StreamWriter.ToSegment(), reliable: true))
                    {
                        Streams.Remove(peer);
                        Plugin.Log.LogInfo($"[World] terrain stream to {peer} complete ({stream.SentCells} cells in {stream.SentChunks} chunks)");
                    }
                    // else: buffer full — retry the end marker next frame
                }
            }
        }

        private static (int x, int y) TargetCell(StreamState stream)
        {
            // 1 cell = 1 world unit (Level.ConvertCells compares world positions to cell
            // coordinates directly), so a ship position IS a cell position.
            try
            {
                if (ShipSync.ShipsBySlot.TryGetValue(stream.Slot, out var ship) && ship != null)
                {
                    var p = ship.transform.position;
                    return ((int)p.x, (int)p.y);
                }
            }
            catch { }
            return ((int)stream.Fallback.x, (int)stream.Fallback.y);
        }

        private static int NearestChunk(HashSet<int> pending, int px, int py)
        {
            if (_width <= 0)
            {
                foreach (var key in pending) return key; // no spatial keys — any order converges
                return -1;
            }
            int chunksPerRow = (_width + ChunkSize - 1) / ChunkSize;
            long best = long.MaxValue;
            int bestKey = -1;
            foreach (var key in pending)
            {
                long dx = (key % chunksPerRow) * ChunkSize + ChunkSize / 2 - px;
                long dy = (key / chunksPerRow) * ChunkSize + ChunkSize / 2 - py;
                long d = dx * dx + dy * dy;
                if (d < best) { best = d; bestKey = key; }
            }
            return bestKey;
        }

        /// <summary>Ledger cells of one chunk, ascending index order (CellDiff delta encoding).</summary>
        private static List<(int index, byte type)> CollectChunk(int key)
        {
            var cells = new List<(int index, byte type)>(256);
            if (_width <= 0)
            {
                int first = key << LinearChunkShift;
                for (int index = first; index < first + (1 << LinearChunkShift); index++)
                    if (Ledger.TryGetValue(index, out var type))
                        cells.Add((index, type));
                return cells;
            }
            int chunksPerRow = (_width + ChunkSize - 1) / ChunkSize;
            int x0 = (key % chunksPerRow) * ChunkSize;
            int y0 = (key / chunksPerRow) * ChunkSize;
            int columns = Math.Min(ChunkSize, _width - x0); // edge chunks must not wrap rows
            for (int y = y0; y < y0 + ChunkSize; y++)
            {
                int rowBase = y * _width + x0;
                for (int x = 0; x < columns; x++)
                    if (Ledger.TryGetValue(rowBase + x, out var type))
                        cells.Add((rowBase + x, type));
            }
            return cells;
        }

        /// <summary>Client: begin/end markers around an incoming catch-up stream.</summary>
        public static void ApplyTerrainSync(TerrainSyncMsg msg)
        {
            if (msg.Phase == TerrainSyncMsg.PhaseBegin)
            {
                Plugin.Log.LogInfo($"[World] terrain sync incoming: {msg.Cells} cells in {msg.Chunks} chunks");
                if (msg.Cells > 100000)
                {
                    _syncToastActive = true;
                    UI.Toast.Show("SYNCING TERRAIN...", 6f);
                }
            }
            else
            {
                Plugin.Log.LogInfo($"[World] terrain sync complete ({msg.Cells} cells in {msg.Chunks} chunks)");
                if (_syncToastActive)
                {
                    _syncToastActive = false;
                    UI.Toast.Show("TERRAIN SYNCED", 3f);
                }
            }
        }
    }
}
