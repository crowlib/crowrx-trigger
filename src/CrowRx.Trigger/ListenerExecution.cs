using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CrowRx.Pool.Collections;

namespace CrowRx.Trigger
{
    /// <summary>한 번의 조건 감시가 진행 중인 단계다.</summary>
    public enum ListenerExecutionState
    {
        /// <summary>리스너와 조건을 초기화하거나 활성화하고 있다.</summary>
        Initializing,
        /// <summary>조건을 검사하고 있다.</summary>
        Checking,
        /// <summary>중지 또는 종료를 위해 남은 검사를 정리하고 있다.</summary>
        Stopping,
        /// <summary>정리와 비활성화까지 종료했다.</summary>
        Completed
    }

    /// <summary>한 번의 조건 감시가 종료된 이유다.</summary>
    public enum ListenerResultKind
    {
        /// <summary>조건을 만족해 성공 알림을 시작했다. callback이나 정리 오류는 별도로 확인한다.</summary>
        Satisfied,
        /// <summary>성공 알림 전에 중지됐다.</summary>
        Canceled,
        /// <summary>조건이 없거나 유효하지 않아 종료했다.</summary>
        Unavailable,
        /// <summary>초기화, 활성화 또는 검사 중 오류로 종료했다.</summary>
        Faulted
    }

    /// <summary>리스너가 오류를 관찰한 단계다.</summary>
    public enum ListenerErrorPhase
    {
        /// <summary>초기화 단계다.</summary>
        Initialization,
        /// <summary>조건 활성화 단계다.</summary>
        Activation,
        /// <summary>유효성 판정 단계다.</summary>
        Validation,
        /// <summary>비동기 검사 단계다.</summary>
        Check,
        /// <summary>만족 여부 판정 단계다.</summary>
        Satisfaction,
        /// <summary>성공 callback 단계다.</summary>
        SuccessCallback,
        /// <summary>오류 callback 단계다.</summary>
        ExceptionCallback,
        /// <summary>취소 요청 단계다.</summary>
        Cancellation,
        /// <summary>남은 검사 정리 단계다.</summary>
        Cleanup,
        /// <summary>조건 비활성화 단계다.</summary>
        Deactivation
    }

    /// <summary>실행에서 관찰한 원래 오류와 위치다.</summary>
    public readonly struct ListenerError
    {
        /// <summary>관찰한 원래 예외다.</summary>
        public Exception Exception { get; }
        /// <summary>오류를 관찰한 단계다.</summary>
        public ListenerErrorPhase Phase { get; }
        /// <summary>확인한 조건 경로다. 위치를 알 수 없거나 조건 외부 오류면 null이다.</summary>
        public string? ConditionPath { get; }

        internal ListenerError(Exception exception, ListenerErrorPhase phase, string? path)
        { Exception = exception; Phase = phase; ConditionPath = path; }
    }

    /// <summary>정리까지 끝난 한 Execution의 결과다. 별도로 시작한 비동기 작업의 결과는 포함하지 않는다.</summary>
    public readonly struct ListenerResult
    {
        private readonly IReadOnlyList<ListenerError>? _errors;
        /// <summary>이 리스너에서 실행한 Execution ID다.</summary>
        public long ExecutionId { get; }
        /// <summary>종료 이유다.</summary>
        public ListenerResultKind Kind { get; }
        /// <summary>정상 검사에서 만족 판정을 관찰했는지 나타낸다. 성공 알림 전에 취소됐다면 Kind는 Canceled일 수 있다.</summary>
        public bool ConditionSatisfied { get; }
        /// <summary>callback이나 정리를 포함해 관찰한 오류가 있는지 나타낸다.</summary>
        public bool HasErrors => Errors.Count != 0;
        /// <summary>관찰 순서대로 고정된 오류 목록이다. Satisfied 결과에도 오류가 있을 수 있다.</summary>
        public IReadOnlyList<ListenerError> Errors => _errors ?? Array.Empty<ListenerError>();

        internal ListenerResult(long id, ListenerResultKind kind, bool satisfied, IReadOnlyList<ListenerError> errors)
        { ExecutionId = id; Kind = kind; ConditionSatisfied = satisfied; _errors = errors; }
    }

