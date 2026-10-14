namespace CrowRx.Trigger
{
    public interface IListener : IInitializer
    {
        ICondition? Condition { get; }
        bool IsIgnoreNotAvailableCondition { get; }
        bool IsOneShot { get; }

        void StartListen();
        void StopListen();

        void CheckConditionsImmediately();
    }
}
