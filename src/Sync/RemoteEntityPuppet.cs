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
        // AimAction subclasses are muted by base type below — left running they keep steering
        // the barrel (AimInRandomDirectionAction literally at random) against the synced aim.
        private static readonly string[] MutedTypes =
            { "AIAgent", "Vision", "UnitMovement", "Seeker", "Shooter", "StateMachine", "PushMovement", "SwimmingMovement", "ChargerRam" };

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

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _barrels = GetComponentsInChildren<BarrelTransform>(true);
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

        private void OnEnable()
        {
            foreach (var behaviour in GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == null || !behaviour.enabled) continue;
                if (behaviour is AimAction)
                {
                    behaviour.enabled = false;
                    _muted.Add(behaviour);
                    continue;
                }
                var tn = behaviour.GetType().Name;
                for (int i = 0; i < MutedTypes.Length; i++)
                {
                    if (tn == MutedTypes[i])
                    {
                        behaviour.enabled = false;
                        _muted.Add(behaviour);
                        break;
                    }
                }
            }
        }

        private void OnDisable() => Unmute();
        private void OnDestroy() => Unmute();

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
            if (Time.unscaledTime - last.Time > 2f) { _rb.linearVelocity = Vector2.zero; return; }

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
