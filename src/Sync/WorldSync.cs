using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

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
        private static byte[] _baseline;
        private static uint _revision;
        private static ulong _ledgerHash;
        private static float _nextDigestAt;
        private static bool _repairInProgress;
        // WS2.1 full-array backstop. Host caches a whole-array checksum on a slow cadence (it is O(cells))
        // and only advertises it while it still matches the live revision. Client confirms a mismatch
        // across two consecutive digests before repairing, so a one-frame in-flight difference can't
        // trigger a spurious resync.
        private static float _nextFullHashAt;
        private static ulong _cachedFullHash;
        private static uint _cachedFullHashRevision;
        private static long _fullMismatchRevision = -1; // client: revision of the first-seen full-array
                                                        // mismatch. -1 sentinel, NOT 0: revision 0 is a
                                                        // real early-run value, and a 0 default made the
                                                        // confirm-twice filter confirm on FIRST sighting
                                                        // at rev 0 (caught live on the coordinator)
        private const float FullHashInterval = 30f;
        // Fog cells live in cellTypes but evolve through the game's own gas sim — an uncaptured,
        // un-ledgered mutation path that is only CONSISTENT between peers running comparable fog
        // sims (a headless coordinator, or a throttled/minimized host, is not). The full-array
        // backstop therefore hashes with fog-type bytes masked to 0 on BOTH sides: it verifies
        // exactly the terrain the diff/ledger system owns, nothing the fog sim owns.
        private static byte _fogCellId;
        private static bool _fogIdResolved;
        private static bool _fogIdWarned;

        internal static int PendingCount => Pending.Count;

        public static void Reset()
        {
            Pending.Clear();
            Ledger.Clear();
            ApplyQueue.Clear();
            Streams.Clear();
            _syncToastActive = false;
            _warnedCellApply = false;
            _level = null;
            _setCellByIndex = null;
            _setCellByVec = null;
            _width = -1;
            _applying = false;
            _baseline = null;
            _revision = 0;
            _ledgerHash = 0;
            _nextDigestAt = 0;
            _repairInProgress = false;
            _nextFullHashAt = 0;
            _cachedFullHash = 0;
            _cachedFullHashRevision = 0;
            _fullMismatchRevision = -1;
            _coordinatorDirectWrites = 0;
            _warnedDirectWrites = false;
            _fogCellId = 0;
            _fogIdResolved = false;
            _fogIdWarned = false;
        }

        internal static uint Revision => _revision;
        internal static ulong LedgerHash => _ledgerHash;
        internal static int LedgerCount => Ledger.Count;

        public static void CaptureBaseline(Level level)
        {
            try
            {
                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (cells is Unity.Collections.NativeArray<byte> native && native.IsCreated)
                {
                    _baseline = native.ToArray();
                    Plugin.Log.LogInfo($"[World] terrain baseline captured ({_baseline.Length} cells)");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[World] baseline capture failed: {e.Message}"); }
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
                var profile = PatchProfiler.Enter(PatchId.WorldCaptureCellChanges);
                try
                {
                    if (!NetSession.Active || _applying) return;
                    var session = NetSession.Instance;
                    if (session.State != SessionState.InGame) return;
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
                        InstrumentationCounters.CellCaptured();
                    }
                    else // DestroyCell(x, y, ...)
                    {
                        if (_width <= 0 && Level != null) { } // ensure width resolution attempted
                        if (_width <= 0 || ps.Length < 2 || ps[0].ParameterType != typeof(int) || ps[1].ParameterType != typeof(int)) return;
                        index = (int)__args[1] * _width + (int)__args[0];
                        Pending[index] = ReadCellType(__instance, index);
                        InstrumentationCounters.CellCaptured();
                    }
                }
                catch { }
                finally { PatchProfiler.Exit(PatchId.WorldCaptureCellChanges, profile); }
            }
        }

        // Neighbour-cascade cell systems (AutoPopper chain-pops, CellRegrower regrows) seed from
        // Level.CellChanged and skip the game's OWN cascade sources (auto-pop 1324, burn 15324) so
        // cascades don't re-trigger each other. Replicated changes carry ChangeSourceNet, which
        // those systems DON'T skip — so on the receiver every replicated auto-pop looked like a
        // fresh player pop, re-scanned its neighbours, re-cascaded, and fed the new destructions
        // back to the sender: an expanding host<->client pop storm that froze the client (the
        // originator, which ran its own correct local cascade on top). The originator already
        // simulates the whole cascade and replicates every destroyed cell, so a receiver's copy
        // must stay inert. This is the double-cascade suppression the class summary always claimed
        // but never actually had — extend the vanilla skip-list to include ChangeSourceNet.
        // WS2.1(b): this list is VERIFIED EXHAUSTIVE, not a guess. A decompile of Punk.Main finds
        // exactly five OnCellChanged(Level.CellChange) handlers — AutoPopper, CellRegrower, FogManager,
        // LevelChangeBuffer, CellInfoManager — and only the first two MUTATE cells (auto-pop cascade /
        // deferred regrow). The other three touch fog metadata, a change buffer, and visual overlays;
        // suppressing them for replicated changes would drop legitimate reactions, so they stay live.
        // The remaining silent-divergence tail (e.g. RNG-timed cascades) is caught by the full-array
        // digest backstop in ApplyDigest, not by adding speculative entries here.
        [HarmonyPatch]
        internal static class SuppressNetCellCascade
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var typeName in new[] { "AutoPopper", "CellRegrower" })
                {
                    var t = AccessTools.TypeByName(typeName);
                    var m = t != null ? AccessTools.Method(t, "OnCellChanged") : null;
                    if (m != null) yield return m;
                }
            }

            private static FieldInfo _changeSourceField;

            // __0 is a Level.CellChange struct (boxed here); read its changeSource and bail on ours.
            private static bool Prefix(object __0)
            {
                var profile = PatchProfiler.Enter(PatchId.WorldSuppressNetCellCascade);
                try
                {
                    if (!NetSession.Active || __0 == null) return true;
                    if (_changeSourceField == null)
                        _changeSourceField = AccessTools.Field(__0.GetType(), "changeSource");
                    if (_changeSourceField != null && (int)_changeSourceField.GetValue(__0) == ChangeSourceNet)
                        return false; // replicated change — do not seed a local cascade
                }
                catch { }
                finally { PatchProfiler.Exit(PatchId.WorldSuppressNetCellCascade, profile); }
                return true;
            }
        }

        /// <summary>Full-array FNV over cellTypes with fog-type bytes masked to 0 — the backstop
        /// verifies the terrain the diff/ledger system owns, never the fog sim's cells (which evolve
        /// through an uncaptured engine path and are only symmetric between peers running comparable
        /// sims). Returns 0 (checks skip, fail-safe) until the fog id resolves; if the level simply
        /// has no FogManager, hashes unmasked — no fog system means no fog asymmetry.</summary>
        private static ulong ComputeMaskedLevelHash()
        {
            var level = Level;
            if (level == null) return 0;
            if (!_fogIdResolved)
            {
                try
                {
                    var mgr = UnityEngine.Object.FindAnyObjectByType<FogManager>();
                    if (mgr != null)
                    {
                        var fogCellType = Traverse.Create(mgr).Field("fogCellType").GetValue() as CellType;
                        if (fogCellType == null)
                        {
                            if (!_fogIdWarned)
                            {
                                _fogIdWarned = true;
                                Plugin.Log.LogWarning("[World] fogCellType unreadable — full-array backstop disabled");
                            }
                            return 0;
                        }
                        _fogCellId = fogCellType.id;
                    }
                    else _fogCellId = 0; // no fog system on this level: 0 is never a maskable type here
                    _fogIdResolved = true;
                }
                catch { return 0; }
            }
            try
            {
                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (!(cells is Unity.Collections.NativeArray<byte> native) || !native.IsCreated) return 0;
                ulong hash = 14695981039346656037UL;
                byte fogId = _fogCellId;
                for (int i = 0; i < native.Length; i++)
                {
                    byte b = native[i];
                    if (fogId != 0 && b == fogId) b = 0;
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
                return hash == 0 ? 1UL : hash; // 0 is the "not computed" sentinel on the wire
            }
            catch { return 0; }
        }

        private static long _coordinatorDirectWrites;
        private static bool _warnedDirectWrites;

        // NativeArray<byte> is a struct VIEW over native memory: the boxed copy Traverse hands us
        // writes through to the same underlying buffer, exactly like ReadCellType reads through it.
        private static void WriteCellDirect(Level level, int index, byte type)
        {
            try
            {
                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (cells is Unity.Collections.NativeArray<byte> native && native.IsCreated
                    && index >= 0 && index < native.Length)
                    native[index] = type;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[World] direct cell write failed: {e.Message}");
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

        // Live-diff budget: a mass-conversion wave can touch tens of thousands of cells per
        // frame — broadcasting them all at once floods the reliable channel (and the outbox
        // behind it). Send a bounded slice per frame; the remainder stays in Pending, where a
        // later change to the same cell just overwrites (only the final value ships).
        private const int MaxLiveCellsPerFrame = 2500; // ~5.5 KB/frame, ~330 KB/s at 60 fps
        private static readonly Comparison<(int index, byte type)> ByIndex
            = (a, b) => a.index.CompareTo(b.index);

        /// <summary>Called once per frame from NetSession.Update while InGame.</summary>
        public static void Flush(NetSession session)
        {
            if (Pending.Count == 0) return;
            var cells = new List<(int index, byte type)>(Math.Min(Pending.Count, MaxLiveCellsPerFrame));
            foreach (var kv in Pending)
            {
                cells.Add((kv.Key, kv.Value));
                if (cells.Count >= MaxLiveCellsPerFrame) break;
            }
            foreach (var (index, _) in cells)
                Pending.Remove(index);
            cells.Sort(ByIndex); // CellDiff delta encoding needs ascending indexes
            uint revision = 0;
            if (session.IsHost)
            {
                revision = ++_revision;
                foreach (var (index, type) in cells) RecordLedger(index, type);
                InstrumentationCounters.TerrainRevisionCommitted();
            }
            Writer.Reset();
            new CellDiffMsg { Revision = revision, Cells = cells }.Write(Writer);
            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
        }

        public static void Apply(CellDiffMsg msg)
        {
            foreach (var (index, type) in msg.Cells)
            {
                RecordLedger(index, type);
                ApplyQueue.Enqueue((index, type));
            }
        }

        public static CellDiffMsg AcceptProposal(CellDiffMsg proposal)
        {
            proposal.Revision = ++_revision;
            Apply(proposal);
            InstrumentationCounters.TerrainRevisionCommitted();
            return proposal;
        }

        public static void ApplyCanonical(CellDiffMsg msg)
        {
            if (msg.Revision == 0) { Apply(msg); return; }
            if (msg.Revision <= _revision) return;
            if (msg.Revision != _revision + 1)
                Plugin.Log.LogWarning($"[World] terrain revision gap {_revision}->{msg.Revision}; reliable ordering should prevent this");
            Apply(msg);
            _revision = msg.Revision;
        }

        private static void RecordLedger(int index, byte type)
        {
            if (Ledger.TryGetValue(index, out byte old)) _ledgerHash ^= CellToken(index, old);
            if (_baseline != null && index >= 0 && index < _baseline.Length && _baseline[index] == type)
            {
                Ledger.Remove(index);
                return;
            }
            Ledger[index] = type;
            _ledgerHash ^= CellToken(index, type);
        }

        private static ulong CellToken(int index, byte type)
        {
            ulong z = ((ulong)(uint)index << 8) | type;
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Called once per frame from NetSession.Update while InGame: applies queued
        /// cells (bounded) and advances any catch-up streams this machine is hosting.</summary>
        public static void Tick(NetSession session)
        {
            DrainApplyQueue();
            TickStreams(session);
            if (session.IsHost && Time.unscaledTime >= _nextDigestAt)
            {
                _nextDigestAt = Time.unscaledTime + 10f;
                // Refresh the whole-array checksum on its slow cadence, stamped with the revision it
                // was taken at; only advertise it when that still matches the live revision (else the
                // ledgers-agree comparison on the client would race an in-flight change). The
                // ApplyQueue guard is NOT optional: remote cells are ledgered (revision++) on receipt
                // but applied to the array over budgeted frames — hashing while any are queued stamps
                // a PRE-burst array with a POST-burst revision, and every viewer then "confirms" a
                // phantom silent divergence (found live on the coordinator, where ALL combat cells
                // arrive as remote bursts and the race window is essentially always open).
                if (Pending.Count == 0 && ApplyQueue.Count == 0 && Time.unscaledTime >= _nextFullHashAt)
                {
                    _nextFullHashAt = Time.unscaledTime + FullHashInterval;
                    _cachedFullHash = ComputeMaskedLevelHash();
                    _cachedFullHashRevision = _revision;
                }
                ulong fullHash = _cachedFullHashRevision == _revision ? _cachedFullHash : 0UL;
                Writer.Reset();
                new TerrainDigestMsg { Revision = _revision, Hash = _ledgerHash, Count = Ledger.Count, FullHash = fullHash }.Write(Writer);
                session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                InstrumentationCounters.TerrainDigestSent();
            }
        }

        public static void ApplyDigest(TerrainDigestMsg msg, NetSession session)
        {
            if (session.IsHost || _repairInProgress || msg.Revision > _revision) return; // ordered event lane will catch up first
            bool ledgerAgrees = msg.Revision == _revision && msg.Hash == _ledgerHash && msg.Count == Ledger.Count;
            if (!ledgerAgrees)
            {
                _fullMismatchRevision = -1;
                InstrumentationCounters.TerrainMismatch();
                Plugin.Log.LogWarning($"[World] TERRAIN LEDGER MISMATCH local={_revision}/{_ledgerHash:X16}/{Ledger.Count} host={msg.Revision}/{msg.Hash:X16}/{msg.Count}; requesting repair");
                RequestTerrainRepair(session, fullReset: false);
                return;
            }
            // Ledgers agree — check the full array for a silently-diverged cell (one a reactive cascade
            // wrote while _applying, dropped from the ledger). Only act when the whole array is quiescent
            // (nothing queued or batched) and confirm across two consecutive digests to filter transients.
            if (msg.FullHash == 0UL || ApplyQueue.Count > 0 || Pending.Count > 0) { _fullMismatchRevision = -1; return; }
            ulong localFull = ComputeMaskedLevelHash();
            if (localFull == 0UL) { _fullMismatchRevision = -1; return; } // fog id not resolvable yet
            if (localFull == msg.FullHash) { _fullMismatchRevision = -1; return; }
            if (_fullMismatchRevision != msg.Revision)
            {
                // First sighting — remember it; a genuine divergence persists to the next digest.
                _fullMismatchRevision = msg.Revision;
                Plugin.Log.LogWarning($"[World] terrain full-array mismatch (ledgers agree) local={localFull:X16} host={msg.FullHash:X16} rev={msg.Revision}; confirming next digest");
                return;
            }
            _fullMismatchRevision = -1;
            InstrumentationCounters.TerrainMismatch();
            Plugin.Log.LogWarning($"[World] TERRAIN SILENT DIVERGENCE confirmed (ledgers agree, arrays differ) local={localFull:X16} host={msg.FullHash:X16}; full resync");
            RequestTerrainRepair(session, fullReset: true);
        }

        // A full-array divergence can include a cell in neither ledger (a client-only phantom), which a
        // ledger-only repair can't reach. Reset every non-baseline cell to baseline first; the host's
        // repair then re-applies its canonical ledger on top, reconstructing the array exactly.
        private static void RequestTerrainRepair(NetSession session, bool fullReset)
        {
            if (fullReset) RestoreToBaseline();
            _repairInProgress = true;
            Writer.Reset();
            new TerrainRepairRequestMsg { Revision = _revision, Hash = _ledgerHash }.Write(Writer);
            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
        }

        private static void RestoreToBaseline()
        {
            if (_baseline == null || Level == null) return;
            try
            {
                var cells = Traverse.Create(Level).Field("cellTypes").GetValue();
                if (!(cells is Unity.Collections.NativeArray<byte> native) || !native.IsCreated) return;
                int n = Math.Min(native.Length, _baseline.Length);
                for (int i = 0; i < n; i++)
                    if (native[i] != _baseline[i]) ApplyQueue.Enqueue((i, _baseline[i]));
                Ledger.Clear();
                _ledgerHash = 0;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[World] baseline restore failed: {e.Message}"); }
        }

        public static void SendRepair(NetSession session, ulong peer, TerrainRepairRequestMsg request)
        {
            if (!session.IsHost) return;
            var cells = LedgerSnapshot();
            cells.Sort(ByIndex);
            const int chunkSize = 500;
            for (int start = 0; start < cells.Count || start == 0; start += chunkSize)
            {
                int count = Math.Min(chunkSize, cells.Count - start);
                Writer.Reset();
                new TerrainRepairChunkMsg { Revision = _revision, Hash = _ledgerHash, Start = start,
                    Total = cells.Count, Cells = count > 0 ? cells.GetRange(start, count) : new List<(int, byte)>() }.Write(Writer);
                session.SendToPeer(peer, NetChannel.Events, Writer.ToSegment(), reliable: true);
                if (cells.Count == 0) break;
            }
            InstrumentationCounters.TerrainRepairSent();
            Plugin.Log.LogWarning($"[World] terrain repair sent to peer={peer}: {cells.Count} canonical cells at revision {_revision}");
        }

        public static void ApplyRepair(TerrainRepairChunkMsg msg)
        {
            if (msg.Start == 0)
            {
                if (_baseline != null)
                    foreach (var kv in Ledger)
                        if (kv.Key >= 0 && kv.Key < _baseline.Length) ApplyQueue.Enqueue((kv.Key, _baseline[kv.Key]));
                Ledger.Clear(); _ledgerHash = 0;
            }
            foreach (var cell in msg.Cells) { RecordLedger(cell.index, cell.type); ApplyQueue.Enqueue(cell); }
            if (msg.Start + msg.Cells.Count >= msg.Total)
            {
                _revision = msg.Revision;
                if (_ledgerHash != msg.Hash)
                    Plugin.Log.LogError($"[World] terrain repair hash failed {_ledgerHash:X16}!={msg.Hash:X16}");
                else Plugin.Log.LogInfo($"[World] terrain repair applied ({msg.Total} cells, revision {_revision})");
                _repairInProgress = false;
                InstrumentationCounters.TerrainRepairApplied();
            }
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
                    // Coordinator (shipless, headless): no segment is ever loaded, and vanilla
                    // SetCell silently no-ops into unloaded space — 616 ledger cells applied with
                    // zero array effect in the first live test, which made the CORRECT client
                    // "repair" itself against a stale canonical array. Verify the write landed and
                    // fall back to writing the data array directly: the coordinator maintains
                    // canonical DATA, not presentation, and net-sourced cascades are suppressed
                    // everywhere anyway. Read-back is cheap and makes the fallback self-diagnosing.
                    if (NetConfig.IsCoordinator && ReadCellType(level, index) != type)
                    {
                        WriteCellDirect(level, index, type);
                        _coordinatorDirectWrites++;
                        if (!_warnedDirectWrites)
                        {
                            _warnedDirectWrites = true;
                            Plugin.Log.LogInfo("[World] coordinator: SetCell no-ops on unloaded segments — writing cell data directly");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (!_warnedCellApply)
                {
                    _warnedCellApply = true; // once per run; a repeating failure would storm the log
                    Plugin.Log.LogWarning($"[World] cell apply failed: {e.Message} (further failures logged as debug)");
                }
                else Plugin.Log.LogDebug($"[World] cell apply failed: {e.Message}");
            }
            finally
            {
                _applying = false;
            }
        }

        private static bool _warnedCellApply;

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
