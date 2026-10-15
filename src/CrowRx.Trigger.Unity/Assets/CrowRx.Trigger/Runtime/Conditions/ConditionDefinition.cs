using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>인스펙터에 저장할 조건 설정이다. 실행 상태는 생성한 조건이 별도로 보관한다.</summary>
    [Serializable]
    public abstract class ConditionDefinition
    {
        /// <summary>설정을 복사한 독립적인 실행 조건을 만든다.</summary>
        /// <returns>초기화와 활성화 전의 새 조건이다.</returns>
        /// <exception cref="InvalidOperationException">설정에 순환 참조나 누락된 항목이 있다.</exception>
        public ICondition CreateCondition() => ConditionTree.Create(this);

        /// <summary>이 설정을 사용하는 실행 조건을 만든다. 실행 중에는 설정 객체를 다시 읽지 않는다.</summary>
        /// <returns>새 실행 조건이다.</returns>
        protected abstract ICondition CreateRuntimeCondition();

        internal ICondition Build() => CreateRuntimeCondition()
            ?? throw new InvalidOperationException("A condition definition returned null.");
    }

    /// <summary>조건 추가 팝업에서 사용할 이름과 분류를 지정한다.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ConditionMenuAttribute : Attribute
    {
        /// <summary>팝업에 표시할 이름이다.</summary>
        public string Name { get; }
        /// <summary>팝업에서 묶을 분류다.</summary>
        public string Category { get; }

        /// <summary>조건 메뉴 정보를 만든다.</summary>
        /// <param name="name">표시할 이름이다.</param>
        /// <param name="category">분류 이름이다.</param>
        public ConditionMenuAttribute(string name, string category = "조건")
        {
            Name = name;
            Category = category;
        }
    }

    internal static class ConditionTree
    {
        internal static ICondition Create(ConditionDefinition definition)
        {
            Validate(definition, new HashSet<ConditionDefinition>());
            return definition.Build();
        }

        internal static ICondition CreateRoot(List<ConditionDefinition> definitions, ConditionCombineType combine)
        {
            if (combine != ConditionCombineType.And && combine != ConditionCombineType.Or)
                throw new InvalidOperationException("The root combination type is invalid.");
            if (definitions == null || definitions.Count == 0)
                throw new InvalidOperationException("The listener needs at least one condition.");
            var path = new HashSet<ConditionDefinition>();
            foreach (var definition in definitions) Validate(definition, path);
            if (definitions.Count == 1) return definitions[0].Build();
            var children = new List<ICondition>(definitions.Count);
            foreach (var definition in definitions) children.Add(definition.Build());
            return new RuntimeGroup(children, combine, false);
        }

        private static void Validate(ConditionDefinition definition, HashSet<ConditionDefinition> path)
        {
            if (definition == null) throw new InvalidOperationException("A condition setting is missing.");
            if (!path.Add(definition)) throw new InvalidOperationException("Cyclic condition settings are not supported.");
            try
            {
                if (definition is ComponentConditionReference reference)
                {
                    if (!reference.Component) throw new InvalidOperationException("A condition component is missing.");
                    Validate(reference.Component.Definition, path);
                }
                else if (definition is ConditionGroupDefinition group)
                {
                    if (group.Conditions == null || group.Conditions.Count == 0)
                        throw new InvalidOperationException("A condition group needs at least one child.");
                    if (group.CombineType != ConditionCombineType.And && group.CombineType != ConditionCombineType.Or)
                        throw new InvalidOperationException("The group combination type is invalid.");
                    foreach (var child in group.Conditions) Validate(child, path);
                }
            }
            finally { path.Remove(definition); }
        }
    }

    internal sealed class RuntimeGroup : ConditionGroup
    {
        internal RuntimeGroup(List<ICondition> children, ConditionCombineType combine, bool isNot)
        {
            Conditions = children;
            CombineType = combine;
            IsNot = isNot;
        }
    }

    internal sealed class RuntimeSharedGroup : ConditionGroupShareTarget<GameObject>
    {
        internal RuntimeSharedGroup(List<ICondition> children, ConditionCombineType combine, bool isNot, GameObject target)
        {
            Conditions = children;
            CombineType = combine;
            IsNot = isNot;
            Target = target;
        }
    }
}
