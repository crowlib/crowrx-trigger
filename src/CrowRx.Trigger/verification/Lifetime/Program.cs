using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CrowRx.Trigger;

sealed class Pump : SynchronizationContext
{
    readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
    public override void Post(SendOrPostCallback callback, object? state) => queue.Add(() => callback(state));
    public void Run(Func<Task> body)
    {
        SetSynchronizationContext(this);
        Task task = body();
        while (!task.IsCompleted) queue.Take()();
        task.GetAwaiter().GetResult();
    }
}
sealed class Fake : Condition
{
    public Func<CancellationToken, Task> Check = _ => Task.CompletedTask;
    public bool Satisfied;
    public bool Valid = true;
    public Action? Activated, Deactivated, Initialized;
    protected override void OnInitialize() => Initialized?.Invoke();
    public int Calls, Active, MaxActive, Canceled, Activations, Deactivations;
    public override bool IsValid => Valid;
    public override bool IsSatisfied => Satisfied;
    protected override void OnActivate() { ++Activations; Activated?.Invoke(); }
    protected override void OnDeactivate() { ++Deactivations; Deactivated?.Invoke(); }
    public override async Task CheckAsync(CancellationToken token)
    {
        ++Calls; ++Active; MaxActive = Math.Max(MaxActive, Active);
        try { await Check(token); }
        catch (OperationCanceledException) { ++Canceled; throw; }
        finally { --Active; }
    }
}
sealed class Group : ConditionGroup
{
    public Group(ConditionCombineType combine, params ICondition[] children)
    { CombineType = combine; AddConditions(children); }
}
sealed class TestListener : Listener
{
    public readonly List<Exception> Errors = new List<Exception>();
    public TestListener(ICondition condition) : base(condition) { Exception += Errors.Add; }
    protected override void OnInitialize() { }
}
sealed class EqualCondition : Condition
{
    public override bool Equals(object? obj) => obj is EqualCondition;
    public override int GetHashCode() => 0;
    public override bool IsValid => true;
    public override bool IsSatisfied => true;
    public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
}
sealed class ValidationProbe : ConditionGroup
{
    public Action? ReadingConditions;
    public override List<ICondition>? Conditions
    {
        get { Action? read = ReadingConditions; ReadingConditions = null; read?.Invoke(); return base.Conditions; }
        protected set => base.Conditions = value;
    }
    public void ValidateNow() => ValidateConditionGraph();
}
sealed class Bad : Condition
{
    readonly Exception? error;
    public Bad(Exception? error) => this.error = error;
    public override bool IsValid => true;
    public override bool IsSatisfied => false;
    public override Task CheckAsync(CancellationToken token) { if (error != null) throw error; return null!; }
}
sealed class AddingGroup : ConditionGroup
{
    public Action<ICondition>? Adding;
    protected override void OnConditionAdding(ICondition condition) => Adding?.Invoke(condition);
}
sealed class TargetCondition : Condition, IConditionTargetSharable<object>
{
    public object? Target { get; set; }
    public object? TargetAtInitialize;
    public override bool IsValid => Target != null;
    public override bool IsSatisfied => false;
    public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
    protected override void OnInitialize() => TargetAtInitialize = Target;
}
sealed class InitializingListener : Listener
{
    ICondition? current;
    readonly ICondition replacement;
    public override ICondition? Condition => current;
    public readonly List<Exception> Errors = new List<Exception>();
    public InitializingListener(ICondition? original, ICondition replacement)
    { current = original; this.replacement = replacement; Exception += Errors.Add; }
    protected override void OnInitialize() { current = replacement; }
}
sealed class FaultTarget : Condition, IConditionTargetSharable<object>
{
    object? target;
    public Action<object?>? BeforeSet, AfterSet;
    public int Assignments;
    public object? Target { get => target; set { BeforeSet?.Invoke(value); target = value; ++Assignments; AfterSet?.Invoke(value); } }
    public override bool IsValid => Target != null;
    public override bool IsSatisfied => false;
    public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
}
sealed class CaptureLogger : CrowRx.ILogger
{
    public readonly List<Exception> Errors = new List<Exception>();
    public bool Throw;
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
    public void Assertion(string message) { }
    public void Exception(Exception error) { if (Throw) throw new Exception("logger failed"); Errors.Add(error); }
}
static class Program
{
    static TaskCompletionSource<bool> Gate() => new TaskCompletionSource<bool>();
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Until(Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!done()) { if (DateTime.UtcNow > deadline) throw new Exception("Timed out"); await Task.Delay(1); }
    }
    static async Task PreservePending()
    {
        var gate = Gate();
        var fast = new Fake { Satisfied = true };
        var slow = new Fake { Check = token => gate.Task.WaitAsync(token) };
        var group = new Group(ConditionCombineType.And, fast, slow);
        using var cts = new CancellationTokenSource();
        group.Initialize(); group.Activate();
        try
        {
            for (int i = 0; i < 3; ++i) await group.CheckAsync(cts.Token);
            Require(slow.Calls == 1 && slow.Active == 1 && slow.Canceled == 0,
                $"slow calls={slow.Calls}, active={slow.Active}, canceled={slow.Canceled}");
        }
        finally { cts.Cancel(); gate.TrySetResult(true); group.Deactivate(); await Until(() => slow.Active == 0); }
    }
    static async Task RestartBarrier()
    {
        var entered = Gate(); var release = Gate();
        var condition = new Fake { Check = async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var listener = new TestListener(condition);
        listener.StartListen(); listener.StopListen(); await entered.Task;
        listener.StartListen();
        bool overlapped = condition.Calls != 1 || condition.Activations != 1 || condition.MaxActive != 1;
        release.TrySetResult(true);
        try
        {
            Require(!overlapped, $"calls={condition.Calls}, activations={condition.Activations}, max active={condition.MaxActive}");
            await Until(() => condition.Calls == 2);
        }
        finally { listener.StopListen(); await Until(() => condition.Active == 0 && condition.Deactivations >= 1); }
    }
    static async Task Run()
    {
        int passed = 0;
        var tests = new List<(string, Func<Task>)> {
            ("pending child survives successive rounds", PreservePending),
            ("stop/start waits for asynchronous cleanup", RestartBarrier),
            ("AND eventually succeeds while its pending child progresses", AndSuccess),
            ("nested OR cleanup drains cached leaf tasks before deactivation", NestedCleanup),
            ("completed pending failure cannot be masked by a fast child", PendingFailure),
            ("synchronous startup failure drains started children", () => StartupFailure(false)),
            ("null task startup failure drains started children", () => StartupFailure(true)),
            ("external cancellation waits for child cleanup", CancellationBarrier),
            ("success handler stop suppresses automatic restart", SuccessStop),
            ("success handler stop/start queues one nonoverlapping run", SuccessRestart),
            ("throwing cancellation callback still drains children", ThrowingCancellation),
            ("changing caller token drains previous session", TokenChange),
            ("mutation of an active condition list fails and cleans up", Mutation),
            ("empty groups can be checked repeatedly", EmptyGroup),
            ("automatic restart waits for nested leaf cleanup", AutomaticRestart),
            ("direct deactivation serializes the next check", DirectDeactivate),
            ("overlapping CheckAsync calls are rejected", ConcurrentCheck),
            ("failure during cleanup is reported after all children drain", CleanupFailure),
            ("completion is reported before a child resets its satisfied state", CompletionBeforeRestart),
            ("nested completion is reported before child rearming", NestedCompletionBeforeRestart),
            ("listener sees completion during its yield without resetting it", ListenerCompletionBeforeRestart),
            ("deactivation attempts all children despite multiple failures", DeactivateAll),
            ("immediate check rejects empty and invalid groups", ImmediateInvalid),
            ("throwing immediate success handler still stops and reports", ImmediateHandlerFailure),
            ("unrequested cancellation is reported as an error", UnexpectedCancellation),
            ("late-added child receives target before initialization", LateTarget),
            ("target changes propagate through nested groups", TargetChange),
            ("activation failure rolls back and can be retried", ActivationRollback),
            ("partial group activation rolls back every child", GroupActivationRollback),
            ("target mutation waits until active checks are drained", ActiveTargetChange),
            ("condition created or replaced during initialization is used", InitializedCondition),
            ("duplicate direct registration is rejected", DuplicateRegistration),
            ("duplicate leaf in nested branches is rejected", DuplicateNestedRegistration),
            ("self and ancestor registration are rejected", CycleRegistration),
            ("addition to an activated group is rejected", ActiveRegistration),
            ("initialization retry does not duplicate diagnostic handlers", InitializationRetry),
            ("asynchronous stop waits for cleanup and permits immediate restart", AsyncStop),
            ("asynchronous stop drains nested retained checks", AsyncStopNested),
            ("asynchronous stop completes when idle or cleanup fails", AsyncStopFailure),
            ("directly mutated cycles fail without recursive initialization", DirectCycle),
            ("directly mutated duplicates fail before child checks start", DirectDuplicate),
            ("failed target update restores parent and all touched children", TargetRollback),
            ("nested target rollback restores distinct descendant values", NestedTargetRollback),
            ("throwing exception subscribers do not skip later subscribers", ExceptionSubscribers),
            ("target rollback failures preserve all errors and continue recovery", TargetRecoveryFailure),
            ("active nested target group is rejected before parent changes", ActiveNestedTarget),
            ("reentrant target update is rejected and previous values restored", ReentrantTarget),
            ("all-pending checks still report and retain other children", AllPendingChecks),
            ("reused validation buffers reject later mutations and recover", ValidationReuse),
            ("validation buffers release temporary condition references", ValidationReferences),
            ("pooled validation preserves reference identity across groups", ValidationIdentity),
            ("reentrant validation keeps outer traversal and returns failed leases", ValidationReentrancy),
            ("addition hook rejects recursive additions and permits retry", () => RecursiveAddition(false)),
            ("child initialization rejects recursive additions and permits retry", () => RecursiveAddition(true)),
            ("initialization activation cannot append an inactive child", AdditionActivates),
            ("initialization check cannot append after a check snapshot", AdditionStartsCheck),
            ("addition hook graph mutation is checked before initialization", () => AdditionMutates(false)),
            ("initialization graph mutation is checked before append", () => AdditionMutates(true)),
            ("inactive group stop drains an in-progress check and permits reuse", StopInactiveCheck),
            ("inactive outer stop drains retained nested checks", StopInactiveNested),
            ("group stop preserves activation state and drains before reporting failures", StopGroupFailure),
            ("target snapshot pools release removed children after success and rollback", TargetPoolReferences),
            ("cancel callback stop request shares the original completion task", ReentrantAsyncStop),
            ("late asynchronous stop waits for cleanup and excludes the queued successor", LateAsyncStop),
            ("stop after synchronous check failure completes the session handoff", CompletedFailureStop),
            ("varying session sizes preserve child counts across reuse", SessionBufferSizes),
            ("draining nested sessions keep their snapshots while other groups borrow", SessionBufferDrain),
            ("stopped sessions release removed condition references", SessionBufferReferences),
            ("completion during later child startup preserves pending checks and rearming", CompletionDuringStartup)
        };
        tests.AddRange(ExecutionTests.Cases());
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--logging") >= 0)
        {
            tests.Add(("primary and handler failures are logged exactly once", ExceptionLogging));
            tests.Add(("failed observer without default logger retains primary failure", ExceptionLoggingWithoutDefault));
            tests.Add(("failing logger cannot interrupt observers or asynchronous stop", FailingLogger));
        }
        foreach (var test in tests)
        {
            try { await test.Item2(); ++passed; Console.WriteLine("PASS " + test.Item1); }
            catch (Exception error) { Console.WriteLine("FAIL " + test.Item1 + ": " + error.Message); }
        }
        Console.WriteLine($"{passed}/{tests.Count} passed; desktop .NET single-context harness.");
        Environment.ExitCode = passed == tests.Count ? 0 : 1;
    }
    static void Main() => new Pump().Run(Run);

    static async Task AndSuccess()
    {
        var ready = Gate();
        var slow = new Fake(); slow.Check = async token => { await ready.Task.WaitAsync(token); slow.Satisfied = true; };
        var fast = new Fake { Satisfied = true };
        var group = new Group(ConditionCombineType.And, fast, slow);
        var listener = new TestListener(group) { IsOneShot = true };
        int success = 0; listener.Success += () => ++success;
        listener.StartListen(); ready.SetResult(true);
        await Until(() => success == 1 && !group.IsActivated);
        Require(slow.MaxActive == 1 && slow.Canceled == 0 && slow.Active == 0 && listener.Errors.Count == 0, "slow check overlapped, was canceled, or failed");
    }

    static async Task NestedCleanup()
    {
        var entered = Gate(); var release = Gate();
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var inner = new Group(ConditionCombineType.And, new Fake { Satisfied = true }, slow);
        var outer = new Group(ConditionCombineType.Or, new Fake { Satisfied = true }, inner);
        var listener = new TestListener(outer) { IsOneShot = true };
        listener.StartListen(); await entered.Task;
        Require(slow.Active == 1 && outer.IsActivated && slow.Deactivations == 0, "deactivated before leaf cleanup");
        listener.StartListen(); listener.StopListen();
        release.SetResult(true);
        await Until(() => !outer.IsActivated && slow.Active == 0);
        await Task.Delay(2);
        Require(slow.Calls == 1 && slow.Deactivations == 1 && listener.Errors.Count == 0, "nested cleanup/restart failed");
    }

    static async Task PendingFailure()
    {
        var failed = Gate(); var expected = new InvalidOperationException("pending failure");
        var slow = new Fake { Check = _ => failed.Task };
        var group = new Group(ConditionCombineType.And, new Fake(), slow);
        using var cts = new CancellationTokenSource();
        await group.CheckAsync(cts.Token); failed.SetException(expected); await Task.Yield();
        Exception? actual = null;
        try { await group.CheckAsync(cts.Token); } catch (Exception error) { actual = error; }
        Require(ReferenceEquals(actual, expected) && slow.Calls == 1 && slow.Active == 0, "failure masked or task restarted");
    }

    static async Task StartupFailure(bool nullTask)
    {
        var slow = new Fake { Check = token => Task.Delay(Timeout.Infinite, token) };
        var expected = new InvalidOperationException("startup failure");
        var group = new Group(ConditionCombineType.And, slow, new Bad(nullTask ? null : expected));
        Exception? actual = null;
        try { await group.CheckAsync(CancellationToken.None); } catch (Exception error) { actual = error; }
        Require((nullTask ? actual is InvalidOperationException : ReferenceEquals(actual, expected)) && slow.Active == 0 && slow.Canceled == 1,
            "startup cleanup did not finish");
    }

    static async Task CancellationBarrier()
    {
        var entered = Gate(); var release = Gate();
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var group = new Group(ConditionCombineType.And, slow);
        using var cts = new CancellationTokenSource();
        Task check = group.CheckAsync(cts.Token); cts.Cancel(); await entered.Task;
        Require(!check.IsCompleted, "check completed before asynchronous cleanup");
        release.SetResult(true);
        try { await check; throw new Exception("cancellation not propagated"); }
        catch (OperationCanceledException) { }
        Require(slow.Active == 0, "child still active");
    }

    static async Task SuccessStop()
    {
        var condition = new Fake { Satisfied = true };
        var listener = new TestListener(condition);
        int success = 0;
        listener.Success += () => { ++success; listener.StopListen(); };
        listener.StartListen(); await Until(() => condition.Deactivations == 1); await Task.Delay(2);
        Require(success == 1 && condition.Calls == 1, "automatic restart occurred after stop");
    }

    static async Task SuccessRestart()
    {
        var condition = new Fake { Satisfied = true };
        var listener = new TestListener(condition);
        int success = 0;
        listener.Success += () => {
            ++success; listener.StopListen();
            if (success == 1) { listener.StartListen(); listener.StartListen(); }
        };
        listener.StartListen(); await Until(() => success == 2 && condition.Deactivations == 2); await Task.Delay(2);
        Require(condition.Calls == 2 && condition.Activations == 2 && condition.MaxActive == 1, "reentrant restart overlapped or repeated");
    }

    static async Task ThrowingCancellation()
    {
        var entered = Gate(); var release = Gate();
        var expected = new InvalidOperationException("cancellation callback failure");
        var slow = new Fake { Check = async token => {
            using var registration = token.Register(() => throw expected);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var group = new Group(ConditionCombineType.And, new Fake(), slow);
        var listener = new TestListener(group);
        listener.StartListen(); listener.StopListen(); await entered.Task;
        Require(slow.Deactivations == 0, "cleanup skipped");
        release.SetResult(true); await Until(() => slow.Deactivations == 1);
        Require(listener.Errors.Count >= 1 && slow.Active == 0, "callback failure not observed or check still active");
    }

    static async Task TokenChange()
    {
        var entered = Gate(); var release = Gate();
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var group = new Group(ConditionCombineType.And, new Fake(), slow);
        using var oldToken = new CancellationTokenSource(); using var newToken = new CancellationTokenSource();
        await group.CheckAsync(oldToken.Token);
        Task next = group.CheckAsync(newToken.Token); await entered.Task;
        Require(!next.IsCompleted && slow.Calls == 1, "new session began before previous drain");
        release.SetResult(true); await next;
        Require(slow.Calls == 2 && slow.MaxActive == 1, "token change overlapped");
        newToken.Cancel(); group.Initialize(); group.Activate(); group.Deactivate(); await Until(() => slow.Active == 0);
    }

    static async Task Mutation()
    {
        var slow = new Fake { Check = token => Task.Delay(Timeout.Infinite, token) };
        var group = new Group(ConditionCombineType.And, new Fake(), slow);
        await group.CheckAsync(CancellationToken.None);
        try { group.AddCondition(new Fake()); throw new Exception("addition was accepted"); }
        catch (InvalidOperationException) { }
        group.Conditions!.Reverse();
        try { await group.CheckAsync(CancellationToken.None); throw new Exception("mutation was accepted"); }
        catch (InvalidOperationException) { }
        Require(slow.Active == 0, "mutation failure did not drain");
        group.AddCondition(new Fake());
        await group.CheckAsync(CancellationToken.None);
        group.Conditions.Clear();
        try { await group.CheckAsync(CancellationToken.None); throw new Exception("cleared list was accepted"); }
        catch (InvalidOperationException) { }
        Require(slow.Active == 0, "clearing the list did not drain the old snapshot");

        var completed = Gate();
        var pending = new Fake { Check = _ => completed.Task };
        var duringAwait = new Group(ConditionCombineType.And, pending);
        Task check = duringAwait.CheckAsync(CancellationToken.None);
        duringAwait.Conditions!.Clear(); completed.SetResult(true);
        try { await check; throw new Exception("mutation during an await was accepted"); }
        catch (InvalidOperationException) { }
        Require(pending.Active == 0, "mutation during await did not drain");
    }

    static async Task EmptyGroup()
    {
        var group = new Group(ConditionCombineType.And);
        await group.CheckAsync(CancellationToken.None); await group.CheckAsync(CancellationToken.None);
    }

    static async Task AutomaticRestart()
    {
        var entered = Gate(); var release = Gate();
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var outer = new Group(ConditionCombineType.Or, new Fake { Satisfied = true },
            new Group(ConditionCombineType.And, new Fake { Satisfied = true }, slow));
        var listener = new TestListener(outer);
        int success = 0;
        listener.Success += () => { ++success; if (success == 2) listener.StopListen(); };
        listener.StartListen(); await entered.Task;
        Require(success == 1 && slow.Calls == 1 && slow.Deactivations == 0, "restarted before cleanup");
        release.SetResult(true); await Until(() => success == 2 && slow.Deactivations == 2);
        Require(slow.Calls == 2 && slow.MaxActive == 1 && listener.Errors.Count == 0, "automatic restart overlapped or failed");
    }

    static async Task DirectDeactivate()
    {
        var entered = Gate(); var release = Gate();
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var group = new Group(ConditionCombineType.And, new Fake(), slow);
        group.Initialize(); group.Activate();
        await group.CheckAsync(CancellationToken.None);
        group.Deactivate(); await entered.Task;
        Task next = group.CheckAsync(CancellationToken.None);
        Require(slow.Calls == 1 && !next.IsCompleted, "new direct check overlapped cleanup");
        release.SetResult(true); await next;
        Require(slow.Calls == 2 && slow.MaxActive == 1, "direct check overlapped");
        group.Activate(); group.Deactivate(); await Until(() => slow.Active == 0);
    }

    static async Task ConcurrentCheck()
    {
        var slow = new Fake { Check = token => Task.Delay(Timeout.Infinite, token) };
        var group = new Group(ConditionCombineType.And, slow);
        using var cts = new CancellationTokenSource();
        Task first = group.CheckAsync(cts.Token);
        try { await group.CheckAsync(cts.Token); throw new Exception("concurrent call accepted"); }
        catch (InvalidOperationException) { }
        cts.Cancel(); try { await first; } catch (OperationCanceledException) { }
        Require(slow.MaxActive == 1 && slow.Active == 0, "concurrent check overlapped or leaked");
    }

    static async Task CleanupFailure()
    {
        var entered = Gate(); var release = Gate();
        var expected = new InvalidOperationException("cleanup failure");
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { entered.TrySetResult(true); await release.Task; throw expected; }
        }};
        var group = new Group(ConditionCombineType.Or, new Fake { Satisfied = true }, slow);
        var listener = new TestListener(group) { IsOneShot = true };
        listener.StartListen(); await entered.Task;
        Require(slow.Deactivations == 0, "deactivated before cleanup failure finished");
        release.SetResult(true); await Until(() => slow.Deactivations == 1);
        Require(listener.Errors.Contains(expected) && slow.Active == 0, "cleanup failure not reported or task leaked");
    }

    static async Task CompletionBeforeRestart()
    {
        var ready = Gate(); var slow = new Fake();
        // A second call must clear the first result for this to reproduce the missing notification.
        var later = Gate();
        slow.Check = async token => { slow.Satisfied = false; await (slow.Calls == 1 ? ready.Task : later.Task).WaitAsync(token); slow.Satisfied = true; };
        var group = new Group(ConditionCombineType.And, new Fake { Satisfied = true }, slow);
        group.Initialize(); group.Activate(); using var cts = new CancellationTokenSource();
        try
        {
            await group.CheckAsync(cts.Token); ready.SetResult(true); await Until(() => slow.Active == 0);
            Require(group.IsSatisfied, "first completion was not satisfied");
            await group.CheckAsync(cts.Token);
            Require(slow.Calls == 1 && group.IsSatisfied, "completed satisfied result was reset before being reported");
        }
        finally { cts.Cancel(); group.Deactivate(); await Until(() => slow.Active == 0); }
    }

    static async Task NestedCompletionBeforeRestart()
    {
        var ready = Gate(); var later = Gate(); var slow = new Fake();
        slow.Check = async token => { slow.Satisfied = false; await (slow.Calls == 1 ? ready.Task : later.Task).WaitAsync(token); slow.Satisfied = true; };
        var inner = new Group(ConditionCombineType.And, new Fake { Satisfied = true }, slow);
        var outer = new Group(ConditionCombineType.And, new Fake { Satisfied = true }, inner);
        outer.Initialize(); outer.Activate(); using var cts = new CancellationTokenSource();
        try
        {
            await outer.CheckAsync(cts.Token); ready.SetResult(true); await Until(() => slow.Active == 0);
            await outer.CheckAsync(cts.Token);
            Require(slow.Calls == 1 && outer.IsSatisfied, "nested completed result was reset");
        }
        finally { cts.Cancel(); outer.Deactivate(); await Until(() => slow.Active == 0); }
    }

    static async Task ListenerCompletionBeforeRestart()
    {
        var ready = Gate(); var later = Gate(); var slow = new Fake();
        slow.Check = async token => { slow.Satisfied = false; await (slow.Calls == 1 ? ready.Task : later.Task).WaitAsync(token); slow.Satisfied = true; };
        var group = new Group(ConditionCombineType.And, new Fake { Satisfied = true }, slow);
        var listener = new TestListener(group) { IsOneShot = true };
        int success = 0; listener.Success += () => ++success;
        try
        {
            listener.StartListen(); ready.SetResult(true);
            await Until(() => success == 1 && !group.IsActivated);
            Require(slow.Calls == 1 && listener.Errors.Count == 0, "listener reset completion or failed");
        }
        finally { listener.StopListen(); await Until(() => slow.Active == 0); }
    }

    static Task DeactivateAll()
    {
        var firstError = new InvalidOperationException("first deactivation");
        var secondError = new InvalidOperationException("second deactivation");
        var first = new Fake { Deactivated = () => throw firstError };
        var second = new Fake { Deactivated = () => throw secondError };
        var last = new Fake(); var group = new Group(ConditionCombineType.And, first, second, last);
        group.Initialize(); group.Activate(); Exception? failure = null;
        try { group.Deactivate(); } catch (Exception error) { failure = error; }
        Require(first.Deactivations == 1 && second.Deactivations == 1 && last.Deactivations == 1,
            "one failure skipped later children");
        Require(failure is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Contains(firstError)
            && aggregate.Flatten().InnerExceptions.Contains(secondError), "not all deactivation failures were reported");
        Require(!group.IsActivated && !first.IsActivated && !second.IsActivated && !last.IsActivated, "activation state remained set");
        var single = new Fake { Deactivated = () => throw firstError }; var following = new Fake();
        var oneFailure = new Group(ConditionCombineType.And, single, following); oneFailure.Activate();
        Exception? original = null;
        try { oneFailure.Deactivate(); } catch (Exception error) { original = error; }
        Require(ReferenceEquals(original, firstError) && following.Deactivations == 1, "single failure changed identity or skipped later cleanup");
        return Task.CompletedTask;
    }

    static Task ImmediateInvalid()
    {
        foreach (ICondition condition in new ICondition[] { new Group(ConditionCombineType.And), new Fake { Valid = false, Satisfied = true } })
        {
            var listener = new TestListener(condition); int success = 0; listener.Success += () => ++success;
            listener.CheckConditionsImmediately();
            Require(success == 0 && listener.Errors.Count == 1, "invalid condition was treated as successful");
            listener.IsIgnoreNotAvailableCondition = true; listener.CheckConditionsImmediately();
            Require(success == 0 && listener.Errors.Count == 1, "ignore mode reported success or extra failure");
        }
        var nullGroup = new Group(ConditionCombineType.And, new Fake { Satisfied = true });
        nullGroup.Conditions!.Add(null!);
        Require(!nullGroup.IsValid, "null child counted as valid");
        return Task.CompletedTask;
    }

    static async Task ImmediateHandlerFailure()
    {
        var expected = new InvalidOperationException("immediate success failure");
        var condition = new Fake { Check = token => Task.Delay(Timeout.Infinite, token) };
        var listener = new TestListener(condition); listener.Success += () => throw expected;
        try
        {
            listener.StartListen(); condition.Satisfied = true;
            Exception? escaped = null;
            try { listener.CheckConditionsImmediately(); } catch (Exception error) { escaped = error; }
            Require(escaped == null && listener.Errors.Contains(expected), "handler failure escaped or was not reported");
            await Until(() => condition.Active == 0 && condition.Deactivations == 1);
        }
        finally { listener.StopListen(); await Until(() => condition.Active == 0); }
    }

    static async Task UnexpectedCancellation()
    {
        var expected = new OperationCanceledException("unrequested cancellation");
        var condition = new Fake { Check = _ => Task.FromException(expected) };
        var listener = new TestListener(condition);
        listener.StartListen(); await Until(() => condition.Deactivations == 1);
        Require(listener.Errors.Contains(expected), "unrequested cancellation was silently ignored");
    }

    static Task LateTarget()
    {
        var expected = new object(); var group = new ConditionGroupShareTarget<object> { Target = expected };
        group.Initialize(); var child = new TargetCondition();
        group.AddConditions(new ICondition[] { child });
        Require(ReferenceEquals(child.Target, expected) && ReferenceEquals(child.TargetAtInitialize, expected),
            "target was missing during late child initialization");
        return Task.CompletedTask;
    }

    static Task TargetChange()
    {
        var first = new object(); var second = new object(); var child = new TargetCondition();
        var inner = new ConditionGroupShareTarget<object>(); inner.AddCondition(child);
        var outer = new ConditionGroupShareTarget<object> { Target = first }; outer.AddCondition(inner); outer.Initialize();
        Require(ReferenceEquals(child.TargetAtInitialize, first), "initial nested target missing");
        outer.Target = second;
        Require(ReferenceEquals(inner.Target, second) && ReferenceEquals(child.Target, second), "updated nested target missing");
        outer.Target = null;
        Require(inner.Target == null && child.Target == null, "null target did not propagate");
        return Task.CompletedTask;
    }

    static Task ActivationRollback()
    {
        var expected = new InvalidOperationException("activation failure"); var condition = new Fake();
        condition.Activated = () => { if (condition.Activations == 1) throw expected; };
        Exception? failure = null;
        try { condition.Activate(); } catch (Exception error) { failure = error; }
        Require(ReferenceEquals(failure, expected) && !condition.IsActivated && condition.Deactivations == 1, "failed activation did not roll back");
        condition.Activate(); Require(condition.IsActivated && condition.Activations == 2, "activation retry was skipped");
        condition.Deactivate(); return Task.CompletedTask;
    }

    static Task GroupActivationRollback()
    {
        var expected = new InvalidOperationException("group activation failure");
        var first = new Fake { Deactivated = () => throw new InvalidOperationException("rollback cleanup failure") };
        var failing = new Fake { Activated = () => throw expected }; var last = new Fake();
        var group = new Group(ConditionCombineType.And, first, failing, last); Exception? failure = null;
        try { group.Activate(); } catch (Exception error) { failure = error; }
        Require(ReferenceEquals(failure, expected) && !group.IsActivated && !first.IsActivated && !failing.IsActivated && !last.IsActivated,
            "partial group activation remained active");
        Require(first.Deactivations == 1 && failing.Deactivations == 1 && last.Activations == 0, "rollback did not clean partial activation");
        return Task.CompletedTask;
    }

    static async Task ActiveTargetChange()
    {
        var first = new object(); var second = new object();
        var group = new ConditionGroupShareTarget<object> { Target = first };
        var slow = new Fake { Check = token => Task.Delay(Timeout.Infinite, token) };
        group.AddConditions(new ICondition[] { new Fake(), slow }); group.Initialize(); group.Activate();
        using var cts = new CancellationTokenSource();
        try
        {
            await group.CheckAsync(cts.Token);
            try { group.Target = second; throw new Exception("active target change was accepted"); }
            catch (InvalidOperationException) { }
            Require(ReferenceEquals(group.Target, first), "rejected update changed target");
        }
        finally { cts.Cancel(); group.Deactivate(); await Until(() => slow.Active == 0); }
        group.Target = second;
        Require(ReferenceEquals(group.Target, second), "target could not change after cleanup");
    }

    static async Task InitializedCondition()
    {
        foreach (ICondition? original in new ICondition?[] { null, new Fake { Valid = false } })
        {
            var condition = new Fake { Satisfied = true };
            var listener = new InitializingListener(original, condition) { IsOneShot = true };
            int success = 0; listener.Success += () => ++success;
            listener.StartListen(); await Task.Delay(5);
            Require(condition.Calls == 1 && success == 1 && listener.Errors.Count == 0 && condition.Deactivations == 1,
                "session retained the pre-initialization condition");
        }
    }
    static void Reject(Action action, string failure)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception(failure);
    }
    static Task DuplicateRegistration()
    {
        var child = new Fake(); var group = new Group(ConditionCombineType.And, child);
        Reject(() => group.AddCondition(child), "duplicate instance accepted");
        Require(group.Conditions!.Count == 1, "rejected addition changed the list");
        return Task.CompletedTask;
    }
    static Task DuplicateNestedRegistration()
    {
        var child = new Fake(); var left = new Group(ConditionCombineType.And, child);
        var right = new Group(ConditionCombineType.And, child); var parent = new Group(ConditionCombineType.And, left);
        Reject(() => parent.AddCondition(right), "shared leaf in sibling branches accepted");
        Require(parent.Conditions!.Count == 1, "rejected branch changed the tree");
        return Task.CompletedTask;
    }
    static Task CycleRegistration()
    {
        var parent = new Group(ConditionCombineType.And); var child = new Group(ConditionCombineType.And);
        Reject(() => parent.AddCondition(parent), "self registration accepted");
        parent.AddCondition(child);
        Reject(() => child.AddCondition(parent), "ancestor registration accepted");
        return Task.CompletedTask;
    }
    static Task ActiveRegistration()
    {
        var group = new Group(ConditionCombineType.And, new Fake()); group.Initialize(); group.Activate();
        var late = new Fake();
        try
        {
            Reject(() => group.AddCondition(late), "activated group accepted an inactive child");
            Require(!late.IsInitialized && group.Conditions!.Count == 1, "rejected addition initialized or registered a child");
        }
        finally { group.Deactivate(); }
        group.AddCondition(late); Require(late.IsInitialized, "addition after deactivation did not initialize child");
        return Task.CompletedTask;
    }
    static Task InitializationRetry()
    {
        var condition = new Fake { Initialized = () => throw new InvalidOperationException("initialize failed") };
        var listener = new Listener(condition); listener.Exception += _ => { };
        var field = typeof(Listener).GetField("Exception", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        int CountHandlers() => ((Delegate?)field.GetValue(listener))?.GetInvocationList().Length ?? 0;
        Reject(listener.Initialize, "initialization unexpectedly succeeded"); int first = CountHandlers();
        Reject(listener.Initialize, "initialization unexpectedly succeeded");
        Require(CountHandlers() == first, "diagnostic handlers accumulated on initialization retry");
        condition.Initialized = null; listener.Initialize();
        Require(listener.IsInitialized && CountHandlers() == first, "successful retry duplicated diagnostics");
        return Task.CompletedTask;
    }

    static async Task AsyncStop()
    {
        var entered = Gate(); var release = Gate();
        var condition = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        IAsyncListener listener = new TestListener(condition);
        await listener.StopListenAsync(); listener.StartListen();
        Task first = listener.StopListenAsync(); Task second = listener.StopListenAsync();
        await entered.Task;
        Require(ReferenceEquals(first, second) && !first.IsCompleted && condition.Deactivations == 0,
            "async stop did not share the pending cleanup barrier");
        release.SetResult(true); await first;
        Require(!condition.IsActivated && condition.Deactivations == 1, "async stop returned before deactivation");
        listener.StartListen();
        Require(condition.Calls == 2 && condition.Activations == 2, "finished session still blocked restart");
        await listener.StopListenAsync(); Require(condition.MaxActive == 1, "async stop/restart overlapped");
    }
    static async Task AsyncStopNested()
    {
        var entered = Gate(); var release = Gate();
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var inner = new Group(ConditionCombineType.And, new Fake(), slow);
        var outer = new Group(ConditionCombineType.And, new Fake(), inner);
        var listener = new TestListener(outer); listener.StartListen();
        Task stopped = listener.StopListenAsync(); await entered.Task;
        Require(!stopped.IsCompleted && slow.Deactivations == 0, "nested cleanup was not awaited");
        release.SetResult(true); await stopped;
        Require(slow.Active == 0 && !outer.IsActivated && !inner.IsActivated && slow.Deactivations == 1,
            "nested cleanup remained active after stop");
    }
    static async Task AsyncStopFailure()
    {
        var expected = new InvalidOperationException("deactivate failed");
        var condition = new Fake { Check = token => Task.Delay(Timeout.Infinite, token), Deactivated = () => throw expected };
        var listener = new TestListener(condition); listener.StartListen(); await listener.StopListenAsync();
        Require(condition.Active == 0 && !condition.IsActivated && listener.Errors.Contains(expected), "cleanup failure prevented completion or diagnostics");
        await listener.StopListenAsync();
    }
    static async Task DirectCycle()
    {
        var root = new Group(ConditionCombineType.And); var child = new Group(ConditionCombineType.And, new Fake { Satisfied = true });
        root.AddCondition(child); child.Conditions!.Add(root);
        Require(!root.IsValid, "cycle was considered valid");
        Reject(() => _ = root.IsSatisfied, "satisfaction query accepted a cycle");
        Reject(root.Initialize, "cycle initialized recursively");
        Reject(root.Activate, "cycle activated recursively");
        try { await root.CheckAsync(CancellationToken.None); throw new Exception("cycle was checked"); }
        catch (InvalidOperationException) { }
        var shared = new ConditionGroupShareTarget<object>(); shared.AddCondition(new Fake()); shared.Conditions!.Add(shared);
        Reject(shared.Initialize, "shared target cycle initialized recursively");
        Reject(() => shared.Target = new object(), "shared target cycle propagated recursively");
    }
    static async Task DirectDuplicate()
    {
        var child = new Fake(); var group = new Group(ConditionCombineType.And, child);
        group.Conditions!.Add(child);
        Reject(group.Initialize, "direct duplicate initialized recursively");
        Reject(group.Activate, "direct duplicate activated");
        try { await group.CheckAsync(CancellationToken.None); throw new Exception("direct duplicate was checked"); }
        catch (InvalidOperationException) { }
        Require(child.Calls == 0 && !group.IsActivated, "duplicate rejection happened after checks started");
    }
    static Task TargetRollback()
    {
        var old = new object(); var next = new object(); var expected = new InvalidOperationException("target rejected");
        var first = new FaultTarget(); var failing = new FaultTarget(); var untouched = new FaultTarget();
        var group = new ConditionGroupShareTarget<object> { Target = old };
        group.AddConditions(new ICondition[] { first, failing, untouched });
        int untouchedAssignments = untouched.Assignments;
        failing.AfterSet = value => { if (ReferenceEquals(value, next)) throw expected; };
        Exception? failure = null; try { group.Target = next; } catch (Exception error) { failure = error; }
        Require(ReferenceEquals(failure, expected), "original target failure changed");
        Require(ReferenceEquals(group.Target, old) && ReferenceEquals(first.Target, old) && ReferenceEquals(failing.Target, old), "failed target update left mixed values");
        Require(untouched.Assignments == untouchedAssignments, "untouched sibling was modified during rollback");
        failing.AfterSet = null; group.Target = next;
        Require(ReferenceEquals(group.Target, next) && ReferenceEquals(untouched.Target, next), "retry after rollback failed");
        return Task.CompletedTask;
    }
    static Task NestedTargetRollback()
    {
        var old = new object(); var distinct = new object(); var next = new object();
        var leaf = new FaultTarget(); var inner = new ConditionGroupShareTarget<object>(); inner.AddCondition(leaf);
        var later = new FaultTarget(); var outer = new ConditionGroupShareTarget<object> { Target = old }; outer.AddConditions(new ICondition[] { inner, later });
        leaf.Target = distinct;
        var expected = new InvalidOperationException("later branch failed");
        later.AfterSet = value => { if (ReferenceEquals(value, next)) throw expected; };
        Exception? failure = null; try { outer.Target = next; } catch (Exception error) { failure = error; }
        Require(ReferenceEquals(failure, expected) && ReferenceEquals(outer.Target, old) && ReferenceEquals(inner.Target, old)
            && ReferenceEquals(leaf.Target, distinct) && ReferenceEquals(later.Target, old), "nested rollback lost original descendant values");
        return Task.CompletedTask;
    }
    static async Task ExceptionSubscribers()
    {
        var primary = new InvalidOperationException("check failed");
        var firstHandler = new InvalidOperationException("first observer failed");
        var secondHandler = new InvalidOperationException("second observer failed");
        var condition = new Fake { Check = _ => Task.FromException(primary) }; var listener = new Listener(condition);
        var seen = new List<Exception>();
        listener.Exception += _ => throw firstHandler;
        listener.Exception += seen.Add;
        listener.Exception += _ => throw secondHandler;
        listener.Exception += seen.Add;
        listener.StartListen(); await listener.StopListenAsync();
        Require(seen.Count == 2 && ReferenceEquals(seen[0], primary) && ReferenceEquals(seen[1], primary), "failed observer skipped remaining subscribers");
        Require(condition.Deactivations == 1 && !condition.IsActivated, "observer failure interrupted cleanup");
    }
    static async Task ExceptionLogging()
    {
        var logger = new CaptureLogger(); CrowRx.Log.SetLogger(logger);
        var primary = new InvalidOperationException("logged check failure"); var handler = new InvalidOperationException("logged observer failure");
        var listener = new Listener(new Fake { Check = _ => Task.FromException(primary) });
        listener.Exception += _ => throw handler;
        listener.StartListen(); await listener.StopListenAsync();
        Require(logger.Errors.FindAll(e => ReferenceEquals(e, primary)).Count == 1 && logger.Errors.FindAll(e => ReferenceEquals(e, handler)).Count == 1,
            "primary or handler failure was missing or duplicated in logs");
    }
    static Task TargetRecoveryFailure()
    {
        var old = new object(); var next = new object(); var distinct = new object();
        var original = new InvalidOperationException("update failed"); var restore = new InvalidOperationException("restore refused");
        var first = new FaultTarget(); var last = new FaultTarget();
        var inner = new ConditionGroupShareTarget<object>(); inner.AddConditions(new ICondition[] { first, last });
        var parent = new ConditionGroupShareTarget<object> { Target = old }; parent.AddCondition(inner);
        first.Target = distinct;
        first.BeforeSet = value => { if (ReferenceEquals(value, distinct)) throw restore; };
        last.AfterSet = value => { if (ReferenceEquals(value, next)) throw original; };
        Exception? failure = null; try { parent.Target = next; } catch (Exception error) { failure = error; }
        Require(failure is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Contains(original)
            && aggregate.Flatten().InnerExceptions.Contains(restore), "rollback errors replaced the original failure");
        Require(ReferenceEquals(parent.Target, old) && ReferenceEquals(inner.Target, old) && ReferenceEquals(last.Target, old),
            "one rollback failure skipped recovery of other targets");
        first.BeforeSet = null; last.AfterSet = null; parent.Target = next;
        Require(ReferenceEquals(first.Target, next) && ReferenceEquals(last.Target, next), "failed rollback left target update locked");
        return Task.CompletedTask;
    }
    static Task ActiveNestedTarget()
    {
        var old = new object(); var next = new object(); var leaf = new FaultTarget();
        var inner = new ConditionGroupShareTarget<object>(); inner.AddCondition(leaf);
        var parent = new ConditionGroupShareTarget<object> { Target = old }; parent.AddCondition(inner); parent.Initialize(); inner.Activate();
        try
        {
            Reject(() => parent.Target = next, "active nested target was accepted");
            Require(ReferenceEquals(parent.Target, old) && ReferenceEquals(inner.Target, old) && ReferenceEquals(leaf.Target, old), "active nested rejection left parent changed");
        }
        finally { inner.Deactivate(); }
        parent.Target = next; Require(ReferenceEquals(leaf.Target, next), "retry after nested deactivation failed");
        return Task.CompletedTask;
    }
    static Task ReentrantTarget()
    {
        var old = new object(); var next = new object(); var child = new FaultTarget();
        var group = new ConditionGroupShareTarget<object> { Target = old }; group.AddCondition(child);
        child.AfterSet = value => { if (ReferenceEquals(value, next)) group.Target = old; };
        Reject(() => group.Target = next, "reentrant target setter succeeded");
        Require(ReferenceEquals(group.Target, old) && ReferenceEquals(child.Target, old), "reentrant failure left mixed values");
        child.AfterSet = null; group.Target = next;
        return Task.CompletedTask;
    }
    static async Task ExceptionLoggingWithoutDefault()
    {
        var logger = new CaptureLogger(); CrowRx.Log.SetLogger(logger);
        var primary = new InvalidOperationException("primary without default logger"); var handler = new InvalidOperationException("handler without default logger");
        var listener = new TestListener(new Fake { Check = _ => Task.FromException(primary) });
        listener.Exception += _ => throw handler;
        listener.StartListen(); await listener.StopListenAsync();
        Require(logger.Errors.FindAll(e => ReferenceEquals(e, primary)).Count == 1 && logger.Errors.FindAll(e => ReferenceEquals(e, handler)).Count == 1,
            "fallback did not preserve primary and handler errors");
    }
    static async Task FailingLogger()
    {
        var logger = new CaptureLogger { Throw = true }; CrowRx.Log.SetLogger(logger);
        try
        {
            var primary = new InvalidOperationException("primary with broken logger");
            var condition = new Fake { Check = _ => Task.FromException(primary) }; var listener = new Listener(condition);
            listener.Initialize();
            var seen = new List<Exception>(); listener.Exception += seen.Add;
            listener.StartListen(); await listener.StopListenAsync();
            Require(seen.Contains(primary) && condition.Deactivations == 1, "logger failure stopped later observers or cleanup");
        }
        finally { CrowRx.Log.SetLogger(new CaptureLogger()); }
    }
    static async Task AllPendingChecks()
    {
        var a = Gate(); var b = Gate();
        var first = new Fake { Check = token => a.Task.WaitAsync(token) };
        var second = new Fake { Check = token => b.Task.WaitAsync(token) };
        var group = new Group(ConditionCombineType.And, first, second);
        using var cts = new CancellationTokenSource();
        try
        {
            Task check = group.CheckAsync(cts.Token);
            Require(!check.IsCompleted && first.Calls == 1 && second.Calls == 1, "pending path did not start both children");
            a.SetResult(true); await check;
            Require(second.Active == 1 && second.Calls == 1, "first completion canceled the other pending child");
            b.SetResult(true); await Until(() => second.Active == 0);
            await group.CheckAsync(cts.Token);
            Require(first.Calls == 1 && second.Calls == 1, "late completion rearmed already reported children before notification");
        }
        finally
        {
            cts.Cancel(); group.Initialize(); group.Activate(); group.Deactivate(); await Until(() => first.Active == 0 && second.Active == 0);
        }
    }
    static Task ValidationReuse()
    {
        var child = new Fake(); var group = new Group(ConditionCombineType.And, child); group.Initialize();
        for (int i = 0; i < 5; ++i) { group.Activate(); group.Deactivate(); }
        Reject(() => group.AddCondition(child), "reused validator accepted duplicate");
        group.AddCondition(new Fake());
        group.Conditions!.Add(group);
        Reject(group.Activate, "reused validator accepted direct cycle");
        group.Conditions.RemoveAt(group.Conditions.Count - 1);
        group.Activate(); Require(group.IsActivated && child.IsActivated, "failed validation poisoned the next valid activation"); group.Deactivate();
        return Task.CompletedTask;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference TemporaryValidationReference(Group root, bool failure)
    {
        var leaf = new Fake();
        if (failure)
        {
            var candidate = new Group(ConditionCombineType.And, leaf); candidate.Conditions!.Add(leaf);
            try { root.AddCondition(candidate); } catch (InvalidOperationException) { }
        }
        else { root.AddCondition(leaf); root.Conditions!.Clear(); }
        return new WeakReference(leaf);
    }
    static Task ValidationReferences()
    {
        var root = new Group(ConditionCombineType.And);
        WeakReference valid = TemporaryValidationReference(root, false);
        WeakReference failed = TemporaryValidationReference(root, true);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(!valid.IsAlive && !failed.IsAlive, "cached validation buffer retained removed conditions");
        GC.KeepAlive(root); return Task.CompletedTask;
    }
    static Task ValidationIdentity()
    {
        var first = new EqualCondition(); var second = new EqualCondition();
        Require(first.Equals(second), "reference identity fixture is not value equal");
        var group = new ValidationProbe(); group.AddConditions(new ICondition[] { first, second });
        group.ValidateNow();
        Reject(() => group.AddCondition(first), "pooled validator accepted the same instance twice");
        var other = new ValidationProbe(); other.AddConditions(new ICondition[] { first, second });
        other.ValidateNow(); group.ValidateNow();
        return Task.CompletedTask;
    }
    static Task ValidationReentrancy()
    {
        var root = new ValidationProbe(); var probe = new ValidationProbe(); var duplicate = new Fake();
        root.AddConditions(new ICondition[] { duplicate, probe });
        root.Conditions!.Insert(0, duplicate);
        var valid = new ValidationProbe(); valid.AddCondition(new Fake());
        var invalid = new ValidationProbe();
        // A nested successful and failing validation run while the outer traversal owns both leases.
        invalid.AddCondition(new Fake()); invalid.Conditions!.Add(null!);
        int entered = 0;
        probe.ReadingConditions = () => {
            ++entered; valid.ValidateNow();
            Reject(invalid.ValidateNow, "nested validation accepted a null child");
            invalid.Conditions!.RemoveAt(invalid.Conditions.Count - 1); invalid.ValidateNow();
        };
        Reject(root.ValidateNow, "nested validation erased the outer duplicate traversal");
        Require(entered == 1, "nested validation callback did not execute");
        root.Conditions!.RemoveAt(0); root.ValidateNow(); valid.ValidateNow();
        return Task.CompletedTask;
    }
    static Task RecursiveAddition(bool duringInitialization)
    {
        var group = new AddingGroup(); group.AddCondition(new Fake()); group.Initialize();
        var candidate = new Fake(); var other = new Fake();
        if (duringInitialization) candidate.Initialized = () => group.AddCondition(other);
        else group.Adding = _ => group.AddCondition(other);
        Reject(() => group.AddCondition(candidate), "recursive addition succeeded");
        Require(group.Conditions!.Count == 1 && !candidate.IsInitialized && !other.IsInitialized, "reentrant addition committed or initialized a child");
        candidate.Initialized = null; group.Adding = null; group.AddCondition(candidate); group.AddCondition(other);
        Require(group.Conditions.Count == 3 && candidate.IsInitialized && other.IsInitialized, "failed addition did not release the guard");
        return Task.CompletedTask;
    }
    static Task AdditionActivates()
    {
        var old = new Fake(); var group = new Group(ConditionCombineType.And, old); group.Initialize();
        var candidate = new Fake { Initialized = group.Activate };
        try
        {
            Reject(() => group.AddCondition(candidate), "initializer appended to an activated group");
            Require(group.IsActivated && old.IsActivated && !candidate.IsActivated && group.Conditions!.Count == 1,
                "addition left an inactive candidate in an active group");
        }
        finally { group.Deactivate(); }
        candidate.Initialized = null; group.AddCondition(candidate);
        Require(group.Conditions!.Count == 2, "addition could not retry after deactivation");
        return Task.CompletedTask;
    }
    static async Task AdditionStartsCheck()
    {
        var slow = new Fake { Check = token => Task.Delay(Timeout.Infinite, token) };
        var group = new Group(ConditionCombineType.And, new Fake(), slow); group.Initialize();
        Task? check = null; var candidate = new Fake { Initialized = () => check = group.CheckAsync(CancellationToken.None) };
        try
        {
            Reject(() => group.AddCondition(candidate), "initializer appended after starting a check");
            Require(check != null && check.IsCompletedSuccessfully && group.Conditions!.Count == 2 && slow.Active == 1,
                "initialization check fixture did not retain its original snapshot");
        }
        finally { await group.StopChecksAsync(); }
        Require(slow.Canceled == 1 && slow.Active == 0, "direct session started by initializer was not drained");
        candidate.Initialized = null; group.AddCondition(candidate);
        Require(group.Conditions!.Count == 3, "addition could not retry after check cleanup");
    }
    static Task AdditionMutates(bool duringInitialization)
    {
        var group = new AddingGroup(); group.AddCondition(new Fake()); group.Initialize();
        var candidate = new Fake();
        if (duringInitialization) candidate.Initialized = () => group.Conditions!.Add(candidate);
        else group.Adding = child => group.Conditions!.Add(child);
        Reject(() => group.AddCondition(candidate), "callback mutation allowed duplicate append");
        Require(group.Conditions!.Count == 2 && candidate.IsInitialized == duringInitialization,
            "mutation check happened too late or performed the outer append");
        // The callback's own raw List change is owned by the callback, so repair it before retrying.
        group.Conditions.RemoveAt(1); candidate.Initialized = null; group.Adding = null; group.AddCondition(candidate);
        Require(group.Conditions.Count == 2 && candidate.IsInitialized, "callback mutation poisoned a later valid addition");
        return Task.CompletedTask;
    }
    static async Task StopInactiveCheck()
    {
        var entered = Gate(); var release = Gate();
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var group = new Group(ConditionCombineType.And, slow);
        await group.StopChecksAsync();
        Task check = group.CheckAsync(CancellationToken.None);
        Task stop = group.StopChecksAsync(); Task again = group.StopChecksAsync();
        try
        {
            await entered.Task;
            Require(!group.IsActivated && !stop.IsCompleted && !again.IsCompleted && slow.Active == 1,
                "inactive stop completed before pending check cleanup");
            Reject(() => group.AddCondition(new Fake()), "addition succeeded while stop was draining");
        }
        finally { release.TrySetResult(true); }
        try { await check; throw new Exception("stopped pending check completed successfully"); }
        catch (OperationCanceledException) { }
        await stop; await again;
        Require(slow.Active == 0 && slow.Canceled == 1, "inactive stop did not drain the child");
        slow.Check = _ => Task.CompletedTask; group.AddCondition(new Fake());
        await group.CheckAsync(CancellationToken.None); await group.StopChecksAsync();
        Require(slow.Calls == 2 && slow.MaxActive == 1, "inactive group could not start a fresh session after stop");
    }
    static async Task StopInactiveNested()
    {
        var entered = Gate(); var release = Gate();
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var inner = new Group(ConditionCombineType.And, new Fake(), slow);
        var outer = new Group(ConditionCombineType.Or, new Fake(), inner);
        await outer.CheckAsync(CancellationToken.None);
        Task stop = outer.StopChecksAsync();
        try { await entered.Task; Require(!stop.IsCompleted && slow.Active == 1, "outer stop missed retained nested checks"); }
        finally { release.TrySetResult(true); }
        await stop;
        Require(slow.Active == 0 && slow.Canceled == 1 && !outer.IsActivated && !inner.IsActivated, "nested inactive cleanup changed activation or leaked a check");
        inner.AddCondition(new Fake()); outer.AddCondition(new Fake()); await outer.StopChecksAsync();
    }
    static async Task StopGroupFailure()
    {
        var expected = new InvalidOperationException("direct stop cleanup failed");
        var entered = Gate(); var release = Gate();
        var failing = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { throw expected; }
        }};
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var group = new Group(ConditionCombineType.And, new Fake(), failing, slow); group.Initialize(); group.Activate();
        await group.CheckAsync(CancellationToken.None); Task stop = group.StopChecksAsync();
        try { await entered.Task; Require(!stop.IsCompleted, "stop reported a failure before other children drained"); }
        finally { release.TrySetResult(true); }
        Exception? failure = null; try { await stop; } catch (Exception error) { failure = error; }
        Require(ReferenceEquals(failure, expected) && failing.Active == 0 && slow.Active == 0 && group.IsActivated
            && failing.IsActivated && slow.IsActivated, "stop lost its failure, leaked a check, or deactivated the group");
        await group.StopChecksAsync(); group.Deactivate(); group.AddCondition(new Fake());
        Require(!failing.IsActivated && !slow.IsActivated, "normal deactivation failed after stop");
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference TemporaryTargetReference(ConditionGroupShareTarget<object> root, bool failure)
    {
        var leaf = new FaultTarget(); root.AddCondition(leaf); var next = new object();
        if (failure) leaf.AfterSet = value => { if (ReferenceEquals(value, next)) throw new InvalidOperationException("target rejected"); };
        try { root.Target = next; }
        catch (InvalidOperationException) when (failure) { }
        root.Conditions!.Clear();
        return new WeakReference(leaf);
    }
    static Task TargetPoolReferences()
    {
        var root = new ConditionGroupShareTarget<object> { Target = new object() };
        WeakReference succeeded = TemporaryTargetReference(root, false);
        WeakReference failed = TemporaryTargetReference(root, true);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(!succeeded.IsAlive && !failed.IsAlive, "returned snapshot or child list retained a removed target condition");
        GC.KeepAlive(root); return Task.CompletedTask;
    }
    static async Task ReentrantAsyncStop()
    {
        var gate = Gate(); TestListener? listener = null; Task? reentered = null;
        CancellationTokenRegistration registration = default;
        var condition = new Fake { Check = token => {
            registration = token.Register(() => {
                reentered = listener!.StopListenAsync(); gate.TrySetCanceled();
            });
            return gate.Task;
        }};
        listener = new TestListener(condition);
        try
        {
            listener.StartListen(); Task stopped = listener.StopListenAsync();
            Require(ReferenceEquals(stopped, reentered), "reentrant stop created a different completion task");
            await stopped.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!condition.IsActivated && condition.Active == 0 && condition.Deactivations == 1 && listener.Errors.Count == 0,
                "reentrant cancellation did not complete cleanup");
        }
        finally { registration.Dispose(); }
    }
    static async Task LateAsyncStop()
    {
        var entered = Gate(); var release = Gate(); int runs = 0;
        var condition = new Fake { Check = async token => {
            int run = ++runs;
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { if (run == 1) { entered.TrySetResult(true); await release.Task; } }
        }};
        var listener = new TestListener(condition); listener.StartListen(); listener.StopListen();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task first = listener.StopListenAsync(); Task duplicate = listener.StopListenAsync();
        listener.StartListen();
        try { Require(ReferenceEquals(first, duplicate) && !first.IsCompleted, "late stop lost the pending cleanup barrier"); }
        finally { release.TrySetResult(true); }
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        await Until(() => condition.Calls == 2);
        Require(first.IsCompletedSuccessfully && condition.Active == 1 && condition.MaxActive == 1,
            "old completion waited for or overlapped the queued successor");
        Task next = listener.StopListenAsync();
        Require(!ReferenceEquals(first, next), "successor reused the previous completion task");
        await next.WaitAsync(TimeSpan.FromSeconds(3));
        Require(condition.Active == 0 && listener.Errors.Count == 0, "successor stop failed");
    }
    static async Task CompletedFailureStop()
    {
        var primary = new InvalidOperationException("immediate check failed");
        var condition = new Fake { Check = _ => Task.FromException(primary) };
        var listener = new TestListener(condition);
        for (int i = 0; i < 3; ++i)
        {
            listener.StartListen(); Task stopped = listener.StopListenAsync();
            await stopped.WaitAsync(TimeSpan.FromSeconds(3));
            Require(condition.Calls == i + 1 && condition.Deactivations == i + 1 && !condition.IsActivated,
                "completed execution did not release the session before stop completed");
        }
        Require(listener.Errors.Count == 3 && listener.Errors.TrueForAll(e => ReferenceEquals(e, primary)),
            "synchronous failure diagnostics changed");
        await listener.StopListenAsync();
    }
    static async Task SessionBufferSizes()
    {
        for (int repeat = 0; repeat < 3; ++repeat)
        foreach (int count in new[] { 1, 5, 17, 33, 129, 5 })
        {
            var leaves = new Fake[count]; var group = new Group(ConditionCombineType.And);
            for (int i = 0; i < count; ++i) { leaves[i] = new Fake(); group.AddCondition(leaves[i]); }
            try
            {
                for (int round = 0; round < 3; ++round) await group.CheckAsync(CancellationToken.None);
                for (int i = 0; i < count; ++i) Require(leaves[i].Calls == 3, "pooled session size or completion state changed child rounds");
            }
            finally { await group.StopChecksAsync(); }
            group.AddCondition(new Fake());
        }
    }
    static async Task SessionBufferDrain()
    {
        var entered = Gate(); var release = Gate();
        var slow = new Fake { Check = async token => {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { entered.TrySetResult(true); await release.Task; }
        }};
        var first = new Group(ConditionCombineType.And, new Fake(), slow);
        var tail = new Group(ConditionCombineType.And, new Fake(), new Fake { Check = token => Task.Delay(Timeout.Infinite, token) });
        var root = new Group(ConditionCombineType.And, first, tail);
        await root.CheckAsync(CancellationToken.None); Task stopped = root.StopChecksAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int round = 0; round < 4; ++round)
            {
                var other = new Group(ConditionCombineType.And);
                for (int i = 0; i < 17; ++i) other.AddCondition(new Fake());
                await other.CheckAsync(CancellationToken.None); await other.StopChecksAsync();
            }
            Require(!stopped.IsCompleted && slow.Active == 1, "borrowing another session prematurely released the original drain");
        }
        finally { release.TrySetResult(true); }
        await stopped.WaitAsync(TimeSpan.FromSeconds(3));
        first.AddCondition(new Fake()); tail.AddCondition(new Fake()); root.AddCondition(new Fake());
        Require(slow.Active == 0, "nested session was not drained");
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference TemporarySessionReference(Group root)
    {
        var leaf = new Fake(); root.AddCondition(leaf);
        Task check = root.CheckAsync(CancellationToken.None);
        Require(check.IsCompleted, "reference fixture did not complete synchronously"); check.GetAwaiter().GetResult();
        Task stop = root.StopChecksAsync(); Require(stop.IsCompleted, "reference fixture did not stop synchronously"); stop.GetAwaiter().GetResult();
        root.Conditions!.Clear(); return new WeakReference(leaf);
    }
    static Task SessionBufferReferences()
    {
        var root = new Group(ConditionCombineType.And); WeakReference leaf = TemporarySessionReference(root);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(!leaf.IsAlive, "returned session buffers retained removed conditions"); GC.KeepAlive(root);
        return Task.CompletedTask;
    }

    static async Task CompletionDuringStartup()
    {
        TaskCompletionSource<bool>? ready = null;
        var first = new Fake { Check = _ => { ready = Gate(); return ready.Task; } };
        var pending = new Fake { Check = token => {
            ready!.TrySetResult(true);
            return Task.Delay(Timeout.Infinite, token);
        }};
        var group = new Group(ConditionCombineType.Or, first, pending);
        try
        {
            Task initial = group.CheckAsync(CancellationToken.None);
            Require(initial.IsCompleted, "completion during child startup was not observed synchronously");
            await initial;
            Require(first.Calls == 1 && first.Active == 0 && pending.Calls == 1 && pending.Active == 1,
                "startup completion restarted or canceled the pending child");

            Task next = group.CheckAsync(CancellationToken.None);
            Require(!next.IsCompleted && first.Calls == 2 && pending.Calls == 1 && pending.Canceled == 0,
                "next round did not rearm the completed child while retaining the pending check");
            ready!.TrySetResult(true);
            await next.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await group.StopChecksAsync(); }
        Require(first.Active == 0 && pending.Active == 0 && pending.Canceled == 1,
            "startup completion fixture did not drain the retained check");
    }
}
