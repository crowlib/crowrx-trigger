using System;
using System.Text;
using CrowRx.Pool.Collections;
using UnityEngine.UIElements;

namespace CrowRx.Trigger.Unity.Editor
{
    // 저장된 관찰 정보만 읽는다. 설정 생성이나 Condition 평가는 하지 않는다.
    internal sealed class ListenerRuntimePanel : VisualElement, IDisposable
    {
        private readonly TriggerListenerBehaviour _listener;
        private readonly Foldout _foldout;
        private readonly Label _execution = new() { name = "runtime-execution" };
        private readonly Label _result = new() { name = "runtime-result" };
        private readonly Label _errors = new() { name = "runtime-errors" };
        private readonly Label _conditions = new() { name = "runtime-conditions" };
        private readonly Label _preparation = new() { name = "runtime-preparation" };
        private readonly IVisualElementScheduledItem _refresh;
        private readonly StringBuilder _text = new();
        private IDisposable _request;
        private bool _disposed;

        internal bool Expanded { get => _foldout.value; set => _foldout.value = value; }

        internal ListenerRuntimePanel(TriggerListenerBehaviour listener)
        {
            _listener = listener;
            name = "listener-runtime";
            _foldout = new Foldout { text = "Runtime", value = false, name = "runtime-foldout" };
            Add(_foldout);
            foreach (Label label in new[] { _execution, _result, _errors, _conditions, _preparation })
            {
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.marginBottom = 4;
                _foldout.Add(label);
            }
            _foldout.RegisterValueChangedCallback(_ => UpdateRequest());
            RegisterCallback<AttachToPanelEvent>(_ => UpdateRequest());
            RegisterCallback<DetachFromPanelEvent>(_ => Release());
            _refresh = schedule.Execute(Refresh).Every(100);
            _refresh.Pause();
            Refresh();
        }

        private void UpdateRequest()
        {
            if (_disposed || panel == null || !Expanded || !_listener)
            {
                Release();
                return;
            }
            _request ??= _listener.AcquireDiagnostics();
            _refresh.Resume();
            Refresh();
        }

        private void Release()
        {
            _refresh?.Pause();
            _request?.Dispose();
            _request = null;
        }

        private void Refresh()
        {
            if (_disposed) return;
            if (!_listener) { Release(); return; }

            ListenerExecution execution = _listener.CurrentExecution;
            _execution.text = execution == null
                ? (_listener.IsInitialized ? "Idle" : "Idle / Not initialized")
                : $"Current Execution #{execution.Id}: {execution.State}\nRestart requested: {execution.RestartRequested}";

            ListenerResult? result = _listener.LastResult;
            _result.text = result.HasValue
                ? $"Last Result #{result.Value.ExecutionId}: {result.Value.Kind}" + (result.Value.HasErrors ? " · Errors present" : "")
                : "Last Result: None";

            _text.Clear();
            if (execution?.LastError is ListenerError currentError)
                AppendError($"Current #{execution.Id}", currentError);
            if (result.HasValue)
                foreach (ListenerError error in result.Value.Errors) AppendError($"Last #{result.Value.ExecutionId}", error);
            _errors.text = _text.Length == 0 ? "Errors: None observed" : _text.ToString();

            _text.Clear();
            using (PooledList<ConditionDiagnostic> diagnostics = ListPool<ConditionDiagnostic>.Get())
            {
                _listener.CopyConditionDiagnostics(diagnostics);
                foreach (ConditionDiagnostic item in diagnostics)
                {
                    _text.Append('#').Append(item.ExecutionId).Append(' ').Append(item.Path)
                        .Append(" · ").Append(item.RuntimeType).Append('\n')
                        .Append("Valid: ").Append(Observed(item.IsValid))
                        .Append(" · Satisfied: ").Append(Observed(item.IsSatisfied))
                        .Append(" · Check: ").Append(item.CheckState).Append('\n');
                    if (item.LastError != null) _text.Append(item.LastError.Message).Append('\n');
                }
            }
            _conditions.text = _text.Length == 0 ? "Conditions: No observations recorded" : _text.ToString();
            _preparation.text = _listener.PreparationError == null ? "" : "Before Execution · Preparation error: " + _listener.PreparationError;
        }

        private void AppendError(string source, ListenerError error)
        {
            _text.Append(source).Append(" · ").Append(error.Phase);
            if (error.ConditionPath != null) _text.Append(" · ").Append(error.ConditionPath);
            _text.Append(": ").Append(error.Exception).Append('\n');
        }

        private static string Observed(bool? value) => value.HasValue ? (value.Value ? "True" : "False") : "Unknown";

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release();
        }
    }
}
