extern alias B;
extern alias F;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

sealed class BaselineLeaf : B::CrowRx.Trigger.Condition
{
    readonly Func<CancellationToken, Task> check;
    public BaselineLeaf(Func<CancellationToken, Task> check) => this.check = check;
    public override bool IsValid => true;
    public override bool IsSatisfied => true;
    public override Task CheckAsync(CancellationToken token) => check(token);
}
sealed class FusedLeaf : F::CrowRx.Trigger.Condition
{
    readonly Func<CancellationToken, Task> check;
    public FusedLeaf(Func<CancellationToken, Task> check) => this.check = check;
    public override bool IsValid => true;
    public override bool IsSatisfied => true;
    public override Task CheckAsync(CancellationToken token) => check(token);
}
interface IGroup
{
    void Add(Func<CancellationToken, Task> check);
    void Add(IGroup child);
    void Initialize();
    void Activate();
    Task Check();
    Task Stop();
    void Deactivate();
    IListener Listen();
}
interface IListener
{
    int Successes { get; }
    void Start();
    Task Stop();
}
sealed class BaselineGroup : IGroup
{
    readonly B::CrowRx.Trigger.ConditionGroup group = new B::CrowRx.Trigger.ConditionGroup();
    public void Add(Func<CancellationToken, Task> check) => group.AddCondition(new BaselineLeaf(check));
    public void Add(IGroup child) => group.AddCondition(((BaselineGroup)child).group);
    public void Initialize() => group.Initialize();
    public void Activate() => group.Activate();
    public Task Check() => group.CheckAsync(CancellationToken.None);
    public Task Stop() => group.StopChecksAsync();
    public void Deactivate() => group.Deactivate();
    public IListener Listen() => new BaselineListener(group);
}
sealed class FusedGroup : IGroup
{
    readonly F::CrowRx.Trigger.ConditionGroup group = new F::CrowRx.Trigger.ConditionGroup();
    public void Add(Func<CancellationToken, Task> check) => group.AddCondition(new FusedLeaf(check));
    public void Add(IGroup child) => group.AddCondition(((FusedGroup)child).group);
    public void Initialize() => group.Initialize();
    public void Activate() => group.Activate();
    public Task Check() => group.CheckAsync(CancellationToken.None);
    public Task Stop() => group.StopChecksAsync();
    public void Deactivate() => group.Deactivate();
    public IListener Listen() => new FusedListener(group);
}
sealed class BaselineListener : IListener
{
    readonly B::CrowRx.Trigger.Listener listener;
    public int Successes { get; private set; }
    public BaselineListener(B::CrowRx.Trigger.ConditionGroup group)
    {
        listener = new B::CrowRx.Trigger.Listener(group);
        listener.Success += () => ++Successes;
        listener.Initialize();
    }
    public void Start() => listener.StartListen();
    public Task Stop() => listener.StopListenAsync();
}
sealed class FusedListener : IListener
{
    readonly F::CrowRx.Trigger.Listener listener;
    public int Successes { get; private set; }
    public FusedListener(F::CrowRx.Trigger.ConditionGroup group)
    {
        listener = new F::CrowRx.Trigger.Listener(group);
        listener.Success += () => ++Successes;
        listener.Initialize();
    }
    public void Start() => listener.StartListen();
    public Task Stop() => listener.StopListenAsync();
}
sealed class Pump : SynchronizationContext
{
    readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new Queue<(SendOrPostCallback, object?)>(256);
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
    public void Drain()
    {
        int count = 0;
        while (Step()) if (++count > 4096) throw new Exception("Measurement queue did not drain.");
    }
}
sealed class Work
{
    public Action Start = () => { }, Run = () => { }, Finish = () => { };
}
static class Program
{
    static object? allocationSink;
    static readonly Func<CancellationToken, Task> Ready = _ => Task.CompletedTask;
    static readonly SendOrPostCallback Complete = state => ((TaskCompletionSource<bool>)state!).SetResult(true);

