using System;
using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Shared map exploration. Every few seconds each client scans Level.fogLevels against its
    /// last snapshot and broadcasts the changed cells as RLE runs; receivers write the values
    /// into their own fogLevels AND their snapshot (the echo guard — applied cells don't get
    /// re-detected as local changes). Cells revealed by exactly one player converge trivially;
    /// jointly explored cells converge to the same values anyway.
    /// </summary>
    internal static class FogSync
    {
        private const float ScanInterval = 4f;
        private const int MaxRunsPerMessage = 256;

        private static float _nextScanAt;
        private static byte[] _snapshot;
        private static readonly NetWriter Writer = new NetWriter(8 * 1024);
        private static bool _loggedFirst;

        public static void Reset()
        {
            _nextScanAt = 0;
            _snapshot = null;
            _loggedFirst = false;
        }

        private static Unity.Collections.NativeArray<byte> GetFog(out bool ok)
        {
            ok = false;
            try
            {
                var level = ServiceLocator.Get<Level>();
                var fog = Traverse.Create(level).Field("fogLevels").GetValue();
                if (fog is Unity.Collections.NativeArray<byte> native && native.IsCreated)
                {
                    ok = true;
                    return native;
                }
            }
            catch { }
            return default;
        }

        public static void Tick(NetSession session)
        {
            if (!NetConfig.ShareMapExploration.Value || Time.unscaledTime < _nextScanAt) return;
            _nextScanAt = Time.unscaledTime + ScanInterval;

            var fog = GetFog(out bool ok);
            if (!ok) return;

            if (_snapshot == null || _snapshot.Length != fog.Length)
            {
                _snapshot = fog.ToArray(); // first scan: baseline only, nothing to send
                return;
            }

            // Collect changed runs: (startIndex, values...).
            var runs = new List<(int start, List<byte> values)>();
            int i = 0;
            int length = fog.Length;
            while (i < length)
            {
                if (fog[i] == _snapshot[i]) { i++; continue; }
                var values = new List<byte>(32);
                int start = i;
                while (i < length && fog[i] != _snapshot[i])
                {
                    byte v = fog[i];
                    values.Add(v);
                    _snapshot[i] = v;
                    i++;
                }
                runs.Add((start, values));
            }
            if (runs.Count == 0) return;

            for (int offset = 0; offset < runs.Count; offset += MaxRunsPerMessage)
            {
                int count = Math.Min(MaxRunsPerMessage, runs.Count - offset);
                Writer.Reset();
                Writer.WriteMsgType(MsgType.FogDiff);
                Writer.WriteVarUInt((uint)count);
                int prevEnd = 0;
                for (int r = 0; r < count; r++)
                {
                    var (start, values) = runs[offset + r];
                    Writer.WriteVarUInt((uint)(start - prevEnd));
                    Writer.WriteVarUInt((uint)values.Count);
                    foreach (var v in values) Writer.WriteByte(v);
                    prevEnd = start + values.Count;
                }
                session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
            }
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Plugin.Log.LogInfo($"[Fog] first exploration diff sent ({runs.Count} runs)");
            }
        }

        public static void Apply(NetReader reader)
        {
            var fog = GetFog(out bool ok);
            if (!ok) return;
            if (_snapshot == null || _snapshot.Length != fog.Length) _snapshot = fog.ToArray();

            int count = (int)reader.ReadVarUInt();
            int index = 0;
            for (int r = 0; r < count; r++)
            {
                index += (int)reader.ReadVarUInt();
                int runLength = (int)reader.ReadVarUInt();
                for (int j = 0; j < runLength; j++)
                {
                    byte v = reader.ReadByte();
                    if (index >= 0 && index < fog.Length)
                    {
                        fog[index] = v;
                        _snapshot[index] = v; // echo guard
                    }
                    index++;
                }
            }
        }
    }
}
