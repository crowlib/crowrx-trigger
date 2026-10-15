using System;
using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>시간 조건이 사용하는 시간 기준이다.</summary>
    public enum ConditionTimeSource
    {
        /// <summary>Time.timeScale의 영향을 받는 게임 시간이다.</summary>
        [InspectorName("게임 시간")] GameTime,

        /// <summary>Time.timeScale의 영향을 받지 않는 실제 시간이다.</summary>
        [InspectorName("실제 시간")] UnscaledTime
    }

    /// <summary>활성화한 뒤 지정 시간이 지났는지 검사하는 설정이다.</summary>
    [Serializable, ConditionMenu("시간 경과")]
    public sealed class TimeConditionDefinition : ConditionDefinition
    {
        [SerializeField, Min(0)] private float duration = 1f;
        [SerializeField] private ConditionTimeSource timeSource;
        [SerializeField] private bool isNot;

        /// <summary>기다릴 초 단위 시간이다. 0 이상인 유한한 값을 지정한다.</summary>
        public float Duration
        {
            get => duration;
            set => duration = value;
        }

        /// <summary>게임 시간 또는 실제 시간을 가져오거나 설정한다.</summary>
        public ConditionTimeSource TimeSource
        {
            get => timeSource;
            set => timeSource = value;
        }

        /// <summary>만족 결과를 반전할지 가져오거나 설정한다.</summary>
        public bool IsNot
        {
            get => isNot;
            set => isNot = value;
        }

        /// <inheritdoc/>
        protected override ICondition CreateRuntimeCondition() => new RuntimeTimeCondition(Duration, TimeSource, IsNot);

        private sealed class RuntimeTimeCondition : UpdateCondition
        {
            private readonly float _duration;
            private readonly ConditionTimeSource _source;
            private double _start;
            private bool _started;

            internal RuntimeTimeCondition(float duration, ConditionTimeSource source, bool isNot) : base(isNot)
            {
                _duration = duration;
                _source = source;
            }

            private double Now => _source == ConditionTimeSource.GameTime ? Time.timeAsDouble : Time.unscaledTimeAsDouble;

            public override bool IsValid => _duration >= 0 && !float.IsInfinity(_duration) && !float.IsNaN(_duration) && _source is ConditionTimeSource.GameTime or ConditionTimeSource.UnscaledTime;

            public override bool IsSatisfied => IsValid && ((_started && Now - _start >= _duration) != IsNot);

            protected override void OnActivate()
            {
                _start = Now;
                _started = true;
            }

            protected override void OnDeactivate()
            {
                _started = false;
            }
        }
    }
}