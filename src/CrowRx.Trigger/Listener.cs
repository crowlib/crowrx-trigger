using System;
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

        private ListenSession? _session;
        private bool _isExceptionLoggerRegistered;
        private static readonly Action<Exception> ExceptionLogger = error => Log.Exception(error);

        /// <inheritdoc/>
        public bool IsInitialized { get; private set; }

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

            Condition?.Initialize();

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

        /// <summary>구독 중지를 요청하고 재시작 예약을 취소한다. 이후 시작을 요청하면 정리 완료 뒤 실행한다.</summary>
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
        /// <returns>현재 구독의 실행 상태가 해제되면 완료되는 작업이다. 구독 중이 아니면 이미 완료된 작업이다.</returns>
        public Task StopListenAsync()
        {
            ListenSession? session = _session;
            if (session is null)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource<bool> completion = session.Completion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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
            catch (Exception exception)
            {
                ExceptionProcess(exception);
            }
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

        /// <summary>현재 만족 여부를 동기적으로 확인하고, 만족하면 성공 이벤트를 호출한다.</summary>
        /// <remarks>
        /// 비동기 검사를 실행하지 않는다. 만족, 유효하지 않은 조건 또는 오류가 발생하면 구독 중지를 요청한다.
        /// 자동 재시작과 <see cref="IsOneShot"/> 설정은 적용하지 않으며, 반복 호출하면 성공 이벤트도 다시 호출할 수 있다.
        /// </remarks>
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

        /// <summary>기본 오류 로그 핸들러를 등록한다. 재정의할 때 기본 로그도 사용하려면 base 구현을 호출한다.</summary>
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
