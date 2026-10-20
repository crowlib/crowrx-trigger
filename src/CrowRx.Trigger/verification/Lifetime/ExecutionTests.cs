using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrowRx.Trigger;

static class ExecutionTests
{
    internal static IEnumerable<(string, Func<Task>)> Cases() => new (string, Func<Task>)[]
    {
        ("execution result is stable and supports multiple and late awaiters", StableResult),
        ("strict execution start rejects both checking and cleanup", BusyAndCleanup),
        ("execution handles exclude queued successors", QueuedSuccessor),
        ("execution completion does not stop automatic repetition", RepeatedExecutions),
        ("satisfied result preserves callback errors and stops repetition", CallbackError),
        ("satisfied result preserves cleanup errors and stops repetition", CleanupError),
        ("canceled result preserves cleanup errors", CanceledCleanupError),
        ("unavailable results preserve notification policy", Unavailable),
        ("execution failures preserve original errors and phases", FailurePhases),
        ("diagnostics do not initialize or evaluate conditions", PassiveDiagnostics),
        ("diagnostics preserve getter counts and short circuit unknowns", ShortCircuit),
        ("nested diagnostics preserve pending paths through stop", PendingPaths),
        ("overlapping diagnostic leases and copied records remain independent", DiagnosticLeases),
        ("opaque custom conditions do not invent nested paths", OpaqueCondition),
        ("immediate checks remain outside execution results", ImmediateCheck),
        ("concurrent completion readers share one task", ConcurrentCompletion),
        ("unavailable results preserve later cleanup errors", UnavailableCleanup),
        ("canceled asynchronous cleanup retains cancellation outcome", CanceledAsyncCleanup),
        ("satisfied group cleanup retains original child error paths", SatisfiedGroupCleanup),
        ("getter failures retain their observation phase", GetterFailures),
        ("late diagnostics do not infer unobserved child checks", LateDiagnostics),
        ("error observers preserve original errors and their own failures", ErrorObserverFailure),
        ("activation rollback retains both primary and cleanup errors", ActivationRollbackErrors)
    };

    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static TaskCompletionSource<bool> Gate() => new();
    static async Task Until(Func<bool> ready)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(3);
        while (!ready()) { if (DateTime.UtcNow > limit) throw new Exception("Execution test timed out."); await Task.Delay(1); }
    }
    static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected InvalidOperationException.");
    }

    static async Task StableResult()
    {
        var condition = new Fake { Satisfied = true };
        var listener = new TestListener(condition) { IsOneShot = true };
        ListenerExecution run = listener.StartExecution();
        Task<ListenerResult> first = run.Completion, second = run.Completion;
        Require(ReferenceEquals(first, second), "Completion task was replaced.");
        ListenerResult[] both = await Task.WhenAll(first, second);
        Require(both.All(x => x.Kind == ListenerResultKind.Satisfied && !x.HasErrors && x.ConditionSatisfied), "Invalid result.");
        Require(run.State == ListenerExecutionState.Completed && listener.CurrentExecution is null && condition.Deactivations == 1,
            "Completion preceded cleanup or ownership release.");
        Require((await run.Completion).ExecutionId == run.Id && listener.LastResult?.ExecutionId == run.Id, "Late result was lost.");
        ListenerExecution next = listener.StartExecution();
        Require(next.Id > run.Id && listener.LastResult?.ExecutionId == run.Id, "ID or previous result changed at startup.");
        await next.Completion;
        Require((await run.Completion).ExecutionId == run.Id, "Old handle points to successor.");
    }

    static async Task BusyAndCleanup()
    {
        var entered = Gate(); var release = Gate();
        var condition = new Fake { Check = async token =>
        { try { await Task.Delay(Timeout.Infinite, token); } finally { entered.TrySetResult(true); await release.Task; } } };
        var listener = new TestListener(condition);
        ListenerExecution run = listener.StartExecution();
        Reject(() => listener.StartExecution());
        listener.StopListen(); await entered.Task;
        try
        {
            Require(run.State == ListenerExecutionState.Stopping && !run.Completion.IsCompleted, "Cleanup was not visible.");
            Reject(() => listener.StartExecution());
        }
        finally { release.TrySetResult(true); }
        Require((await run.Completion).Kind == ListenerResultKind.Canceled, "Stop was not canceled.");
    }

    static async Task QueuedSuccessor()
    {
        var entered = Gate(); var release = Gate();
        var condition = new Fake { Check = async token =>
        { try { await Task.Delay(Timeout.Infinite, token); } finally { entered.TrySetResult(true); await release.Task; } } };
        var listener = new TestListener(condition);
        ListenerExecution first = listener.StartExecution();
        Task stop = listener.StopListenAsync(); await entered.Task;
        listener.StartListen();
        Require(first.RestartRequested, "Legacy restart was not queued.");
        release.TrySetResult(true); await stop;
        await Until(() => listener.CurrentExecution is not null && listener.CurrentExecution.Id != first.Id);
        ListenerExecution next = listener.CurrentExecution!;
        Require(!next.Completion.IsCompleted && (await first.Completion).Kind == ListenerResultKind.Canceled, "Await crossed execution boundary.");
        await listener.StopListenAsync();
    }

    static async Task RepeatedExecutions()
    {
        var listener = new TestListener(new Fake { Satisfied = true });
        int successes = 0; ListenerExecution? second = null;
        listener.Success += () => { if (++successes == 2) { second = listener.CurrentExecution; listener.StopListen(); } };
        ListenerExecution first = listener.StartExecution();
        ListenerResult result = await first.Completion;
        await Until(() => second?.State == ListenerExecutionState.Completed);
        Require(result.Kind == ListenerResultKind.Satisfied && second!.Id > first.Id &&
            (await second.Completion).Kind == ListenerResultKind.Satisfied && successes == 2, "Repeating executions overlapped or were canceled after success.");
    }

    static async Task CallbackError()
    {
        var error = new Exception("success callback");
        var condition = new Fake { Satisfied = true };
        var listener = new TestListener(condition);
        listener.Success += () => throw error;
        ListenerResult result = await listener.StartExecution().Completion;
        Require(result.Kind == ListenerResultKind.Satisfied && result.HasErrors && result.ConditionSatisfied, "Success was overwritten.");
        Require(result.Errors.Count == 1 && ReferenceEquals(result.Errors[0].Exception, error) &&
            result.Errors[0].Phase == ListenerErrorPhase.SuccessCallback, "Callback error was lost.");
        await Task.Yield();
        Require(condition.Calls == 1 && listener.CurrentExecution is null, "Callback failure repeated automatically.");
        bool readOnly = false;
        try { ((IList<ListenerError>)result.Errors)[0] = default; } catch (NotSupportedException) { readOnly = true; }
        Require(readOnly, "Published errors are mutable.");
    }

    static async Task CleanupError()
    {
        var error = new Exception("deactivation");
        var condition = new Fake { Satisfied = true, Deactivated = () => throw error };
        var listener = new TestListener(condition);
        ListenerResult result = await listener.StartExecution().Completion;
        Require(result.Kind == ListenerResultKind.Satisfied && result.Errors.Any(x => ReferenceEquals(x.Exception, error) &&
            x.Phase == ListenerErrorPhase.Deactivation), "Cleanup replaced satisfaction.");
        await Task.Yield(); Require(condition.Calls == 1 && listener.CurrentExecution is null, "Cleanup failure repeated automatically.");
    }

    static async Task CanceledCleanupError()
    {
        var error = new Exception("canceled cleanup");
        var condition = new Fake { Check = token => Task.Delay(Timeout.Infinite, token), Deactivated = () => throw error };
        var listener = new TestListener(condition);
        ListenerExecution run = listener.StartExecution();
        await listener.StopListenAsync();
        ListenerResult result = await run.Completion;
        Require(result.Kind == ListenerResultKind.Canceled && !result.ConditionSatisfied && result.HasErrors &&
            ReferenceEquals(result.Errors[0].Exception, error), "Cancellation or cleanup error was lost.");
    }

    static async Task Unavailable()
    {
        foreach (bool ignore in new[] { false, true })
        {
            var listener = new TestListener(new Fake { Valid = false }) { IsIgnoreNotAvailableCondition = ignore };
            ListenerResult result = await listener.StartExecution().Completion;
            Require(result.Kind == ListenerResultKind.Unavailable && result.HasErrors == !ignore &&
                listener.Errors.Count == (ignore ? 0 : 1), "Unavailable notification policy changed.");
        }
    }

    static async Task FailurePhases()
    {
        foreach (ListenerErrorPhase phase in new[] { ListenerErrorPhase.Initialization, ListenerErrorPhase.Activation, ListenerErrorPhase.Check })
        {
            var error = new Exception(phase.ToString());
            var condition = new Fake();
            if (phase == ListenerErrorPhase.Initialization) condition.Initialized = () => throw error;
            else if (phase == ListenerErrorPhase.Activation) condition.Activated = () => throw error;
            else condition.Check = _ => throw error;
            var listener = new TestListener(condition);
            ListenerResult result = await listener.StartExecution().Completion;
            Require(result.Kind == ListenerResultKind.Faulted && result.Errors.Count == 1 &&
                ReferenceEquals(result.Errors[0].Exception, error) && result.Errors[0].Phase == phase, "Failure phase or original error changed.");
        }
    }

    sealed class Probe : Condition
    {
        internal int ValidReads, SatisfiedReads, Checks;
        internal bool Value;
        internal Action? ReadingSatisfied;
        internal Func<CancellationToken, Task> Wait = _ => Task.CompletedTask;
        public override bool IsValid { get { ++ValidReads; return true; } }
        public override bool IsSatisfied { get { ++SatisfiedReads; ReadingSatisfied?.Invoke(); return Value; } }
        public override Task CheckAsync(CancellationToken token) { ++Checks; return Wait(token); }
    }

    static Task PassiveDiagnostics()
    {
        var probe = new Probe(); var listener = new TestListener(probe);
        using IDisposable lease = listener.AcquireDiagnostics();
        var data = new List<ConditionDiagnostic>();
        for (int i = 0; i < 5; ++i) Require(listener.CopyConditionDiagnostics(data) == 0, "Idle diagnostics fabricated execution.");
        Require(!listener.IsInitialized && !probe.IsInitialized && probe.ValidReads + probe.SatisfiedReads + probe.Checks == 0,
            "Diagnostics evaluated a condition.");
        return Task.CompletedTask;
    }

    static async Task<(int, int, int, int, int, int)> ShortCircuitRun(bool diagnostics)
    {
        var first = new Probe(); var second = new Probe { Value = true };
        var listener = new TestListener(new Group(ConditionCombineType.And, first, second));
        first.ReadingSatisfied = listener.StopListen;
        IDisposable? lease = diagnostics ? listener.AcquireDiagnostics() : null;
        try
        {
            await listener.StartExecution().Completion;
            var counts = (first.ValidReads, first.SatisfiedReads, first.Checks, second.ValidReads, second.SatisfiedReads, second.Checks);
            var data = new List<ConditionDiagnostic>();
            listener.CopyConditionDiagnostics(data);
            listener.CopyConditionDiagnostics(data);
            if (diagnostics)
            {
                ConditionDiagnostic one = data.Single(x => x.Path == "Root/0"), two = data.Single(x => x.Path == "Root/1");
                Require(one.IsSatisfied == false && two.IsSatisfied is null && two.IsValid == true,
                    "Short-circuited satisfaction was evaluated or guessed.");
            }
            Require(counts == (first.ValidReads, first.SatisfiedReads, first.Checks, second.ValidReads, second.SatisfiedReads, second.Checks),
                "Copy reevaluated conditions.");
            return counts;
        }
        finally { lease?.Dispose(); }
    }

    static async Task ShortCircuit() => Require(await ShortCircuitRun(false) == await ShortCircuitRun(true), "Diagnostics changed call counts.");

    static async Task PendingPaths()
    {
        var entered = Gate(); var release = Gate();
        var slow = new Fake { Check = async token =>
        { try { await Task.Delay(Timeout.Infinite, token); } finally { entered.TrySetResult(true); await release.Task; } } };
        var listener = new TestListener(new Group(ConditionCombineType.And, new Group(ConditionCombineType.Or, slow)));
        using IDisposable lease = listener.AcquireDiagnostics();
        ListenerExecution run = listener.StartExecution();
        var data = new List<ConditionDiagnostic>(); listener.CopyConditionDiagnostics(data);
        Require(data.Single(x => x.Path == "Root/0/0").CheckState == ConditionCheckState.Pending, "Nested pending path missing.");
        listener.StopListen(); await entered.Task;
        try { Require(!run.Completion.IsCompleted && run.State == ListenerExecutionState.Stopping, "Result preceded nested cleanup."); }
        finally { release.TrySetResult(true); }
        await run.Completion; listener.CopyConditionDiagnostics(data);
        Require(data.Single(x => x.Path == "Root/0/0").CheckState == ConditionCheckState.Canceled, "Nested terminal task state missing.");
    }

    static async Task DiagnosticLeases()
    {
        var probe = new Probe { Value = true }; var listener = new TestListener(probe) { IsOneShot = true };
        IDisposable first = listener.AcquireDiagnostics(), second = listener.AcquireDiagnostics();
        first.Dispose(); first.Dispose();
        ListenerExecution run = listener.StartExecution(); await run.Completion;
        var saved = new List<ConditionDiagnostic>(); listener.CopyConditionDiagnostics(saved);
        Require(saved.Count == 1 && saved[0].IsSatisfied == true, "Remaining lease was disabled.");
        second.Dispose();
        await listener.StartExecution().Completion;
        var current = new List<ConditionDiagnostic>(); listener.CopyConditionDiagnostics(current);
        Require(current.Count == 0 && saved[0].ExecutionId == run.Id && saved[0].IsSatisfied == true,
            "Released leases kept recording or copied records changed.");
        using IDisposable late = listener.AcquireDiagnostics();
        Require(listener.CopyConditionDiagnostics(current) == 0, "Late diagnostics invented historical records.");
    }

    sealed class Opaque : Condition
    {
        internal readonly Group Hidden = new(ConditionCombineType.And, new Fake { Satisfied = true });
        public override bool IsValid => Hidden.IsValid;
        public override bool IsSatisfied => Hidden.IsSatisfied;
        public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
    }
    static async Task OpaqueCondition()
    {
        var listener = new TestListener(new Opaque()) { IsOneShot = true };
        using IDisposable lease = listener.AcquireDiagnostics();
        await listener.StartExecution().Completion;
        var data = new List<ConditionDiagnostic>(); listener.CopyConditionDiagnostics(data);
        Require(data.Count == 1 && data[0].Path == "Root", "Opaque inner groups were presented as public children.");
    }

    static async Task ImmediateCheck()
    {
        var condition = new Fake { Satisfied = true, Check = token => Task.Delay(Timeout.Infinite, token) };
        var listener = new TestListener(condition); int successes = 0; listener.Success += () => ++successes;
        listener.CheckConditionsImmediately();
        Require(successes == 1 && listener.CurrentExecution is null && listener.LastResult is null, "Immediate check fabricated an execution.");
        ListenerExecution run = listener.StartExecution(); listener.CheckConditionsImmediately();
        Require((await run.Completion).Kind == ListenerResultKind.Canceled && successes == 2 && !run.ConditionSatisfied,
            "Immediate success was confused with execution satisfaction.");
    }

    static async Task ConcurrentCompletion()
    {
        var listener = new TestListener(new Fake { Check = token => Task.Delay(Timeout.Infinite, token) });
        ListenerExecution execution = listener.StartExecution();
        var tasks = new Task<ListenerResult>[32];
        await Task.WhenAll(Enumerable.Range(0, tasks.Length).Select(i => Task.Run(() => { tasks[i] = execution.Completion; })));
        Require(tasks.All(x => ReferenceEquals(x, tasks[0])), "Concurrent readers received different tasks.");
        await listener.StopListenAsync();
        Require((await Task.WhenAll(tasks)).All(x => x.ExecutionId == execution.Id), "A completion reader was stranded.");
    }

    static async Task UnavailableCleanup()
    {
        var error = new Exception("unavailable cleanup");
        var listener = new TestListener(new Fake { Valid = false, Deactivated = () => throw error }) { IsIgnoreNotAvailableCondition = true };
        ListenerResult result = await listener.StartExecution().Completion;
        Require(result.Kind == ListenerResultKind.Unavailable && result.Errors.Any(x => ReferenceEquals(x.Exception, error)),
            "Unavailable was replaced by cleanup failure.");
    }

    static async Task CanceledAsyncCleanup()
    {
        foreach (bool grouped in new[] { false, true })
        {
            var error = new Exception("async cleanup");
            var leaf = new Fake { Check = async token =>
            { try { await Task.Delay(Timeout.Infinite, token); } finally { await Task.Yield(); throw error; } } };
            var listener = new TestListener(grouped ? new Group(ConditionCombineType.And, leaf) : leaf);
            using IDisposable lease = listener.AcquireDiagnostics();
            ListenerExecution execution = listener.StartExecution();
            await listener.StopListenAsync();
            ListenerResult result = await execution.Completion;
            Require(result.Kind == ListenerResultKind.Canceled && result.Errors.Any(x => ReferenceEquals(x.Exception, error) && x.Phase == ListenerErrorPhase.Cleanup),
                "Canceled cleanup changed outcome or lost the original error.");
        }
    }

    static async Task SatisfiedGroupCleanup()
    {
        var error = new Exception("losing child cleanup");
        var slow = new Fake { Check = async token =>
        { try { await Task.Delay(Timeout.Infinite, token); } finally { await Task.Yield(); throw error; } } };
        var listener = new TestListener(new Group(ConditionCombineType.Or, new Fake { Satisfied = true }, slow));
        using IDisposable lease = listener.AcquireDiagnostics();
        ListenerResult result = await listener.StartExecution().Completion;
        Require(result.Kind == ListenerResultKind.Satisfied && result.Errors.Any(x => ReferenceEquals(x.Exception, error) &&
            x.ConditionPath == "Root/1" && x.Phase == ListenerErrorPhase.Cleanup), "Satisfied cleanup lost child origin.");
        await Task.Yield();
        Require(listener.CurrentExecution is null && slow.Calls == 1, "Cleanup errors repeated automatically.");
    }

    sealed class GetterFailure : Condition
    {
        internal readonly Exception Error = new("getter failure");
        internal bool Validity;
        public override bool IsValid => Validity ? throw Error : true;
        public override bool IsSatisfied => throw Error;
        public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
    }
    static async Task GetterFailures()
    {
        foreach (bool validity in new[] { false, true })
        {
            var condition = new GetterFailure { Validity = validity };
            var listener = new TestListener(condition);
            using IDisposable lease = listener.AcquireDiagnostics();
            ListenerResult result = await listener.StartExecution().Completion;
            Require(result.Kind == ListenerResultKind.Faulted && result.Errors.Any(x => ReferenceEquals(x.Exception, condition.Error) &&
                x.Phase == (validity ? ListenerErrorPhase.Validation : ListenerErrorPhase.Satisfaction) && x.ConditionPath == "Root"),
                "Getter error lost phase or path.");
        }
    }

    static async Task LateDiagnostics()
    {
        var slow = new Fake { Check = token => Task.Delay(Timeout.Infinite, token) };
        var listener = new TestListener(new Group(ConditionCombineType.And, slow));
        ListenerExecution execution = listener.StartExecution();
        using IDisposable lease = listener.AcquireDiagnostics();
        var data = new List<ConditionDiagnostic>();
        Require(listener.CopyConditionDiagnostics(data) == 0, "Late lease fabricated previously read values.");
        await listener.StopListenAsync();
        listener.CopyConditionDiagnostics(data);
        Require(data.All(x => x.ExecutionId == execution.Id && x.IsSatisfied is null), "Late diagnostics fabricated satisfaction.");
    }

    static async Task ErrorObserverFailure()
    {
        var original = new Exception("execution primary"); var handler = new Exception("execution observer");
        var listener = new TestListener(new Fake { Check = _ => throw original });
        listener.Exception += _ => throw handler;
        ListenerResult result = await listener.StartExecution().Completion;
        Require(result.Kind == ListenerResultKind.Faulted && result.Errors.Any(x => ReferenceEquals(x.Exception, original)) &&
            result.Errors.Any(x => ReferenceEquals(x.Exception, handler) && x.Phase == ListenerErrorPhase.ExceptionCallback && x.ConditionPath is null),
            "Error observer failure was lost or attributed to a condition.");
    }

    static async Task ActivationRollbackErrors()
    {
        var primary = new Exception("activation primary"); var cleanup = new Exception("rollback cleanup");
        var listener = new TestListener(new Fake { Activated = () => throw primary, Deactivated = () => throw cleanup });
        using IDisposable lease = listener.AcquireDiagnostics();
        ListenerResult result = await listener.StartExecution().Completion;
        Require(result.Kind == ListenerResultKind.Faulted && result.Errors.Count == 2 &&
            ReferenceEquals(result.Errors[0].Exception, primary) && result.Errors[0].Phase == ListenerErrorPhase.Activation &&
            ReferenceEquals(result.Errors[1].Exception, cleanup) && result.Errors[1].Phase == ListenerErrorPhase.Deactivation &&
            result.Errors.All(x => x.ConditionPath == "Root"), "Activation rollback lost an error or its origin.");
        Require(listener.Errors.Count == 1 && ReferenceEquals(listener.Errors[0], primary), "Legacy activation notifications changed.");
    }
}
