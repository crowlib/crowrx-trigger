using System;
using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>타겟의 activeInHierarchy 상태를 검사하는 설정이다.</summary>
    [Serializable, ConditionMenu("활성 상태")]
    public sealed class ActiveStateConditionDefinition : ConditionDefinition
    {
        [SerializeField] private GameObject _target;
        [SerializeField] private bool _isNot;
        /// <summary>검사할 GameObject다. 공유 그룹에 속하면 실행 객체에 그룹 타겟이 적용된다.</summary>
        public GameObject Target { get => _target; set => _target = value; }
        /// <summary>비활성 상태를 검사하도록 만족 결과를 반전할지 가져오거나 설정한다.</summary>
        public bool IsNot { get => _isNot; set => _isNot = value; }
        /// <inheritdoc/>
        protected override ICondition CreateRuntimeCondition() => new RuntimeActiveCondition(Target, IsNot);

        private sealed class RuntimeActiveCondition : UpdateCondition, IConditionTargetSharable<GameObject>
        {
            public GameObject Target { get; set; }
            internal RuntimeActiveCondition(GameObject target, bool isNot) : base(isNot) { Target = target; }
            public override bool IsValid => Target;
            public override bool IsSatisfied => IsValid && (Target.activeInHierarchy != IsNot);
        }
    }
}
