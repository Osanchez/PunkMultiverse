using System.Collections.Generic;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Lightweight interpolation for physics props (hooked rocks, pushed debris) while their
    /// authority streams them — raw position snaps at stream rate looked like broken spring
    /// physics on peers. Unlike RemoteEntityPuppet it mutes nothing and blocks nothing, and it
    /// SELF-EXPIRES when snapshots stop, handing physics back to the local simulation with the
    /// last synced velocity.
    /// </summary>
    public sealed class PropPuppet : MonoBehaviour
    {
        private const float InterpDelay = 0.12f;
        private const float HardSnapDistance = 4f;
        private const float ExpireAfter = 1.5f;

        private struct Snap
        {
            public float Time;
            public Vector2 Pos;
            public Vector2 Vel;
            public float Rot;
        }

        private readonly List<Snap> _buffer = new List<Snap>(16);
        private Rigidbody2D _rb;
        private float _lastSnapAt;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
        }

        public void PushSnapshot(float time, Vector2 pos, Vector2 vel, float rot)
        {
            _lastSnapAt = Time.unscaledTime;
            _buffer.Add(new Snap { Time = time, Pos = pos, Vel = vel, Rot = rot });
            if (_buffer.Count > 20) _buffer.RemoveRange(0, _buffer.Count - 20);
        }

        private void FixedUpdate()
        {
            if (Time.unscaledTime - _lastSnapAt > ExpireAfter)
            {
                Destroy(this); // stream went quiet — local physics owns the prop again
                return;
            }
            if (_rb == null || _buffer.Count == 0) return;

            float renderTime = Time.unscaledTime - InterpDelay;
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
            if (renderTime > b.Time) target = b.Pos + b.Vel * Mathf.Min(renderTime - b.Time, 0.25f);

            if (Vector2.Distance(_rb.position, target) > HardSnapDistance) _rb.position = target;
            else _rb.MovePosition(target);
            _rb.linearVelocity = Vector2.LerpUnclamped(a.Vel, b.Vel, t);
            _rb.MoveRotation(Mathf.LerpAngle(a.Rot, b.Rot, t));
        }
    }
}
