using System;
using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>직접 지정한 기준점과 타겟 사이의 월드 거리를 검사하는 설정이다.</summary>
    [Serializable, ConditionMenu("거리")]
    public sealed class DistanceConditionDefinition : ConditionDefinition
    {
        [SerializeField] private Transform origin;
        [SerializeField] private GameObject target;
        [SerializeField, Min(0)] private float maxDistance = 5f;
        [SerializeField] private bool isNot;

        /// <summary>거리의 기준점이다. 리스너의 위치로 자동 대체하지 않는다.</summary>
        public Transform Origin
        {
            get => origin;
            set => origin = value;
        }

        /// <summary>거리를 검사할 타겟이다. 공유 그룹은 실행 객체의 이 값을 대체한다.</summary>
        public GameObject Target
        {
            get => target;
            set => target = value;
        }

        /// <summary>만족으로 판정할 최대 거리다. 경계값을 포함하며 0 이상인 유한한 값을 지정한다.</summary>
        public float MaxDistance
        {
            get => maxDistance;
            set => maxDistance = value;
        }

        /// <summary>거리 판정을 반전할지 가져오거나 설정한다.</summary>
        public bool IsNot
        {
            get => isNot;
            set => isNot = value;
        }

        /// <inheritdoc/>
        protected override ICondition CreateRuntimeCondition() => new RuntimeDistanceCondition(Origin, Target, MaxDistance, IsNot);

        private sealed class RuntimeDistanceCondition : UpdateCondition, IConditionTargetSharable<GameObject>
        {
            private readonly Transform _origin;
            private readonly float _distance;

            public GameObject Target { get; set; }

            internal RuntimeDistanceCondition(Transform origin, GameObject target, float distance, bool isNot) : base(isNot)
            {
                _origin = origin;
                Target = target;
                _distance = distance;
            }

            public override bool IsValid => _origin && Target && _distance >= 0 && !float.IsInfinity(_distance) && !float.IsNaN(_distance);

            public override bool IsSatisfied => IsValid && (_origin.position - Target.transform.position).sqrMagnitude <= (double)_distance * _distance != IsNot;
        }
    }
}