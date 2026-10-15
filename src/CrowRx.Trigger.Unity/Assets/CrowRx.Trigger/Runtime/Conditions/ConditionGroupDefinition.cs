using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>자식 조건의 순서를 유지하며 결합하는 그룹 설정이다.</summary>
    [Serializable, ConditionMenu("조건 그룹", "그룹")]
    public class ConditionGroupDefinition : ConditionDefinition
    {
        [SerializeField] private ConditionCombineType combineType;
        [SerializeField] private bool isNot;
        [SerializeReference] private List<ConditionDefinition> conditions = new List<ConditionDefinition>();

        /// <summary>자식을 결합할 AND 또는 OR를 가져오거나 설정한다.</summary>
        public ConditionCombineType CombineType
        {
            get => combineType;
            set => combineType = value;
        }

        /// <summary>그룹 결과를 반전할지 가져오거나 설정한다.</summary>
        public bool IsNot
        {
            get => isNot;
            set => isNot = value;
        }

        /// <summary>인스펙터에 저장할 자식 설정 목록이다. 생성한 실행 트리에는 변경이 반영되지 않는다.</summary>
        public List<ConditionDefinition> Conditions => conditions;

        /// <inheritdoc/>
        protected override ICondition CreateRuntimeCondition() => new RuntimeGroup(BuildChildren(), CombineType, IsNot);

        /// <summary>자식 설정의 순서를 유지하며 독립적인 실행 조건들을 만든다.</summary>
        /// <returns>새 자식 조건 목록이다.</returns>
        protected List<ICondition> BuildChildren()
        {
            List<ICondition> children = new(conditions.Count);
            foreach (ConditionDefinition definition in conditions)
            {
                children.Add(definition.Build());
            }

            return children;
        }
    }

    /// <summary>같은 GameObject 타겟을 자식에게 전달하는 그룹 설정이다.</summary>
    [Serializable, ConditionMenu("타겟 공유 그룹", "그룹")]
    public sealed class SharedTargetGroupDefinition : ConditionGroupDefinition
    {
        [SerializeField] private GameObject target;

        /// <summary>자식에게 전달할 타겟이다. 같은 타입의 하위 공유 그룹에도 부모 값이 전달된다.</summary>
        public GameObject Target
        {
            get => target;
            set => target = value;
        }

        /// <inheritdoc/>
        protected override ICondition CreateRuntimeCondition() => new RuntimeSharedGroup(BuildChildren(), CombineType, IsNot, Target);
    }

    /// <summary>기존 조건 컴포넌트의 설정을 참조한다. 실행 조건은 참조할 때마다 따로 만든다.</summary>
    [Serializable, ConditionMenu("컴포넌트 참조", "참조")]
    public sealed class ComponentConditionReference : ConditionDefinition
    {
        [SerializeField] private ConditionBehaviour component;

        /// <summary>설정을 제공할 조건 컴포넌트를 가져오거나 설정한다.</summary>
        public ConditionBehaviour Component
        {
            get => component;
            set => component = value;
        }

        /// <inheritdoc/>
        protected override ICondition CreateRuntimeCondition() => Component.Definition.Build();
    }
}