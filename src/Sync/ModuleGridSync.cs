using System;
using HarmonyLib;
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
        private static readonly NetWriter Writer = new NetWriter(4096);

        public static void Reset()
        {
            _nextCheckAt = 0;
            _lastSent = null;
        }

        public static void Tick(NetSession session)
        {
            if (Time.unscaledTime < _nextCheckAt || ShipSync.LocalShip == null) return;
            _nextCheckAt = Time.unscaledTime + CheckInterval;
            try
            {
                var bytes = SerializeGrid(ShipSync.LocalShip);
                if (bytes == null) return;
                if (_lastSent != null && SameBytes(bytes, _lastSent)) return;
                _lastSent = bytes;

                Writer.Reset();
                Writer.WriteMsgType(MsgType.ModuleGridState);
                Writer.WriteByte((byte)session.LocalSlot);
                Writer.WriteBytes(bytes, 0, bytes.Length);
                session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                Plugin.Log.LogInfo($"[Grid] module grid broadcast ({bytes.Length} bytes)");
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
                var gridOwner = Traverse.Create(ship).Field("moduleGridOwner").GetValue();
                var data = gridOwner != null ? Traverse.Create(gridOwner).Property("Data").GetValue() : null;
                if (data == null) return;
                var memento = SerializationUtility.DeserializeValueWeak(bytes, DataFormat.Binary);
                if (memento == null) return;
                var restore = AccessTools.Method(data.GetType(), "RestoreFromMemento");
                if (restore == null)
                {
                    Plugin.Log.LogWarning("[Grid] RestoreFromMemento not found");
                    return;
                }
                restore.Invoke(data, new[] { memento });
                Plugin.Log.LogInfo($"[Grid] applied P{slot + 1}'s module grid ({bytes.Length} bytes)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Grid] apply failed: {e.Message}");
            }
        }

        private static byte[] SerializeGrid(Ship ship)
        {
            var gridOwner = Traverse.Create(ship).Field("moduleGridOwner").GetValue();
            var data = gridOwner != null ? Traverse.Create(gridOwner).Property("Data").GetValue() : null;
            if (data == null) return null;
            var create = AccessTools.Method(data.GetType(), "CreateMemento");
            var memento = create?.Invoke(data, null);
            return memento == null ? null : SerializationUtility.SerializeValueWeak(memento, DataFormat.Binary);
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
