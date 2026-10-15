using System.Threading;
using System.Threading.Tasks;

namespace CrowRx.Trigger
{
    /// <summary>
    /// 리스너가 조건을 확인한 결과를 나타낸다.
    /// </summary>
    public enum ConditionState
    {
        /// <summary>
        /// 만족 여부가 확정되지 않았거나 검사가 취소된 상태다.
        /// </summary>
        NotYet,

        /// <summary>
        /// 조건 확인 중 오류가 발생한 상태다.
        /// </summary>
        NotAvailable,

        /// <summary>
        /// 조건을 만족한 상태다.
        /// </summary>
        Satisfied,
    }

    /// <summary>
    /// 트리거 조건의 상태와 비동기 검사를 제공한다.
    /// </summary>
    /// <remarks>상태 조회와 수명 주기 호출은 같은 실행 컨텍스트에서 수행한다.</remarks>
    public interface ICondition : IInitializer
    {
        /// <summary>
        /// 현재 조건을 검사할 수 있는지 확인한다.
        /// </summary>
        bool IsValid { get; }

        /// <summary>
        /// 원래 조건의 만족 결과를 반전하는지 확인한다.
        /// </summary>
        bool IsNot { get; }

        /// <summary>
        /// 현재 조건을 만족하는지 확인한다. 반전 여부도 이 결과에 반영한다.
        /// </summary>
        bool IsSatisfied { get; }

        /// <summary>
        /// 조건이 활성화되어 있는지 확인한다.
        /// </summary>
        bool IsActivated { get; }

        /// <summary>
        /// 조건을 활성화한다. 이미 활성화했다면 다시 수행하지 않는다.
        /// </summary>
        void Activate();

        /// <summary>
        /// 조건을 비활성화한다. 이미 비활성화했다면 다시 수행하지 않는다.
        /// </summary>
        void Deactivate();

        /// <summary>조건을 비동기로 검사하고 현재 상태를 갱신한다.</summary>
        /// <param name="cancellationToken">검사 중지를 요청할 토큰이다.</param>
        /// <returns>이번 검사가 끝나면 완료되는 작업이다. 완료 자체가 조건 만족을 뜻하지는 않는다.</returns>
        /// <remarks>
        /// 완료 후 <see cref="IsSatisfied"/>를 조회한다. null 작업을 반환하지 않으며,
        /// 취소 요청 시 진행 중인 작업을 정리하고 종료해야 리스너와 그룹도 중지를 마칠 수 있다.
        /// </remarks>
        Task CheckAsync(CancellationToken cancellationToken);
    }
}
