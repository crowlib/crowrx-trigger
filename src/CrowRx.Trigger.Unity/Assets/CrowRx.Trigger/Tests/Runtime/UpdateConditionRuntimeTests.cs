using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CrowRx.Trigger.Unity.Tests
{
    public sealed class UpdateConditionRuntimeTests
    {
        private readonly List<ICondition> _conditions = new();
        private readonly List<Task> _checks = new();
        private readonly List<Listener> _listeners = new();
        private readonly List<GameObject> _objects = new();
        private CancellationTokenSource _cancellation;

        [SetUp]
        public void SetUp() => _cancellation = new CancellationTokenSource();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _cancellation.Cancel();
            foreach (Listener listener in _listeners) yield return Drain(listener.StopListenAsync());
            foreach (ICondition condition in _conditions) condition.Deactivate();
            foreach (Task check in _checks)
            {
                Assert.That(check.IsCompleted, Is.True, "Cancellation must release pending checks immediately.");
                if (check.IsFaulted) _ = check.Exception;
            }
            foreach (GameObject item in _objects) if (item) Object.Destroy(item);
            _conditions.Clear();
            _checks.Clear();
            _listeners.Clear();
            _objects.Clear();
            _cancellation.Dispose();
            yield return null;
        }

        [UnityTest]
        public IEnumerator FirstCheckReportsBothInitialStatesOnALaterFrame()
        {
            foreach (bool satisfied in new[] { false, true })
            {
                var condition = Activate(new MutableCondition { Satisfied = satisfied });
                int frame = Time.frameCount;
                Task check = Check(condition);
                Assert.That(check.IsCompleted, Is.False);
                yield return Drain(check);
                Assert.That(Time.frameCount, Is.GreaterThan(frame));
                Assert.That(condition.IsSatisfied, Is.EqualTo(satisfied));
            }
        }

        [UnityTest]
        public IEnumerator StableCheckStaysPendingAndReportsBothSatisfactionTransitions()
        {
            var condition = Activate(new MutableCondition());
            yield return Drain(Check(condition));
            foreach (bool satisfied in new[] { true, false })
            {
                Task check = Check(condition);
                for (int i = 0; i < 8; ++i)
                {
                    yield return null;
                    Assert.That(check.IsCompleted, Is.False);
                }
                condition.Satisfied = satisfied;
                yield return Drain(check);
                Assert.That(condition.IsSatisfied, Is.EqualTo(satisfied));
            }
        }

        [UnityTest]
        public IEnumerator StateChangeBetweenCallsIsNotLost()
        {
            var condition = Activate(new MutableCondition());
            yield return Drain(Check(condition));
            condition.Satisfied = true;
            yield return Drain(Check(condition));
        }

        [UnityTest]
        public IEnumerator ValidityChangesCompleteEvenWhenSatisfactionRemainsFalse()
        {
            var condition = Activate(new MutableCondition());
            yield return Drain(Check(condition));
            foreach (bool valid in new[] { false, true })
            {
                Task check = Check(condition);
                yield return null;
                Assert.That(check.IsCompleted, Is.False);
                condition.Valid = valid;
                yield return Drain(check);
                Assert.That(condition.IsSatisfied, Is.False);
            }
        }

        [UnityTest]
        public IEnumerator CancellationReleasesPendingCheckWithoutWaitingForAnotherFrame()
        {
            var condition = Activate(new MutableCondition());
            yield return Drain(Check(condition));
            Task check = Check(condition);
            yield return null;
            Assert.That(check.IsCompleted, Is.False);
            _cancellation.Cancel();
            Assert.That(check.IsCompleted, Is.True);
            Assert.Throws<OperationCanceledException>(() => check.GetAwaiter().GetResult());
            Task canceled = Check(condition);
            Assert.That(canceled.IsCompleted, Is.True);
            Assert.Throws<OperationCanceledException>(() => canceled.GetAwaiter().GetResult());
        }

        [UnityTest]
        public IEnumerator ReactivationReportsUnchangedStateAgainForBuiltInConditions()
        {
            var target = new GameObject("UpdateConditionTarget");
            _objects.Add(target);
            target.transform.position = Vector3.right * 100000f;
            target.AddComponent<BoxCollider>();
            var sensorObject = new GameObject("UpdateConditionSensor");
            _objects.Add(sensorObject);
            var rigidbody = sensorObject.AddComponent<Rigidbody>();
            rigidbody.isKinematic = true;
            rigidbody.useGravity = false;
            var volume = sensorObject.AddComponent<SphereCollider>();
            volume.isTrigger = true;
            var sensor = sensorObject.AddComponent<UnityTriggerSensor>();
            sensor.Configure(volume, ~0);
            ICondition[] conditions =
            {
                new ActiveStateConditionDefinition { Target = target }.CreateCondition(),
                new TimeConditionDefinition { Duration = 0 }.CreateCondition(),
                new TimeConditionDefinition { Duration = 0, IsNot = true }.CreateCondition(),
                new TriggerOverlapConditionDefinition { Sensor = sensor, Target = target }.CreateCondition()
            };
            foreach (ICondition condition in conditions)
            {
                Activate(condition);
                yield return Drain(Check(condition));
                Task pending = Check(condition);
                yield return null;
                Assert.That(pending.IsCompleted, Is.False);
                // A previous execution must drain before the same condition is activated again.
                _cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
                _cancellation.Dispose();
                _cancellation = new CancellationTokenSource();
                condition.Deactivate();
                condition.Activate();
                yield return Drain(Check(condition));
            }
        }

        [UnityTest]
        public IEnumerator AndGroupDoesNotRecheckStableChildrenEveryFrame() => VerifyGroup(ConditionCombineType.And, false);

        [UnityTest]
        public IEnumerator OrGroupDoesNotRecheckStableChildrenEveryFrame() => VerifyGroup(ConditionCombineType.Or, false);

        [UnityTest]
        public IEnumerator NegatedAndGroupObservesFallingSatisfaction() => VerifyGroup(ConditionCombineType.And, true);

        [UnityTest]
        public IEnumerator NegatedOrGroupObservesFallingSatisfaction() => VerifyGroup(ConditionCombineType.Or, true);

        private IEnumerator VerifyGroup(ConditionCombineType combineType, bool isNot)
        {
            var first = new MutableCondition { Satisfied = combineType == ConditionCombineType.And || isNot };
            var second = new MutableCondition { Satisfied = combineType == ConditionCombineType.And && isNot };
            var group = new InvertibleGroup(combineType, isNot);
            group.AddCondition(first);
            group.AddCondition(second);
            var listener = new Listener(group) { IsOneShot = true };
            _listeners.Add(listener);
            ListenerExecution execution = listener.StartExecution();
            for (int i = 0; i < 12; ++i) yield return null;
            Assert.That(execution.Completion.IsCompleted, Is.False);
            Assert.That(first.CheckCalls, Is.EqualTo(2), "A stable child should retain its second check.");
            Assert.That(second.CheckCalls, Is.EqualTo(2));

            if (combineType == ConditionCombineType.And && !isNot)
            {
                first.Satisfied = false;
                for (int i = 0; i < 4; ++i) yield return null;
                second.Satisfied = true;
                for (int i = 0; i < 4; ++i) yield return null;
                Assert.That(execution.Completion.IsCompleted, Is.False, "A previously satisfied child is now false.");
                first.Satisfied = true;
            }
            else if (isNot) first.Satisfied = false;
            else second.Satisfied = true;

            yield return Drain(execution.Completion);
            Assert.That(execution.Completion.Result.Kind, Is.EqualTo(ListenerResultKind.Satisfied));
            Assert.That(first.IsActivated, Is.False);
            Assert.That(second.IsActivated, Is.False);
        }

        private T Activate<T>(T condition) where T : ICondition
        {
            _conditions.Add(condition);
            condition.Initialize();
            condition.Activate();
            return condition;
        }

        private Task Check(ICondition condition)
        {
            Task check = condition.CheckAsync(_cancellation.Token);
            _checks.Add(check);
            return check;
        }

        private static IEnumerator Drain(Task task)
        {
            for (int i = 0; i < 60 && !task.IsCompleted; ++i) yield return null;
            Assert.That(task.IsCompleted, Is.True, "The check did not complete within 60 frames.");
            task.GetAwaiter().GetResult();
        }

        private sealed class MutableCondition : UpdateCondition
        {
            internal bool Valid = true;
            internal bool Satisfied;
            internal int CheckCalls;

            internal MutableCondition() : base(false) { }
            public override bool IsValid => Valid;
            public override bool IsSatisfied => Valid && Satisfied;
            public override Task CheckAsync(CancellationToken cancellationToken)
            {
                ++CheckCalls;
                return base.CheckAsync(cancellationToken);
            }
        }

        private sealed class InvertibleGroup : ConditionGroup
        {
            internal InvertibleGroup(ConditionCombineType combineType, bool isNot)
            {
                CombineType = combineType;
                IsNot = isNot;
            }
        }
    }
}
