using System.Collections.Generic;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Physics-prop replica. Two modes:
    /// - Hold (ownership model, §6): the prop is simulated by exactly one remote peer (or is
    ///   Dormant). The body is held kinematically at its canonical pose and interpolated from
    ///   the owner's snapshots; the hold persists until this machine becomes the owner
    ///   (ApplyOwnership destroys the component, restoring local physics).
    /// - Legacy transient (no Hold): interpolate while snapshots flow, then SELF-EXPIRE and
    ///   hand physics back to the local simulation with the last synced velocity.
    /// </summary>
    public sealed class PropPuppet : MonoBehaviour
    {
        // Two snapshot intervals of buffer at the configured rate — enough for one lost packet.
        private const float HardSnapDistance = 4f;
        private const float ExpireAfter = 1.5f;

        /// <summary>Persistent kinematic hold for a remote/dormant-owned prop (never expires).</summary>
        public bool Hold;

        private struct Snap
        {
            public float Time;
            public Vector2 Pos;
            public Vector2 Vel;
            public float Rot;
        }

        private readonly List<Snap> _buffer = new List<Snap>(16);
        private readonly AdaptiveSnapshotTiming _timing = new AdaptiveSnapshotTiming(0.05f, 0.22f, 0.05f);
        private Rigidbody2D _rb;
        private float _lastSnapAt;
        private bool _held;
        private RigidbodyType2D _savedBodyType;

        private RigidbodyInterpolation2D _savedInterpolation;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            if (_rb != null)
            {
                _savedInterpolation = _rb.interpolation;
                _rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            }
        }

        /// <summary>Freeze the body at its current pose: a non-owner must not run local physics
        /// on a shared prop (that was the "falling containers only the host sees" divergence).
        /// Restored on destroy — i.e. when this machine becomes the simulator.</summary>
        public void EnsureHeld()
        {
            if (_rb == null || _held) return;
            _held = true;
            _savedBodyType = _rb.bodyType;
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            _rb.bodyType = RigidbodyType2D.Kinematic;
        }

        private void OnDestroy()
        {
            if (_rb == null) return;
            _rb.interpolation = _savedInterpolation;
            if (_held) _rb.bodyType = _savedBodyType;
        }

        public void PushSnapshot(float time, Vector2 pos, Vector2 vel, float rot)
        {
            _lastSnapAt = Time.unscaledTime;
            _timing.Observe(time);
            // Ascending timeline guard — a clock re-anchor may step mapped time backwards.
            if (_buffer.Count > 0 && time <= _buffer[_buffer.Count - 1].Time)
                time = _buffer[_buffer.Count - 1].Time + 0.001f;
            _buffer.Add(new Snap { Time = time, Pos = pos, Vel = vel, Rot = rot });
            if (_buffer.Count > 20) _buffer.RemoveRange(0, _buffer.Count - 20);
        }

        public void ResetSnapshots()
        {
            _buffer.Clear();
            _timing.Reset();
        }

        private void FixedUpdate()
        {
            if (!Hold && Time.unscaledTime - _lastSnapAt > ExpireAfter)
            {
                Destroy(this); // legacy transient: stream went quiet — local physics resumes
                return;
            }
            if (_rb == null || _buffer.Count == 0) return; // held: frozen at canonical pose

            float renderTime = Time.unscaledTime - _timing.Delay;
            Snap a = _buffer[0], b = _buffer[_buffer.Count - 1];
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
                target = b.Pos + b.Vel * Mathf.Min(renderTime - b.Time, 0.25f);
            }

            if (Vector2.Distance(_rb.position, target) > HardSnapDistance) RemoteEntityPuppet.TeleportWithChildren(_rb, target);
            else _rb.MovePosition(target);
            _rb.linearVelocity = Vector2.LerpUnclamped(a.Vel, b.Vel, t);
            _rb.MoveRotation(Mathf.LerpAngle(a.Rot, b.Rot, t));
        }
    }
}
