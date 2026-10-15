using System.Collections.Generic;
using CrowRx.Pool.Collections;

namespace CrowRx.Trigger.Unity.Editor
{
    // 편집 중에는 사용자 정의 실행 조건의 생성자나 실행 훅을 호출하지 않는다.
    internal static class ConditionAuthoringValidation
    {
        internal static string Message(ConditionDefinition definition)
        {
            using PooledHashSet<ConditionDefinition> path = HashSetPool<ConditionDefinition>.Get();
            return Visit(definition, default, path);
        }

        private static string Visit(ConditionDefinition definition, SharedTargetPreview inherited, HashSet<ConditionDefinition> path)
        {
            if (definition == null)
            {
                return "조건 설정이나 타입이 누락됐어.";
            }

            if (!path.Add(definition))
            {
                return "조건 설정에 순환 참조가 있어.";
            }

            try
            {
                switch (definition)
                {
                    case ComponentConditionReference reference:
                        return reference.Component ? Visit(reference.Component.Definition, inherited, path) : "조건 컴포넌트를 지정해.";
                    case ConditionGroupDefinition group when group.Conditions.Count == 0:
                        return "그룹에 조건을 하나 이상 추가해.";
                    case ConditionGroupDefinition group when group.CombineType != ConditionCombineType.And && group.CombineType != ConditionCombineType.Or:
                        return "그룹의 결합 방식이 잘못됐어.";
                    case ConditionGroupDefinition group:
                    {
                        SharedTargetPreview next =
                            definition is SharedTargetGroupDefinition shared
                                ? new SharedTargetPreview(inherited.Inherited ? inherited.Target : shared.Target)
                                : default;
                        foreach (ConditionDefinition child in group.Conditions)
                        {
                            string error = Visit(child, next, path);
                            if (error is not null)
                            {
                                return error;
                            }
                        }

                        break;
                    }
                    case TimeConditionDefinition time when !NonNegative(time.Duration):
                        return "대기 시간은 0 이상인 유한한 값으로 지정해.";
                    case TimeConditionDefinition time when time.TimeSource != ConditionTimeSource.GameTime && time.TimeSource != ConditionTimeSource.UnscaledTime:
                        return "시간 기준이 잘못됐어.";
                    case ActiveStateConditionDefinition active when !(inherited.Inherited ? inherited.Target : active.Target):
                        return "활성 상태를 검사할 타겟을 지정해.";
                    case DistanceConditionDefinition distance when !distance.Origin:
                        return "거리 조건의 기준점을 직접 지정해.";
                    case DistanceConditionDefinition distance when !(inherited.Inherited ? inherited.Target : distance.Target):
                        return "거리를 검사할 타겟을 지정해.";
                    case DistanceConditionDefinition distance when !NonNegative(distance.MaxDistance):
                        return "최대 거리는 0 이상인 유한한 값으로 지정해.";
                }

                return null;
            }
            finally
            {
                path.Remove(definition);
            }
        }

        private static bool NonNegative(float value) => value >= 0 && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}