    /// <summary>특정 조건 감시 한 번을 가리키는 핸들이다. 완료 후에도 다른 실행을 가리키지 않는다.</summary>
    /// <remarks>상태 조회는 리스너와 같은 실행 컨텍스트에서 한다. Completion은 여러 호출자가 기다릴 수 있다.</remarks>
    public sealed class ListenerExecution
    {
        private Listener? _owner;
        private TaskCompletionSource<ListenerResult>? _completion;
        private PooledList<ListenerError>? _errors;
        private ListenerResult? _result;
        private volatile bool _published;

        /// <summary>리스너별로 증가하는 Execution ID다.</summary>
        public long Id { get; }
        /// <summary>현재 실행 단계다.</summary>
        public ListenerExecutionState State { get; internal set; }
        /// <summary>정리 뒤 명시적인 재시작이 예약됐는지 나타낸다. 자동 반복 여부와는 별개다.</summary>
        public bool RestartRequested { get; internal set; }
        /// <summary>정상 검사에서 만족 판정을 관찰했는지 나타낸다.</summary>
        public bool ConditionSatisfied { get; internal set; }
        /// <summary>이 실행에서 가장 최근에 관찰한 오류다. 아직 없으면 null이다.</summary>
        public ListenerError? LastError { get; private set; }
        /// <summary>검사 정리와 비활성화 뒤 고정된 Result로 완료된다. 실행 오류와 취소는 Task 예외 대신 Result로 전달한다.</summary>
        public Task<ListenerResult> Completion
        {
            get
            {
                TaskCompletionSource<ListenerResult>? completion = Volatile.Read(ref _completion);
                if (completion is null)
                {
                    var created = new TaskCompletionSource<ListenerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    completion = Interlocked.CompareExchange(ref _completion, created, null) ?? created;
                }
                if (_published) completion.TrySetResult(_result!.Value);
                return completion.Task;
            }
        }

        internal readonly CancellationTokenSource Cancellation = new();
        internal ICondition? Condition;
        internal bool StopRequested;
        internal bool Stopping
        {
            get => State == ListenerExecutionState.Stopping;
            set { if (value) State = ListenerExecutionState.Stopping; }
        }
        internal ListenerResultKind Kind = ListenerResultKind.Canceled;
        internal ListenerErrorPhase Phase;
        internal ConditionDiagnosticTrace? Diagnostics;
        internal readonly ConditionObservation RootObservation;
        internal readonly ConditionObservation OpaqueObservation;
        internal bool DetailsEnabled => State != ListenerExecutionState.Completed && _owner?.DiagnosticsRequested == true;
        internal bool HasErrors => _errors is not null && _errors.Count != 0;

        internal ListenerExecution(Listener owner, long id)
        {
            _owner = owner; Id = id;
            RootObservation = new ConditionObservation(this, false);
            OpaqueObservation = new ConditionObservation(this, true);
        }

        internal void RecordError(Exception error, ListenerErrorPhase phase, string? path = null)
        {
            if (State == ListenerExecutionState.Completed) return;
            _errors ??= ListPool<ListenerError>.Get();
            // 그룹에서 관찰한 오류를 리스너가 다시 받더라도 원래 위치를 보존한다.
            foreach (ListenerError recorded in _errors)
                if (ReferenceEquals(recorded.Exception, error)) return;
            var value = new ListenerError(error, phase, path);
            _errors.Add(value);
            LastError = value;
        }

        internal ListenerResult Finish()
        {
            IReadOnlyList<ListenerError> errors = Array.Empty<ListenerError>();
            if (_errors is not null)
            {
                try { errors = Array.AsReadOnly(_errors.ToArray()); }
                finally { _errors.Dispose(); _errors = null; }
            }
            _result = new ListenerResult(Id, Kind, ConditionSatisfied, errors);
            State = ListenerExecutionState.Completed;
            Diagnostics?.ReleaseConditions();
            Condition = null;
            RootObservation.Node = null;
            _owner = null;
            return _result.Value;
        }

        internal void PublishCompletion()
        {
            _published = true;
            Volatile.Read(ref _completion)?.TrySetResult(_result!.Value);
        }
    }
}
