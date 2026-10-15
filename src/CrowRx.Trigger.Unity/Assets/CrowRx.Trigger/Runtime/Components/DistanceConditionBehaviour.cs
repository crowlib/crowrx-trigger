using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>기준점을 직접 지정하는 거리 조건의 설정을 제공한다.</summary>
    [AddComponentMenu("CrowRx/Trigger/Distance Condition")]
    public sealed class DistanceConditionBehaviour : ConditionBehaviour
    {
        [SerializeField] private DistanceConditionDefinition settings = new();

        /// <summary>기준점과 타겟을 포함한 거리 조건의 편집 설정이다.</summary>
        public DistanceConditionDefinition Settings => settings;
        /// <inheritdoc/>
        public override ConditionDefinition Definition => settings;
    }
}
