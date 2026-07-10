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
    /// Shared map progression: station upgrades and scanner map-reveals replicate to everyone
    /// (the purchaser pays from their own economy — shared progression, per-player price).
    /// If the target entity isn't streamed in locally yet, the event queues and applies on
    /// spawn via EnemySync's spawn hook calling <see cref="ApplyPendingFor"/>.
    /// </summary>
    internal static class ProgressionSync
    {
        private static bool _applyingRemote;
        private static readonly NetWriter Writer = new NetWriter(64);
        private static readonly Dictionary<int, List<uint>> PendingUpgrades = new Dictionary<int, List<uint>>();
        private static readonly HashSet<int> PendingScanners = new HashSet<int>();

        // Host-side event history for rejoin catch-up.
        private static readonly List<(int netId, uint hash)> AppliedUpgrades = new List<(int, uint)>();
        private static readonly HashSet<int> UsedScanners = new HashSet<int>();

        public static List<(int netId, uint hash)> UpgradeSnapshot() => new List<(int, uint)>(AppliedUpgrades);
        public static List<int> ScannerSnapshot() => new List<int>(UsedScanners);

        /// <summary>Most recently upgraded/unlocked station by anyone — the party's respawn
        /// checkpoint. Rejoiners spawn here instead of at the run start.</summary>
        public static int LatestStationNetId { get; private set; }

        // Catch-up ledgers accumulate on EVERY machine (not just the host) so that a client
        // promoted by host migration can serve full catch-up to rejoiners and late joiners.
        public static void RecordUpgrade(int netId, uint hash)
        {
            LatestStationNetId = netId;
            AppliedUpgrades.Add((netId, hash));
        }

        public static void RecordScanner(int netId)
        {
            UsedScanners.Add(netId);
        }

        public static void Reset()
        {
            _applyingRemote = false;
            LatestStationNetId = 0;
            PendingUpgrades.Clear();
            PendingScanners.Clear();
            AppliedUpgrades.Clear();
            UsedScanners.Clear();
            PendingInstruments.Clear();
            UsedInstruments.Clear();
            _discoverablesByHash = null;
        }

        // ---------------------------------------------------------------- station upgrades

        [HarmonyPatch(typeof(Station), "TryInstallUpgrade")]
        internal static class CaptureUpgrade
        {
            private static void Postfix(Station __instance, bool __result, object __0)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote || !__result) return;
                try
                {
                    if (!EnemySync.TryGetNetId(__instance, out int netId)) return;
                    var upgradeName = (__0 as UnityEngine.Object)?.name;
                    if (string.IsNullOrEmpty(upgradeName)) return;
                    uint hash = DamageSync.HashName(upgradeName);
                    RecordUpgrade(netId, hash);
                    Writer.Reset();
                    new StationUpgradeMsg { StationNetId = netId, UpgradeHash = hash }.Write(Writer);
                    session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                    Plugin.Log.LogInfo($"[Progress] station upgrade '{upgradeName}' broadcast (netId {netId})");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Progress] upgrade capture failed: {e.Message}");
                }
            }
        }

        public static void ApplyStationUpgrade(StationUpgradeMsg msg)
        {
            RecordUpgrade(msg.StationNetId, msg.UpgradeHash);
            if (!TryApplyUpgrade(msg.StationNetId, msg.UpgradeHash))
            {
                if (!PendingUpgrades.TryGetValue(msg.StationNetId, out var list))
                    PendingUpgrades[msg.StationNetId] = list = new List<uint>();
                list.Add(msg.UpgradeHash);
                Plugin.Log.LogInfo($"[Progress] station {msg.StationNetId} not spawned — upgrade queued");
            }
        }

        private static bool TryApplyUpgrade(int netId, uint upgradeHash)
        {
            if (!NetIds.TryGetInstanceId(netId, out int instanceId)) return false;
            try
            {
                var egm = ServiceLocator.Get<EntityGameObjectManager>();
                if (!egm.TryGetSavableEntity(instanceId, out var se) || se == null) return false;
                var station = se.GetComponent<Station>();
                if (station == null) return false;

                var upgrade = ResolveUpgrade(upgradeHash);
                if (upgrade == null)
                {
                    Plugin.Log.LogWarning($"[Progress] unknown upgrade hash {upgradeHash:X8}");
                    return true; // don't queue forever
                }
                _applyingRemote = true;
                try
                {
                    // Same effect as a successful purchase, minus the cost: Data.Install drives
                    // lights, map icon, shop unlock and the local respawn cascade.
                    var data = Traverse.Create(station).Property("Data").GetValue()
                               ?? Traverse.Create(station).Field("data").GetValue();
                    var install = data != null ? AccessTools.Method(data.GetType(), "Install") : null;
                    if (install != null)
                    {
                        install.Invoke(data, new[] { (object)upgrade });
                        Plugin.Log.LogInfo($"[Progress] applied remote station upgrade '{upgrade.name}'");
                        return true;
                    }
                    Plugin.Log.LogWarning("[Progress] Station.Data.Install not found");
                    return true;
                }
                finally
                {
                    _applyingRemote = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Progress] upgrade apply failed: {e.Message}");
                return true;
            }
        }

        private static Dictionary<uint, UnityEngine.Object> _upgradesByHash;

        private static UnityEngine.Object ResolveUpgrade(uint hash)
        {
            if (_upgradesByHash == null)
            {
                _upgradesByHash = new Dictionary<uint, UnityEngine.Object>();
                var type = AccessTools.TypeByName("StationUpgrade");
                if (type != null)
                    foreach (var asset in Resources.FindObjectsOfTypeAll(type))
                        _upgradesByHash[DamageSync.HashName(asset.name)] = asset;
            }
            return _upgradesByHash.TryGetValue(hash, out var upgrade) ? upgrade : null;
        }

        // ---------------------------------------------------------------- scanners

        [HarmonyPatch(typeof(Scanner), "OnUseActivated")]
        internal static class CaptureScannerUse
        {
            private static void Postfix(Scanner __instance)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
                try
                {
                    if (!EnemySync.TryGetNetId(__instance, out int netId)) return;
                    RecordScanner(netId);
                    Writer.Reset();
                    new ScannerUsedMsg { NetId = netId }.Write(Writer);
                    session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                    Plugin.Log.LogInfo($"[Progress] scanner used (netId {netId}) — broadcast");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Progress] scanner capture failed: {e.Message}");
                }
            }
        }

        public static void ApplyScannerUsed(ScannerUsedMsg msg)
        {
            RecordScanner(msg.NetId);
            if (!TryApplyScanner(msg.NetId))
            {
                PendingScanners.Add(msg.NetId);
                Plugin.Log.LogInfo($"[Progress] scanner {msg.NetId} not spawned — reveal queued");
            }
        }

        private static bool TryApplyScanner(int netId)
        {
            if (!NetIds.TryGetInstanceId(netId, out int instanceId)) return false;
            try
            {
                var egm = ServiceLocator.Get<EntityGameObjectManager>();
                if (!egm.TryGetSavableEntity(instanceId, out var se) || se == null) return false;
                var scanner = se.GetComponent<Scanner>();
                if (scanner == null) return false;
                _applyingRemote = true;
                try
                {
                    scanner.OnUseActivated(null); // reveals the area + marks Data.isUsed
                    Plugin.Log.LogInfo($"[Progress] applied remote scanner reveal (netId {netId})");
                }
                catch (Exception inner)
                {
                    // Fallback: at least persist the used flag so the world state agrees.
                    Plugin.Log.LogWarning($"[Progress] scanner reveal failed ({inner.Message}) — marking used only");
                    var data = Traverse.Create(scanner).Property("Data").GetValue()
                               ?? Traverse.Create(scanner).Field("data").GetValue();
                    if (data != null) Traverse.Create(data).Field("isUsed").SetValue(true);
                }
                finally
                {
                    _applyingRemote = false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- instruments

        private static readonly Dictionary<int, List<uint>> PendingInstruments = new Dictionary<int, List<uint>>();
        private static readonly List<(int netId, uint hash)> UsedInstruments = new List<(int, uint)>();

        public static List<(int netId, uint hash)> InstrumentSnapshot() => new List<(int, uint)>(UsedInstruments);

        [HarmonyPatch(typeof(Instrument), "Discover")]
        internal static class CaptureInstrumentUse
        {
            private static void Postfix(Instrument __instance, object __0)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
                try
                {
                    if (!EnemySync.TryGetNetId(__instance, out int netId)) return;
                    var name = (__0 as UnityEngine.Object)?.name;
                    if (string.IsNullOrEmpty(name)) return;
                    uint hash = DamageSync.HashName(name);
                    UsedInstruments.Add((netId, hash));
                    Writer.Reset();
                    new InstrumentUsedMsg { NetId = netId, DiscoverableHash = hash }.Write(Writer);
                    session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                    Plugin.Log.LogInfo($"[Progress] instrument used (netId {netId}) — broadcast");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Progress] instrument capture failed: {e.Message}");
                }
            }
        }

        public static void ApplyInstrumentUsed(InstrumentUsedMsg msg)
        {
            UsedInstruments.Add((msg.NetId, msg.DiscoverableHash));
            if (!TryApplyInstrument(msg.NetId, msg.DiscoverableHash))
            {
                if (!PendingInstruments.TryGetValue(msg.NetId, out var list))
                    PendingInstruments[msg.NetId] = list = new List<uint>();
                list.Add(msg.DiscoverableHash);
            }
        }

        private static Dictionary<uint, UnityEngine.Object> _discoverablesByHash;

        private static bool TryApplyInstrument(int netId, uint hash)
        {
            if (!NetIds.TryGetInstanceId(netId, out int instanceId)) return false;
            try
            {
                var egm = ServiceLocator.Get<EntityGameObjectManager>();
                if (!egm.TryGetSavableEntity(instanceId, out var se) || se == null) return false;
                var instrument = se.GetComponent<Instrument>();
                if (instrument == null) return false;

                if (_discoverablesByHash == null)
                {
                    _discoverablesByHash = new Dictionary<uint, UnityEngine.Object>();
                    var t = AccessTools.TypeByName("InstrumentDiscoverable");
                    if (t != null)
                        foreach (var asset in Resources.FindObjectsOfTypeAll(t))
                            _discoverablesByHash[DamageSync.HashName(asset.name)] = asset;
                }
                if (!_discoverablesByHash.TryGetValue(hash, out var discoverable)) return true; // unknown asset — drop

                _applyingRemote = true;
                try
                {
                    var discover = AccessTools.Method(typeof(Instrument), "Discover");
                    discover.Invoke(instrument, new object[] { discoverable, ShipSync.LocalShip });
                    Plugin.Log.LogInfo($"[Progress] applied remote instrument discovery (netId {netId})");
                }
                catch (Exception inner)
                {
                    Plugin.Log.LogWarning($"[Progress] instrument apply failed ({inner.Message}) — marking used only");
                    var data = Traverse.Create(instrument).Property("Data").GetValue()
                               ?? Traverse.Create(instrument).Field("data").GetValue();
                    if (data != null) AccessTools.Method(data.GetType(), "SetUsed")?.Invoke(data, null);
                }
                finally
                {
                    _applyingRemote = false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- deferred application

        /// <summary>Called when an entity streams in — apply any progression events that arrived early.</summary>
        public static void ApplyPendingFor(int netId)
        {
            if (PendingUpgrades.TryGetValue(netId, out var upgrades))
            {
                bool allApplied = true;
                foreach (var hash in upgrades)
                    allApplied &= TryApplyUpgrade(netId, hash);
                if (allApplied) PendingUpgrades.Remove(netId);
            }
            if (PendingScanners.Contains(netId) && TryApplyScanner(netId))
                PendingScanners.Remove(netId);
            if (PendingInstruments.TryGetValue(netId, out var instruments))
            {
                bool all = true;
                foreach (var hash in instruments) all &= TryApplyInstrument(netId, hash);
                if (all) PendingInstruments.Remove(netId);
            }
        }
    }
}
