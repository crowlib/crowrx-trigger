using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using CrowRx.Pool.Collections;

namespace CrowRx.Trigger
{
    public enum ConditionCombineType
    {
        And,
        Or,
    }

    /// <summary>
    /// 여러개의 <see cref="ICondition"/> 묶음.
    /// <see cref="ConditionGroup"/>도 <see cref="ICondition"/>으로써 다른 <see cref="ConditionGroup"/>에 포함 가능하다.
    /// 포함된 <see cref="ICondition"/>중에 하나라도 <see cref="ConditionState.NotAvailable"/>이 오면 <see cref="ConditionGroup"/>이 유효하지 않은 것으로 처리한다.
    /// 검사와 조건 변경은 동일한 실행 컨텍스트에서 호출한다.
    /// </summary>
    public class ConditionGroup : Condition
    {
        #region static

        private static async Task ObserveCleanupAsync(Task cleanup)
        {
            try
            {
                await cleanup;
            }
            catch (Exception error)
            {
                LogCheckFailure(error);
            }
        }

        private static void RecordCheckFailure(ref ExceptionDispatchInfo? failure, Exception error)
        {
            if (failure is null || (failure.SourceException is OperationCanceledException && error is not OperationCanceledException))
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
            else if (!ReferenceEquals(failure.SourceException, error))
            {
                LogCheckFailure(error);
            }
        }

        private static void LogCheckFailure(Exception error)
        {
            try { Log.Exception(error); }
            catch (Exception) { } // 진단 처리 실패로 검사 정리를 중단하지 않는다.
        }

        #endregion

        private CheckSession? _checks;
        private bool _checkInProgress;
        private bool _readingValidity;
        private bool _readingSatisfaction;
        private bool _addingCondition;

        private sealed class ConditionReferenceComparer : IEqualityComparer<ICondition>
        {
            public static readonly ConditionReferenceComparer Instance = new ConditionReferenceComparer();
            public bool Equals(ICondition? x, ICondition? y) => ReferenceEquals(x, y);
            public int GetHashCode(ICondition condition) => RuntimeHelpers.GetHashCode(condition);
        }

        private sealed class CheckSession
        {
            public readonly PooledList<ICondition> Conditions;
            public readonly PooledList<Task> Tasks;
            public readonly PooledList<bool> CompletionReported;
            public readonly CancellationToken ParentToken;
            public readonly CancellationTokenSource Cancellation;
            public readonly CancellationToken Token;
            public bool IsChecking;
            public TaskCompletionSource<bool>? CheckFinished;
            public TaskCompletionSource<ExceptionDispatchInfo?>? Cleanup;

            public CheckSession(IEnumerable<ICondition> source, CancellationToken parentToken)
            {
                PooledList<ICondition>? snapshot = null;
                PooledList<bool>? reported = null;
                PooledList<Task>? tasks = null;
                CancellationTokenSource? cancellation = null;
                try
                {
                    snapshot = ListPool<ICondition>.Get(source);
                    int count = snapshot.Count;
                    reported = ListPool<bool>.Get(count);
                    for (int i = 0; i < count; ++i)
                    {
                        reported.Add(false);
                    }
                    tasks = ListPool<Task>.Get(count);
                    cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);

                    Conditions = snapshot;
                    CompletionReported = reported;
                    Tasks = tasks;
                    ParentToken = parentToken;
                    Cancellation = cancellation;
                    Token = cancellation.Token;
                }
                catch
                {
                    cancellation?.Dispose();
                    tasks?.Dispose();
                    reported?.Dispose();
                    snapshot?.Dispose();
                    throw;
                }
            }
        }

        public override bool IsValid
        {
            get
            {
                if (_readingValidity)
                {
                    return false;
                }

                _readingValidity = true;

                try
                {
                    if (Conditions is null || Conditions.Count <= 0)
                    {
                        return false;
                    }

                    for (int i = 0, count = Conditions.Count; i < count; ++i)
                    {
                        if (!(Conditions[i]?.IsValid ?? false))
                        {
                            return false;
                        }
                    }

                    return true;
                }
                finally
                {
                    _readingValidity = false;
                }
            }
        }

