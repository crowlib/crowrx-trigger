using System;
using System.Threading;
using System.Threading.Tasks;

namespace CrowRx.Trigger
{
    /// <summary>
    /// 초기화와 활성화 수명 주기를 관리하는 조건의 기본 구현이다.
    /// </summary>
    /// <remarks>파생 클래스에서 검사와 만족 여부를 구현한다. <see cref="IsNot"/>의 반전도 파생 클래스에서 반영한다.</remarks>
    public abstract class Condition : ICondition
    {
        /// <inheritdoc/>
        public abstract bool IsValid { get; }

        /// <inheritdoc/>
        public bool IsInitialized { get; private set; }
        /// <inheritdoc/>
        public bool IsActivated { get; private set; }

        /// <summary>만족 결과를 반전할지 가져오거나 파생 클래스에서 설정한다. 기본값은 false다.</summary>
        public virtual bool IsNot { get; protected set; }
        /// <inheritdoc/>
        public abstract bool IsSatisfied { get; }

        /// <inheritdoc/>
        public void Initialize()
        {
            if (IsInitialized)
            {
                return;
            }

            OnInitialize();

            IsInitialized = true;
        }

        /// <summary>조건을 활성화한다. 활성화에 실패하면 비활성화를 시도한 뒤 원래 오류를 전달한다.</summary>
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

        /// <inheritdoc/>
        public void Deactivate()
        {
            if (!IsActivated)
            {
                return;
            }

            IsActivated = false;

            OnDeactivate();
        }

        /// <inheritdoc/>
        public abstract Task CheckAsync(CancellationToken cancellationToken);

        /// <summary>최초 초기화에 필요한 설정을 수행한다. 실패하면 다음 초기화 호출에서 다시 시도한다.</summary>
        protected virtual void OnInitialize()
        {
        }

        /// <summary>활성화에 필요한 자원을 준비한다. 호출 시점의 활성화 상태는 true다.</summary>
        protected virtual void OnActivate()
        {
        }

        /// <summary>활성화 중 사용한 자원을 정리한다. 호출 시점의 활성화 상태는 false다.</summary>
        protected virtual void OnDeactivate()
        {
        }
    }
}
