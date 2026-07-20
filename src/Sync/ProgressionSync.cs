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
        private static readonly HashSet<ulong> AppliedUpgradeKeys = new HashSet<ulong>();
        private static readonly HashSet<int> UsedScanners = new HashSet<int>();

        public static List<(int netId, uint hash)> UpgradeSnapshot() => new List<(int, uint)>(AppliedUpgrades);
        public static List<int> ScannerSnapshot() => new List<int>(UsedScanners);

        /// <summary>Most recently upgraded/unlocked station by anyone — the party's respawn
        /// checkpoint. Rejoiners spawn here instead of at the run start.</summary>
        public static int LatestStationNetId { get; private set; }

        /// <summary>Run resume: replayed upgrades overwrite the checkpoint in arbitrary order,
        /// so the saved value is restored explicitly afterwards.</summary>
        public static void RestoreCheckpoint(int netId) => LatestStationNetId = netId;

        // Discovered map locations (station/POI netIds marked permanently visible). Ledger for
        // late-join / rejoin catch-up; also dedups our own broadcasts.
        private static readonly HashSet<int> DiscoveredEntities = new HashSet<int>();
        public static List<int> DiscoveredSnapshot() => new List<int>(DiscoveredEntities);

        // Catch-up ledgers accumulate on EVERY machine (not just the host) so that a client
        // promoted by host migration can serve full catch-up to rejoiners and late joiners.
        private static ulong EventKey(int netId, uint hash) => ((ulong)(uint)netId << 32) | hash;

        public static bool RecordUpgrade(int netId, uint hash)
        {
            if (!AppliedUpgradeKeys.Add(EventKey(netId, hash))) return false;
            LatestStationNetId = netId;
            AppliedUpgrades.Add((netId, hash));
            NetSession.Instance?.OnStationUnlocked(netId);
            InstrumentationCounters.DurableMutationApplied();
            return true;
        }

        public static bool RecordScanner(int netId)
        {
            if (!UsedScanners.Add(netId)) return false;
            InstrumentationCounters.DurableMutationApplied();
            return true;
        }

        public static void Reset()
        {
            _applyingRemote = false;
            LatestStationNetId = 0;
            PendingUpgrades.Clear();
            PendingScanners.Clear();
            AppliedUpgrades.Clear();
            AppliedUpgradeKeys.Clear();
            UsedScanners.Clear();
            PendingInstruments.Clear();
            UsedInstruments.Clear();
            UsedInstrumentKeys.Clear();
            DiscoveredEntities.Clear();
            _discoverablesByHash = null;
        }

        // ---------------------------------------------------------------- station upgrades

        // A station's very first upgrade IS its unlock (Data.IsUnlocked == installedUpgrades.Count
        // > 0), and every purchase path funnels through Data.Install — Station.TryInstallUpgrade
        // and the string overload both end here. The previous capture patched TryInstallUpgrade
        // and resolved the netId through GetComponentInParent, which could bail SILENTLY — an
        // unlock then stayed machine-local: closed station + no respawn for everyone else.
        [HarmonyPatch(typeof(Station.Data), nameof(Station.Data.Install), typeof(StationUpgrade))]
        internal static class CaptureUpgrade
        {
            private static void Postfix(Station.Data __instance, StationUpgrade __0)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
                try
                {
                    // StationUpgrade is a plain [Serializable] class, NOT a UnityEngine.Object.
                    // The old capture cast it to UnityEngine.Object for .name — always null, so
                    // every unlock bailed silently. The game's own identity for an upgrade is its
                    // string id (Data.Install(string) and RegisterStationUpgradePurchase use it).
                    if (__0 == null || string.IsNullOrEmpty(__0.id))
                    {
                        Plugin.Log.LogWarning("[Progress] station upgrade NOT synced — upgrade has no id");
                        return;
                    }
                    var entity = __instance?.entity;
                    if (entity == null || !NetIds.TryGetNetId(entity.instanceId, out int netId))
                    {
                        Plugin.Log.LogWarning($"[Progress] station upgrade '{__0.id}' NOT synced — " +
                            $"no netId for station entity (instanceId {(entity != null ? entity.instanceId.ToString() : "null")})");
                        return;
                    }
                    uint hash = DamageSync.HashName(__0.id);
                    if (!RecordUpgrade(netId, hash)) return; // echo of a replayed install — already broadcast
                    Writer.Reset();
                    new StationUpgradeMsg { StationNetId = netId, UpgradeHash = hash }.Write(Writer);
                    session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                    Plugin.Log.LogInfo($"[Progress] station upgrade '{__0.id}' broadcast (netId {netId})");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Progress] upgrade capture failed: {e}");
                }
            }
        }

        // Vanilla station-unlock respawn revives the FIRST dead ship in ShipManager.ships — but in
        // a net run that list also holds remote puppets (ShipSync spawns them through ShipManager),
        // and puppet resurrection is gated to the owner's life event (DamageSync.GatePuppetResurrect).
        // A dead puppet ahead of the dead local ship would eat the revive: vanilla teleports the
        // puppet, the gate blocks its Resurrect, and nobody respawns. Route the cascade to the
        // LOCAL ship only — each machine revives its own, and the life-event broadcast replicates.
        [HarmonyPatch(typeof(ShipManager), "OnUpgradeInstalled")]
        internal static class RouteStationRespawn
        {
            private static bool Prefix(Station.Data station)
            {
                if (!NetSession.Active) return true; // single player: vanilla behavior
                try
                {
                    var ship = ShipSync.LocalShip;
                    if (ship == null || !ship.IsDead || station == null || station.entity == null) return false;
                    ship.Unit.ComponentData.entity.MoveTo(station.entity.position);
                    ship.transform.position = station.entity.position;
                    ship.Resurrect(); // broadcasts via DamageSync.BroadcastResurrect; spectator cam disengages on IsDead=false
                    InstrumentationCounters.StationRespawnAssigned();
                    Plugin.Log.LogInfo("[Progress] station unlock — respawned local ship at the new station");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Progress] station respawn failed: {e.Message}");
                }
                return false;
            }
        }

        public static void ApplyStationUpgrade(StationUpgradeMsg msg)
        {
            if (!RecordUpgrade(msg.StationNetId, msg.UpgradeHash)) return;
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
                // Install on the entity DATA, not the streamed GameObject. A dead spectator or a
                // far-away teammate hasn't streamed the station in, and the old component lookup
                // queued the upgrade until stream-in — for them the station stayed closed and the
                // respawn cascade never ran unless they happened to fly there. Every subscriber
                // that matters binds to the Data at run start (ShipManager respawn, lights, map
                // icon, music), and Station.Bind restores the open visual state on stream-in.
                var entity = ServiceLocator.Get<EntityManager>()?.GetEntity(instanceId);
                if (entity == null || !entity.TryGetComponent<Station.Data>(out var data) || data == null) return false;

                // StationUpgrade is a plain class, not an asset — the station's own allUpgrades
                // list (identical on every machine: copied from the prefab at deterministic gen)
                // is the only registry. Match by the same id hash the sender used.
                var upgrade = ResolveUpgrade(data, upgradeHash);
                if (upgrade == null)
                {
                    Plugin.Log.LogWarning($"[Progress] unknown upgrade hash {upgradeHash:X8} for station netId {netId}");
                    return true; // don't queue forever
                }
                for (int i = 0; i < data.installedUpgrades.Count; i++)
                    if (data.installedUpgrades[i] == upgrade) return true; // catch-up replay echo
                _applyingRemote = true;
                try
                {
                    // Same effect as a successful purchase, minus the cost.
                    data.Install(upgrade);
                    // Price parity (tester report 2026-07-20): vanilla registers the purchase only
                    // on the BUYER's machine (Station.TryInstallUpgrade -> RunData), and repeat
                    // prices scale off that count — so everyone else kept seeing base prices and
                    // upgrade availability drifted per machine. Register remote installs too.
                    try { ServiceLocator.Get<RunData>()?.RegisterStationUpgradePurchase(upgrade.id); } catch { }
                }
                finally
                {
                    _applyingRemote = false;
                }
                Plugin.Log.LogInfo($"[Progress] applied remote station upgrade '{upgrade.id}' (netId {netId})");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Progress] upgrade apply failed: {e.Message}");
                return true;
            }
        }

        private static StationUpgrade ResolveUpgrade(Station.Data data, uint hash)
        {
            var all = data.allUpgrades;
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                var u = all[i];
                if (u != null && !string.IsNullOrEmpty(u.id) && DamageSync.HashName(u.id) == hash) return u;
            }
            return null;
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
            if (!RecordScanner(msg.NetId)) return;
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
        private static readonly HashSet<ulong> UsedInstrumentKeys = new HashSet<ulong>();

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
                    if (!UsedInstrumentKeys.Add(EventKey(netId, hash))) return;
                    UsedInstruments.Add((netId, hash));
                    InstrumentationCounters.DurableMutationApplied();
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
            if (!UsedInstrumentKeys.Add(EventKey(msg.NetId, msg.DiscoverableHash))) return;
            UsedInstruments.Add((msg.NetId, msg.DiscoverableHash));
            InstrumentationCounters.DurableMutationApplied();
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

        // ---------------------------------------------------------------- map discovery

        // The instrument-discovery replay (above) marks the instrument used everywhere, but the
        // actual "reveal on the map" (MapIconManager.SetIconToOverdrawn) only runs on a machine
        // when its player opens the map tab (ShipMenuToggler.PlayShowEntitySequence) — so a
        // teammate could fly to a station the host discovered and still see it undiscovered.
        // Capture the overdraw at its source and replicate it directly, so every map marks the
        // SAME location immediately. Message ordering (InstrumentUsed before MapDiscovered) means
        // the receiver's replayed Discover still runs while the entity is undiscovered locally, so
        // it never over-picks a different one.
        [HarmonyPatch(typeof(MapIconManager), "SetIconToOverdrawn")]
        internal static class CaptureDiscovery
        {
            private static void Prefix(MapIconManager __instance, EntityData __0, out bool __state)
            {
                __state = __0 != null && __instance.IsIconOverdrawn(__0.instanceId); // already discovered here?
            }

            private static void Postfix(EntityData __0, bool __state)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
                if (__0 == null || __state) return; // nothing newly discovered
                try
                {
                    if (!NetIds.TryGetNetId(__0.instanceId, out int netId)) return;
                    if (!DiscoveredEntities.Add(netId)) return; // already broadcast
                    Writer.Reset();
                    new MapDiscoveredMsg { NetId = netId }.Write(Writer);
                    session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                    Plugin.Log.LogInfo($"[Progress] map discovery broadcast (netId {netId})");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Progress] discovery capture failed: {e.Message}");
                }
            }
        }

        public static void ApplyMapDiscovered(MapDiscoveredMsg msg)
        {
            if (!DiscoveredEntities.Add(msg.NetId)) return;
            InstrumentationCounters.DurableMutationApplied();
            if (!NetIds.TryGetInstanceId(msg.NetId, out int instanceId)) return;
            try
            {
                var entity = ServiceLocator.Get<EntityManager>()?.GetEntity(instanceId);
                var mim = ServiceLocator.Get<MapIconManager>();
                if (entity == null || mim == null) return; // ledger keeps it for a later catch-up
                _applyingRemote = true;
                try { mim.SetIconToOverdrawn(entity); }
                finally { _applyingRemote = false; }
                Plugin.Log.LogInfo($"[Progress] applied remote map discovery (netId {msg.NetId})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Progress] discovery apply failed: {e.Message}");
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
