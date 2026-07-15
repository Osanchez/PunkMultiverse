using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>Identity attached at projectile spawn. ShotId identifies the burst and Ordinal the
    /// projectile/pellet within it, allowing impact dedupe without collapsing legitimate pellets.</summary>
    internal sealed class NetworkProjectileIdentity : MonoBehaviour
    {
        internal int SourceNetId;
        internal byte SourceSlot;
        internal uint ShotId;
        internal ushort Ordinal;
        internal bool Replayed;
    }

    /// <summary>
    /// Weapon-fire replication. The local player's shots are captured at WeaponBase.DoShoot (one
    /// call per actual burst tick, sync — never patch the async Fire) and replayed on peers through
    /// the puppet ship's own weapon via a FakeBarrel. Replayed projectiles are VISUAL ONLY: real
    /// damage happens exactly once, on the simulating owner's machine (routed by DamageSync).
    /// </summary>
    internal static class ProjectileSync
    {
        private static readonly HashSet<int> VisualProjectiles = new HashSet<int>();
        internal static int VisualProjectileCount => VisualProjectiles.Count;
        private static int _replayDepth;
        private static readonly NetWriter Writer = new NetWriter(64);
        private static uint _shotSequence;
        private sealed class PendingShipFire
        {
            internal FireEventMsg Msg;
            internal float PlayAt;
        }
        private static readonly List<PendingShipFire> PendingShipFires = new List<PendingShipFire>();
        private static long _shipFireQueued, _shipFireLate;
        private static double _muzzleCorrectionTotal;
        private static float _muzzleCorrectionMax;
        internal static int PendingShipFireCount => PendingShipFires.Count;
        internal static long ShipFireQueued => _shipFireQueued;
        internal static long ShipFireLate => _shipFireLate;
        internal static double MuzzleCorrectionAverage => _shipFireQueued + _shipFireLate > 0
            ? _muzzleCorrectionTotal / (_shipFireQueued + _shipFireLate) : 0.0;
        internal static float MuzzleCorrectionMax => _muzzleCorrectionMax;

        internal readonly struct DamageTrace
        {
            internal readonly int SourceNetId;
            internal readonly byte SourceSlot;
            internal readonly uint ShotId;
            internal readonly ushort ProjectileOrdinal;
            internal readonly int ProjectileInstanceId;
            internal readonly bool Replayed;
            internal readonly string Kind;

            internal DamageTrace(int sourceNetId, byte sourceSlot, uint shotId, ushort projectileOrdinal,
                int projectileInstanceId, bool replayed, string kind)
            {
                SourceNetId = sourceNetId;
                SourceSlot = sourceSlot;
                ShotId = shotId;
                ProjectileOrdinal = projectileOrdinal;
                ProjectileInstanceId = projectileInstanceId;
                Replayed = replayed;
                Kind = kind;
            }
        }

        private sealed class ShotContext
        {
            internal int SourceNetId;
            internal byte SourceSlot;
            internal uint ShotId;
            internal bool Replayed;
            internal ushort NextOrdinal;
        }

        private readonly struct ImpactKey : System.IEquatable<ImpactKey>
        {
            private readonly int _sourceNetId, _projectileInstance, _victimInstance;
            private readonly uint _shotId;
            private readonly ushort _ordinal;
            internal ImpactKey(int sourceNetId, uint shotId, ushort ordinal, int projectileInstance, int victimInstance)
            {
                _sourceNetId = sourceNetId; _shotId = shotId; _ordinal = ordinal;
                _projectileInstance = projectileInstance; _victimInstance = victimInstance;
            }
            public bool Equals(ImpactKey other) => _sourceNetId == other._sourceNetId
                && _shotId == other._shotId && _ordinal == other._ordinal
                && _projectileInstance == other._projectileInstance && _victimInstance == other._victimInstance;
            public override bool Equals(object obj) => obj is ImpactKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _sourceNetId;
                    h = h * 397 ^ (int)_shotId;
                    h = h * 397 ^ _ordinal;
                    h = h * 397 ^ _projectileInstance;
                    return h * 397 ^ _victimInstance;
                }
            }
        }

        private static ShotContext _currentShot;
        private static DamageTrace? _currentDamageTrace;
        private static readonly HashSet<ulong> SeenFireEvents = new HashSet<ulong>();
        private static readonly Queue<ulong> SeenFireOrder = new Queue<ulong>();
        private static readonly HashSet<ImpactKey> SeenImpacts = new HashSet<ImpactKey>();
        private static readonly Queue<ImpactKey> SeenImpactOrder = new Queue<ImpactKey>();
        private const int RecentIdentityLimit = 8192;

        // weapon -> owning net entity, for enemy/boss fire capture (enemy weapons are stable
        // per instance, unlike ship weapons which rebuild on grid refresh).
        private static readonly Dictionary<WeaponBase, (SavableEntity se, int netId)> EntityWeapons
            = new Dictionary<WeaponBase, (SavableEntity, int)>();

        public static bool IsReplaying => _replayDepth > 0;

        public static void Reset()
        {
            VisualProjectiles.Clear();
            EntityWeapons.Clear();
            _replayDepth = 0;
            _shotSequence = 0;
            _currentShot = null;
            _currentDamageTrace = null;
            SeenFireEvents.Clear();
            SeenFireOrder.Clear();
            SeenImpacts.Clear();
            SeenImpactOrder.Clear();
            PendingShipFires.Clear();
            _shipFireQueued = 0;
            _shipFireLate = 0;
            _muzzleCorrectionTotal = 0;
            _muzzleCorrectionMax = 0;
            _warnedEntityReplay = false;
            _warnedShipReplay = false;
        }

        // Per-shot failure paths log once as warning, then debug — a repeating failure at
        // enemy fire rates becomes a disk-log storm that reads as an FPS bug.
        private static bool _warnedEntityReplay;
        private static bool _warnedShipReplay;
        private static float _nextNullHolderWarnAt;

        // ---------------------------------------------------------------- capture

        // Spread, angle variance, and projectile noise all roll UnityEngine.Random inside the
        // DoShoot call tree. The shooter seeds the RNG deterministically per burst tick (and
        // restores the game's stream after), sends the seed with the fire event, and replays
        // seed identically — so every client sees the exact same pellet pattern.
        private static int _fireSeedCounter;
        private static int _lastFireSeed;

        private struct FirePatchState
        {
            internal bool Seeded;
            internal UnityEngine.Random.State RandomState;
            internal ShotContext PreviousShot;
            internal bool InstalledShot;
            internal bool Suppressed;
            internal int Holder;
            internal SavableEntity Entity;
            internal int NetId;
            internal uint ShotId;
        }

        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        internal static class CaptureFire
        {
            private static bool Prefix(WeaponBase __instance, out FirePatchState __state)
            {
                var profile = PatchProfiler.Enter(PatchId.ProjectileCaptureFirePrefix);
                try { return PrefixBody(__instance, out __state); }
                finally { PatchProfiler.Exit(PatchId.ProjectileCaptureFirePrefix, profile); }
            }

            private static bool PrefixBody(WeaponBase weapon, out FirePatchState state)
            {
                state = new FirePatchState { Holder = -1, PreviousShot = _currentShot };
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _replayDepth > 0)
                    return true;

                state.Holder = FindLocalHolder(weapon);
                if (state.Holder < 0)
                {
                    if (!EntityWeapons.TryGetValue(weapon, out var owner))
                    {
                        owner = ResolveEntityWeapon(weapon);
                        EntityWeapons[weapon] = owner;
                    }
                    state.Entity = owner.se;
                    state.NetId = owner.netId;
                    if (owner.se != null && !EnemySync.CanSimulate(owner.se, owner.netId))
                    {
                        state.Suppressed = true;
                        InstrumentationCounters.DuplicateFireDropped();
                        if (NetDiag.Enabled) NetDiag.Throttled($"blockedfire{owner.se.GetInstanceID()}", 1f, "Fire",
                            () => $"blocked non-canonical/non-owner fire from {NetDiag.Describe(owner.netId)} object={owner.se.GetInstanceID()}");
                        return false;
                    }
                }

                state.Seeded = true;
                state.RandomState = UnityEngine.Random.state;
                _fireSeedCounter = unchecked(_fireSeedCounter * 486187739 + 1);
                _lastFireSeed = unchecked(_fireSeedCounter ^ (session.LocalSlot << 20) ^ session.CurrentRunSeed);
                UnityEngine.Random.InitState(_lastFireSeed);
                if (state.Holder >= 0 || state.Entity != null)
                {
                    state.ShotId = NextShotId((byte)session.LocalSlot);
                    _currentShot = new ShotContext
                    {
                        SourceNetId = state.Entity != null ? state.NetId : -1,
                        SourceSlot = (byte)session.LocalSlot,
                        ShotId = state.ShotId,
                        Replayed = false,
                    };
                    state.InstalledShot = true;
                }
                return true;
            }

            private static void Postfix(WeaponBase __instance, IBarrel __0,
                FirePatchState __state)
            {
                var profile = PatchProfiler.Enter(PatchId.ProjectileCaptureFirePostfix);
                try { PostfixBody(__instance, __0, __state); }
                finally { PatchProfiler.Exit(PatchId.ProjectileCaptureFirePostfix, profile); }
            }

            private static void PostfixBody(WeaponBase __instance, IBarrel __0,
                FirePatchState state)
            {
                if (state.Seeded) UnityEngine.Random.state = state.RandomState;
                if (state.InstalledShot) _currentShot = state.PreviousShot;
                if (state.Suppressed) return;

                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame || _replayDepth > 0) return;
                try
                {
                    if (state.Holder >= 0)
                    {
                        var msg = new FireEventMsg
                        {
                            Slot = (byte)session.LocalSlot,
                            Holder = (byte)state.Holder,
                            TimeMs = (uint)(Time.unscaledTime * 1000f),
                            BodyPos = ShipSync.LocalShip != null ? (Vector2)ShipSync.LocalShip.transform.position : Vector2.zero,
                            Pos = __0.Position,
                            Dir = __0.Direction,
                            Seed = _lastFireSeed,
                            ShotId = state.ShotId,
                        };
                        Writer.Reset();
                        msg.Write(Writer);
                        session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
                        return;
                    }
                    TryCaptureEntityFire(session, __instance, __0, state.Entity, state.NetId, state.ShotId);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"[Fire] capture failed: {e.Message}");
                }
            }
        }

        private static uint NextShotId(byte slot)
        {
            _shotSequence = (_shotSequence + 1u) & 0x00FFFFFFu;
            if (_shotSequence == 0) _shotSequence = 1;
            return ((uint)slot << 24) | _shotSequence;
        }

        /// <summary>Replay DoShoot with the shooter's RNG seed, restoring the local stream after.</summary>
        private static void InvokeSeededDoShoot(WeaponBase weapon, FakeBarrel barrel, int seed)
        {
            var saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            try
            {
                AccessTools.Method(typeof(WeaponBase), "DoShoot").Invoke(weapon, new object[] { barrel });
            }
            finally
            {
                UnityEngine.Random.state = saved;
            }
        }

        private static int FindLocalHolder(WeaponBase weapon)
        {
            var ship = ShipSync.LocalShip;
            if (ship == null) return -1;
            if (GetHolderWeapon(ship, 0) == weapon) return 0;
            if (GetHolderWeapon(ship, 1) == weapon) return 1;
            return -1;
        }

        private static WeaponBase GetHolderWeapon(Ship ship, int holder)
        {
            try
            {
                var field = holder == 0 ? "primaryWeaponHolder" : "secondaryWeaponHolder";
                var wh = Traverse.Create(ship).Field(field).GetValue() as WeaponHolder;
                return wh != null ? wh.Weapon : null;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- entity (enemy/boss) fire

        private static void TryCaptureEntityFire(NetSession session, WeaponBase weapon, IBarrel barrel,
            SavableEntity resolvedEntity, int resolvedNetId, uint shotId)
        {
            var owner = (se: resolvedEntity, netId: resolvedNetId);
            if (owner.se == null && !EntityWeapons.TryGetValue(weapon, out owner))
            {
                owner = ResolveEntityWeapon(weapon);
                EntityWeapons[weapon] = owner; // caches failures as (null, 0) too
            }
            if (owner.se == null) return;
            if (EnemySync.IsKilled(owner.netId)) return; // zombie awaiting re-kill — never announce
            // Only the entity's simulating authority announces its shots.
            if (!EnemySync.CanSimulate(owner.se, owner.netId)) return;

            // Homing projectiles get their target at fire time from the shooter's AIAgent —
            // muted on puppets, so peers must be told who is being shot at.
            byte targetSlot = 255;
            try
            {
                var agent = owner.se.GetComponentInChildren<AIAgent>(true);
                var target = agent != null && agent.HasTarget ? agent.Target : null;
                var targetShip = target != null ? target.GetComponent<Ship>() : null;
                if (targetShip != null)
                {
                    foreach (var kv in ShipSync.ShipsBySlot)
                        if (kv.Value == targetShip) { targetSlot = (byte)kv.Key; break; }
                }
            }
            catch { }

            var segment = AuthorityManager.SegmentOf(owner.se.transform.position);
            var msg = new EntityFireMsg
            {
                NetId = owner.netId,
                Lifetime = Core.NetIds.LifetimeOf(owner.netId),
                SourceSlot = (byte)session.LocalSlot,
                SegmentX = segment.X,
                SegmentY = segment.Y,
                Epoch = EnemySync.FixedOwners.Contains(owner.netId) ? 0 : AuthorityManager.EpochOf(segment),
                ShotId = shotId,
                Pos = barrel.Position,
                Dir = barrel.Direction,
                BodyPos = owner.se.transform.position,
                TargetSlot = targetSlot,
                Seed = _lastFireSeed,
            };
            // Host-simulated fire never passes through Dispatch — feed the registrar here.
            if (session.IsHost) Core.AuthorityManager.NoteAggro(owner.netId, targetSlot);
            if (Core.NetDiag.Enabled)
                Core.NetDiag.Throttled($"fire{owner.netId}", 1f, "Fire",
                    () => $"{Core.NetDiag.Describe(owner.netId)} announcing shot={shotId} (I simulate it)" +
                          (targetSlot != 255 ? $" at {Core.NetDiag.Owner(targetSlot)}" : ""));
            Writer.Reset();
            msg.Write(Writer);
            session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
        }

        private static (SavableEntity, int) ResolveEntityWeapon(WeaponBase weapon)
        {
            foreach (var shooter in Object.FindObjectsByType<Shooter>(FindObjectsSortMode.None))
            {
                var w = Traverse.Create(shooter).Field("weapon").GetValue() as WeaponBase;
                if (w != weapon) continue;
                var se = shooter.GetComponentInParent<SavableEntity>();
                if (se != null && se.EntityData != null && Core.NetIds.TryGetNetId(se.EntityData.instanceId, out int netId))
                    return (se, netId);
                break;
            }
            return (null, 0);
        }

        public static void ReplayEntityFire(EntityFireMsg msg)
        {
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame) return; // stale post-run traffic
            if (!Core.NetIds.LifetimeMatches(msg.NetId, msg.Lifetime))
            {
                Core.InstrumentationCounters.StaleLifetimeDropped();
                return;
            }
            if (EnemySync.IsKilled(msg.NetId)) return; // dead here — nothing to anchor the replay to
            var segment = new AuthorityManager.SegmentKey(msg.SegmentX, msg.SegmentY);
            if (!AuthorityManager.SegmentOf(msg.BodyPos).Equals(segment)
                || !AuthorityManager.IsStateAuthority(msg.NetId, segment, msg.SourceSlot, msg.Epoch))
            {
                InstrumentationCounters.StaleFireDropped();
                if (NetDiag.Enabled) NetDiag.Throttled($"stalefire{msg.NetId}", 1f, "Fire",
                    () => $"dropped stale fire {NetDiag.Describe(msg.NetId)} shot={msg.ShotId} from P{msg.SourceSlot + 1}/{msg.Epoch} segment={segment}");
                return;
            }
            if (!AcceptFireIdentity(((ulong)(uint)msg.NetId << 32) | msg.ShotId)) return;
            if (!Core.NetIds.TryGetInstanceId(msg.NetId, out int instanceId)) return;
            try
            {
                var egm = ServiceLocator.Get<EntityGameObjectManager>();
                if (!egm.TryGetSavableEntity(instanceId, out var se) || se == null) return;
                if (se.GetComponent<RemoteEntityPuppet>() == null) return; // we own it — our own echo
                var shooter = se.GetComponentInChildren<Shooter>(true);
                var weapon = shooter != null ? Traverse.Create(shooter).Field("weapon").GetValue() as WeaponBase : null;
                if (weapon == null) return;
                // The puppet is drawn where the interpolation buffer says, ~100-250 ms behind the
                // authority — anchor the muzzle to the local body (keeping the authority's muzzle
                // offset) so shots visibly come out of the enemy, not out of empty space.
                Vector2 pos = msg.BodyPos != Vector2.zero
                    ? (Vector2)se.transform.position + (msg.Pos - msg.BodyPos)
                    : msg.Pos;
                if (Core.NetDiag.Enabled)
                    Core.NetDiag.Throttled($"replay{msg.NetId}", 1f, "Fire",
                        () => $"{Core.NetDiag.Describe(msg.NetId)} replaying remote shot={msg.ShotId} (puppet here)" +
                              (msg.TargetSlot != 255 ? $", targeting {Core.NetDiag.Owner(msg.TargetSlot)}" : ""));
                ReplaySpawned.Clear();
                var previousShot = _currentShot;
                _currentShot = new ShotContext
                {
                    SourceNetId = msg.NetId,
                    SourceSlot = msg.SourceSlot,
                    ShotId = msg.ShotId,
                    Replayed = true,
                };
                _replayDepth++;
                try
                {
                    InvokeSeededDoShoot(weapon, new FakeBarrel(pos, msg.Dir), msg.Seed);
                }
                finally
                {
                    _replayDepth--;
                    _currentShot = previousShot;
                }

                // Prefab-wired fire cosmetics (recoil VFX etc.) hang off Shooter.OnShoot,
                // which the DoShoot-direct replay skips.
                try { shooter.OnShoot?.Invoke(); } catch { }

                // Re-target replayed homing projectiles: their HomingTargetFromAIAgent read the
                // puppet's muted AIAgent (no target), so they'd fly straight while the
                // authority's real ones curve. Aim them at the same player the authority is
                // shooting at — if that's the local ship, victim-side hit detection makes them
                // genuinely dangerous, exactly like the authority's copies.
                if (msg.TargetSlot != 255
                    && ShipSync.ShipsBySlot.TryGetValue(msg.TargetSlot, out var targetShip) && targetShip != null)
                {
                    foreach (var spawned in ReplaySpawned)
                        if (spawned is PhysicsProjectile pp)
                            pp.Target = targetShip.gameObject;
                }
                ReplaySpawned.Clear();
            }
            catch (System.Exception e)
            {
                // Once as a warning, then debug: this runs per shot, and a repeating failure
                // (a half-destroyed entity, a game update) becomes a disk-log storm that
                // reads as an FPS bug on the receiving machine.
                if (!_warnedEntityReplay)
                {
                    _warnedEntityReplay = true;
                    Plugin.Log.LogWarning($"[Fire] entity replay failed: {e.Message} (further failures logged as debug)");
                }
                else Plugin.Log.LogDebug($"[Fire] entity replay failed: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- replay

        public static void ReplayFire(FireEventMsg msg)
        {
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame) return; // stale post-run traffic
            ulong identity = 0x8000000000000000UL | ((ulong)msg.Slot << 32) | msg.ShotId;
            if (!AcceptFireIdentity(identity)) return;
            if (!ShipSync.ShipsBySlot.TryGetValue(msg.Slot, out var ship) || ship == null) return;
            if (ship.GetComponent<RemotePuppet>() == null) return; // our own echo
            float playAt = Core.ClockSync.MapToLocalTime(msg.Slot, msg.TimeMs) + RemotePuppet.VisualDelay;
            float wait = playAt - Time.unscaledTime;
            if (msg.TimeMs != 0 && wait > 0.002f && wait < 0.5f)
            {
                PendingShipFires.Add(new PendingShipFire { Msg = msg, PlayAt = playAt });
                _shipFireQueued++;
                return;
            }
            _shipFireLate++;
            ReplayFireNow(msg);
        }

        public static void Tick()
        {
            float now = Time.unscaledTime;
            for (int i = PendingShipFires.Count - 1; i >= 0; i--)
            {
                var pending = PendingShipFires[i];
                if (now < pending.PlayAt) continue;
                PendingShipFires.RemoveAt(i);
                ReplayFireNow(pending.Msg);
            }
        }

        private static void ReplayFireNow(FireEventMsg msg)
        {
            if (!ShipSync.ShipsBySlot.TryGetValue(msg.Slot, out var ship) || ship == null) return;
            if (ship.GetComponent<RemotePuppet>() == null) return;
            var weapon = GetHolderWeapon(ship, msg.Holder);
            if (weapon == null)
            {
                // The owner fired a weapon this puppet doesn't have — its module grid hasn't
                // (or never) arrived. Was a silent return; that hid a dead ModuleGridSync.
                if (Time.unscaledTime >= _nextNullHolderWarnAt)
                {
                    _nextNullHolderWarnAt = Time.unscaledTime + 5f;
                    Plugin.Log.LogWarning($"[Fire] P{msg.Slot + 1}'s holder {msg.Holder} has no weapon on the puppet " +
                        "(module grid not applied?) — shot dropped");
                }
                return;
            }

            Vector2 pos = msg.BodyPos != Vector2.zero
                ? (Vector2)ship.transform.position + (msg.Pos - msg.BodyPos)
                : msg.Pos;
            float correction = Vector2.Distance(pos, msg.Pos);
            _muzzleCorrectionTotal += correction;
            if (correction > _muzzleCorrectionMax) _muzzleCorrectionMax = correction;

            var previousShot = _currentShot;
            _currentShot = new ShotContext
            {
                SourceNetId = -1,
                SourceSlot = msg.Slot,
                ShotId = msg.ShotId,
                Replayed = true,
            };
            _replayDepth++;
            try
            {
                InvokeSeededDoShoot(weapon, new FakeBarrel(pos, msg.Dir), msg.Seed);
            }
            catch (System.Exception e)
            {
                if (!_warnedShipReplay)
                {
                    _warnedShipReplay = true;
                    Plugin.Log.LogWarning($"[Fire] replay failed: {e.Message} (further failures logged as debug)");
                }
                else Plugin.Log.LogDebug($"[Fire] replay failed: {e.Message}");
            }
            finally
            {
                _replayDepth--;
                _currentShot = previousShot;
            }
        }

        private static bool AcceptFireIdentity(ulong identity)
        {
            if (!SeenFireEvents.Add(identity))
            {
                InstrumentationCounters.DuplicateFireDropped();
                return false;
            }
            SeenFireOrder.Enqueue(identity);
            if (SeenFireOrder.Count > RecentIdentityLimit)
                SeenFireEvents.Remove(SeenFireOrder.Dequeue());
            return true;
        }

        // Anything spawned while replaying is visual-only; also collected so the replay can
        // post-process it (homing re-target).
        private static readonly List<Component> ReplaySpawned = new List<Component>();

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        internal static class MarkVisualProjectile
        {
            private static void Prefix(Projectile __instance)
            {
                var profile = PatchProfiler.Enter(PatchId.ProjectileMarkVisual);
                try { PrefixBody(__instance); }
                finally { PatchProfiler.Exit(PatchId.ProjectileMarkVisual, profile); }
            }

            private static void PrefixBody(Projectile __instance)
            {
                StampProjectile(__instance);
                if (_replayDepth <= 0) return;
                VisualProjectiles.Add(__instance.gameObject.GetInstanceID());
                ReplaySpawned.Add(__instance);
                InstrumentationCounters.VisualProjectileSpawned();
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        internal static class MarkVisualPhysicsProjectile
        {
            private static void Prefix(PhysicsProjectile __instance)
            {
                var profile = PatchProfiler.Enter(PatchId.ProjectileMarkVisualPhysics);
                try { PrefixBody(__instance); }
                finally { PatchProfiler.Exit(PatchId.ProjectileMarkVisualPhysics, profile); }
            }

            private static void PrefixBody(PhysicsProjectile __instance)
            {
                StampProjectile(__instance);
                if (_replayDepth <= 0) return;
                VisualProjectiles.Add(__instance.gameObject.GetInstanceID());
                ReplaySpawned.Add(__instance);
                InstrumentationCounters.VisualProjectileSpawned();
            }
        }

        private static void StampProjectile(Component projectile)
        {
            if (projectile == null || _currentShot == null) return;
            var identity = projectile.GetComponent<NetworkProjectileIdentity>();
            if (identity != null) return;
            identity = projectile.gameObject.AddComponent<NetworkProjectileIdentity>();
            identity.SourceNetId = _currentShot.SourceNetId;
            identity.SourceSlot = _currentShot.SourceSlot;
            identity.ShotId = _currentShot.ShotId;
            identity.Ordinal = _currentShot.NextOrdinal++;
            identity.Replayed = _currentShot.Replayed;
        }

        // ---------------------------------------------------------------- damage suppression

        /// <summary>True when this projectile must not deal damage on this machine: it was spawned
        /// by a fire-event replay, or it belongs to a puppet's unit (its real twin lives on the
        /// owner's machine, which routes damage authoritatively).</summary>
        public static bool IsVisual(object projectile)
        {
            if (projectile is Component c)
            {
                if (VisualProjectiles.Contains(c.gameObject.GetInstanceID())) return true;
                try
                {
                    var owner = Traverse.Create(projectile).Property("Owner").GetValue() as Unit;
                    if (owner == null)
                        owner = Traverse.Create(projectile).Field("owner").GetValue() as Unit;
                    if (owner != null && (owner.GetComponent<RemotePuppet>() != null
                                          || owner.GetComponent<RemoteEntityPuppet>() != null)) return true;
                }
                catch { }
            }
            return false;
        }

        // Enemy fire hit-detects on the VICTIM's machine (NEW-style): the only enemy bullets that
        // exist here for a remote-simulated enemy are its replayed ones, and they may damage the
        // local ship — full vanilla pipeline — and nothing else. The mirror half: this machine's
        // real enemies never damage player puppets; that victim sees the replay and applies it
        // themselves. Matches how contact/hazard damage already works (victim-side), and means
        // you can only be hit by shots that visibly reach you on your own screen.
        [HarmonyPatch(typeof(HealthBase), "ProjectileCollided")]
        internal static class SuppressVisualProjectileDamage
        {
            private static bool Prefix(HealthBase __instance, object __0, out DamageTrace? __state)
            {
                __state = _currentDamageTrace;
                var profile = PatchProfiler.Enter(PatchId.ProjectileSuppressDamage);
                try
                {
                    if (!TryAcceptImpact(__instance, __0)) return false;
                    bool allowed = PrefixBody(__instance, __0);
                    if (allowed && IsLocalShip(__instance)) _currentDamageTrace = TraceOf(__0, "projectile");
                    return allowed;
                }
                finally { PatchProfiler.Exit(PatchId.ProjectileSuppressDamage, profile); }
            }

            private static void Postfix(DamageTrace? __state) => _currentDamageTrace = __state;

            private static bool PrefixBody(HealthBase __instance, object __0)
            {
                if (!NetSession.Active) return true;
                var owner = OwnerUnit(__0);
                if (owner != null && owner.GetComponent<RemoteEntityPuppet>() != null)
                {
                    if (IsLocalShip(__instance))
                    {
                        // A puppet enemy's bullets should ONLY be the ones we replayed from the
                        // owner's EntityFire. One that was NOT spawned during a replay means the
                        // puppet fired it locally (muting gap / extra spawn) — a duplicate, and the
                        // likely "ghost" shot. Flag which kind hit us.
                        NoteHit(owner, WasReplaySpawned(__0)
                            ? "projectile (replayed, expected)"
                            : "projectile (RAW — puppet fired locally, DUPLICATE)");
                        return true; // replayed enemy fire: victim-side
                    }
                    UnitStatus.PlayDamageFlash(__instance);   // cosmetic — the authority owns the hit
                    return false;
                }
                if (IsVisual(__0))
                {
                    // A teammate's replayed shot landing here means the real hit is being applied
                    // on their machine right now — show the flash they're seeing (plants and other
                    // props never sync damage, only death, so this is the ONLY feedback).
                    UnitStatus.PlayDamageFlash(__instance);
                    return false;
                }
                // Environmental (ownerless) projectiles fire independently on every client — only
                // the victim's own authority applies their damage, or shared enemies eat it twice.
                if (owner == null) return !VictimIsRemote(__instance);
                // Real local enemy vs a teammate's puppet: suppressed — they apply the replay.
                // (Player-vs-player stays shooter-routed: Ship owners fall through.)
                if (owner.GetComponent<Ship>() == null && IsPuppetShip(__instance)) return false;
                if (FriendlyFireBlocked(owner, __instance)) return false;
                return true;
            }
        }

        internal static bool TryGetCurrentDamageTrace(out DamageTrace trace)
        {
            if (_currentDamageTrace.HasValue)
            {
                trace = _currentDamageTrace.Value;
                return true;
            }
            trace = default;
            return false;
        }

        private static DamageTrace TraceOf(object projectile, string kind)
        {
            if (projectile is Component component)
            {
                var identity = component.GetComponent<NetworkProjectileIdentity>();
                if (identity != null)
                    return new DamageTrace(identity.SourceNetId, identity.SourceSlot, identity.ShotId,
                        identity.Ordinal, component.gameObject.GetInstanceID(), identity.Replayed, kind);
                var owner = OwnerUnit(projectile);
                int netId = owner != null && EnemySync.TryGetNetId(owner, out int id) ? id : -2;
                byte slot = netId >= 0 ? EnemySync.OwnerOf(netId) : (byte)255;
                return new DamageTrace(netId, slot, 0, 0, component.gameObject.GetInstanceID(), false, kind);
            }
            return new DamageTrace(-2, 255, 0, 0, 0, false, kind);
        }

        private static bool TryAcceptImpact(Component victim, object projectile)
        {
            if (!NetSession.Active || victim == null || !(projectile is Component component)) return true;
            var identity = component.GetComponent<NetworkProjectileIdentity>();
            var key = identity != null
                ? new ImpactKey(identity.SourceNetId, identity.ShotId, identity.Ordinal, 0, victim.gameObject.GetInstanceID())
                : new ImpactKey(0, 0, 0, component.gameObject.GetInstanceID(), victim.gameObject.GetInstanceID());
            if (!SeenImpacts.Add(key))
            {
                InstrumentationCounters.DuplicateImpactDropped();
                if (NetDiag.Enabled) NetDiag.Throttled($"impact{component.gameObject.GetInstanceID()}:{victim.gameObject.GetInstanceID()}", 1f, "Damage",
                    () => $"dropped duplicate projectile impact shot={(identity != null ? identity.ShotId : 0)} pellet={(identity != null ? identity.Ordinal : (ushort)0)} victim={victim.name}");
                return false;
            }
            SeenImpactOrder.Enqueue(key);
            if (SeenImpactOrder.Count > RecentIdentityLimit)
                SeenImpacts.Remove(SeenImpactOrder.Dequeue());
            return true;
        }

        /// <summary>My real shot against a teammate's puppet while the host disabled friendly
        /// fire — drop it before it reaches the routed damage path.</summary>
        private static bool FriendlyFireBlocked(Unit owner, Component victim)
        {
            var session = NetSession.Instance;
            if (session == null || session.FriendlyFire) return false;
            return owner != null
                   && owner.GetComponent<Ship>() != null
                   && owner.GetComponent<RemotePuppet>() == null
                   && IsPuppetShip(victim);
        }

        // My own weapon explosions apply AoE synchronously inside SpawnExplosion — the depth
        // flag lets the TakeDamage interceptor drop teammate-puppet AoE when friendly fire is off.
        private static int _localShipExplosionDepth;

        public static bool FriendlyExplosionBlocked(Component victim)
        {
            var session = NetSession.Instance;
            if (session == null || session.FriendlyFire) return false;
            return _localShipExplosionDepth > 0 && victim != null && victim.GetComponent<RemotePuppet>() != null;
        }

        [HarmonyPatch]
        internal static class TrackLocalShipExplosions
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var typeName in new[] { "Projectile", "PhysicsProjectile" })
                {
                    var t = AccessTools.TypeByName(typeName);
                    var m = t != null ? AccessTools.Method(t, "SpawnExplosion") : null;
                    if (m != null) yield return m;
                }
            }

            private static void Prefix(object __instance, out bool __state)
            {
                __state = false;
                if (!NetSession.Active || IsVisual(__instance)) return;
                var owner = OwnerUnit(__instance);
                var local = ShipSync.LocalShip;
                if (owner == null || local == null || owner.gameObject != local.gameObject) return;
                __state = true;
                _localShipExplosionDepth++;
            }

            private static void Finalizer(bool __state)
            {
                if (__state) _localShipExplosionDepth--;
            }
        }

        private static bool VictimIsRemote(Component victim)
        {
            return victim != null && (victim.GetComponent<RemotePuppet>() != null
                                      || victim.GetComponent<RemoteEntityPuppet>() != null);
        }

        private static bool IsLocalShip(Component victim)
        {
            return victim != null && victim.GetComponent<Ship>() != null
                   && victim.GetComponent<RemotePuppet>() == null;
        }

        private static bool IsPuppetShip(Component victim)
        {
            return victim != null && victim.GetComponent<RemotePuppet>() != null;
        }

        private static Unit OwnerUnit(object weaponOrProjectile)
        {
            try
            {
                var owner = Traverse.Create(weaponOrProjectile).Property("Owner").GetValue() as Unit;
                if (owner == null)
                    owner = Traverse.Create(weaponOrProjectile).Field("owner").GetValue() as Unit;
                return owner;
            }
            catch { return null; }
        }

        private static bool IsOwnerless(object projectile) => OwnerUnit(projectile) == null;

        /// <summary>Was this projectile spawned during a fire replay (marked visual)? If not, but
        /// its owner is a puppet enemy, the puppet fired it locally — a duplicate.</summary>
        private static bool WasReplaySpawned(object projectile) =>
            projectile is Component c && VisualProjectiles.Contains(c.gameObject.GetInstanceID());

        /// <summary>Diag: log an incoming hit on the local ship, naming the source enemy, the kind
        /// (projectile/hitscan), and how far the shooter is. A large distance means the shot came
        /// from an off-screen enemy — which reads as a "ghost"/invisible projectile appearing from
        /// nowhere; a tiny one means point-blank. Also flags whether the shooter is even spawned.</summary>
        private static void NoteHit(Unit owner, string kind)
        {
            if (owner == null || !Core.NetDiag.Enabled) return;
            Core.NetDiag.Throttled($"hit{owner.GetInstanceID()}", 0.5f, "Hit", () =>
            {
                bool hasNetId = EnemySync.TryGetNetId(owner, out int en);
                string src = hasNetId ? Core.NetDiag.Describe(en) : owner.name;
                var ship = ShipSync.LocalShip;
                float dist = ship != null ? Vector2.Distance(ship.transform.position, owner.transform.position) : -1f;
                byte ownerSlot = hasNetId ? EnemySync.OwnerOf(en) : (byte)255;
                string sim = ownerSlot == 255 ? "" : $", simulated by {Core.NetDiag.Owner(ownerSlot)}";
                return $"my ship hit by {kind} from {src} at {dist:0}u{sim}";
            });
        }

        // Hitscan mirrors the projectile rules; the raycast happens synchronously inside the
        // replayed DoShoot, so the enemy-puppet-owner allowance must come before the blanket
        // replay suppression.
        [HarmonyPatch(typeof(HealthBase), "OnHitByHitscanWeapon")]
        internal static class SuppressVisualHitscanDamage
        {
            private static bool Prefix(HealthBase __instance, object __0, out DamageTrace? __state)
            {
                __state = _currentDamageTrace;
                var profile = PatchProfiler.Enter(PatchId.ProjectileSuppressHitscanDamage);
                try
                {
                    if (!TryAcceptHitscan(__instance)) return false;
                    bool allowed = PrefixBody(__instance, __0);
                    if (allowed && IsLocalShip(__instance))
                    {
                        if (_currentShot != null)
                            _currentDamageTrace = new DamageTrace(_currentShot.SourceNetId, _currentShot.SourceSlot,
                                _currentShot.ShotId, 0, 0, _currentShot.Replayed, "hitscan");
                        else
                            _currentDamageTrace = TraceOf(__0, "hitscan");
                    }
                    return allowed;
                }
                finally { PatchProfiler.Exit(PatchId.ProjectileSuppressHitscanDamage, profile); }
            }

            private static void Postfix(DamageTrace? __state) => _currentDamageTrace = __state;

            private static bool PrefixBody(HealthBase __instance, object __0)
            {
                if (!NetSession.Active) return true;
                var owner = OwnerUnit(__0);
                if (owner != null && owner.GetComponent<RemoteEntityPuppet>() != null)
                {
                    if (IsLocalShip(__instance)) { NoteHit(owner, "HITSCAN (beam may not render on replay)"); return true; } // replayed enemy beam: victim-side
                    UnitStatus.PlayDamageFlash(__instance);
                    return false;
                }
                if (_replayDepth > 0)
                {
                    UnitStatus.PlayDamageFlash(__instance); // replayed teammate beam: cosmetic hit
                    return false;
                }
                if (owner != null && owner.GetComponent<RemotePuppet>() != null)
                {
                    UnitStatus.PlayDamageFlash(__instance);
                    return false;
                }
                if (owner == null) return !VictimIsRemote(__instance);
                if (owner.GetComponent<Ship>() == null && IsPuppetShip(__instance)) return false;
                if (FriendlyFireBlocked(owner, __instance)) return false;
                return true;
            }
        }

        private static bool TryAcceptHitscan(Component victim)
        {
            if (!NetSession.Active || victim == null || _currentShot == null) return true;
            var key = new ImpactKey(_currentShot.SourceNetId, _currentShot.ShotId, 0, -1,
                victim.gameObject.GetInstanceID());
            if (!SeenImpacts.Add(key))
            {
                InstrumentationCounters.DuplicateImpactDropped();
                return false;
            }
            SeenImpactOrder.Enqueue(key);
            if (SeenImpactOrder.Count > RecentIdentityLimit)
                SeenImpacts.Remove(SeenImpactOrder.Dequeue());
            return true;
        }

        private static bool OwnerIsRemote(object weaponOrProjectile)
        {
            var owner = OwnerUnit(weaponOrProjectile);
            return owner != null && (owner.GetComponent<RemotePuppet>() != null
                                     || owner.GetComponent<RemoteEntityPuppet>() != null);
        }

        // Visual projectiles must not spawn REAL explosions (area damage + cell destruction would
        // double up with the simulating machine's authoritative versions — terrain truth arrives
        // via CELL_DIFF). Instead we spawn a sanitized copy with all damage/push/burn zeroed so
        // peers still see the boom. If sanitizing fails for a weapon, fall back to no explosion.
        [HarmonyPatch]
        internal static class SuppressVisualExplosions
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var typeName in new[] { "Projectile", "PhysicsProjectile" })
                {
                    var t = AccessTools.TypeByName(typeName);
                    var m = t != null ? AccessTools.Method(t, "SpawnExplosion") : null;
                    if (m != null) yield return m;
                }
            }

            private static bool Prefix(object __instance)
            {
                if (!NetSession.Active) return true;
                if (!IsVisual(__instance)) return true;
                TrySpawnHarmlessExplosion(__instance);
                return false;
            }
        }

        /// <summary>Zero every damaging aspect of a boxed Explosion, keep radius/types for visuals.</summary>
        internal static void ZeroExplosionFields(object boxed)
        {
            foreach (var field in boxed.GetType().GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType == typeof(float)
                    && field.Name.IndexOf("radius", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    field.SetValue(boxed, 0f);
                }
                else if (typeof(System.Collections.IList).IsAssignableFrom(field.FieldType))
                {
                    if (field.GetValue(boxed) is System.Collections.IList list)
                        for (int i = 0; i < list.Count; i++)
                            if (list[i] is Damage damage)
                            {
                                var type = Traverse.Create(damage).Field("damageType").GetValue() as Resource;
                                list[i] = new Damage(0f, type);
                            }
                }
            }
        }

        // A death replayed from the network (synced barrel/enemy kill) must not re-apply its
        // death explosion's damage — the kill originator's real explosion already did, exactly once.
        [HarmonyPatch(typeof(ExplosionManager), "SpawnExplosion")]
        internal static class SanitizeReplayedDeathExplosions
        {
            private static void Prefix(ref Explosion __1)
            {
                if (!NetSession.Active || !EnemySync.SuppressLocalDeathEffects) return;
                object boxed = __1;
                try { ZeroExplosionFields(boxed); __1 = (Explosion)boxed; } catch { }
            }
        }

        private static void TrySpawnHarmlessExplosion(object projectile)
        {
            try
            {
                var component = projectile as Component;
                object boxed = Traverse.Create(projectile).Field("explosion").GetValue();
                if (component == null || boxed == null) return;

                ZeroExplosionFields(boxed);

                var manager = ServiceLocator.Get<ExplosionManager>();
                AccessTools.Method(typeof(ExplosionManager), "SpawnExplosion")
                    .Invoke(manager, new object[] { (Vector2)component.transform.position, boxed });
            }
            catch
            {
                // Visual-only nicety; correctness path (skip) already happened.
            }
        }
    }
}
