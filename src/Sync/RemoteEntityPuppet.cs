using System.Collections.Generic;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Attached to an enemy/minion GameObject that some OTHER machine simulates. Mutes the local
    /// AI stack (state machine, targeting, movement, shooting) and drives the body from network
    /// snapshots. Removing the component re-enables everything — that's an authority handoff.
    /// </summary>
    public sealed class RemoteEntityPuppet : MonoBehaviour
    {
        // AI drivers to mute, by type name (game types looked up per-instance so new enemy
        // variants keep working). Shooter is muted so puppets don't fire duplicate projectiles.
        // AimAction/MovementAction subclasses are muted by base type below.
        // The action types matter because actions call into disabled components DIRECTLY
        // (ShootComplexAction.Update -> Shooter.Shoot fires weapons — and MinionSpawnerWeapon
        // spawns entities — even while Shooter itself is disabled; SelfDestructAction kills the
        // puppet locally; ProjectileDispenser is a prop-mounted auto-firer).
        private static readonly string[] MutedTypes =
        {
            "AIAgent", "Vision", "UnitMovement", "Seeker", "Shooter", "StateMachine",
            // SwayMovement AddForce()s a Perlin-noise force onto the body every FixedUpdate. On a
            // puppet that force keeps accumulating velocity WHILE snapshots also drive the body —
            // the two fight and the enemy (fireflies especially) runs away into insane, unhittable
            // speeds. Muting it lets the authority's snapshots be the only thing moving the puppet.
            "PushMovement", "SwimmingMovement", "SwayMovement", "ChargerRam",
            "ShootComplexAction", "ShootAction", "ActivateShooterAction", "SelfDestructAction",
            // Runs a resource-drain check every frame and Object.Destroy()s the entity + spawns its
            // drop. On a puppet it would fire off the local (un-synced) resource — destroying/looting
            // out of step with the owner. Muted; the owner's SyncDestroy removes it here in sync.
            "DestroyWhenResourceDrained",
            "ProjectileDispenser", "WaitForTargetAction", "MoveAwayFromTargetAction",
            "MoveToPositionAction", "PushSelfAction", "StopAction", "ApplyTorqueAction",
            "ReduceAngularVelocityAction", "RepeateChildrenAction", "ForgetTargetAction",
            // ChangeAnimatorParamAction is deliberately NOT muted: it only sets animator bools
            // (pure cosmetics), and with AI states replicated it keeps puppet animations correct.
        };

        // Two snapshot intervals of buffer at the configured rate — enough for one lost packet.
        private const float HardSnapDistance = 4f;

        public int NetId;
        public Vector2 AimDirection { get; private set; }

        private byte _fireState; // 0 idle, 1 warming, 2 firing — from the owner (see SetFireState)
        private Shooter _shooter;
        private bool _shooterChecked;
        private float _nextFireAuditAt;

        /// <summary>Owner's fire state, so a beam (hitscan) enemy's beam can be drawn here — the
        /// muted Shooter would otherwise never render it, leaving invisible beams that still hurt.</summary>
        public void SetFireState(byte state)
        {
            // Viewer half of the fire audit: the owner logs "[FireAudit] owned ..." when its
            // AI pulls the trigger; this logs when that state REACHES a viewer. An owned line
            // with no puppet line = state sync gap; both lines but no projectiles on screen =
            // fire-EVENT capture/replay gap for this weapon; neither = the AI never fired.
            if (state != 0 && _fireState == 0 && Time.unscaledTime >= _nextFireAuditAt)
            {
                _nextFireAuditAt = Time.unscaledTime + 5f; // per-entity limit; transitions are rare
                Plugin.Log.LogInfo($"[FireAudit] puppet #{NetId} entered fire={state}");
            }
            _fireState = state;
        }

        private struct Snap
        {
            public float Time;
            public Vector2 Pos;
            public Vector2 Vel;
            public float Rot;
        }

        private readonly List<Snap> _buffer = new List<Snap>(16);
        private readonly AdaptiveSnapshotTiming _timing = new AdaptiveSnapshotTiming(0.045f, 0.18f, 0.05f);
        private readonly List<Behaviour> _muted = new List<Behaviour>();
        private Rigidbody2D _rb;
        private BarrelTransform[] _barrels;

        private RigidbodyInterpolation2D _savedInterpolation;
        private bool _savedSimulated;
        private RigidbodyType2D _savedBodyType;
        private float _savedGravityScale;
        private bool _savedFullKinematicContacts;
        private bool _instrumentationCounted;
        public bool HasSnapshot => _buffer.Count > 0;
        private float _enabledAt;
        private float _lastSnapshotReceivedAt;
        public float PuppetAge => Time.unscaledTime - _enabledAt;
        public float SnapshotAge => _buffer.Count == 0
            ? float.PositiveInfinity
            : Time.unscaledTime - _lastSnapshotReceivedAt;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _barrels = GetComponentsInChildren<BarrelTransform>(true);
            if (_rb != null)
            {
                // Fixed-step snapshot driving needs render interpolation or the enemy stutters
                // on high-refresh displays; restored on handoff (OnDestroy) below.
                _savedInterpolation = _rb.interpolation;
                _savedSimulated = _rb.simulated;
                _savedBodyType = _rb.bodyType;
                _savedGravityScale = _rb.gravityScale;
                _savedFullKinematicContacts = _rb.useFullKinematicContacts;
                // Passive kinematic bodies remain raycast/projectile targets but do not fall,
                // integrate forces, or generate kinematic-vs-static terrain contacts. This is
                // intentionally true even before the first snapshot and while packets are stale.
                _rb.linearVelocity = Vector2.zero;
                _rb.angularVelocity = 0;
                _rb.gravityScale = 0;
                _rb.bodyType = RigidbodyType2D.Kinematic;
                _rb.useFullKinematicContacts = false;
                _rb.simulated = true;
            }
        }

        // The muted AI no longer steers the barrels; mirror the authority's aim through the
        // game's own BarrelTransform.Direction (kept enabled) so its LateUpdate applies the
        // visible rotation. Body facing arrives separately via the rb.rotation snapshots.
        private void Update()
        {
            if (_barrels != null && AimDirection.sqrMagnitude >= 0.01f)
                foreach (var barrel in _barrels)
                    if (barrel != null)
                        barrel.Direction = AimDirection;
            DriveBeam();
            TickDormantAnimatorFreeze();
        }

        // A dormant puppet has NO simulator anywhere, but its Animator isn't muted (owned
        // puppets need it for replicated states) — so a mannequin frozen mid-attack loops its
        // attack animation forever and reads as "shooting without projectiles". Freeze the
        // animators while the entity is dormant-owned; the claim/wake (or first snapshot)
        // restores them.
        private Animator[] _animators;
        private float[] _animatorSpeeds;
        private bool _animatorsFrozen;
        private float _nextDormantCheckAt;

        private void TickDormantAnimatorFreeze()
        {
            if (NetId < 0) return; // orphans keep vanilla visuals
            float now = Time.unscaledTime;
            if (now < _nextDormantCheckAt) return;
            _nextDormantCheckAt = now + 1f;
            bool dormant = EnemySync.OwnerOf(NetId) == Core.AuthorityManager.DormantOwner
                && SnapshotAge > 2f; // fed puppets are alive regardless of the lease table
            if (dormant != _animatorsFrozen) SetAnimatorsFrozen(dormant);
        }

        private void SetAnimatorsFrozen(bool frozen)
        {
            _animators ??= GetComponentsInChildren<Animator>(true);
            if (frozen)
            {
                _animatorSpeeds = new float[_animators.Length];
                for (int i = 0; i < _animators.Length; i++)
                {
                    if (_animators[i] == null) continue;
                    _animatorSpeeds[i] = _animators[i].speed;
                    _animators[i].speed = 0f;
                }
            }
            else if (_animatorSpeeds != null)
            {
                for (int i = 0; i < _animators.Length; i++)
                    if (_animators[i] != null) _animators[i].speed = _animatorSpeeds[i];
                _animatorSpeeds = null;
            }
            _animatorsFrozen = frozen;
        }

        // Hitscan (beam) enemies draw their beam in Shooter.Update via weapon.OnBarrelMoved, which
        // is muted on a puppet — so the beam was invisible while its replayed fire still dealt
        // damage. OnBarrelMoved only draws (the raycast finds the beam's end; damage lives in
        // FireSingle, which we replay separately), so we can safely drive it here from the owner's
        // fire state without double-damaging.
        private void DriveBeam()
        {
            if (!_shooterChecked)
            {
                _shooterChecked = true;
                try { _shooter = GetComponentInChildren<Shooter>(true); } catch { }
            }
            if (_shooter == null || _barrels == null || _barrels.Length == 0) return;
            // Shooter.weapon is subscription-assigned in OnEnable — null forever on replicas
            // muted before first activation. The holder owns the weapon either way.
            var weapon = _shooter.Weapon;
            if (weapon == null && _shooter.weaponHolder != null) weapon = _shooter.weaponHolder.Weapon;
            if (!(weapon is HitscanWeapon beam)) return;
            var barrel = _barrels[0];
            if (barrel == null) return;
            try
            {
                if (_fireState == 2)
                {
                    beam.IsTriggerPulled = true;
                    beam.Warmup(Time.deltaTime); // reach warmed state so OnBarrelMoved shows Firing
                    beam.OnBarrelMoved(barrel.Position, barrel.Direction);
                }
                else if (beam.IsTriggerPulled)
                {
                    beam.IsTriggerPulled = false;
                    beam.OnBarrelMoved(barrel.Position, barrel.Direction); // clears the beam visual
                }
            }
            catch { }
        }

        private void OnEnable()
        {
            _enabledAt = Time.unscaledTime;
            _lastSnapshotReceivedAt = 0f;
            if (!_instrumentationCounted)
            {
                _instrumentationCounted = true;
                Core.InstrumentationCounters.RemoteEntityAdded();
            }
            MuteNow();
        }

        /// <summary>Idempotent, and callable while the GameObject is still INACTIVE — replicas
        /// must be muted before their first activation, because prefab-active actions fire
        /// from OnEnable and their UniTask bursts only cancel on destroy.</summary>
        public void MuteNow()
        {
            if (_muted.Count > 0) return;
            foreach (var behaviour in GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == null || !behaviour.enabled) continue;
                bool mute = behaviour is AimAction || behaviour is MovementAction;
                if (!mute)
                {
                    var tn = behaviour.GetType().Name;
                    for (int i = 0; i < MutedTypes.Length; i++)
                    {
                        if (tn == MutedTypes[i]) { mute = true; break; }
                    }
                }
                if (!mute) continue;
                behaviour.enabled = false;
                _muted.Add(behaviour);
            }
        }

        private void OnDisable()
        {
            RemoveInstrumentationCount();
            StopWeaponSounds();
            // A superseded lifetime is permanently quarantined. Restoring its AI/body here would
            // undo DuplicateEntityInert at exactly the moment the puppet component is disabled.
            if (GetComponent<DuplicateEntityInert>() != null) return;
            if (_animatorsFrozen) SetAnimatorsFrozen(false);
            Unmute();
        }

        private void OnDestroy()
        {
            RemoveInstrumentationCount();
            StopWeaponSounds();
            if (GetComponent<DuplicateEntityInert>() != null) return;
            // The Animators outlive this component — a woken entity must not stay speed-0.
            if (_animatorsFrozen) SetAnimatorsFrozen(false);
            Unmute();
            if (_rb != null)
            {
                _rb.simulated = _savedSimulated;
                _rb.bodyType = _savedBodyType;
                _rb.gravityScale = _savedGravityScale;
                _rb.useFullKinematicContacts = _savedFullKinematicContacts;
                _rb.interpolation = _savedInterpolation;
            }
        }

        private void RemoveInstrumentationCount()
        {
            if (!_instrumentationCounted) return;
            _instrumentationCounted = false;
            Core.InstrumentationCounters.RemoteEntityRemoved();
        }

        // Replicated warmup/continuous loops must not outlive the puppet (death mid-telegraph
        // would leave the charge-up sound playing forever).
        private void StopWeaponSounds()
        {
            try
            {
                foreach (var shooter in GetComponentsInChildren<Shooter>(true))
                {
                    shooter.StopWarmupSound();
                    shooter.StopContinousSound();
                }
            }
            catch { }
        }

        private void Unmute()
        {
            foreach (var behaviour in _muted)
                if (behaviour != null)
                    behaviour.enabled = true;
            _muted.Clear();
        }

        public void PushSnapshot(float time, Vector2 pos, Vector2 vel, float rot, Vector2 aim)
        {
            _lastSnapshotReceivedAt = Time.unscaledTime;
            _timing.Observe(time);
            AimDirection = aim;
            if (_rb != null)
                Core.InstrumentationCounters.EntitySnapshotCorrection(Vector2.Distance(_rb.position, pos));
            // Interpolation needs an ascending timeline; an authority handoff or clock
            // re-anchor can step the mapped time slightly backwards — clamp, don't corrupt.
            if (_buffer.Count > 0 && time <= _buffer[_buffer.Count - 1].Time)
                time = _buffer[_buffer.Count - 1].Time + 0.001f;
            _buffer.Add(new Snap { Time = time, Pos = pos, Vel = vel, Rot = rot });
            if (_buffer.Count > 20) _buffer.RemoveRange(0, _buffer.Count - 20);
        }

        /// <summary>Clocks and trajectories from different simulators are not interpolatable.
        /// Drop the old owner's tail before accepting the first snapshot after a handoff.</summary>
        public void ResetSnapshots()
        {
            _buffer.Clear();
            _timing.Reset();
        }

        private void FixedUpdate()
        {
            if (_rb == null || _buffer.Count == 0) return; // frozen at its last safe pose
            float renderTime = Time.unscaledTime - _timing.Delay;
            var last = _buffer[_buffer.Count - 1];
            if (SnapshotAge > 2f)
            {
                // Stream-starved replicas stay frozen and passive. Letting hundreds fall into
                // terrain here caused the observed 60k-80k collision callbacks per second.
                Core.InstrumentationCounters.StarvedPuppetFrame();
                return;
            }

            Snap a = _buffer[0], b = last;
            for (int i = _buffer.Count - 1; i >= 1; i--)
            {
                if (_buffer[i - 1].Time <= renderTime)
                {
                    a = _buffer[i - 1];
                    b = _buffer[i];
                    break;
                }
            }
            float span = Mathf.Max(0.0001f, b.Time - a.Time);
            float t = Mathf.Clamp01((renderTime - a.Time) / span);
            Vector2 target = Vector2.LerpUnclamped(a.Pos, b.Pos, t);
            if (renderTime > b.Time)
            {
                _timing.NoteUnderrun();
                target = b.Pos + b.Vel * Mathf.Min(renderTime - b.Time, 0.3f);
            }

            float correction = Vector2.Distance(_rb.position, target);
            if (correction > HardSnapDistance)
            {
                TeleportWithChildren(_rb, target);
                Core.InstrumentationCounters.EntityHardSnap();
            }
            else
            {
                // Direct rb.position writes bypass Rigidbody2D's render interpolation and show
                // every fixed-step edge. MovePosition/MoveRotation preserve smooth presentation.
                _rb.MovePosition(target);
            }
            _rb.linearVelocity = Vector2.LerpUnclamped(a.Vel, b.Vel, t);
            _rb.MoveRotation(Mathf.LerpAngle(a.Rot, b.Rot, t));
        }

        /// <summary>Hard-move a root body AND its articulated child bodies by the same delta.
        /// Multi-part enemies (a swimmer's jointed tail) have child Rigidbody2Ds that only local
        /// physics moves — a root-only teleport strands them at their old world position and the
        /// joint stretches across the gap (seen live: a fish reclaimed after a dormancy commit,
        /// body at the committed pose, tail still at its pre-commit spot). Carrying the children
        /// by the root's delta preserves their relative pose, exactly like a fresh spawn.</summary>
        internal static void TeleportWithChildren(Rigidbody2D root, Vector2 target)
        {
            Vector2 delta = target - root.position;
            root.position = target;
            if (delta.sqrMagnitude < 0.0001f) return;
            foreach (var child in root.GetComponentsInChildren<Rigidbody2D>(true))
                if (child != null && child != root) child.position += delta;
        }
    }
}
