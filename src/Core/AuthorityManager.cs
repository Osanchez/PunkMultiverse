using System;
using System.Collections.Generic;
using PunkMultiverse.Protocol;
using PunkMultiverse.Sync;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Host-arbitrated, segment-scoped simulation leases. A transfer is transactional: the old
    /// owner remains authoritative during PREPARE; the selected peer installs the lease and ACKs;
    /// only then does the host broadcast COMMIT. Every commit increments the segment epoch, so
    /// delayed state from a prior simulator is harmless.
    /// </summary>
    internal static class AuthorityManager
    {
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
        private const int AcquireRadiusSegments = 3;
        private const int KeepRadiusSegments = 4;
        private const int MaxPreparesPerScan = 8;
        private static readonly Dictionary<SegmentKey, Lease> Leases = new Dictionary<SegmentKey, Lease>();
        private static readonly Dictionary<int, SegmentKey> EntitySegments = new Dictionary<int, SegmentKey>();
        private static readonly HashSet<SegmentKey> Interested = new HashSet<SegmentKey>();
        private static readonly HashSet<SegmentKey> Retained = new HashSet<SegmentKey>();
        private static readonly Dictionary<SegmentKey, (byte owner, float until)> ForcedOwners = new Dictionary<SegmentKey, (byte, float)>();
        private static readonly NetWriter Writer = new NetWriter(256);
        private static float _nextScanAt;
        private static uint _nextEpoch = 1;
        private static float _nextLedgerAt;
        private static int _ledgerCursor;

        internal static int CommittedLeaseCount => Leases.Count;
        internal static int PendingLeaseCount { get { int n = 0; foreach (var l in Leases.Values) if (l.Pending) n++; return n; } }

        public static void Reset()
        {
            Leases.Clear();
            EntitySegments.Clear();
            Interested.Clear();
            Retained.Clear();
            ForcedOwners.Clear();
            _nextScanAt = 0;
            _nextEpoch = 1;
            _nextLedgerAt = 0;
            _ledgerCursor = 0;
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
            var s = NetSession.Instance;
            return s != null ? s.HostSlot : (byte)0;
        }

        internal static uint EpochOf(int netId)
        {
            if (EnemySync.FixedOwners.Contains(netId)) return 0;
            return TrySegmentOf(netId, out var key) && Leases.TryGetValue(key, out var lease) ? lease.Epoch : 0;
        }

        internal static byte OwnerOf(SegmentKey key)
        {
            if (Leases.TryGetValue(key, out var lease)) return lease.Owner;
            var s = NetSession.Instance;
            return s != null ? s.HostSlot : (byte)0;
        }

        internal static uint EpochOf(SegmentKey key) => Leases.TryGetValue(key, out var lease) ? lease.Epoch : 0;

        internal static bool IsStateAuthority(int netId, SegmentKey key, byte owner, uint epoch)
        {
            if (EnemySync.FixedOwners.Contains(netId))
                return epoch == 0 && EnemySync.Owners.TryGetValue(netId, out byte fixedOwner) && fixedOwner == owner;
            if (Leases.TryGetValue(key, out var lease)) return lease.Owner == owner && lease.Epoch == epoch;
            var s = NetSession.Instance;
            return epoch == 0 && owner == (s != null ? s.HostSlot : (byte)0);
        }

        public static void NoteCombat(int netId) { }
        public static void NoteAggro(int netId, byte targetSlot) { }
        public static void OnReleased(int netId, byte releasingSlot) { }
        public static void OnDormantHit(int netId, byte attackerSlot)
        {
            var session = NetSession.Instance;
            if (session == null || !session.IsHost || EnemySync.IsKilled(netId)) return;
            if (!TrySegmentOf(netId, out var key)) return;
            ForcedOwners[key] = (attackerSlot, Time.unscaledTime + 5f);
            if (!Leases.TryGetValue(key, out var lease))
                Leases[key] = lease = new Lease { Owner = session.HostSlot, Epoch = 0 };
            if (lease.Owner != attackerSlot && (!lease.Pending || lease.PendingOwner != attackerSlot))
                Prepare(session, key, lease, attackerSlot);
            else if (lease.Owner == attackerSlot)
                DamageSync.OnSegmentAuthorityCommitted(key, attackerSlot);
        }

        public static void OnPeerLost(byte slot)
        {
            foreach (var l in Leases.Values)
            {
                if (l.Owner == slot || (l.Pending && l.PendingOwner == slot))
                {
                    l.Pending = false;
                    l.PreparedAt = 0;
                }
            }
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
                    var chunk = new List<int>(count);
                    for (int i = 0; i < count; i++) chunk.Add(kills[(_ledgerCursor + i) % kills.Count]);
                    _ledgerCursor = (_ledgerCursor + count) % kills.Count;
                    Writer.Reset();
                    new KillLedgerMsg { NetIds = chunk }.Write(Writer);
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

            Interested.Clear();
            Retained.Clear();
            foreach (var sim in simulators)
            {
                for (int x = sim.segment.X - AcquireRadiusSegments; x <= sim.segment.X + AcquireRadiusSegments; x++)
                    for (int y = sim.segment.Y - AcquireRadiusSegments; y <= sim.segment.Y + AcquireRadiusSegments; y++)
                        Interested.Add(new SegmentKey(x, y));
                for (int x = sim.segment.X - KeepRadiusSegments; x <= sim.segment.X + KeepRadiusSegments; x++)
                    for (int y = sim.segment.Y - KeepRadiusSegments; y <= sim.segment.Y + KeepRadiusSegments; y++)
                        Retained.Add(new SegmentKey(x, y));
            }

            int budget = MaxPreparesPerScan;
            foreach (var key in Interested)
            {
                byte desired = SelectOwner(key, simulators, session.HostSlot);
                if (!Leases.TryGetValue(key, out var lease))
                {
                    lease = new Lease { Owner = session.HostSlot, Epoch = 0 };
                    Leases[key] = lease;
                }
                if (lease.Owner == desired) { lease.Pending = false; continue; }
                if (lease.Pending && lease.PendingOwner == desired && Time.unscaledTime - lease.PreparedAt < PrepareRetry) continue;
                if (budget-- <= 0) break;
                Prepare(session, key, lease, desired);
            }

            // Segments no player streams no longer need a client simulator. Return them to the
            // host nominally; their entities remain dormant until somebody streams the segment.
            _scratch.Clear();
            foreach (var kv in Leases)
                if (!Retained.Contains(kv.Key) && kv.Value.Owner != session.HostSlot) _scratch.Add(kv.Key);
            foreach (var key in _scratch)
            {
                if (budget-- <= 0) break;
                Prepare(session, key, Leases[key], session.HostSlot);
            }
        }

        private static readonly List<SegmentKey> _scratch = new List<SegmentKey>();

        private static byte SelectOwner(SegmentKey key, List<(byte slot, SegmentKey segment)> sims, byte fallback)
        {
            if (ForcedOwners.TryGetValue(key, out var forced))
            {
                if (Time.unscaledTime < forced.until) return forced.owner;
                ForcedOwners.Remove(key);
            }
            // Sticky while the current owner still streams this segment; otherwise closest wins.
            if (Leases.TryGetValue(key, out var current))
                foreach (var s in sims)
                    if (s.slot == current.Owner && Chebyshev(key, s.segment) <= KeepRadiusSegments) return current.Owner;
            byte best = fallback;
            int bestDistance = int.MaxValue;
            foreach (var s in sims)
            {
                int d = Chebyshev(key, s.segment);
                if (d > AcquireRadiusSegments) continue;
                if (d < bestDistance || (d == bestDistance && s.slot < best)) { bestDistance = d; best = s.slot; }
            }
            return best;
        }

        private static int Chebyshev(SegmentKey a, SegmentKey b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private static void Prepare(NetSession session, SegmentKey key, Lease lease, byte owner)
        {
            lease.Pending = true;
            lease.PendingOwner = owner;
            lease.PendingEpoch = _nextEpoch++;
            lease.PreparedAt = Time.unscaledTime;
            NetStats.AuthFlips++;
            SendLease(session, key, owner, lease.PendingEpoch, 0);
            if (NetDiag.Enabled) NetDiag.Log("Lease", $"segment {key} prepare P{owner + 1} epoch={lease.PendingEpoch} (current P{lease.Owner + 1}/{lease.Epoch})");
            if (owner == session.LocalSlot) Commit(session, key, lease);
        }

        private static void Commit(NetSession session, SegmentKey key, Lease lease)
        {
            byte old = lease.Owner;
            lease.Owner = lease.PendingOwner;
            lease.Epoch = lease.PendingEpoch;
            lease.Pending = false;
            SendLease(session, key, lease.Owner, lease.Epoch, 1);
            EnemySync.ApplySegmentOwnership(key);
            DamageSync.OnSegmentAuthorityCommitted(key, lease.Owner);
            InstrumentationCounters.LeaseCommitted();
            Plugin.Log.LogInfo($"[Lease] segment {key} P{old + 1}->P{lease.Owner + 1} epoch={lease.Epoch}");
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
                lease = new Lease { Owner = session.HostSlot, Epoch = 0 };
                Leases[key] = lease;
            }
            if (msg.Phase == 0)
            {
                if (msg.Epoch < lease.Epoch) return;
                lease.Pending = true; lease.PendingOwner = msg.Owner; lease.PendingEpoch = msg.Epoch;
                if (msg.Owner == session.LocalSlot && !session.IsHost)
                {
                    Writer.Reset();
                    new SegmentLeaseAckMsg { X = msg.X, Y = msg.Y, Owner = msg.Owner, Epoch = msg.Epoch }.Write(Writer);
                    session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                    InstrumentationCounters.LeaseAcked();
                }
                return;
            }
            if (msg.Epoch < lease.Epoch) return;
            if (existed && msg.Epoch == lease.Epoch && msg.Owner == lease.Owner && !lease.Pending)
                return; // reliable replay/catch-up duplicate; ownership components are already correct
            lease.Owner = msg.Owner; lease.Epoch = msg.Epoch; lease.Pending = false;
            EnemySync.ApplySegmentOwnership(key);
        }

        internal static void ApplyAck(SegmentLeaseAckMsg msg, NetSession session)
        {
            if (!session.IsHost) return;
            var key = new SegmentKey(msg.X, msg.Y);
            if (!Leases.TryGetValue(key, out var lease) || !lease.Pending) return;
            if (lease.PendingOwner != msg.Owner || lease.PendingEpoch != msg.Epoch) return;
            Commit(session, key, lease);
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
