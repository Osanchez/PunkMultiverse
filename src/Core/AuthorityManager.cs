using System;
using System.Collections.Generic;
using System.Linq;
using PunkMultiverse.Protocol;
using PunkMultiverse.Sync;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Host-arbitrated, segment-scoped simulation leases, constrained by residency: a lease may
    /// only be granted to (and only stays with) a peer whose game is actually streaming the
    /// segment's GameObjects, as continuously reported by every peer. Segments nobody streams
    /// are DORMANT — owned by nobody, canonical state held in the coordinator's store — never
    /// silently defaulted to the host. A transfer is transactional: the old owner remains
    /// authoritative during PREPARE; the selected peer installs the lease and ACKs; only then
    /// does the host broadcast COMMIT. Every commit increments the segment epoch, so delayed
    /// state from a prior simulator is harmless.
    /// </summary>
    internal static class AuthorityManager
    {
        /// <summary>The "nobody" owner: a Dormant segment/entity. 255 never equals a real slot,
        /// so every "is it mine" comparison naturally answers no.</summary>
        internal const byte DormantOwner = byte.MaxValue;

        internal readonly struct SegmentKey : IEquatable<SegmentKey>
        {
            internal readonly int X, Y;
            internal SegmentKey(int x, int y) { X = x; Y = y; }
            public bool Equals(SegmentKey o) => X == o.X && Y == o.Y;
            public override bool Equals(object o) => o is SegmentKey k && Equals(k);
            public override int GetHashCode() => unchecked((X * 397) ^ Y);
            public override string ToString() => $"({X},{Y})";
        }

        private sealed class Lease
        {
            internal byte Owner;
            internal uint Epoch;
            internal bool Pending;
            internal byte PendingOwner;
            internal uint PendingEpoch;
            internal float PreparedAt;
        }

        private const float ScanInterval = 0.5f;
        private const float PrepareRetry = 2f;
        // Transitions arrive in bursts (go-live activation storm; several segments/second during
        // fast flight), and each PREPARE builds a baseline roster inline when the host is the
        // source — a fixed count of 16 produced 200 ms Authority frames. Budget by TIME instead:
        // spend at most this many ms preparing per scan (minimum 2 so progress is guaranteed);
        // the scan re-runs every 0.5 s or immediately on residency events, so a burst amortizes
        // over a few frames instead of stalling one.
        private const double PrepareBudgetMs = 3.0;
        private const int MinPreparesPerScan = 2;
        private const int MinDormantClaimsPerScan = 16;
        // How long the host waits for an owner's SegmentDormancyCommit after learning (via a
        // residency report) that the owner unloaded a segment, before falling back to its own
        // state cache. Commits and reports ride the same reliable channel, so in practice the
        // commit wins; the grace only covers crashes and lost owners.
        private const float DormancyCommitGrace = 2f;
        private const float ResidencyReportDebounce = 0.25f;
        private static readonly Dictionary<SegmentKey, Lease> Leases = new Dictionary<SegmentKey, Lease>();
        private static readonly Dictionary<int, SegmentKey> EntitySegments = new Dictionary<int, SegmentKey>();
        private static readonly HashSet<SegmentKey> Interested = new HashSet<SegmentKey>();
        private static readonly Dictionary<SegmentKey, (byte owner, float until)> ForcedOwners = new Dictionary<SegmentKey, (byte, float)>();
        private static readonly Dictionary<(SegmentKey segment, byte owner), float> UnreadyUntil
            = new Dictionary<(SegmentKey, byte), float>();
        // Residency truth per slot: which segments each peer's game is streaming right now.
        // The local slot's set refreshes from the EGM directly; remote sets arrive as reports.
        private static readonly Dictionary<byte, HashSet<SegmentKey>> ResidentSets
            = new Dictionary<byte, HashSet<SegmentKey>>();
        private static readonly Dictionary<byte, uint> ResidencyRevs = new Dictionary<byte, uint>();
        // WS10.1 — capability-weighted placement. Load factor per peer (0..255, from frame-time EMA);
        // used ONLY to re-rank eligible resident candidates and to soft-cap grants to a struggling
        // peer. Sticky-grab and "leases only within residency" are untouched.
        private static readonly Dictionary<byte, byte> PeerLoads = new Dictionary<byte, byte>();
        private static readonly Dictionary<byte, int> ScanOwnedCounts = new Dictionary<byte, int>();
        private static readonly HashSet<Vector2Int> EmptySegments = new HashSet<Vector2Int>();
        private static float _frameMsEma = 16.7f;
        private const byte OverloadThreshold = 160;   // ≈ 25 fps sustained
        private const int OverloadedSegmentCap = 6;   // grants stop here while overloaded
        // A segment that just left an owner's residency set stays "grace-resident" until this time,
        // so one-frame streaming flicker at a segment boundary doesn't ping-pong the lease (the RC2
        // authChurn storm). Keyed (slot, segment); consulted only to RETAIN the current owner when
        // NO peer is strictly resident — never to grant a new lease.
        private static readonly Dictionary<(byte slot, SegmentKey key), float> ResidentGraceUntil
            = new Dictionary<(byte, SegmentKey), float>();
        private static readonly List<(byte slot, SegmentKey key)> _graceScratch
            = new List<(byte, SegmentKey)>();
        // Segments whose owner stopped being resident and whose dormancy commit hasn't arrived.
        private static readonly Dictionary<SegmentKey, float> PendingDormancy = new Dictionary<SegmentKey, float>();
        // Dormancy commits that arrived while another peer still looked resident: the transition
        // defers to a possible handoff, but the received commit must stay first-class state — if
        // the prospective target leaves before the scan hands off, the segment goes Dormant
        // directly from the commit instead of waiting out the crash grace (a co-flying trailing
        // player made that fallback fire on 29 host-owned segments in one playtest).
        private static readonly Dictionary<SegmentKey, byte> DeferredDormancyCommits
            = new Dictionary<SegmentKey, byte>();

        /// <summary>Why a segment moves to Dormant. The paths must not share a log message —
        /// deliberate disconnect cleanup reading as "commit lost" buries the real anomalies.</summary>
        private enum DormantReason { OwnerCommit, GraceFallback, PeerLost }
        private static readonly NetWriter Writer = new NetWriter(512);
        private static float _nextScanAt;
        private static uint _nextEpoch = 1;
        private static float _nextLedgerAt;
        private static int _ledgerCursor;
        private static uint _localResidencyRev;
        private static ulong _lastResidencyHash = ulong.MaxValue;
        private static float _nextResidencyReportAt;

        internal static int CommittedLeaseCount => Leases.Count;
        internal static int PendingLeaseCount { get { int n = 0; foreach (var l in Leases.Values) if (l.Pending) n++; return n; } }
        internal static int DormantLeaseCount { get { int n = 0; foreach (var l in Leases.Values) if (l.Owner == DormantOwner) n++; return n; } }

        public static void Reset()
        {
            Leases.Clear();
            EntitySegments.Clear();
            Interested.Clear();
            ForcedOwners.Clear();
            UnreadyUntil.Clear();
            ResidentSets.Clear();
            ResidencyRevs.Clear();
            ResidentGraceUntil.Clear();
            PendingDormancy.Clear();
            DeferredDormancyCommits.Clear();
            AggroTargets.Clear();
            PeerLoads.Clear();
            ScanOwnedCounts.Clear();
            _frameMsEma = 16.7f;
            _nextScanAt = 0;
            _nextEpoch = 1;
            _nextLedgerAt = 0;
            _ledgerCursor = 0;
            _localResidencyRev = 0;
            _lastResidencyHash = ulong.MaxValue;
            _nextResidencyReportAt = 0;
        }

        internal static SegmentKey SegmentOf(Vector2 p)
        {
            float size = Level.SegmentSize > 0 ? Level.SegmentSize : 25f;
            return new SegmentKey(Mathf.FloorToInt(p.x / size), Mathf.FloorToInt(p.y / size));
        }

        internal static bool TrySegmentOf(int netId, out SegmentKey key)
        {
            if (NetIds.TryGetInstanceId(netId, out int instanceId))
            {
                try
                {
                    var data = ServiceLocator.Get<EntityManager>()?.GetEntity(instanceId);
                    if (data != null)
                    {
                        key = SegmentOf(data.position);
                        EntitySegments[netId] = key;
                        return true;
                    }
                }
                catch { }
            }
            return EntitySegments.TryGetValue(netId, out key);
        }

        internal static byte OwnerOf(int netId)
        {
            if (EnemySync.FixedOwners.Contains(netId) && EnemySync.Owners.TryGetValue(netId, out byte fixedOwner))
                return fixedOwner;
            if (TrySegmentOf(netId, out var key) && Leases.TryGetValue(key, out var lease)) return lease.Owner;
            // Nothing is owned by default. The old host-default fabricated authority the host
            // could not exercise (it wasn't streaming those segments) — the split-brain root.
            return DormantOwner;
        }

        internal static uint EpochOf(int netId)
        {
            if (EnemySync.FixedOwners.Contains(netId)) return 0;
            return TrySegmentOf(netId, out var key) && Leases.TryGetValue(key, out var lease) ? lease.Epoch : 0;
        }

        internal static byte OwnerOf(SegmentKey key)
        {
            return Leases.TryGetValue(key, out var lease) ? lease.Owner : DormantOwner;
        }

        /// <summary>Segments currently leased to <paramref name="slot"/>. Used at host-migration time to
        /// capture the departing host's segments BEFORE OnPeerLost flips them Dormant, so the new host
        /// can seed their last-known entity states to every surviving peer (WS4.2).</summary>
        internal static List<SegmentKey> SegmentsOwnedBy(byte slot)
        {
            var result = new List<SegmentKey>();
            foreach (var pair in Leases)
                if (pair.Value.Owner == slot) result.Add(pair.Key);
            return result;
        }

        internal static uint EpochOf(SegmentKey key) => Leases.TryGetValue(key, out var lease) ? lease.Epoch : 0;

        internal static bool CanApplyRuntimeBaseline(SegmentKey key, uint targetEpoch,
            RuntimeBaselinePurpose purpose)
        {
            if (!Leases.TryGetValue(key, out var lease)) return purpose == RuntimeBaselinePurpose.Handoff;
            if (targetEpoch < lease.Epoch) return false;
            if (purpose == RuntimeBaselinePurpose.Interest) return targetEpoch >= lease.Epoch;
            return !lease.Pending || lease.PendingEpoch == targetEpoch;
        }

        internal static bool IsStateAuthority(int netId, SegmentKey key, byte owner, uint epoch)
        {
            if (EnemySync.FixedOwners.Contains(netId))
                return epoch == 0 && EnemySync.Owners.TryGetValue(netId, out byte fixedOwner) && fixedOwner == owner;
            if (Leases.TryGetValue(key, out var lease)) return lease.Owner == owner && lease.Epoch == epoch;
            return false; // unleased = Dormant: nobody is a state authority
        }

        public static void NoteCombat(int netId) { }

        // WS3.2 — handoff aggro memory. Every enemy shot already advertises its target slot
        // (EntityFireMsg.TargetSlot); recording it on EVERY receiver means a peer that later takes
        // over the entity's simulation (lease handoff, migration) inherits its aggro instead of the
        // AI cold-starting and losing the player it was fighting. Only real targets are remembered
        // (255 = "no target this shot" keeps the last one), so a brief LOS break doesn't erase it.
        private static readonly Dictionary<int, byte> AggroTargets = new Dictionary<int, byte>();
        public static void NoteAggro(int netId, byte targetSlot)
        {
            if (targetSlot != 255) AggroTargets[netId] = targetSlot;
        }
        internal static bool TryGetAggro(int netId, out byte targetSlot) => AggroTargets.TryGetValue(netId, out targetSlot);
        internal static void ForgetAggro(int netId) => AggroTargets.Remove(netId);

        public static void OnReleased(int netId, byte releasingSlot) { }

        // ---------------------------------------------------------------- residency

        /// <summary>Runs on EVERY peer at the entity tick rate: watch the game's activeSegments
        /// and publish the full set to the coordinator whenever it changes (debounced). Full-set
        /// semantics are idempotent — a lost report is healed by the next one.</summary>
        public static void TickResidency(NetSession session)
        {
            if (session == null) return;
            // WS10.1: frame-time load factor. EMA of the real frame delta, scaled so 60 fps ≈ 67
            // and 25 fps ≈ 160 (the overload threshold). Updated every tick regardless of the
            // report debounce, so the next report always carries a current value.
            _frameMsEma = Mathf.Lerp(_frameMsEma, Time.unscaledDeltaTime * 1000f, 0.05f);
            byte load = (byte)Mathf.Clamp(_frameMsEma * 4f, 0f, 255f);
            if (Time.unscaledTime < _nextResidencyReportAt) return;
            var active = EnemySync.TryGetActiveSegments();
            if (active == null) return;
            // A dedicated coordinator is resident NOWHERE by definition — even if the engine
            // streams segments around the camera/origin, reporting them would let the scan grant
            // this shipless process leases it can never meaningfully simulate. Empty residency +
            // "leases only within residency" = the coordinator structurally owns nothing.
            if (NetConfig.IsCoordinator && active.Count > 0) active = EmptySegments;
            ulong hash = 0;
            foreach (var s in active) hash ^= ElementHash(s.x, s.y);
            hash ^= (ulong)active.Count << 56; // distinguish ∅ from an unluckily-cancelling set
            hash ^= (ulong)(load / 32) * 0x9E3779B97F4A7C15UL; // a load-bucket change re-reports too
            if (hash == _lastResidencyHash) return;
            _lastResidencyHash = hash;
            _nextResidencyReportAt = Time.unscaledTime + ResidencyReportDebounce;
            _localResidencyRev++;
            var segments = new List<Vector2Int>(active.Count);
            foreach (var s in active) segments.Add(s);
            InstrumentationCounters.ResidencyReportSent(segments.Count);
            if (session.IsHost)
            {
                PeerLoads[(byte)session.LocalSlot] = load;
                ApplyResidencySet((byte)session.LocalSlot, _localResidencyRev, segments, session);
                return;
            }
            Writer.Reset();
            new ResidencyReportMsg { Slot = (byte)session.LocalSlot, Rev = _localResidencyRev, Load = load, Segments = segments }
                .Write(Writer);
            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
        }

        private static ulong ElementHash(int x, int y)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                h = (h ^ (uint)x) * 1099511628211UL;
                h = (h ^ (uint)y) * 1099511628211UL;
                return h;
            }
        }

        internal static void ApplyResidencyReport(ResidencyReportMsg msg, NetSession session)
        {
            if (!session.IsHost) return;
            PeerLoads[msg.Slot] = msg.Load; // WS10.1: lease tiebreak + soft cap input
            ApplyResidencySet(msg.Slot, msg.Rev, msg.Segments, session);
        }

        private static void ApplyResidencySet(byte slot, uint rev, List<Vector2Int> segments,
            NetSession session)
        {
            if (ResidencyRevs.TryGetValue(slot, out uint last) && (int)(rev - last) <= 0) return;
            ResidencyRevs[slot] = rev;
            if (!ResidentSets.TryGetValue(slot, out var set))
                ResidentSets[slot] = set = new HashSet<SegmentKey>();
            var previous = new HashSet<SegmentKey>(set);
            set.Clear();
            if (segments != null)
                foreach (var s in segments) set.Add(new SegmentKey(s.x, s.y));
            InstrumentationCounters.ResidencyReportApplied(set.Count);
            if (!session.IsHost) return;
            foreach (var key in set)
            {
                // Event-driven readiness retry: a target that just became resident no longer
                // needs to sit out the blind post-NACK backoff.
                UnreadyUntil.Remove((key, slot));
                ResidentGraceUntil.Remove((slot, key)); // strictly resident now — grace unneeded
                // The committed-and-left owner is streaming the segment again: it resumes
                // simulating, so its old final-state commit no longer describes the future.
                if (DeferredDormancyCommits.TryGetValue(key, out byte committedBy) && committedBy == slot)
                    DeferredDormancyCommits.Remove(key);
                // Newly resident: issue any interest baseline that was deferred while this peer
                // hadn't streamed the segment, instead of waiting for the next distance poll (RC1).
                if (!previous.Contains(key)) EnemySync.OnTargetResident(slot, key);
            }
            float graceWindow = NetConfig.ResidencyGraceSeconds.Value;
            foreach (var key in previous)
            {
                if (set.Contains(key)) continue;
                // The peer unloaded this segment. Keep it grace-resident briefly so a one-frame
                // report flicker (the game still has the segment loaded) doesn't flip the lease.
                if (graceWindow > 0f) ResidentGraceUntil[(slot, key)] = Time.unscaledTime + graceWindow;
                // If it owns the lease, its dormancy commit is owed; give it a grace window before
                // the coordinator-cache fallback.
                if (Leases.TryGetValue(key, out var lease) && lease.Owner == slot
                    && !PendingDormancy.ContainsKey(key))
                    PendingDormancy[key] = Time.unscaledTime + DormancyCommitGrace;
            }
            // Drop expired grace entries so the map can't grow unbounded across a long run.
            if (ResidentGraceUntil.Count > 0)
            {
                _graceScratch.Clear();
                float nowT = Time.unscaledTime;
                foreach (var kv in ResidentGraceUntil) if (kv.Value <= nowT) _graceScratch.Add(kv.Key);
                foreach (var k in _graceScratch) ResidentGraceUntil.Remove(k);
            }
            _nextScanAt = 0; // residency changed — react promptly
        }

        public static bool IsResident(byte slot, SegmentKey key) =>
            ResidentSets.TryGetValue(slot, out var set) && set.Contains(key);

        // Resident, OR we have no residency report from this peer yet — used to gate interest
        // baselines (don't withhold the very first baseline before the peer has ever reported).
        public static bool IsResidentOrUnknown(byte slot, SegmentKey key) =>
            !ResidentSets.TryGetValue(slot, out var set) || set.Contains(key);

        // Within the post-drop grace window (RC2). Only ever used to retain the current owner.
        private static bool IsInResidencyGrace(byte slot, SegmentKey key) =>
            ResidentGraceUntil.TryGetValue((slot, key), out float until) && Time.unscaledTime < until;
        public static void OnDormantHit(int netId, byte attackerSlot)
        {
            var session = NetSession.Instance;
            if (session == null || !session.IsHost || EnemySync.IsKilled(netId)) return;
            if (!TrySegmentOf(netId, out var key)) return;
            ForcedOwners[key] = (attackerSlot, Time.unscaledTime + 5f);
            if (!Leases.TryGetValue(key, out var lease))
                Leases[key] = lease = new Lease { Owner = DormantOwner, Epoch = 0 };
            if (lease.Owner != attackerSlot && (!lease.Pending || lease.PendingOwner != attackerSlot))
                Prepare(session, key, lease, attackerSlot);
            else if (lease.Owner == attackerSlot)
                DamageSync.OnSegmentAuthorityCommitted(key, attackerSlot);
        }

        public static void OnPeerLost(byte slot)
        {
            var session = NetSession.Instance;
            ResidentSets.Remove(slot);
            ResidencyRevs.Remove(slot);
            PeerLoads.Remove(slot);
            _lostScratch.Clear();
            foreach (var pair in Leases)
            {
                var l = pair.Value;
                if (l.Owner == slot || (l.Pending && l.PendingOwner == slot))
                {
                    if (l.Pending)
                    {
                        uint cancelledEpoch = l.PendingEpoch;
                        if (session != null && session.IsHost)
                            SendLease(session, pair.Key, l.Owner, cancelledEpoch, 2);
                        EnemySync.CancelHandoffFreeze(pair.Key, cancelledEpoch);
                    }
                    l.Pending = false;
                    l.PreparedAt = 0;
                    if (l.Owner == slot) _lostScratch.Add(pair.Key);
                }
            }
            // A disconnected owner can never send its dormancy commit — its leases resolve now,
            // from the coordinator's cache. Segments other peers stream are re-granted by the
            // next scan; the rest stay Dormant.
            if (session != null && session.IsHost)
            {
                foreach (var key in _lostScratch)
                    CommitDormant(session, key, Leases[key],
                        DeferredDormancyCommits.TryGetValue(key, out byte by) && by == slot
                            ? DormantReason.OwnerCommit : DormantReason.PeerLost);
                if (_lostScratch.Count > 0)
                    Plugin.Log.LogInfo($"[Lease] P{slot + 1} disconnected — resolved " +
                        $"{_lostScratch.Count} owned segment(s) to dormant from the coordinator cache");
            }
            _nextScanAt = 0;
        }

        private static readonly List<SegmentKey> _lostScratch = new List<SegmentKey>();

        /// <summary>Migration: the promoted host continues the epoch sequence — restarting at 1
        /// would make every new PREPARE lose to the higher epochs peers already hold.</summary>
        internal static void OnPromotedToHost()
        {
            uint max = 0;
            foreach (var l in Leases.Values)
            {
                if (l.Epoch > max) max = l.Epoch;
                if (l.Pending && l.PendingEpoch > max) max = l.PendingEpoch;
            }
            if (_nextEpoch <= max) _nextEpoch = max + 1;
            _nextScanAt = 0;
        }

        public static void Tick(NetSession session)
        {
            if (!session.IsHost || !NetIds.ManifestComplete || Time.unscaledTime < _nextScanAt) return;
            _nextScanAt = Time.unscaledTime + ScanInterval;

            if (Time.unscaledTime >= _nextLedgerAt)
            {
                _nextLedgerAt = Time.unscaledTime + 5f;
                var kills = EnemySync.KilledSnapshot();
                if (kills.Count > 0)
                {
                    if (_ledgerCursor >= kills.Count) _ledgerCursor = 0;
                    int count = Math.Min(128, kills.Count);
                    var chunk = new List<(int, uint, uint)>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int netId = kills[(_ledgerCursor + i) % kills.Count];
                        chunk.Add((netId, NetIds.LifetimeOf(netId), EnemySync.MutationRevisionOf(netId)));
                    }
                    _ledgerCursor = (_ledgerCursor + count) % kills.Count;
                    Writer.Reset();
                    new KillLedgerMsg { Entries = chunk }.Write(Writer);
                    session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                    InstrumentationCounters.KillLedgerSent(chunk.Count);
                }
            }

            var simulators = new List<(byte slot, SegmentKey segment)>(4);
            foreach (var p in session.Players)
            {
                if (p == null || !p.Connected) continue;
                if (!ShipSync.ShipsBySlot.TryGetValue(p.Slot, out var ship) || ship == null || ship.IsDead) continue;
                simulators.Add((p.Slot, SegmentOf(ship.transform.position)));
            }
            // During the first few frames ShipSync may not yet expose the local ship.
            if (simulators.Count == 0 && ShipSync.LocalShip != null)
                simulators.Add(((byte)session.LocalSlot, SegmentOf(ShipSync.LocalShip.transform.position)));

            // The scan set IS residency: every segment somebody streams, plus every lease that
            // still names an owner (so owner-departure can resolve to handoff or dormancy).
            Interested.Clear();
            foreach (var kv in ResidentSets)
                foreach (var key in kv.Value)
                    Interested.Add(key);
            foreach (var kv in Leases)
                if (kv.Value.Owner != DormantOwner || kv.Value.Pending) Interested.Add(kv.Key);

            // WS10.1: owned-segment counts for the soft cap, one pass per scan (not per segment).
            ScanOwnedCounts.Clear();
            foreach (var kv in Leases)
            {
                if (kv.Value.Owner == DormantOwner) continue;
                ScanOwnedCounts.TryGetValue(kv.Value.Owner, out int n);
                ScanOwnedCounts[kv.Value.Owner] = n + 1;
            }

            _claims.Clear();
            foreach (var key in Interested)
            {
                Leases.TryGetValue(key, out var lease);
                byte currentOwner = lease != null ? lease.Owner : DormantOwner;
                byte desired = SelectOwner(key, simulators, lease);
                if (desired == currentOwner)
                {
                    if (lease != null && lease.Pending)
                    {
                        uint cancelledEpoch = lease.PendingEpoch;
                        lease.Pending = false;
                        SendLease(session, key, lease.Owner, cancelledEpoch, 2);
                        EnemySync.CancelHandoffFreeze(key, cancelledEpoch);
                    }
                    continue;
                }
                if (desired == DormantOwner)
                {
                    // Owner left and nobody else streams the segment. If the owner's dormancy
                    // commit already arrived (deferred while a handoff looked possible), the
                    // canonical store is fed — go Dormant now, no grace. Otherwise wait for the
                    // commit (or the grace fallback below) — never a blind reassignment to a
                    // peer that can't simulate it.
                    if (lease != null && lease.Owner != DormantOwner
                        && DeferredDormancyCommits.TryGetValue(key, out byte committedBy)
                        && committedBy == lease.Owner)
                    {
                        PendingDormancy.Remove(key);
                        CommitDormant(session, key, lease, DormantReason.OwnerCommit);
                        continue;
                    }
                    if (lease != null && !PendingDormancy.ContainsKey(key))
                        PendingDormancy[key] = Time.unscaledTime + DormancyCommitGrace;
                    continue;
                }
                var readinessKey = (key, desired);
                if (UnreadyUntil.TryGetValue(readinessKey, out float until))
                {
                    if (Time.unscaledTime < until) continue;
                    UnreadyUntil.Remove(readinessKey);
                }
                if (lease != null && lease.Pending && lease.PendingOwner == desired
                    && Time.unscaledTime - lease.PreparedAt < PrepareRetry) continue;
                // Distance from the segment to its would-be owner's ship: the segment the player
                // is standing in (and fighting in) must claim FIRST. HashSet iteration order made
                // that random — a boss segment could be last in its wave.
                int distance = int.MaxValue;
                foreach (var s in simulators)
                    if (s.slot == desired) distance = Math.Min(distance, Chebyshev(key, s.segment));
                _claims.Add((key, lease, desired, distance));
            }

            _claims.Sort((a, b) => a.distance.CompareTo(b.distance));
            int preparesHandoff = 0, preparesDormant = 0;
            var budget = System.Diagnostics.Stopwatch.StartNew();
            foreach (var claim in _claims)
            {
                var (key, lease, desired, _) = claim;
                // Dormant activations get a far bigger allowance than live P2P handoffs: waking
                // generation entities is local bookkeeping (commits batch into one flush now),
                // while a real handoff moves baselines between peers and stays throttled.
                bool dormantClaim = lease == null || lease.Owner == DormantOwner;
                if (dormantClaim)
                {
                    if (preparesDormant >= MinDormantClaimsPerScan && budget.Elapsed.TotalMilliseconds > PrepareBudgetMs)
                    {
                        _nextScanAt = 0; // burst not drained — continue next frame, not in 0.5 s
                        break;
                    }
                    preparesDormant++;
                }
                else
                {
                    if (preparesHandoff >= MinPreparesPerScan && budget.Elapsed.TotalMilliseconds > PrepareBudgetMs)
                    {
                        _nextScanAt = 0;
                        break;
                    }
                    preparesHandoff++;
                }
                if (lease == null)
                {
                    lease = new Lease { Owner = DormantOwner, Epoch = 0 };
                    Leases[key] = lease;
                }
                PendingDormancy.Remove(key);
                Prepare(session, key, lease, desired);
            }

            // Owners that unloaded without a commit reaching us (crash, lost peer): after the
            // grace, the coordinator's own state cache is the dormancy source.
            _scratch.Clear();
            foreach (var kv in PendingDormancy)
                if (Time.unscaledTime >= kv.Value) _scratch.Add(kv.Key);
            foreach (var key in _scratch)
            {
                PendingDormancy.Remove(key);
                if (!Leases.TryGetValue(key, out var lease) || lease.Owner == DormantOwner) continue;
                if (IsResident(lease.Owner, key)) continue; // owner came back — keep the lease
                CommitDormant(session, key, lease, DormantReason.GraceFallback);
            }

            // Everything this scan committed flips in one entity pass.
            EnemySync.FlushSegmentOwnership();
        }

        private static readonly List<SegmentKey> _scratch = new List<SegmentKey>();
        private static readonly List<(SegmentKey key, Lease lease, byte desired, int distance)> _claims
            = new List<(SegmentKey, Lease, byte, int)>();

        private static byte SelectOwner(SegmentKey key, List<(byte slot, SegmentKey segment)> sims, Lease current)
        {
            if (ForcedOwners.TryGetValue(key, out var forced))
            {
                if (Time.unscaledTime < forced.until) return forced.owner;
                ForcedOwners.Remove(key);
            }
            // Sticky while the current owner's game still streams the segment (no distance
            // re-optimization); otherwise the closest RESIDENT peer; nobody resident = Dormant.
            if (current != null && current.Owner != DormantOwner && IsResident(current.Owner, key))
                return current.Owner;
            byte best = DormantOwner;
            int bestDistance = int.MaxValue;
            byte bestLoad = byte.MaxValue;
            // WS10.1: two passes. Pass 0 skips candidates that are overloaded AND already at their
            // segment cap; pass 1 (only if pass 0 found nobody) ignores the cap — a struggling
            // resident peer still beats artificial dormancy (frozen enemies are the worse failure).
            // Among equals, lower load wins before the slot-number tiebreak.
            for (int pass = 0; pass < 2 && best == DormantOwner; pass++)
            {
                foreach (var s in sims)
                {
                    if (!IsResident(s.slot, key)) continue;
                    if (pass == 0
                        && PeerLoads.TryGetValue(s.slot, out byte candidateLoad) && candidateLoad >= OverloadThreshold
                        && ScanOwnedCounts.TryGetValue(s.slot, out int owned) && owned >= OverloadedSegmentCap)
                        continue;
                    int d = Chebyshev(key, s.segment);
                    byte load = PeerLoads.TryGetValue(s.slot, out byte l) ? l : (byte)0;
                    if (d < bestDistance
                        || (d == bestDistance && load < bestLoad)
                        || (d == bestDistance && load == bestLoad && s.slot < best))
                    {
                        bestDistance = d;
                        bestLoad = load;
                        best = s.slot;
                    }
                }
            }
            // No peer is strictly resident this instant. Rather than flip away from (or dormant-ize)
            // the current owner on a one-frame streaming flicker, keep it while it's inside its
            // residency grace window. This RETAINS the current owner only — a new owner is still
            // only ever a strictly-resident peer (loop above) — so "leases only within reported
            // residency" holds for grants; grace merely defers the release for sub-second boundary
            // flicker (RC2 authChurn). A genuinely-present closer peer above wins immediately.
            if (best == DormantOwner && current != null && current.Owner != DormantOwner
                && IsInResidencyGrace(current.Owner, key))
                return current.Owner;
            return best;
        }

        private static int Chebyshev(SegmentKey a, SegmentKey b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private static void Prepare(NetSession session, SegmentKey key, Lease lease, byte owner)
        {
            if (lease.Pending)
            {
                uint cancelledEpoch = lease.PendingEpoch;
                SendLease(session, key, lease.Owner, cancelledEpoch, 2);
                EnemySync.CancelHandoffFreeze(key, cancelledEpoch);
                lease.Pending = false;
            }
            if (lease.Owner == DormantOwner)
            {
                // Activation from Dormant: nobody holds live state to transfer — the canonical
                // store is already replicated on every peer (dormancy commits broadcast; late
                // joiners get DormantState), so the baseline round-trip bought nothing but two
                // RTTs of frozen mannequins plus a NACK-retry loop whenever the target's loader
                // hadn't finished building (the 1,898-baseline / 142-incomplete / 26-reject
                // churn in the 0.1.96 playtest). Commit directly: entities arm as the target
                // materializes them, and canonical state applies on spawn.
                lease.Pending = true;
                lease.PendingOwner = owner;
                lease.PendingEpoch = _nextEpoch++;
                lease.PreparedAt = Time.unscaledTime;
                NetStats.AuthFlips++;
                Commit(session, key, lease);
                return;
            }
            lease.Pending = true;
            lease.PendingOwner = owner;
            lease.PendingEpoch = _nextEpoch++;
            lease.PreparedAt = Time.unscaledTime;
            NetStats.AuthFlips++;
            SendLease(session, key, owner, lease.PendingEpoch, 0);
            if (NetDiag.Enabled) NetDiag.Log("Lease", $"segment {key} prepare P{owner + 1} epoch={lease.PendingEpoch} (current {NetDiag.Owner(lease.Owner)}/{lease.Epoch})");
            // PREPARE only reserves the epoch. The old simulator remains authoritative until the
            // candidate installs and ACKs a reliable full runtime baseline for this exact epoch.
            EnemySync.BeginHandoffBaseline(key, lease.Owner, owner, lease.Epoch, lease.PendingEpoch, session);
        }

        private static void Commit(NetSession session, SegmentKey key, Lease lease)
        {
            byte old = lease.Owner;
            float duration = lease.PreparedAt > 0f ? Time.unscaledTime - lease.PreparedAt : 0f;
            lease.Owner = lease.PendingOwner;
            lease.Epoch = lease.PendingEpoch;
            lease.Pending = false;
            // The handoff the deferred commit waited on happened — the latch is spent (a stale
            // one could later dormant-commit a NEW owner's lease without its commit).
            DeferredDormancyCommits.Remove(key);
            SendLease(session, key, lease.Owner, lease.Epoch, 1);
            EnemySync.FinalizeHandoffFreeze(key, lease.Epoch);
            EnemySync.MarkSegmentOwnershipDirty(key); // batched: one entity pass per wave, not per segment
            EnemySync.NoteLeaseCommitted(key, lease.Epoch, lease.Owner);
            DamageSync.OnSegmentAuthorityCommitted(key, lease.Owner);
            InstrumentationCounters.LeaseCommitted();
            InstrumentationCounters.HandoffCommitted(duration);
            // Dormant->grant cycles run at player movement rate (2,785 lease lines in one
            // 6-minute playtest); BepInEx log writes are synchronous, so routine cycles are
            // diag-gated and only true peer->peer handoffs stay at Info.
            if (old == DormantOwner)
            {
                if (NetDiag.Enabled)
                    NetDiag.Log("Lease", $"segment {key} dormant->P{lease.Owner + 1} epoch={lease.Epoch}");
            }
            else
                Plugin.Log.LogInfo($"[Lease] segment {key} {NetDiag.Owner(old)}->P{lease.Owner + 1} epoch={lease.Epoch} " +
                    $"handoff={duration * 1000f:0}ms");
        }

        /// <summary>Host: move a segment to Dormant — nobody simulates it; the canonical store
        /// (fed by the owner's dormancy commit, or the host's own snapshot cache on the
        /// non-commit reasons) is the truth until residency re-activates it.</summary>
        private static void CommitDormant(NetSession session, SegmentKey key, Lease lease, DormantReason reason)
        {
            if (lease.Pending)
            {
                SendLease(session, key, lease.Owner, lease.PendingEpoch, 2);
                EnemySync.CancelHandoffFreeze(key, lease.PendingEpoch);
                lease.Pending = false;
            }
            byte old = lease.Owner;
            lease.Owner = DormantOwner;
            lease.Epoch = _nextEpoch++;
            lease.PreparedAt = 0f;
            DeferredDormancyCommits.Remove(key);
            SendLease(session, key, DormantOwner, lease.Epoch, 1);
            EnemySync.MarkSegmentOwnershipDirty(key);
            InstrumentationCounters.DormantTransition(reason != DormantReason.OwnerCommit);
            // Routine commit-driven transitions are diag-gated (movement-rate volume). Disconnect
            // cleanup is expected and says so. Only the grace fallback stays loud — after the
            // deferred-commit latch, it means an owner truly left without its commit.
            switch (reason)
            {
                case DormantReason.GraceFallback:
                    Plugin.Log.LogWarning($"[Lease] segment {key} {NetDiag.Owner(old)}->dormant epoch={lease.Epoch} " +
                        "(coordinator-cache fallback — no dormancy commit received within grace)");
                    break;
                case DormantReason.PeerLost:
                    if (NetDiag.Enabled)
                        NetDiag.Log("Lease", $"segment {key} {NetDiag.Owner(old)}->dormant epoch={lease.Epoch} " +
                            "(owner disconnected — resolved from coordinator cache)");
                    break;
                default:
                    if (NetDiag.Enabled)
                        NetDiag.Log("Lease", $"segment {key} {NetDiag.Owner(old)}->dormant epoch={lease.Epoch}");
                    break;
            }
        }

        /// <summary>Host: an owner committed its final states for a segment it is unloading. If
        /// another peer streams the segment the ordinary scan hands it over (the commit already
        /// refreshed the state cache the baseline will draw from); otherwise it goes Dormant.</summary>
        internal static void OnDormancyCommit(SegmentKey key, byte sender, NetSession session)
        {
            if (!session.IsHost) return;
            PendingDormancy.Remove(key);
            if (!Leases.TryGetValue(key, out var lease) || lease.Owner != sender) return;
            foreach (var kv in ResidentSets)
                if (kv.Key != sender && kv.Value.Contains(key))
                {
                    // A resident peer exists — let the scan hand off instead. But LATCH the
                    // commit: if that peer unloads before the handoff, the scan resolves the
                    // segment dormant from this commit, not the crash-grace cache fallback.
                    DeferredDormancyCommits[key] = sender;
                    _nextScanAt = 0;
                    return;
                }
            CommitDormant(session, key, lease, DormantReason.OwnerCommit);
        }

        private static void SendLease(NetSession session, SegmentKey key, byte owner, uint epoch, byte phase)
        {
            Writer.Reset();
            new SegmentLeaseMsg { X = key.X, Y = key.Y, Owner = owner, Epoch = epoch, Phase = phase }.Write(Writer);
            session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
        }

        internal static void ApplyLease(SegmentLeaseMsg msg, NetSession session)
        {
            var key = new SegmentKey(msg.X, msg.Y);
            bool existed = Leases.TryGetValue(key, out var lease);
            if (!existed)
            {
                lease = new Lease { Owner = DormantOwner, Epoch = 0 };
                Leases[key] = lease;
            }
            if (msg.Phase == 0)
            {
                if (msg.Epoch < lease.Epoch) return;
                lease.Pending = true; lease.PendingOwner = msg.Owner; lease.PendingEpoch = msg.Epoch;
                return;
            }
            if (msg.Phase == 2)
            {
                if (lease.Pending && lease.PendingEpoch == msg.Epoch) lease.Pending = false;
                EnemySync.CancelHandoffFreeze(key, msg.Epoch);
                return;
            }
            if (msg.Epoch < lease.Epoch) return;
            if (existed && msg.Epoch == lease.Epoch && msg.Owner == lease.Owner && !lease.Pending)
                return; // reliable replay/catch-up duplicate; ownership components are already correct
            lease.Owner = msg.Owner; lease.Epoch = msg.Epoch; lease.Pending = false;
            EnemySync.FinalizeHandoffFreeze(key, msg.Epoch);
            EnemySync.MarkSegmentOwnershipDirty(key);
            EnemySync.NoteLeaseCommitted(key, msg.Epoch, msg.Owner);
        }

        internal static void CompleteBaselineHandoff(SegmentKey key, byte owner, uint epoch, NetSession session)
        {
            if (!session.IsHost || !Leases.TryGetValue(key, out var lease) || !lease.Pending) return;
            if (lease.PendingOwner != owner || lease.PendingEpoch != epoch) return;
            UnreadyUntil.Remove((key, owner));
            InstrumentationCounters.LeaseAcked();
            Commit(session, key, lease);
        }

        internal static void RejectBaselineHandoff(SegmentKey key, byte owner, uint epoch,
            IReadOnlyCollection<int> missingNetIds, NetSession session)
        {
            if (!session.IsHost || !Leases.TryGetValue(key, out var lease) || !lease.Pending) return;
            if (lease.PendingOwner != owner || lease.PendingEpoch != epoch) return;
            SendLease(session, key, lease.Owner, epoch, 2);
            EnemySync.CancelHandoffFreeze(key, epoch);
            lease.Pending = false;
            lease.PreparedAt = 0f;
            InstrumentationCounters.HandoffRejected();
            // Give the target segment loader time to finish. The next retry will carry the same
            // expected roster and can commit as soon as every GameObject is concrete.
            UnreadyUntil[(key, owner)] = Time.unscaledTime + 3f;
            string missing = missingNetIds == null || missingNetIds.Count == 0
                ? "unknown"
                : string.Join(",", missingNetIds.Take(16).Select(id => "#" + id));
            Plugin.Log.LogWarning($"[Lease] segment {key} remains P{lease.Owner + 1}; " +
                $"P{owner + 1} roster incomplete for epoch={epoch} missing={missing}");
        }

        /// <summary>Segments this machine owns whose GameObjects the game is not streaming here —
        /// authority without residency, the split-brain precondition. The Phase-2 residency
        /// leases make this structurally impossible; until then it must be watched, not assumed.</summary>
        internal static int CountOwnedSegmentsNotResident(NetSession session)
        {
            if (session == null) return 0;
            var active = EnemySync.TryGetActiveSegments();
            if (active == null) return 0;
            int count = 0;
            foreach (var kv in Leases)
                if (kv.Value.Owner == session.LocalSlot
                    && !active.Contains(new Vector2Int(kv.Key.X, kv.Key.Y)))
                    count++;
            return count;
        }

        internal static List<SegmentLeaseMsg> Snapshot()
        {
            var result = new List<SegmentLeaseMsg>(Leases.Count);
            foreach (var kv in Leases)
                result.Add(new SegmentLeaseMsg { X = kv.Key.X, Y = kv.Key.Y, Owner = kv.Value.Owner, Epoch = kv.Value.Epoch, Phase = 1 });
            return result;
        }
    }
}
