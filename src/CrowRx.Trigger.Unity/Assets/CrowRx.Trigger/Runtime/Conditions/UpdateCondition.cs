using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace CrowRx.Trigger.Unity
{
    /// <summary>Unity의 다음 Update에서 검사를 이어가는 실행 조건의 기반이다.</summary>
    public abstract class UpdateCondition : Condition
    {
        /// <summary>조건의 반전 설정을 적용한다.</summary>
        /// <param name="isNot">만족 결과를 반전할지 나타낸다.</param>
        protected UpdateCondition(bool isNot) { IsNot = isNot; }

        /// <summary>다음 프레임의 Update까지 기다린다. 완료 후 IsSatisfied를 확인한다.</summary>
        /// <param name="cancellationToken">대기를 중지할 토큰이다.</param>
        /// <returns>Update에 도달하거나 취소되면 완료되는 작업이다.</returns>
        public override Task CheckAsync(CancellationToken cancellationToken)
            => UniTask.NextFrame(PlayerLoopTiming.Update, cancellationToken, cancelImmediately: true).AsTask();
    }
}
