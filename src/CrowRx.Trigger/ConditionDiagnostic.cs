using System;
using System.Collections.Generic;

namespace CrowRx.Trigger
{
    /// <summary>최근 관찰한 검사 Task의 상태다.</summary>
    public enum ConditionCheckState
    {
        /// <summary>검사를 아직 관찰하지 않았다.</summary>
        Unknown,
        /// <summary>검사 Task의 완료를 기다리고 있다.</summary>
        Pending,
        /// <summary>검사 Task가 정상 완료됐다. 조건 만족을 뜻하지는 않는다.</summary>
        Completed,
        /// <summary>검사 Task가 취소됐다.</summary>
        Canceled,
        /// <summary>검사 Task에서 오류를 관찰했다.</summary>
        Faulted
    }

    /// <summary>정상 실행 중 관찰한 조건 정보의 복사본이다. 현재 조건을 다시 평가한 결과는 아니다.</summary>
    public readonly struct ConditionDiagnostic
    {
        /// <summary>정보를 기록한 Execution ID다.</summary>
        public long ExecutionId { get; }
        /// <summary>실행 중 조건 순서에 따른 경로다. 영구 에셋 식별자는 아니다.</summary>
        public string Path { get; }
        /// <summary>실행 조건의 runtime type 이름이다.</summary>
        public string RuntimeType { get; }
        /// <summary>최근 유효성 판정이다. 관찰하지 않았으면 null이다.</summary>
        public bool? IsValid { get; }
        /// <summary>최근 만족 판정이다. 단락 평가 등으로 관찰하지 않았으면 null이다.</summary>
        public bool? IsSatisfied { get; }
        /// <summary>최근 검사 Task 상태다.</summary>
        public ConditionCheckState CheckState { get; }
        /// <summary>이 위치에서 관찰한 마지막 오류다. 없으면 null이다.</summary>
        public Exception? LastError { get; }

        internal ConditionDiagnostic(long id, DiagnosticNode node)
        {
            ExecutionId = id; Path = node.Path; RuntimeType = node.RuntimeType;
            IsValid = node.Valid; IsSatisfied = node.Satisfied; CheckState = node.CheckState; LastError = node.Error;
        }
    }

    internal sealed class ConditionDiagnosticTrace
    {
        internal readonly long ExecutionId;
        private readonly List<DiagnosticNode> _nodes = new();
        internal readonly DiagnosticNode Root;

        internal ConditionDiagnosticTrace(ListenerExecution execution, ICondition condition)
        {
            ExecutionId = execution.Id;
            Root = new DiagnosticNode(execution, condition, "Root");
            _nodes.Add(Root);
        }

        internal DiagnosticNode Child(DiagnosticNode parent, int index, ICondition condition)
        {
            parent.Children ??= new Dictionary<int, DiagnosticNode>();
            if (!parent.Children.TryGetValue(index, out DiagnosticNode node) || !ReferenceEquals(node.Condition, condition))
            {
                node = new DiagnosticNode(parent.Observation.Execution, condition, parent.Path + "/" + index);
                parent.Children[index] = node;
                _nodes.Add(node);
            }
            return node;
        }

        internal int CopyTo(List<ConditionDiagnostic> destination)
        {
            foreach (DiagnosticNode node in _nodes) destination.Add(new ConditionDiagnostic(ExecutionId, node));
            return destination.Count;
        }

        internal void ReleaseConditions()
        {
            foreach (DiagnosticNode node in _nodes) node.Condition = null;
        }
    }

    internal sealed class DiagnosticNode
    {
        internal ICondition? Condition;
        internal readonly string Path;
        internal readonly string RuntimeType;
        internal readonly ConditionObservation Observation;
        internal Dictionary<int, DiagnosticNode>? Children;
        internal bool? Valid;
        internal bool? Satisfied;
        internal ConditionCheckState CheckState;
        internal Exception? Error;

        internal DiagnosticNode(ListenerExecution execution, ICondition condition, string path)
        {
            Condition = condition; Path = path; RuntimeType = condition.GetType().FullName ?? condition.GetType().Name;
            Observation = new ConditionObservation(execution, false) { Node = this };
        }
    }
}
