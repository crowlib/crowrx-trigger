using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using CrowRx.Trigger;

sealed class ArrayEnumerable : IEnumerable<ICondition>
{
    readonly ICondition[] items;
    public ArrayEnumerable(ICondition[] items) => this.items = items;
    public IEnumerator<ICondition> GetEnumerator() => ((IEnumerable<ICondition>)items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
static class EnumerationBenchmark
{
    static object? sink;
    static Action Add(ICondition[] items, bool wrapped)
    {
        var group = new ConditionGroup();
        IEnumerable<ICondition> source = wrapped ? new ArrayEnumerable(items) : items;
        return () => { group.Conditions?.Clear(); group.AddConditions(source); };
    }
    static Action Create(ICondition[] items, bool wrapped)
    {
        IEnumerable<ICondition> source = wrapped ? new ArrayEnumerable(items) : items;
        return () => Volatile.Write(ref sink, ConditionGroup.Create(source));
    }
    static (double Ns, double Bytes) Batch(Action work)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long bytes = GC.GetAllocatedBytesForCurrentThread(), tick = Stopwatch.GetTimestamp();
        for (int i = 0; i < 4000; ++i) work();
        long elapsed = Stopwatch.GetTimestamp() - tick, allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        return ((double)elapsed * 1e9 / Stopwatch.Frequency / 4000, (double)allocated / 4000);
    }
    static object Result(string variant, string name, int count, double[] ns, double[] bytes)
    {
        Array.Sort(ns); Array.Sort(bytes);
        return new { Variant = variant, Name = name, Count = count, Iterations = 4000,
            MedianNs = ns[3], MinNs = ns[0], MaxNs = ns[6], BytesPerOp = bytes[3], MinBytesPerOp = bytes[0], MaxBytesPerOp = bytes[6] };
    }
    static void Pair(List<object> results, string name, int count, Action direct, Action wrapped)
    {
        for (int i = 0; i < 128; ++i) { direct(); wrapped(); }
        var dn = new double[7]; var db = new double[7]; var wn = new double[7]; var wb = new double[7];
        for (int b = 0; b < 7; ++b)
        {
            (double Ns, double Bytes) d, w;
            if ((b & 1) == 0) { d = Batch(direct); w = Batch(wrapped); }
            else { w = Batch(wrapped); d = Batch(direct); }
            dn[b] = d.Ns; db[b] = d.Bytes; wn[b] = w.Ns; wb[b] = w.Bytes;
        }
        results.Add(Result("array_direct", name, count, dn, db));
        results.Add(Result("array_as_enumerable", name, count, wn, wb));
    }
    public static void Run()
    {
        var results = new List<object>();
        Pair(results, "control_no_op", 0, () => { }, () => { });
        Pair(results, "control_allocate_64", 0, () => Volatile.Write(ref sink, new byte[64]), () => Volatile.Write(ref sink, new byte[64]));
        foreach (int count in new[] { 1, 8, 32, 128 })
        {
            var items = new ICondition[count]; for (int i = 0; i < count; ++i) items[i] = new BenchLeaf();
            Pair(results, "add_reused", count, Add(items, false), Add(items, true));
            Pair(results, "create_prebuilt", count, Create(items, false), Create(items, true));
        }
        Console.WriteLine(JsonSerializer.Serialize(new {
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            LibraryMvid = typeof(ConditionGroup).Assembly.ManifestModule.ModuleVersionId.ToString(), Counter = "GC.GetAllocatedBytesForCurrentThread",
            Warmup = 128, Batches = 7, AlternatingOrder = true,
            Notes = "Both inputs expose the same prebuilt array. A wrapper forces the unchanged IEnumerable path and forwards its array enumerator. It is allocated outside measurement. The current Create array path reserves the nonnull count; allocation differences include list growth as well as array enumeration. CPU includes the forwarding call.",
            Results = results
        }));
    }
}
