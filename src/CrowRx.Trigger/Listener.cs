using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CrowRx.Trigger
{
    /// <summary>
    /// 기본 Trigger Listener class
    /// 포함된 <see cref="ICondition"/>의 <see cref="ICondition.CheckAsync"/>를 구독하여 Trigger를 발생 시킨다.
    /// 시작, 중지, 조건 변경은 동일한 실행 컨텍스트에서 호출한다.
    /// </summary>
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

        public event Action? Success;
        public event Action<Exception>? Exception;

        private ListenSession? _session;
        private bool _isExceptionLoggerRegistered;
        private static readonly Action<Exception> ExceptionLogger = error => Log.Exception(error);

        public bool IsInitialized { get; private set; }

        public virtual ICondition? Condition { get; }
        public virtual bool IsIgnoreNotAvailableCondition { get; set; }
        public virtual bool IsOneShot { get; set; }

        protected Listener()
        {
        }

        public Listener(ICondition condition)
        {
            Condition = condition;
        }

        public void Initialize()
        {
            if (IsInitialized)
            {
                return;
            }

            OnInitialize();

            Condition?.Initialize();

            IsInitialized = true;
        }

        /// <summary>구독을 시작한다. 기존 구독이 종료 중이면 종료가 끝난 뒤 한 번 시작한다.</summary>
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

            ListenSession session = new();
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
                    ExceptionProcess(exception);
                }

                bool ownsSession = ReferenceEquals(session, _session);
                if (ownsSession)
                {
                    _session = null;
                }

                session.Completion?.TrySetResult(true);

                if (ownsSession && (session.RestartRequested || (needToRestart && !session.StopRequested)))
                {
                    StartListen();
                }
            });
        }

        /// <summary>중지를 요청한다. 종료 중 호출한 StartListen은 기존 검사가 종료된 뒤 실행된다.</summary>
        public void StopListen()
        {
            ListenSession? session = _session;
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
                ExceptionProcess(exception);
            }
        }

        /// <summary>
        /// 중지를 요청하고 현재 구독의 검사 정리, 비활성화 및 실행 상태 해제를 기다린다.
        /// 정리 실패는 Exception 이벤트로 전달한다. 별도로 예약한 다음 구독의 종료는 기다리지 않는다.
        /// </summary>
        public Task StopListenAsync()
        {
            ListenSession? session = _session;
            if (session is null)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource<bool> completion = session.Completion ??=
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 취소 콜백이 종료 처리를 진행하기 전에 대기할 Task를 확보한다.
            Task completionTask = completion.Task;
            StopListen();

            return completionTask;
        }

        private async Task<bool> RunSessionAsync(ListenSession session)
        {
            bool repeat = false;

            try
            {
                Initialize();

                session.Condition = Condition;

                if (!session.StopRequested)
                {
                    session.Condition?.Activate();
                }

                ConditionState state = await ListenConditionAsync(session.Condition, session.Cancellation.Token);

                if (state == ConditionState.Satisfied && !session.StopRequested)
                {
                    // 즉시 완료되는 검사도 StartListen의 호출 스택에서 성공 이벤트를 발생시키지 않는다.
                    await Task.Yield();

                    if (!session.StopRequested)
                    {
                        Success?.Invoke();
                        repeat = !IsOneShot && !session.StopRequested;
                    }
                }
            }
            catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
            catch (Exception exception) { ExceptionProcess(exception); }
            finally
            {
                session.Stopping = true;

                try
                {
                    session.Cancellation.Cancel();
                }
                catch (Exception exception)
                {
                    repeat = false; ExceptionProcess(exception);
                }

                if (session.Condition is ConditionGroup group)
                {
                    try
                    {
                        await group.EndChecksAsync();
                    }
                    catch (Exception exception)
                    {
                        repeat = false; ExceptionProcess(exception);
                    }
                }

                try
                {
                    session.Condition?.Deactivate();
                }
                catch (Exception exception)
                {
                    repeat = false; ExceptionProcess(exception);
                }

                session.Cancellation.Dispose();
            }

            return repeat;
        }

        public void CheckConditionsImmediately()
        {
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

        protected virtual void OnInitialize()
        {
            if (!_isExceptionLoggerRegistered)
            {
                Exception += ExceptionLogger;
                _isExceptionLoggerRegistered = true;
            }
        }

        private async Task<ConditionState> ListenConditionAsync(ICondition? condition, CancellationToken cancellationToken)
        {
            ConditionState conditionState = ConditionState.NotYet;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (condition is null || !condition.IsValid)
                    {
                        if (!IsIgnoreNotAvailableCondition)
                        {
                            throw new InvalidOperationException("Condition is not available. Check the conditions in the group.");
                        }

                        break;
                    }

                    await condition.CheckAsync(cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();

                    if (condition.IsSatisfied)
                    {
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

                ExceptionProcess(exception);
            }

            return conditionState;
        }

        private void ExceptionProcess(Exception exception)
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
                    handlerFailed = true;
                    LogExceptionSafely(handlerException);
                }
            }

            if (handlerFailed && !originalLogged)
            {
                LogExceptionSafely(exception);
            }
        }

        private sealed class ListenSession
        {
            public ICondition? Condition;

            public bool Stopping;
            public bool StopRequested;
            public bool RestartRequested;

            public readonly CancellationTokenSource Cancellation = new();
            public TaskCompletionSource<bool>? Completion;
        }
    }
}
