using System.Threading.Tasks;

namespace CrowRx.Trigger
{
    /// <summary>구독의 종료 완료를 비동기로 기다릴 수 있는 리스너를 나타낸다.</summary>
    public interface IAsyncListener : IListener
    {
        /// <summary>현재 구독을 중지하고 검사 정리 및 비활성화가 끝날 때까지 기다린다.</summary>
        /// <returns>현재 구독의 종료가 끝나면 완료되는 작업이다. 예약된 다음 구독의 종료는 기다리지 않는다.</returns>
        Task StopListenAsync();
    }
}
