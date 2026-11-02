using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>A dedicated 3D trigger volume, enabled only while conditions use it.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("CrowRx/Trigger/Unity Trigger Sensor")]
    public sealed class UnityTriggerSensor : MonoBehaviour
    {
        [SerializeField, Tooltip("Dedicated Box, Sphere or Capsule trigger on this GameObject. Do not assign a blocking collider.")]
        private Collider volume;

        [SerializeField, Tooltip("Layers accepted by this sensor, in addition to the physics collision matrix.")]
        private LayerMask targetLayers = ~0;

        private readonly HashSet<Collider> _contacts = new();
        private readonly List<Collider> _staleContacts = new();
        private Collider[] _overlaps = new Collider[16];
        private Rigidbody _body;
        private int _users;

        public Collider Volume => volume;
        public int ActiveUsers => _users;

        public bool IsConfigured => volume && volume.gameObject == gameObject && volume.isTrigger &&
                                    volume is BoxCollider or SphereCollider or CapsuleCollider &&
                                    _body;

        private void Awake()
        {
            TryGetComponent(out _body);

            if (!volume)
            {
                TryGetComponent(out volume);
            }

            if (volume && volume.gameObject == gameObject)
            {
                volume.enabled = false;
            }
        }

        private void OnEnable()
        {
            if (_users > 0 && IsConfigured)
            {
                EnableAndReconcile();
            }
        }

        private void OnDisable()
        {
            _contacts.Clear();

            if (volume && volume.gameObject == gameObject)
            {
                volume.enabled = false;
            }
        }

        /// <summary>Configure before starting any condition. The volume must have an attached physics body.</summary>
        public void Configure(Collider triggerVolume, LayerMask layers)
        {
            if (_users != 0)
            {
                throw new InvalidOperationException("Cannot configure a sensor while conditions use it.");
            }

            // attachedRigidbody may still be null while an inactive hierarchy is being assembled.
            if (!triggerVolume || triggerVolume.gameObject != gameObject || !triggerVolume.isTrigger ||
                triggerVolume is not (BoxCollider or SphereCollider or CapsuleCollider) || !TryGetComponent(out _body))
            {
                throw new ArgumentException("Assign a dedicated primitive trigger with an attached Rigidbody on the sensor GameObject.", nameof(triggerVolume));
            }

            volume = triggerVolume;
            targetLayers = layers;
            volume.enabled = false;
            _contacts.Clear();
        }

        /// <summary>Acquire from a condition's activation hook; dispose from its deactivation hook.</summary>
        public IDisposable Acquire()
        {
            if (!isActiveAndEnabled || !IsConfigured)
            {
                throw new InvalidOperationException("The trigger sensor is not active or configured.");
            }

            if (_users == 0)
            {
                EnableAndReconcile();
            }

            _users++;
            return new Lease(this);
        }

        private void Release()
        {
            _users--;
            if (_users == 0)
            {
                if (volume)
                {
                    volume.enabled = false;
                }

                _contacts.Clear();
            }
        }

        /// <summary>Rebuild current overlaps after activation or an explicit volume-size change.</summary>
        public void Reconcile()
        {
            _contacts.Clear();
            if (!isActiveAndEnabled || !IsConfigured || !volume.enabled)
            {
                return;
            }

            Physics.SyncTransforms();
            Bounds bounds = volume.bounds;
            PhysicsScene physicsScene = gameObject.scene.GetPhysicsScene();
            int count;
            while (true)
            {
                count = physicsScene.OverlapBox(bounds.center, bounds.extents, _overlaps,
                    Quaternion.identity, targetLayers, QueryTriggerInteraction.Collide);
                if (count < _overlaps.Length)
                {
                    break;
                }

                Array.Resize(ref _overlaps, checked(_overlaps.Length * 2));
            }

            for (int i = 0; i < count; i++)
            {
                Collider other = _overlaps[i];
                if (IsOverlapping(other))
                {
                    _contacts.Add(other);
                }
            }

            Array.Clear(_overlaps, 0, _overlaps.Length);
        }

        /// <summary>Matches the target root or its descendants. Multiple colliders count as one target.</summary>
        public bool Contains(GameObject target)
        {
            if (_users == 0 || !isActiveAndEnabled || !volume || !volume.enabled || !target || !target.activeInHierarchy)
            {
                return false;
            }

            bool found = false;
            Transform targetTransform = target.transform;
            foreach (Collider contact in _contacts)
            {
                // Disabled/destroyed colliders do not necessarily send an exit callback.
                if (!IsOverlapping(contact))
                {
                    _staleContacts.Add(contact);
                    continue;
                }

                if (contact.transform.IsChildOf(targetTransform))
                {
                    found = true;
                }
            }

            foreach (Collider contact in _staleContacts)
            {
                _contacts.Remove(contact);
            }

            _staleContacts.Clear();
            return found;
        }

        private bool IsOverlapping(Collider other)
        {
            if (!other || other == volume || !other.enabled || !other.gameObject.activeInHierarchy ||
                (targetLayers.value & (1 << other.gameObject.layer)) == 0 ||
                Physics.GetIgnoreLayerCollision(gameObject.layer, other.gameObject.layer) ||
                Physics.GetIgnoreCollision(volume, other))
            {
                return false;
            }

            Transform otherTransform = other.transform;
            return Physics.ComputePenetration(volume, transform.position, transform.rotation,
                other, otherTransform.position, otherTransform.rotation, out _, out _);
        }

        private void EnableAndReconcile()
        {
            volume.enabled = true;
            try
            {
                Reconcile();
            }
            catch
            {
                volume.enabled = false;
                _contacts.Clear();
                throw;
            }
        }

        private void OnTriggerEnter(Collider other) => Track(other);
        private void OnTriggerStay(Collider other) => Track(other);

        private void Track(Collider other)
        {
            if (_users > 0 && isActiveAndEnabled && volume && volume.enabled && IsOverlapping(other))
            {
                _contacts.Add(other);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (!volume || !volume.enabled || !IsOverlapping(other))
            {
                _contacts.Remove(other);
            }
        }

        private sealed class Lease : IDisposable
        {
            private UnityTriggerSensor _sensor;

            public Lease(UnityTriggerSensor sensor) => _sensor = sensor;

            public void Dispose()
            {
                UnityTriggerSensor sensor = _sensor;
                _sensor = null;
                if (sensor)
                {
                    sensor.Release();
                }
            }
        }
    }
}