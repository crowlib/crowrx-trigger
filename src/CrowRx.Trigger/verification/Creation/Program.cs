using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CrowRx.Trigger;

class Leaf : Condition
{
    public bool Satisfied = true, Configured;
    public int Initializations;
    public Action? Initializing;
    public Func<CancellationToken, Task> Check = _ => Task.CompletedTask;
    public override bool IsValid => true;
    public override bool IsSatisfied => Satisfied;
    protected override void OnInitialize() { ++Initializations; Initializing?.Invoke(); }
    public override Task CheckAsync(CancellationToken token) => Check(token);
}
sealed class EqualLeaf : Leaf
{
    public override bool Equals(object? value) => value is EqualLeaf;
    public override int GetHashCode() => 0;
}
sealed class TargetLeaf : Leaf, IConditionTargetSharable<object>
{
    public object? Target { get; set; }
}
sealed class RawGroup : ConditionGroup
{
    public void Set(params ICondition[] conditions) => Conditions = new List<ICondition>(conditions);
}
sealed class HookGroup : ConditionGroup
{
    public int Hooks;
    public Action<ICondition>? Adding;
    protected override void OnConditionAdding(ICondition condition)
    {
        ++Hooks; ((Leaf)condition).Configured = true; Adding?.Invoke(condition); base.OnConditionAdding(condition);
    }
}
sealed class Input : IEnumerable<ICondition>
{
    readonly ICondition[] items;
    readonly Exception? failure;
    public int Enumerations, Disposals;
    public Input(ICondition[] items, Exception? failure = null) { this.items = items; this.failure = failure; }
    public IEnumerator<ICondition> GetEnumerator()
    {
        if (++Enumerations != 1) throw new Exception("Input was enumerated twice.");
        return Enumerate().GetEnumerator();
    }
    IEnumerable<ICondition> Enumerate()
    {
        try { foreach (ICondition item in items) yield return item; if (failure is not null) throw failure; }
        finally { ++Disposals; }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
sealed class RedirectedList : List<ICondition>, IEnumerable<ICondition>
{
    readonly ICondition[] items;
    public int Enumerations, Disposals;
    public RedirectedList(params ICondition[] items) { this.items = items; Add(new Leaf()); }
    IEnumerator<ICondition> IEnumerable<ICondition>.GetEnumerator() { ++Enumerations; return Enumerate().GetEnumerator(); }
    IEnumerable<ICondition> Enumerate()
    {
        try { foreach (ICondition item in items) yield return item; }
        finally { ++Disposals; }
    }
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<ICondition>)this).GetEnumerator();
}
sealed class Pump : SynchronizationContext
{
    readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
    public override void Post(SendOrPostCallback callback, object? state) => queue.Add(() => callback(state));
    public void Run(Func<Task> body)
    {
        SetSynchronizationContext(this); Task task = body();
        while (!task.IsCompleted) queue.Take()();
        task.GetAwaiter().GetResult();
    }
}
static class Program
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static T Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }
    static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--benchmark") >= 0) { Benchmark.Run(); return; }
        if (Array.IndexOf(args, "--enumeration") >= 0) { EnumerationBenchmark.Run(); return; }
        if (Array.IndexOf(args, "--capacity-tests") >= 0) { CapacityBenchmark.VerifyAndReport(); return; }
        if (Array.IndexOf(args, "--capacity") >= 0) { CapacityBenchmark.Run(); return; }
        new Pump().Run(Run);
    }
    static async Task Run()
    {
        var tests = new List<(string Name, Func<Task> Run)> {
            ("creation copies order and references without retaining the input list", OrderedCopy),
            ("null and empty inputs return inactive uninitialized empty groups", EmptyInputs),
            ("factory supports explicit AND and OR combinations", CombinationModes),
            ("invalid combination is rejected before input enumeration", InvalidCombination),
            ("factory enumerates once and disposes the enumerator", EnumerationOnce),
            ("throwing enumeration disposes input without initializing children", EnumerationFailure),
            ("duplicate instances are rejected", DuplicateInstances),
            ("reference validation accepts distinct value-equal conditions", EqualInstances),
            ("shared leaves across nested branches are rejected", NestedAliasing),
            ("cycles and nested null entries are rejected", InvalidNestedGraphs),
            ("final validation catches graph mutation during input enumeration", FinalGraphValidation),
            ("child initialization is deferred until explicit initialization", DeferredInitialization),
            ("initialization failure occurs after creation and preserves the original error", DeferredFailure),
            ("creation preserves existing child initialization and activation state", ExistingState),
            ("factory groups retain and drain pending checks", RetainedChecks),
            ("legacy additions retain partial success and configuration hooks", LegacyAdditions),
            ("nested target groups retain their configured targets", NestedTarget),
            ("failed validation releases condition references from pools", FailedReferences),
            ("array additions observe later element changes from hooks and initialization", ArrayMutation),
            ("array initialization failure preserves partial success and resets the addition guard", ArrayPartialFailure),
            ("covariant arrays preserve child references and null filtering", CovariantArrays),
            ("non-array enumeration failure preserves additions and disposes the input", AdditionEnumerationFailure),
            ("non-array input is disposed when child addition fails", AdditionDisposal),
            ("custom list enumerable implementations remain observable", CustomListEnumeration)
        };
        int passed = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); ++passed; Console.WriteLine("PASS " + test.Name); }
            catch (Exception error) { Console.WriteLine("FAIL " + test.Name + ": " + error.Message); }
        }
        Console.WriteLine($"{passed}/{tests.Count} passed; desktop .NET creation harness.");
        Environment.ExitCode = passed == tests.Count ? 0 : 1;
    }
    static Task OrderedCopy()
    {
        var first = new Leaf(); var last = new Leaf(); var input = new List<ICondition> { first, null!, last };
        ConditionGroup group = ConditionGroup.Create(input);
        Require(group.GetType() == typeof(ConditionGroup) && !group.IsInitialized && !group.IsActivated,
            "Factory did not return an inactive uninitialized base group.");
        Require(group.Conditions!.Count == 2 && ReferenceEquals(group.Conditions[0], first) && ReferenceEquals(group.Conditions[1], last),
            "Factory changed order or child references.");
        Require(!ReferenceEquals(group.Conditions, input), "Factory retained the caller's list.");
        input.Clear(); input.Add(new Leaf());
        Require(group.Conditions.Count == 2, "Input mutation changed the constructed group.");
        group.Conditions.Clear(); Require(input.Count == 1, "Group mutation changed the caller's list.");
        return Task.CompletedTask;
    }
    static Task EmptyInputs()
    {
        foreach (IEnumerable<ICondition>? input in new IEnumerable<ICondition>?[] { null, Array.Empty<ICondition>(), new ICondition[] { null! } })
        {
            ConditionGroup group = ConditionGroup.Create(input);
            Require(group.Conditions is not null && group.Conditions.Count == 0 && !group.IsInitialized && !group.IsActivated,
                "Empty factory result changed its initial state.");
            Require(!group.IsValid && group.IsSatisfied, "Empty AND factory group changed its existing validity or satisfaction semantics.");
        }
        ConditionGroup or = ConditionGroup.Create(null, ConditionCombineType.Or);
        Require(!or.IsValid && !or.IsSatisfied, "Empty OR factory group changed its existing satisfaction semantics.");
        return Task.CompletedTask;
    }
    static Task CombinationModes()
    {
        var first = new Leaf(); var last = new Leaf { Satisfied = false };
        ConditionGroup and = ConditionGroup.Create(new ICondition[] { first, last });
        ConditionGroup or = ConditionGroup.Create(new ICondition[] { first, last }, ConditionCombineType.Or);
        Require(and.CombineType == ConditionCombineType.And && !and.IsSatisfied && or.CombineType == ConditionCombineType.Or && or.IsSatisfied,
            "Factory did not apply the requested combination.");
        return Task.CompletedTask;
    }
    static Task InvalidCombination()
    {
        var input = new Input(new ICondition[] { new Leaf() });
        var error = Reject<ArgumentOutOfRangeException>(() => ConditionGroup.Create(input, (ConditionCombineType)42));
        Require(error.ParamName == "combineType" && input.Enumerations == 0, "Invalid combination consumed the input.");
        return Task.CompletedTask;
    }
    static Task EnumerationOnce()
    {
        var first = new Leaf(); var last = new Leaf(); var input = new Input(new ICondition[] { first, null!, last });
        ConditionGroup group = ConditionGroup.Create(input);
        Require(input.Enumerations == 1 && input.Disposals == 1 && group.Conditions!.Count == 2,
            "Factory did not enumerate once and dispose the input.");
        return Task.CompletedTask;
    }
    static Task EnumerationFailure()
    {
        var child = new Leaf(); var marker = new InvalidOperationException("Input failed.");
        var input = new Input(new ICondition[] { child }, marker);
        Exception error = Reject<InvalidOperationException>(() => ConditionGroup.Create(input));
        Require(ReferenceEquals(error, marker) && input.Enumerations == 1 && input.Disposals == 1 && child.Initializations == 0,
            "Enumeration failure changed diagnostics or initialized a child.");
        return Task.CompletedTask;
    }
    static Task DuplicateInstances()
    {
        var child = new Leaf(); Reject<InvalidOperationException>(() => ConditionGroup.Create(new ICondition[] { child, child }));
        Require(child.Initializations == 0, "Validation failure initialized a child."); return Task.CompletedTask;
    }
    static Task EqualInstances()
    {
        var first = new EqualLeaf(); var last = new EqualLeaf();
        Require(ConditionGroup.Create(new ICondition[] { first, last }).Conditions!.Count == 2, "Value equality replaced reference identity.");
        return Task.CompletedTask;
    }
    static Task NestedAliasing()
    {
        var shared = new Leaf(); var left = new RawGroup(); var right = new RawGroup(); left.Set(shared); right.Set(shared);
        Reject<InvalidOperationException>(() => ConditionGroup.Create(new ICondition[] { left, right })); return Task.CompletedTask;
    }
    static Task InvalidNestedGraphs()
    {
        var self = new RawGroup(); self.Set(self);
        Reject<InvalidOperationException>(() => ConditionGroup.Create(new ICondition[] { self }));
        var left = new RawGroup(); var right = new RawGroup(); left.Set(right); right.Set(left);
        Reject<InvalidOperationException>(() => ConditionGroup.Create(new ICondition[] { left }));
        var invalid = new RawGroup(); invalid.Set(new Leaf(), null!);
        Reject<InvalidOperationException>(() => ConditionGroup.Create(new ICondition[] { invalid })); return Task.CompletedTask;
    }
    static IEnumerable<ICondition> MutatingInput(RawGroup left, RawGroup right, Leaf shared)
    {
        yield return left; right.Set(shared); yield return right;
    }
    static Task FinalGraphValidation()
    {
        var shared = new Leaf(); var left = new RawGroup(); var right = new RawGroup(); left.Set(shared); right.Set(new Leaf());
        Reject<InvalidOperationException>(() => ConditionGroup.Create(MutatingInput(left, right, shared))); return Task.CompletedTask;
    }
    static Task DeferredInitialization()
    {
        var child = new Leaf(); ConditionGroup group = ConditionGroup.Create(new ICondition[] { child });
        Require(child.Initializations == 0 && !child.IsInitialized, "Creation initialized the child.");
        group.Initialize(); group.Initialize();
        Require(group.IsInitialized && child.IsInitialized && child.Initializations == 1, "Explicit initialization did not initialize once.");
        var listenerChild = new Leaf(); var listener = new Listener(ConditionGroup.Create(new ICondition[] { listenerChild }));
        listener.Initialize(); Require(listenerChild.Initializations == 1, "Listener initialization did not initialize the factory group.");
        return Task.CompletedTask;
    }
    static Task DeferredFailure()
    {
        var marker = new InvalidOperationException("Initialization failed."); var child = new Leaf { Initializing = () => throw marker };
        ConditionGroup group = ConditionGroup.Create(new ICondition[] { child });
        Exception error = Reject<InvalidOperationException>(group.Initialize);
        Require(ReferenceEquals(error, marker) && !group.IsInitialized && child.Initializations == 1 && group.Conditions!.Count == 1,
            "Deferred initialization failure changed the original error or group state.");
        return Task.CompletedTask;
    }
    static Task ExistingState()
    {
        var child = new Leaf(); child.Initialize(); child.Activate();
        try {
            ConditionGroup group = ConditionGroup.Create(new ICondition[] { child });
            Require(!group.IsInitialized && !group.IsActivated && child.IsInitialized && child.IsActivated && child.Initializations == 1,
                "Creation changed existing child state.");
        }
        finally { child.Deactivate(); }
        return Task.CompletedTask;
    }
    static async Task RetainedChecks()
    {
        var ready = new Leaf(); var pending = new Leaf { Satisfied = false, Check = token => Task.Delay(Timeout.Infinite, token) };
        ConditionGroup group = ConditionGroup.Create(new ICondition[] { ready, pending }, ConditionCombineType.Or);
        try { await group.CheckAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { await group.StopChecksAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        group.AddCondition(new Leaf()); Require(group.Conditions!.Count == 3, "Factory group did not support cleanup and subsequent additions.");
    }
    static Task LegacyAdditions()
    {
        var first = new Leaf(); var last = new Leaf(); var group = new ConditionGroup();
        Reject<InvalidOperationException>(() => group.AddConditions(new ICondition[] { first, last, first }));
        Require(group.Conditions!.Count == 2 && ReferenceEquals(group.Conditions[0], first) && ReferenceEquals(group.Conditions[1], last),
            "Legacy partial success changed.");
        var hooked = new HookGroup(); hooked.Initialize(); var child = new Leaf();
        child.Initializing = () => Require(child.Configured, "Legacy initialization ran before configuration.");
        hooked.AddConditions(new ICondition[] { child });
        Require(hooked.Hooks == 1 && child.Initializations == 1 && child.IsInitialized, "Legacy hooks or immediate initialization changed.");
        return Task.CompletedTask;
    }
    static Task NestedTarget()
    {
        var target = new object(); var child = new TargetLeaf(); var nested = new ConditionGroupShareTarget<object> { Target = target };
        nested.AddCondition(child); ConditionGroup group = ConditionGroup.Create(new ICondition[] { nested });
        Require(ReferenceEquals(child.Target, target) && !child.IsInitialized, "Creation changed a nested target.");
        group.Initialize(); Require(ReferenceEquals(child.Target, target) && child.Initializations == 1, "Nested target initialization changed.");
        return Task.CompletedTask;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference FailedReference()
    {
        var child = new Leaf(); Reject<InvalidOperationException>(() => ConditionGroup.Create(new ICondition[] { child, child }));
        return new WeakReference(child);
    }
    static Task FailedReferences()
    {
        WeakReference child = FailedReference(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(!child.IsAlive, "Failed validation retained a condition reference.");
        Require(ConditionGroup.Create(new ICondition[] { new Leaf() }).Conditions!.Count == 1, "Factory did not recover after failed validation.");
        return Task.CompletedTask;
    }

    static Task ArrayMutation()
    {
        var first = new Leaf(); var replaced = new Leaf(); var skipped = new Leaf(); var replacement = new Leaf();
        ICondition[] input = { first, replaced, skipped };
        var group = new HookGroup(); group.Initialize();
        group.Adding = child => { if (ReferenceEquals(child, first)) input[1] = replacement; };
        first.Initializing = () => { Require(first.Configured, "Array initialization ran before its hook."); input[2] = null!; };
        group.AddConditions(input);
        Require(group.Conditions!.Count == 2 && ReferenceEquals(group.Conditions[0], first) && ReferenceEquals(group.Conditions[1], replacement),
            "Array traversal used a snapshot or changed ordering.");
        Require(group.Hooks == 2 && first.Initializations == 1 && replacement.Initializations == 1 && replaced.Initializations == 0 && skipped.Initializations == 0,
            "Array mutation changed hook or initialization behavior.");
        return Task.CompletedTask;
    }
    static Task ArrayPartialFailure()
    {
        var first = new Leaf(); var marker = new InvalidOperationException("Array child initialization failed.");
        var failed = new Leaf { Initializing = () => throw marker }; var last = new Leaf();
        var group = new HookGroup(); group.Initialize();
        Exception error = Reject<InvalidOperationException>(() => group.AddConditions(new ICondition[] { first, failed, last }));
        Require(ReferenceEquals(error, marker) && group.Conditions!.Count == 1 && ReferenceEquals(group.Conditions[0], first),
            "Array failure changed the original error or partial success.");
        Require(group.Hooks == 2 && failed.Configured && failed.Initializations == 1 && !failed.IsInitialized && last.Initializations == 0,
            "Array failure processed later children or lost callback effects.");
        group.AddCondition(last); Require(last.IsInitialized && group.Conditions!.Count == 2, "Array failure left the addition guard locked.");
        return Task.CompletedTask;
    }
    static Task CovariantArrays()
    {
        var first = new Leaf(); var last = new Leaf(); Leaf[] input = { first, null!, last };
        var added = new ConditionGroup(); added.AddConditions(input); ConditionGroup created = ConditionGroup.Create(input);
        foreach (ConditionGroup group in new[] { added, created })
            Require(group.Conditions!.Count == 2 && ReferenceEquals(group.Conditions[0], first) && ReferenceEquals(group.Conditions[1], last),
                "Covariant array traversal changed references or null filtering.");
        return Task.CompletedTask;
    }
    static Task AdditionEnumerationFailure()
    {
        var first = new Leaf(); var last = new Leaf(); var marker = new InvalidOperationException("Addition input failed.");
        var input = new Input(new ICondition[] { first, null!, last }, marker); var group = new ConditionGroup();
        Exception error = Reject<InvalidOperationException>(() => group.AddConditions(input));
        Require(ReferenceEquals(error, marker) && input.Enumerations == 1 && input.Disposals == 1 && group.Conditions!.Count == 2,
            "Non-array enumeration failure changed partial success or disposal.");
        return Task.CompletedTask;
    }
    static Task AdditionDisposal()
    {
        var child = new Leaf(); var input = new Input(new ICondition[] { child, child, new Leaf() }); var group = new ConditionGroup();
        Reject<InvalidOperationException>(() => group.AddConditions(input));
        Require(input.Enumerations == 1 && input.Disposals == 1 && group.Conditions!.Count == 1,
            "Addition failure did not dispose its non-array input.");
        return Task.CompletedTask;
    }
    static Task CustomListEnumeration()
    {
        var first = new Leaf(); var last = new Leaf(); var input = new RedirectedList(first, null!, last);
        var added = new ConditionGroup(); added.AddConditions(input); ConditionGroup created = ConditionGroup.Create(input);
        Require(input.Count == 1 && input.Enumerations == 2 && input.Disposals == 2, "List specialization bypassed the custom enumerable.");
        foreach (ConditionGroup group in new[] { added, created })
            Require(group.Conditions!.Count == 2 && ReferenceEquals(group.Conditions[0], first) && ReferenceEquals(group.Conditions[1], last),
                "Custom list enumeration was replaced by underlying list contents.");
        return Task.CompletedTask;
    }
}
