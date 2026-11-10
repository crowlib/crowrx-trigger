using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace CrowRx.Trigger.Unity.Tests
{
    public sealed class ChangeDrivenConditionRuntimeTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void DifferentTokenIsRejectedWithoutReplacingPendingCheck(bool cancelable)
        {
            using var original = new CancellationTokenSource();
            using var replacement = new CancellationTokenSource();
            using var condition = new Probe();
            condition.Activate();
            CancellationToken token = cancelable ? original.Token : CancellationToken.None;
            Assert.That(condition.CheckAsync(token).IsCompleted, Is.True);
            Task pending = condition.CheckAsync(token);

            Assert.Throws<InvalidOperationException>(() => condition.CheckAsync(replacement.Token));
            Assert.That(condition.CheckAsync(token), Is.SameAs(pending));
            replacement.Cancel();
            Assert.That(pending.IsCompleted, Is.False);

            if (cancelable)
            {
                original.Cancel();
                Assert.That(pending.IsCanceled, Is.True);
            }
            else
            {
                condition.Signal();
                Assert.That(pending.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            }
        }

        [Test]
        public void NotificationAllowsNextCheckToUseAnotherToken()
        {
            using var replacement = new CancellationTokenSource();
            using var condition = new Probe();
            condition.Activate();
            condition.CheckAsync(CancellationToken.None);
            Task previous = condition.CheckAsync(CancellationToken.None);
            condition.Signal();
            Assert.That(previous.Status, Is.EqualTo(TaskStatus.RanToCompletion));

            Task pending = condition.CheckAsync(replacement.Token);
            Assert.That(pending, Is.Not.SameAs(previous));
            Assert.That(pending.IsCompleted, Is.False);
            replacement.Cancel();
            Assert.That(pending.IsCanceled, Is.True);
        }

        [Test]
        public void CancellationAllowsNextCheckToUseAnotherToken()
        {
            using var original = new CancellationTokenSource();
            using var replacement = new CancellationTokenSource();
            using var condition = new Probe();
            condition.Activate();
            condition.CheckAsync(original.Token);
            Task previous = condition.CheckAsync(original.Token);
            original.Cancel();
            Assert.That(previous.IsCanceled, Is.True);

            Task pending = condition.CheckAsync(replacement.Token);
            Assert.That(pending.IsCompleted, Is.False);
            replacement.Cancel();
            Assert.That(pending.IsCanceled, Is.True);
        }

        [Test]
        public void ReactivationClearsPreviousPendingToken()
        {
            using var replacement = new CancellationTokenSource();
            using var condition = new Probe();
            condition.Activate();
            condition.CheckAsync(CancellationToken.None);
            Task previous = condition.CheckAsync(CancellationToken.None);
            condition.Deactivate();
            Assert.That(previous.IsCanceled, Is.True);

            condition.Activate();
            Assert.That(condition.CheckAsync(replacement.Token).IsCompleted, Is.True);
            Task pending = condition.CheckAsync(replacement.Token);
            Assert.That(condition.CheckAsync(replacement.Token), Is.SameAs(pending));
            replacement.Cancel();
            Assert.That(pending.IsCanceled, Is.True);
        }

        private sealed class Probe : ChangeDrivenCondition, IDisposable
        {
            public override bool IsValid => true;
            public override bool IsSatisfied => false;
            protected override void Subscribe() { }
            protected override void Unsubscribe() { }
            internal void Signal() => NotifyStateChanged();
            public void Dispose() => Deactivate();
        }
    }
}
