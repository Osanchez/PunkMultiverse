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
    /// Entity (enemy/minion) replication under the proximity-authority model. Exactly one machine
    /// simulates each entity (its "owner"; the current host by default), streams coalesced,
    /// interest-routed state bundles at StateHz, and announces kills. Everyone else runs the
    /// entity as a muted RemoteEntityPuppet. Ownership changes arrive as segment lease commits
    /// from the host's AuthorityManager and are applied idempotently here.
    /// </summary>
    internal static class EnemySync
    {
        /// <summary>netId -> owner slot. Missing key = host (slot 0).</summary>
        public static readonly Dictionary<int, byte> Owners = new Dictionary<int, byte>();
        /// <summary>Runtime ids exempt from proximity handoff (player minions).</summary>
        public static readonly HashSet<int> FixedOwners = new HashSet<int>();
        private static readonly HashSet<int> KilledNetIds = new HashSet<int>();
        // netIds whose local death chain (Die() -> loot/explosion/VFX) has already run here. A
        // killed entity can stream in again (LevelSegment rebuild re-instantiates it, or the
        // data-destroy below is unavailable in this game version); the second time we must remove
        // the zombie WITHOUT re-firing its death chain — see KillInstance.
        private static readonly HashSet<int> DeathEffectsDone = new HashSet<int>();
        private static readonly Dictionary<int, Vector2> LastSentPos = new Dictionary<int, Vector2>();
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
        private const float DuplicateRetireGrace = 0.75f;
        private const float DuplicateRetireScanInterval = 0.25f;
        private static float _nextRetirementScanAt;
        private static int _firstLifetimes, _reenteredLifetimes, _overlappingLifetimes, _retiredLifetimes;

        public static void Reset()
        {
            Owners.Clear();
            FixedOwners.Clear();
            KilledNetIds.Clear();
            DeathEffectsDone.Clear();
            DroppedLootNetIds.Clear();
            RemoteKillerSlot = 255;
            LastSentPos.Clear();
            LastEntityStateMs.Clear();
            NextReleaseAt.Clear();
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

        public static int KilledCount => KilledNetIds.Count;

        public static bool IsKilled(int netId) => KilledNetIds.Contains(netId);

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
            if (se.GetComponent<Unit>() == null) return; // static prop — no authority needed
            if (se.GetComponent<DuplicateEntityInert>() != null) return;

            var puppet = se.GetComponent<RemoteEntityPuppet>();
            bool mine = OwnerOf(netId) == session.LocalSlot;
            if (mine && puppet != null) UnityEngine.Object.Destroy(puppet);
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
                if (kv.Value == null || kv.Value.GetComponent<Unit>() == null) continue;
                if (!AuthorityManager.TrySegmentOf(kv.Key, out var key) || !key.Equals(segment)) continue;
                if (NetIds.TryGetInstanceId(kv.Key, out int instanceId)) ApplyOwnership(kv.Key, instanceId);
            }
        }

        internal static void ApplyAllOwnership()
        {
            foreach (var kv in LiveEntities)
            {
                if (kv.Value == null || kv.Value.GetComponent<Unit>() == null) continue;
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
            if (LiveEntities.TryGetValue(netId, out var current) && current == registration.Entity)
                LiveEntities.Remove(netId);
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
                Owners[netId] = owner;
                if (prev == owner) continue; // retain explicit mapping, but do not re-arm components
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

        // ---------------------------------------------------------------- state streaming

        /// <summary>Called from NetSession.Update while InGame: stream entities I own.</summary>
        public static void Tick(NetSession session)
        {
            TickDuplicateRetirements();
            if (!NetIds.ManifestComplete || Time.unscaledTime < _nextSendAt) return;
            _nextSendAt = Time.unscaledTime + 1f / Mathf.Max(1f, NetConfig.StateHz.Value);

            var egm = TryGetEgm();
            if (egm == null) return;

            var groups = new Dictionary<(AuthorityManager.SegmentKey key, uint epoch), List<EntityStateEntry>>();
            foreach (var netId in SpawnedUnitCandidates())
            {
                if (KilledNetIds.Contains(netId) || OwnerOf(netId) != session.LocalSlot) continue;
                if (!TryCollectEntry(egm, netId, out var entry)) continue;
                var key = AuthorityManager.SegmentOf(entry.Pos);
                uint epoch = FixedOwners.Contains(netId) ? 0 : AuthorityManager.EpochOf(key);
                byte owner = FixedOwners.Contains(netId) ? OwnerOf(netId) : AuthorityManager.OwnerOf(key);
                if (owner != session.LocalSlot) continue; // crossed a lease boundary this frame
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
            if (!session.IsHost)
            {
                // A client has one route: send one complete tick to the host. The host needs the
                // full state for canonical positions/authority and interest-routes it onward.
                SendBundleToHost(session, slot, timeMs, stateGroups);
                return;
            }

            foreach (var player in session.Players)
            {
                if (player == null || player.IsLocal || !player.Connected) continue;
                SelectInterestedGroups(player.Slot, stateGroups, TargetGroups, out int droppedGroups, out int droppedEntries);
                SendBundleToPeer(session, player.PeerId, slot, timeMs, TargetGroups, droppedGroups, droppedEntries);
            }
        }

        private static void SendBundleToHost(NetSession session, byte slot, uint timeMs,
            List<EntityStateGroup> groups)
        {
            Writer.Reset();
            new EntityStateBundleMsg { Slot = slot, TimeMs = timeMs, Groups = groups }.Write(Writer);
            session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
            InstrumentationCounters.StateBundleSent(groups.Count, CountEntries(groups), Writer.Position, 0, 0);
        }

        private static void SendBundleToPeer(NetSession session, ulong peer, byte slot, uint timeMs,
            List<EntityStateGroup> groups, int droppedGroups, int droppedEntries)
        {
            if (groups.Count == 0)
            {
                InstrumentationCounters.StateInterestFiltered(droppedGroups, droppedEntries);
                return;
            }
            Writer.Reset();
            new EntityStateBundleMsg { Slot = slot, TimeMs = timeMs, Groups = groups }.Write(Writer);
            session.SendToPeer(peer, NetChannel.State, Writer.ToSegment(), reliable: false);
            InstrumentationCounters.StateBundleSent(groups.Count, CountEntries(groups), Writer.Position,
                droppedGroups, droppedEntries);
        }

        private static int CountEntries(List<EntityStateGroup> groups)
        {
            if (groups == null) return 0;
            int count = 0;
            foreach (var group in groups) count += group.Entries?.Count ?? 0;
            return count;
        }

        private static void SelectInterestedGroups(byte targetSlot, List<EntityStateGroup> source,
            List<EntityStateGroup> target, out int droppedGroups, out int droppedEntries)
        {
            target.Clear();
            droppedGroups = 0;
            droppedEntries = 0;
            foreach (var group in source)
            {
                if (IsGroupInterestingTo(targetSlot, group)) target.Add(group);
                else
                {
                    droppedGroups++;
                    droppedEntries += group.Entries?.Count ?? 0;
                }
            }
        }

        private static bool IsGroupInterestingTo(byte targetSlot, EntityStateGroup group)
        {
            if (!ShipSync.ShipsBySlot.TryGetValue(targetSlot, out var ship) || ship == null)
                return true; // missing route/ship during a transition: correctness over filtering
            var entries = group.Entries;
            if (entries == null || entries.Count == 0) return false;
            Vector2 player = ship.transform.position;
            // A segment-width guard band ensures an entity is already streaming before it can
            // enter the camera, while still excluding rooms owned/loaded around distant players.
            float radius = Mathf.Max(25f, NetConfig.InterestRadius.Value)
                           + Mathf.Max(10f, Level.SegmentSize);
            float radiusSq = radius * radius;
            foreach (var entry in entries)
                if ((entry.Pos - player).sqrMagnitude <= radiusSq) return true;
            return false;
        }

        private static bool IsSpawnedHere(EntityGameObjectManager egm, int netId)
        {
            return NetIds.TryGetInstanceId(netId, out int instanceId)
                   && egm.TryGetSavableEntity(instanceId, out var se) && se != null;
        }

        private static readonly Dictionary<int, float> NextReleaseAt = new Dictionary<int, float>();

        private static void MaybeReleaseAuthority(NetSession session, int netId)
        {
            // Host-owned unspawned entities are simply dormant (by design); clients ask the
            // host to reassign. Rate-limited per entity — reassignment arrives as AUTH_ASSIGN.
            if (session.IsHost) return;
            if (NextReleaseAt.TryGetValue(netId, out float at) && Time.unscaledTime < at) return;
            NextReleaseAt[netId] = Time.unscaledTime + 5f;
            NetStats.AuthReleases++;
            if (NetDiag.Enabled)
                NetDiag.Log("Release", $"{NetDiag.Describe(netId)} — I own it but it isn't spawned here; asking host to take it back");
            Writer.Reset();
            new AuthReleaseMsg { NetId = netId }.Write(Writer);
            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
        }

        /// <summary>Host: an owner can't simulate this entity — take it back (dormant). The
        /// releasing slot is denied this entity for a while (AuthorityManager.OnReleased), or
        /// the next scan would hand it straight back and loop forever.</summary>
        public static void ApplyAuthRelease(AuthReleaseMsg msg, NetSession session)
        {
            byte releasing = OwnerOf(msg.NetId);
            NetStats.AuthReleases++;
            if (NetDiag.Enabled)
                NetDiag.Log("Release", $"{NetDiag.Describe(msg.NetId)} released by {NetDiag.Owner(releasing)} — taking it back to host, {NetDiag.Owner(releasing)} denied for a cooldown");
            var assign = new AuthAssignMsg
            {
                Entries = new List<(int netId, byte owner)> { (msg.NetId, session.HostSlot) },
            };
            ApplyAuthAssign(assign);
            AuthorityManager.OnReleased(msg.NetId, releasing);
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
            // Entities near the host stream in on the host; those without an explicit owner are ours.
            foreach (var kv in LiveEntities)
            {
                var unit = kv.Value != null ? kv.Value.GetComponent<Unit>() : null;
                if (unit == null) continue;
                if (unit.GetComponent<RemotePuppet>() != null) continue;         // player ships handled by ShipSync
                if (unit.GetComponent<Ship>() != null) continue;
                int netId = kv.Key;
                if (KilledNetIds.Contains(netId)) continue;
                _hostScratch.Add(netId);
            }
            return _hostScratch;
        }

        private static bool TryCollectEntry(EntityGameObjectManager egm, int netId, out EntityStateEntry entry)
        {
            entry = default;
            if (!LiveEntities.TryGetValue(netId, out var se) || se == null) return false;
            if (se.GetComponent<RemoteEntityPuppet>() != null) return false; // not actually ours
            if (se.GetComponent<DuplicateEntityInert>() != null) return false;
            var rb = se.GetComponent<Rigidbody2D>();
            var dr = se.GetComponent<DamagableResource>();
            Vector2 pos = rb != null ? rb.position : (Vector2)se.transform.position;
            // Non-Unit props (pushable rocks etc.) only stream while actually moving.
            if (se.GetComponent<Unit>() == null)
            {
                if (rb == null) return false;
                if (LastSentPos.TryGetValue(netId, out var last) && Vector2.Distance(last, pos) < 0.05f) return false;
            }
            LastSentPos[netId] = pos;
            float hp = 1f;
            try { if (dr != null && dr.MaxHealth > 0) hp = dr.CurrentHealth / dr.MaxHealth; } catch { }
            entry = new EntityStateEntry
            {
                NetId = netId,
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
            return true;
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
            foreach (var group in bundle.Groups)
            {
                ApplyEntityState(new EntityStateMsg
                {
                    Slot = bundle.Slot,
                    SegmentX = group.SegmentX,
                    SegmentY = group.SegmentY,
                    Epoch = group.Epoch,
                    TimeMs = bundle.TimeMs,
                    Entries = group.Entries,
                });
            }
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
                SelectInterestedGroups(player.Slot, bundle.Groups, TargetGroups,
                    out int droppedGroups, out int droppedEntries);
                SendBundleToPeer(session, player.PeerId, bundle.Slot, bundle.TimeMs, TargetGroups,
                    droppedGroups, droppedEntries);
            }
        }

        public static void ApplyEntityState(EntityStateMsg msg)
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
            float localTime = Core.ClockSync.ToLocalTime(msg.Slot, msg.TimeMs);
            var transmittedSegment = new AuthorityManager.SegmentKey(msg.SegmentX, msg.SegmentY);
            foreach (var e in msg.Entries)
            {
                var positionSegment = AuthorityManager.SegmentOf(e.Pos);
                if (!positionSegment.Equals(transmittedSegment)
                    || !AuthorityManager.IsStateAuthority(e.NetId, transmittedSegment, msg.Slot, msg.Epoch))
                {
                    InstrumentationCounters.StaleEntityStateDropped();
                    if (NetDiag.Enabled) NetDiag.Throttled($"epoch{e.NetId}", 2f, "Epoch",
                        () => $"drop {NetDiag.Describe(e.NetId)} from P{msg.Slot + 1}/{msg.Epoch} segment={transmittedSegment}; committed P{AuthorityManager.OwnerOf(transmittedSegment) + 1}/{AuthorityManager.EpochOf(transmittedSegment)}");
                    continue;
                }
                if (KilledNetIds.Contains(e.NetId)) continue; // dead here — don't animate a corpse
                if (LastEntityStateMs.TryGetValue(e.NetId, out var last))
                {
                    // Sender changed = authority handed off. The puppet re-baselines onto the new
                    // owner's timeline (accepted below) — a visible snap if their positions differ.
                    if (NetDiag.Enabled && last.slot != msg.Slot)
                        NetDiag.Log("State", $"{NetDiag.Describe(e.NetId)} authority changed {NetDiag.Owner(last.slot)} -> {NetDiag.Owner(msg.Slot)} (puppet re-baselines — expect a visual snap)");
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
                        // Pushed/hooked physics prop: interpolate while its authority streams
                        // it (PropPuppet self-expires and returns the prop to local physics).
                        if (se.GetComponent<Rigidbody2D>() != null)
                        {
                            var prop = se.GetComponent<PropPuppet>();
                            if (prop == null) prop = se.gameObject.AddComponent<PropPuppet>();
                            prop.PushSnapshot(localTime, e.Pos, e.Vel, e.Rot);
                        }
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
                if (!KilledNetIds.Add(netId)) return;
                byte killer = KillerOf(netId, session);
                NetStats.AddKill(killer);

                Writer.Reset();
                new EntityKilledMsg { NetId = netId, KillerSlot = killer }.Write(Writer);
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
            if (!KilledNetIds.Add(netId)) return;
            byte killer = KillerOf(netId, session);
            NetStats.AddKill(killer);
            Writer.Reset();
            new EntityKilledMsg { NetId = netId, KillerSlot = killer }.Write(Writer);
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

        public static void ApplyEntityKilled(EntityKilledMsg msg)
        {
            if (!KilledNetIds.Add(msg.NetId)) return;
            NetStats.AddKill(msg.KillerSlot);
            RemoteKillerSlot = msg.KillerSlot; // so the DropLoot guard credits the killer
            if (!NetIds.TryGetInstanceId(msg.NetId, out int instanceId)) return;
            KillInstance(instanceId, msg.NetId);
        }

        public static void ApplyKillLedger(KillLedgerMsg msg)
        {
            foreach (int netId in msg.NetIds)
                ApplyEntityKilled(new EntityKilledMsg { NetId = netId, KillerSlot = 0 });
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
                if (!TryGetNetId(__instance, out int netId)) return;
                if (!IsCanonical(__instance)) return;
                if (!KilledNetIds.Add(netId)) return;
                Writer.Reset();
                new EntityKilledMsg { NetId = netId, KillerSlot = KillerOf(netId, session) }.Write(Writer);
                session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
            }
        }

        public static bool SuppressLocalDeathEffects => _applyingRemote;
    }
}
