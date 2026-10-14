using System.Threading;
using System.Threading.Tasks;

namespace CrowRx.Trigger
{
    /// <summary>
    /// Trigger 발동 조건의 상태.
    /// </summary>
    public enum ConditionState
    {
        /// <summary>
        /// 아직 시작되지 않음
        /// </summary>
        NotYet,

        /// <summary>
        /// 더이상 조건이 유효하지 않음
        /// </summary>
        NotAvailable,

        /// <summary>
        /// 조건을 만족함
        /// </summary>
        Satisfied,
    }

    /// <summary>
    /// Trigger 조건 기본 인터페이스
    /// </summary>
    public interface ICondition : IInitializer
    {
        /// <summary>
        /// 조건 확인이 가능한 상황인지 조회
        /// </summary>
        bool IsValid { get; }

        /// <summary>
        /// 기본 조건의 역 여부
        /// </summary>
        bool IsNot { get; }

        /// <summary>
        /// 조건이 만족하는지 조회
        /// </summary>
        bool IsSatisfied { get; }

        /// <summary>
        /// 조건 확인이 활성화 되어 있는지 여부
        /// </summary>
        bool IsActivated { get; }

        /// <summary>
        /// 조건 확인 활성화
        /// </summary>
        void Activate();

        /// <summary>
        /// 조건 확인 비활성화
        /// </summary>
        void Deactivate();

        Task CheckAsync(CancellationToken cancellationToken);
    }
}