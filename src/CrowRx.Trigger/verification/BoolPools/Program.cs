using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using CrowRx.Pool.Collections;

static class Program
{
    static int sink;
    static object? allocationSink;
    static void ArrayWork(int n, bool access)
    {
        bool[] a = ArrayPool<bool>.Shared.Rent(n);
        try
        {
            Array.Clear(a, 0, n);
            if (a[0] || a[n - 1]) throw new Exception("Array initialization failed");
            int sum = 0;
            if (access)
            {
                for (int i = 0; i < n; ++i) a[i] = (i & 3) == 0;
                for (int i = 0; i < n; ++i) if (a[i]) ++sum;
            }
            a[0] = true; a[n - 1] = true;
            Volatile.Write(ref sink, sum);
        }
        finally { ArrayPool<bool>.Shared.Return(a); }
    }
    static void ListWork(int n, bool access)
    {
        using PooledList<bool> a = ListPool<bool>.Get(n);
        for (int i = 0; i < n; ++i) a.Add(false);
        if (a.Count != n || a[0] || a[n - 1]) throw new Exception("List initialization failed");
        int sum = 0;
        if (access)
        {
            for (int i = 0; i < n; ++i) a[i] = (i & 3) == 0;
            for (int i = 0; i < n; ++i) if (a[i]) ++sum;
        }
        a[0] = true; a[n - 1] = true;
        Volatile.Write(ref sink, sum);
    }
    static (double ns, double bytes) Batch(Action work, int iterations)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; ++i) work();
        long elapsed = Stopwatch.GetTimestamp() - start;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return ((double)elapsed * 1_000_000_000 / Stopwatch.Frequency / iterations, (double)bytes / iterations);
    }
    static object Result(string name, int n, int iterations, double[] ns, double[] bytes)
    {
        Array.Sort(ns); Array.Sort(bytes);
        return new { Name = name, Count = n, Iterations = iterations, MedianNs = ns[3], MinNs = ns[0], MaxNs = ns[6], BytesPerOp = bytes[3], MinBytesPerOp = bytes[0], MaxBytesPerOp = bytes[6] };
    }
    static void Main()
    {
        var results = new List<object>();
        foreach (bool allocate in new[] { false, true })
        {
            Action control = allocate ? (Action)(() => Volatile.Write(ref allocationSink, new byte[64])) : () => { };
            for (int i = 0; i < 2000; ++i) control();
            var ns = new double[7]; var bytes = new double[7];
            for (int b = 0; b < 7; ++b) { var r = Batch(control, 100000); ns[b] = r.ns; bytes[b] = r.bytes; }
            if (allocate ? bytes[0] != 88 : bytes[0] != 0) throw new Exception("Allocation control failed");
            results.Add(Result(allocate ? "control_allocate_64" : "control_no_op", 0, 100000, ns, bytes));
        }
        foreach (int n in new[] { 1, 8, 32, 128, 512, 4096 })
        foreach (bool access in new[] { false, true })
        {
            Action array = () => ArrayWork(n, access); Action list = () => ListWork(n, access);
            for (int i = 0; i < 2000; ++i) { array(); list(); }
            var an = new double[7]; var ab = new double[7]; var ln = new double[7]; var lb = new double[7];
            int iterations = n <= 512 ? 100000 : 10000;
            for (int b = 0; b < 7; ++b)
            {
                (double ns, double bytes) a, l;
                if ((b & 1) == 0) { a = Batch(array, iterations); l = Batch(list, iterations); }
                else { l = Batch(list, iterations); a = Batch(array, iterations); }
                an[b] = a.ns; ab[b] = a.bytes; ln[b] = l.ns; lb[b] = l.bytes;
                if (a.bytes != 0 || l.bytes != 0) throw new Exception("Unexpected warmed allocation");
            }
            string mode = access ? "initialize_read_write" : "initialize";
            results.Add(Result("array_" + mode, n, iterations, an, ab));
            results.Add(Result("list_" + mode, n, iterations, ln, lb));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), Warmup = 2000, Batches = 7, AlternatingOrder = true, Counter = "GC.GetAllocatedBytesForCurrentThread", PoolMvid = typeof(PooledList<bool>).Assembly.ManifestModule.ModuleVersionId.ToString(), Results = results }));
    }
}
