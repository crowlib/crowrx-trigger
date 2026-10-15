using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>시간 경과 조건 설정을 제공한다. 컴포넌트를 켜는 것으로 타이머를 시작하지 않는다.</summary>
    [AddComponentMenu("CrowRx/Trigger/Time Condition")]
    public sealed class TimeConditionBehaviour : ConditionBehaviour
    {
        [SerializeField] private TimeConditionDefinition settings = new();

        /// <summary>시간 조건의 편집 설정이다. 이미 생성한 실행 조건에는 변경이 반영되지 않는다.</summary>
        public TimeConditionDefinition Settings => settings;
        /// <inheritdoc/>
        public override ConditionDefinition Definition => settings;
    }
}
