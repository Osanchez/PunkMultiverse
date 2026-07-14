using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Entity (enemy/minion) replication under the proximity-authority model. Exactly one machine
    /// simulates each entity (its "owner"; the current host by default), streams coalesced,
    /// interest-routed state bundles at StateHz, and announces kills. Everyone else runs the
    /// entity as a muted RemoteEntityPuppet. Ownership changes arrive as segment lease commits
    /// from the host's AuthorityManager and are applied idempotently here.
    /// </summary>
    internal static class EnemySync
    {
        /// <summary>netId -> explicit entity owner. Entries are used for runtime entities and
        /// availability-promoted world entities that cannot safely follow a whole segment lease.</summary>
        public static readonly Dictionary<int, byte> Owners = new Dictionary<int, byte>();
        /// <summary>Entity ids exempt from proximity handoff (runtime entities and starvation
        /// recoveries whose concrete availability was proven by the requesting viewer).</summary>
        public static readonly HashSet<int> FixedOwners = new HashSet<int>();
        private static readonly HashSet<int> KilledNetIds = new HashSet<int>();
        private static readonly HashSet<ulong> KilledPlantFruits = new HashSet<ulong>();
        private static readonly Dictionary<ulong, uint> PlantFruitMutationRevisions = new Dictionary<ulong, uint>();
        private static readonly HashSet<ulong> DroppedPlantFruitLoot = new HashSet<ulong>();
        private static long _plantFruitAnnounced, _plantFruitApplied, _plantFruitMissing;
        // netIds whose local death chain (Die() -> loot/explosion/VFX) has already run here. A
        // killed entity can stream in again (LevelSegment rebuild re-instantiates it, or the
        // data-destroy below is unavailable in this game version); the second time we must remove
        // the zombie WITHOUT re-firing its death chain — see KillInstance.
        private static readonly HashSet<int> DeathEffectsDone = new HashSet<int>();
        private static readonly Dictionary<int, Vector2> LastSentPos = new Dictionary<int, Vector2>();
        private static readonly Dictionary<int, float> LastSentAt = new Dictionary<int, float>();
        private static readonly Dictionary<int, EntityStateEntry> LastSentState = new Dictionary<int, EntityStateEntry>();
        private static readonly Dictionary<int, EntityStateEntry> FullState = new Dictionary<int, EntityStateEntry>();
        // Provenance of each FullState entry. Absent = Live (real simulator output — snapshots,
        // boundary handoffs, our own collection). Baseline applies record weaker origins so the
        // cache can never launder a Generation guess into "last known simulator state".
        private static readonly Dictionary<int, BaselineEntryOrigin> FullStateOrigins
            = new Dictionary<int, BaselineEntryOrigin>();
        private static readonly Dictionary<int, float> LastKeyframeAt = new Dictionary<int, float>();
        private static readonly Dictionary<int, AuthorityManager.SegmentKey> SimulationSegments
            = new Dictionary<int, AuthorityManager.SegmentKey>();
        private static readonly Dictionary<int, uint> MutationRevisions = new Dictionary<int, uint>();
        private static readonly Dictionary<int, uint> AppliedMutationRevisions = new Dictionary<int, uint>();
        private const float StationaryPropHeartbeat = 0.75f;
        private const float StateKeyframeInterval = 0.5f;
        private const int MaxStateDatagramBytes = 1100;
        private static readonly NetWriter Writer = new NetWriter(2048);
        private static float _nextSendAt;
        private static bool _applyingRemote;
        // The loader can overlap multiple GameObjects for one EntityData. LiveEntities contains
        // only the current canonical lifetime; Lifetimes retains every concrete object until its
        // OnDestroy so superseded objects can be quarantined instead of accidentally simulating.
        private static readonly Dictionary<int, SavableEntity> LiveEntities = new Dictionary<int, SavableEntity>();
        private static readonly Dictionary<int, List<EntityIdentityRegistration>> Lifetimes
            = new Dictionary<int, List<EntityIdentityRegistration>>();
        private static readonly HashSet<int> SeenLifetimeNetIds = new HashSet<int>();
        private static readonly Dictionary<string, int> ReplacementTypes = new Dictionary<string, int>();
        private static readonly List<DuplicateEntityInert> PendingRetirements = new List<DuplicateEntityInert>();
        private static readonly List<EntityStateGroup> TargetGroups = new List<EntityStateGroup>(32);
        private static readonly List<List<EntityStateGroup>> ChunkScratch = new List<List<EntityStateGroup>>(8);
        private sealed class InterestRoute
        {
            internal byte Owner;
            internal uint Epoch;
            internal bool Ready;
            internal uint RequestId;
            internal float RequestedAt;
            internal float LastDirectPulse;
            internal float LastInterestedAt;
        }
        private sealed class BaselineRequest
        {
            internal readonly byte Source, Target;
            internal readonly AuthorityManager.SegmentKey Segment;
            internal readonly uint SourceEpoch, TargetEpoch;
            internal readonly RuntimeBaselinePurpose Purpose;
            internal int ExpectedCount;
            internal ulong RosterDigest;
            internal BaselineRequest(byte source, byte target, AuthorityManager.SegmentKey segment,
                uint sourceEpoch, uint targetEpoch, RuntimeBaselinePurpose purpose)
            {
                Source = source; Target = target; Segment = segment;
                SourceEpoch = sourceEpoch; TargetEpoch = targetEpoch; Purpose = purpose;
            }
        }
        private static readonly Dictionary<(byte target, AuthorityManager.SegmentKey segment), InterestRoute> InterestRoutes
            = new Dictionary<(byte, AuthorityManager.SegmentKey), InterestRoute>();
        private static readonly Dictionary<uint, BaselineRequest> PendingBaselines = new Dictionary<uint, BaselineRequest>();
        private static readonly Dictionary<(byte target, AuthorityManager.SegmentKey segment), uint> DirectSendRoutes
            = new Dictionary<(byte, AuthorityManager.SegmentKey), uint>();
        private static readonly HashSet<(AuthorityManager.SegmentKey segment, uint epoch)> FrozenHandoffSegments
            = new HashSet<(AuthorityManager.SegmentKey, uint)>();
        private static uint _nextBaselineRequestId = 1;
        private static uint _stateTick;
        private static float _nextInterestPruneAt;
        private static readonly Dictionary<byte, (uint tick, ushort count, ulong seen)> ReceivedChunks
            = new Dictionary<byte, (uint, ushort, ulong)>();
        private static readonly Dictionary<byte, (uint tick, float localTime)> MappedBundleTimes
            = new Dictionary<byte, (uint, float)>();
        private static readonly Dictionary<int, float> NextStarvedRequestAt = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> LastStarvedPromotionAt = new Dictionary<int, float>();
        // segment -> the lease commit we are owed a first snapshot for. A committed owner that
        // never streams is the residency failure this design hunts; one pending expectation per
        // segment, superseded by newer commits.
        private static readonly Dictionary<AuthorityManager.SegmentKey, (uint epoch, byte owner, float committedAt)>
            FirstSnapshotDeadlines = new Dictionary<AuthorityManager.SegmentKey, (uint, byte, float)>();
        private static float _nextFirstSnapshotSweepAt;
        private const float FirstSnapshotDeadlineSeconds = 2f;
        private static readonly Dictionary<int, (byte candidate, byte previous, float preparedAt)> PendingPromotions
            = new Dictionary<int, (byte, byte, float)>();
        // Availability recovery is deliberately slower than ordinary state streaming. During
        // go-live, entity objects exist before their first snapshot; treating that brief state as
        // owner failure caused dozens of immediate, unnecessary handoffs and visible host jitter.
        private const float AvailabilityStartupGrace = 3f;
        private const float StarvedSnapshotAfter = 1.5f;
        private const float StarvedNeverAfter = 3f;
        private const float StarvedCandidateResidence = 2f;
        private const float StarvedCandidateRecheck = 1f;
        private const float StarvedRequestRetry = 2f;
        private const float StarvedPromotionCooldown = 5f;
        // Beyond this many seconds of total owner silence (never-snapshotted, or stale after the
        // owner stopped) a concrete local replica may be promoted despite lacking a fresh
        // authoritative pose — the alternative is the permanently frozen puppet failure.
        private const float SilentOwnerPromotionAfter = 10f;
        private const float DuplicateRetireGrace = 0.75f;
        private const float DuplicateRetireScanInterval = 0.25f;
        private static float _availabilityRecoveryReadyAt;
        private static float _nextRetirementScanAt;
        private static int _firstLifetimes, _reenteredLifetimes, _overlappingLifetimes, _retiredLifetimes;

        public static void Reset()
        {
            Owners.Clear();
            FixedOwners.Clear();
            KilledNetIds.Clear();
            KilledPlantFruits.Clear();
            PlantFruitMutationRevisions.Clear();
            DroppedPlantFruitLoot.Clear();
            _plantFruitAnnounced = _plantFruitApplied = _plantFruitMissing = 0;
            DeathEffectsDone.Clear();
            DroppedLootNetIds.Clear();
            RemoteKillerSlot = 255;
            LastSentPos.Clear();
            LastSentAt.Clear();
            LastSentState.Clear();
            FullState.Clear();
            FullStateOrigins.Clear();
            FirstSnapshotDeadlines.Clear();
            _nextFirstSnapshotSweepAt = 0;
            LastKeyframeAt.Clear();
            SimulationSegments.Clear();
            MutationRevisions.Clear();
            AppliedMutationRevisions.Clear();
            LastEntityStateMs.Clear();
            NextReleaseAt.Clear();
            NextStarvedRequestAt.Clear();
            LastStarvedPromotionAt.Clear();
            PendingPromotions.Clear();
            _availabilityRecoveryReadyAt = 0;
            _nextHostScanAt = 0;
            _hostScratch.Clear();
            UnitStatus.Reset();
            _nextSendAt = 0;
            _applyingRemote = false;
            LiveEntities.Clear();
            Lifetimes.Clear();
            SeenLifetimeNetIds.Clear();
            ReplacementTypes.Clear();
            PendingRetirements.Clear();
            TargetGroups.Clear();
            ChunkScratch.Clear();
            InterestRoutes.Clear();
            PendingBaselines.Clear();
            DirectSendRoutes.Clear();
            FrozenHandoffSegments.Clear();
            NextDirectPulse.Clear();
            _nextBaselineRequestId = 1;
            _stateTick = 0;
            _nextInterestPruneAt = 0;
            ReceivedChunks.Clear();
            MappedBundleTimes.Clear();
            _nextRetirementScanAt = 0;
            _firstLifetimes = _reenteredLifetimes = _overlappingLifetimes = _retiredLifetimes = 0;
            _loggedFirstAssign = false;
            _loggedFirstState = false;
        }

        /// <summary>netId -> owner slot; unassigned entities belong to the CURRENT host
        /// (which is not necessarily slot 0 after a host migration).</summary>
        public static byte OwnerOf(int netId)
        {
            return AuthorityManager.OwnerOf(netId);
        }

        public static List<int> KilledSnapshot() => new List<int>(KilledNetIds);

        public static List<(int plantNetId, int fruitId)> PlantFruitKilledSnapshot()
        {
            var result = new List<(int, int)>(KilledPlantFruits.Count);
            foreach (ulong key in KilledPlantFruits.OrderBy(key =>
                         PlantFruitMutationRevisions.TryGetValue(key, out uint revision) ? revision : uint.MaxValue))
                result.Add(((int)(key >> 32), unchecked((int)(uint)key)));
            return result;
        }

        public static int PlantFruitKilledCount => KilledPlantFruits.Count;
        public static long PlantFruitAnnouncedCount => _plantFruitAnnounced;
        public static long PlantFruitAppliedCount => _plantFruitApplied;
        public static long PlantFruitMissingCount => _plantFruitMissing;

        public static int KilledCount => KilledNetIds.Count;

        public static bool IsKilled(int netId) => KilledNetIds.Contains(netId);

        internal static uint MutationRevisionOf(int netId) => MutationRevisions.TryGetValue(netId, out uint value)
            ? value : 1u;

        internal static uint PlantFruitRevisionOf(int plantNetId, int fruitId) =>
            PlantFruitMutationRevisions.TryGetValue(PlantFruitKey(plantNetId, fruitId), out uint value)
                ? value : MutationRevisionOf(plantNetId);

        internal static uint NextMutationRevision(int netId)
        {
            uint next = MutationRevisions.TryGetValue(netId, out uint current) ? current + 1u : 1u;
            if (next == 0) next = 1;
            MutationRevisions[netId] = next;
            return next;
        }

        private static bool AcceptMutation(int netId, uint lifetime, uint revision)
        {
            if (!NetIds.LifetimeMatches(netId, lifetime))
            {
                InstrumentationCounters.StaleLifetimeDropped();
                return false;
            }
            if (revision == 0) revision = 1;
            if (AppliedMutationRevisions.TryGetValue(netId, out uint current))
            {
                if (revision <= current)
                {
                    InstrumentationCounters.StaleMutationDropped();
                    return false;
                }
                if (revision > current + 1) InstrumentationCounters.MutationRevisionGap(revision - current - 1);
            }
            AppliedMutationRevisions[netId] = revision;
            if (!MutationRevisions.TryGetValue(netId, out uint known) || revision > known)
                MutationRevisions[netId] = revision;
            InstrumentationCounters.DurableMutationApplied();
            return true;
        }

        // An entity may drop its loot at most ONCE per machine. The game re-runs the whole death
        // (including LootDropper.DropLoot) every time a kill is applied — and a kill can be applied
        // more than once here: the owner's own death, a broadcast kill, and zombie re-kills on
        // stream-in all funnel through Die(). Without this guard each of those re-drops resources,
        // which is the "way more gold than vanilla" inflation. This does NOT suppress the legit
        // one-copy-per-player drop (per-player economy) — only the repeats. See Patches.LootDiag.
        private static readonly HashSet<int> DroppedLootNetIds = new HashSet<int>();

        /// <summary>True the FIRST time this machine drops loot for netId (allow the drop); false
        /// on every repeat (a re-applied/zombie kill — suppress the duplicate drop).</summary>
        public static bool TryMarkLootDropped(int netId) => DroppedLootNetIds.Add(netId);

        private static ulong PlantFruitKey(int plantNetId, int fruitId) =>
            ((ulong)(uint)plantNetId << 32) | (uint)fruitId;

        public static bool TryGetPlantFruitIdentity(Component component, out int plantNetId, out int fruitId)
        {
            plantNetId = 0;
            fruitId = 0;
            var fruit = component != null ? component.GetComponentInParent<EntityPlantFruit>() : null;
            if (fruit == null || fruit.Fruit == null || !TryGetNetId(fruit, out plantNetId)) return false;
            fruitId = fruit.Fruit.id;
            return true;
        }

        public static bool TryMarkPlantFruitLootDropped(int plantNetId, int fruitId) =>
            DroppedPlantFruitLoot.Add(PlantFruitKey(plantNetId, fruitId));

        public static List<(int netId, byte owner)> OwnersSnapshot()
        {
            var list = new List<(int, byte)>(Owners.Count);
            foreach (var kv in Owners) list.Add((kv.Key, kv.Value));
            return list;
        }

        public static bool IsLocallyOwned(int netId)
        {
            var session = NetSession.Instance;
            return session != null && OwnerOf(netId) == session.LocalSlot;
        }

        /// <summary>Player-owned runtime entities cannot retain a departed simulator. Every peer
        /// applies the same deterministic fallback, so fixed ownership remains coherent while the
        /// new host is coming online and before any catch-up exchange is possible.</summary>
        internal static void ReassignFixedOwners(byte lostSlot, byte fallbackSlot)
        {
            var changed = new List<int>();
            foreach (int netId in FixedOwners)
                if (Owners.TryGetValue(netId, out byte owner) && owner == lostSlot)
                    changed.Add(netId);
            foreach (int netId in changed)
            {
                Owners[netId] = fallbackSlot;
                if (NetIds.TryGetInstanceId(netId, out int instanceId))
                    try { ApplyOwnership(netId, instanceId); } catch { }
            }
            if (changed.Count > 0)
                Plugin.Log.LogInfo($"[Auth] reassigned {changed.Count} fixed entities P{lostSlot + 1}->P{fallbackSlot + 1}");
        }

        internal static int CanonicalLifetimeCount => LiveEntities.Count;
        internal static int PendingRetirementCount => PendingRetirements.Count;
        internal static int FirstLifetimeCount => _firstLifetimes;
        internal static int ReenteredLifetimeCount => _reenteredLifetimes;
        internal static int OverlappingLifetimeCount => _overlappingLifetimes;
        internal static int RetiredLifetimeCount => _retiredLifetimes;
        internal static float OldestQuarantineAge
        {
            get
            {
                float oldest = 0f;
                foreach (var inert in PendingRetirements)
                    if (inert != null) oldest = Mathf.Max(oldest, inert.Age);
                return oldest;
            }
        }
        internal static int UnsafeDuplicateLifetimeCount
        {
            get
            {
                int count = 0;
                foreach (var all in Lifetimes.Values)
                    foreach (var registration in all)
                        if (registration != null && !registration.IsCanonical
                            && registration.GetComponent<DuplicateEntityInert>() == null)
                            count++;
                return count;
            }
        }

        internal static bool IsCanonical(Component component)
        {
            if (component == null) return false;
            var se = component.GetComponentInParent<SavableEntity>();
            if (se == null || se.EntityData == null) return false;
            if (!NetIds.TryGetNetId(se.EntityData.instanceId, out int netId)) return false;
            return LiveEntities.TryGetValue(netId, out var canonical) && canonical == se;
        }

        internal static bool CanSimulate(SavableEntity entity, int netId)
        {
            var session = NetSession.Instance;
            if (session == null || entity == null) return false;
            byte actualOwner = FixedOwners.Contains(netId)
                ? OwnerOf(netId)
                : AuthorityManager.OwnerOf(AuthorityManager.SegmentOf(entity.transform.position));
            return LiveEntities.TryGetValue(netId, out var canonical) && canonical == entity
                   && actualOwner == session.LocalSlot
                   && entity.GetComponent<RemoteEntityPuppet>() == null
                   && entity.GetComponent<DuplicateEntityInert>() == null;
        }

        private static EntityIdentityRegistration RegisterLive(int netId, SavableEntity current)
        {
            var registration = current.GetComponent<EntityIdentityRegistration>();
            if (registration == null) registration = current.gameObject.AddComponent<EntityIdentityRegistration>();
            registration.Initialize(netId, current);

            if (!Lifetimes.TryGetValue(netId, out var all))
                Lifetimes[netId] = all = new List<EntityIdentityRegistration>(2);
            if (!all.Contains(registration)) all.Add(registration);

            if (LiveEntities.TryGetValue(netId, out var existing) && existing == current)
            {
                registration.MakeCanonical();
                return registration; // reconciliation and spawn hook observed the same lifetime
            }

            string entityType = current.EntityData?.entityId ?? "<unknown>";
            bool firstLifetime = SeenLifetimeNetIds.Add(netId);
            if (firstLifetime) _firstLifetimes++;

            if (LiveEntities.TryGetValue(netId, out var prior) && prior != null && prior != current)
            {
                var priorRegistration = prior.GetComponent<EntityIdentityRegistration>();
                priorRegistration?.MakeDuplicateInert();
                _overlappingLifetimes++;
                ReplacementTypes[entityType] = ReplacementTypes.TryGetValue(entityType, out int count) ? count + 1 : 1;
                InstrumentationCounters.DuplicateEntityPrevented();
                InstrumentationCounters.DuplicateLifetimeQuarantined();
                if (NetDiag.Enabled) NetDiag.Throttled($"overlap{netId}", 2f, "Identity",
                    () => $"quarantined overlap {NetDiag.Describe(netId)} old={prior.GetInstanceID()} new={current.GetInstanceID()} canonical=new");
            }
            else if (!firstLifetime)
            {
                // A prior lifetime was fully unloaded before this one appeared. This is normal
                // segment streaming, but tracking it separately prevents it being mistaken for a
                // genuinely new room spawn or an overlapping duplicate.
                _reenteredLifetimes++;
            }

            LiveEntities[netId] = current;
            registration.MakeCanonical();
            return registration;
        }

        /// <summary>The EGM already selected a newer object before an old lifetime reaches here.
        /// Keep the old object inert for one loader grace window, then retire it centrally. This
        /// avoids both immediate-destroy fights with deferred stream-out and one LateUpdate scan
        /// per quarantined object forever.</summary>
        internal static void ScheduleDuplicateRetirement(DuplicateEntityInert inert)
        {
            // DuplicateEntityInert calls this only on its first Initialize, so no membership scan
            // is required on the hot overlap path.
            if (inert != null) PendingRetirements.Add(inert);
        }

        private static void TickDuplicateRetirements()
        {
            if (Time.unscaledTime < _nextRetirementScanAt) return;
            _nextRetirementScanAt = Time.unscaledTime + DuplicateRetireScanInterval;
            for (int i = PendingRetirements.Count - 1; i >= 0; i--)
            {
                var inert = PendingRetirements[i];
                if (inert == null)
                {
                    PendingRetirements.RemoveAt(i);
                    continue;
                }
                if (inert.Age < DuplicateRetireGrace) continue;

                var old = inert.Entity;
                bool superseded = old == null
                    || !LiveEntities.TryGetValue(inert.NetId, out var canonical)
                    || canonical == null || canonical != old;
                if (!superseded) continue; // defensive: never retire the canonical object

                PendingRetirements.RemoveAt(i);
                _retiredLifetimes++;
                InstrumentationCounters.DuplicateLifetimeRetired();
                if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                else UnityEngine.Object.Destroy(inert.gameObject);
            }
        }

        /// <summary>Cheap canonical population audit; iterates only the identity registry, never
        /// the Unity scene. The dictionaries are supplied by the caller and cleared here.</summary>
        internal static void GetPopulationAudit(out int units, out int props,
            Dictionary<string, int> unitTypes, Dictionary<string, int> unitSegments)
        {
            units = 0;
            props = 0;
            unitTypes.Clear();
            unitSegments.Clear();
            foreach (var kv in LiveEntities)
            {
                var se = kv.Value;
                if (se == null || se.GetComponent<DuplicateEntityInert>() != null) continue;
                if (se.GetComponent<Unit>() == null) { props++; continue; }
                units++;
                string type = se.EntityData?.entityId ?? "<unknown>";
                unitTypes[type] = unitTypes.TryGetValue(type, out int tc) ? tc + 1 : 1;
                string segment = AuthorityManager.SegmentOf(se.transform.position).ToString();
                unitSegments[segment] = unitSegments.TryGetValue(segment, out int sc) ? sc + 1 : 1;
            }
        }

        internal static void GetReplacementTypes(Dictionary<string, int> target)
        {
            target.Clear();
            foreach (var kv in ReplacementTypes) target[kv.Key] = kv.Value;
        }

        /// <summary>Register GameObjects that streamed in before the manifest assigned netIds.
        /// SpawnObjectForEntity cannot register those at spawn time, and it may never run again
        /// for an object that remains near a player. This pass is therefore a required part of
        /// the manifest barrier, not a best-effort diagnostic scan.</summary>
        internal static void ReconcileSpawnedIdentities(string reason)
        {
            if (!NetIds.ManifestComplete) return;
            var egm = TryGetEgm();
            if (egm == null)
            {
                Plugin.Log.LogWarning($"[Ids] live reconciliation {reason}: EntityGameObjectManager unavailable");
                return;
            }

            int mapped = 0, spawned = 0, units = 0, props = 0;
            int local = 0, puppets = 0, killed = 0, failures = 0;
            foreach (var mapping in NetIds.MappedEntities)
            {
                mapped++;
                try
                {
                    if (!egm.TryGetSavableEntity(mapping.Value, out var se) || se == null) continue;
                    spawned++;
                    RegisterLive(mapping.Key, se);
                    if (KilledNetIds.Contains(mapping.Key))
                    {
                        killed++;
                        KillInstance(mapping.Value, mapping.Key);
                        continue;
                    }

                    bool unit = se.GetComponent<Unit>() != null;
                    if (unit) units++; else props++;
                    ApplyOwnership(mapping.Key, mapping.Value);
                    if (unit)
                    {
                        if (se.GetComponent<RemoteEntityPuppet>() != null) puppets++;
                        else local++;
                    }
                }
                catch (Exception e)
                {
                    failures++;
                    if (failures <= 3)
                        Plugin.Log.LogWarning($"[Ids] live reconciliation failed for #{mapping.Key}: {e.Message}");
                }
            }

            Plugin.Log.LogInfo($"[Ids] live reconciliation {reason}: mapped={mapped} spawned={spawned} " +
                $"units={units} local={local} puppets={puppets} props={props} killed={killed} failures={failures}");
        }

        /// <summary>Kill/authority helpers need the netId of an arbitrary component's entity.</summary>
        public static bool TryGetNetId(Component c, out int netId)
        {
            netId = 0;
            var se = c != null ? c.GetComponentInParent<SavableEntity>() : null;
            if (se == null || se.EntityData == null) return false;
            return NetIds.TryGetNetId(se.EntityData.instanceId, out netId);
        }

        // ---------------------------------------------------------------- spawn hook

        // Whenever an entity GameObject streams in, align it with the current authority state.
        [HarmonyPatch(typeof(EntityGameObjectManager), "SpawnObjectForEntity")]
        internal static class OnEntitySpawned
        {
            private static void Postfix(EntityData __0)
            {
                var profile = PatchProfiler.Enter(PatchId.EnemyOnEntitySpawned);
                try { PostfixBody(__0); }
                finally { PatchProfiler.Exit(PatchId.EnemyOnEntitySpawned, profile); }
            }

            private static void PostfixBody(EntityData __0)
            {
                var session = NetSession.Instance;
                if (session == null || __0 == null) return;
                if (session.State != SessionState.InGame && session.State != SessionState.Loading) return;
                if (!NetIds.TryGetNetId(__0.instanceId, out int netId))
                {
                    // Manifest completes during Loading — orphans must be muted from the
                    // first spawn, whichever side of go-live they stream in on.
                    if (NetIds.ManifestComplete && NetIds.IsOrphanInstance(__0.instanceId))
                        MuteOrphan(__0.instanceId);
                    return;
                }
                // Unity Destroy is deferred, so a normal segment stream-out/stream-in can expose
                // two lifetimes for one ID. The newest EGM object becomes canonical; the previous
                // object is hidden and made non-simulating without fighting the loader's destroy.
                try
                {
                    var egm = TryGetEgm();
                    if (egm != null && egm.TryGetSavableEntity(__0.instanceId, out var current) && current != null)
                        RegisterLive(netId, current);
                }
                catch { }
                // A kill received while this entity was unspawned here may not have destroyed
                // the data (game-version dependent) — streaming it back in as a live zombie is
                // how "enemies only I can see" happens. Re-kill on arrival.
                if (KilledNetIds.Contains(netId))
                {
                    KillInstance(__0.instanceId, netId);
                    return;
                }
                if (session.State != SessionState.InGame) return;
                try { ApplyOwnership(netId, __0.instanceId); } catch { }
                try { ProgressionSync.ApplyPendingFor(netId); } catch { }
                try { HookSync.ApplyPendingFor(netId); } catch { }
            }
        }

        /// <summary>An orphan just got adopted into a shared identity (type+position resolve):
        /// give its muted puppet the real netId, honor any kill recorded for it, and let the
        /// normal ownership machinery take over.</summary>
        public static void OnResolvedOrphan(int netId, int instanceId)
        {
            try
            {
                if (KilledNetIds.Contains(netId))
                {
                    KillInstance(instanceId, netId);
                    return;
                }
                var egm = TryGetEgm();
                if (egm != null && egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                {
                    RegisterLive(netId, se);
                    var puppet = se.GetComponent<RemoteEntityPuppet>();
                    if (puppet != null) puppet.NetId = netId; // was the orphan marker (-1)
                }
                ApplyOwnership(netId, instanceId);
            }
            catch { }
        }

        /// <summary>A fingerprint orphan has no cross-machine identity: left alone it runs full
        /// local AI invisible to the sync layer — a phantom enemy whose spawner adds multiply
        /// on one machine only. Mute it like a puppet (frozen body, no AI, still shootable —
        /// DamageSync applies orphan damage locally). Props keep working untouched.</summary>
        public static void MuteOrphan(int instanceId)
        {
            try
            {
                var egm = TryGetEgm();
                if (egm == null || !egm.TryGetSavableEntity(instanceId, out var se) || se == null) return;
                if (se.GetComponent<Unit>() == null) return; // static prop — no AI to mute
                if (se.GetComponent<RemoteEntityPuppet>() != null) return;
                var puppet = se.gameObject.AddComponent<RemoteEntityPuppet>();
                puppet.NetId = -1; // orphan marker — never referenced in sync traffic
                puppet.MuteNow();
            }
            catch { }
        }

        private static void ApplyOwnership(int netId, int instanceId)
        {
            var session = NetSession.Instance;
            if (session == null) return;
            if (!LiveEntities.TryGetValue(netId, out var se) || se == null) return;
            if (se.GetComponent<DuplicateEntityInert>() != null) return;
            if (se.GetComponent<Unit>() == null)
            {
                // Physics prop: exactly one simulator (§6 of the design). Non-owners hold the
                // body kinematically at its canonical pose; the owner's snapshots drive it
                // while it moves. Bodiless decor stays untouched (generation-deterministic),
                // and runtime-spawn replicas already carry a RemoteEntityPuppet that owns
                // the body.
                var body = se.GetComponent<Rigidbody2D>();
                if (body == null || se.GetComponent<RemoteEntityPuppet>() != null) return;
                var hold = se.GetComponent<PropPuppet>();
                if (OwnerOf(netId) == session.LocalSlot)
                {
                    if (hold != null) UnityEngine.Object.Destroy(hold);
                    return;
                }
                if (hold == null) hold = se.gameObject.AddComponent<PropPuppet>();
                hold.Hold = true;
                hold.EnsureHeld();
                return;
            }

            var puppet = se.GetComponent<RemoteEntityPuppet>();
            bool mine = OwnerOf(netId) == session.LocalSlot;
            if (mine)
            {
                // Establish the source segment before the first state tick. Without this,
                // a freshly-owned unit can cross a lease boundary before it has emitted a
                // snapshot and neither simulator will have enough history to hand it off.
                if (!FixedOwners.Contains(netId))
                    SimulationSegments[netId] = AuthorityManager.SegmentOf(se.transform.position);
                if (puppet != null) UnityEngine.Object.Destroy(puppet);
            }
            else
            {
                SimulationSegments.Remove(netId);
            }
            if (!mine && puppet == null)
            {
                puppet = se.gameObject.AddComponent<RemoteEntityPuppet>();
                puppet.NetId = netId;
            }
            UnitStatus.ApplyEnemyHpScale(se, instanceId, netId);
        }

        internal static void ApplySegmentOwnership(AuthorityManager.SegmentKey segment)
        {
            foreach (var kv in LiveEntities)
            {
                if (kv.Value == null) continue;
                if (!AuthorityManager.TrySegmentOf(kv.Key, out var key) || !key.Equals(segment)) continue;
                if (NetIds.TryGetInstanceId(kv.Key, out int instanceId)) ApplyOwnership(kv.Key, instanceId);
            }
        }

        internal static void ApplyAllOwnership()
        {
            foreach (var kv in LiveEntities)
            {
                if (kv.Value == null) continue;
                if (NetIds.TryGetInstanceId(kv.Key, out int instanceId)) ApplyOwnership(kv.Key, instanceId);
            }
        }

        internal static void UnregisterLive(int netId, SavableEntity entity)
        {
            // Legacy call site retained for binary-local compatibility during hot reload.
            var registration = entity != null ? entity.GetComponent<EntityIdentityRegistration>() : null;
            if (registration != null) UnregisterLive(registration);
        }

        internal static void UnregisterLive(EntityIdentityRegistration registration)
        {
            if (registration == null) return;
            int netId = registration.NetId;
            if (Lifetimes.TryGetValue(netId, out var all))
            {
                all.Remove(registration);
                if (all.Count == 0) Lifetimes.Remove(netId);
            }
            bool removedCanonical = LiveEntities.TryGetValue(netId, out var current)
                                    && current == registration.Entity;
            if (removedCanonical)
            {
                LiveEntities.Remove(netId);
                SimulationSegments.Remove(netId);
                LastSentAt.Remove(netId);
                LastSentPos.Remove(netId);
            }
            // Explicit authority is based on concrete availability. If its simulator streams the
            // entity out, clear the exception immediately so another viewer can claim it.
            if (removedCanonical && FixedOwners.Contains(netId) && !KilledNetIds.Contains(netId))
            {
                var session = NetSession.Instance;
                if (session != null && OwnerOf(netId) == session.LocalSlot)
                    MaybeReleaseAuthority(session, netId);
            }
        }

        // ---------------------------------------------------------------- dormancy (I-10)

        // The release edge: the game is about to destroy this segment's GameObjects. If we own
        // the lease, our final entity states must leave BEFORE the objects die — this prefix is
        // what makes "authority without a live simulator" structurally impossible for owners
        // that unload gracefully (crashes are covered by the host's grace fallback).
        [HarmonyPatch(typeof(EntityGameObjectManager), "DestroyGameObjects")]
        internal static class OnSegmentUnloading
        {
            private static void Prefix(LevelSegmentComponent __0)
            {
                try { SendDormancyCommit(__0); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Dormancy] commit capture failed: {e.Message}"); }
            }
        }

        private static void SendDormancyCommit(LevelSegmentComponent segmentComponent)
        {
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame || segmentComponent == null) return;
            if (!NetIds.ManifestComplete) return;
            var pos = segmentComponent.SegmentPosition;
            var key = new AuthorityManager.SegmentKey(pos.x, pos.y);
            if (AuthorityManager.OwnerOf(key) != session.LocalSlot) return;
            BuildRuntimeBaselineRoster(key, session, out var entries, out _, out var entryFlags);
            var msg = new SegmentDormancyCommitMsg
            {
                Slot = (byte)session.LocalSlot,
                SegmentX = key.X, SegmentY = key.Y,
                Epoch = AuthorityManager.EpochOf(key),
                Entries = entries, EntryFlags = entryFlags,
                RosterDigest = ComputeRosterDigest(entries, null, entryFlags),
            };
            InstrumentationCounters.DormancyCommitSent(entries.Count);
            // Even an empty roster commits: the host moves the lease to Dormant on receipt
            // instead of waiting out the crash-grace timeout. The host must also BROADCAST its
            // own commits — every peer keeps the canonical-store replica (I-12), and client
            // commits are relayed by the dispatch path this send doesn't take.
            Writer.Reset(); msg.Write(Writer);
            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
            if (session.IsHost) ApplySegmentDormancyCommit(msg, session);
        }

        /// <summary>Install an owner's final segment states into the canonical cache (every
        /// peer keeps the replica — the host-migration input, I-12) and freeze any live local
        /// copies at the committed pose. Sender validation happens at dispatch.</summary>
        internal static void ApplySegmentDormancyCommit(SegmentDormancyCommitMsg msg, NetSession session)
        {
            var key = new AuthorityManager.SegmentKey(msg.SegmentX, msg.SegmentY);
            msg.Entries ??= new List<EntityStateEntry>();
            msg.EntryFlags ??= new List<byte>();
            if (msg.EntryFlags.Count != msg.Entries.Count
                || msg.RosterDigest != ComputeRosterDigest(msg.Entries, null, msg.EntryFlags))
            {
                Plugin.Log.LogWarning($"[Dormancy] rejected malformed commit for segment {key} from P{msg.Slot + 1}");
                return;
            }
            float now = Time.unscaledTime;
            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var entry = msg.Entries[i];
                if (!NetIds.LifetimeMatches(entry.NetId, entry.Lifetime) || KilledNetIds.Contains(entry.NetId))
                    continue;
                var origin = BaselineEntryFlags.Origin(msg.EntryFlags[i]);
                var full = entry; full.FieldMask = EntityStateEntry.Fields.Full;
                FullState[entry.NetId] = full;
                if (origin == BaselineEntryOrigin.Generation)
                    FullStateOrigins[entry.NetId] = BaselineEntryOrigin.Generation;
                else FullStateOrigins.Remove(entry.NetId); // owner's last real simulator output
                if (!NetIds.TryGetInstanceId(entry.NetId, out int instanceId)) continue;
                try
                {
                    var data = ServiceLocator.Get<EntityManager>()?.GetEntity(instanceId);
                    if (data != null) data.MoveTo(new Vector3(entry.Pos.x, entry.Pos.y, data.position.z));
                }
                catch { }
                // Peers holding the object live freeze it at the committed pose.
                if (LiveEntities.TryGetValue(entry.NetId, out var se) && se != null
                    && origin != BaselineEntryOrigin.Generation)
                {
                    var rb = se.GetComponent<Rigidbody2D>();
                    if (rb != null) rb.position = entry.Pos;
                    var puppet = se.GetComponent<RemoteEntityPuppet>();
                    puppet?.PushSnapshot(now, entry.Pos, Vector2.zero, entry.Rot, entry.Aim);
                }
            }
            InstrumentationCounters.DormancyCommitApplied(msg.Entries.Count);
            if (session.IsHost)
                AuthorityManager.OnDormancyCommit(key, msg.Slot, session);
        }

        /// <summary>Late join: replay the canonical state cache so the rejoiner's dormant world
        /// carries last-simulated vitals and poses, not generation guesses.</summary>
        internal static void SendDormantState(NetSession session, ulong peer)
        {
            var states = FullState.Values
                .Where(value => !KilledNetIds.Contains(value.NetId)
                                && NetIds.LifetimeMatches(value.NetId, value.Lifetime))
                .OrderBy(value => value.NetId).ToList();
            const int chunkSize = 100;
            int total = Mathf.Min(states.Count, ushort.MaxValue);
            for (int start = 0; start < total || start == 0; start += chunkSize)
            {
                int count = Mathf.Min(chunkSize, total - start);
                var msg = new DormantStateMsg
                {
                    Start = (ushort)start, Total = (ushort)total,
                    Entries = new List<EntityStateEntry>(Mathf.Max(0, count)),
                    EntryFlags = new List<byte>(Mathf.Max(0, count)),
                };
                for (int i = 0; i < count; i++)
                {
                    var entry = states[start + i];
                    msg.Entries.Add(entry);
                    var origin = FullStateOrigins.TryGetValue(entry.NetId, out var known)
                        ? known : BaselineEntryOrigin.Live;
                    msg.EntryFlags.Add(BaselineEntryFlags.Pack(origin, false));
                }
                Writer.Reset(); msg.Write(Writer);
                session.SendReliable(peer, NetChannel.Events, Writer.ToSegment());
                if (total == 0) break;
            }
            Plugin.Log.LogInfo($"[Dormancy] canonical state cache sent ({total} entries)");
        }

        internal static void ApplyDormantState(DormantStateMsg msg)
        {
            if (msg.Entries == null) return;
            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var entry = msg.Entries[i];
                if (!NetIds.LifetimeMatches(entry.NetId, entry.Lifetime)
                    || KilledNetIds.Contains(entry.NetId)) continue;
                var full = entry; full.FieldMask = EntityStateEntry.Fields.Full;
                FullState[entry.NetId] = full;
                var origin = msg.EntryFlags != null && i < msg.EntryFlags.Count
                    ? BaselineEntryFlags.Origin(msg.EntryFlags[i]) : BaselineEntryOrigin.CoordinatorCache;
                if (origin == BaselineEntryOrigin.Live) FullStateOrigins.Remove(entry.NetId);
                else FullStateOrigins[entry.NetId] = origin;
            }
        }

        // ---------------------------------------------------------------- authority messages

        private static bool _loggedFirstAssign;
        private static bool _loggedFirstState;

        public static void ApplyAuthAssign(AuthAssignMsg msg)
        {
            if (!_loggedFirstAssign)
            {
                _loggedFirstAssign = true;
                Plugin.Log.LogInfo($"[Auth] first assignment batch applied ({msg.Entries.Count} entries)");
            }
            var egm = TryGetEgm();
            var session = NetSession.Instance;
            foreach (var (netId, owner) in msg.Entries)
            {
                byte prev = OwnerOf(netId);
                if (owner == byte.MaxValue)
                {
                    bool wasExplicit = FixedOwners.Remove(netId);
                    Owners.Remove(netId);
                    if (!wasExplicit) continue;
                    if (egm != null && NetIds.TryGetInstanceId(netId, out int clearedInstance))
                        try { ApplyOwnership(netId, clearedInstance); } catch { }
                    if (NetDiag.Enabled)
                        NetDiag.Log("Assign", $"{NetDiag.Describe(netId)} explicit {NetDiag.Owner(prev)} cleared — segment authority resumes");
                    continue;
                }
                bool wasFixed = FixedOwners.Contains(netId);
                FixedOwners.Add(netId);
                Owners[netId] = owner;
                if (prev == owner && wasFixed) continue; // reliable replay; components already armed
                if (egm != null && NetIds.TryGetInstanceId(netId, out int instanceId))
                {
                    try { ApplyOwnership(netId, instanceId); } catch { }
                }
                if (NetDiag.Enabled && prev != owner && session != null)
                {
                    string effect = owner == session.LocalSlot ? "now MINE (live)"
                        : prev == session.LocalSlot ? "handed away (now puppet)" : "puppet owner changed";
                    NetDiag.Log("Assign", $"{NetDiag.Describe(netId)} {NetDiag.Owner(prev)} -> {NetDiag.Owner(owner)} — {effect}");
                }
            }
        }

        private static EntityGameObjectManager TryGetEgm()
        {
            try { return ServiceLocator.Get<EntityGameObjectManager>(); } catch { return null; }
        }

        /// <summary>The game's own residency truth: segments whose entity GameObjects are
        /// currently instantiated on this machine (radius-3 streamer, one build per frame).</summary>
        internal static HashSet<Vector2Int> TryGetActiveSegments()
        {
            try
            {
                var egm = TryGetEgm();
                if (egm == null) return null;
                return Traverse.Create(egm).Field("activeSegments").GetValue<HashSet<Vector2Int>>();
            }
            catch { return null; }
        }

        /// <summary>Fixed-owner entities assigned to me with no canonical live object. Until
        /// dormancy is first-class (Phase 2/3), this gauge must hover at ~0; sustained growth is
        /// authority held without a simulator.</summary>
        internal static int CountFixedOwnedNotLive(NetSession session)
        {
            if (session == null) return 0;
            int count = 0;
            foreach (int netId in FixedOwners)
                if (Owners.TryGetValue(netId, out byte owner) && owner == session.LocalSlot
                    && !KilledNetIds.Contains(netId)
                    && (!LiveEntities.TryGetValue(netId, out var se) || se == null))
                    count++;
            return count;
        }

        // ---------------------------------------------------------------- state streaming

        /// <summary>Called from NetSession.Update while InGame: stream entities I own.</summary>
        public static void Tick(NetSession session)
        {
            TickDuplicateRetirements();
            if (!NetIds.ManifestComplete || Time.unscaledTime < _nextSendAt) return;
            _nextSendAt = Time.unscaledTime + 1f / Mathf.Max(1f,
                Mathf.Max(NetConfig.StateHz.Value, NetConfig.CombatStateHz.Value));

            AuthorityManager.TickResidency(session);
            DetectStarvedPuppets(session);
            SweepFirstSnapshotDeadlines(session);
            if (session.IsHost) PruneInterestRoutes(session);

            var egm = TryGetEgm();
            if (egm == null) return;

            var groups = new Dictionary<(AuthorityManager.SegmentKey key, uint epoch), List<EntityStateEntry>>();
            foreach (var netId in SpawnedUnitCandidates())
            {
                if (KilledNetIds.Contains(netId)) continue;
                bool currentlyMine = OwnerOf(netId) == session.LocalSlot;
                bool boundaryHandoff = false;
                AuthorityManager.SegmentKey sourceSegment = default;
                if (!currentlyMine)
                {
                    if (FixedOwners.Contains(netId)
                        || !LiveEntities.TryGetValue(netId, out var live) || live == null
                        || live.GetComponent<Unit>() == null
                        || live.GetComponent<RemoteEntityPuppet>() != null
                        || !SimulationSegments.TryGetValue(netId, out sourceSegment)
                        || AuthorityManager.OwnerOf(sourceSegment) != session.LocalSlot) continue;
                    boundaryHandoff = true;
                }
                if (!TryCollectEntry(egm, netId, out var entry, forceFull: boundaryHandoff)) continue;
                // WritePosition rounds to 1/32u. Group using that exact wire position too, or an
                // entity within 1/64u of a segment edge can arrive in the adjacent segment and be
                // rejected despite carrying the correct owner/epoch (visible as periodic snaps).
                entry.Pos = new Vector2(Mathf.RoundToInt(entry.Pos.x * 32f) / 32f,
                    Mathf.RoundToInt(entry.Pos.y * 32f) / 32f);
                var key = AuthorityManager.SegmentOf(entry.Pos);
                if (boundaryHandoff)
                {
                    SendBoundaryHandoff(session, entry, sourceSegment, key);
                    SimulationSegments.Remove(netId);
                    continue;
                }
                uint epoch = FixedOwners.Contains(netId) ? 0 : AuthorityManager.EpochOf(key);
                byte owner = FixedOwners.Contains(netId) ? OwnerOf(netId) : AuthorityManager.OwnerOf(key);
                if (owner != session.LocalSlot) continue; // crossed a lease boundary this frame
                if (!FixedOwners.Contains(netId)) SimulationSegments[netId] = key;
                var groupKey = (key, epoch);
                if (!groups.TryGetValue(groupKey, out var entries)) groups[groupKey] = entries = new List<EntityStateEntry>(32);
                entries.Add(entry);
            }

            var stateGroups = new List<EntityStateGroup>(groups.Count);
            foreach (var group in groups)
            {
                stateGroups.Add(new EntityStateGroup
                {
                    SegmentX = group.Key.key.X,
                    SegmentY = group.Key.key.Y,
                    Epoch = group.Key.epoch,
                    Entries = group.Value,
                });
            }
            if (stateGroups.Count == 0) return;

            byte slot = (byte)session.LocalSlot;
            uint timeMs = (uint)(Time.unscaledTime * 1000f);
            uint tick = ++_stateTick;
            if (!session.IsHost)
            {
                // A client has one route: send one complete tick to the host. The host needs the
                // full state for canonical positions/authority and interest-routes it onward.
                SendBundleToHost(session, slot, timeMs, tick, stateGroups);
                SendDirectBundles(session, slot, timeMs, tick, stateGroups);
                return;
            }

            foreach (var player in session.Players)
            {
                if (player == null || player.IsLocal || !player.Connected) continue;
                SelectInterestedGroups(player.Slot, slot, stateGroups, TargetGroups, out int droppedGroups, out int droppedEntries);
                SendBundleToPeer(session, player.PeerId, slot, timeMs, tick, TargetGroups, droppedGroups, droppedEntries);
            }
        }

        private static void SendBundleToHost(NetSession session, byte slot, uint timeMs, uint tick,
            List<EntityStateGroup> groups)
        {
            SendChunked(session, 0, toHost: true, slot, timeMs, tick, groups, 0, 0);
        }

        private static void SendBundleToPeer(NetSession session, ulong peer, byte slot, uint timeMs, uint tick,
            List<EntityStateGroup> groups, int droppedGroups, int droppedEntries)
        {
            if (groups.Count == 0)
            {
                InstrumentationCounters.StateInterestFiltered(droppedGroups, droppedEntries);
                return;
            }
            SendChunked(session, peer, toHost: false, slot, timeMs, tick, groups, droppedGroups, droppedEntries);
        }

        private static void SendChunked(NetSession session, ulong peer, bool toHost, byte slot,
            uint timeMs, uint tick, List<EntityStateGroup> groups, int droppedGroups, int droppedEntries)
        {
            BuildChunks(groups, ChunkScratch);
            ushort chunkCount = (ushort)Mathf.Min(ushort.MaxValue, ChunkScratch.Count);
            for (ushort i = 0; i < chunkCount; i++)
            {
                var chunk = ChunkScratch[i];
                Writer.Reset();
                new EntityStateBundleMsg
                {
                    Slot = slot, TimeMs = timeMs, Tick = tick,
                    ChunkIndex = i, ChunkCount = chunkCount, Groups = chunk,
                }.Write(Writer);
                if (toHost) session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
                else session.SendToPeer(peer, NetChannel.State, Writer.ToSegment(), reliable: false);
                InstrumentationCounters.StateBundleSent(chunk.Count, CountEntries(chunk), Writer.Position,
                    i == 0 ? droppedGroups : 0, i == 0 ? droppedEntries : 0);
                InstrumentationCounters.SnapshotChunkSent(Writer.Position, chunkCount);
            }
        }

        private static void BuildChunks(List<EntityStateGroup> groups, List<List<EntityStateGroup>> target)
        {
            target.Clear();
            List<EntityStateGroup> chunk = null;
            int bytes = 18; // message header + group count
            foreach (var group in groups)
            {
                EntityStateGroup output = default;
                foreach (var entry in group.Entries)
                {
                    int entryBytes = entry.EstimatedWireBytes;
                    bool needGroup = output.Entries == null;
                    int addition = entryBytes + (needGroup ? 13 : 0);
                    if (chunk == null || (bytes + addition > MaxStateDatagramBytes && bytes > 18))
                    {
                        chunk = new List<EntityStateGroup>(8);
                        target.Add(chunk);
                        bytes = 18;
                        output = default;
                        needGroup = true;
                        addition = entryBytes + 13;
                    }
                    if (needGroup)
                    {
                        output = new EntityStateGroup
                        {
                            SegmentX = group.SegmentX, SegmentY = group.SegmentY,
                            Epoch = group.Epoch, Entries = new List<EntityStateEntry>(16),
                        };
                        chunk.Add(output);
                    }
                    output.Entries.Add(entry);
                    bytes += addition;
                }
            }
        }

        private static int CountEntries(List<EntityStateGroup> groups)
        {
            if (groups == null) return 0;
            int count = 0;
            foreach (var group in groups) count += group.Entries?.Count ?? 0;
            return count;
        }

        private static void SelectInterestedGroups(byte targetSlot, byte sourceSlot, List<EntityStateGroup> source,
            List<EntityStateGroup> target, out int droppedGroups, out int droppedEntries)
        {
            target.Clear();
            droppedGroups = 0;
            droppedEntries = 0;
            foreach (var group in source)
            {
                bool selected = IsGroupInterestingTo(targetSlot, group);
                if (selected && NetSession.Instance is NetSession session && session.IsHost
                    && targetSlot != session.LocalSlot)
                {
                    selected = EnsureInterestRoute(session, targetSlot, sourceSlot, group);
                    if (selected && IsDirectRouteFresh(targetSlot, sourceSlot, group))
                    {
                        InstrumentationCounters.DirectRelayBypassed(group.Entries?.Count ?? 0);
                        selected = false;
                    }
                }
                if (selected) target.Add(group);
                else
                {
                    droppedGroups++;
                    droppedEntries += group.Entries?.Count ?? 0;
                }
            }
        }

        private static bool IsGroupInterestingTo(byte targetSlot, EntityStateGroup group)
        {
            if (!ShipSync.TryGetViewPosition(targetSlot, out Vector2 player))
                return true; // missing route/ship during a transition: correctness over filtering
            var entries = group.Entries;
            if (entries == null || entries.Count == 0) return false;
            // A starvation-promoted entity represents a concrete replica that was already live
            // on another peer. Keep servicing it until it despawns; a distance-only cutoff would
            // recreate the exact frozen-puppet failure that caused the promotion.
            foreach (var entry in entries)
                if (FixedOwners.Contains(entry.NetId)) return true;
            // A segment-width guard band ensures an entity is already streaming before it can
            // enter the camera, while still excluding rooms owned/loaded around distant players.
            float radius = Mathf.Max(25f, NetConfig.InterestRadius.Value)
                           + Mathf.Max(10f, Level.SegmentSize);
            float radiusSq = radius * radius;
            foreach (var entry in entries)
                if ((entry.Pos - player).sqrMagnitude <= radiusSq) return true;
            return false;
        }

        private static bool EnsureInterestRoute(NetSession session, byte targetSlot, byte sourceSlot,
            EntityStateGroup group)
        {
            var segment = new AuthorityManager.SegmentKey(group.SegmentX, group.SegmentY);
            var key = (targetSlot, segment);
            if (!InterestRoutes.TryGetValue(key, out var route) || route.Owner != sourceSlot)
            {
                if (route != null && route.Owner != session.LocalSlot)
                    SendDirectRoute(session, route.Owner, targetSlot, segment, route.Epoch, false);
                route = new InterestRoute { Owner = sourceSlot, Epoch = group.Epoch };
                InterestRoutes[key] = route;
                BeginRuntimeBaseline(session, sourceSlot, targetSlot, segment, group.Epoch, group.Epoch,
                    RuntimeBaselinePurpose.Interest, route);
                return false;
            }
            if (route.Epoch != group.Epoch)
            {
                // Same simulator, new lease epoch: the target's materialization is unaffected —
                // a full route re-baseline here turned every lease-churn epoch bump into a
                // multi-second snapshot gap. Refresh the epoch (and any direct route) in place.
                route.Epoch = group.Epoch;
                if (route.Ready && route.Owner != session.LocalSlot)
                    SendDirectRoute(session, route.Owner, targetSlot, segment, group.Epoch, true);
            }
            route.LastInterestedAt = Time.unscaledTime;
            if (!route.Ready && Time.unscaledTime - route.RequestedAt >= 1.5f)
                BeginRuntimeBaseline(session, sourceSlot, targetSlot, segment, group.Epoch, group.Epoch,
                    RuntimeBaselinePurpose.Interest, route);
            return route.Ready;
        }

        private static void PruneInterestRoutes(NetSession session)
        {
            if (Time.unscaledTime < _nextInterestPruneAt) return;
            _nextInterestPruneAt = Time.unscaledTime + 0.5f;
            foreach (var pair in InterestRoutes.ToArray())
            {
                var route = pair.Value;
                if (Time.unscaledTime - Mathf.Max(route.LastInterestedAt, route.RequestedAt) < 2f) continue;
                if (route.Owner != session.LocalSlot)
                    SendDirectRoute(session, route.Owner, pair.Key.target, pair.Key.segment, route.Epoch, false);
                PendingBaselines.Remove(route.RequestId);
                InterestRoutes.Remove(pair.Key);
            }
        }

        private static bool IsDirectRouteFresh(byte targetSlot, byte sourceSlot, EntityStateGroup group)
        {
            var key = (targetSlot, new AuthorityManager.SegmentKey(group.SegmentX, group.SegmentY));
            return InterestRoutes.TryGetValue(key, out var route) && route.Ready
                && route.Owner == sourceSlot && route.Epoch == group.Epoch
                && route.LastDirectPulse > 0f && Time.unscaledTime - route.LastDirectPulse < 2.5f;
        }

        private static void BeginRuntimeBaseline(NetSession session, byte sourceSlot, byte targetSlot,
            AuthorityManager.SegmentKey segment, uint sourceEpoch, uint targetEpoch,
            RuntimeBaselinePurpose purpose, InterestRoute interestRoute = null)
        {
            uint requestId = _nextBaselineRequestId++;
            if (requestId == 0) requestId = _nextBaselineRequestId++;
            PendingBaselines[requestId] = new BaselineRequest(sourceSlot, targetSlot, segment,
                sourceEpoch, targetEpoch, purpose);
            if (interestRoute != null)
            {
                interestRoute.RequestId = requestId;
                interestRoute.RequestedAt = Time.unscaledTime;
            }
            var request = new RuntimeBaselineRequestMsg
            {
                RequestId = requestId, SourceSlot = sourceSlot, TargetSlot = targetSlot,
                SegmentX = segment.X, SegmentY = segment.Y,
                SourceEpoch = sourceEpoch, TargetEpoch = targetEpoch, Purpose = purpose,
            };
            InstrumentationCounters.RuntimeBaselineRequested(purpose == RuntimeBaselinePurpose.Handoff);
            if (sourceSlot == session.LocalSlot) ApplyRuntimeBaselineRequest(request, session);
            else
            {
                if (session.TryGetPeerId(sourceSlot, out _))
                {
                    Writer.Reset(); request.Write(Writer);
                    session.SendReliableToSlot(sourceSlot, NetChannel.Combat, Writer.ToSegment());
                }
                else
                {
                    // Lost-owner recovery: the host's expanded state cache is the last canonical
                    // state it received. It is safer than promoting a target's generated pose.
                    BuildCachedBaselineRoster(segment, out var entries, out var entityTypes, out var entryFlags);
                    HostRouteRuntimeBaseline(new RuntimeBaselineMsg
                    {
                        RequestId = requestId, SourceSlot = sourceSlot, TargetSlot = targetSlot,
                        SegmentX = segment.X, SegmentY = segment.Y,
                        SourceEpoch = sourceEpoch, TargetEpoch = targetEpoch,
                        Tick = _stateTick, Purpose = purpose, Entries = entries, EntityTypes = entityTypes,
                        EntryFlags = entryFlags,
                        RosterDigest = ComputeRosterDigest(entries, entityTypes, entryFlags),
                    }, session);
                    InstrumentationCounters.RuntimeBaselineCacheFallback(entries.Count);
                }
            }
        }

        internal static void BeginHandoffBaseline(AuthorityManager.SegmentKey segment, byte sourceSlot,
            byte targetSlot, uint sourceEpoch, uint targetEpoch, NetSession session)
        {
            BeginRuntimeBaseline(session, sourceSlot, targetSlot, segment, sourceEpoch, targetEpoch,
                RuntimeBaselinePurpose.Handoff);
        }

        private static void BuildRuntimeBaselineRoster(AuthorityManager.SegmentKey segment,
            NetSession session, out List<EntityStateEntry> entries, out List<string> entityTypes,
            out List<byte> entryFlags)
        {
            entries = new List<EntityStateEntry>(32);
            entityTypes = new List<string>(32);
            entryFlags = new List<byte>(32);
            EntityManager em;
            try { em = ServiceLocator.Get<EntityManager>(); }
            catch { return; }
            var egm = TryGetEgm();
            var roster = new List<(int netId, string type, byte flags, EntityStateEntry state)>();
            // Query the game's spatial bucket directly. Walking every manifest identity and
            // calling EntityManager.GetEntity for each would turn every lease prepare into an
            // O(world²) scan on large maps.
            foreach (var data in em.GetEntitiesInSegment(new Vector2Int(segment.X, segment.Y)).ToArray())
            {
                if (data == null || !NetIds.TryGetNetId(data.instanceId, out int netId)) continue;
                if (KilledNetIds.Contains(netId) || FixedOwners.Contains(netId)) continue;
                if (!AuthorityManager.SegmentOf(data.position).Equals(segment)) continue;
                string entityType = data.entityId ?? string.Empty;
                LiveEntities.TryGetValue(netId, out var live);
                bool isUnit = live != null && live.GetComponent<Unit>() != null;
                SavableEntity prefab = null;
                if (!isUnit)
                {
                    prefab = MinionSync.FindPrefab(entityType);
                    isUnit = prefab != null && prefab.GetComponent<Unit>() != null;
                }
                // Physics props (crates, boxes, debris) carry synced canonical poses too; static
                // decor without a body stays deterministic and never enters rosters.
                bool isProp = !isUnit && (live != null
                    ? live.GetComponent<Rigidbody2D>() != null
                    : prefab != null && prefab.GetComponent<Rigidbody2D>() != null);
                if (!isUnit && !isProp) continue;
                // Non-unloadable entities are managed by bespoke systems. Only include one if a
                // concrete object is already present in the ordinary entity registry.
                if (!data.isUnloadable && live == null) continue;

                // Identity existence must never impersonate a simulator. Every entry says where
                // its state came from; the old unconditional HP=1/State=255 fallback silently
                // healed damaged entities and let non-resident sources "complete" handoffs.
                EntityStateEntry state;
                BaselineEntryOrigin origin;
                if (egm != null && OwnerOf(netId) == session.LocalSlot
                    && TryCollectEntry(egm, netId, out state, forceFull: true))
                {
                    origin = BaselineEntryOrigin.Live;
                }
                else if (FullState.TryGetValue(netId, out var cached)
                         && NetIds.LifetimeMatches(netId, cached.Lifetime))
                {
                    state = cached;
                    origin = FullStateOrigins.TryGetValue(netId, out var cachedOrigin)
                        ? cachedOrigin : BaselineEntryOrigin.LastKnown;
                    if (origin == BaselineEntryOrigin.Live) origin = BaselineEntryOrigin.LastKnown;
                }
                else
                {
                    state = new EntityStateEntry
                    {
                        NetId = netId,
                        Lifetime = NetIds.LifetimeOf(netId),
                        FieldMask = EntityStateEntry.Fields.Full,
                        Pos = data.position,
                        Vel = Vector2.zero,
                        Rot = data.rotation.eulerAngles.z,
                        Aim = Vector2.zero,
                        State = byte.MaxValue,
                        Fire = 0,
                        Ammo = byte.MaxValue,
                        HpFraction = 1f,
                        ShieldFraction = 0f,
                        BurnLevel = 0f,
                    };
                    origin = BaselineEntryOrigin.Generation;
                }
                state.Pos = new Vector2(Mathf.RoundToInt(state.Pos.x * 32f) / 32f,
                    Mathf.RoundToInt(state.Pos.y * 32f) / 32f);
                if (!AuthorityManager.SegmentOf(state.Pos).Equals(segment)) continue;
                state.FieldMask = EntityStateEntry.Fields.Full;
                InstrumentationCounters.BaselineEntryBuilt(origin);
                roster.Add((netId, entityType, BaselineEntryFlags.Pack(origin, isProp), state));
            }
            roster.Sort((a, b) => a.netId.CompareTo(b.netId));
            foreach (var item in roster)
            {
                entries.Add(item.state);
                entityTypes.Add(item.type);
                entryFlags.Add(item.flags);
            }
        }

        private static void BuildCachedBaselineRoster(AuthorityManager.SegmentKey segment,
            out List<EntityStateEntry> entries, out List<string> entityTypes, out List<byte> entryFlags)
        {
            entries = new List<EntityStateEntry>();
            entityTypes = new List<string>();
            entryFlags = new List<byte>();
            EntityManager em = null;
            try { em = ServiceLocator.Get<EntityManager>(); } catch { }
            foreach (var state in FullState.Values
                         .Where(value => AuthorityManager.SegmentOf(value.Pos).Equals(segment))
                         .OrderBy(value => value.NetId))
            {
                if (FixedOwners.Contains(state.NetId) || KilledNetIds.Contains(state.NetId)) continue;
                string entityType = string.Empty;
                bool isProp = false;
                if (NetIds.TryGetInstanceId(state.NetId, out int instanceId))
                {
                    entityType = em?.GetEntity(instanceId)?.entityId ?? string.Empty;
                    var prefab = MinionSync.FindPrefab(entityType);
                    isProp = prefab != null && prefab.GetComponent<Unit>() == null;
                }
                // The cache holds real simulator output — unless the cached value itself came
                // from a Generation-origin baseline, which must stay marked as such.
                var origin = FullStateOrigins.TryGetValue(state.NetId, out var cachedOrigin)
                             && cachedOrigin == BaselineEntryOrigin.Generation
                    ? BaselineEntryOrigin.Generation
                    : BaselineEntryOrigin.CoordinatorCache;
                InstrumentationCounters.BaselineEntryBuilt(origin);
                entries.Add(state);
                entityTypes.Add(entityType);
                entryFlags.Add(BaselineEntryFlags.Pack(origin, isProp));
            }
        }

        private static ulong ComputeRosterDigest(List<EntityStateEntry> entries, List<string> entityTypes,
            List<byte> entryFlags)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            for (int i = 0; i < entries.Count; i++)
            {
                unchecked
                {
                    ulong id = (uint)entries[i].NetId;
                    ulong lifetime = entries[i].Lifetime;
                    for (int b = 0; b < 4; b++) { hash ^= (byte)(id >> (b * 8)); hash *= prime; }
                    for (int b = 0; b < 4; b++) { hash ^= (byte)(lifetime >> (b * 8)); hash *= prime; }
                    string type = entityTypes != null && i < entityTypes.Count ? entityTypes[i] ?? string.Empty : string.Empty;
                    foreach (char c in type)
                    {
                        hash ^= (byte)c; hash *= prime;
                        hash ^= (byte)(c >> 8); hash *= prime;
                    }
                    hash ^= entryFlags != null && i < entryFlags.Count ? entryFlags[i] : (byte)0;
                    hash *= prime;
                    hash ^= 0xff; hash *= prime;
                }
            }
            return hash;
        }

        internal static void ApplyRuntimeBaselineRequest(RuntimeBaselineRequestMsg request, NetSession session)
        {
            if (request.SourceSlot != session.LocalSlot) return;
            var segment = new AuthorityManager.SegmentKey(request.SegmentX, request.SegmentY);
            BuildRuntimeBaselineRoster(segment, session, out var entries, out var entityTypes, out var entryFlags);
            if (entries.Count == 0 && request.Purpose == RuntimeBaselinePurpose.Handoff)
                BuildCachedBaselineRoster(segment, out entries, out entityTypes, out entryFlags);
            var baseline = new RuntimeBaselineMsg
            {
                RequestId = request.RequestId, SourceSlot = request.SourceSlot,
                TargetSlot = request.TargetSlot, SegmentX = request.SegmentX, SegmentY = request.SegmentY,
                SourceEpoch = request.SourceEpoch, TargetEpoch = request.TargetEpoch,
                Tick = _stateTick, Purpose = request.Purpose, Entries = entries, EntityTypes = entityTypes,
                EntryFlags = entryFlags,
                RosterDigest = ComputeRosterDigest(entries, entityTypes, entryFlags),
            };
            if (request.Purpose == RuntimeBaselinePurpose.Handoff)
                FreezeHandoffSegment(segment, request.TargetEpoch, entries);
            if (session.IsHost) HostRouteRuntimeBaseline(baseline, session);
            else
            {
                Writer.Reset(); baseline.Write(Writer);
                session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
            }
        }

        private static void FreezeHandoffSegment(AuthorityManager.SegmentKey segment, uint targetEpoch,
            List<EntityStateEntry> entries)
        {
            if (!FrozenHandoffSegments.Add((segment, targetEpoch))) return;
            float now = Time.unscaledTime;
            foreach (var entry in entries)
            {
                if (!LiveEntities.TryGetValue(entry.NetId, out var se) || se == null
                    || se.GetComponent<Unit>() == null) continue;
                var puppet = se.GetComponent<RemoteEntityPuppet>();
                if (puppet == null) puppet = se.gameObject.AddComponent<RemoteEntityPuppet>();
                puppet.NetId = entry.NetId;
                puppet.ResetSnapshots();
                puppet.PushSnapshot(now, entry.Pos, Vector2.zero, entry.Rot, entry.Aim);
            }
        }

        internal static void CancelHandoffFreeze(AuthorityManager.SegmentKey segment, uint epoch)
        {
            if (!FrozenHandoffSegments.Remove((segment, epoch))) return;
            if (FrozenHandoffSegments.Any(item => item.segment.Equals(segment))) return;
            var session = NetSession.Instance;
            if (session == null) return;
            foreach (var kv in LiveEntities)
            {
                var se = kv.Value;
                if (se == null || !AuthorityManager.TrySegmentOf(kv.Key, out var entitySegment)
                    || !entitySegment.Equals(segment)) continue;
                if (OwnerOf(kv.Key) != session.LocalSlot) continue;
                var puppet = se.GetComponent<RemoteEntityPuppet>();
                if (puppet != null) UnityEngine.Object.Destroy(puppet);
            }
        }

        internal static void FinalizeHandoffFreeze(AuthorityManager.SegmentKey segment, uint epoch) =>
            FrozenHandoffSegments.Remove((segment, epoch));

        internal static void HostRouteRuntimeBaseline(RuntimeBaselineMsg baseline, NetSession session)
        {
            if (!session.IsHost || !PendingBaselines.TryGetValue(baseline.RequestId, out var pending)) return;
            if (pending.Source != baseline.SourceSlot || pending.Target != baseline.TargetSlot
                || pending.Segment.X != baseline.SegmentX || pending.Segment.Y != baseline.SegmentY
                || pending.TargetEpoch != baseline.TargetEpoch || pending.Purpose != baseline.Purpose) return;
            baseline.Entries ??= new List<EntityStateEntry>();
            baseline.EntityTypes ??= new List<string>();
            baseline.EntryFlags ??= new List<byte>();
            if (baseline.EntityTypes.Count != baseline.Entries.Count
                || baseline.EntryFlags.Count != baseline.Entries.Count
                || baseline.RosterDigest != ComputeRosterDigest(baseline.Entries, baseline.EntityTypes, baseline.EntryFlags))
            {
                PendingBaselines.Remove(baseline.RequestId);
                InstrumentationCounters.RuntimeBaselineIncomplete();
                Plugin.Log.LogWarning($"[BaselineRoster] rejected malformed roster request={baseline.RequestId} " +
                    $"segment={pending.Segment} source=P{pending.Source + 1}");
                if (pending.Purpose == RuntimeBaselinePurpose.Handoff)
                    AuthorityManager.RejectBaselineHandoff(pending.Segment, pending.Target,
                        pending.TargetEpoch, baseline.Entries.Select(entry => entry.NetId).ToList(), session);
                return;
            }
            pending.ExpectedCount = baseline.Entries.Count;
            pending.RosterDigest = baseline.RosterDigest;
            if (baseline.TargetSlot == session.LocalSlot) ApplyRuntimeBaseline(baseline, session);
            else
            {
                Writer.Reset(); baseline.Write(Writer);
                session.SendReliableToSlot(baseline.TargetSlot, NetChannel.Combat, Writer.ToSegment());
            }
        }

        internal static void ApplyRuntimeBaseline(RuntimeBaselineMsg baseline, NetSession session)
        {
            if (baseline.TargetSlot != session.LocalSlot) return;
            var baselineSegment = new AuthorityManager.SegmentKey(baseline.SegmentX, baseline.SegmentY);
            if (!AuthorityManager.CanApplyRuntimeBaseline(baselineSegment, baseline.TargetEpoch, baseline.Purpose))
            {
                InstrumentationCounters.StaleEntityStateDropped();
                return;
            }
            EntityManager em = null;
            try { em = ServiceLocator.Get<EntityManager>(); } catch { }
            var egm = TryGetEgm();
            var activeSegments = TryGetActiveSegments();
            float now = Time.unscaledTime;
            var missing = new List<int>();
            var missingDetails = new List<string>();
            int materialized = 0;
            bool rosterValid = baseline.Entries != null && baseline.EntityTypes != null
                && baseline.EntryFlags != null
                && baseline.Entries.Count == baseline.EntityTypes.Count
                && baseline.Entries.Count == baseline.EntryFlags.Count
                && baseline.RosterDigest == ComputeRosterDigest(baseline.Entries, baseline.EntityTypes, baseline.EntryFlags);
            if (!rosterValid)
            {
                if (baseline.Entries != null) missing.AddRange(baseline.Entries.Select(entry => entry.NetId));
                missingDetails.Add("roster-digest");
            }
            for (int i = 0; rosterValid && i < baseline.Entries.Count; i++)
            {
                var entry = baseline.Entries[i];
                string expectedType = baseline.EntityTypes[i] ?? string.Empty;
                var origin = BaselineEntryFlags.Origin(baseline.EntryFlags[i]);
                bool isProp = BaselineEntryFlags.IsProp(baseline.EntryFlags[i]);
                if (!NetIds.LifetimeMatches(entry.NetId, entry.Lifetime))
                {
                    InstrumentationCounters.StaleLifetimeDropped();
                    InstrumentationCounters.RuntimeBaselineEntityMissing();
                    missing.Add(entry.NetId);
                    if (missingDetails.Count < 12) missingDetails.Add($"#{entry.NetId}:lifetime");
                    continue;
                }
                if (!NetIds.TryGetInstanceId(entry.NetId, out int instanceId))
                {
                    InstrumentationCounters.RuntimeBaselineEntityMissing();
                    missing.Add(entry.NetId);
                    if (missingDetails.Count < 12) missingDetails.Add($"#{entry.NetId}:identity");
                    continue;
                }
                EntityData data = null;
                try
                {
                    data = em?.GetEntity(instanceId);
                    if (data != null) data.MoveTo(new Vector3(entry.Pos.x, entry.Pos.y, data.position.z));
                }
                catch { }
                string failure = null;
                SavableEntity se = null;
                if (data == null) failure = "data";
                else if (!string.IsNullOrEmpty(expectedType)
                         && !string.Equals(data.entityId, expectedType, StringComparison.Ordinal))
                    failure = $"type:{data.entityId}->{expectedType}";
                else if (egm == null) failure = "egm";
                else if (!egm.TryGetSavableEntity(instanceId, out se) || se == null)
                {
                    var key = new Vector2Int(baseline.SegmentX, baseline.SegmentY);
                    if (activeSegments == null || !activeSegments.Contains(key)) failure = "segment-inactive";
                    else
                    {
                        try { se = egm.SpawnObjectForEntity(data); }
                        catch (Exception e) { failure = "spawn:" + e.GetType().Name; }
                    }
                }
                if (failure == null && se == null) failure = "no-object";
                if (failure == null && !isProp && se.GetComponent<Unit>() == null) failure = "not-unit";
                if (failure == null && !string.IsNullOrEmpty(expectedType)
                    && !string.Equals(se.EntityData?.entityId, expectedType, StringComparison.Ordinal))
                    failure = $"bound-type:{se.EntityData?.entityId}->{expectedType}";
                if (failure != null)
                {
                    InstrumentationCounters.RuntimeBaselineEntityMissing();
                    missing.Add(entry.NetId);
                    if (missingDetails.Count < 12) missingDetails.Add($"#{entry.NetId}:{failure}");
                    continue;
                }

                RegisterLive(entry.NetId, se);
                try { ApplyOwnership(entry.NetId, instanceId); } catch { }
                materialized++;
                InstrumentationCounters.RuntimeBaselineEntityMaterialized();
                // The cache is written only for entries that actually bound; a rejected entry
                // must not poison the lost-owner fallback with state nothing ever validated.
                var full = entry; full.FieldMask = EntityStateEntry.Fields.Full;
                FullState[entry.NetId] = full;
                if (origin == BaselineEntryOrigin.Live) FullStateOrigins.Remove(entry.NetId);
                else FullStateOrigins[entry.NetId] = origin;
                var rb = se.GetComponent<Rigidbody2D>();
                if (rb != null) rb.position = entry.Pos;
                if (isProp) continue; // canonical pose applied; props carry no AI/vitals state
                if (origin == BaselineEntryOrigin.Generation)
                {
                    // Never-simulated entity: the pose positions the object, but no simulator
                    // produced it — leaving HasSnapshot false keeps starvation/promotion honest,
                    // and local generation vitals are already the same truth this entry guessed.
                    var generationPuppet = se.GetComponent<RemoteEntityPuppet>();
                    generationPuppet?.ResetSnapshots();
                    continue;
                }
                var puppet = se.GetComponent<RemoteEntityPuppet>();
                puppet?.ResetSnapshots();
                puppet?.PushSnapshot(now, entry.Pos, entry.Vel, entry.Rot, entry.Aim);
                UnitStatus.WriteState(se, entry.State);
                UnitStatus.WriteFireState(se, entry.Fire);
                UnitStatus.WriteShieldFraction(se, entry.ShieldFraction);
                UnitStatus.WriteAmmoFraction(se, entry.Ammo);
                UnitStatus.WriteBurnLevel(se, entry.BurnLevel);
                try
                {
                    var dr = se.GetComponent<DamagableResource>();
                    if (dr != null && dr.MaxHealth > 0) dr.CurrentHealth = entry.HpFraction * dr.MaxHealth;
                }
                catch { }
            }
            InstrumentationCounters.RuntimeBaselineApplied(baseline.Entries.Count,
                baseline.Purpose == RuntimeBaselinePurpose.Handoff);
            if (!rosterValid || missing.Count > 0)
            {
                InstrumentationCounters.RuntimeBaselineIncomplete();
                Plugin.Log.LogWarning($"[BaselineRoster] incomplete request={baseline.RequestId} " +
                    $"segment={baselineSegment} target=P{session.LocalSlot + 1} " +
                    $"materialized={materialized}/{baseline.Entries.Count} missing={string.Join(",", missingDetails)}");
            }
            var ack = new RuntimeBaselineAckMsg
            {
                RequestId = baseline.RequestId, TargetSlot = baseline.TargetSlot,
                SegmentX = baseline.SegmentX, SegmentY = baseline.SegmentY,
                TargetEpoch = baseline.TargetEpoch, Purpose = baseline.Purpose,
                RosterDigest = baseline.RosterDigest,
                Installed = rosterValid && missing.Count == 0,
                ExpectedCount = baseline.Entries.Count,
                MaterializedCount = materialized,
                MissingNetIds = missing,
            };
            if (session.IsHost) ApplyRuntimeBaselineAck(ack, baseline.TargetSlot, session);
            else
            {
                Writer.Reset(); ack.Write(Writer);
                session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
            }
        }

        internal static void ApplyRuntimeBaselineAck(RuntimeBaselineAckMsg ack, byte senderSlot, NetSession session)
        {
            if (!session.IsHost || senderSlot != ack.TargetSlot
                || !PendingBaselines.TryGetValue(ack.RequestId, out var pending)) return;
            if (pending.Target != ack.TargetSlot || pending.TargetEpoch != ack.TargetEpoch
                || pending.Purpose != ack.Purpose || pending.Segment.X != ack.SegmentX
                || pending.Segment.Y != ack.SegmentY
                || pending.ExpectedCount != ack.ExpectedCount
                || pending.RosterDigest != ack.RosterDigest) return;
            PendingBaselines.Remove(ack.RequestId);
            if (!ack.Complete)
            {
                InstrumentationCounters.RuntimeBaselineIncomplete();
                Plugin.Log.LogWarning($"[BaselineRoster] target P{ack.TargetSlot + 1} not ready for " +
                    $"segment={pending.Segment} epoch={ack.TargetEpoch} " +
                    $"materialized={ack.MaterializedCount}/{ack.ExpectedCount} " +
                    $"missing={string.Join(",", (ack.MissingNetIds ?? new List<int>()).Take(16).Select(id => "#" + id))}");
                if (ack.Purpose == RuntimeBaselinePurpose.Handoff)
                    AuthorityManager.RejectBaselineHandoff(pending.Segment, ack.TargetSlot,
                        ack.TargetEpoch, ack.MissingNetIds ?? new List<int>(), session);
                else
                {
                    var failedKey = (ack.TargetSlot, pending.Segment);
                    if (InterestRoutes.TryGetValue(failedKey, out var failedRoute)
                        && failedRoute.RequestId == ack.RequestId)
                    {
                        failedRoute.Ready = false;
                        failedRoute.RequestId = 0;
                        failedRoute.RequestedAt = Time.unscaledTime;
                    }
                }
                return;
            }
            InstrumentationCounters.RuntimeBaselineAcked(ack.Purpose == RuntimeBaselinePurpose.Handoff);
            if (ack.Purpose == RuntimeBaselinePurpose.Handoff)
            {
                AuthorityManager.CompleteBaselineHandoff(pending.Segment, ack.TargetSlot, ack.TargetEpoch, session);
                return;
            }
            var key = (ack.TargetSlot, pending.Segment);
            if (InterestRoutes.TryGetValue(key, out var route) && route.RequestId == ack.RequestId)
            {
                route.Ready = true;
                if (route.Owner != session.LocalSlot)
                    SendDirectRoute(session, route.Owner, ack.TargetSlot, pending.Segment, route.Epoch, true);
            }
        }

        private static void SendDirectRoute(NetSession session, byte owner, byte target,
            AuthorityManager.SegmentKey segment, uint epoch, bool enabled)
        {
            if (!session.UsingSteam) return; // loopback transport is intentionally star-only
            var msg = new DirectRouteMsg
            {
                OwnerSlot = owner, TargetSlot = target, SegmentX = segment.X, SegmentY = segment.Y,
                Epoch = epoch, Enabled = enabled,
            };
            Writer.Reset(); msg.Write(Writer);
            session.SendReliableToSlot(owner, NetChannel.Combat, Writer.ToSegment());
        }

        internal static void ApplyDirectRoute(DirectRouteMsg route, NetSession session)
        {
            if (route.OwnerSlot != session.LocalSlot) return;
            var key = (route.TargetSlot, new AuthorityManager.SegmentKey(route.SegmentX, route.SegmentY));
            if (route.Enabled) DirectSendRoutes[key] = route.Epoch;
            else DirectSendRoutes.Remove(key);
        }

        internal static void OnDirectPeerLost(byte slot)
        {
            foreach (var route in DirectSendRoutes.Keys.Where(key => key.target == slot).ToArray())
                DirectSendRoutes.Remove(route);
            foreach (var pulse in NextDirectPulse.Keys.Where(key => key.owner == slot).ToArray())
                NextDirectPulse.Remove(pulse);
        }

        private static void SendDirectBundles(NetSession session, byte slot, uint timeMs, uint tick,
            List<EntityStateGroup> groups)
        {
            if (DirectSendRoutes.Count == 0) return;
            foreach (var route in DirectSendRoutes.ToArray())
            {
                if (!session.TryGetPeerId(route.Key.target, out ulong peer)) continue;
                var selected = new List<EntityStateGroup>(1);
                foreach (var group in groups)
                    if (group.SegmentX == route.Key.segment.X && group.SegmentY == route.Key.segment.Y
                        && group.Epoch == route.Value) selected.Add(group);
                if (selected.Count == 0) continue;
                SendBundleToPeer(session, peer, slot, timeMs, tick, selected, 0, 0);
                InstrumentationCounters.DirectSnapshotSent(CountEntries(selected));
            }
        }

        private static void SendBoundaryHandoff(NetSession session, EntityStateEntry entry,
            AuthorityManager.SegmentKey from, AuthorityManager.SegmentKey to)
        {
            byte target = AuthorityManager.OwnerOf(to);
            var msg = new EntityBoundaryHandoffMsg
            {
                SourceSlot = (byte)session.LocalSlot, TargetSlot = target,
                FromX = from.X, FromY = from.Y, FromEpoch = AuthorityManager.EpochOf(from),
                ToX = to.X, ToY = to.Y, ToEpoch = AuthorityManager.EpochOf(to), Entry = entry,
            };
            Writer.Reset(); msg.Write(Writer);
            session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
            FreezeBoundaryEntity(entry);
            InstrumentationCounters.EntityBoundaryHandoffSent();
            if (NetDiag.Enabled) NetDiag.Log("Handoff", $"{NetDiag.Describe(entry.NetId)} crossed {from}->{to}; " +
                $"P{session.LocalSlot + 1}->P{target + 1} reliable final state");
        }

        private static void FreezeBoundaryEntity(EntityStateEntry entry)
        {
            if (!LiveEntities.TryGetValue(entry.NetId, out var se) || se == null
                || se.GetComponent<Unit>() == null) return;
            var puppet = se.GetComponent<RemoteEntityPuppet>();
            if (puppet == null) puppet = se.gameObject.AddComponent<RemoteEntityPuppet>();
            puppet.NetId = entry.NetId;
            puppet.ResetSnapshots();
            puppet.PushSnapshot(Time.unscaledTime, entry.Pos, Vector2.zero, entry.Rot, entry.Aim);
        }

        internal static bool ValidateBoundaryHandoff(EntityBoundaryHandoffMsg msg, byte senderSlot)
        {
            if (senderSlot != msg.SourceSlot || !NetIds.LifetimeMatches(msg.Entry.NetId, msg.Entry.Lifetime))
                return false;
            var from = new AuthorityManager.SegmentKey(msg.FromX, msg.FromY);
            var to = new AuthorityManager.SegmentKey(msg.ToX, msg.ToY);
            return AuthorityManager.IsStateAuthority(msg.Entry.NetId, from, msg.SourceSlot, msg.FromEpoch)
                && AuthorityManager.OwnerOf(to) == msg.TargetSlot
                && AuthorityManager.EpochOf(to) == msg.ToEpoch;
        }

        internal static void ApplyBoundaryHandoff(EntityBoundaryHandoffMsg msg)
        {
            if (!NetIds.LifetimeMatches(msg.Entry.NetId, msg.Entry.Lifetime))
            {
                InstrumentationCounters.StaleLifetimeDropped();
                return;
            }
            var entry = msg.Entry; entry.FieldMask = EntityStateEntry.Fields.Full;
            FullState[entry.NetId] = entry;
            FullStateOrigins.Remove(entry.NetId); // reliable final state from the live authority
            if (!NetIds.TryGetInstanceId(entry.NetId, out int instanceId)) return;
            try
            {
                var data = ServiceLocator.Get<EntityManager>()?.GetEntity(instanceId);
                if (data != null) data.MoveTo(new Vector3(entry.Pos.x, entry.Pos.y, data.position.z));
            }
            catch { }
            var egm = TryGetEgm();
            if (egm != null && egm.TryGetSavableEntity(instanceId, out var se) && se != null)
            {
                var rb = se.GetComponent<Rigidbody2D>();
                if (rb != null) rb.position = entry.Pos;
                var puppet = se.GetComponent<RemoteEntityPuppet>();
                puppet?.ResetSnapshots();
                puppet?.PushSnapshot(Time.unscaledTime, entry.Pos, Vector2.zero, entry.Rot, entry.Aim);
                try { ApplyOwnership(entry.NetId, instanceId); } catch { }
                UnitStatus.WriteState(se, entry.State);
                UnitStatus.WriteFireState(se, entry.Fire);
                UnitStatus.WriteShieldFraction(se, entry.ShieldFraction);
                UnitStatus.WriteAmmoFraction(se, entry.Ammo);
                UnitStatus.WriteBurnLevel(se, entry.BurnLevel);
                try
                {
                    var dr = se.GetComponent<DamagableResource>();
                    if (dr != null && dr.MaxHealth > 0) dr.CurrentHealth = entry.HpFraction * dr.MaxHealth;
                }
                catch { }
            }
            InstrumentationCounters.EntityBoundaryHandoffApplied();
        }

        private static readonly Dictionary<(byte owner, AuthorityManager.SegmentKey segment), float> NextDirectPulse
            = new Dictionary<(byte, AuthorityManager.SegmentKey), float>();

        internal static void NoteDirectBundle(ulong senderPeer, EntityStateBundleMsg bundle, NetSession session)
        {
            if (session.IsHost || session.Players[session.HostSlot]?.PeerId == senderPeer) return;
            var sender = session.Players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == senderPeer);
            if (sender == null || sender.Slot != bundle.Slot) return;
            foreach (var group in bundle.Groups)
            {
                var segment = new AuthorityManager.SegmentKey(group.SegmentX, group.SegmentY);
                var key = (bundle.Slot, segment);
                if (NextDirectPulse.TryGetValue(key, out float next) && Time.unscaledTime < next) continue;
                NextDirectPulse[key] = Time.unscaledTime + 0.75f;
                var pulse = new DirectRoutePulseMsg
                {
                    OwnerSlot = bundle.Slot, TargetSlot = (byte)session.LocalSlot,
                    SegmentX = group.SegmentX, SegmentY = group.SegmentY, Epoch = group.Epoch,
                };
                Writer.Reset(); pulse.Write(Writer);
                session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
                InstrumentationCounters.DirectSnapshotReceived(group.Entries?.Count ?? 0);
            }
        }

        internal static void ApplyDirectRoutePulse(DirectRoutePulseMsg pulse, byte senderSlot, NetSession session)
        {
            if (!session.IsHost || senderSlot != pulse.TargetSlot) return;
            var key = (pulse.TargetSlot, new AuthorityManager.SegmentKey(pulse.SegmentX, pulse.SegmentY));
            if (InterestRoutes.TryGetValue(key, out var route) && route.Ready
                && route.Owner == pulse.OwnerSlot && route.Epoch == pulse.Epoch)
                route.LastDirectPulse = Time.unscaledTime;
        }

        private static void DetectStarvedPuppets(NetSession session)
        {
            float now = Time.unscaledTime;
            if (_availabilityRecoveryReadyAt <= 0)
            {
                _availabilityRecoveryReadyAt = now + AvailabilityStartupGrace;
                Plugin.Log.LogInfo($"[Availability] recovery gate opens in {AvailabilityStartupGrace:0.0}s; " +
                    "waiting for initial entity snapshots");
            }
            if (now < _availabilityRecoveryReadyAt) return;

            foreach (var kv in LiveEntities)
            {
                int netId = kv.Key;
                var entity = kv.Value;
                if (entity == null || KilledNetIds.Contains(netId)
                    || entity.GetComponent<DuplicateEntityInert>() != null) continue;
                var puppet = entity.GetComponent<RemoteEntityPuppet>();
                if (puppet == null || puppet.NetId < 0) continue;
                float staleAfter = puppet.HasSnapshot ? StarvedSnapshotAfter : StarvedNeverAfter;
                if (puppet.PuppetAge < staleAfter || puppet.SnapshotAge < staleAfter) continue;
                if (NextStarvedRequestAt.TryGetValue(netId, out float next) && now < next) continue;

                // Dormant entities are owed nothing: nobody simulates them by agreement, and
                // since WE hold the live object our residency report is already driving an
                // activation grant. Requesting per-entity promotion here would race it.
                if (OwnerOf(netId) == AuthorityManager.DormantOwner)
                {
                    NextStarvedRequestAt[netId] = now + StarvedRequestRetry;
                    continue;
                }

                // The authority table can already name us while the local component still reflects
                // the preceding lease. Repair that local convergence directly; promoting P2->P2
                // would create a fixed-owner exception and another avoidable handoff later.
                if (OwnerOf(netId) == session.LocalSlot)
                {
                    NextStarvedRequestAt[netId] = now + StarvedRequestRetry;
                    if (NetIds.TryGetInstanceId(netId, out int localInstance))
                    {
                        ApplyOwnership(netId, localInstance);
                        InstrumentationCounters.LocalAuthorityComponentRepaired();
                        Plugin.Log.LogInfo($"[Availability] repaired local authority component for " +
                            $"{NetDiag.Describe(netId)}; no ownership promotion needed");
                    }
                    continue;
                }

                if (!IsStableAvailabilityCandidate(entity, puppet, out float distance, out _))
                {
                    // Rechecking every state tick is needless work when a starved object is merely
                    // in the loader's retention fringe. Count at most one deferral/entity/second.
                    NextStarvedRequestAt[netId] = now + StarvedCandidateRecheck;
                    InstrumentationCounters.AvailabilityCandidateDeferred();
                    continue;
                }

                NextStarvedRequestAt[netId] = now + StarvedRequestRetry;
                InstrumentationCounters.StarvedOwnershipRequested();

                string segment = AuthorityManager.TrySegmentOf(netId, out var key) ? key.ToString() : "unknown";
                Plugin.Log.LogWarning($"[Availability] requesting {NetDiag.Describe(netId)} segment={segment} " +
                    $"for P{session.LocalSlot + 1}; owner=P{OwnerOf(netId) + 1} distance={distance:0.0} " +
                    $"residence={puppet.PuppetAge:0.00}s snapshotAge=" +
                    (float.IsPositiveInfinity(puppet.SnapshotAge) ? "never" : $"{puppet.SnapshotAge:0.00}s"));

                if (session.IsHost)
                {
                    ApplyStarvedOwnershipRequest(netId, (byte)session.LocalSlot, session);
                    continue;
                }
                Writer.Reset();
                new EntityStarvedRequestMsg { NetId = netId }.Write(Writer);
                session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
            }
        }

        /// <summary>A committed lease whose owner is someone else owes this machine snapshots for
        /// any entities it holds live in that segment. Recorded on commit, cleared by the first
        /// bundle group carrying the committed epoch, judged by a 1 Hz sweep.</summary>
        internal static void NoteLeaseCommitted(AuthorityManager.SegmentKey segment, uint epoch, byte owner)
        {
            var session = NetSession.Instance;
            if (session == null || owner == session.LocalSlot) return;
            if (owner == AuthorityManager.DormantOwner)
            {
                FirstSnapshotDeadlines.Remove(segment); // dormant: nobody owes anybody snapshots
                return;
            }
            FirstSnapshotDeadlines[segment] = (epoch, owner, Time.unscaledTime);
        }

        private static void SweepFirstSnapshotDeadlines(NetSession session)
        {
            if (FirstSnapshotDeadlines.Count == 0 || Time.unscaledTime < _nextFirstSnapshotSweepAt) return;
            _nextFirstSnapshotSweepAt = Time.unscaledTime + 1f;
            List<AuthorityManager.SegmentKey> expired = null;
            foreach (var kv in FirstSnapshotDeadlines)
            {
                if (Time.unscaledTime - kv.Value.committedAt < FirstSnapshotDeadlineSeconds) continue;
                (expired ??= new List<AuthorityManager.SegmentKey>(4)).Add(kv.Key);
            }
            if (expired == null) return;
            foreach (var segment in expired)
            {
                var awaited = FirstSnapshotDeadlines[segment];
                FirstSnapshotDeadlines.Remove(segment);
                // A deadline only counts as missed if this machine actually holds a live object
                // there that the committed owner should be servicing.
                if (!AwaitingStateInSegment(segment, awaited.owner, session)) continue;
                InstrumentationCounters.FirstSnapshotDeadlineMissed();
                Plugin.Log.LogWarning($"[Residency] no snapshots from P{awaited.owner + 1} for segment {segment} " +
                    $"within {FirstSnapshotDeadlineSeconds:0.#}s of epoch={awaited.epoch} commit — " +
                    "owner may hold authority without a live simulator");
            }
        }

        private static bool AwaitingStateInSegment(AuthorityManager.SegmentKey segment, byte owner,
            NetSession session)
        {
            if (owner == session.LocalSlot) return false;
            foreach (var kv in LiveEntities)
            {
                var se = kv.Value;
                if (se == null || KilledNetIds.Contains(kv.Key)) continue;
                if (se.GetComponent<Unit>() == null && se.GetComponent<Rigidbody2D>() == null) continue;
                if (!AuthorityManager.TrySegmentOf(kv.Key, out var key) || !key.Equals(segment)) continue;
                if (OwnerOf(kv.Key) != owner) continue;
                return true;
            }
            return false;
        }

        /// <summary>A concrete object is only a useful simulator candidate while it is well inside
        /// that player's streamed area. TransferRadius provides a margin from InterestRadius so a
        /// newly promoted entity is not immediately unloaded and released again.</summary>
        internal static bool IsStableAvailabilityCandidate(SavableEntity entity, RemoteEntityPuppet puppet,
            out float distance, out string reason)
        {
            distance = float.PositiveInfinity;
            if (entity == null || puppet == null)
            {
                reason = "missing";
                return false;
            }
            if (ShipSync.LocalShip == null)
            {
                reason = "no-local-ship";
                return false;
            }
            // A generated GameObject proves identity, not its current position, so a zero-snapshot
            // replica is normally refused: promoting it would turn a stale generation pose into
            // canonical truth. But that refusal deadlocked permanently when the owner never had a
            // live simulator at all (authority without residency — the observed 30-minute frozen
            // puppets). Prolonged silence is itself the evidence: past SilentOwnerPromotionAfter
            // no snapshot is ever coming, and this concrete object is the best state in existence.
            float silence = puppet.HasSnapshot ? puppet.SnapshotAge : puppet.PuppetAge;
            if (silence <= SilentOwnerPromotionAfter)
            {
                if (!puppet.HasSnapshot)
                {
                    reason = "no-authoritative-pose";
                    return false;
                }
                if (puppet.SnapshotAge > 5f)
                {
                    reason = "authoritative-pose-too-old";
                    return false;
                }
            }
            distance = Vector2.Distance(entity.transform.position, ShipSync.LocalShip.transform.position);
            if (float.IsNaN(distance) || float.IsInfinity(distance))
            {
                reason = "invalid-position";
                return false;
            }
            if (puppet.PuppetAge < StarvedCandidateResidence)
            {
                reason = "unsettled";
                return false;
            }

            float segment = Level.SegmentSize > 0 ? Level.SegmentSize : 25f;
            float interestMargin = Mathf.Max(10f, segment * 0.5f);
            float radius = Mathf.Max(15f, Mathf.Min(NetConfig.TransferRadius.Value,
                NetConfig.InterestRadius.Value - interestMargin));
            if (distance > radius)
            {
                reason = $"far>{radius:0.#}";
                return false;
            }
            reason = "eligible";
            return true;
        }

        /// <summary>Begin a two-phase promotion for one proven-live entity. A remote candidate
        /// must ACK that the concrete netId is still instantiated before authority changes.</summary>
        public static void ApplyStarvedOwnershipRequest(int netId, byte requester, NetSession session)
        {
            if (!session.IsHost || KilledNetIds.Contains(netId)
                || !NetIds.TryGetInstanceId(netId, out _)) return;
            var player = session.Players.FirstOrDefault(p => p != null && p.Connected && p.Slot == requester);
            if (player == null) return;

            float now = Time.unscaledTime;
            byte previous = OwnerOf(netId);
            // Includes already-explicit and segment-owned cases. The requester repairs a stale
            // local puppet component itself; there is no authority change to transact here.
            if (previous == requester) return;
            if (LastStarvedPromotionAt.TryGetValue(netId, out float last)
                && now - last < StarvedPromotionCooldown) return;
            if (PendingPromotions.TryGetValue(netId, out var pending)
                && now - pending.preparedAt < StarvedPromotionCooldown) return;

            if (requester == session.LocalSlot)
            {
                if (!LiveEntities.TryGetValue(netId, out var local) || local == null)
                {
                    InstrumentationCounters.AvailabilityCandidateDeferred();
                    if (NetDiag.Enabled)
                        NetDiag.Log("Availability", $"declined local promotion for {NetDiag.Describe(netId)} — not-live");
                    return;
                }
                var localPuppet = local.GetComponent<RemoteEntityPuppet>();
                if (localPuppet == null)
                {
                    InstrumentationCounters.AvailabilityCandidateDeferred();
                    if (NetDiag.Enabled)
                        NetDiag.Log("Availability", $"declined local promotion for {NetDiag.Describe(netId)} — not-puppet");
                    return;
                }
                if (!IsStableAvailabilityCandidate(local, localPuppet, out _, out string reason))
                {
                    InstrumentationCounters.AvailabilityCandidateDeferred();
                    if (NetDiag.Enabled)
                        NetDiag.Log("Availability", $"declined local promotion for {NetDiag.Describe(netId)} — {reason}");
                    return;
                }
                CommitStarvedPromotion(netId, requester, previous, session);
                return;
            }

            PendingPromotions[netId] = (requester, previous, now);
            Writer.Reset();
            new EntityAuthorityPrepareMsg { NetId = netId }.Write(Writer);
            session.SendToPeer(player.PeerId, NetChannel.Events, Writer.ToSegment(), reliable: true);
            Plugin.Log.LogInfo($"[Availability] prepare {NetDiag.Describe(netId)} for P{requester + 1}; awaiting live-entity ACK");
        }

        public static void ApplyAuthorityPrepare(EntityAuthorityPrepareMsg msg, NetSession session)
        {
            if (session.IsHost || KilledNetIds.Contains(msg.NetId)) return;
            if (!LiveEntities.TryGetValue(msg.NetId, out var entity) || entity == null
                || entity.GetComponent<RemoteEntityPuppet>() is not RemoteEntityPuppet puppet)
            {
                Plugin.Log.LogInfo($"[Availability] declined prepare for {NetDiag.Describe(msg.NetId)} — entity streamed out");
                return;
            }
            if (!IsStableAvailabilityCandidate(entity, puppet, out float distance, out string reason))
            {
                InstrumentationCounters.AvailabilityCandidateDeferred();
                Plugin.Log.LogInfo($"[Availability] declined prepare for {NetDiag.Describe(msg.NetId)} — " +
                    $"{reason}, distance={distance:0.0}, residence={puppet.PuppetAge:0.00}s");
                return;
            }

            Writer.Reset();
            new EntityAuthorityAckMsg { NetId = msg.NetId }.Write(Writer);
            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
            Plugin.Log.LogInfo($"[Availability] ready ACK for {NetDiag.Describe(msg.NetId)} " +
                $"distance={distance:0.0} residence={puppet.PuppetAge:0.00}s");
        }

        public static void ApplyAuthorityReadyAck(int netId, byte requester, NetSession session)
        {
            if (!session.IsHost || !PendingPromotions.TryGetValue(netId, out var pending)
                || pending.candidate != requester) return;
            PendingPromotions.Remove(netId);
            if (Time.unscaledTime - pending.preparedAt > StarvedPromotionCooldown) return;
            CommitStarvedPromotion(netId, requester, pending.previous, session);
        }

        private static void CommitStarvedPromotion(int netId, byte requester, byte previous, NetSession session)
        {
            LastStarvedPromotionAt[netId] = Time.unscaledTime;

            var assign = new AuthAssignMsg
            {
                Entries = new List<(int netId, byte owner)> { (netId, requester) },
            };
            ApplyAuthAssign(assign);
            Writer.Reset();
            assign.Write(Writer);
            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
            InstrumentationCounters.StarvedOwnershipPromoted();
            string segment = AuthorityManager.TrySegmentOf(netId, out var key) ? key.ToString() : "unknown";
            Plugin.Log.LogWarning($"[Availability] promoted {NetDiag.Describe(netId)} segment={segment} " +
                $"P{previous + 1}->P{requester + 1}; explicit entity authority now bypasses interest filtering");
        }

        private static bool IsSpawnedHere(EntityGameObjectManager egm, int netId)
        {
            return NetIds.TryGetInstanceId(netId, out int instanceId)
                   && egm.TryGetSavableEntity(instanceId, out var se) && se != null;
        }

        private static readonly Dictionary<int, float> NextReleaseAt = new Dictionary<int, float>();

        private static void MaybeReleaseAuthority(NetSession session, int netId)
        {
            if (NextReleaseAt.TryGetValue(netId, out float at) && Time.unscaledTime < at) return;
            NextReleaseAt[netId] = Time.unscaledTime + 5f;
            if (NetDiag.Enabled)
                NetDiag.Log("Release", $"{NetDiag.Describe(netId)} — I own it but it isn't spawned here; asking host to take it back");
            if (session.IsHost)
            {
                ApplyAuthRelease(new AuthReleaseMsg { NetId = netId }, session);
            }
            else
            {
                NetStats.AuthReleases++;
                Writer.Reset();
                new AuthReleaseMsg { NetId = netId }.Write(Writer);
                session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
            }
        }

        /// <summary>Host: an explicit owner streamed the entity out. Clear the entity exception
        /// and resume ordinary segment authority until another concrete viewer proves readiness.</summary>
        public static void ApplyAuthRelease(AuthReleaseMsg msg, NetSession session)
        {
            byte releasing = OwnerOf(msg.NetId);
            NetStats.AuthReleases++;
            if (NetDiag.Enabled)
                NetDiag.Log("Release", $"{NetDiag.Describe(msg.NetId)} released by {NetDiag.Owner(releasing)} — clearing explicit authority");
            var assign = new AuthAssignMsg
            {
                Entries = new List<(int netId, byte owner)> { (msg.NetId, byte.MaxValue) },
            };
            ApplyAuthAssign(assign);
            Writer.Reset();
            assign.Write(Writer);
            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
        }

        private static readonly List<int> _hostScratch = new List<int>(64);
        private static float _nextHostScanAt;
        private const float HostScanInterval = 0.5f; // authority scan cadence — fresh enough

        private static List<int> SpawnedUnitCandidates()
        {
            // FindObjectsByType is a whole-scene walk — at the 20 Hz state rate it was the
            // single hottest line on the host. Refresh the candidate list on the authority
            // cadence instead; the send loop re-checks Owners/KilledNetIds per use.
            if (Time.unscaledTime < _nextHostScanAt) return _hostScratch;
            _nextHostScanAt = Time.unscaledTime + HostScanInterval;

            _hostScratch.Clear();
            // Cache every streamed identity. Units send continuously; physics props send on
            // movement plus a low-rate heartbeat so late interest still receives their rest pose.
            foreach (var kv in LiveEntities)
            {
                var entity = kv.Value;
                if (entity == null) continue;
                if (entity.GetComponent<RemotePuppet>() != null) continue;      // player ships handled by ShipSync
                if (entity.GetComponent<Ship>() != null) continue;
                int netId = kv.Key;
                if (KilledNetIds.Contains(netId)) continue;
                _hostScratch.Add(netId);
            }
            return _hostScratch;
        }

        private static bool TryCollectEntry(EntityGameObjectManager egm, int netId, out EntityStateEntry entry,
            bool forceFull = false)
        {
            entry = default;
            if (!LiveEntities.TryGetValue(netId, out var se) || se == null) return false;
            if (se.GetComponent<RemoteEntityPuppet>() != null) return false; // not actually ours
            if (se.GetComponent<DuplicateEntityInert>() != null) return false;
            var rb = se.GetComponent<Rigidbody2D>();
            var dr = se.GetComponent<DamagableResource>();
            var unit = se.GetComponent<Unit>();
            Vector2 pos = rb != null ? rb.position : (Vector2)se.transform.position;
            float now = Time.unscaledTime;
            // Non-Unit props (pushable rocks etc.) stream on movement plus a low-rate keyframe.
            if (unit == null)
            {
                if (rb == null) return false;
                bool moved = !LastSentPos.TryGetValue(netId, out var last)
                    || Vector2.Distance(last, pos) >= 0.05f;
                bool heartbeatDue = !LastSentAt.TryGetValue(netId, out float sentAt)
                    || now - sentAt >= StationaryPropHeartbeat;
                if (!forceFull && !moved && !heartbeatDue) return false;
                if (!moved && !forceFull) InstrumentationCounters.StationaryPropHeartbeatSent();
            }
            else
            {
                float nearestSq = float.PositiveInfinity;
                foreach (var ship in ShipSync.ShipsBySlot.Values)
                    if (ship != null) nearestSq = Mathf.Min(nearestSq,
                        ((Vector2)ship.transform.position - pos).sqrMagnitude);
                byte fire = UnitStatus.ReadFireState(se);
                float hz = fire != 0 || nearestSq <= 35f * 35f
                    ? NetConfig.CombatStateHz.Value
                    : nearestSq <= NetConfig.InterestRadius.Value * NetConfig.InterestRadius.Value
                        ? NetConfig.StateHz.Value : NetConfig.DistantStateHz.Value;
                if (!forceFull && LastSentAt.TryGetValue(netId, out float sentAt) && now - sentAt < 1f / Mathf.Max(1f, hz))
                    return false;
            }
            LastSentPos[netId] = pos;
            LastSentAt[netId] = now;
            float hp = 1f;
            try { if (dr != null && dr.MaxHealth > 0) hp = dr.CurrentHealth / dr.MaxHealth; } catch { }
            entry = new EntityStateEntry
            {
                NetId = netId,
                Lifetime = NetIds.LifetimeOf(netId),
                FieldMask = EntityStateEntry.Fields.Full,
                Pos = pos,
                Vel = rb != null ? rb.linearVelocity : Vector2.zero,
                Rot = rb != null ? rb.rotation : se.transform.eulerAngles.z,
                Aim = UnitStatus.ReadAim(se),
                State = UnitStatus.ReadState(se),
                Fire = UnitStatus.ReadFireState(se),
                Ammo = UnitStatus.ReadAmmoFraction(se),
                HpFraction = hp,
                ShieldFraction = UnitStatus.ReadShieldFraction(se),
                BurnLevel = UnitStatus.ReadBurnLevel(se),
            };
            var full = entry;
            bool keyframe = !LastSentState.TryGetValue(netId, out var prior)
                || prior.Lifetime != entry.Lifetime
                || !LastKeyframeAt.TryGetValue(netId, out float keyframeAt)
                || now - keyframeAt >= StateKeyframeInterval;
            if (!forceFull && !keyframe)
            {
                var mask = EntityStateEntry.Fields.None;
                if ((entry.Aim - prior.Aim).sqrMagnitude >= 0.0025f) mask |= EntityStateEntry.Fields.Aim;
                if (entry.State != prior.State || entry.Fire != prior.Fire || entry.Ammo != prior.Ammo)
                    mask |= EntityStateEntry.Fields.Status;
                if (Mathf.Abs(entry.HpFraction - prior.HpFraction) >= 0.005f
                    || Mathf.Abs(entry.ShieldFraction - prior.ShieldFraction) >= 0.005f
                    || Mathf.Abs(entry.BurnLevel - prior.BurnLevel) >= 0.005f)
                    mask |= EntityStateEntry.Fields.Vitals;
                entry.FieldMask = mask;
                InstrumentationCounters.SnapshotDeltaFieldsOmitted(3 - BitCount(mask));
            }
            else
            {
                LastKeyframeAt[netId] = now;
                InstrumentationCounters.SnapshotKeyframeSent();
            }
            LastSentState[netId] = full;
            FullState[netId] = full;
            FullStateOrigins.Remove(netId); // live collection — strongest provenance
            return true;
        }

        private static int BitCount(EntityStateEntry.Fields fields)
        {
            int value = (byte)fields;
            return (value & 1) + ((value >> 1) & 1) + ((value >> 2) & 1);
        }

        // netId -> (authority, its clock) of the newest applied snapshot. The state channel is
        // unreliable AND unordered; late packets must not yank puppets backwards. A different
        // sender means an authority handoff — clocks aren't comparable, accept and re-baseline.
        private static readonly Dictionary<int, (byte slot, uint epoch, uint ms)> LastEntityStateMs
            = new Dictionary<int, (byte, uint, uint)>();

        public static void ApplyEntityStateBundle(EntityStateBundleMsg bundle)
        {
            int entries = CountEntries(bundle.Groups);
            InstrumentationCounters.StateBundleReceived(bundle.Groups?.Count ?? 0, entries);
            if (bundle.Groups == null) return;
            NoteReceivedChunk(bundle);
            // One packet has one timestamp and therefore one transit sample. Updating the clock
            // per group amplifies the same packet-arrival jitter several times over.
            float localTime;
            if (MappedBundleTimes.TryGetValue(bundle.Slot, out var mapped) && mapped.tick == bundle.Tick)
            {
                localTime = mapped.localTime;
                InstrumentationCounters.EntityClockSamplesCoalesced(1);
            }
            else
            {
                localTime = Core.ClockSync.ToLocalTime(bundle.Slot, bundle.TimeMs);
                MappedBundleTimes[bundle.Slot] = (bundle.Tick, localTime);
            }
            if (bundle.Groups.Count > 1)
                InstrumentationCounters.EntityClockSamplesCoalesced(bundle.Groups.Count - 1);
            foreach (var group in bundle.Groups)
            {
                ApplyEntityStateMapped(new EntityStateMsg
                {
                    Slot = bundle.Slot,
                    SegmentX = group.SegmentX,
                    SegmentY = group.SegmentY,
                    Epoch = group.Epoch,
                    TimeMs = bundle.TimeMs,
                    Entries = group.Entries,
                }, localTime);
            }
        }

        private static void NoteReceivedChunk(EntityStateBundleMsg bundle)
        {
            ulong bit = bundle.ChunkIndex < 64 ? 1UL << bundle.ChunkIndex : 0;
            if (ReceivedChunks.TryGetValue(bundle.Slot, out var prior))
            {
                if (prior.tick == bundle.Tick)
                {
                    prior.seen |= bit;
                    ReceivedChunks[bundle.Slot] = prior;
                }
                else if ((int)(bundle.Tick - prior.tick) > 0)
                {
                    int expected = Mathf.Min(64, prior.count);
                    int received = 0;
                    ulong value = prior.seen;
                    while (value != 0) { received += (int)(value & 1UL); value >>= 1; }
                    if (received < expected) InstrumentationCounters.SnapshotChunksMissing(expected - received);
                    ReceivedChunks[bundle.Slot] = (bundle.Tick, bundle.ChunkCount, bit);
                }
            }
            else ReceivedChunks[bundle.Slot] = (bundle.Tick, bundle.ChunkCount, bit);
            InstrumentationCounters.SnapshotChunkReceived(bundle.ChunkIndex, bundle.ChunkCount);
        }

        /// <summary>Host-side router for client-authoritative state. The host consumes the full
        /// bundle to keep canonical data positions current, then sends each other client only the
        /// groups within that client's streaming interest.</summary>
        public static void ForwardEntityStateBundle(NetSession session, EntityStateBundleMsg bundle,
            ulong senderPeer)
        {
            if (!session.IsHost || bundle.Groups == null) return;
            foreach (var player in session.Players)
            {
                if (player == null || player.IsLocal || !player.Connected || player.PeerId == senderPeer) continue;
                SelectInterestedGroups(player.Slot, bundle.Slot, bundle.Groups, TargetGroups,
                    out int droppedGroups, out int droppedEntries);
                SendBundleToPeer(session, player.PeerId, bundle.Slot, bundle.TimeMs, bundle.Tick, TargetGroups,
                    droppedGroups, droppedEntries);
            }
        }

        public static void ApplyEntityState(EntityStateMsg msg)
        {
            ApplyEntityStateMapped(msg, Core.ClockSync.ToLocalTime(msg.Slot, msg.TimeMs));
        }

        private static void ApplyEntityStateMapped(EntityStateMsg msg, float localTime)
        {
            if (!_loggedFirstState)
            {
                _loggedFirstState = true;
                Plugin.Log.LogInfo($"[Enemies] receiving entity states (first batch: {msg.Entries.Count})");
            }
            var egm = TryGetEgm();
            var session = NetSession.Instance;
            EntityManager em = null;
            try { em = ServiceLocator.Get<EntityManager>(); } catch { }
            var transmittedSegment = new AuthorityManager.SegmentKey(msg.SegmentX, msg.SegmentY);
            if (FirstSnapshotDeadlines.TryGetValue(transmittedSegment, out var awaited)
                && awaited.epoch == msg.Epoch && awaited.owner == msg.Slot)
            {
                FirstSnapshotDeadlines.Remove(transmittedSegment);
                InstrumentationCounters.FirstSnapshotObserved(Time.unscaledTime - awaited.committedAt);
            }
            foreach (var wireEntry in msg.Entries)
            {
                if (!NetIds.LifetimeMatches(wireEntry.NetId, wireEntry.Lifetime))
                {
                    InstrumentationCounters.StaleLifetimeDropped();
                    continue;
                }
                if (!TryMergeState(wireEntry, out var e)) continue;
                var positionSegment = AuthorityManager.SegmentOf(e.Pos);
                if (!positionSegment.Equals(transmittedSegment))
                {
                    InstrumentationCounters.PositionSegmentStateDropped();
                    if (NetDiag.Enabled) NetDiag.Throttled($"epoch{e.NetId}", 2f, "Epoch",
                        () => $"drop {NetDiag.Describe(e.NetId)}: wire position maps to {positionSegment}, group says {transmittedSegment}");
                    continue;
                }
                if (!AuthorityManager.IsStateAuthority(e.NetId, transmittedSegment, msg.Slot, msg.Epoch))
                {
                    InstrumentationCounters.AuthorityStateDropped();
                    if (NetDiag.Enabled) NetDiag.Throttled($"epoch{e.NetId}", 2f, "Epoch",
                        () => $"drop {NetDiag.Describe(e.NetId)} from P{msg.Slot + 1}/{msg.Epoch} segment={transmittedSegment}; committed P{AuthorityManager.OwnerOf(transmittedSegment) + 1}/{AuthorityManager.EpochOf(transmittedSegment)}");
                    continue;
                }
                if (KilledNetIds.Contains(e.NetId)) continue; // dead here — don't animate a corpse
                bool authorityChanged = false;
                if (LastEntityStateMs.TryGetValue(e.NetId, out var last))
                {
                    // Sender changed = authority handed off. The puppet re-baselines onto the new
                    // owner's timeline (accepted below) — a visible snap if their positions differ.
                    authorityChanged = last.slot != msg.Slot;
                    if (NetDiag.Enabled && authorityChanged)
                        NetDiag.Log("State", $"{NetDiag.Describe(e.NetId)} authority changed {NetDiag.Owner(last.slot)} -> {NetDiag.Owner(msg.Slot)} (snapshot buffer re-baselined)");
                    // Epoch changes with the same owner use the same clock and do not represent a
                    // simulator handoff. Preserve ordering and interpolation instead of P2 -> P2
                    // pseudo-transitions every time a neighboring segment lease commits.
                    if (last.slot == msg.Slot && (int)(msg.TimeMs - last.ms) <= 0) continue;
                }
                LastEntityStateMs[e.NetId] = (msg.Slot, msg.Epoch, msg.TimeMs);
                if (!NetIds.TryGetInstanceId(e.NetId, out int instanceId)) continue;

                // Keep the data-side position fresh so stream-in spawns at the right spot.
                try
                {
                    var data = em?.GetEntity(instanceId);
                    // MoveTo is not cosmetic: it updates SpatialGrid bucket membership. Directly
                    // assigning position left the entity indexed in its old segment, so every
                    // rebuild of that segment instantiated the same netId again on clients.
                    if (data != null) data.MoveTo(new Vector3(e.Pos.x, e.Pos.y, data.position.z));
                }
                catch { }

                // Ownership is derived only after the authoritative position has been installed.
                // This breaks the old circular failure where a stale local position selected the
                // wrong epoch, causing the position update itself to be rejected forever.
                if (session != null && msg.Slot == session.LocalSlot) continue; // relayed echo
                if (egm != null) try { ApplyOwnership(e.NetId, instanceId); } catch { }

                if (LiveEntities.TryGetValue(e.NetId, out var se) && se != null)
                {
                    var puppet = se.GetComponent<RemoteEntityPuppet>();
                    if (puppet == null && se.GetComponent<Unit>() != null)
                    {
                        puppet = se.gameObject.AddComponent<RemoteEntityPuppet>();
                        puppet.NetId = e.NetId;
                    }
                    if (puppet == null)
                    {
                        // Remote-owned physics prop: held kinematically and interpolated from
                        // the owner's snapshots (the hold persists at the last pose when the
                        // stream quiets or the segment goes dormant).
                        if (se.GetComponent<Rigidbody2D>() != null)
                        {
                            var prop = se.GetComponent<PropPuppet>();
                            if (prop == null) prop = se.gameObject.AddComponent<PropPuppet>();
                            prop.Hold = true;
                            prop.EnsureHeld();
                            if (authorityChanged) prop.ResetSnapshots();
                            prop.PushSnapshot(localTime, e.Pos, e.Vel, e.Rot);
                        }
                    }
                    if (authorityChanged && puppet != null)
                    {
                        puppet.ResetSnapshots();
                        InstrumentationCounters.AuthoritySnapshotRebaselined();
                    }
                    puppet?.PushSnapshot(localTime, e.Pos, e.Vel, e.Rot, e.Aim);
                    if (puppet != null)
                    {
                        puppet.SetFireState(e.Fire); // drives beam-weapon visuals (muted Shooter can't)
                        UnitStatus.WriteState(se, e.State);
                        UnitStatus.WriteFireState(se, e.Fire);
                        UnitStatus.WriteShieldFraction(se, e.ShieldFraction);
                        UnitStatus.WriteAmmoFraction(se, e.Ammo);
                    }
                    UnitStatus.WriteBurnLevel(se, e.BurnLevel);
                    try
                    {
                        var dr = se.GetComponent<DamagableResource>();
                        if (dr != null && dr.MaxHealth > 0)
                        {
                            float target = e.HpFraction * dr.MaxHealth;
                            if (Mathf.Abs(dr.CurrentHealth - target) > 0.5f)
                            {
                                // Someone else hurt this entity — show the hit flash observers
                                // would see in vanilla (the pipeline didn't run locally).
                                if (target < dr.CurrentHealth) UnitStatus.PlayDamageFlash(se);
                                dr.CurrentHealth = target;
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        private static bool TryMergeState(EntityStateEntry incoming, out EntityStateEntry merged)
        {
            merged = incoming;
            if (incoming.FieldMask != EntityStateEntry.Fields.Full)
            {
                if (!FullState.TryGetValue(incoming.NetId, out var prior)
                    || prior.Lifetime != incoming.Lifetime)
                {
                    InstrumentationCounters.SnapshotDeltaWithoutBaseline();
                    return false;
                }
                if ((incoming.FieldMask & EntityStateEntry.Fields.Aim) == 0) merged.Aim = prior.Aim;
                if ((incoming.FieldMask & EntityStateEntry.Fields.Status) == 0)
                {
                    merged.State = prior.State; merged.Fire = prior.Fire; merged.Ammo = prior.Ammo;
                }
                if ((incoming.FieldMask & EntityStateEntry.Fields.Vitals) == 0)
                {
                    merged.HpFraction = prior.HpFraction;
                    merged.ShieldFraction = prior.ShieldFraction;
                    merged.BurnLevel = prior.BurnLevel;
                }
            }
            merged.FieldMask = EntityStateEntry.Fields.Full;
            FullState[incoming.NetId] = merged;
            FullStateOrigins.Remove(incoming.NetId); // snapshot from the live authority
            return true;
        }

        // ---------------------------------------------------------------- kills

        [HarmonyPatch(typeof(DamagableResource), "Die")]
        internal static class BroadcastEntityDeath
        {
            private static void Postfix(DamagableResource __instance)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
                if (__instance.GetComponent<Ship>() != null) return; // ships have their own life events
                if (!TryGetNetId(__instance, out int netId)) return;
                // Announce any death WE simulated (no puppet = live local sim). Checking the
                // ownership registrar instead loses kills that land mid-handoff — the other
                // machine then keeps a live copy and HP-syncs our corpse back up (zombies).
                // A double announce after a race is harmless: receivers dedupe on KilledNetIds.
                if (__instance.GetComponentInParent<RemoteEntityPuppet>() != null) return;
                if (!IsCanonical(__instance)) return;
                if (OwnerOf(netId) != session.LocalSlot) return;
                if (!KilledNetIds.Add(netId)) return;
                byte killer = KillerOf(netId, session);
                NetStats.AddKill(killer);

                Writer.Reset();
                new EntityKilledMsg
                {
                    NetId = netId,
                    Lifetime = NetIds.LifetimeOf(netId),
                    MutationRevision = NextMutationRevision(netId),
                    KillerSlot = killer,
                    HasPosition = true,
                    Position = __instance.transform.position,
                }.Write(Writer);
                session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
            }
        }

        /// <summary>Killer slot for loot/kill credit: the last player to damage this entity
        /// (tracked by DamageSync on the machine that simulates it), or the local simulator when
        /// unknown. Read by the loot drop guard so resources go to who earned the kill.</summary>
        public static byte RemoteKillerSlot = 255;

        private static byte KillerOf(int netId, NetSession session)
        {
            byte k = DamageSync.LastKiller(netId);
            return k != 255 ? k : (byte)session.LocalSlot;
        }

        /// <summary>An entity is being removed by a non-Die() path (DestroyWhenResourceDrained
        /// Object.Destroy()s the unit directly and drops its spawnOnDeath). Announce it as a kill
        /// so it's removed on every machine — otherwise it stays standing on clients while it's
        /// gone on the machine that destroyed it (each ran the drain check on its own un-synced
        /// resource). Only the owner announces; puppets mute the component and remove via this.</summary>
        public static void SyncDestroy(Component c)
        {
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
            if (c == null || c.GetComponentInParent<RemoteEntityPuppet>() != null) return;
            if (!TryGetNetId(c, out int netId)) return; // orphan / no shared identity — can't sync
            if (!IsCanonical(c)) return;
            if (OwnerOf(netId) != session.LocalSlot) return;
            if (!KilledNetIds.Add(netId)) return;
            byte killer = KillerOf(netId, session);
            NetStats.AddKill(killer);
            Writer.Reset();
            new EntityKilledMsg
            {
                NetId = netId,
                Lifetime = NetIds.LifetimeOf(netId),
                MutationRevision = NextMutationRevision(netId),
                KillerSlot = killer,
                HasPosition = true,
                Position = c.transform.position,
            }.Write(Writer);
            session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
        }

        // DestroyWhenResourceDrained Object.Destroy()s the unit the frame its tank empties — never
        // through Die(), so nothing else here sees the death. Announce it just before it happens.
        [HarmonyPatch(typeof(DestroyWhenResourceDrained), "Update")]
        internal static class SyncResourceDrainDestroy
        {
            private static bool Prefix(DestroyWhenResourceDrained __instance)
            {
                if (!NetSession.Active) return true;
                if (!IsCanonical(__instance)) return false;
                try
                {
                    var unit = Traverse.Create(__instance).Field("unit").GetValue() as Unit;
                    var resource = Traverse.Create(__instance).Field("resource").GetValue() as Resource;
                    if (unit == null || resource == null) return true;
                    bool hasTank = Traverse.Create(unit).Method("HasTank", resource).GetValue<bool>();
                    if (!hasTank) return true;
                    float amount = Traverse.Create(unit).Method("GetResource", resource).GetValue<float>();
                    if (amount <= 0f) SyncDestroy(unit);
                }
                catch { }
                return true;
            }
        }

        public static bool ApplyEntityKilled(EntityKilledMsg msg)
        {
            if (!AcceptMutation(msg.NetId, msg.Lifetime, msg.MutationRevision)) return false;
            if (!KilledNetIds.Add(msg.NetId)) return false;
            NetStats.AddKill(msg.KillerSlot);
            RemoteKillerSlot = msg.KillerSlot; // so the DropLoot guard credits the killer
            if (!NetIds.TryGetInstanceId(msg.NetId, out int instanceId)) return true;
            if (msg.HasPosition) ApplyAuthoritativeDeathPosition(instanceId, msg.NetId, msg.Position);
            KillInstance(instanceId, msg.NetId);
            return true;
        }

        private static void ApplyAuthoritativeDeathPosition(int instanceId, int netId, Vector2 position)
        {
            try
            {
                var em = ServiceLocator.Get<EntityManager>();
                var data = em?.GetEntity(instanceId);
                if (data != null) data.MoveTo(new Vector3(position.x, position.y, data.position.z));
                var egm = TryGetEgm();
                if (egm == null || !egm.TryGetSavableEntity(instanceId, out var se) || se == null) return;
                float correction = Vector2.Distance(se.transform.position, position);
                if (correction > 0.25f)
                {
                    var rb = se.GetComponent<Rigidbody2D>();
                    if (rb != null) rb.position = position;
                    else se.transform.position = new Vector3(position.x, position.y, se.transform.position.z);
                    InstrumentationCounters.DeathPositionRepaired(correction);
                    if (NetDiag.Enabled)
                        NetDiag.Log("Position", $"death repair {NetDiag.Describe(netId)} by {correction:0.00}u to ({position.x:0.0},{position.y:0.0})");
                }
            }
            catch { }
        }

        public static void ApplyKillLedger(KillLedgerMsg msg)
        {
            foreach (var tombstone in msg.Entries)
            {
                int netId = tombstone.netId;
                if (KilledNetIds.Contains(netId)) continue;
                ApplyEntityKilled(new EntityKilledMsg
                {
                    NetId = netId, Lifetime = tombstone.lifetime,
                    MutationRevision = tombstone.revision, KillerSlot = 0,
                });
            }
        }

        public static bool ApplyPlantFruitKilled(PlantFruitKilledMsg msg)
        {
            ulong key = PlantFruitKey(msg.PlantNetId, msg.FruitId);
            if (KilledPlantFruits.Contains(key)) return false;
            if (!AcceptMutation(msg.PlantNetId, msg.Lifetime, msg.MutationRevision)) return false;
            KilledPlantFruits.Add(key);
            PlantFruitMutationRevisions[key] = msg.MutationRevision;
            _plantFruitApplied++;
            RemoteKillerSlot = msg.KillerSlot;
            if (!NetIds.TryGetInstanceId(msg.PlantNetId, out int instanceId))
            {
                _plantFruitMissing++;
                return true;
            }

            EntityData data = null;
            try { data = ServiceLocator.Get<EntityManager>()?.GetEntity(instanceId); } catch { }
            EntityPlant.Data plantData = null;
            data?.TryGetComponent(out plantData);
            bool found = false;
            _applyingRemote = true;
            try
            {
                var egm = TryGetEgm();
                if (egm != null && egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                {
                    foreach (var fruit in se.GetComponentsInChildren<EntityPlantFruit>(true))
                    {
                        if (fruit == null || fruit.Fruit == null || fruit.Fruit.id != msg.FruitId) continue;
                        found = true;
                        var health = fruit.GetComponent<HealthBase>() ??
                            Traverse.Create(fruit).Field("health").GetValue() as HealthBase;
                        health?.Die();
                        break;
                    }
                }
            }
            finally { _applyingRemote = false; }

            // Persist the child tombstone in EntityData so stream-out/in cannot resurrect it.
            if (plantData?.fruits != null)
                plantData.fruits.RemoveAll(fruit => fruit != null && fruit.id == msg.FruitId);
            if (!found) _plantFruitMissing++;
            return true;
        }

        internal static bool IsDurableEventAuthorized(int netId, byte senderSlot)
        {
            return OwnerOf(netId) == senderSlot;
        }

        private static bool _warnedDataDestroy;

        /// <summary>Destroy an entity's data so a later stream-in can't resurrect it.</summary>
        private static void DestroyData(int instanceId)
        {
            try
            {
                var em = ServiceLocator.Get<EntityManager>();
                var data = em?.GetEntity(instanceId);
                if (data == null) return;
                var destroy = AccessTools.Method(data.GetType(), "Destroy");
                if (destroy != null && destroy.GetParameters().Length == 0) destroy.Invoke(data, null);
            }
            catch { }
        }

        /// <summary>Apply a recorded kill to a local instance — the spawned GameObject when it
        /// exists, else the entity data (so a later stream-in doesn't resurrect it). Also runs
        /// from the spawn hook: if the data destroy is unavailable in this game version, the
        /// entity streams back in alive and gets re-killed right here instead of becoming a
        /// zombie only one machine can see.</summary>
        private static void KillInstance(int instanceId, int netId)
        {
            _applyingRemote = true;
            try
            {
                var egm = TryGetEgm();
                if (egm != null && egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                {
                    // Re-stream of an entity whose death chain already ran here: remove the zombie
                    // quietly, no second Die(). Die() re-fires loot/explosion/VFX, and because it
                    // does NOT clear the EntityData, a LevelSegment rebuild re-instantiated the
                    // corpse every frame -> SpawnObjectForEntity -> re-kill -> Die() again: a 60 Hz
                    // onDeath storm that tanked the CLIENT's FPS (it owns these) and kept running
                    // after the player died, since streaming — not the player — drives it.
                    // (Observed live: Box_Money #552/#306 hitting DropLoot 30x+ in seconds.) The
                    // first kill already dropped this player's instanced loot, so re-streams owe
                    // nothing but their own removal.
                    if (DeathEffectsDone.Contains(netId))
                    {
                        UnityEngine.Object.Destroy(se.gameObject);
                        DestroyData(instanceId);
                        return;
                    }
                    DeathEffectsDone.Add(netId);

                    // DestroyWhenResourceDrained entities are removed by the game via Object.Destroy
                    // plus their spawnOnDeath drop — never through Die(). Match that here: spawn OUR
                    // OWN copy of the drop (instanced per player, like all loot) and remove the
                    // entity, so the world stays in sync while each player gets the upgrade.
                    var drainer = se.GetComponent<DestroyWhenResourceDrained>();
                    if (drainer != null)
                    {
                        try
                        {
                            var prefab = AccessTools.Field(typeof(DestroyWhenResourceDrained), "spawnOnDeath")
                                ?.GetValue(drainer) as GameObject;
                            if (prefab != null)
                                UnityEngine.Object.Instantiate(prefab, se.transform.position, se.transform.rotation);
                        }
                        catch { }
                        UnityEngine.Object.Destroy(se.gameObject);
                        DestroyData(instanceId);
                        return;
                    }

                    // Die() runs the death chain (loot/VFX) and clears the live GameObject, but it
                    // leaves the streaming EntityData behind — which is exactly what let the segment
                    // re-instantiate the corpse. Destroy the data too so it can't stream back in.
                    var dr = se.GetComponent<DamagableResource>();
                    if (dr != null) { dr.Die(); DestroyData(instanceId); return; }
                    var health = se.GetComponent<Health>();
                    if (health != null)
                    {
                        AccessTools.Method(typeof(Health), "Die")?.Invoke(health, null);
                        DestroyData(instanceId);
                        return;
                    }
                }
                var em = ServiceLocator.Get<EntityManager>();
                var data = em.GetEntity(instanceId);
                if (data != null)
                {
                    var destroy = AccessTools.Method(data.GetType(), "Destroy");
                    if (destroy != null && destroy.GetParameters().Length == 0) destroy.Invoke(data, null);
                    else if (!_warnedDataDestroy)
                    {
                        _warnedDataDestroy = true; // spawn-hook re-kill covers it, but say so once
                        Plugin.Log.LogWarning($"[Enemies] no data-destroy on {data.GetType().Name} — killed entities despawn on stream-in instead");
                    }
                }
            }
            catch (Exception e)
            {
                // Reflection Invoke wraps the real error in TargetInvocationException — unwrap,
                // and log the full exception (type + stack), not just Message. A broken vanilla
                // death effect must not leave the killed object standing on this peer: force the
                // visual and data cleanup after reporting it.
                var cause = e.InnerException ?? e;
                Plugin.Log.LogWarning($"[Enemies] kill apply failed for netId {netId}: {cause}");
                try
                {
                    var egm = TryGetEgm();
                    if (egm != null && egm.TryGetSavableEntity(instanceId, out var failed) && failed != null)
                        UnityEngine.Object.Destroy(failed.gameObject);
                    DestroyData(instanceId);
                    Plugin.Log.LogWarning($"[Enemies] forced killed-entity cleanup for netId {netId}");
                }
                catch (Exception cleanup)
                {
                    Plugin.Log.LogWarning($"[Enemies] forced cleanup also failed for netId {netId}: {cleanup.Message}");
                }
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        [HarmonyPatch(typeof(Health), "Die")]
        internal static class BroadcastPropDeath
        {
            private static void Postfix(Health __instance)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
                var fruit = __instance.GetComponentInParent<EntityPlantFruit>();
                if (fruit != null)
                {
                    if (fruit.Fruit == null || !TryGetNetId(fruit, out int plantNetId)) return;
                    if (OwnerOf(plantNetId) != session.LocalSlot) return;
                    int fruitId = fruit.Fruit.id;
                    if (!KilledPlantFruits.Add(PlantFruitKey(plantNetId, fruitId))) return;
                    byte killer = KillerOf(plantNetId, session);
                    uint mutationRevision = NextMutationRevision(plantNetId);
                    PlantFruitMutationRevisions[PlantFruitKey(plantNetId, fruitId)] = mutationRevision;
                    _plantFruitAnnounced++;
                    Writer.Reset();
                    new PlantFruitKilledMsg
                    {
                        PlantNetId = plantNetId,
                        FruitId = fruitId,
                        KillerSlot = killer,
                        Lifetime = NetIds.LifetimeOf(plantNetId),
                        MutationRevision = mutationRevision,
                    }.Write(Writer);
                    session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
                    return;
                }
                if (!TryGetNetId(__instance, out int netId)) return;
                if (!IsCanonical(__instance)) return;
                if (OwnerOf(netId) != session.LocalSlot) return;
                if (!KilledNetIds.Add(netId)) return;
                Writer.Reset();
                new EntityKilledMsg
                {
                    NetId = netId,
                    Lifetime = NetIds.LifetimeOf(netId),
                    MutationRevision = NextMutationRevision(netId),
                    KillerSlot = KillerOf(netId, session),
                    HasPosition = true,
                    Position = __instance.transform.position,
                }.Write(Writer);
                session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
            }
        }

        public static bool SuppressLocalDeathEffects => _applyingRemote;
    }
}
