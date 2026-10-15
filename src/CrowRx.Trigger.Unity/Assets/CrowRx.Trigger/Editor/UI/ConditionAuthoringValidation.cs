using System.Collections.Generic;

namespace CrowRx.Trigger.Unity.Editor
{
    // 편집 중에는 사용자 정의 실행 조건의 생성자나 실행 훅을 호출하지 않는다.
    internal static class ConditionAuthoringValidation
    {
        internal static string Message(ConditionDefinition definition)
            => Visit(definition, default, new HashSet<ConditionDefinition>());

        private static string Visit(ConditionDefinition definition, SharedTargetPreview inherited, HashSet<ConditionDefinition> path)
        {
            if (definition == null) return "조건 설정이나 타입이 누락됐어.";
            if (!path.Add(definition)) return "조건 설정에 순환 참조가 있어.";
            try
            {
                if (definition is ComponentConditionReference reference)
                    return reference.Component ? Visit(reference.Component.Definition, inherited, path) : "조건 컴포넌트를 지정해.";
                if (definition is ConditionGroupDefinition group)
                {
                    if (group.Conditions.Count == 0) return "그룹에 조건을 하나 이상 추가해.";
                    if (group.CombineType != ConditionCombineType.And && group.CombineType != ConditionCombineType.Or) return "그룹의 결합 방식이 잘못됐어.";
                    var next = definition is SharedTargetGroupDefinition shared
                        ? new SharedTargetPreview(inherited.Inherited ? inherited.Target : shared.Target) : default;
                    foreach (var child in group.Conditions)
                    { var error = Visit(child, next, path); if (error != null) return error; }
                }
                else if (definition is TimeConditionDefinition time)
                {
                    if (!NonNegative(time.Duration)) return "대기 시간은 0 이상인 유한한 값으로 지정해.";
                    if (time.TimeSource != ConditionTimeSource.GameTime && time.TimeSource != ConditionTimeSource.UnscaledTime) return "시간 기준이 잘못됐어.";
                }
                else if (definition is ActiveStateConditionDefinition active)
                {
                    if (!(inherited.Inherited ? inherited.Target : active.Target)) return "활성 상태를 검사할 타겟을 지정해.";
                }
                else if (definition is DistanceConditionDefinition distance)
                {
                    if (!distance.Origin) return "거리 조건의 기준점을 직접 지정해.";
                    if (!(inherited.Inherited ? inherited.Target : distance.Target)) return "거리를 검사할 타겟을 지정해.";
                    if (!NonNegative(distance.MaxDistance)) return "최대 거리는 0 이상인 유한한 값으로 지정해.";
                }
                return null;
            }
            finally { path.Remove(definition); }
        }
        private static bool NonNegative(float value) => value >= 0 && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
