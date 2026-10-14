namespace CrowRx.Trigger
{
    public interface IInitializer
    {
        /// <summary>
        /// 초기화 되어 있는지 여부
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// 초기화
        /// </summary>
        void Initialize();
    }
}