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
    /// Authority-routed damage for player ships. A ship's HP truth lives on its owner's machine:
    /// local hits on a puppet become DAMAGE_REQUESTs to the owner (via host relay); the owner runs
    /// the full vanilla TakeDamage pipeline (shields, damage matrix, i-frames) exactly once.
    /// Death/resurrection replicate as reliable events.
    /// </summary>
    internal static class DamageSync
    {
        private static readonly NetWriter Writer = new NetWriter(64);
        private static Dictionary<uint, Resource> _resourcesByHash;
        private static bool _applyingRemote;
        internal static bool IsApplyingRemote => _applyingRemote;

        // netId -> slot of the player who last damaged this entity, tracked on the machine that
        // simulates it (the owner). At death this is the killer, so the loot goes to whoever
        // actually earned it instead of everyone standing nearby (see Patches.LootDiag).
        private static readonly Dictionary<int, byte> LastDamager = new Dictionary<int, byte>();
        // Host-side dormant claims: damage whose owner has no live object yet. Drained when the
        // segment's authority commits (usually to the attacker via OnDormantHit); pruned after
        // DormantClaimTtl so unownable races (attacker unloaded/disconnected mid-claim) cannot
        // accumulate forever. Deliberately NOT applied "in absentia" — a claim without any
        // possible simulator is a sub-second race, not a state worth a parallel HP ledger.
        private static readonly List<(DamageRequestMsg msg, float queuedAt)> PendingDormantDamage
            = new List<(DamageRequestMsg, float)>();
        private const float DormantClaimTtl = 15f;
        private static readonly HashSet<ulong> SeenDamageRequests = new HashSet<ulong>();
        private static readonly Queue<ulong> SeenDamageRequestOrder = new Queue<ulong>();
        private static uint _requestSequence;
        private static int _takeDamageListDepth;
        private const int RecentDamageRequestLimit = 8192;

        private struct DamageAuditState
        {
            internal bool Track;
            internal bool Applied;
            internal float HpBefore;
            internal float ShieldBefore;
            internal float Amount;
            internal string Type;
            internal ProjectileSync.DamageTrace Trace;
            internal bool HasTrace;
            internal bool EnteredList;
        }

        /// <summary>Slot that last damaged the entity, or 255 if unknown.</summary>
        public static byte LastKiller(int netId) => LastDamager.TryGetValue(netId, out var s) ? s : (byte)255;

        public static void Reset()
        {
            _resourcesByHash = null;
            _applyingRemote = false;
            LastDamager.Clear();
            PendingDormantDamage.Clear();
            SeenDamageRequests.Clear();
            SeenDamageRequestOrder.Clear();
            _requestSequence = 0;
            _takeDamageListDepth = 0;
            ResetLifeWatchdog(); // fresh run starts alive and un-announced
        }

        public static uint HashName(string name)
        {
            uint hash = 2166136261;
            foreach (char c in name)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }

        private static Resource ResolveType(uint hash)
        {
            if (hash == 0) return null;
            if (_resourcesByHash == null)
            {
                _resourcesByHash = new Dictionary<uint, Resource>();
                foreach (var res in Resources.FindObjectsOfTypeAll<Resource>())
                    _resourcesByHash[HashName(res.name)] = res;
            }
            return _resourcesByHash.TryGetValue(hash, out var r) ? r : null;
        }

        // ---------------------------------------------------------------- interception

        /// <summary>Non-null routing target when this victim is simulated elsewhere.</summary>
        private static bool TryGetRemoteTarget(DamagableResource dr, out bool isEntity, out byte slot, out int netId)
        {
            isEntity = false;
            slot = 0;
            netId = 0;
            var shipPuppet = dr.GetComponent<RemotePuppet>();
            if (shipPuppet != null)
            {
                slot = shipPuppet.Slot;
                return true;
            }
            var entityPuppet = dr.GetComponent<RemoteEntityPuppet>();
            if (entityPuppet != null)
            {
                if (entityPuppet.NetId < 0) return false; // muted orphan — damage applies locally
                isEntity = true;
                netId = entityPuppet.NetId;
                return true;
            }
            // Held physics prop: its HP truth lives on the simulator like any entity's. Local
            // application here was the old per-machine crate-HP divergence.
            var hold = dr.GetComponent<PropPuppet>();
            if (hold != null && hold.Hold && EnemySync.TryGetNetId(dr, out netId))
            {
                isEntity = true;
                return true;
            }
            return false;
        }

        // Pre-shield chokepoint: remote-simulated victims never take damage locally.
        [HarmonyPatch(typeof(DamagableResource), "TakeDamage", typeof(Damage))]
        internal static class RouteTakeDamage
        {
            private static bool Prefix(DamagableResource __instance, Damage __0, out DamageAuditState __state)
            {
                __state = CaptureAudit(__instance, __0);
                var profile = PatchProfiler.Enter(PatchId.DamageRouteSingle);
                try
                {
                    bool result = PrefixBody(__instance, __0);
                    __state.Applied = result;
                    return result;
                }
                finally { PatchProfiler.Exit(PatchId.DamageRouteSingle, profile); }
            }

            private static void Postfix(DamagableResource __instance, DamageAuditState __state)
                => CompleteAudit(__instance, __state);

            private static bool PrefixBody(DamagableResource __instance, Damage __0)
            {
                if (!NetSession.Active || _applyingRemote) return true;
                if (IsGodShieldedLocalShip(__instance)) return false; // dev sweep: audit-only hit
                if (!TryGetRemoteTarget(__instance, out bool isEntity, out byte slot, out int netId))
                {
                    NoteLocalDamage(__instance); // I simulate it and I'm hitting it → I'm the attacker
                    return true;
                }
                if (ProjectileSync.FriendlyExplosionBlocked(__instance)) return false; // FF off: my AoE spares teammates
                SendDamageRequest(isEntity, slot, netId, __0);
                UnitStatus.PlayDamageFlash(__instance); // instant local feedback; HP truth arrives later
                return false;
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "TakeDamage", typeof(IReadOnlyList<Damage>))]
        internal static class RouteTakeDamageList
        {
            private static bool Prefix(DamagableResource __instance, IReadOnlyList<Damage> __0,
                out DamageAuditState __state)
            {
                __state = CaptureAudit(__instance, __0);
                var profile = PatchProfiler.Enter(PatchId.DamageRouteList);
                try
                {
                    bool result = PrefixBody(__instance, __0);
                    __state.Applied = result;
                    if (result)
                    {
                        _takeDamageListDepth++;
                        __state.EnteredList = true;
                    }
                    return result;
                }
                finally { PatchProfiler.Exit(PatchId.DamageRouteList, profile); }
            }

            private static void Postfix(DamagableResource __instance, DamageAuditState __state)
            {
                if (__state.EnteredList && _takeDamageListDepth > 0) _takeDamageListDepth--;
                CompleteAudit(__instance, __state);
            }

            private static bool PrefixBody(DamagableResource __instance, IReadOnlyList<Damage> __0)
            {
                if (!NetSession.Active || _applyingRemote) return true;
                if (IsGodShieldedLocalShip(__instance)) return false; // dev sweep: audit-only hit
                if (!TryGetRemoteTarget(__instance, out bool isEntity, out byte slot, out int netId))
                {
                    NoteLocalDamage(__instance);
                    return true;
                }
                if (ProjectileSync.FriendlyExplosionBlocked(__instance)) return false; // FF off: my AoE spares teammates
                foreach (var damage in __0) SendDamageRequest(isEntity, slot, netId, damage);
                UnitStatus.PlayDamageFlash(__instance); // instant local feedback; HP truth arrives later
                return false;
            }
        }

        private static DamageAuditState CaptureAudit(DamagableResource dr, Damage damage)
        {
            var state = CaptureAuditBase(dr);
            if (!state.Track) return state;
            ReadDamage(damage, out state.Amount, out state.Type);
            return state;
        }

        private static DamageAuditState CaptureAudit(DamagableResource dr, IReadOnlyList<Damage> damages)
        {
            var state = CaptureAuditBase(dr);
            if (!state.Track || damages == null) return state;
            var names = new List<string>(damages.Count);
            foreach (var damage in damages)
            {
                ReadDamage(damage, out float amount, out string type);
                state.Amount += amount;
                if (!string.IsNullOrEmpty(type) && !names.Contains(type)) names.Add(type);
            }
            state.Type = names.Count == 0 ? "untyped" : string.Join("+", names);
            return state;
        }

        private static DamageAuditState CaptureAuditBase(DamagableResource dr)
        {
            if (_applyingRemote || _takeDamageListDepth > 0) return default;
            var local = ShipSync.LocalShip;
            if (local == null || dr == null || dr.GetComponentInParent<Ship>() != local) return default;
            var state = new DamageAuditState
            {
                Track = true,
                HpBefore = dr.CurrentHealth,
                ShieldBefore = UnitStatus.ReadShieldFraction(local),
                Type = "untyped",
            };
            state.HasTrace = ProjectileSync.TryGetCurrentDamageTrace(out state.Trace);
            return state;
        }

        private static void ReadDamage(Damage damage, out float amount, out string typeName)
        {
            amount = 0f;
            typeName = "untyped";
            try
            {
                amount = Traverse.Create(damage).Field("amount").GetValue<float>();
                var type = Traverse.Create(damage).Field("damageType").GetValue() as Resource;
                if (type != null) typeName = type.name;
            }
            catch { }
        }

        private static void CompleteAudit(DamagableResource dr, DamageAuditState state)
        {
            if (!state.Track) return;
            float hpAfter = dr != null ? dr.CurrentHealth : -1f;
            float shieldAfter = ShipSync.LocalShip != null ? UnitStatus.ReadShieldFraction(ShipSync.LocalShip) : -1f;
            string source = state.HasTrace
                ? (state.Trace.SourceNetId >= 0 ? $"entity#{state.Trace.SourceNetId}"
                    : state.Trace.SourceNetId == -1 ? $"player=P{state.Trace.SourceSlot + 1}" : "source=unknown")
                : "source=unknown";
            string identity = state.HasTrace
                ? $"shot={state.Trace.ShotId} pellet={state.Trace.ProjectileOrdinal} projectile={state.Trace.ProjectileInstanceId} kind={state.Trace.Kind} replayed={state.Trace.Replayed}"
                : "shot=unknown";
            Plugin.Log.LogInfo($"[CombatHit] {source} {identity} amount={state.Amount:0.###} type={state.Type} applied={state.Applied} hp={state.HpBefore:0.###}->{hpAfter:0.###} shield={state.ShieldBefore:0.###}->{shieldAfter:0.###}");
        }

        /// <summary>Real damage to an entity we simulate: only the LOCAL player's weapons deal
        /// real local damage (teammates route theirs as DamageRequests), so the local player is
        /// the attacker. Recorded so a death credits the killer for loot.</summary>
        private static void NoteLocalDamage(DamagableResource dr)
        {
            var session = NetSession.Instance;
            if (session == null) return;
            if (EnemySync.TryGetNetId(dr, out int netId)) LastDamager[netId] = (byte)session.LocalSlot;
        }

        // World-sourced contact damage (cells, hazards, electricity, rams) fires from local physics
        // on BOTH simulations of an entity. Only the victim's authority may apply it; the local
        // duplicate against a remote-simulated victim is dropped before it reaches TakeDamage.
        [HarmonyPatch]
        internal static class DropWorldDamageOnRemoteVictims
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var name in new[] { "OnCellCollision", "OnHazardTouched", "OnHitByElectricity" })
                {
                    var m = AccessTools.Method(typeof(HealthBase), name);
                    if (m != null) yield return m;
                }
            }

            private static bool Prefix(HealthBase __instance)
            {
                var profile = PatchProfiler.Enter(PatchId.DamageDropWorldRemote);
                try { return PrefixBody(__instance); }
                finally { PatchProfiler.Exit(PatchId.DamageDropWorldRemote, profile); }
            }

            private static bool PrefixBody(HealthBase __instance)
            {
                if (!NetSession.Active) return true;
                if (IsGodShieldedLocalShip(__instance)) return false; // dev sweep shield
                if (__instance.GetComponent<RemotePuppet>() != null
                    || __instance.GetComponent<RemoteEntityPuppet>() != null) return false;
                var hold = __instance.GetComponent<PropPuppet>();
                return hold == null || !hold.Hold;
            }
        }

        // Final chokepoint (burn ticks, direct calls): puppets' HP is written only by snapshots.
        [HarmonyPatch(typeof(DamagableResource), "Damage", typeof(float))]
        internal static class BlockPuppetDamage
        {
            private static bool Prefix(DamagableResource __instance)
            {
                if (!NetSession.Active || _applyingRemote) return true;
                if (IsGodShieldedLocalShip(__instance)) return false;
                if (__instance.GetComponent<RemotePuppet>() != null
                    || __instance.GetComponent<RemoteEntityPuppet>() != null) return false;
                var hold = __instance.GetComponent<PropPuppet>();
                return hold == null || !hold.Hold;
            }
        }

        /// <summary>Dev-sweep god shield: blocks damage to the LOCAL ship only, and only while
        /// the `god` dev command armed it. Sits after the audit capture, so every blocked hit
        /// still logs [CombatHit] applied=False with full source attribution.</summary>
        private static bool IsGodShieldedLocalShip(Component victim)
        {
            if (!Core.DevTools.GodMode) return false;
            var local = ShipSync.LocalShip;
            return local != null && victim != null && victim.GetComponentInParent<Ship>() == local;
        }

        private static void SendDamageRequest(bool isEntity, byte targetSlot, int targetNetId, Damage damage)
        {
            var session = NetSession.Instance;
            float amount;
            Resource type;
            try
            {
                amount = Traverse.Create(damage).Field("amount").GetValue<float>();
                type = Traverse.Create(damage).Field("damageType").GetValue() as Resource;
            }
            catch
            {
                return;
            }
            if (amount <= 0) return;
            // Host shooting a client-simulated entity never re-enters Dispatch — mark here.
            if (isEntity && session.IsHost) Core.AuthorityManager.NoteCombat(targetNetId);
            bool hasTrace = ProjectileSync.TryGetCurrentDamageTrace(out var trace);
            var msg = new DamageRequestMsg
            {
                IsEntity = isEntity,
                TargetSlot = targetSlot,
                TargetNetId = targetNetId,
                TargetLifetime = isEntity ? Core.NetIds.LifetimeOf(targetNetId) : 0,
                Amount = amount,
                TypeHash = type != null ? HashName(type.name) : 0,
                AttackerSlot = (byte)session.LocalSlot,
                RequestId = NextDamageRequestId((byte)session.LocalSlot),
                ShotId = hasTrace ? trace.ShotId : 0,
                ProjectileOrdinal = hasTrace ? trace.ProjectileOrdinal : (ushort)0,
            };
            Writer.Reset();
            msg.Write(Writer);
            session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
            // The host never routes its own broadcast back to itself — a host attacker hitting
            // a dormant entity must queue the claim locally or it evaporates.
            if (session.IsHost && isEntity
                && Core.AuthorityManager.OwnerOf(targetNetId) == Core.AuthorityManager.DormantOwner)
                QueueDormantClaim(msg);
        }

        private static uint NextDamageRequestId(byte slot)
        {
            _requestSequence = (_requestSequence + 1u) & 0x00FFFFFFu;
            if (_requestSequence == 0) _requestSequence = 1;
            return ((uint)slot << 24) | _requestSequence;
        }

        // ---------------------------------------------------------------- application (on owner)

        public static void ApplyDamageRequest(DamageRequestMsg msg)
            => ApplyDamageRequest(msg, false);

        private static void ApplyDamageRequest(DamageRequestMsg msg, bool pendingReplay)
        {
            var session = NetSession.Instance;
            // Replayed claims were already recorded as "seen" on every peer when the original
            // broadcast passed through — running them through the dedup again would drop every
            // dormant-claim replay to a remote owner on arrival.
            if (!pendingReplay && !msg.Replay && !AcceptDamageRequest(msg)) return;
            DamagableResource dr = null;
            if (msg.IsEntity)
            {
                if (!Core.NetIds.LifetimeMatches(msg.TargetNetId, msg.TargetLifetime))
                {
                    InstrumentationCounters.StaleLifetimeDropped();
                    return;
                }
                // OwnerOf defaults unassigned entities to the current host's slot, so this
                // single check covers both assigned and host-fallback ownership.
                if (!EnemySync.IsLocallyOwned(msg.TargetNetId)) return;
                LastDamager[msg.TargetNetId] = msg.AttackerSlot; // credit the teammate who fired
                bool spawnedHere = false;
                if (Core.NetIds.TryGetInstanceId(msg.TargetNetId, out int instanceId))
                {
                    try
                    {
                        var egm = ServiceLocator.Get<EntityGameObjectManager>();
                        if (egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                        {
                            spawnedHere = true;
                            // Root first, then children: some entities (Unit_Hiver) keep their
                            // DamagableResource on a sub-part — the root-only lookup made them
                            // permanently immune to routed teammate damage.
                            dr = se.GetComponent<DamagableResource>();
                            if (dr == null) dr = se.GetComponentInChildren<DamagableResource>(true);
                        }
                    }
                    catch { }
                }
                if (!spawnedHere)
                {
                    // Dormant: ours, but not streamed in here. Dropping the request makes the
                    // entity unbreakable on the attacker's machine — hand it to them instead
                    // (they have it spawned; their shot just landed on it).
                    if (session.IsHost)
                    {
                        QueueDormantClaim(msg);
                    }
                    else
                    {
                        // A client owner used to silently discard these — the attacker's target
                        // became unbreakable for the whole session (the split-brain symptom).
                        // Bounce the full claim to the host, which queues it and forces the
                        // segment lease toward the attacker exactly as it does for its own
                        // dormant entities.
                        Writer.Reset();
                        msg.Write(Writer, MsgType.DamageUnservable);
                        session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
                        Core.InstrumentationCounters.DormantDamageForwarded();
                    }
                    return;
                }
            }
            else
            {
                if (msg.TargetSlot != session.LocalSlot) return; // host routing delivers only ours here
                dr = ShipSync.LocalShip != null ? ShipSync.LocalShip.GetComponent<DamagableResource>() : null;
            }
            if (dr == null) return;

            _applyingRemote = true;
            float hpBefore = dr.CurrentHealth;
            float shieldBefore = !msg.IsEntity && ShipSync.LocalShip != null
                ? UnitStatus.ReadShieldFraction(ShipSync.LocalShip) : -1f;
            try
            {
                var type = ResolveType(msg.TypeHash);
                if (type != null)
                    dr.TakeDamage(new Damage(msg.Amount, type)); // full pipeline: shields, matrix, i-frames
                else
                    dr.Damage(msg.Amount); // untyped fallback
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Damage] apply failed: {e.Message}");
            }
            finally
            {
                _applyingRemote = false;
                if (!msg.IsEntity)
                {
                    float shieldAfter = ShipSync.LocalShip != null ? UnitStatus.ReadShieldFraction(ShipSync.LocalShip) : -1f;
                    Plugin.Log.LogInfo($"[CombatHit] remote-request={msg.RequestId} attacker=P{msg.AttackerSlot + 1} shot={msg.ShotId} pellet={msg.ProjectileOrdinal} amount={msg.Amount:0.###} typeHash={msg.TypeHash:X8} applied=True hp={hpBefore:0.###}->{dr.CurrentHealth:0.###} shield={shieldBefore:0.###}->{shieldAfter:0.###}");
                }
            }
        }

        private static bool AcceptDamageRequest(DamageRequestMsg msg)
        {
            ulong key = ((ulong)msg.AttackerSlot << 56) | msg.RequestId;
            if (!SeenDamageRequests.Add(key))
            {
                InstrumentationCounters.DamageRequestDeduped();
                if (NetDiag.Enabled) NetDiag.Throttled($"damagereq{key}", 1f, "Damage",
                    () => $"dropped duplicate damage request {msg.RequestId} from P{msg.AttackerSlot + 1}");
                return false;
            }
            SeenDamageRequestOrder.Enqueue(key);
            if (SeenDamageRequestOrder.Count > RecentDamageRequestLimit)
                SeenDamageRequests.Remove(SeenDamageRequestOrder.Dequeue());
            return true;
        }

        /// <summary>Host: a damage claim targets an entity nobody currently simulates (dormant
        /// owner, or an owner that reported it unservable). Queue it and force the segment
        /// toward the attacker — the claim replays to whichever simulator the commit lands on.</summary>
        public static void QueueDormantClaim(DamageRequestMsg msg)
        {
            var session = NetSession.Instance;
            if (session == null || !session.IsHost || !msg.IsEntity) return;
            if (!Core.NetIds.LifetimeMatches(msg.TargetNetId, msg.TargetLifetime))
            {
                Core.InstrumentationCounters.StaleLifetimeDropped();
                return;
            }
            PendingDormantDamage.Add((msg, UnityEngine.Time.unscaledTime));
            Core.InstrumentationCounters.DormantDamageQueued();
            // Loud on purpose: dormantDamage has read 0/0 across sessions where players report
            // fighting frozen "mannequins" — this line proves whether hits on dormant entities
            // actually reach the claim path (and OnDormantHit wakes them for the attacker).
            Plugin.Log.LogInfo($"[Damage] dormant hit on #{msg.TargetNetId} — claiming its segment for P{msg.AttackerSlot + 1}");
            Core.AuthorityManager.OnDormantHit(msg.TargetNetId, msg.AttackerSlot);
            // The segment claim alone died in the field (dormantDamage=16 queued / 0 replayed,
            // 12 TTL drops in one playtest): the forced grant NACKs whenever the host's
            // canonical position maps the target to a segment the attacker isn't streaming.
            // The attacker's possession is per-ENTITY proof, so wake per entity as well —
            // whichever path commits first drains the claim.
            EnemySync.ApplyStarvedOwnershipRequest(msg.TargetNetId, msg.AttackerSlot, session, wake: true);
        }

        /// <summary>Host: a per-entity authority assignment just committed — drain any queued
        /// dormant claims for that entity to its new simulator (segment commits have their own
        /// drain; per-entity promotions never match a segment key).</summary>
        internal static void OnEntityAssigned(int netId, byte owner, NetSession session)
        {
            if (session == null || !session.IsHost || PendingDormantDamage.Count == 0) return;
            ulong peer = 0;
            foreach (var p in session.Players)
                if (p != null && p.Connected && p.Slot == owner) { peer = p.PeerId; break; }
            for (int i = 0; i < PendingDormantDamage.Count;)
            {
                var msg = PendingDormantDamage[i].msg;
                if (msg.TargetNetId != netId) { i++; continue; }
                PendingDormantDamage.RemoveAt(i);
                if (owner == session.LocalSlot) ApplyDamageRequest(msg, true);
                else if (peer != 0)
                {
                    msg.Replay = true; // every peer already saw the original RequestId
                    Writer.Reset(); msg.Write(Writer);
                    session.SendToPeer(peer, NetChannel.Combat, Writer.ToSegment(), reliable: true);
                }
                Core.InstrumentationCounters.DormantDamageReplayed();
            }
        }

        /// <summary>Host: an owner reported it cannot serve a damage claim (no live object).
        /// Queue it like host-side dormant damage and force the segment toward the attacker —
        /// the claim replays to whichever simulator the commit lands on.</summary>
        public static void ApplyDamageUnservable(DamageRequestMsg msg, byte senderSlot, NetSession session)
        {
            if (!session.IsHost || !msg.IsEntity) return;
            if (!Core.NetIds.LifetimeMatches(msg.TargetNetId, msg.TargetLifetime))
            {
                Core.InstrumentationCounters.StaleLifetimeDropped();
                return;
            }
            byte owner = EnemySync.OwnerOf(msg.TargetNetId);
            if (owner == session.LocalSlot)
            {
                // Ownership already moved to the host while the bounce was in flight.
                msg.Replay = true;
                ApplyDamageRequest(msg, true);
                return;
            }
            if (owner == Core.AuthorityManager.DormantOwner || owner == senderSlot)
            {
                QueueDormantClaim(msg);
                return;
            }
            // Moved to a third peer mid-flight — re-route the claim to the current owner.
            msg.Replay = true;
            Writer.Reset(); msg.Write(Writer);
            session.SendReliableToSlot(owner, NetChannel.Combat, Writer.ToSegment());
            Core.InstrumentationCounters.DormantDamageReplayed();
        }

        internal static void OnSegmentAuthorityCommitted(AuthorityManager.SegmentKey segment, byte owner)
        {
            var session = NetSession.Instance;
            if (session == null || !session.IsHost || PendingDormantDamage.Count == 0) return;
            ulong peer = 0;
            foreach (var p in session.Players)
                if (p != null && p.Connected && p.Slot == owner) { peer = p.PeerId; break; }
            for (int i = 0; i < PendingDormantDamage.Count;)
            {
                var msg = PendingDormantDamage[i].msg;
                if (!AuthorityManager.TrySegmentOf(msg.TargetNetId, out var key) || !key.Equals(segment)) { i++; continue; }
                PendingDormantDamage.RemoveAt(i);
                if (owner == session.LocalSlot) ApplyDamageRequest(msg, true);
                else if (peer != 0)
                {
                    msg.Replay = true; // every peer already saw the original RequestId (see Replay)
                    Writer.Reset(); msg.Write(Writer);
                    session.SendToPeer(peer, NetChannel.Combat, Writer.ToSegment(), reliable: true);
                }
                Core.InstrumentationCounters.DormantDamageReplayed();
            }
        }

        private static void PruneDormantClaims()
        {
            float now = UnityEngine.Time.unscaledTime;
            for (int i = PendingDormantDamage.Count - 1; i >= 0; i--)
            {
                if (now - PendingDormantDamage[i].queuedAt <= DormantClaimTtl) continue;
                var expired = PendingDormantDamage[i].msg;
                PendingDormantDamage.RemoveAt(i);
                Core.InstrumentationCounters.DormantClaimDropped();
                if (Core.NetDiag.Enabled)
                    Core.NetDiag.Log("Damage", $"dropped dormant claim for {Core.NetDiag.Describe(expired.TargetNetId)} " +
                        $"from P{expired.AttackerSlot + 1} — no simulator materialized within {DormantClaimTtl:0}s");
            }
        }

        // Puppets may only resurrect via their owner's SHIP_RESURRECTED event — vanilla systems
        // (e.g. the station-unlock respawn) must not revive them locally.
        [HarmonyPatch(typeof(Ship), "Resurrect")]
        internal static class GatePuppetResurrect
        {
            private static bool Prefix(Ship __instance)
            {
                if (!NetSession.Active || _applyingRemote) return true;
                return __instance.GetComponent<RemotePuppet>() == null;
            }
        }

        // ---------------------------------------------------------------- death / resurrection

        // Organic deaths never call Ship.Die() — the chain is DamagableResource.Die ->
        // onDeath (UnityEvent) -> Ship.OnDeath. Hooking OnDeath catches both organic and
        // scripted deaths; without it a damage death was never broadcast and the victim's
        // puppet stayed visible, frozen, on everyone else's screen.
        [HarmonyPatch(typeof(Ship), "OnDeath")]
        internal static class BroadcastDeath
        {
            private static void Postfix(Ship __instance)
            {
                SendLifeEvent(__instance, died: true);
            }
        }

        [HarmonyPatch(typeof(Ship), "Resurrect")]
        internal static class BroadcastResurrect
        {
            private static void Postfix(Ship __instance)
            {
                SendLifeEvent(__instance, died: false);
            }
        }

        private static void SendLifeEvent(Ship ship, bool died)
        {
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame || _applyingRemote) return;
            if (ship != ShipSync.LocalShip) return; // only our own ship's fate is ours to announce
            _announcedDead = died;
            Writer.Reset();
            new ShipLifeMsg { Slot = (byte)session.LocalSlot }.Write(Writer, died);
            session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
            if (died) NetStats.AddDeath(session.LocalSlot);
            Plugin.Log.LogInfo($"[Damage] local ship {(died ? "died" : "resurrected")} — broadcast");
        }

        // ---------------------------------------------------------------- life watchdog

        private static bool _announcedDead;
        private static float _nextLifeCheckAt;

        public static void ResetLifeWatchdog() => _announcedDead = false;

        /// <summary>Self-healing life-state reconciliation, called every frame from NetSession
        /// while InGame. The event hooks (Ship.OnDeath / Resurrect) miss deaths in two ways:
        /// a death that lands INSIDE an applied remote event (_applyingRemote swallows the
        /// broadcast — e.g. chain-killed by a teammate-puppet's explosion) and any vanilla death
        /// path that skips OnDeath. Observed live: the client died, no broadcast, and the host
        /// spectated a frozen "alive" prop forever while the run couldn't end. Instead of hoping
        /// every path is hooked, compare the ship's actual IsDead against what we last announced
        /// and re-announce on mismatch.</summary>
        public static void TickLifeWatchdog()
        {
            if (Time.unscaledTime < _nextLifeCheckAt) return;
            _nextLifeCheckAt = Time.unscaledTime + 0.5f;
            if (PendingDormantDamage.Count > 0) PruneDormantClaims();
            var ship = ShipSync.LocalShip;
            if (ship == null) return;
            bool dead;
            try { dead = ship.IsDead; } catch { return; }
            if (dead == _announcedDead) return;
            Plugin.Log.LogInfo($"[Damage] life watchdog: IsDead={dead} but announced={_announcedDead} — re-announcing");
            SendLifeEvent(ship, dead);
        }

        public static void ApplyLifeEvent(ShipLifeMsg msg, bool died)
        {
            if (!ShipSync.ShipsBySlot.TryGetValue(msg.Slot, out var ship) || ship == null) return;
            if (ship.GetComponent<RemotePuppet>() == null) return; // our own echo
            if (died) NetStats.AddDeath(msg.Slot);
            _applyingRemote = true;
            try
            {
                if (died)
                {
                    // Ship.Die routes through DamagableResource.Damage, which i-frames or
                    // invincibility can silently swallow on a puppet — the ship then never
                    // deactivates and its station keeps the shop pose. DamagableResource.Die
                    // is unconditional (same approach the enemy kill path uses).
                    var dr = ship.GetComponent<DamagableResource>();
                    if (dr != null && !dr.IsDead) dr.Die();
                    else ship.Die();
                    RemotePuppet.ScrubInteractions(ship);
                }
                else
                {
                    ship.Resurrect();
                }
                Plugin.Log.LogInfo($"[Damage] applied remote {(died ? "death" : "resurrect")} for slot {msg.Slot}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Damage] life event failed: {e.Message}");
            }
            finally
            {
                _applyingRemote = false;
            }
        }
    }
}
