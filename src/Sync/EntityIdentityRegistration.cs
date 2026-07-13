using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>Tracks one concrete GameObject lifetime for a shared entity identity. The game may
    /// overlap stream-out and stream-in objects for the same EntityData; EnemySync chooses exactly
    /// one canonical lifetime and makes every other lifetime permanently inert.</summary>
    internal sealed class EntityIdentityRegistration : MonoBehaviour
    {
        private int _netId;
        private SavableEntity _entity;
        private bool _initialized;
        internal int NetId => _netId;
        internal SavableEntity Entity => _entity;
        internal bool IsCanonical { get; private set; }

        internal void Initialize(int netId, SavableEntity entity)
        {
            if (_initialized) return;
            _initialized = true;
            _netId = netId;
            _entity = entity;
        }

        internal void MakeCanonical() => IsCanonical = true;

        internal void MakeDuplicateInert()
        {
            if (!IsCanonical && GetComponent<DuplicateEntityInert>() != null) return;
            IsCanonical = false;
            var inert = GetComponent<DuplicateEntityInert>();
            if (inert == null) inert = gameObject.AddComponent<DuplicateEntityInert>();
            inert.Initialize(_netId, _entity);
        }

        private void OnDestroy()
        {
            if (_initialized) EnemySync.UnregisterLive(this);
        }
    }

    /// <summary>Non-destructive quarantine for a superseded entity GameObject. Destroying it in a
    /// spawn callback fights the segment loader; leaving it active duplicates AI, colliders,
    /// weapons, damage and visuals. Quarantine hides it and removes it from simulation while the
    /// game remains free to finish its normal deferred unload.</summary>
    internal sealed class DuplicateEntityInert : MonoBehaviour
    {
        private int _netId;
        private SavableEntity _entity;
        private bool _initialized;
        private float _quarantinedAt;

        internal int NetId => _netId;
        internal SavableEntity Entity => _entity;
        internal float Age => _initialized ? Mathf.Max(0f, Time.unscaledTime - _quarantinedAt) : 0f;

        internal void Initialize(int netId, SavableEntity entity)
        {
            _netId = netId;
            _entity = entity;
            if (!_initialized)
            {
                _initialized = true;
                _quarantinedAt = Time.unscaledTime;
                Core.InstrumentationCounters.DuplicateLifetimeActivated();
                EnemySync.ScheduleDuplicateRetirement(this);
            }
            EnforceNow();
        }

        // Called once on quarantine. Keeping this component free of Update/LateUpdate is
        // essential: a pathological run can create hundreds of superseded lifetimes before the
        // loader finishes its deferred unload.
        internal void EnforceNow()
        {
            foreach (var behaviour in GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == null || !behaviour.enabled) continue;
                if (behaviour == this || behaviour is EntityIdentityRegistration) continue;
                behaviour.enabled = false;
            }
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
                if (renderer != null) renderer.enabled = false;
            foreach (var audio in GetComponentsInChildren<AudioSource>(true))
                if (audio != null) audio.Stop();
            var rb = GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.simulated = false;
            }
        }

        private void OnDestroy()
        {
            if (_initialized) Core.InstrumentationCounters.DuplicateLifetimeReleased();
        }
    }
}
