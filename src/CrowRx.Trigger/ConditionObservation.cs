using System;
using System.Threading;
using System.Threading.Tasks;

namespace CrowRx.Trigger
{
    // 검사 호출의 ExecutionContext 안에서만 위치를 전달한다. 조건 객체를 프록시로 교체하지 않는다.
    internal sealed class ConditionObservation
    {
        private static readonly AsyncLocal<ConditionObservation?> Ambient = new();
        internal readonly ListenerExecution Execution;
        private readonly bool _opaque;
        internal DiagnosticNode? Node;
        internal static ConditionObservation? Current => Ambient.Value;

        internal ConditionObservation(ListenerExecution execution, bool opaque)
        { Execution = execution; _opaque = opaque; }

        internal DiagnosticNode? ObservedNode
        {
            get
            {
                if (_opaque || !Execution.DetailsEnabled) return null;
                if (Node is null && Execution.Condition is not null)
                {
                    Execution.Diagnostics ??= new ConditionDiagnosticTrace(Execution, Execution.Condition);
                    Node = Execution.Diagnostics.Root;
                }
                return Node;
            }
        }

        internal static Scope Enter(ConditionObservation? observation) => new(observation);

        internal static Scope EnterGroup(ConditionGroup group)
        {
            ConditionObservation? current = Current;
            if (current is null) return default;
            DiagnosticNode? node = current.ObservedNode;
            // 사용자 조건이 내부에서 호출한 별도 그룹을 공개된 트리의 자식으로 추측하지 않는다.
            return node is not null && ReferenceEquals(node.Condition, group)
                ? default : new Scope(current.Execution.OpaqueObservation);
        }

        internal static Scope EnterChild(ICondition condition, int index)
        {
            ConditionObservation? current = Current;
            if (current is null) return default;
            DiagnosticNode? parent = current.ObservedNode;
            ConditionObservation next = parent is null
                ? current.Execution.OpaqueObservation
                : current.Execution.Diagnostics!.Child(parent, index, condition).Observation;
            return new Scope(next);
        }

        internal void Validity(bool value) { DiagnosticNode? node = ObservedNode; if (node is not null) node.Valid = value; }
        internal void Satisfaction(bool value) { DiagnosticNode? node = ObservedNode; if (node is not null) node.Satisfied = value; }
        internal void TaskState(Task task)
        {
            DiagnosticNode? node = ObservedNode;
            if (node is null) return;
            node.CheckState = task.IsCanceled ? ConditionCheckState.Canceled : task.IsFaulted ? ConditionCheckState.Faulted
                : task.IsCompleted ? ConditionCheckState.Completed : ConditionCheckState.Pending;
            if (task.IsFaulted && node.Error is null) node.Error = task.Exception?.InnerException;
        }

        internal static void Error(Exception error, ListenerErrorPhase phase)
        {
            ConditionObservation? current = Current;
            if (current is null) return;
            if (error is OperationCanceledException && current.Execution.Cancellation.IsCancellationRequested) return;
            if (phase == ListenerErrorPhase.Check && current.Execution.StopRequested)
                phase = ListenerErrorPhase.Cleanup;
            DiagnosticNode? node = phase == ListenerErrorPhase.SuccessCallback || phase == ListenerErrorPhase.ExceptionCallback || phase == ListenerErrorPhase.Cancellation
                ? null : current.ObservedNode;
            if (node is not null) node.Error = error;
            current.Execution.RecordError(error, phase, node?.Path);
        }

        internal static bool ReadValidity(ICondition? condition, int index)
        {
            if (condition is null) return false;
            using Scope scope = EnterChild(condition, index);
            try { bool value = condition.IsValid; Current?.Validity(value); return value; }
            catch (Exception error) { Error(error, ListenerErrorPhase.Validation); throw; }
        }

        internal static bool ReadSatisfaction(ICondition condition, int index)
        {
            using Scope scope = EnterChild(condition, index);
            try { bool value = condition.IsSatisfied; Current?.Satisfaction(value); return value; }
            catch (Exception error) { Error(error, ListenerErrorPhase.Satisfaction); throw; }
        }

        internal static void Initialize(ICondition? condition, int index)
        {
            if (condition is null) return;
            using Scope scope = EnterChild(condition, index);
            try { condition.Initialize(); }
            catch (Exception error) { Error(error, ListenerErrorPhase.Initialization); throw; }
        }

        internal static void Activate(ICondition condition, int index)
        {
            using Scope scope = EnterChild(condition, index);
            try { condition.Activate(); }
            catch (Exception error) { Error(error, ListenerErrorPhase.Activation); throw; }
        }

        internal static void Deactivate(ICondition? condition, int index)
        {
            if (condition is null) return;
            using Scope scope = EnterChild(condition, index);
            try { condition.Deactivate(); }
            catch (Exception error) { Error(error, ListenerErrorPhase.Deactivation); throw; }
        }

        internal static Task Check(ICondition condition, int index, CancellationToken token)
        {
            using Scope scope = EnterChild(condition, index);
            try
            {
                Task task = condition.CheckAsync(token) ?? throw new InvalidOperationException("CheckAsync must not return a null Task.");
                Current?.TaskState(task);
                return task;
            }
            catch (Exception error) { Error(error, ListenerErrorPhase.Check); throw; }
        }

        internal static void CompleteCheck(ICondition condition, int index, Task task)
        {
            using Scope scope = EnterChild(condition, index);
            Current?.TaskState(task);
            try { task.GetAwaiter().GetResult(); }
            catch (Exception error) { Error(error, ListenerErrorPhase.Check); throw; }
        }

        internal static void ObserveTask(ICondition condition, int index, Task task)
        {
            using Scope scope = EnterChild(condition, index);
            Current?.TaskState(task);
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly ConditionObservation? _previous;
            private readonly bool _changed;
            internal Scope(ConditionObservation? next)
            {
                _previous = Ambient.Value;
                _changed = !ReferenceEquals(_previous, next);
                if (_changed) Ambient.Value = next;
            }
            public void Dispose() { if (_changed) Ambient.Value = _previous; }
        }
    }
}
