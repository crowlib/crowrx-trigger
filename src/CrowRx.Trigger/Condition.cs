using System;
using System.Threading;
using System.Threading.Tasks;

namespace CrowRx.Trigger
{
    /// <summary>
    /// <see cref="ICondition"/> 기본 구현 class.
    /// </summary>
    public abstract class Condition : ICondition
    {
        public abstract bool IsValid { get; }

        public bool IsInitialized { get; private set; }
        public bool IsActivated { get; private set; }

        public virtual bool IsNot { get; protected set; }
        public abstract bool IsSatisfied { get; }

        public void Initialize()
        {
            if (IsInitialized)
            {
                return;
            }

            OnInitialize();

            IsInitialized = true;
        }

        public void Activate()
        {
            if (IsActivated)
            {
                return;
            }

            IsActivated = true;

            try
            {
                OnActivate();
            }
            catch
            {
                // 부분적으로 활성화된 자원과 그룹의 자식도 정리한 뒤 원래 실패를 전달한다.
                try
                {
                    Deactivate();
                }
                catch (Exception cleanupError)
                {
                    try
                    {
                        Log.Exception(cleanupError);
                    }
                    catch (Exception) { }
                }

                throw;
            }
        }

        public void Deactivate()
        {
            if (!IsActivated)
            {
                return;
            }

            IsActivated = false;

            OnDeactivate();
        }

        public abstract Task CheckAsync(CancellationToken cancellationToken);

        protected virtual void OnInitialize()
        {
        }

        protected virtual void OnActivate()
        {
        }

        protected virtual void OnDeactivate()
        {
        }
    }
}
