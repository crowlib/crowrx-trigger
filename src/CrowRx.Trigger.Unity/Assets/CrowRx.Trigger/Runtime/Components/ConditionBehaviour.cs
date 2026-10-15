using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>실행 수명과 독립적으로 조건 설정을 제공하는 컴포넌트의 기반이다.</summary>
    public abstract class ConditionBehaviour : MonoBehaviour
    {
        /// <summary>이 컴포넌트가 제공하는 설정이다. 실행 상태를 저장하지 않는다.</summary>
        public abstract ConditionDefinition Definition { get; }

        /// <summary>컴포넌트의 현재 설정을 복사한 실행 조건을 만든다.</summary>
        /// <returns>다른 리스너와 상태를 공유하지 않는 새 조건이다.</returns>
        public ICondition CreateCondition() => Definition.CreateCondition();
    }
}
