using System;
using System.Collections.Generic;
using CrowRx.Pool.Collections;

namespace CrowRx.Trigger
{
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

        /// <summary>자식에게 공유할 대상. 전달 실패 시 이전 값을 복구하고, 복구 실패도 함께 전달한다.</summary>
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
                using PooledList<(IConditionTargetSharable<TTarget> Condition, int SnapshotEnd)> children =
                    ListPool<(IConditionTargetSharable<TTarget> Condition, int SnapshotEnd)>.Get();
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
                    foreach (var child in children)
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
                        try { snapshot.Condition.Target = snapshot.Target; }
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
