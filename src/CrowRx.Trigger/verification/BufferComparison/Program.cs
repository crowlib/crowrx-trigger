extern alias A;
extern alias L;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AG = A::CrowRx.Trigger.ConditionGroup;
using LG = L::CrowRx.Trigger.ConditionGroup;
using AL = A::CrowRx.Trigger.Listener;
using LL = L::CrowRx.Trigger.Listener;

sealed class FA : A::CrowRx.Trigger.Condition
{
    public override bool IsValid => true;
    public override bool IsSatisfied => true;
    public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
}
sealed class FL : L::CrowRx.Trigger.Condition
{
    public override bool IsValid => true;
    public override bool IsSatisfied => true;
    public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
}
sealed class Pump : SynchronizationContext
{
    readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new Queue<(SendOrPostCallback, object?)>(64);
    readonly int thread = Environment.CurrentManagedThreadId;
    public int ExternalPosts;
    public override void Post(SendOrPostCallback cb, object? state) { if (thread != Environment.CurrentManagedThreadId) ++ExternalPosts; queue.Enqueue((cb, state)); }
    public bool Step() { if (queue.Count == 0) return false; var item = queue.Dequeue(); item.Callback(item.State); return true; }
    public void Drain() { int n = 0; while (Step()) if (++n > 1024) throw new Exception("Pump did not stop"); }
}
sealed class Work
{
    public Action Run = () => { }, Start = () => { }, Finish = () => { };
}
static class Program
{
    static object? sink;
    static AG ArrayGroup(int n, bool nested = false)
    {
        var g = new AG();
        for (int i = 0; i < (nested ? 8 : n); ++i) g.AddCondition(nested ? (A::CrowRx.Trigger.ICondition)ArrayGroup(4) : new FA());
        g.Initialize(); return g;
    }
    static LG ListGroup(int n, bool nested = false)
    {
        var g = new LG();
        for (int i = 0; i < (nested ? 8 : n); ++i) g.AddCondition(nested ? (L::CrowRx.Trigger.ICondition)ListGroup(4) : new FL());
        g.Initialize(); return g;
    }
    static (double ns, double bytes) Batch(Work work, int iterations)
    {
        work.Start(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long bytes = GC.GetAllocatedBytesForCurrentThread(); long tick = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; ++i) work.Run();
        long elapsed = Stopwatch.GetTimestamp() - tick; long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        work.Finish(); return ((double)elapsed * 1e9 / Stopwatch.Frequency / iterations, (double)allocated / iterations);
    }
    static object Summarize(string variant, string name, int count, int iterations, double[] ns, double[] bytes)
    {
        Array.Sort(ns); Array.Sort(bytes);
        return new { Variant = variant, Name = name, Count = count, Iterations = iterations, MedianNs = ns[3], MinNs = ns[0], MaxNs = ns[6], BytesPerOp = bytes[3], MinBytesPerOp = bytes[0], MaxBytesPerOp = bytes[6] };
    }
    static void Pair(List<object> results, string name, int n, Work array, Work list)
    {
        foreach (Work w in new[] { array, list }) { w.Start(); for (int i = 0; i < 2000; ++i) w.Run(); w.Finish(); }
        var an = new double[7]; var ab = new double[7]; var ln = new double[7]; var lb = new double[7];
        for (int b = 0; b < 7; ++b)
        {
            (double ns, double bytes) a, l;
            if ((b & 1) == 0) { a = Batch(array, 4000); l = Batch(list, 4000); }
            else { l = Batch(list, 4000); a = Batch(array, 4000); }
            an[b] = a.ns; ab[b] = a.bytes; ln[b] = l.ns; lb[b] = l.bytes;
            if (a.bytes != l.bytes) throw new Exception("Variant allocation differs");
        }
        results.Add(Summarize("array", name, n, 4000, an, ab)); results.Add(Summarize("list", name, n, 4000, ln, lb));
    }
    static void Main()
    {
        var pump = new Pump(); SetContext(pump); var results = new List<object>();
        Pair(results, "control_no_op", 0, new Work(), new Work());
        Pair(results, "control_allocate_64", 0, new Work { Run = () => Volatile.Write(ref sink, new byte[64]) }, new Work { Run = () => Volatile.Write(ref sink, new byte[64]) });
        foreach (int n in new[] { 1, 8, 32, 128 })
        {
            AG a = ArrayGroup(n); LG l = ListGroup(n);
            Pair(results, "group_cycle", n,
                new Work { Run = () => { a.Activate(); a.CheckAsync(CancellationToken.None).GetAwaiter().GetResult(); a.Deactivate(); } },
                new Work { Run = () => { l.Activate(); l.CheckAsync(CancellationToken.None).GetAwaiter().GetResult(); l.Deactivate(); } });
            Pair(results, "ready_check", n,
                new Work { Start = () => { a.Activate(); a.CheckAsync(CancellationToken.None).GetAwaiter().GetResult(); }, Run = () => a.CheckAsync(CancellationToken.None).GetAwaiter().GetResult(), Finish = () => { a.StopChecksAsync().GetAwaiter().GetResult(); a.Deactivate(); } },
                new Work { Start = () => { l.Activate(); l.CheckAsync(CancellationToken.None).GetAwaiter().GetResult(); }, Run = () => l.CheckAsync(CancellationToken.None).GetAwaiter().GetResult(), Finish = () => { l.StopChecksAsync().GetAwaiter().GetResult(); l.Deactivate(); } });
        }
        foreach (bool nested in new[] { false, true })
        {
            var a = new AL(ArrayGroup(nested ? 32 : 8, nested)); var l = new LL(ListGroup(nested ? 32 : 8, nested));
            a.Initialize(); l.Initialize(); int ac = 0, lc = 0; a.Success += () => ++ac; l.Success += () => ++lc;
            Pair(results, nested ? "auto_nested" : "auto_flat", nested ? 32 : 8,
                new Work { Start = a.StartListen, Run = () => { int target = ac + 1; while (ac < target) if (!pump.Step()) throw new Exception("Array listener stalled"); }, Finish = () => { Task stop = a.StopListenAsync(); pump.Drain(); stop.GetAwaiter().GetResult(); } },
                new Work { Start = l.StartListen, Run = () => { int target = lc + 1; while (lc < target) if (!pump.Step()) throw new Exception("List listener stalled"); }, Finish = () => { Task stop = l.StopListenAsync(); pump.Drain(); stop.GetAwaiter().GetResult(); } });
        }
        if (pump.ExternalPosts != 0) throw new Exception("Off-thread work contaminated measurement");
        Console.WriteLine(JsonSerializer.Serialize(new { Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), ArrayMvid = typeof(AG).Assembly.ManifestModule.ModuleVersionId.ToString(), ListMvid = typeof(LG).Assembly.ManifestModule.ModuleVersionId.ToString(), Warmup = 2000, Batches = 7, AlternatingOrder = true, ExternalPosts = pump.ExternalPosts, TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), Results = results }));
    }
    static void SetContext(Pump pump) => SynchronizationContext.SetSynchronizationContext(pump);
}
