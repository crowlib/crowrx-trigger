using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrowRx.Trigger;

// No instance fields: candidate groups share the production graph validator and object layout.
sealed class CapacityGroup : ConditionGroup
{
    public CapacityGroup(List<ICondition> children, ConditionCombineType combineType = ConditionCombineType.And)
    {
        Conditions = children;
        CombineType = combineType;
    }
    public void Validate() => ValidateConditionGraph();
}
sealed class CapacityLeaf : Condition
{
    public int Initializations;
    public override bool IsValid => true;
    public override bool IsSatisfied => true;
    public override Task CheckAsync(CancellationToken token) => Task.CompletedTask;
    protected override void OnInitialize() => ++Initializations;
}
static class CapacityBenchmark
{
    static readonly string[] variants = { "growth_model", "array_length", "nonnull_count", "production" };
    static object? sink;
    static int LiveCount(ICondition[]? input)
    {
        int count = 0;
        if (input is not null)
            foreach (ICondition child in input) if (child is not null) ++count;
        return count;
    }
    static ConditionGroup Model(ICondition[]? input, int variant, ConditionCombineType combineType = ConditionCombineType.And)
    {
        if (combineType != ConditionCombineType.And && combineType != ConditionCombineType.Or)
            throw new ArgumentOutOfRangeException(nameof(combineType), combineType, "Unsupported condition combination type.");
        int capacity = variant == 1 ? input?.Length ?? 0 : variant == 2 ? LiveCount(input) : 0;
        var children = new List<ICondition>(capacity);
        if (input is not null)
            foreach (ICondition child in input) if (child is not null) children.Add(child);
        var group = new CapacityGroup(children, combineType);
        group.Validate();
        return group;
    }
    static ConditionGroup Create(ICondition[]? input, int variant, ConditionCombineType combineType = ConditionCombineType.And)
        => variant == 3 ? ConditionGroup.Create(input, combineType) : Model(input, variant, combineType);
    static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    static void Reject<T>(Action work) where T : Exception
    {
        try { work(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }
    static ICondition[] Input(int length, int nullPercent)
    {
        var input = new ICondition[length];
        for (int i = 0; i < length; ++i)
            input[i] = (i + 1) * nullPercent / 100 != i * nullPercent / 100 ? null! : new BenchLeaf();
        return input;
    }
    static int Verify()
    {
        int scenarios = 0;
        foreach (int length in new[] { 0, 1, 8, 32, 128, 1024 })
        foreach (int nullPercent in new[] { 0, 50, 90, 100 })
        {
            var input = Input(length, nullPercent); int expected = LiveCount(input);
            for (int v = 0; v < variants.Length; ++v)
            {
                ConditionGroup group = Create(input, v); List<ICondition> list = group.Conditions!;
                Require(!group.IsInitialized && !group.IsActivated && list.Count == expected, "Candidate changed creation state or null filtering.");
                int index = 0;
                foreach (ICondition child in input)
                    if (child is not null) Require(ReferenceEquals(list[index++], child), "Candidate changed references or order.");
                Require(v != 1 || list.Capacity == length, "Array-length candidate did not reserve the input length.");
                Require(v != 2 && v != 3 || list.Capacity == expected, "Count policy did not reserve the live count.");
                list.Clear(); Require(LiveCount(input) == expected, "Candidate shared ownership with its input.");
            }
            ++scenarios;
        }
        foreach (int v in new[] { 0, 1, 2, 3 })
        {
            foreach (ConditionCombineType mode in new[] { ConditionCombineType.And, ConditionCombineType.Or })
            {
                ConditionGroup group = Create(null, v, mode);
                Require(group.Conditions!.Count == 0 && group.CombineType == mode && !group.IsValid && group.IsSatisfied == (mode == ConditionCombineType.And),
                    "Candidate changed empty input or combination semantics.");
            }
            Reject<ArgumentOutOfRangeException>(() => Create(null, v, (ConditionCombineType)99));
            var first = new CapacityLeaf(); var last = new CapacityLeaf(); ICondition[] input = { first, null!, last };
            ConditionGroup result = Create(input, v); input[0] = last;
            Require(ReferenceEquals(result.Conditions![0], first) && first.Initializations == 0 && last.Initializations == 0,
                "Candidate retained its input or initialized a child during creation.");
            result.Initialize(); Require(first.Initializations == 1 && last.Initializations == 1, "Candidate changed deferred initialization.");
            var active = new CapacityLeaf(); active.Initialize(); active.Activate();
            ConditionGroup withActive = Create(new ICondition[] { active }, v);
            Require(active.IsInitialized && active.IsActivated && active.Initializations == 1 && !withActive.IsActivated,
                "Candidate changed an existing child's lifecycle.");
            Reject<InvalidOperationException>(() => Create(new ICondition[] { first, first }, v));
            var left = ConditionGroup.Create(new ICondition[] { first }); var right = ConditionGroup.Create(new ICondition[] { first });
            Reject<InvalidOperationException>(() => Create(new ICondition[] { left, right }, v));
            var cyclic = new CapacityGroup(new List<ICondition>()); cyclic.Conditions!.Add(cyclic);
            Reject<InvalidOperationException>(() => Create(new ICondition[] { cyclic }, v));
            var invalid = new CapacityGroup(new List<ICondition> { null! });
            Reject<InvalidOperationException>(() => Create(new ICondition[] { invalid }, v));
            Require(left.Conditions!.Count == 1 && right.Conditions!.Count == 1 && cyclic.Conditions!.Count == 1,
                "Candidate changed nested inputs during failed validation.");
        }
        return scenarios + 8;
    }
    public static void VerifyAndReport() => Console.WriteLine(Verify() + " capacity scenarios passed for all four variants; desktop .NET only.");
    static (double Ns, double Bytes) Batch(Action work, int iterations)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long bytes = GC.GetAllocatedBytesForCurrentThread(), tick = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; ++i) work();
        return ((double)(Stopwatch.GetTimestamp() - tick) * 1e9 / Stopwatch.Frequency / iterations,
            (double)(GC.GetAllocatedBytesForCurrentThread() - bytes) / iterations);
    }
    static void Compare(List<object> results, string name, int length, int nullPercent, ICondition[]? input, bool allocate = false)
    {
        int live = LiveCount(input), iterations = length == 0 ? 100000 : Math.Max(512, Math.Min(16384, 524288 / Math.Max(1, length)));
        var work = new Action[variants.Length]; var capacities = new int[variants.Length];
        for (int v = 0; v < variants.Length; ++v)
        {
            int captured = v;
            work[v] = name == "control_no_op" ? () => { } : allocate ? () => Volatile.Write(ref sink, new byte[64])
                : () => Volatile.Write(ref sink, Create(input, captured));
            if (input is not null) capacities[v] = Create(input, v).Conditions!.Capacity;
            for (int i = 0; i < 128; ++i) work[v]();
        }
        var ns = new double[variants.Length][]; var bytes = new double[variants.Length][];
        for (int v = 0; v < variants.Length; ++v) { ns[v] = new double[8]; bytes[v] = new double[8]; }
        for (int batch = 0; batch < 8; ++batch)
        for (int position = 0; position < variants.Length; ++position)
        {
            int v = (batch + position) % variants.Length;
            var measured = Batch(work[v], iterations); ns[v][batch] = measured.Ns; bytes[v][batch] = measured.Bytes;
        }
        Require(capacities[2] == capacities[3], "Count model capacity differs from the production factory.");
        foreach (double modelBytes in bytes[2])
            foreach (double productionBytes in bytes[3])
                Require(modelBytes == productionBytes, "Count model allocation differs from the production factory.");
        for (int v = 0; v < variants.Length; ++v)
        {
            Array.Sort(ns[v]); Array.Sort(bytes[v]); double allocated = (bytes[v][3] + bytes[v][4]) / 2;
            Require(name != "control_no_op" || allocated == 0, "Zero allocation control failed.");
            Require(!allocate || allocated > 0, "Positive allocation control failed.");
            results.Add(new { Variant = variants[v], Name = name, InputLength = length, RequestedNullPercent = nullPercent,
                NullCount = length - live, LiveCount = live, Capacity = capacities[v], Iterations = iterations,
                MedianNs = (ns[v][3] + ns[v][4]) / 2, MinNs = ns[v][0], MaxNs = ns[v][7], BytesPerOp = allocated,
                MinBytesPerOp = bytes[v][0], MaxBytesPerOp = bytes[v][7] });
        }
    }
    public static void Run()
    {
        int validated = Verify();
        var results = new List<object>();
        Compare(results, "control_no_op", 0, 0, null);
        Compare(results, "control_allocate_64", 0, 0, null, true);
        foreach (int length in new[] { 1, 8, 32, 128, 1024 })
        foreach (int nullPercent in new[] { 0, 50, 90 })
            Compare(results, "create_prebuilt", length, nullPercent, Input(length, nullPercent));
        Console.WriteLine(JsonSerializer.Serialize(new {
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), GCServer = System.Runtime.GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            LibraryMvid = typeof(ConditionGroup).Assembly.ManifestModule.ModuleVersionId.ToString(), Counter = "GC.GetAllocatedBytesForCurrentThread",
            Batches = 8, RotatingOrder = true, Warmup = 128, ValidatedScenariosPerVariant = validated,
            ProductionPolicy = "nonnull_count",
            Notes = "Prebuilt flat arrays and children are outside measurement. Three array-only collection policies use a fieldless derived group and the production graph validator. growth_model retains the pre-capacity policy; production now reserves the nonnull count. CPU includes policy selection and helper calls in the models. Null count is floor(length * requested percent / 100).",
            Results = results
        }));
    }
}
