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
            "PushMovement", "SwimmingMovement", "ChargerRam",
            "ShootComplexAction", "ShootAction", "ActivateShooterAction", "SelfDestructAction",
            "ProjectileDispenser", "WaitForTargetAction", "MoveAwayFromTargetAction",
            "MoveToPositionAction", "PushSelfAction", "StopAction", "ApplyTorqueAction",
            "ReduceAngularVelocityAction", "RepeateChildrenAction", "ForgetTargetAction",
            // ChangeAnimatorParamAction is deliberately NOT muted: it only sets animator bools
            // (pure cosmetics), and with AI states replicated it keeps puppet animations correct.
        };

        private const float InterpDelay = 0.12f;
        private const float HardSnapDistance = 4f;

        public int NetId;
        public Vector2 AimDirection { get; private set; }

        private struct Snap
        {
            public float Time;
            public Vector2 Pos;
            public Vector2 Vel;
            public float Rot;
        }

        private readonly List<Snap> _buffer = new List<Snap>(16);
        private readonly List<Behaviour> _muted = new List<Behaviour>();
        private Rigidbody2D _rb;
        private BarrelTransform[] _barrels;

        private RigidbodyInterpolation2D _savedInterpolation;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _barrels = GetComponentsInChildren<BarrelTransform>(true);
            if (_rb != null)
            {
                // Fixed-step snapshot driving needs render interpolation or the enemy stutters
                // on high-refresh displays; restored on handoff (OnDestroy) below.
                _savedInterpolation = _rb.interpolation;
                _rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            }
        }

        // The muted AI no longer steers the barrels; mirror the authority's aim through the
        // game's own BarrelTransform.Direction (kept enabled) so its LateUpdate applies the
        // visible rotation. Body facing arrives separately via the rb.rotation snapshots.
        private void Update()
        {
            if (_barrels == null || AimDirection.sqrMagnitude < 0.01f) return;
            foreach (var barrel in _barrels)
                if (barrel != null)
                    barrel.Direction = AimDirection;
        }

        private void OnEnable() => MuteNow();

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
            StopWeaponSounds();
            Unmute();
        }

        private void OnDestroy()
        {
            StopWeaponSounds();
            Unmute();
            if (_rb != null) _rb.interpolation = _savedInterpolation;
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
            AimDirection = aim;
            _buffer.Add(new Snap { Time = time, Pos = pos, Vel = vel, Rot = rot });
            if (_buffer.Count > 20) _buffer.RemoveRange(0, _buffer.Count - 20);
        }

        private void FixedUpdate()
        {
            if (_rb == null || _buffer.Count == 0) return;
            float renderTime = Time.unscaledTime - InterpDelay;
            var last = _buffer[_buffer.Count - 1];
            if (Time.unscaledTime - last.Time > 2f)
            {
                // Stream starved (owner unloaded the GameObject, dormancy, corpse): release the
                // body to local physics — pinning it per-frame fights gravity forever.
                _buffer.Clear();
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
            if (renderTime > b.Time) target = b.Pos + b.Vel * Mathf.Min(renderTime - b.Time, 0.3f);

            if (Vector2.Distance(_rb.position, target) > HardSnapDistance) _rb.position = target;
            else _rb.MovePosition(target);
            _rb.linearVelocity = Vector2.LerpUnclamped(a.Vel, b.Vel, t);
            _rb.MoveRotation(Mathf.LerpAngle(a.Rot, b.Rot, t));
        }
    }
}
