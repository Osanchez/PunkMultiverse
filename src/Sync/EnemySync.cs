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
    /// simulates each entity (its "owner"; slot 0 / host by default), streams ENTITY_STATE at
    /// EntityStateHz for spawned entities, and announces kills. Everyone else runs the entity as a
    /// muted RemoteEntityPuppet. Ownership changes arrive as AUTH_ASSIGN batches from the host's
    /// AuthorityManager and are applied idempotently here.
    /// </summary>
    internal static class EnemySync
    {
        /// <summary>netId -> owner slot. Missing key = host (slot 0).</summary>
        public static readonly Dictionary<int, byte> Owners = new Dictionary<int, byte>();
        /// <summary>Runtime ids exempt from proximity handoff (player minions).</summary>
        public static readonly HashSet<int> FixedOwners = new HashSet<int>();
        private static readonly HashSet<int> KilledNetIds = new HashSet<int>();
        private static readonly Dictionary<int, Vector2> LastSentPos = new Dictionary<int, Vector2>();
        private static readonly NetWriter Writer = new NetWriter(2048);
        private static float _nextSendAt;
        private static bool _applyingRemote;

        public static void Reset()
        {
            Owners.Clear();
            FixedOwners.Clear();
            KilledNetIds.Clear();
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
            _loggedFirstAssign = false;
            _loggedFirstState = false;
        }

        /// <summary>netId -> owner slot; unassigned entities belong to the CURRENT host
        /// (which is not necessarily slot 0 after a host migration).</summary>
        public static byte OwnerOf(int netId)
        {
            if (Owners.TryGetValue(netId, out var slot)) return slot;
            var session = NetSession.Instance;
            return session != null ? session.HostSlot : (byte)0;
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
            var egm = ServiceLocator.Get<EntityGameObjectManager>();
            if (!egm.TryGetSavableEntity(instanceId, out var se) || se == null) return;
            if (se.GetComponent<Unit>() == null) return; // static prop — no authority needed

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
            if (!NetIds.ManifestComplete || Time.unscaledTime < _nextSendAt) return;
            _nextSendAt = Time.unscaledTime + 1f / Mathf.Max(1f, NetConfig.StateHz.Value);

            var egm = TryGetEgm();
            if (egm == null) return;

            var entries = new List<EntityStateEntry>(32);
            foreach (var kv in Owners)
            {
                if (kv.Value != session.LocalSlot || KilledNetIds.Contains(kv.Key)) continue;
                // Authority follows distance but simulation needs the GameObject — segment
                // streaming can unload an entity we still own. Owning what we can't simulate
                // starves everyone else's puppets (frozen, brainless enemies): hand it back.
                if (!IsSpawnedHere(egm, kv.Key))
                {
                    MaybeReleaseAuthority(session, kv.Key);
                    continue;
                }
                CollectEntry(egm, kv.Key, entries);
            }
            // Host also owns every un-assigned entity; it only streams the spawned ones.
            if (session.IsHost)
            {
                foreach (var netId in HostSpawnedUnassigned(egm))
                {
                    // The cached scan can be up to a refresh stale — re-check cheaply.
                    if (Owners.ContainsKey(netId) || KilledNetIds.Contains(netId)) continue;
                    CollectEntry(egm, netId, entries);
                }
            }

            byte slot = (byte)session.LocalSlot;
            uint timeMs = (uint)(Time.unscaledTime * 1000f);
            for (int start = 0; start < entries.Count; start += 32)
            {
                int count = Math.Min(32, entries.Count - start);
                Writer.Reset();
                new EntityStateMsg { Slot = slot, TimeMs = timeMs, Entries = entries.GetRange(start, count) }.Write(Writer);
                session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
            }
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

        private static List<int> HostSpawnedUnassigned(EntityGameObjectManager egm)
        {
            // FindObjectsByType is a whole-scene walk — at the 20 Hz state rate it was the
            // single hottest line on the host. Refresh the candidate list on the authority
            // cadence instead; the send loop re-checks Owners/KilledNetIds per use.
            if (Time.unscaledTime < _nextHostScanAt) return _hostScratch;
            _nextHostScanAt = Time.unscaledTime + HostScanInterval;

            _hostScratch.Clear();
            // Entities near the host stream in on the host; those without an explicit owner are ours.
            foreach (var unit in UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsSortMode.None))
            {
                if (unit.GetComponent<RemotePuppet>() != null) continue;         // player ships handled by ShipSync
                if (unit.GetComponent<Ship>() != null) continue;
                if (!TryGetNetId(unit, out int netId)) continue;
                if (Owners.ContainsKey(netId) || KilledNetIds.Contains(netId)) continue;
                _hostScratch.Add(netId);
            }
            return _hostScratch;
        }

        private static void CollectEntry(EntityGameObjectManager egm, int netId, List<EntityStateEntry> entries)
        {
            if (!NetIds.TryGetInstanceId(netId, out int instanceId)) return;
            if (!egm.TryGetSavableEntity(instanceId, out var se) || se == null) return;
            if (se.GetComponent<RemoteEntityPuppet>() != null) return; // stale ownership — not actually ours
            var rb = se.GetComponent<Rigidbody2D>();
            var dr = se.GetComponent<DamagableResource>();
            Vector2 pos = rb != null ? rb.position : (Vector2)se.transform.position;
            // Non-Unit props (pushable rocks etc.) only stream while actually moving.
            if (se.GetComponent<Unit>() == null)
            {
                if (rb == null) return;
                if (LastSentPos.TryGetValue(netId, out var last) && Vector2.Distance(last, pos) < 0.05f) return;
            }
            LastSentPos[netId] = pos;
            float hp = 1f;
            try { if (dr != null && dr.MaxHealth > 0) hp = dr.CurrentHealth / dr.MaxHealth; } catch { }
            entries.Add(new EntityStateEntry
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
            });
        }

        // netId -> (authority, its clock) of the newest applied snapshot. The state channel is
        // unreliable AND unordered; late packets must not yank puppets backwards. A different
        // sender means an authority handoff — clocks aren't comparable, accept and re-baseline.
        private static readonly Dictionary<int, (byte slot, uint ms)> LastEntityStateMs
            = new Dictionary<int, (byte, uint)>();

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
            foreach (var e in msg.Entries)
            {
                if (IsLocallyOwned(e.NetId))
                {
                    // Normally my own relayed echo. But a state for an entity I own arriving from
                    // a DIFFERENT slot means another machine is simulating it too — dual authority,
                    // the source of rubber-banding and double-fire. Surface it.
                    if (NetDiag.Enabled && session != null && msg.Slot != session.LocalSlot)
                        NetDiag.Throttled($"dual{e.NetId}", 2f, "Dual",
                            () => $"{NetDiag.Describe(e.NetId)} — I own it ({NetDiag.Owner((byte)session.LocalSlot)}) but {NetDiag.Owner(msg.Slot)} is ALSO streaming it (DUAL AUTHORITY)");
                    continue;
                }
                if (KilledNetIds.Contains(e.NetId)) continue; // dead here — don't animate a corpse
                if (LastEntityStateMs.TryGetValue(e.NetId, out var last))
                {
                    // Sender changed = authority handed off. The puppet re-baselines onto the new
                    // owner's timeline (accepted below) — a visible snap if their positions differ.
                    if (NetDiag.Enabled && last.slot != msg.Slot)
                        NetDiag.Log("State", $"{NetDiag.Describe(e.NetId)} authority changed {NetDiag.Owner(last.slot)} -> {NetDiag.Owner(msg.Slot)} (puppet re-baselines — expect a visual snap)");
                    if (last.slot == msg.Slot && (int)(msg.TimeMs - last.ms) <= 0) continue;
                }
                LastEntityStateMs[e.NetId] = (msg.Slot, msg.TimeMs);
                if (!NetIds.TryGetInstanceId(e.NetId, out int instanceId)) continue;

                // Keep the data-side position fresh so stream-in spawns at the right spot.
                try
                {
                    var data = em?.GetEntity(instanceId);
                    if (data != null) data.position = new Vector3(e.Pos.x, e.Pos.y, data.position.z);
                }
                catch { }

                if (egm != null && egm.TryGetSavableEntity(instanceId, out var se) && se != null)
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
            private static void Prefix(DestroyWhenResourceDrained __instance)
            {
                if (!NetSession.Active) return;
                try
                {
                    var unit = Traverse.Create(__instance).Field("unit").GetValue() as Unit;
                    var resource = Traverse.Create(__instance).Field("resource").GetValue() as Resource;
                    if (unit == null || resource == null) return;
                    bool hasTank = Traverse.Create(unit).Method("HasTank", resource).GetValue<bool>();
                    if (!hasTank) return;
                    float amount = Traverse.Create(unit).Method("GetResource", resource).GetValue<float>();
                    if (amount <= 0f) SyncDestroy(unit);
                }
                catch { }
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

        private static bool _warnedDataDestroy;

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
                    var dr = se.GetComponent<DamagableResource>();
                    if (dr != null) { dr.Die(); return; }
                    var health = se.GetComponent<Health>();
                    if (health != null)
                    {
                        AccessTools.Method(typeof(Health), "Die")?.Invoke(health, null);
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
                Plugin.Log.LogWarning($"[Enemies] kill apply failed for netId {netId}: {e.Message}");
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
                if (!KilledNetIds.Add(netId)) return;
                Writer.Reset();
                new EntityKilledMsg { NetId = netId, KillerSlot = KillerOf(netId, session) }.Write(Writer);
                session.SendToAll(NetChannel.Combat, Writer.ToSegment(), reliable: true);
            }
        }

        public static bool SuppressLocalDeathEffects => _applyingRemote;
    }
}
