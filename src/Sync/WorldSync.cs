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

        // Record of every cell changed since generation — the rejoin catch-up source. Kept on
        // EVERY machine so a migration-promoted host can serve rejoins too.
        private static readonly Dictionary<int, byte> Ledger = new Dictionary<int, byte>();

        public static List<(int index, byte type)> LedgerSnapshot()
            => Ledger.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();

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
                Ledger[index] = type;
            var level = Level;
            if (level == null) return;
            _applying = true;
            try
            {
                foreach (var (index, type) in msg.Cells)
                {
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
    }
}
