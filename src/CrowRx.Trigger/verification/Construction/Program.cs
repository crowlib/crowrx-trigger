using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrowRx.Trigger;

sealed class Leaf : Condition
{
    public override bool IsValid => true;
    public override bool IsSatisfied => true;
    public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
}
sealed class Probe : ConditionGroup
{
    public void ValidateOnly() => ValidateConditionGraph();
}
sealed class Work
{
    public string Name = "";
    public int Count;
    public bool Nested, ParentInitialized, IncludesChildCreation;
    public Action Run = () => { };
}
sealed class Result
{
    public string Name { get; set; } = "";
    public int DirectChildren { get; set; }
    public int LeafCount { get; set; }
    public int TotalNodes { get; set; }
    public bool ParentInitialized { get; set; }
    public bool IncludesChildCreation { get; set; }
    public int Warmup { get; set; }
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
    static object? sink;

    static ICondition[] Children(int count, bool nested)
    {
        var children = new ICondition[count];
        for (int i = 0; i < count; ++i)
        {
            if (!nested) { children[i] = new Leaf(); continue; }
            var group = new ConditionGroup();
            group.AddConditions(new ICondition[] { new Leaf(), new Leaf(), new Leaf(), new Leaf() });
            children[i] = group;
        }
        return children;
    }
    static Work Reuse(int count, bool nested, bool initialized, bool single = false)
    {
        var group = new ConditionGroup(); ICondition[] children = Children(count, nested);
        if (initialized) group.Initialize();
        return new Work {
            Name = "reuse_" + (nested ? "nested" : "flat") + (single ? "_single_add" : "_bulk_add") + (initialized ? "_initialized" : "_uninitialized"),
            Count = count, Nested = nested, ParentInitialized = initialized,
            Run = () => {
                group.Conditions?.Clear();
                if (single) { foreach (ICondition child in children) group.AddCondition(child); }
                else group.AddConditions(children);
                if (group.Conditions?.Count != count) throw new Exception("Reused construction lost children.");
            }
        };
    }
    static Work Fresh(int count, bool nested, bool initialized)
    {
        return new Work {
            Name = "fresh_" + (nested ? "nested" : "flat") + (initialized ? "_initialized" : "_uninitialized"),
            Count = count, Nested = nested, ParentInitialized = initialized, IncludesChildCreation = true,
            Run = () => {
                var group = new ConditionGroup(); ICondition[] children = Children(count, nested);
                if (initialized) group.Initialize();
                group.AddConditions(children);
                if (group.Conditions?.Count != count) throw new Exception("Fresh construction lost children.");
                Volatile.Write(ref sink, group);
            }
        };
    }
    static Work Validate(int count, bool nested)
    {
        var group = new Probe(); group.AddConditions(Children(count, nested));
        return new Work { Name = "validate_" + (nested ? "nested" : "flat"), Count = count, Nested = nested, Run = group.ValidateOnly };
    }
    static Result Measure(Work work)
    {
        bool control = work.Count == 0, validation = work.Name.StartsWith("validate_");
        long size = (long)Math.Max(1, work.Count) * (work.Nested ? 5 : 1);
        long cost = validation ? size : size * Math.Max(1, work.Count);
        int iterations = control ? 100000 : (int)Math.Max(3, Math.Min(16384, 524288 / cost));
        int warmup = control ? 2000 : (int)Math.Max(2, Math.Min(128, 32768 / cost));
        for (int i = 0; i < warmup; ++i) work.Run();
        var ns = new double[7]; var bytes = new double[7]; int gen0 = 0;
        for (int batch = 0; batch < 7; ++batch)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int before = GC.CollectionCount(0);
            long allocated = GC.GetAllocatedBytesForCurrentThread(), tick = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; ++i) work.Run();
            long elapsed = Stopwatch.GetTimestamp() - tick, delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
            gen0 += GC.CollectionCount(0) - before;
            ns[batch] = (double)elapsed * 1e9 / Stopwatch.Frequency / iterations;
            bytes[batch] = (double)delta / iterations;
        }
        Array.Sort(ns); Array.Sort(bytes);
        return new Result {
            Name = work.Name, DirectChildren = work.Count,
            LeafCount = work.Count * (work.Nested ? 4 : 1), TotalNodes = control ? 0 : 1 + work.Count * (work.Nested ? 5 : 1),
            ParentInitialized = work.ParentInitialized, IncludesChildCreation = work.IncludesChildCreation,
            Warmup = warmup, Iterations = iterations,
            MedianNs = ns[3], MinNs = ns[0], MaxNs = ns[6], BytesPerOp = bytes[3], MinBytesPerOp = bytes[0], MaxBytesPerOp = bytes[6], Gen0Collections = gen0
        };
    }
    static void Main()
    {
        var results = new List<Result>();
        results.Add(Measure(new Work { Name = "control_no_op" }));
        results.Add(Measure(new Work { Name = "control_allocate_64", Run = () => Volatile.Write(ref sink, new byte[64]) }));
        foreach (bool nested in new[] { false, true })
        foreach (int count in nested ? new[] { 1, 8, 32, 128, 512 } : new[] { 1, 8, 32, 128, 512, 1024 })
        {
            foreach (bool initialized in new[] { false, true })
            {
                results.Add(Measure(Reuse(count, nested, initialized)));
                results.Add(Measure(Fresh(count, nested, initialized)));
            }
            results.Add(Measure(Validate(count, nested)));
        }
        foreach (int count in new[] { 8, 128, 1024 })
        foreach (bool initialized in new[] { false, true })
            results.Add(Measure(Reuse(count, false, initialized, true)));
        if (results[0].BytesPerOp != 0 || results[1].BytesPerOp <= 0)
            throw new Exception("Allocation controls failed.");
        Console.WriteLine(JsonSerializer.Serialize(new {
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), GCServer = System.Runtime.GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            LibraryMvid = typeof(ConditionGroup).Assembly.ManifestModule.ModuleVersionId.ToString(),
            Counter = "GC.GetAllocatedBytesForCurrentThread", Batches = 7,
            Input = "ICondition[]", NestedLeavesPerChildGroup = 4,
            Notes = "Reuse clears a warmed child list and reuses child instances; initialized reuse children are already initialized. Fresh includes child arrays, child groups, leaves, root construction and initialization. Validate only is a reference, not an equivalent bulk API.",
            Results = results
        }));
    }
}
