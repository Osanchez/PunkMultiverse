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
    // Runs before default-order components so our snapshot write to ShipMovement.flyDirection lands
    // before the VFX readers (EngineParticle/HoverParticles) sample it. Input callbacks fire before
    // any Update, so a stray local-input write to flyDirection would otherwise leak the LOCAL
    // player's movement onto this puppet's engine trail for a frame — this makes our write win.
    [DefaultExecutionOrder(-50)]
    public sealed class RemotePuppet : MonoBehaviour
    {
        internal static float VisualDelay => 1.35f / Mathf.Max(1f, NetConfig.ShipStateHz.Value);
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
        private readonly AdaptiveSnapshotTiming _timing = new AdaptiveSnapshotTiming(0.025f, 0.12f, 0.025f);
        private Rigidbody2D _rb;
        private ShipMovement _movement;
        private Aimer[] _aimers;
        private BarrelTransform[] _barrels; // only barrels no Aimer owns — written directly
        private Crosshair _crosshair;
        private bool _boosting;
        private bool _frozenStale;
        private float _savedGravityScale;
        private bool _instrumentationCounted;

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
            // The ship's Aimer stays enabled and rotates its barrel toward Aimer.targetPosition
            // every frame — a direct barrel.Direction write loses to it (Aimer.Update runs after
            // us). Feed the Aimer instead; only barrels no Aimer owns are written directly.
            _aimers = GetComponentsInChildren<Aimer>(true);
            var owned = new HashSet<BarrelTransform>();
            foreach (var aimer in _aimers)
                if (aimer != null && aimer.barrel != null) owned.Add(aimer.barrel);
            var free = new List<BarrelTransform>();
            foreach (var barrel in GetComponentsInChildren<BarrelTransform>(true))
                if (!owned.Contains(barrel)) free.Add(barrel);
            _barrels = free.ToArray();
            _crosshair = GetComponentInChildren<Crosshair>(true);
            StartCoroutine(RemoveCameraTargets());
        }

        private void OnEnable()
        {
            if (_instrumentationCounted) return;
            _instrumentationCounted = true;
            Core.InstrumentationCounters.RemoteShipAdded();
        }

        private void OnDisable()
        {
            RemoveInstrumentationCount();
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
            RemoveInstrumentationCount();
            if (_boostLoopHandle.HasValue)
            {
                try { AudioManager.Stop(_boostLoopHandle.Value); } catch { }
                _boostLoopHandle = null;
            }
        }

        private void RemoveInstrumentationCount()
        {
            if (!_instrumentationCounted) return;
            _instrumentationCounted = false;
            Core.InstrumentationCounters.RemoteShipRemoved();
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

            if (AimDirection.sqrMagnitude < 0.01f) return;
            // Drive the vanilla Aimer with a world target along the owner's aim — its own
            // rotation-speed smoothing then turns the turret exactly like it does for a local
            // player. (An un-fed Aimer keeps rotating toward a stale target, which both fought
            // our writes and made puppet turrets wander on their own.)
            if (_aimers != null)
                foreach (var aimer in _aimers)
                    if (aimer != null)
                        aimer.AimAt((Vector2)aimer.transform.position + AimDirection.normalized * 20f);
            if (_barrels == null) return;
            foreach (var barrel in _barrels)
                if (barrel != null)
                    barrel.Direction = AimDirection;
        }

        // The ship prefab's Crosshair is a WORLD-SPACE sprite parked at Aimer.TargetPosition —
        // feeding the puppet's Aimer (above) floats the remote player's crosshair on everyone's
        // screen. Vanilla Ship.Update re-asserts crosshair.Visible EVERY frame (from the control
        // action map, which stays enabled on puppets), so this must run in LateUpdate to land
        // after it deterministically — a plain Update write is a script-order coin flip.
        private void LateUpdate()
        {
            if (_crosshair != null && _crosshair.Visible) _crosshair.Visible = false;
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
            _timing.Observe(time);
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

            float renderTime = Time.unscaledTime - _timing.Delay;
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
            {
                _timing.NoteUnderrun();
                targetPos = b.Pos + b.Vel * Mathf.Min(renderTime - b.Time, 0.25f);
            }

            if (Vector2.Distance(_rb.position, targetPos) > HardSnapDistance)
                RemoteEntityPuppet.TeleportWithChildren(_rb, targetPos);
            else
                _rb.MovePosition(targetPos);
            _rb.linearVelocity = Vector2.LerpUnclamped(a.Vel, b.Vel, t);
            _rb.MoveRotation(Mathf.LerpAngle(a.Rot, b.Rot, t));
        }
    }
}
