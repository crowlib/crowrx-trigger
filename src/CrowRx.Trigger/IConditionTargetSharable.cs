namespace CrowRx.Trigger
{
    /// <summary>외부에서 전달받은 대상을 사용하는 조건을 나타낸다.</summary>
    /// <typeparam name="TTarget">조건이 사용할 대상의 형식이다.</typeparam>
    public interface IConditionTargetSharable<TTarget> : ICondition
    {
        /// <summary>조건이 사용할 대상을 가져오거나 설정한다.</summary>
        TTarget? Target { get; set; }
    }
}
