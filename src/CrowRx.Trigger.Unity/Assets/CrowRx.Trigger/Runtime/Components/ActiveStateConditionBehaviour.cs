using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>GameObject 활성 상태 조건의 설정을 제공한다.</summary>
    [AddComponentMenu("CrowRx/Trigger/Active State Condition")]
    public sealed class ActiveStateConditionBehaviour : ConditionBehaviour
    {
        [SerializeField] private ActiveStateConditionDefinition settings = new();

        /// <summary>활성 상태 조건의 편집 설정이다.</summary>
        public ActiveStateConditionDefinition Settings => settings;
        /// <inheritdoc/>
        public override ConditionDefinition Definition => settings;
    }
}
