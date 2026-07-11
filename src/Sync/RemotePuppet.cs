using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
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
        // Two snapshot intervals of buffer at the configured rate — enough for one lost packet.
        private static float InterpDelay => 2f / Mathf.Max(1f, NetConfig.ShipStateHz.Value);
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
        private ShipMovement _movement;
        private BarrelTransform[] _barrels;
        private bool _boosting;
        private bool _frozenStale;
        private float _savedGravityScale;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _movement = GetComponent<ShipMovement>();
            // Snapshots apply in the 50 Hz physics step — without render interpolation the
            // ship visibly stutters on high-refresh displays ("the other player is lagging").
            if (_rb != null) _rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        }

        private void Start()
        {
            Neuter();
            _barrels = GetComponentsInChildren<BarrelTransform>(true);
            StartCoroutine(RemoveCameraTargets());
        }

        private int? _boostLoopHandle;

        /// <summary>Owner's boost flag from the ship snapshot — drives the vanilla boost VFX
        /// and audio. ShipMovement.SetBoosted works while the component is disabled (Awake
        /// populated its particle list), and nothing fights us since its Update isn't running.
        /// Sounds are played manually: vanilla plays them in StartBoosting/StopBoosting, which
        /// puppets never run.</summary>
        public void SetBoosting(bool boosting)
        {
            if (boosting == _boosting) return;
            _boosting = boosting;
            if (_movement == null) return;
            try
            {
                _movement.SetBoosted(boosting);
                // SetBoosted also arms boost ram damage — a puppet's is cosmetic only.
                if (_movement.impactDamage != null) _movement.impactDamage.enabled = false;
                if (boosting && _movement.boostStartParticle != null) _movement.boostStartParticle.Play();

                if (boosting)
                {
                    if (!string.IsNullOrEmpty(_movement.boostSfx))
                        AudioManager.PlaySfx(_movement.boostSfx, (Vector2)transform.position);
                    if (!string.IsNullOrEmpty(_movement.boostLoopedSfx))
                        _boostLoopHandle = AudioManager.PlaySfx(_movement.boostLoopedSfx, transform); // tracks the ship
                }
                else if (_boostLoopHandle.HasValue)
                {
                    AudioManager.Stop(_boostLoopHandle.Value);
                    _boostLoopHandle = null;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Puppet] boost VFX failed: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            if (_boostLoopHandle.HasValue)
            {
                try { AudioManager.Stop(_boostLoopHandle.Value); } catch { }
                _boostLoopHandle = null;
            }
        }

        private Vector2 _moveInput;

        /// <summary>Owner's movement input from the ship snapshot — feeds the vanilla engine /
        /// hover VFX and engine audio, which read ShipMovement.flyDirection every frame even
        /// while ShipMovement itself is disabled.</summary>
        public void SetMoveInput(Vector2 move) => _moveInput = move;

        /// <summary>Owner's hover state — HoverParticles/ShipEngineSound read this field live.</summary>
        public void SetHovering(bool hovering)
        {
            if (_movement != null) _movement.isHovering = hovering;
        }

        // Turrets track the owner's aim: write the game's own BarrelTransform.Direction and let
        // its LateUpdate apply the visible rotation (rotating the transform ourselves would race it).
        private void Update()
        {
            if (_movement != null) _movement.flyDirection = _moveInput;

            if (_barrels == null || AimDirection.sqrMagnitude < 0.01f) return;
            foreach (var barrel in _barrels)
                if (barrel != null)
                    barrel.Direction = AimDirection;
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
            // ShipVirtualJoyInput writes ShipMovement.flyDirection from LOCAL input every frame —
            // on a puppet that mirrors the local player's thrust VFX onto the wrong ship.
            // (ShipControlActionMap feeds flyDirection too, but it hangs off PlayerInput, which
            // is disabled above.)
            foreach (var joy in GetComponentsInChildren<ShipVirtualJoyInput>(true)) joy.enabled = false;
            // Puppets spawn on the start station and may register hover before this runs.
            ScrubInteractions(this);
        }

        /// <summary>Remove a puppet ship's Interactor from every Interactable it registered
        /// with. Hover state only ever exits via Interactor.Update — disabled on puppets — so
        /// without this a station stays in its "shop available" pose forever (most visibly
        /// after the player dies on it).</summary>
        public static void ScrubInteractions(Component shipRoot)
        {
            if (shipRoot == null) return;
            try
            {
                foreach (var interactor in shipRoot.GetComponentsInChildren<Interactor>(true))
                {
                    var inRange = interactor.InteractablesInRange;
                    if (inRange == null) continue;
                    foreach (var interactable in new List<Interactable>(inRange))
                    {
                        if (interactable == null) continue;
                        try { interactable.OnHoverExited(interactor); } catch { }
                    }
                    inRange.Clear();
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Puppet] interaction scrub failed: {e.Message}");
            }
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
                            if (t != null && (t == transform || t.IsChildOf(transform))
                                && UI.SpectatorCam.SpectatedSlot != Slot)
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
            // Ascending timeline guard — a clock re-anchor may step mapped time backwards.
            if (_buffer.Count > 0 && time <= _buffer[_buffer.Count - 1].Time)
                time = _buffer[_buffer.Count - 1].Time + 0.001f;
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
                // Owner went quiet — hard freeze. Zeroing velocity per-frame still lets gravity
                // integrate a slow sink; kill gravity until snapshots resume instead.
                if (!_frozenStale)
                {
                    _frozenStale = true;
                    _savedGravityScale = _rb.gravityScale;
                    _rb.gravityScale = 0f;
                    _rb.linearVelocity = Vector2.zero;
                    _rb.angularVelocity = 0f;
                }
                return;
            }
            if (_frozenStale)
            {
                _frozenStale = false;
                _rb.gravityScale = _savedGravityScale;
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
