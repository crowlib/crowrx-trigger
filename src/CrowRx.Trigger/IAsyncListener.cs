using System.Threading.Tasks;

namespace CrowRx.Trigger
{
    /// <summary>현재 구독의 종료 완료를 비동기로 기다릴 수 있는 리스너.</summary>
    public interface IAsyncListener : IListener
    {
        /// <summary>현재 구독을 중지하고 검사 정리 및 비활성화가 끝날 때까지 기다린다.</summary>
        Task StopListenAsync();
    }
}
