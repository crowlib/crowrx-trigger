namespace CrowRx.Trigger
{
    /// <summary>조건을 검사하고 만족했을 때 동작을 실행하는 구독을 관리한다.</summary>
    /// <remarks>시작, 중지, 즉시 확인은 같은 실행 컨텍스트에서 호출한다.</remarks>
    public interface IListener : IInitializer
    {
        /// <summary>구독할 조건을 가져온다.</summary>
        ICondition? Condition { get; }
        /// <summary>조건이 없거나 유효하지 않을 때 오류 알림 없이 구독을 끝낼지 확인한다.</summary>
        bool IsIgnoreNotAvailableCondition { get; }
        /// <summary>조건을 한 번 만족한 뒤 자동으로 다시 구독하지 않을지 확인한다.</summary>
        bool IsOneShot { get; }

        /// <summary>구독을 시작한다. 종료 중이라면 정리가 끝난 뒤 시작하도록 예약한다.</summary>
        void StartListen();
        /// <summary>구독 중지를 요청한다. 비동기 정리의 완료까지 기다리지는 않는다.</summary>
        void StopListen();

        /// <summary>현재 만족 여부를 동기적으로 확인한다. 만족하거나 조건이 유효하지 않으면 구독 중지를 요청한다.</summary>
        void CheckConditionsImmediately();
    }
}
