using System.Collections.Generic;
using System.Linq;
using PunkMultiverse.Protocol;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Cross-client entity identity. Entity instanceIds are random per client, so identity is a
    /// deterministic fingerprint: hash(entityId string + spawn position quantized to 0.5u), with
    /// an ordinal salt for identical duplicates (swapped pairing among true duplicates is
    /// harmless). The host sorts fingerprints and broadcasts them in chunks; a fingerprint's
    /// index in that sorted list IS its netId. Runtime spawns (minions) use composite ids
    /// (slot+1)<<12 | counter, far above any manifest id.
    /// </summary>
    internal static class NetIds
    {
        private static readonly Dictionary<int, int> NetToInstance = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> InstanceToNet = new Dictionary<int, int>();
        private static Dictionary<ulong, int> _localFps; // fingerprint -> instanceId
        private static int _expectedTotal = -1;
        private static int _matched, _missing;

        public static int Count => NetToInstance.Count;
        public static bool ManifestComplete { get; private set; }

        public static void Reset()
        {
            NetToInstance.Clear();
            InstanceToNet.Clear();
            _localFps = null;
            _expectedTotal = -1;
            _matched = 0;
            _missing = 0;
            ManifestComplete = false;
        }

        public static bool TryGetInstanceId(int netId, out int instanceId) => NetToInstance.TryGetValue(netId, out instanceId);
        public static bool TryGetNetId(int instanceId, out int netId) => InstanceToNet.TryGetValue(instanceId, out netId);

        public static void RegisterRuntime(int netId, int instanceId)
        {
            // A netId must never alias two entities — remove the stale reverse mapping, or
            // authority/state/kill traffic cross-applies between the pair.
            if (NetToInstance.TryGetValue(netId, out int oldInstance) && oldInstance != instanceId)
            {
                Plugin.Log.LogWarning($"[Ids] netId {netId} remapped (instance {oldInstance} -> {instanceId})");
                InstanceToNet.Remove(oldInstance);
            }
            NetToInstance[netId] = instanceId;
            InstanceToNet[instanceId] = netId;
        }

        // ---------------------------------------------------------------- fingerprints

        private static ulong Fingerprint(string entityId, Vector3 position, int ordinal)
        {
            ulong hash = 14695981039346656037UL;
            void Mix(ulong v)
            {
                for (int i = 0; i < 8; i++)
                {
                    hash ^= (byte)(v >> (i * 8));
                    hash *= 1099511628211UL;
                }
            }
            foreach (char c in entityId) { hash ^= (byte)c; hash *= 1099511628211UL; }
            Mix((ulong)(long)Mathf.RoundToInt(position.x * 2f));
            Mix((ulong)(long)Mathf.RoundToInt(position.y * 2f));
            Mix((ulong)ordinal);
            return hash;
        }

        /// <summary>Walk the EntityManager and build fingerprint -> instanceId for this client.
        /// Ships are excluded — they're identified by player slot, not by manifest.</summary>
        public static Dictionary<ulong, int> BuildLocalFingerprints()
        {
            var result = new Dictionary<ulong, int>();
            var seen = new Dictionary<ulong, int>(); // base fp -> duplicate count
            var em = ServiceLocator.Get<EntityManager>();
            foreach (var e in em.GetAllEntities())
            {
                if (e == null || e.entityId == "Ship") continue;
                ulong baseFp = Fingerprint(e.entityId, e.position, 0);
                seen.TryGetValue(baseFp, out int ordinal);
                seen[baseFp] = ordinal + 1;
                ulong fp = ordinal == 0 ? baseFp : Fingerprint(e.entityId, e.position, ordinal);
                result[fp] = e.instanceId;
            }
            return result;
        }

        /// <summary>Client: cache local fingerprints (call on LevelGenerated).</summary>
        public static void PrepareLocal()
        {
            _localFps = BuildLocalFingerprints();
            Plugin.Log.LogInfo($"[Ids] local fingerprints: {_localFps.Count} entities");
        }

        /// <summary>The manifest the host handed out — replayed to rejoining players.</summary>
        public static List<ulong> LastManifest { get; private set; } = new List<ulong>();

        /// <summary>Host: netId = index in the fingerprint list sorted ascending.</summary>
        public static List<ulong> BuildManifest()
        {
            _localFps = BuildLocalFingerprints();
            var sorted = _localFps.Keys.OrderBy(fp => fp).ToList();
            for (int netId = 0; netId < sorted.Count; netId++)
            {
                int instanceId = _localFps[sorted[netId]];
                NetToInstance[netId] = instanceId;
                InstanceToNet[instanceId] = netId;
            }
            ManifestComplete = true;
            LastManifest = sorted;
            Plugin.Log.LogInfo($"[Ids] manifest built: {sorted.Count} entities");
            return sorted;
        }

        /// <summary>Client: apply one manifest chunk; fingerprints arrive in netId order.</summary>
        public static void ApplyChunk(ManifestMsg msg)
        {
            if (_localFps == null) PrepareLocal();
            _expectedTotal = msg.Total;
            for (int i = 0; i < msg.Fps.Length; i++)
            {
                int netId = msg.StartIndex + i;
                if (_localFps.TryGetValue(msg.Fps[i], out int instanceId))
                {
                    NetToInstance[netId] = instanceId;
                    InstanceToNet[instanceId] = netId;
                    _matched++;
                }
                else
                {
                    _missing++;
                }
            }
            if (_matched + _missing >= _expectedTotal)
            {
                ManifestComplete = true;
                int orphans = _localFps.Count - _matched;
                var log = $"[Ids] manifest applied: {_expectedTotal} total, {_matched} matched, {_missing} missing here, {orphans} local orphans";
                if (_missing > 0 || orphans > 0) Plugin.Log.LogWarning(log);
                else Plugin.Log.LogInfo(log);
            }
        }
    }
}
