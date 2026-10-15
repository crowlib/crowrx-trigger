using System;
using System.Collections.Generic;
using CrowRx.Pool.Collections;

namespace CrowRx.Trigger
{
    /// <summary>같은 대상 형식을 사용하는 자식 조건에 대상을 전달하는 그룹이다.</summary>
    /// <typeparam name="TTarget">자식과 공유할 대상의 형식이다.</typeparam>
    public class ConditionGroupShareTarget<TTarget> : ConditionGroup, IConditionTargetSharable<TTarget>
    {
        private TTarget? _target;
        private bool _settingTarget;

        private readonly struct TargetSnapshot
        {
            public readonly IConditionTargetSharable<TTarget> Condition;
            public readonly TTarget? Target;

            public TargetSnapshot(IConditionTargetSharable<TTarget> condition)
            {
                Condition = condition;
                Target = condition.Target;
            }
        }

        /// <summary>자식에게 공유할 대상을 가져오거나 설정한다. 전달에 실패하면 이전 값을 복구한다.</summary>
        /// <remarks>활성화와 검사가 모두 끝난 뒤 변경한다. 복구에도 실패하면 원래 오류와 복구 오류를 함께 전달한다.</remarks>
        /// <exception cref="InvalidOperationException">그룹이 활성화 또는 검사 중이거나, 잘못된 그래프 또는 대상 변경 재진입이 발견됐다.</exception>
        /// <exception cref="AggregateException">대상 전달과 이전 값 복구가 모두 실패했다.</exception>
        public virtual TTarget? Target
        {
            get => _target;
            set
            {
                EnsureChecksAreStopped();
                ValidateConditionGraph();
                SetTargetOfCondition(value);
            }
        }

        /// <summary>새 자식의 대상 형식이 같으면 초기화 전에 그룹의 대상을 전달한다.</summary>
        /// <param name="condition">추가할 자식 조건이다.</param>
        protected override void OnConditionAdding(ICondition condition)
        {
            if (_settingTarget)
            {
                throw new InvalidOperationException("Conditions cannot be added during a target update.");
            }

            if (condition is IConditionTargetSharable<TTarget> conditionTargetSharable)
            {
                conditionTargetSharable.Target = Target;
            }

            base.OnConditionAdding(condition);
        }

        /// <summary>조건 그래프를 검증하고 대상을 전달한 뒤 자식들을 초기화한다.</summary>
        protected override void OnInitialize()
        {
            ValidateConditionGraph();
            SetTargetOfCondition(Target);

            base.OnInitialize();
        }

        private void SetTargetOfCondition(TTarget? value)
        {
            if (_settingTarget)
            {
                throw new InvalidOperationException("Reentrant target updates are not allowed.");
            }

            _settingTarget = true;

            try
            {
                if (Conditions is null || Conditions.Count == 0)
                {
                    _target = value;
                    return;
                }

                using PooledList<TargetSnapshot> snapshots = ListPool<TargetSnapshot>.Get();
                using PooledList<(IConditionTargetSharable<TTarget> Condition, int SnapshotEnd)> children = ListPool<(IConditionTargetSharable<TTarget> Condition, int SnapshotEnd)>.Get();

                if (Conditions is not null)
                {
                    using PooledList<ICondition> conditions = ListPool<ICondition>.Get(Conditions);
                    foreach (ICondition condition in conditions)
                    {
                        if (condition is IConditionTargetSharable<TTarget> child)
                        {
                            CaptureTargets(condition, snapshots);
                            children.Add((child, snapshots.Count));
                        }
                    }
                }

                TTarget? previousTarget = _target;
                int restoreCount = 0;
                _target = value;

                try
                {
                    foreach ((IConditionTargetSharable<TTarget> Condition, int SnapshotEnd) child in children)
                    {
                        // Setter가 값을 변경한 뒤 실패해도 해당 자식의 이전 상태를 복구한다.
                        restoreCount = child.SnapshotEnd;
                        child.Condition.Target = value;
                    }
                }
                catch (Exception updateError)
                {
                    _target = previousTarget;

                    List<Exception>? failures = null;
                    for (int i = 0; i < restoreCount; ++i)
                    {
                        TargetSnapshot snapshot = snapshots[i];
                        try
                        {
                            snapshot.Condition.Target = snapshot.Target;
                        }
                        catch (Exception restoreError)
                        {
                            failures ??= new List<Exception> { updateError };
                            failures.Add(restoreError);
                        }
                    }

                    if (failures is not null)
                    {
                        throw new AggregateException("Target update failed and one or more previous targets could not be restored.", failures);
                    }

                    throw;
                }
            }
            finally
            {
                _settingTarget = false;
            }
        }

        private static void CaptureTargets(ICondition condition, List<TargetSnapshot> snapshots)
        {
            using PooledStack<ICondition> pending = StackPool<ICondition>.Get();
            pending.Push(condition);

            while (pending.Count > 0)
            {
                ICondition current = pending.Pop();
                if (current is ConditionGroupShareTarget<TTarget> group)
                {
                    group.EnsureChecksAreStopped();
                }
                if (current is IConditionTargetSharable<TTarget> sharable)
                {
                    snapshots.Add(new TargetSnapshot(sharable));
                }
                if (current is ConditionGroup childGroup && childGroup.Conditions is not null)
                {
                    // 부모를 먼저 복구한 뒤 자식의 개별 값을 복구하는 순서로 기록한다.
                    for (int i = childGroup.Conditions.Count - 1; i >= 0; --i)
                    {
                        pending.Push(childGroup.Conditions[i]);
                    }
                }
            }
        }
    }
}