        public override bool IsSatisfied
        {
            get
            {
                if (_readingSatisfaction)
                {
                    throw new InvalidOperationException("Cyclic group references are not allowed.");
                }

                _readingSatisfaction = true;

                try
                {
                    if (Conditions is null)
                    {
                        return true;
                    }

                    switch (CombineType)
                    {
                        case ConditionCombineType.And:
                            {
                                for (int i = 0, count = Conditions.Count; i < count; ++i)
                                {
                                    ICondition condition = Conditions[i];
                                    if (condition is null)
                                    {
                                        continue;
                                    }

                                    if (!condition.IsSatisfied)
                                    {
                                        return IsNot;
                                    }
                                }

                                return !IsNot;
                            }

                        case ConditionCombineType.Or:
                            {
                                for (int i = 0, count = Conditions.Count; i < count; ++i)
                                {
                                    ICondition condition = Conditions[i];
                                    if (condition is null)
                                    {
                                        continue;
                                    }

                                    if (condition.IsSatisfied)
                                    {
                                        return !IsNot;
                                    }
                                }

                                return IsNot;
                            }


                        default:
                            return false;
                    }
                }
                finally
                {
                    _readingSatisfaction = false;
                }
            }
        }

        /// <summary>활성화 및 검사 중에는 목록을 변경하지 않는다. 동일 트리에서 조건 인스턴스를 중복 사용하지 않는다.</summary>
        public virtual List<ICondition>? Conditions { get; protected set; }
        public virtual ConditionCombineType CombineType { get; protected set; }

        /// <summary>조건을 추가한다. 추가 콜백 중 같은 그룹의 재귀 추가는 허용하지 않는다.</summary>
        public void AddCondition(ICondition? condition)
        {
            if (condition is null)
            {
                return;
            }

            if (_addingCondition)
            {
                throw new InvalidOperationException("Reentrant condition additions are not allowed.");
            }

            _addingCondition = true;

            try
            {
                EnsureChecksAreStopped();
                ValidateConditionGraph(condition);

                OnConditionAdding(condition);
                EnsureChecksAreStopped();
                ValidateConditionGraph(condition);

                if (IsInitialized)
                {
                    condition.Initialize();
                    EnsureChecksAreStopped();
                    ValidateConditionGraph(condition);
                }

                Conditions ??= new List<ICondition>();
                Conditions.Add(condition);
            }
            finally
            {
                _addingCondition = false;
            }
        }

        /// <summary>자식을 목록에 추가하고 초기화하기 전에 필요한 설정을 전달한다.</summary>
        protected virtual void OnConditionAdding(ICondition condition)
        {
        }

        /// <summary>실행 중인 검사에 사용되는 조건 설정의 변경을 거부한다.</summary>
        protected void EnsureChecksAreStopped()
        {
            if (_checks is not null || IsActivated)
            {
                throw new InvalidOperationException("Conditions cannot be changed while the group is active or checks are pending.");
            }
        }

        /// <summary>같은 인스턴스의 중복, 순환 참조 및 null 자식 조건을 검사한다.</summary>
        protected void ValidateConditionGraph(ICondition? additionalCondition = null)
        {
            using PooledHashSet<ICondition> visited = HashSetPool<ICondition>.Get(0, ConditionReferenceComparer.Instance);
            using PooledStack<ICondition> pending = StackPool<ICondition>.Get();
            pending.Push(this);
            if (additionalCondition is not null)
            {
                pending.Push(additionalCondition);
            }

            while (pending.Count > 0)
            {
                ICondition condition = pending.Pop();
                if (!visited.Add(condition))
                {
                    throw new InvalidOperationException("Duplicate condition instances and cyclic group references are not allowed.");
                }

                if (condition is ConditionGroup group && group.Conditions is not null)
                {
                    foreach (ICondition child in group.Conditions)
                    {
                        pending.Push(child ?? throw new InvalidOperationException("Conditions cannot contain null."));
                    }
                }
            }
        }

        public void AddConditions(in IEnumerable<ICondition>? conditions)
        {
            if (conditions is null)
            {
                return;
            }

            foreach (ICondition condition in conditions)
            {
                AddCondition(condition);
            }
        }

        protected override void OnInitialize()
        {
            ValidateConditionGraph();
            if (Conditions is null)
            {
                return;
            }

            for (int i = 0, count = Conditions.Count; i < count; ++i)
            {
                Conditions[i]?.Initialize();
            }
        }

        protected override void OnActivate()
        {
            ValidateConditionGraph();
            if (Conditions is null)
            {
                return;
            }

            for (int i = 0, count = Conditions.Count; i < count; ++i)
            {
                ICondition condition = Conditions[i];

                if (condition?.IsValid ?? false)
                {
                    condition.Activate();
                }
            }
        }

