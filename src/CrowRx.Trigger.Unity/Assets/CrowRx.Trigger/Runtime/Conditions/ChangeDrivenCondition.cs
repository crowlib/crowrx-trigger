using System;
using System.Threading;
using System.Threading.Tasks;

namespace CrowRx.Trigger.Unity
{
    /// <summary>처음 한 번 상태를 읽은 뒤 변경 통지까지 대기합니다. 사용과 통지는 Unity main thread에서 수행합니다.</summary>
    public abstract class ChangeDrivenCondition : Condition
    {
        private TaskCompletionSource<bool> _pending;
        private CancellationToken _pendingToken;
        private CancellationTokenRegistration _registration;
        private bool _observed;
        private bool _changed;

        /// <summary>최초 상태 또는 변경 통지를 기다린다. 같은 토큰의 미완료 검사는 재사용한다.</summary>
        /// <exception cref="InvalidOperationException">미완료 검사가 있는데 다른 취소 토큰을 전달했다.</exception>
        public override Task CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValidateCheck();

            if (!_observed || _changed)
            {
                _observed = true;
                _changed = false;

                return Task.CompletedTask;
            }

            if (_pending is not null && !_pending.Task.IsCompleted)
            {
                if (_pendingToken != cancellationToken)
                {
                    throw new InvalidOperationException("A pending check cannot be reused with a different cancellation token.");
                }

                return _pending.Task;
            }

            _registration.Dispose();
            _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingToken = cancellationToken;
            _registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state).TrySetCanceled(), _pending, false);

            return _pending.Task;
        }

        protected sealed override void OnActivate()
        {
            _observed = false;
            _changed = false;

            Subscribe();
        }

        protected sealed override void OnDeactivate()
        {
            try
            {
                Unsubscribe();
            }
            finally
            {
                _registration.Dispose();

                TaskCompletionSource<bool> pending = _pending;
                _pending = null;
                _pendingToken = default;

                _observed = false;
                _changed = false;

                pending?.TrySetCanceled();
            }
        }

        protected abstract void Subscribe();
        protected abstract void Unsubscribe();
        protected virtual void ValidateCheck() { }

        protected void NotifyStateChanged()
        {
            _changed = true;

            TaskCompletionSource<bool> pending = _pending;
            _pending = null;
            _pendingToken = default;

            _registration.Dispose();

            if (pending != null && !pending.Task.IsCompleted)
            {
                _changed = false;

                pending.TrySetResult(true);
            }
        }
    }
}
