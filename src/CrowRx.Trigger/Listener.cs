using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CrowRx.Trigger
{
    /// <summary>
    /// 조건을 반복 검사하고 만족하면 <see cref="Success"/> 이벤트를 발생시킨다.
    /// </summary>
    /// <remarks>
    /// 시작, 중지, 조건 변경은 같은 실행 컨텍스트에서 호출한다.
    /// 성공 후에는 이전 검사 정리와 비활성화를 마친 뒤 다시 구독하며, 오류나 유효하지 않은 조건으로 종료하면 자동 재시작하지 않는다.
    /// </remarks>
    public class Listener : IAsyncListener
    {
        #region static

        private static void LogExceptionSafely(Exception exception)
        {
            try
            {
                Log.Exception(exception);
            }
            catch (Exception) // 진단 실패로 종료 작업이나 다른 구독자의 처리를 중단하지 않는다.
            {
            }
        }

        #endregion

        /// <summary>조건을 만족했을 때 호출한다. 핸들러에서 예외가 발생하면 나머지 성공 핸들러와 자동 재시작은 진행하지 않는다.</summary>
        public event Action? Success;
        /// <summary>검사, 이벤트 처리, 종료 정리 중 발생한 오류를 알린다. 한 핸들러가 실패해도 다른 오류 핸들러는 호출한다.</summary>
        public event Action<Exception>? Exception;

        private ListenerExecution? _session;
        private long _executionId;
        private int _diagnosticRequests;
        private ConditionDiagnosticTrace? _lastDiagnostics;
        private bool _isExceptionLoggerRegistered;
        private static readonly Action<Exception> ExceptionLogger = error => Log.Exception(error);

        /// <inheritdoc/>
        public bool IsInitialized { get; private set; }

        /// <summary>현재 검사 또는 정리 중인 Execution이다. Idle이면 null이다.</summary>
        public ListenerExecution? CurrentExecution => _session;
        /// <summary>마지막으로 종료한 Execution의 결과다. 새로운 실행을 시작해도 다음 종료까지 유지한다.</summary>
        public ListenerResult? LastResult { get; private set; }

        internal bool DiagnosticsRequested => _diagnosticRequests != 0;

        /// <summary>상세 Condition 진단을 요청한다. 초기화나 실행을 시작하지 않는다.</summary>
        /// <returns>요청을 해제할 때 Dispose할 객체다. 마지막 요청이 해제되면 상세 기록을 중단한다.</returns>
        public IDisposable AcquireDiagnostics()
        {
            ++_diagnosticRequests;
            return new DiagnosticsLease(this);
        }

        /// <summary>저장된 Condition 진단을 복사한다. 조건을 다시 평가하지 않는다.</summary>
        /// <param name="destination">먼저 비운 뒤 진단 정보의 복사본을 채울 목록이다.</param>
        /// <returns>복사한 항목 수다. 현재 실행이 있으면 그 실행만, Idle이면 마지막 실행만 복사한다.</returns>
        /// <exception cref="ArgumentNullException">목록이 null이다.</exception>
        public int CopyConditionDiagnostics(List<ConditionDiagnostic> destination)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            ConditionDiagnosticTrace? trace = _session is not null ? _session.Diagnostics : _lastDiagnostics;
            return trace?.CopyTo(destination) ?? 0;
        }

        /// <summary>구독할 조건을 가져온다. 파생 클래스에서 재정의할 때 구독 중에는 같은 조건을 유지한다.</summary>
        public virtual ICondition? Condition { get; }
        /// <summary>조건이 없거나 유효하지 않을 때 오류 알림 없이 종료할지 가져오거나 설정한다. 기본값은 false다.</summary>
        public virtual bool IsIgnoreNotAvailableCondition { get; set; }
        /// <summary>조건을 한 번 만족한 뒤 자동 재시작하지 않을지 가져오거나 설정한다. 기본값은 false다.</summary>
        public virtual bool IsOneShot { get; set; }

        /// <summary>파생 클래스에서 조건을 제공할 리스너를 만든다.</summary>
        protected Listener()
        {
        }

        /// <summary>지정한 조건을 구독할 리스너를 만든다.</summary>
        /// <param name="condition">구독할 조건이다.</param>
        public Listener(ICondition condition)
        {
            Condition = condition;
        }

        /// <summary>리스너와 조건을 한 번 초기화한다. 시작과 즉시 확인 시에도 자동으로 호출한다.</summary>
        public void Initialize()
        {
            if (IsInitialized)
            {
                return;
            }

            OnInitialize();

            ICondition? condition = Condition;
            if (ConditionObservation.Current?.Execution == _session && _session is not null)
                _session.Condition = condition;
            condition?.Initialize();

            IsInitialized = true;
        }

        /// <summary>구독을 시작한다. 구독 중이면 유지하고, 종료 중이면 정리 후 한 번 시작하도록 예약한다.</summary>
        public void StartListen()
        {
            if (_session is not null)
            {
                if (_session.Stopping)
                {
                    _session.RestartRequested = true;
                }

                return;
            }

            StartExecutionCore();
        }

        /// <summary>새 조건 감시를 시작하고 해당 Execution을 반환한다.</summary>
        /// <returns>이번 감시의 결과와 Cleanup 완료를 기다릴 핸들이다. 자동 반복은 IsOneShot 설정을 따른다.</returns>
        /// <exception cref="InvalidOperationException">이미 실행 중이거나 Cleanup 중이다.</exception>
        public ListenerExecution StartExecution()
        {
            if (_session is not null) throw new InvalidOperationException("An execution is already running or stopping.");
            return StartExecutionCore();
        }

        private ListenerExecution StartExecutionCore()
        {
            ListenerExecution session = new(this, ++_executionId);
            _session = session;
            TaskAwaiter<bool> awaiter = RunSessionAsync(session).GetAwaiter();

            awaiter.OnCompleted(() =>
            {
                bool needToRestart = false;
                try
                {
                    needToRestart = awaiter.GetResult();
                }
                catch (Exception exception)
                {
                    session.RecordError(exception, session.Phase);
                    if (session.Kind == ListenerResultKind.Canceled && !session.StopRequested)
                        session.Kind = ListenerResultKind.Faulted;
                    ExceptionProcess(exception, session);
                }

                ListenerResult result = session.Finish();
                bool ownsSession = ReferenceEquals(session, _session);
                if (ownsSession)
                {
                    _session = null;
                    LastResult = result;
                    _lastDiagnostics = session.Diagnostics;
                }

                session.Diagnostics = null;
                session.PublishCompletion();
                needToRestart &= !result.HasErrors;

                if (ownsSession && (session.RestartRequested || (needToRestart && !session.StopRequested)))
                {
                    StartListen();
                }
            });
            return session;
        }

        /// <summary>구독 중지를 요청하고 재시작 예약을 취소한다. 이후 시작을 요청하면 정리 완료 뒤 실행한다.</summary>
        public void StopListen()
        {
            ListenerExecution? session = _session;
            if (session is null)
            {
                return;
            }

            session.RestartRequested = false;
            session.StopRequested = true;

            if (session.Stopping)
            {
                return;
            }

            session.Stopping = true;

            try
            {
                session.Cancellation.Cancel();
            }
            catch (Exception exception)
            {
                session.RecordError(exception, ListenerErrorPhase.Cancellation);
                ExceptionProcess(exception, session);
            }
        }

        /// <summary>
        /// 중지를 요청하고 현재 구독의 검사 정리, 비활성화 및 실행 상태 해제를 기다린다.
        /// 정리 실패는 Exception 이벤트로 전달한다. 별도로 예약한 다음 구독의 종료는 기다리지 않는다.
        /// </summary>
        /// <returns>현재 구독의 실행 상태가 해제되면 완료되는 작업이다. 구독 중이 아니면 이미 완료된 작업이다.</returns>
        public Task StopListenAsync()
        {
            ListenerExecution? session = _session;
            if (session is null)
            {
                return Task.CompletedTask;
            }

            // 취소 콜백이 종료 처리를 진행하기 전에 대기할 Task를 확보한다.
            Task completionTask = session.Completion;
            StopListen();

            return completionTask;
        }

        private async Task<bool> RunSessionAsync(ListenerExecution session)
        {
            bool repeat = false;
            using ConditionObservation.Scope observation = ConditionObservation.Enter(session.RootObservation);

            try
            {
                session.Phase = ListenerErrorPhase.Initialization;
                Initialize();

                session.Condition = Condition;

                if (!session.StopRequested)
                {
                    session.Phase = ListenerErrorPhase.Activation;
                    session.Condition?.Activate();
                }

                if (!session.Stopping) session.State = ListenerExecutionState.Checking;
                ConditionState state = await ListenConditionAsync(session);

                if (state == ConditionState.Satisfied && !session.StopRequested)
                {
                    // 즉시 완료되는 검사도 StartListen의 호출 스택에서 성공 이벤트를 발생시키지 않는다.
                    await Task.Yield();

                    if (!session.StopRequested)
                    {
                        session.Kind = ListenerResultKind.Satisfied;
                        session.Phase = ListenerErrorPhase.SuccessCallback;
                        Success?.Invoke();
                        repeat = !IsOneShot && !session.StopRequested;
                    }
                }
            }
            catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                ConditionObservation.Error(exception, session.Phase);
                if (session.Kind != ListenerResultKind.Satisfied) session.Kind = ListenerResultKind.Faulted;
                ExceptionProcess(exception, session);
            }
            finally
            {
                session.Stopping = true;
                session.Phase = ListenerErrorPhase.Cancellation;

                try
                {
                    session.Cancellation.Cancel();
                }
                catch (Exception exception)
                {
                    session.RecordError(exception, ListenerErrorPhase.Cancellation);
                    repeat = false; ExceptionProcess(exception, session);
                }

                if (session.Condition is ConditionGroup group)
                {
                    session.Phase = ListenerErrorPhase.Cleanup;
                    try
                    {
                        await group.EndChecksAsync();
                    }
                    catch (Exception exception)
                    {
                        ConditionObservation.Error(exception, ListenerErrorPhase.Cleanup);
                        repeat = false; ExceptionProcess(exception, session);
                    }
                }

                try
                {
                    session.Phase = ListenerErrorPhase.Deactivation;
                    session.Condition?.Deactivate();
                }
                catch (Exception exception)
                {
                    ConditionObservation.Error(exception, ListenerErrorPhase.Deactivation);
                    repeat = false; ExceptionProcess(exception, session);
                }

                session.Cancellation.Dispose();
            }

            return repeat;
        }

        /// <summary>현재 만족 여부를 동기적으로 확인하고, 만족하면 성공 이벤트를 호출한다.</summary>
        /// <remarks>
        /// 비동기 검사를 실행하지 않는다. 만족, 유효하지 않은 조건 또는 오류가 발생하면 구독 중지를 요청한다.
        /// 자동 재시작과 <see cref="IsOneShot"/> 설정은 적용하지 않으며, 반복 호출하면 성공 이벤트도 다시 호출할 수 있다.
        /// Execution은 만들지 않는다. 진행 중인 Execution을 중지했다면 그 Execution은 일반 취소 규칙을 따르며,
        /// 이 동기 호출의 Success를 해당 Execution의 Satisfied 결과로 기록하지 않는다.
        /// </remarks>
        public void CheckConditionsImmediately()
        {
            using ConditionObservation.Scope observation = ConditionObservation.Enter(null);
            bool shouldStop = false;
            try
            {
                Initialize();

                ICondition? condition = Condition;
                if (condition is null || !condition.IsValid)
                {
                    shouldStop = true;

                    if (!IsIgnoreNotAvailableCondition)
                    {
                        throw new InvalidOperationException("Condition is not available. Check the conditions in the group.");
                    }

                    return;
                }

                if (condition.IsSatisfied)
                {
                    shouldStop = true;
                    Success?.Invoke();
                }
            }
            catch (Exception exception)
            {
                shouldStop = true;
                ExceptionProcess(exception);
            }
            finally
            {
                if (shouldStop)
                {
                    StopListen();
                }
            }
        }

        /// <summary>기본 오류 로그 핸들러를 등록한다. 재정의할 때 기본 로그도 사용하려면 base 구현을 호출한다.</summary>
        protected virtual void OnInitialize()
        {
            if (!_isExceptionLoggerRegistered)
            {
                Exception += ExceptionLogger;
                _isExceptionLoggerRegistered = true;
            }
        }

        private async Task<ConditionState> ListenConditionAsync(ListenerExecution session)
        {
            ICondition? condition = session.Condition;
            CancellationToken cancellationToken = session.Cancellation.Token;
            ConditionState conditionState = ConditionState.NotYet;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    session.Phase = ListenerErrorPhase.Validation;
                    bool valid = condition is not null && condition.IsValid;
                    session.RootObservation.Validity(valid);
                    if (!valid)
                    {
                        session.Kind = ListenerResultKind.Unavailable;
                        if (!IsIgnoreNotAvailableCondition)
                        {
                            throw new InvalidOperationException("Condition is not available. Check the conditions in the group.");
                        }

                        break;
                    }

                    session.Phase = ListenerErrorPhase.Check;
                    Task check = condition!.CheckAsync(cancellationToken) ?? throw new InvalidOperationException("CheckAsync must not return a null Task.");
                    session.RootObservation.TaskState(check);
                    try { await check; }
                    finally { session.RootObservation.TaskState(check); }

                    cancellationToken.ThrowIfCancellationRequested();

                    session.Phase = ListenerErrorPhase.Satisfaction;
                    bool satisfied = condition.IsSatisfied;
                    session.RootObservation.Satisfaction(satisfied);
                    if (satisfied)
                    {
                        session.ConditionSatisfied = true;
                        conditionState = ConditionState.Satisfied;

                        break;
                    }

                    await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                conditionState = ConditionState.NotYet;
            }
            catch (Exception exception)
            {
                conditionState = ConditionState.NotAvailable;
                if (session.Kind != ListenerResultKind.Unavailable && !session.StopRequested)
                    session.Kind = ListenerResultKind.Faulted;
                ConditionObservation.Error(exception, session.StopRequested ? ListenerErrorPhase.Cleanup : session.Phase);

                ExceptionProcess(exception, session);
            }

            return conditionState;
        }

        private void ExceptionProcess(Exception exception, ListenerExecution? execution = null)
        {
            Action<Exception>? handlers = Exception;
            if (handlers is null)
            {
                LogExceptionSafely(exception);
                return;
            }

            bool handlerFailed = false;
            bool originalLogged = false;
            foreach (Action<Exception> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(exception);
                    originalLogged |= handler == ExceptionLogger;
                }
                catch (Exception handlerException)
                {
                    execution?.RecordError(handlerException, ListenerErrorPhase.ExceptionCallback);
                    handlerFailed = true;
                    LogExceptionSafely(handlerException);
                }
            }

            if (handlerFailed && !originalLogged)
            {
                LogExceptionSafely(exception);
            }
        }

        private sealed class DiagnosticsLease : IDisposable
        {
            private Listener? _owner;
            internal DiagnosticsLease(Listener owner) { _owner = owner; }
            public void Dispose()
            {
                if (_owner is null) return;
                --_owner._diagnosticRequests;
                _owner = null;
            }
        }
    }
}