        protected override void OnDeactivate()
        {
            // 직접 사용하는 그룹도 남아 있는 검사에 취소를 요청한다.
            // Listener는 EndChecksAsync를 기다린 후 Deactivate를 호출한다.
            _ = ObserveCleanupAsync(EndChecksAsync());

            if (Conditions is null)
            {
                return;
            }

            List<Exception>? failures = null;
            for (int i = 0, count = Conditions.Count; i < count; ++i)
            {
                try
                {
                    Conditions[i]?.Deactivate();
                }
                catch (Exception error)
                {
                    (failures ??= new List<Exception>()).Add(error);
                }
            }

            if (failures?.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            else if (failures is not null)
            {
                throw new AggregateException("One or more conditions failed to deactivate.", failures);
            }
        }

        /// <summary>
        /// 자식 검사의 완료를 알린다. 호출 사이에 새로 완료된 검사는 재시작하기 전에 먼저 알린다.
        /// 같은 토큰으로 다시 호출하면 미완료 검사는 유지하고, 이미 완료를 알린 검사만 다시 시작한다.
        /// 조건 목록은 검사 종료 전까지 유지해야 한다. 직접 사용하는 경우 사용 종료 시 StopChecksAsync를 기다린다.
        /// 활성화했던 그룹은 검사 정리 후 Deactivate를 호출한다.
        /// </summary>
        public override async Task CheckAsync(CancellationToken cancellationToken)
        {
            if (_checkInProgress)
            {
                throw new InvalidOperationException("Concurrent CheckAsync calls are not supported.");
            }

            _checkInProgress = true;
            CheckSession? checks = null;
            ExceptionDispatchInfo? failure = null;
            try
            {
                try
                {
                    if (_checks is not null && (_checks.ParentToken != cancellationToken || _checks.Cleanup is not null))
                    {
                        await EndChecksAsync();
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (_checks is null)
                    {
                        if (Conditions is null || Conditions.Count == 0)
                        {
                            return;
                        }
                        ValidateConditionGraph();
                        _checks = new CheckSession(Conditions, cancellationToken);
                    }

                    checks = _checks;
                    checks.IsChecking = true;
                    ValidateConditions(checks);
                    checks.Token.ThrowIfCancellationRequested();

                    // 호출 사이에 완료된 결과를 먼저 전달하여 다음 검사가 만족 상태를 초기화하지 않도록 한다.
                    if (ObserveCompletedChecks(checks))
                    {
                        return;
                    }

                    bool hasCompletedCheck = false;
                    for (int i = 0; i < checks.Conditions.Count; ++i)
                    {
                        checks.Token.ThrowIfCancellationRequested();
                        if (i < checks.Tasks.Count)
                        {
                            Task previous = checks.Tasks[i];
                            bool isCompleted = previous.IsCompleted;
                            if (!isCompleted || !checks.CompletionReported[i])
                            {
                                hasCompletedCheck = hasCompletedCheck || isCompleted;
                                continue;
                            }
                            // 첫 관찰 이후에 완료된 실패도 덮어쓰기 전에 확인한다.
                            previous.GetAwaiter().GetResult();
                        }

                        ICondition condition = checks.Conditions[i]
                            ?? throw new InvalidOperationException("Conditions cannot contain null.");
                        Task task = condition.CheckAsync(checks.Token)
                            ?? throw new InvalidOperationException("CheckAsync must not return a null Task.");
                        if (i == checks.Tasks.Count)
                        {
                            checks.Tasks.Add(task);
                        }
                        else
                        {
                            checks.Tasks[i] = task;
                        }
                        checks.CompletionReported[i] = false;
                        hasCompletedCheck = hasCompletedCheck || task.IsCompleted;
                    }


                    // 완료된 검사가 있으면 대기용 Task를 만들지 않고 아래에서 모든 결과를 확인한다.
                    if (!hasCompletedCheck)
                    {
                        if (checks.Tasks.Count == 1)
                        {
                            await checks.Tasks[0];
                        }
                        else
                        {
                            await Task.WhenAny(checks.Tasks);
                        }
                    }
                    checks.Token.ThrowIfCancellationRequested();
                    ValidateConditions(checks);
                    ObserveCompletedChecks(checks);
                }
                catch (Exception error)
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                }
                finally
                {
                    if (checks is not null)
                    {
                        checks.IsChecking = false;
                        checks.CheckFinished?.TrySetResult(true);
                    }
                }

                if (failure is not null)
                {
                    try { await EndChecksAsync(); }
                    catch (Exception error) { RecordCheckFailure(ref failure, error); }
                    failure?.Throw();
                }
            }
            finally
            {
                _checkInProgress = false;
            }
        }

        private void ValidateConditions(CheckSession checks)
        {
            if (Conditions is null || Conditions.Count != checks.Conditions.Count)
            {
                throw new InvalidOperationException("Conditions cannot be changed while checks are active.");
            }

            for (int i = 0; i < checks.Conditions.Count; ++i)
            {
                if (!ReferenceEquals(Conditions[i], checks.Conditions[i]))
                {
                    throw new InvalidOperationException("Conditions cannot be changed while checks are active.");
                }
            }
        }

        private static bool ObserveCompletedChecks(CheckSession checks)
        {
            bool hasNewCompletion = false;
            for (int i = 0; i < checks.Tasks.Count; ++i)
            {
                Task task = checks.Tasks[i];
                if (!task.IsCompleted)
                {
                    continue;
                }

                // 즉시 완료되는 자식이 다른 자식의 실패를 가리지 않도록 완료된 모든 결과를 확인한다.
                task.GetAwaiter().GetResult();
                if (!checks.CompletionReported[i])
                {
                    checks.CompletionReported[i] = true;
                    hasNewCompletion = true;
                }
            }
            return hasNewCompletion;
        }

        /// <summary>현재 자식 및 중첩 그룹 검사에 취소를 요청하고 모든 검사 정리를 기다린다.</summary>
        /// <remarks>
        /// Activate 없이 시작한 검사도 정리한다. 활성화 상태는 유지하므로 비활성화가 필요하면 완료 후 Deactivate를 호출한다.
        /// 중복 종료 요청은 같은 세션의 정리를 기다리며, 정리 실패는 반환한 Task로 전달한다.
        /// 시작, 종료 및 조건 변경은 동일한 실행 컨텍스트에서 호출한다.
        /// </remarks>
        public Task StopChecksAsync() => EndChecksAsync();

        internal async Task EndChecksAsync()
        {
            CheckSession? checks = _checks;
            if (checks is null)
            {
                return;
            }

            if (checks.Cleanup is null)
            {
                // Cancel의 콜백이 재진입하더라도 같은 종료 작업을 기다린다.
                checks.Cleanup = new TaskCompletionSource<ExceptionDispatchInfo?>();
                _ = CleanupChecksAsync(checks);
            }

            ExceptionDispatchInfo? failure = await checks.Cleanup.Task;
            failure?.Throw();
        }

        private async Task CleanupChecksAsync(CheckSession checks)
        {
            ExceptionDispatchInfo? failure = null;
            try
            {
                Task checkFinished = checks.IsChecking
                    ? (checks.CheckFinished = new TaskCompletionSource<bool>()).Task
                    : Task.CompletedTask;

                try { checks.Cancellation.Cancel(); }
                catch (Exception error) { RecordCheckFailure(ref failure, error); }

                await checkFinished;

                Task allChecks = Task.WhenAll(checks.Tasks);
                try { await allChecks; }
                catch (OperationCanceledException) when (allChecks.IsCanceled) { }
                catch (Exception) when (allChecks.IsFaulted)
                {
                    foreach (Exception error in allChecks.Exception!.Flatten().InnerExceptions)
                    {
                        RecordCheckFailure(ref failure, error);
                    }
                }

                // 자식 그룹의 CheckAsync가 이미 완료되어도 내부 검사는 실행 중일 수 있다.
                foreach (ICondition condition in checks.Conditions)
                {
                    if (condition is ConditionGroup group)
                    {
                        try { await group.EndChecksAsync(); }
                        catch (Exception error) { RecordCheckFailure(ref failure, error); }
                    }
                }
            }
            catch (Exception error)
            {
                RecordCheckFailure(ref failure, error);
            }
            finally
            {
                checks.Tasks.Dispose();
                checks.Conditions.Dispose();
                checks.CompletionReported.Dispose();
                checks.Cancellation.Dispose();
                if (ReferenceEquals(_checks, checks))
                {
                    _checks = null;
                }
                checks.Cleanup!.TrySetResult(failure);
            }
        }
    }
}
