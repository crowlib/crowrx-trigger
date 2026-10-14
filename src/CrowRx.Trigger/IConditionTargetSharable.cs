namespace CrowRx.Trigger
{
    public interface IConditionTargetSharable<TTarget> : ICondition
    {
        TTarget? Target { get; set; }
    }
}