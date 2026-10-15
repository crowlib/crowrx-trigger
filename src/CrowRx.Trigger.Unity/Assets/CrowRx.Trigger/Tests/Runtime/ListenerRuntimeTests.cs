using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CrowRx.Trigger.Unity.Tests
{
    public class ListenerRuntimeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private float _timeScale;
        [SetUp] public void SetUp() { _timeScale = Time.timeScale; }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = _timeScale;
            foreach (var gameObject in _objects) if (gameObject) Object.Destroy(gameObject);
            _objects.Clear();
            yield return null; yield return null;
        }
        private GameObject MakeObject(string name)
        { var result = new GameObject(name); _objects.Add(result); return result; }
        private TriggerListenerBehaviour MakeListener(ConditionDefinition definition, bool oneShot = true, ListenerStartMode mode = ListenerStartMode.Manual)
        {
            var go = MakeObject("Listener"); go.SetActive(false);
            var listener = go.AddComponent<TriggerListenerBehaviour>();
            listener.StartMode = mode; listener.IsOneShot = oneShot;
            listener.Conditions.Add(definition);
            return listener;
        }
        private static IEnumerator Drain(Task task)
        {
            for (int i = 0; i < 100 && !task.IsCompleted; i++) yield return null;
            Assert.That(task.IsCompleted, Is.True, "The condition did not stop within 100 frames.");
            task.GetAwaiter().GetResult();
        }

        [UnityTest]
        public IEnumerator OneShotDeliversBothEventsOnceAndReusesItsCondition()
        {
            var target = MakeObject("Target");
            var listener = MakeListener(new ActiveStateConditionDefinition { Target = target });
            var code = 0; var unity = 0;
            listener.Success += () => code++; listener.OnSuccess.AddListener(() => unity++);
            listener.gameObject.SetActive(true); listener.StartListen();
            var runtime = listener.Condition;
            Assert.That(code, Is.Zero);
            for (int i = 0; i < 20 && code == 0; i++) yield return null;
            yield return Drain(listener.StopListenAsync());
            Assert.That(code, Is.EqualTo(1)); Assert.That(unity, Is.EqualTo(1));
            listener.StartListen();
            for (int i = 0; i < 20 && code < 2; i++) yield return null;
            Assert.That(listener.Condition, Is.SameAs(runtime));
            yield return Drain(listener.StopListenAsync());
            Assert.That(code, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator RepeatingListenerStopsAndDoesNotDeliverLaterCallbacks()
        {
            var listener = MakeListener(new ActiveStateConditionDefinition { Target = MakeObject("Target") }, false);
            int count = 0; listener.Success += () => count++;
            listener.gameObject.SetActive(true); listener.StartListen();
            for (int i = 0; i < 40 && count < 3; i++) yield return null;
            Assert.That(count, Is.GreaterThanOrEqualTo(3));
            yield return Drain(listener.StopListenAsync());
            var stopped = count;
            yield return null; yield return null;
            Assert.That(count, Is.EqualTo(stopped));
            Assert.That(listener.Condition.IsActivated, Is.False);
        }

        [UnityTest]
        public IEnumerator RapidDisableEnableKeepsOneActivationAndReusesTree()
        {
            var counts = new Counts();
            var listener = MakeListener(new CountingDefinition(counts), false, ListenerStartMode.OnEnable);
            listener.gameObject.SetActive(true);
            var runtime = listener.Condition;
            for (int i = 0; i < 8; i++)
            { listener.gameObject.SetActive(false); listener.gameObject.SetActive(true); yield return null; }
            listener.gameObject.SetActive(false);
            yield return Drain(listener.StopListenAsync());
            Assert.That(listener.Condition, Is.SameAs(runtime));
            Assert.That(counts.Peak, Is.EqualTo(1));
            Assert.That(counts.Active, Is.Zero);
            Assert.That(counts.Activations, Is.GreaterThan(1));
        }

        [UnityTest]
        public IEnumerator PendingSharedAndNestedChecksDrainOnStop()
        {
            var nested = new ConditionGroupDefinition();
            nested.Conditions.Add(new TimeConditionDefinition { Duration = 100 });
            var shared = new SharedTargetGroupDefinition { Target = MakeObject("Target") };
            shared.Conditions.Add(nested);
            shared.Conditions.Add(new ActiveStateConditionDefinition());
            var listener = MakeListener(shared);
            listener.gameObject.SetActive(true); listener.StartListen();
            yield return null;
            yield return Drain(listener.StopListenAsync());
            Assert.That(listener.Condition.IsActivated, Is.False);
            var group = (ConditionGroup)listener.Condition;
            Assert.That(group.Conditions[0].IsActivated, Is.False);
            Assert.That(group.Conditions[1].IsActivated, Is.False);
        }

        [UnityTest]
        public IEnumerator DestroyingSourceComponentDoesNotDestroyItsExecutionState()
        {
            var target = MakeObject("Target"); target.SetActive(false);
            var source = MakeObject("Source").AddComponent<ActiveStateConditionBehaviour>(); source.Settings.Target = target;
            var listener = MakeListener(new ComponentConditionReference { Component = source });
            int count = 0; listener.Success += () => count++;
            listener.gameObject.SetActive(true); listener.StartListen();
            source.enabled = false; Object.Destroy(source); yield return null;
            Assert.That(listener.Condition.IsValid, Is.True);
            target.SetActive(true);
            for (int i = 0; i < 20 && count == 0; i++) yield return null;
            Assert.That(count, Is.EqualTo(1));
            yield return Drain(listener.StopListenAsync());
        }

        [UnityTest]
        public IEnumerator TwoListenersCanShareOneComponentWithDifferentTargets()
        {
            var firstTarget = MakeObject("First");
            var secondTarget = MakeObject("Second"); secondTarget.SetActive(false);
            var source = MakeObject("Source").AddComponent<ActiveStateConditionBehaviour>();
            var firstGroup = new SharedTargetGroupDefinition { Target = firstTarget };
            var secondGroup = new SharedTargetGroupDefinition { Target = secondTarget };
            firstGroup.Conditions.Add(new ComponentConditionReference { Component = source });
            secondGroup.Conditions.Add(new ComponentConditionReference { Component = source });
            var first = MakeListener(firstGroup); var second = MakeListener(secondGroup);
            int a = 0, b = 0; first.Success += () => a++; second.Success += () => b++;
            first.gameObject.SetActive(true); second.gameObject.SetActive(true);
            first.StartListen(); second.StartListen();
            for (int i = 0; i < 20 && a == 0; i++) yield return null;
            Assert.That(a, Is.EqualTo(1)); Assert.That(b, Is.Zero);
            Assert.That(source.Settings.Target, Is.Null);
            secondTarget.SetActive(true);
            for (int i = 0; i < 20 && b == 0; i++) yield return null;
            Assert.That(b, Is.EqualTo(1));
            yield return Drain(first.StopListenAsync()); yield return Drain(second.StopListenAsync());
        }

        [UnityTest]
        public IEnumerator DestroyedActualTargetReportsErrorAndStops()
        {
            var target = MakeObject("Target"); target.SetActive(false);
            var listener = MakeListener(new ActiveStateConditionDefinition { Target = target });
            int codeErrors = 0, unityErrors = 0;
            listener.Exception += _ => codeErrors++;
            listener.OnError.AddListener(_ => unityErrors++);
            listener.gameObject.SetActive(true); listener.StartListen();
            LogAssert.Expect(LogType.Exception, new Regex("Condition is not available"));
            Object.Destroy(target);
            for (int i = 0; i < 30 && codeErrors == 0; i++) yield return null;
            yield return Drain(listener.StopListenAsync());
            Assert.That(codeErrors, Is.EqualTo(1)); Assert.That(unityErrors, Is.EqualTo(1));
            Assert.That(listener.Condition.IsActivated, Is.False);
        }

        [UnityTest]
        public IEnumerator DestroyingListenerCancelsPendingChecksWithoutSuccess()
        {
            var listener = MakeListener(new TimeConditionDefinition { Duration = 100 });
            int success = 0; listener.Success += () => success++;
            listener.gameObject.SetActive(true); listener.StartListen();
            var runtime = listener.Condition;
            Object.Destroy(listener);
            for (int i = 0; i < 20 && runtime.IsActivated; i++) yield return null;
            Assert.That(runtime.IsActivated, Is.False); Assert.That(success, Is.Zero);
        }

        [UnityTest]
        public IEnumerator TimeSourcesAndReactivationRespectTimerLifetime()
        {
            Time.timeScale = 0;
            var game = new TimeConditionDefinition { Duration = .02f }.CreateCondition();
            var real = new TimeConditionDefinition { Duration = .02f, TimeSource = ConditionTimeSource.UnscaledTime }.CreateCondition();
            game.Initialize(); real.Initialize(); game.Activate(); real.Activate();
            yield return new WaitForSecondsRealtime(.05f);
            Assert.That(game.IsSatisfied, Is.False); Assert.That(real.IsSatisfied, Is.True);
            real.Deactivate(); real.Activate(); Assert.That(real.IsSatisfied, Is.False);
            yield return new WaitForSecondsRealtime(.05f); Assert.That(real.IsSatisfied, Is.True);
            game.Deactivate(); real.Deactivate();
        }

        [UnityTest]
        public IEnumerator NotTimeIsObservableBeforeTheTimerExpires()
        {
            var listener = MakeListener(new TimeConditionDefinition { Duration = 100, IsNot = true });
            int count = 0; listener.Success += () => count++;
            listener.gameObject.SetActive(true); listener.StartListen();
            for (int i = 0; i < 20 && count == 0; i++) yield return null;
            Assert.That(count, Is.EqualTo(1));
            yield return Drain(listener.StopListenAsync());
        }

        private sealed class Counts { internal int Active, Peak, Activations; }
        [Serializable]
        private sealed class CountingDefinition : ConditionDefinition
        {
            private readonly Counts _counts;
            internal CountingDefinition(Counts counts) { _counts = counts; }
            protected override ICondition CreateRuntimeCondition() => new CountingCondition(_counts);
        }
        private sealed class CountingCondition : UpdateCondition
        {
            private readonly Counts _counts;
            internal CountingCondition(Counts counts) : base(false) { _counts = counts; }
            public override bool IsValid => true;
            public override bool IsSatisfied => false;
            protected override void OnActivate() { _counts.Active++; _counts.Activations++; _counts.Peak = Math.Max(_counts.Peak, _counts.Active); }
            protected override void OnDeactivate() { _counts.Active--; }
        }
    }
}
