using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrowRx.Trigger;

sealed class BenchLeaf : Condition
{
    public override bool IsValid => true;
    public override bool IsSatisfied => true;
    public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
}
sealed class CreationResult
{
    public string Variant { get; set; } = "";
    public string Name { get; set; } = "";
    public int DirectChildren { get; set; }
    public int TotalNodes { get; set; }
    public bool InitializedResult { get; set; }
    public int Iterations { get; set; }
    public int Warmup { get; set; }
    public double MedianNs { get; set; }
    public double MinNs { get; set; }
    public double MaxNs { get; set; }
    public double BytesPerOp { get; set; }
    public double MinBytesPerOp { get; set; }
    public double MaxBytesPerOp { get; set; }
    public int Gen0Collections { get; set; }
}
static class Benchmark
{
    static object? sink;
    static ICondition[] Children(int count, bool nested)
    {
        var children = new ICondition[count];
        for (int i = 0; i < count; ++i)
        {
            if (!nested) { children[i] = new BenchLeaf(); continue; }
            var child = new ConditionGroup();
            child.AddConditions(new ICondition[] { new BenchLeaf(), new BenchLeaf(), new BenchLeaf(), new BenchLeaf() });
            children[i] = child;
        }
        return children;
    }
    static Action Work(int count, bool nested, bool initialize, bool create)
    {
        return () => {
            ICondition[] children = Children(count, nested); ConditionGroup group;
            if (create)
            {
                group = ConditionGroup.Create(children);
                if (initialize) group.Initialize();
            }
            else
            {
                group = new ConditionGroup();
                if (initialize) group.Initialize();
                group.AddConditions(children);
            }
            if (group.Conditions?.Count != count || group.IsInitialized != initialize)
                throw new Exception("Construction result did not match the requested lifecycle.");
            Volatile.Write(ref sink, group);
        };
    }
    static (double Ns, double Bytes, int Gen0) Batch(Action work, int iterations)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int collections = GC.CollectionCount(0);
        long allocated = GC.GetAllocatedBytesForCurrentThread(), tick = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; ++i) work();
        long elapsed = Stopwatch.GetTimestamp() - tick, bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return ((double)elapsed * 1e9 / Stopwatch.Frequency / iterations, (double)bytes / iterations, GC.CollectionCount(0) - collections);
    }
    static CreationResult Summarize(string variant, string name, int count, bool nested, bool initialized,
        int iterations, int warmup, double[] ns, double[] bytes, int gen0)
    {
        Array.Sort(ns); Array.Sort(bytes);
        return new CreationResult {
            Variant = variant, Name = name, DirectChildren = count, TotalNodes = count == 0 ? 0 : 1 + count * (nested ? 5 : 1),
            InitializedResult = initialized, Iterations = iterations, Warmup = warmup,
            MedianNs = ns[3], MinNs = ns[0], MaxNs = ns[6], BytesPerOp = bytes[3], MinBytesPerOp = bytes[0], MaxBytesPerOp = bytes[6], Gen0Collections = gen0
        };
    }
    static void Pair(List<CreationResult> results, string name, int count, bool nested, bool initialize, Action legacy, Action create)
    {
        long size = (long)Math.Max(1, count) * (nested ? 5 : 1), legacyCost = size * Math.Max(1, count);
        int li = count == 0 ? 100000 : (int)Math.Max(3, Math.Min(16384, 524288 / legacyCost));
        int ci = count == 0 ? 100000 : (int)Math.Max(32, Math.Min(16384, 524288 / size));
        int lw = count == 0 ? 2000 : (int)Math.Max(2, Math.Min(128, 32768 / legacyCost));
        int cw = count == 0 ? 2000 : (int)Math.Max(2, Math.Min(128, 32768 / size));
        for (int i = 0; i < lw; ++i) legacy();
        for (int i = 0; i < cw; ++i) create();
        var ln = new double[7]; var lb = new double[7]; var cn = new double[7]; var cb = new double[7]; int lg = 0, cg = 0;
        for (int batch = 0; batch < 7; ++batch)
        {
            (double Ns, double Bytes, int Gen0) l, c;
            if ((batch & 1) == 0) { l = Batch(legacy, li); c = Batch(create, ci); }
            else { c = Batch(create, ci); l = Batch(legacy, li); }
            ln[batch] = l.Ns; lb[batch] = l.Bytes; lg += l.Gen0;
            cn[batch] = c.Ns; cb[batch] = c.Bytes; cg += c.Gen0;
        }
        results.Add(Summarize("legacy", name, count, nested, initialize, li, lw, ln, lb, lg));
        results.Add(Summarize("create", name, count, nested, initialize, ci, cw, cn, cb, cg));
    }
    public static void Run()
    {
        var results = new List<CreationResult>();
        Pair(results, "control_no_op", 0, false, false, () => { }, () => { });
        Pair(results, "control_allocate_64", 0, false, false,
            () => Volatile.Write(ref sink, new byte[64]), () => Volatile.Write(ref sink, new byte[64]));
        foreach (bool nested in new[] { false, true })
        foreach (int count in nested ? new[] { 1, 8, 32, 128, 512 } : new[] { 1, 8, 32, 128, 512, 1024 })
        foreach (bool initialized in new[] { false, true })
            Pair(results, (nested ? "nested" : "flat") + (initialized ? "_initialized" : "_uninitialized"), count, nested, initialized,
                Work(count, nested, initialized, false), Work(count, nested, initialized, true));
        foreach (CreationResult result in results)
        {
            if (result.Name == "control_no_op" && result.BytesPerOp != 0 || result.Name == "control_allocate_64" && result.BytesPerOp <= 0)
                throw new Exception("Allocation controls failed.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new {
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), GCServer = System.Runtime.GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            LibraryMvid = typeof(ConditionGroup).Assembly.ManifestModule.ModuleVersionId.ToString(),
            Batches = 7, AlternatingOrder = true, Counter = "GC.GetAllocatedBytesForCurrentThread",
            Notes = "Both variants create arrays, children and a new root. Initialized legacy initializes an empty parent before addition; initialized factory initializes the populated result afterward. Nested child groups use legacy additions in both variants. Variant-specific iteration counts are recorded; factory batches can include more naturally occurring Gen0 collections.",
            Results = results
        }));
    }
}
