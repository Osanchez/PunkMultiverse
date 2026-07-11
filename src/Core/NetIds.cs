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
        private static Dictionary<ulong, int> _localFps;    // fingerprint -> instanceId
        private static Dictionary<ulong, int> _neighborFps; // adjacent-cell fps (jitter net)
        private static readonly List<(int netId, ulong fp)> _unmatched = new List<(int, ulong)>();
        private static readonly HashSet<int> OrphanInstances = new HashSet<int>();
        private static int _expectedTotal = -1;
        private static int _matched, _missing;

        public static int Count => NetToInstance.Count;
        public static bool ManifestComplete { get; private set; }

        /// <summary>Local entities that never matched a shared identity (muted phantoms) — the
        /// count that keeps churning authority when it drifts above zero.</summary>
        public static int OrphanCount => OrphanInstances.Count;

        /// <summary>Local entity with no cross-machine identity (fingerprint never matched the
        /// host's manifest). Orphans must not run live AI — see EnemySync.MuteOrphan.</summary>
        public static bool IsOrphanInstance(int instanceId) => OrphanInstances.Contains(instanceId);

        public static void Reset()
        {
            NetToInstance.Clear();
            InstanceToNet.Clear();
            LastManifest = new List<ulong>();
            _localFps = null;
            _neighborFps = null;
            _unmatched.Clear();
            OrphanInstances.Clear();
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

        private static ulong Fingerprint(string entityId, int qx, int qy, int ordinal)
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
            Mix((ulong)(long)qx);
            Mix((ulong)(long)qy);
            Mix((ulong)ordinal);
            return hash;
        }

        /// <summary>Walk the EntityManager and build fingerprint -> instanceId for this client.
        /// Ships are excluded — they're identified by player slot, not by manifest. Also fills
        /// the neighbor map: the same entity fingerprinted in each of the 8 adjacent
        /// quantization cells, so a position sitting on a rounding boundary (float jitter
        /// between machines) still matches in ApplyChunk's second pass.</summary>
        public static Dictionary<ulong, int> BuildLocalFingerprints()
        {
            var result = new Dictionary<ulong, int>();
            _neighborFps = new Dictionary<ulong, int>();
            var seen = new Dictionary<ulong, int>(); // base fp -> duplicate count
            var em = ServiceLocator.Get<EntityManager>();
            foreach (var e in em.GetAllEntities())
            {
                if (e == null || e.entityId == "Ship") continue;
                int qx = Mathf.RoundToInt(e.position.x * 2f);
                int qy = Mathf.RoundToInt(e.position.y * 2f);
                ulong baseFp = Fingerprint(e.entityId, qx, qy, 0);
                seen.TryGetValue(baseFp, out int ordinal);
                seen[baseFp] = ordinal + 1;
                ulong fp = ordinal == 0 ? baseFp : Fingerprint(e.entityId, qx, qy, ordinal);
                result[fp] = e.instanceId;
                if (ordinal != 0) continue; // duplicates keep exact-match only
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        ulong nfp = Fingerprint(e.entityId, qx + dx, qy + dy, 0);
                        if (!_neighborFps.ContainsKey(nfp)) _neighborFps[nfp] = e.instanceId;
                    }
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

        /// <summary>Host: netId = index in the fingerprint list sorted ascending. Uses the
        /// fingerprints cached at level generation (PrepareLocal): go-live can be seconds
        /// later, and entities that moved in the meantime would fingerprint differently from
        /// every client's generation-time snapshot.</summary>
        public static List<ulong> BuildManifest()
        {
            if (_localFps == null) _localFps = BuildLocalFingerprints();
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
                // Clients keep the host's manifest verbatim: a migration-promoted host must be
                // able to replay it to rejoiners exactly as the original host handed it out.
                while (LastManifest.Count <= netId) LastManifest.Add(0UL);
                LastManifest[netId] = msg.Fps[i];
                if (_localFps.TryGetValue(msg.Fps[i], out int instanceId))
                {
                    NetToInstance[netId] = instanceId;
                    InstanceToNet[instanceId] = netId;
                    _matched++;
                }
                else
                {
                    _unmatched.Add((netId, msg.Fps[i]));
                    _missing++;
                }
            }
            if (_matched + _missing >= _expectedTotal)
            {
                // Second pass, after every exact match has claimed its instance: a miss whose
                // fingerprint lands in a neighboring quantization cell of an unclaimed local
                // entity is the same entity seen through cross-machine float jitter.
                int jitterMatched = 0;
                foreach (var (netId, fp) in _unmatched)
                {
                    if (_neighborFps != null && _neighborFps.TryGetValue(fp, out int instanceId)
                        && !InstanceToNet.ContainsKey(instanceId))
                    {
                        NetToInstance[netId] = instanceId;
                        InstanceToNet[instanceId] = netId;
                        _matched++;
                        _missing--;
                        jitterMatched++;
                    }
                }
                var unresolvedIds = new List<int>();
                foreach (var (netId, _) in _unmatched)
                    if (!NetToInstance.ContainsKey(netId))
                        unresolvedIds.Add(netId);
                _unmatched.Clear();

                // Whatever still has no netId must not run live AI (it would be a phantom the
                // sync layer can't see): mute what's spawned now, the spawn hook gets the rest.
                OrphanInstances.Clear();
                foreach (var inst in _localFps.Values)
                    if (!InstanceToNet.ContainsKey(inst))
                        OrphanInstances.Add(inst);
                ManifestComplete = true;
                foreach (var inst in OrphanInstances)
                    Sync.EnemySync.MuteOrphan(inst);

                var log = $"[Ids] manifest applied: {_expectedTotal} total, {_matched} matched " +
                    $"({jitterMatched} via jitter), {_missing} missing here, {OrphanInstances.Count} local orphans (muted)";
                if (_missing > 0 || OrphanInstances.Count > 0) Plugin.Log.LogWarning(log);
                else Plugin.Log.LogInfo(log);

                // Residual mismatches (unseeded generation randomness — position hashes can't
                // match what was never at the same spot): ask the host what those netIds ARE
                // and match by entity type + nearest position instead.
                if (unresolvedIds.Count > 0)
                {
                    if (NetDiag.Enabled)
                        NetDiag.Log("Ids", $"{unresolvedIds.Count} netIds unresolved after manifest, requesting resolve: " +
                            string.Join(", ", unresolvedIds.ConvertAll(id => "#" + id)));
                    NetSession.Instance?.RequestIdResolve(unresolvedIds);
                }
            }
        }

        private const float ResolveMatchDistance = 12f; // world units; generous — entities drift

        private static uint HashId(string s)
        {
            uint hash = 2166136261u;
            foreach (char c in s) { hash ^= (byte)c; hash *= 16777619u; }
            return hash;
        }

        /// <summary>Host: describe the requested netIds (type + current position) so the
        /// client can adopt its orphans into shared identity.</summary>
        public static List<Protocol.IdResolveEntry> DescribeNetIds(List<int> netIds)
        {
            var entries = new List<Protocol.IdResolveEntry>(netIds.Count);
            EntityManager em;
            try { em = ServiceLocator.Get<EntityManager>(); } catch { return entries; }
            int cap = Mathf.Min(netIds.Count, 2048);
            for (int i = 0; i < cap; i++)
            {
                if (!NetToInstance.TryGetValue(netIds[i], out int instanceId)) continue;
                var data = em.GetEntity(instanceId);
                if (data == null || string.IsNullOrEmpty(data.entityId)) continue;
                entries.Add(new Protocol.IdResolveEntry
                {
                    NetId = netIds[i],
                    TypeHash = HashId(data.entityId),
                    Qx = Mathf.RoundToInt(data.position.x * 2f),
                    Qy = Mathf.RoundToInt(data.position.y * 2f),
                });
            }
            return entries;
        }

        /// <summary>Client: adopt local orphans into the host's identities by entity type and
        /// nearest position. Whatever still doesn't match stays a muted orphan.</summary>
        public static void ApplyResolve(Protocol.IdResolveReplyMsg msg)
        {
            EntityManager em;
            try { em = ServiceLocator.Get<EntityManager>(); } catch { return; }

            // Index the orphans by type hash with their current positions.
            var byType = new Dictionary<uint, List<(int instanceId, Vector2 pos)>>();
            foreach (var inst in OrphanInstances)
            {
                var data = em.GetEntity(inst);
                if (data == null || string.IsNullOrEmpty(data.entityId)) continue;
                uint hash = HashId(data.entityId);
                if (!byType.TryGetValue(hash, out var list)) byType[hash] = list = new List<(int, Vector2)>();
                list.Add((inst, (Vector2)data.position));
            }

            int resolved = 0;
            foreach (var e in msg.Entries)
            {
                if (NetToInstance.ContainsKey(e.NetId)) continue;
                if (!byType.TryGetValue(e.TypeHash, out var candidates) || candidates.Count == 0) continue;
                var target = new Vector2(e.Qx / 2f, e.Qy / 2f);
                int best = -1;
                float bestDist = ResolveMatchDistance;
                for (int i = 0; i < candidates.Count; i++)
                {
                    float d = Vector2.Distance(candidates[i].pos, target);
                    if (d < bestDist) { bestDist = d; best = i; }
                }
                if (best < 0) continue;
                int instanceId = candidates[best].instanceId;
                candidates.RemoveAt(best);
                NetToInstance[e.NetId] = instanceId;
                InstanceToNet[instanceId] = e.NetId;
                OrphanInstances.Remove(instanceId);
                resolved++;
                Sync.EnemySync.OnResolvedOrphan(e.NetId, instanceId);
            }
            Plugin.Log.LogInfo($"[Ids] type+position resolve: {resolved} of {msg.Entries.Count} adopted, " +
                $"{OrphanInstances.Count} orphans remain muted");
            if (NetDiag.Enabled && OrphanInstances.Count > 0)
            {
                EntityManager emd = null;
                try { emd = ServiceLocator.Get<EntityManager>(); } catch { }
                var labels = new List<string>();
                foreach (var inst in OrphanInstances)
                {
                    var d = emd?.GetEntity(inst);
                    labels.Add(d != null && !string.IsNullOrEmpty(d.entityId) ? $"{d.entityId}@inst{inst}" : $"inst{inst}");
                    if (labels.Count >= 20) break;
                }
                NetDiag.Warn("Ids", $"{OrphanInstances.Count} entities never resolved (run local AI muted, never owned): " +
                    string.Join(", ", labels));
            }
        }
    }
}
