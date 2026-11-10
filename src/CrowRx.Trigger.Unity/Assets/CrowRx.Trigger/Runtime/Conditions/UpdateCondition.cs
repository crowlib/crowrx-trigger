using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>Unity의 Update에서 조건의 상태 변화를 기다리는 실행 조건의 기반이다.</summary>
    public abstract class UpdateCondition : Condition
    {
        private readonly PlayerLoopTiming _updateTiming;

        private bool _hasObservation;
        private bool _wasValid;
        private bool _wasSatisfied;

        /// <summary>조건의 반전 설정을 적용한다.</summary>
        /// <param name="isNot">만족 결과를 반전할지 나타낸다.</param>
        /// <param name="updateTiming">업데이트 타이밍이다.</param>
        protected UpdateCondition(bool isNot, PlayerLoopTiming updateTiming)
        {
            IsNot = isNot;
            _updateTiming = updateTiming;
        }

        protected UpdateCondition(bool isNot) : this(isNot, PlayerLoopTiming.Update) { }

        /// <summary>최초 상태 또는 이전 검사 이후의 유효성·만족 상태 변화를 다음 프레임 이후 Update에서 전달한다.</summary>
        /// <param name="cancellationToken">대기를 중지할 토큰이다.</param>
        /// <returns>최초 상태를 관찰하거나 상태가 바뀌거나 취소되면 완료되는 작업이다.</returns>
        /// <remarks>상태가 유지되면 같은 작업으로 대기한다. 완료 후 IsValid와 IsSatisfied를 확인한다.</remarks>
        public override Task CheckAsync(CancellationToken cancellationToken) =>
            UniTask.WaitUntil(
                (Condition: this, Frame: Time.frameCount),
                static state => Time.frameCount != state.Frame && state.Condition.ObserveStateChange(),
                _updateTiming,
                cancellationToken,
                cancelImmediately: true)
                .AsTask();

        /// <summary>새 활성화의 첫 검사에서 현재 상태를 전달하도록 관찰 기록을 초기화한다.</summary>
        /// <remarks>파생 클래스가 재정의하면 base.OnActivate()도 호출해야 한다.</remarks>
        protected override void OnActivate()
        {
            base.OnActivate();

            _hasObservation = false;
        }

        private bool ObserveStateChange()
        {
            bool valid = IsValid;
            bool satisfied = valid && IsSatisfied;
            if (_hasObservation && _wasValid == valid && _wasSatisfied == satisfied)
            {
                return false;
            }

            _hasObservation = true;
            _wasValid = valid;
            _wasSatisfied = satisfied;

            return true;
        }
    }
}
