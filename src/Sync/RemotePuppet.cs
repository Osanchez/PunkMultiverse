using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Attached to a remote player's ship. Mutes all local control/AI of the ship and drives its
    /// Rigidbody2D from a 100 ms interpolation buffer of network snapshots. The ship stays a real
    /// Ship (enemies target it, collisions work) but its simulation truth lives on its owner.
    /// </summary>
    public sealed class RemotePuppet : MonoBehaviour
    {
        private const float InterpDelay = 0.1f;
        private const float HardSnapDistance = 3f;
        private const float StaleAfter = 1.5f;

        public byte Slot;
        public Vector2 AimDirection { get; private set; }

        private struct Snap
        {
            public float Time;
            public Vector2 Pos;
            public Vector2 Vel;
            public float Rot;
        }

        private readonly List<Snap> _buffer = new List<Snap>(32);
        private Rigidbody2D _rb;
        private BarrelTransform[] _barrels;
        private ParticleSystem[] _boostParticles;
        private bool _boosting;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
        }

        private void Start()
        {
            Neuter();
            _barrels = GetComponentsInChildren<BarrelTransform>(true);
            var boost = new List<ParticleSystem>();
            foreach (var ps in GetComponentsInChildren<ParticleSystem>(true))
                if (ps.name.IndexOf("boost", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    boost.Add(ps);
            _boostParticles = boost.ToArray();
            StartCoroutine(RemoveCameraTargets());
        }

        /// <summary>Owner's boost flag from the ship snapshot — drives the boost particles.</summary>
        public void SetBoosting(bool boosting)
        {
            if (boosting == _boosting || _boostParticles == null) return;
            _boosting = boosting;
            foreach (var ps in _boostParticles)
            {
                if (ps == null) continue;
                if (boosting) ps.Play(true);
                else ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        // Turrets track the owner's aim (their real barrels do this from input; ours is muted).
        private void LateUpdate()
        {
            if (_barrels == null || AimDirection.sqrMagnitude < 0.01f) return;
            float angle = Mathf.Atan2(AimDirection.y, AimDirection.x) * Mathf.Rad2Deg;
            foreach (var barrel in _barrels)
                if (barrel != null)
                    barrel.transform.rotation = Quaternion.Euler(0, 0, angle);
        }

        /// <summary>Disable local input/physics-driving components; replication owns this body.</summary>
        private void Neuter()
        {
            foreach (var input in GetComponentsInChildren<PlayerInput>(true)) input.enabled = false;
            foreach (var shipInput in GetComponentsInChildren<ShipInput>(true)) shipInput.enabled = false;
            foreach (var movement in GetComponentsInChildren<ShipMovement>(true)) movement.enabled = false;
            // A puppet must not vacuum the local player's per-client drops or trigger interactions.
            foreach (var collector in GetComponentsInChildren<LootCollector>(true)) collector.enabled = false;
            foreach (var interactor in GetComponentsInChildren<Interactor>(true)) interactor.enabled = false;
        }

        // Ship camera targets get (re-)registered by various game systems over the ship's life;
        // sweep forever so the local camera only ever frames the local ship.
        private IEnumerator RemoveCameraTargets()
        {
            bool warned = false;
            var wait = new WaitForSeconds(1f);
            while (true)
            {
                try
                {
                    var cam = Com.LuisPedroFonseca.ProCamera2D.ProCamera2D.Instance;
                    if (cam != null)
                    {
                        for (int i = cam.CameraTargets.Count - 1; i >= 0; i--)
                        {
                            var t = cam.CameraTargets[i].TargetTransform;
                            if (t != null && (t == transform || t.IsChildOf(transform)))
                                cam.RemoveCameraTarget(t);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    if (!warned)
                    {
                        warned = true;
                        Plugin.Log.LogWarning($"[Puppet] camera target removal failed: {e.Message}");
                    }
                }
                yield return wait;
            }
        }

        public void PushSnapshot(float time, Vector2 pos, Vector2 vel, float rot, Vector2 aim)
        {
            AimDirection = aim;
            _buffer.Add(new Snap { Time = time, Pos = pos, Vel = vel, Rot = rot });
            if (_buffer.Count > 30) _buffer.RemoveRange(0, _buffer.Count - 30);
        }

        private void FixedUpdate()
        {
            if (_rb == null || _buffer.Count == 0) return;

            float renderTime = Time.unscaledTime - InterpDelay;
            var last = _buffer[_buffer.Count - 1];

            if (Time.unscaledTime - last.Time > StaleAfter)
            {
                _rb.linearVelocity = Vector2.zero; // owner went quiet — freeze in place
                return;
            }

            // Find the two snapshots bracketing renderTime.
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
            Vector2 targetPos = Vector2.LerpUnclamped(a.Pos, b.Pos, t);
            if (renderTime > b.Time) // extrapolate briefly on late packets
                targetPos = b.Pos + b.Vel * Mathf.Min(renderTime - b.Time, 0.25f);

            if (Vector2.Distance(_rb.position, targetPos) > HardSnapDistance)
                _rb.position = targetPos;
            else
                _rb.MovePosition(targetPos);
            _rb.linearVelocity = Vector2.LerpUnclamped(a.Vel, b.Vel, t);
            _rb.MoveRotation(Mathf.LerpAngle(a.Rot, b.Rot, t));
        }
    }
}
