using System;
using System.IO;
using System.IO.Compression;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using Sirenix.Serialization;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Keeps puppet ships' builds in sync with their owners: every few seconds the local ship's
    /// module-grid memento (the game's own save format — Odin-serialized, registry-rehydrated)
    /// is compared against the last sent copy and broadcast on change. Receivers restore it on
    /// the puppet's grid; ModuleSlotWeaponHolder then rebuilds the visible weapons automatically.
    /// </summary>
    internal static class ModuleGridSync
    {
        private const float CheckInterval = 5f;

        private static float _nextCheckAt;
        private static byte[] _lastSent;
        private static bool _warnedNoGrid;
        private static readonly NetWriter Writer = new NetWriter(4096);

        public static void Reset()
        {
            _nextCheckAt = 0;
            _lastSent = null;
            _warnedNoGrid = false;
        }

        /// <summary>A peer (re)joined: its puppet of our ship was built from prefab defaults.
        /// Drop the change gate so the next tick resends our current build to everyone.</summary>
        public static void ForceRebroadcast()
        {
            _lastSent = null;
            _nextCheckAt = 0f;
        }

        public static void Tick(NetSession session)
        {
            if (Time.unscaledTime < _nextCheckAt || ShipSync.LocalShip == null) return;
            _nextCheckAt = Time.unscaledTime + CheckInterval;
            try
            {
                var bytes = SerializeGrid(ShipSync.LocalShip);
                if (bytes == null)
                {
                    // A silent null here is how this sync stayed dead for weeks — say so once.
                    if (!_warnedNoGrid)
                    {
                        _warnedNoGrid = true;
                        Plugin.Log.LogWarning("[Grid] local ship has no bound module grid — build sync inactive");
                    }
                    return;
                }
                if (_lastSent != null && SameBytes(bytes, _lastSent)) return;
                _lastSent = bytes;

                // The raw Odin memento is ~95KB of mostly-default slot data — over the UDP
                // datagram limit (loopback sends failed with MessageSize). Gzip gets it down
                // to a few KB; both ends always run the same mod version, so no back-compat.
                var packed = Compress(bytes);
                if (packed.Length > 60000)
                    Plugin.Log.LogWarning($"[Grid] compressed grid is {packed.Length} bytes — nearing the datagram limit");
                Writer.Reset();
                Writer.WriteMsgType(MsgType.ModuleGridState);
                Writer.WriteByte((byte)session.LocalSlot);
                Writer.WriteBytes(packed, 0, packed.Length);
                session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                Plugin.Log.LogInfo($"[Grid] module grid broadcast ({bytes.Length} -> {packed.Length} bytes)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Grid] serialize failed: {e.Message}");
                _nextCheckAt = Time.unscaledTime + 30f; // don't spam on persistent failure
            }
        }

        public static void Apply(byte slot, byte[] bytes)
        {
            var session = NetSession.Instance;
            if (slot == session.LocalSlot) return; // our own echo
            if (!ShipSync.ShipsBySlot.TryGetValue(slot, out var ship) || ship == null) return;
            if (ship.GetComponent<RemotePuppet>() == null) return;
            try
            {
                // Typed access — the old reflection looked up a "Data" property that does not
                // exist (SavableComponent exposes ComponentData), so this sync never ran.
                var data = ship.ModuleGridOwner != null ? ship.ModuleGridOwner.ComponentData : null;
                if (data == null)
                {
                    Plugin.Log.LogWarning($"[Grid] P{slot + 1} puppet has no bound module grid — apply skipped");
                    return;
                }
                var memento = SerializationUtility.DeserializeValueWeak(Decompress(bytes), DataFormat.Binary)
                    as ModuleGridOwner.Data.Memento;
                if (memento == null)
                {
                    Plugin.Log.LogWarning($"[Grid] P{slot + 1} grid memento failed to deserialize ({bytes.Length} bytes)");
                    return;
                }
                data.RestoreFromMemento(memento);
                Plugin.Log.LogInfo($"[Grid] applied P{slot + 1}'s module grid ({bytes.Length} bytes)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Grid] apply failed: {e.Message}");
            }
        }

        private static byte[] SerializeGrid(Ship ship)
        {
            var data = ship.ModuleGridOwner != null ? ship.ModuleGridOwner.ComponentData : null;
            if (data == null) return null;
            var memento = data.CreateMemento();
            return memento == null ? null : SerializationUtility.SerializeValueWeak(memento, DataFormat.Binary);
        }

        private static byte[] Compress(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest))
                    gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        private static byte[] Decompress(byte[] packed)
        {
            using (var src = new MemoryStream(packed))
            using (var gz = new GZipStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                gz.CopyTo(dst);
                return dst.ToArray();
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }
    }
}