    static Work ReadyChecks(Func<IGroup> factory, int count, int readyIndex)
    {
        IGroup group = factory(); TaskCompletionSource<bool>? pending = null;
        for (int i = 0; i < count; ++i) group.Add(readyIndex < 0 || i == readyIndex ? Ready : _ => pending!.Task);
        group.Initialize();
        return new Work {
            Start = () => { pending = new TaskCompletionSource<bool>(); group.Activate(); group.Check().GetAwaiter().GetResult(); },
            Run = () => group.Check().GetAwaiter().GetResult(),
            Finish = () => { pending!.TrySetResult(true); group.Stop().GetAwaiter().GetResult(); group.Deactivate(); }
        };
    }
    static Work QueuedChecks(Func<IGroup> factory, int count, Pump pump)
    {
        IGroup group = factory();
        for (int i = 0; i < count; ++i) group.Add(_ => {
            var ready = new TaskCompletionSource<bool>(); pump.Post(Complete, ready); return ready.Task;
        });
        group.Initialize();
        return new Work {
            Start = () => { group.Activate(); Task initial = group.Check(); pump.Drain(); initial.GetAwaiter().GetResult(); },
            Run = () => { Task check = group.Check(); pump.Drain(); check.GetAwaiter().GetResult(); },
            Finish = () => { Task stop = group.Stop(); pump.Drain(); stop.GetAwaiter().GetResult(); group.Deactivate(); }
        };
    }
    static Work StartupCompletion(Func<IGroup> factory, int count)
    {
        IGroup group = factory(); var sources = new TaskCompletionSource<bool>[count];
        for (int i = 0; i < count; ++i)
        {
            int index = i;
            group.Add(_ => {
                sources[index] = new TaskCompletionSource<bool>();
                if (index == count - 1) sources[0].SetResult(true);
                return sources[index].Task;
            });
        }
        group.Initialize();
        return new Work { Run = () => {
            group.Activate(); Task check = group.Check();
            if (!check.IsCompleted) throw new Exception("Completion during startup was not observed synchronously.");
            check.GetAwaiter().GetResult();
            foreach (var source in sources) source.TrySetResult(true);
            group.Stop().GetAwaiter().GetResult(); group.Deactivate();
        }};
    }
    static Work Automatic(Func<IGroup> factory, bool nested, Pump pump)
    {
        IGroup group = factory();
        for (int i = 0; i < 8; ++i)
        {
            if (!nested) { group.Add(Ready); continue; }
            IGroup child = factory(); for (int j = 0; j < 4; ++j) child.Add(Ready); group.Add(child);
        }
        IListener listener = group.Listen();
        return new Work {
            Start = listener.Start,
            Run = () => {
                int target = listener.Successes + 1, steps = 0;
                while (listener.Successes < target)
                    if (!pump.Step() || ++steps > 32) throw new Exception("Automatic listener did not advance.");
            },
            Finish = () => { Task stop = listener.Stop(); pump.Drain(); stop.GetAwaiter().GetResult(); }
        };
    }
    static (double ns, double bytes) Batch(Work work)
    {
        work.Start();
        try
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long bytes = GC.GetAllocatedBytesForCurrentThread(), tick = Stopwatch.GetTimestamp();
            for (int i = 0; i < 4000; ++i) work.Run();
            long elapsed = Stopwatch.GetTimestamp() - tick, allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            return ((double)elapsed * 1e9 / Stopwatch.Frequency / 4000, (double)allocated / 4000);
        }
        finally { work.Finish(); }
    }
    static object Summarize(string variant, string name, int count, double[] ns, double[] bytes)
    {
        Array.Sort(ns); Array.Sort(bytes);
        return new { Variant = variant, Name = name, Count = count, Iterations = 4000,
            MedianNs = ns[3], MinNs = ns[0], MaxNs = ns[6], BytesPerOp = bytes[3], MinBytesPerOp = bytes[0], MaxBytesPerOp = bytes[6] };
    }
    static void Pair(List<object> results, string name, int count, Work baseline, Work fused)
    {
        foreach (Work work in new[] { baseline, fused })
        {
            work.Start(); try { for (int i = 0; i < 2000; ++i) work.Run(); } finally { work.Finish(); }
        }
        var bn = new double[7]; var bb = new double[7]; var fn = new double[7]; var fb = new double[7];
        for (int batch = 0; batch < 7; ++batch)
        {
            (double ns, double bytes) b, f;
            if ((batch & 1) == 0) { b = Batch(baseline); f = Batch(fused); }
            else { f = Batch(fused); b = Batch(baseline); }
            bn[batch] = b.ns; bb[batch] = b.bytes; fn[batch] = f.ns; fb[batch] = f.bytes;
        }
        results.Add(Summarize("baseline", name, count, bn, bb));
        results.Add(Summarize("fused", name, count, fn, fb));
    }
    static void Main()
    {
        var pump = new Pump(); SynchronizationContext.SetSynchronizationContext(pump);
        var results = new List<object>();
        Pair(results, "control_no_op", 0, new Work(), new Work());
        Pair(results, "control_allocate_64", 0,
            new Work { Run = () => Volatile.Write(ref allocationSink, new byte[64]) },
            new Work { Run = () => Volatile.Write(ref allocationSink, new byte[64]) });
        Func<IGroup> baseline = () => new BaselineGroup(), fused = () => new FusedGroup();
        foreach (int count in new[] { 1, 8, 32, 128 })
        {
            Pair(results, "all_ready", count, ReadyChecks(baseline, count, -1), ReadyChecks(fused, count, -1));
            Pair(results, "all_pending_queued", count, QueuedChecks(baseline, count, pump), QueuedChecks(fused, count, pump));
        }
        foreach (int count in new[] { 8, 32, 128 })
        {
            Pair(results, "mixed_ready_first", count, ReadyChecks(baseline, count, 0), ReadyChecks(fused, count, 0));
            Pair(results, "mixed_ready_last", count, ReadyChecks(baseline, count, count - 1), ReadyChecks(fused, count, count - 1));
        }
        foreach (int count in new[] { 2, 8, 32, 128 })
            Pair(results, "completion_during_startup", count, StartupCompletion(baseline, count), StartupCompletion(fused, count));
        foreach (bool nested in new[] { false, true })
            Pair(results, nested ? "auto_nested" : "auto_flat", nested ? 32 : 8,
                Automatic(baseline, nested, pump), Automatic(fused, nested, pump));
        if (pump.ExternalPosts != 0) throw new Exception("Off-thread work contaminated the measurement.");
        Console.WriteLine(JsonSerializer.Serialize(new {
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), GCServer = System.Runtime.GCSettings.IsServerGC,
            BaselineMvid = typeof(B::CrowRx.Trigger.ConditionGroup).Assembly.ManifestModule.ModuleVersionId.ToString(),
            FusedMvid = typeof(F::CrowRx.Trigger.ConditionGroup).Assembly.ManifestModule.ModuleVersionId.ToString(),
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            Warmup = 2000, Batches = 7, AlternatingOrder = true,
            Counter = "GC.GetAllocatedBytesForCurrentThread", ExternalPosts = pump.ExternalPosts, Results = results
        }));
    }
}
