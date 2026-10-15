namespace CrowRx.Trigger
{
    /// <summary>사용 전에 한 번 초기화할 수 있는 객체를 나타낸다.</summary>
    public interface IInitializer
    {
        /// <summary>
        /// 초기화를 성공적으로 마쳤는지 확인한다.
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// 사용에 필요한 상태를 초기화한다. 이미 초기화했다면 다시 수행하지 않는다.
        /// </summary>
        void Initialize();
    }
}
