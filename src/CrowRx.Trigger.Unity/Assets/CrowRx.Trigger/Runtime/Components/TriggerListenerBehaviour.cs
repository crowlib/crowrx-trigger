using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace CrowRx.Trigger.Unity
{
    /// <summary>리스너의 자동 구독 시작 시점이다.</summary>
    public enum ListenerStartMode
    {
        /// <summary>컴포넌트가 활성화될 때마다 시작한다.</summary>
        OnEnable,

        /// <summary>MonoBehaviour.Start에서 한 번 시작한다.</summary>
        Start,

        /// <summary>코드나 UnityEvent에서 직접 시작한다.</summary>
        Manual
    }

    /// <summary>직렬화된 조건 설정을 기존 Listener에 연결하는 Unity 컴포넌트다.</summary>
    [AddComponentMenu("CrowRx/Trigger/Trigger Listener")]
    public sealed class TriggerListenerBehaviour : MonoBehaviour, IAsyncListener
    {
        #region serialized

        [SerializeField] private ListenerStartMode startMode;
        [SerializeField] private bool isOneShot;
        [SerializeField] private bool ignoreInvalidCondition;
        [SerializeField] private ConditionCombineType combineType;
        [SerializeReference] private List<ConditionDefinition> conditions = new();
        [SerializeField] private UnityEvent onSuccess = new();
        [SerializeField] private UnityEvent<string> onError = new();

        #endregion

        private Listener _listener;
        private bool _destroyed;
        private int _diagnosticsRequests;
        private IDisposable _coreDiagnostics;

        /// <summary>현재 감시하거나 Cleanup 중인 Execution이다. Idle이면 null이다.</summary>
        public ListenerExecution CurrentExecution => _listener?.CurrentExecution;

        /// <summary>마지막 Execution의 Cleanup까지 끝난 결과다. 다음 실행을 시작해도 유지한다.</summary>
        public ListenerResult? LastResult => _listener?.LastResult;

        /// <summary>Execution 생성 전 설정 준비에서 마지막으로 발생한 오류다. 준비에 성공하면 지운다.</summary>
        public Exception PreparationError { get; private set; }

        /// <summary>성공을 코드에서 구독한다. 예외가 발생하면 기존 Listener의 오류 처리로 전달된다.</summary>
        public event Action Success;

        /// <summary>실행이나 이벤트 처리 중 발생한 원래 예외를 코드에서 구독한다.</summary>
        public event Action<Exception> Exception;

        /// <summary>직렬화할 조건 설정 목록이다. 최초 초기화 이후 변경은 실행 트리에 반영되지 않는다.</summary>
        public List<ConditionDefinition> Conditions => conditions;

        /// <summary>인스펙터에서 연결할 성공 이벤트다.</summary>
        public UnityEvent OnSuccess => onSuccess;

        /// <summary>인스펙터에서 연결할 오류 메시지 이벤트다.</summary>
        public UnityEvent<string> OnError => onError;

        /// <inheritdoc/>
        public bool IsInitialized => _listener != null && _listener.IsInitialized;

        /// <inheritdoc/>
        public ICondition Condition => _listener?.Condition;

        /// <summary>루트 항목이 여러 개일 때 사용할 결합 방식이다.</summary>
        public ConditionCombineType CombineType
        {
            get => combineType;
            set
            {
                EnsureNotBuilt();
                combineType = value;
            }
        }

        /// <summary>자동 시작 시점이다. 수동 시작도 항상 사용할 수 있다.</summary>
        public ListenerStartMode StartMode
        {
            get => startMode;
            set
            {
                EnsureNotBuilt();
                startMode = value;
            }
        }

        /// <summary>오류 알림 없이 유효하지 않은 조건의 구독을 끝낼지 나타낸다.</summary>
        public bool IsIgnoreNotAvailableCondition
        {
            get => _listener?.IsIgnoreNotAvailableCondition ?? ignoreInvalidCondition;
            set
            {
                EnsureNotBuilt();
                ignoreInvalidCondition = value;
            }
        }

        /// <summary>성공 후 자동으로 다시 시작하지 않을지 나타낸다.</summary>
        public bool IsOneShot
        {
            get => _listener?.IsOneShot ?? isOneShot;
            set
            {
                EnsureNotBuilt();
                isOneShot = value;
            }
        }

        #region unity event

        private void OnEnable()
        {
            if (startMode == ListenerStartMode.OnEnable)
            {
                StartListen();
            }
        }

        private void Start()
        {
            if (startMode == ListenerStartMode.Start)
            {
                StartListen();
            }
        }

        private void OnDisable() => StopListen();

        private void OnDestroy()
        {
            _destroyed = true;
            _coreDiagnostics?.Dispose();
            _coreDiagnostics = null;

            if (_listener is null)
            {
                return;
            }

            _listener.Success -= ForwardSuccess;
            _listener.Exception -= ForwardException;
            _listener.Exception += LogAfterDestroy;

            _ = ObserveStopAsync(_listener);

            Success = null;
            Exception = null;
        }

        #endregion

        /// <summary>설정을 실행 트리로 복사하고 기존 Listener를 한 번 초기화한다.</summary>
        /// <exception cref="InvalidOperationException">설정이 잘못됐거나 컴포넌트가 파괴됐다.</exception>
        public void Initialize()
        {
            if (_destroyed)
            {
                throw new InvalidOperationException("The listener component has been destroyed.");
            }

            try
            {
                PrepareListener();
                _listener.Initialize();
                PreparationError = null;
            }
            catch (Exception error)
            {
                PreparationError = error;
                throw;
            }
        }

        private void PrepareListener()
        {
            if (_listener != null) return;

            ICondition root = ConditionTree.CreateRoot(conditions, combineType);
            _listener = new UnityListener(root)
            {
                IsOneShot = isOneShot,
                IsIgnoreNotAvailableCondition = ignoreInvalidCondition
            };
            _listener.Success += ForwardSuccess;
            _listener.Exception += ForwardException;
            if (_diagnosticsRequests > 0) _coreDiagnostics = _listener.AcquireDiagnostics();
        }

        /// <summary>새 Execution을 시작한다. OneShot 설정에 따라 완료 후 자동 반복할 수 있다.</summary>
        /// <returns>이번 감시와 Cleanup만 가리키는 핸들이다.</returns>
        /// <exception cref="InvalidOperationException">Play Mode의 활성 컴포넌트가 아니거나 실행 또는 Cleanup 중이다.</exception>
        /// <remarks>설정 준비 실패는 PreparationError에 남기고 호출자에게 전달한다. Execution 내부 오류는 Completion의 Result로 받는다.</remarks>
        public ListenerExecution StartExecution()
        {
            if (_destroyed || !Application.isPlaying || !isActiveAndEnabled)
                throw new InvalidOperationException("StartExecution requires an active component in Play Mode.");

            // 실행 중의 중복 호출을 설정 준비 실패로 기록하지 않는다.
            if (_listener?.CurrentExecution != null)
                throw new InvalidOperationException("An execution is already running or stopping.");

            PrepareForExecution();
            return _listener.StartExecution();
        }

        private void PrepareForExecution()
        {
            try
            {
                PrepareListener();
                PreparationError = null;
            }
            catch (Exception error)
            {
                PreparationError = error;
                throw;
            }
        }

        /// <summary>Condition 상세 진단을 요청한다. 초기화나 실행은 하지 않는다. 같은 컨텍스트에서 해제한다.</summary>
        /// <returns>요청을 해제할 핸들이다. 마지막 요청이 해제되면 새 상세 기록을 중단한다.</returns>
        public IDisposable AcquireDiagnostics()
        {
            if (_destroyed) throw new InvalidOperationException("The listener component has been destroyed.");
            _diagnosticsRequests++;
            if (_diagnosticsRequests == 1 && _listener != null)
                _coreDiagnostics = _listener.AcquireDiagnostics();
            return new DiagnosticsRequest(this);
        }

        /// <summary>추가 검사 없이 저장된 현재 또는 마지막 Execution의 Condition 진단을 복사한다.</summary>
        /// <param name="destination">먼저 비운 뒤 진단을 채울 목록이다.</param>
        /// <returns>복사한 항목 수다. 아직 기록이 없으면 0이다.</returns>
        public int CopyConditionDiagnostics(List<ConditionDiagnostic> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            return _listener?.CopyConditionDiagnostics(destination) ?? 0;
        }

        private sealed class DiagnosticsRequest : IDisposable
        {
            private TriggerListenerBehaviour _owner;
            internal DiagnosticsRequest(TriggerListenerBehaviour owner) => _owner = owner;
            public void Dispose()
            {
                TriggerListenerBehaviour owner = _owner;
                if (ReferenceEquals(owner, null)) return;
                _owner = null;
                owner._diagnosticsRequests--;
                if (owner._diagnosticsRequests == 0)
                {
                    owner._coreDiagnostics?.Dispose();
                    owner._coreDiagnostics = null;
                }
            }
        }

        /// <inheritdoc/>
        public void StartListen()
        {
            if (_destroyed || !Application.isPlaying || !isActiveAndEnabled)
            {
                return;
            }

            try
            {
                PrepareForExecution();
                _listener.StartListen();
            }
            catch (Exception error)
            {
                ForwardException(error);
            }
        }

        /// <inheritdoc/>
        public void StopListen() => _listener?.StopListen();

        /// <inheritdoc/>
        public Task StopListenAsync() => _listener?.StopListenAsync() ?? Task.CompletedTask;

        /// <inheritdoc/>
        public void CheckConditionsImmediately()
        {
            if (_destroyed || !Application.isPlaying || !isActiveAndEnabled)
            {
                return;
            }

            try
            {
                Initialize();
                _listener.CheckConditionsImmediately();
            }
            catch (Exception error)
            {
                ForwardException(error);
            }
        }

        private void EnsureNotBuilt()
        {
            if (_listener != null)
            {
                throw new InvalidOperationException("Listener settings are fixed after initialization.");
            }
        }

        private static void LogAfterDestroy(Exception error) => Debug.LogException(error);

        private static async Task ObserveStopAsync(Listener listener)
        {
            try
            {
                await listener.StopListenAsync();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
            finally
            {
                listener.Exception -= LogAfterDestroy;
            }
        }

        private void ForwardSuccess()
        {
            if (_destroyed)
            {
                return;
            }

            onSuccess.Invoke();

            if (!_destroyed)
            {
                Success?.Invoke();
            }
        }

        private void ForwardException(Exception error)
        {
            if (_destroyed)
            {
                return;
            }

            Debug.LogException(error, this);

            try
            {
                onError.Invoke(error.Message);
            }
            catch (Exception handlerError)
            {
                Debug.LogException(handlerError, this);
            }

            if (Exception is null)
            {
                return;
            }

            foreach (Action<Exception> handler in Exception.GetInvocationList())
            {
                try
                {
                    handler(error);
                }
                catch (Exception handlerError)
                {
                    Debug.LogException(handlerError, this);
                }
            }
        }

        private sealed class UnityListener : Listener
        {
            internal UnityListener(ICondition condition) : base(condition)
            {
            }

            // Unity 로그와 이벤트 브리지에서 오류를 처리해 중복 로깅을 피한다.
            protected override void OnInitialize()
            {
            }
        }
    }
}
