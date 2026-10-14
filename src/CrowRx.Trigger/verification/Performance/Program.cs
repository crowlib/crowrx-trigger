using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using CrowRx.Trigger;

sealed class Pump : SynchronizationContext
{
    readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new Queue<(SendOrPostCallback, object?)>(64);
    readonly int thread = Environment.CurrentManagedThreadId;
    public int ExternalPosts;
    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (thread != Environment.CurrentManagedThreadId) ++ExternalPosts;
        queue.Enqueue((callback, state));
    }
    public bool Step()
    {
        if (queue.Count == 0) return false;
        var item = queue.Dequeue(); item.Callback(item.State); return true;
    }
    public void Drain() { int n = 0; while (Step()) if (++n > 1000) throw new Exception("Unexpected infinite pump"); }
}
sealed class Leaf : Condition
{
    public bool Satisfied = true;
    public override bool IsValid => true;
    public override bool IsSatisfied => Satisfied;
    public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
}
sealed class PendingLeaf : Condition
{
    readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>();
    readonly Action cancel;
    bool registered;
    public PendingLeaf() { cancel = () => ready.TrySetCanceled(); }
    public override bool IsValid => true;
    public override bool IsSatisfied => false;
    public override Task CheckAsync(CancellationToken token)
    { if (!registered) { token.Register(cancel); registered = true; } return ready.Task; }
}
sealed class Group : ConditionGroup
{
    public void ValidateOnly() => ValidateConditionGraph();
}
sealed class QueuedLeaf : Condition
{
    readonly Pump pump;
    static readonly SendOrPostCallback Complete = state => ((TaskCompletionSource<bool>)state!).SetResult(true);
    public QueuedLeaf(Pump pump) => this.pump = pump;
    public override bool IsValid => true;
    public override bool IsSatisfied => true;
    public override Task CheckAsync(CancellationToken token)
    {
        var ready = new TaskCompletionSource<bool>();
        pump.Post(Complete, ready);
        return ready.Task;
    }
}
sealed class TargetLeaf : Condition, IConditionTargetSharable<object>
{
    public object? Target { get; set; }
    public override bool IsValid => true;
    public override bool IsSatisfied => false;
    public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
}
sealed class BenchCase
{
    public readonly string Name;
    public readonly int Count;
    public readonly Action Work, Finish;
    public BenchCase(string name, int count, Action work, Action? finish = null)
    { Name = name; Count = count; Work = work; Finish = finish ?? (() => { }); }
}
sealed class Result
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public int Iterations { get; set; }
    public double MedianNs { get; set; }
    public double MinNs { get; set; }
    public double MaxNs { get; set; }
    public double BytesPerOp { get; set; }
    public double MinBytesPerOp { get; set; }
    public double MaxBytesPerOp { get; set; }
    public int Gen0Collections { get; set; }
}
static class Program
{
    static object? allocationSink;
    static readonly SendOrPostCallback NoOpCallback = _ => { };
    static Group Flat(int count, bool mixed = false, bool satisfied = true)
    {
        var g = new Group();
        for (int i = 0; i < count; ++i)
            g.AddCondition(mixed && i > 0 ? (ICondition)new PendingLeaf() : new Leaf { Satisfied = satisfied });
        return g;
    }
    static Group Nested()
    {
        var g = new Group(); for (int i = 0; i < 8; ++i) g.AddCondition(Flat(4)); return g;
    }
    static void Check(Group g, CancellationToken token)
    {
        Task task = g.CheckAsync(token);
        if (!task.IsCompleted) throw new Exception("Synchronous benchmark suspended unexpectedly");
        task.GetAwaiter().GetResult();
    }
    static void Finish(Group g, CancellationTokenSource cts, Pump pump)
    { cts.Cancel(); g.Deactivate(); pump.Drain(); cts.Dispose(); }
    static BenchCase SteadyCheck(Pump pump, int count, bool mixed)
    {
        var g = Flat(count, mixed); var cts = new CancellationTokenSource(); g.Initialize(); g.Activate();
        Check(g, cts.Token);
        return new BenchCase(mixed ? "check_mixed_ready_pending" : "check_all_ready", count,
            () => Check(g, cts.Token), () => Finish(g, cts, pump));
    }
    static BenchCase GroupCycle(Pump pump, int count)
    {
        var g = Flat(count); var cts = new CancellationTokenSource(); g.Initialize();
        return new BenchCase("group_start_check_stop", count, () => { g.Activate(); Check(g, cts.Token); g.Deactivate(); }, () => Finish(g, cts, pump));
    }
    static BenchCase ListenerCycle(Pump pump, int count, bool nested, bool auto)
    {
        var g = nested ? Nested() : Flat(count); var listener = new Listener(g) { IsOneShot = !auto };
        int successes = 0; listener.Success += () => ++successes;
        listener.Initialize();
        if (auto) listener.StartListen();
        Action work = auto ? (Action)(() => {
            int expected = successes + 1; int steps = 0;
            while (successes < expected) if (!pump.Step() || ++steps > 16) throw new Exception("Automatic listener did not advance");
        }) : () => { int expected = successes + 1; listener.StartListen(); pump.Drain(); if (successes != expected) throw new Exception("One-shot listener result mismatch"); };
        return new BenchCase(auto ? (nested ? "listener_auto_nested" : "listener_auto_flat") : "listener_one_shot",
            count, work, () => { Task stopped = listener.StopListenAsync(); pump.Drain(); stopped.GetAwaiter().GetResult(); });
    }
    static BenchCase ListenerStopEach(Pump pump, int count)
    {
        var listener = new Listener(Flat(count)) { IsOneShot = true }; listener.Initialize();
        return new BenchCase("listener_stop_async_each", count, () => {
            listener.StartListen(); Task stop = listener.StopListenAsync(); pump.Drain(); stop.GetAwaiter().GetResult();
        });
    }
    static Result Measure(BenchCase c, int iterations)
    {
        for (int i = 0; i < 2000; ++i) c.Work();
        const int batches = 5;
        var ns = new double[batches]; var bytes = new double[batches]; int gen0 = 0;
        for (int b = 0; b < batches; ++b)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int gcBefore = GC.CollectionCount(0);
            long allocated = GC.GetAllocatedBytesForCurrentThread(); long ticks = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; ++i) c.Work();
            long elapsed = Stopwatch.GetTimestamp() - ticks;
            long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
            gen0 += GC.CollectionCount(0) - gcBefore;
            ns[b] = (double)elapsed * 1_000_000_000 / Stopwatch.Frequency / iterations;
            bytes[b] = (double)delta / iterations;
        }
        Array.Sort(ns); Array.Sort(bytes);
        return new Result { Name = c.Name, Count = c.Count, Iterations = iterations, MedianNs = ns[2], MinNs = ns[0], MaxNs = ns[4],
            BytesPerOp = bytes[2], MinBytesPerOp = bytes[0], MaxBytesPerOp = bytes[4], Gen0Collections = gen0 };
    }
    static void Main(string[] args)
    {
        var pump = new Pump(); SynchronizationContext.SetSynchronizationContext(pump);
        var results = new List<Result>();
        void Run(BenchCase c, int iterations = 8000)
        {
            try { results.Add(Measure(c, iterations)); } finally { c.Finish(); }
        }
        Run(new BenchCase("control_no_op", 0, () => { }));
        Run(new BenchCase("control_allocate_64_byte_array", 0, () => Volatile.Write(ref allocationSink, new byte[64])));
        Run(new BenchCase("control_context_post", 0, () => { pump.Post(NoOpCallback, null); pump.Drain(); }));
        foreach (int count in new[] { 1, 8, 32, 128 })
        {
            var g = Flat(count); Run(new BenchCase("validate_flat", count, g.ValidateOnly));
            Run(SteadyCheck(pump, count, false));
            Run(GroupCycle(pump, count));
        }
        foreach (int count in new[] { 8, 32 }) Run(SteadyCheck(pump, count, true));
        foreach (int count in new[] { 1, 8 })
        {
            var waiting = new Group(); for (int i = 0; i < count; ++i) waiting.AddCondition(new QueuedLeaf(pump));
            var tokenSource = new CancellationTokenSource(); waiting.Initialize(); waiting.Activate();
            Run(new BenchCase("check_all_pending_queued", count, () => {
                Task check = waiting.CheckAsync(tokenSource.Token); pump.Drain(); check.GetAwaiter().GetResult();
            }, () => Finish(waiting, tokenSource, pump)));
        }
        var nested = Nested(); Run(new BenchCase("validate_nested", 32, nested.ValidateOnly));
        Run(ListenerCycle(pump, 1, false, false)); Run(ListenerCycle(pump, 8, false, false));
        Run(ListenerCycle(pump, 8, false, true)); Run(ListenerCycle(pump, 32, true, true));
        Run(ListenerStopEach(pump, 8));
        var unsatisfied = Flat(8, false, false); var polling = new Listener(unsatisfied); polling.StartListen();
        Run(new BenchCase("listener_poll_unsatisfied", 8, () => { if (!pump.Step()) throw new Exception("Polling did not resume"); },
            () => { Task stopped = polling.StopListenAsync(); pump.Drain(); stopped.GetAwaiter().GetResult(); }));
        var targetGroup = new ConditionGroupShareTarget<object>(); for (int i = 0; i < 8; ++i) targetGroup.AddCondition(new TargetLeaf());
        var a = new object(); var b = new object(); bool toggle = false;
        Run(new BenchCase("target_change", 8, () => { toggle = !toggle; targetGroup.Target = toggle ? a : b; }));
        if (results[0].BytesPerOp != 0 || results[1].BytesPerOp <= 0 || pump.ExternalPosts != 0)
            throw new Exception("Allocation controls or single-thread measurement contract failed");
        Console.WriteLine(JsonSerializer.Serialize(new {
            Run = args.Length == 0 ? "baseline" : args[0], Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount, GCServer = System.Runtime.GCSettings.IsServerGC,
            LibraryMvid = typeof(ConditionGroup).Assembly.ManifestModule.ModuleVersionId.ToString(),
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            Warmup = 2000, Batches = 5, Counter = "GC.GetAllocatedBytesForCurrentThread", ExternalPosts = pump.ExternalPosts, Results = results
        }));
    }
}
