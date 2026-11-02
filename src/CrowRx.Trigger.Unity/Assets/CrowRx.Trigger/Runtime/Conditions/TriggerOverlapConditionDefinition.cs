using System;
using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>Tests current 3D occupancy, not a latched enter/exit event.</summary>
    [Serializable, ConditionMenu("Unity Trigger 영역", "Physics 3D")]
    public sealed class TriggerOverlapConditionDefinition : ConditionDefinition
    {
        [SerializeField, Tooltip("Dedicated trigger sensor shared through activation leases.")]
        private UnityTriggerSensor sensor;

        [SerializeField, Tooltip("Target root. All descendant colliders are included.")]
        private GameObject target;

        [SerializeField, Tooltip("Optional dynamic target provider; takes precedence over Target.")]
        private TriggerTargetProvider targetProvider;

        [SerializeField, Tooltip("Require the target to be outside the volume instead.")]
        private bool isNot;

        public UnityTriggerSensor Sensor
        {
            get => sensor;
            set => sensor = value;
        }

        public GameObject Target
        {
            get => target;
            set => target = value;
        }

        public TriggerTargetProvider TargetProvider
        {
            get => targetProvider;
            set => targetProvider = value;
        }

        public bool IsNot
        {
            get => isNot;
            set => isNot = value;
        }

        protected override ICondition CreateRuntimeCondition() => new RuntimeCondition(sensor, target, targetProvider, isNot);

        private sealed class RuntimeCondition : UpdateCondition, IConditionTargetSharable<GameObject>
        {
            private readonly UnityTriggerSensor _sensor;
            private readonly TriggerTargetProvider _provider;
            private readonly bool _usesProvider;
            private IDisposable _lease;

            public GameObject Target { get; set; }

            public RuntimeCondition(UnityTriggerSensor sensor, GameObject target, TriggerTargetProvider provider, bool isNot) : base(isNot)
            {
                _sensor = sensor;
                Target = target;
                _provider = provider;
                _usesProvider = provider;
            }

            public override bool IsValid => _sensor && _sensor.isActiveAndEnabled && _sensor.IsConfigured &&
                                            (_usesProvider ? _provider && _provider.isActiveAndEnabled : Target);

            public override bool IsSatisfied
            {
                get
                {
                    if (!IsActivated || !IsValid)
                    {
                        return false;
                    }

                    GameObject current = _usesProvider ? _provider.Target : Target;
                    return current && current.activeInHierarchy && (_sensor.Contains(current) != IsNot);
                }
            }

            protected override void OnActivate() => _lease = _sensor.Acquire();

            protected override void OnDeactivate()
            {
                _lease?.Dispose();
                _lease = null;
            }
        }
    }
}