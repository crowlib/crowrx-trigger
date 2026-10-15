# CrowRx.Pool

A high-performance pooling library for CrowRx, designed for Unity.

## Features

- **Generic Collection Pooling**: Supports `List<T>`, `Dictionary<TKey, T>`, `HashSet<T>`, `Queue<T>`, and `Stack<T>`.
- **StringBuilder Pooling**: Efficient `StringBuilder` reuse to minimize GC allocations.
- **Disposable Pattern**: Simple resource management using `using` blocks for automatic pool return.
- **Unity Optimized**: Specifically designed for Unity 6.0+ environments.

## Usage

### Pooled Collections

All pooled collections implement `IDisposable` and inherit from their
`System.Collections.Generic` counterparts. Dispose clears and returns them to the pool;
returned references must not be used, including after the object is rented again.

Queue lifetime, capacity, allocation contracts and migration instructions: [QueuePool](QUEUE_POOL.ko.md).

```csharp
using CrowRx.Pool.Collections;

// Using PooledList
using (var list = ListPool<int>.Get())
{
    list.Add(1);
    list.Add(2);
    // Use the list...
} // Automatically cleared and returned to pool here

// Using PooledDictionary
using (var dict = DictionaryPool<string, int>.Get())
{
    dict["key"] = 100;
}

// Using PooledHashSet
using (var set = HashSetPool<float>.Get())
{
    set.Add(1.5f);
}

// Using PooledQueue
using (var queue = QueuePool<string>.Get())
{
    queue.Enqueue("item");
}

// Using PooledStack: input order is pushed, so Pop returns 3 first.
using (var stack = StackPool<int>.Get(new[] { 1, 2, 3 }))
{
    int top = stack.Pop(); // 3
}
```

### StringBuilder Pool

Reduces memory pressure when performing frequent string operations.
Builders are created on demand. Use an expected final length to prepare reusable buffers;
`ToString()` still allocates the resulting non-empty string, and clearing a builder with
multiple chunks can allocate an internal buffer. Returned builder references must not be used.

```csharp
using CrowRx.Pool.Text;

StringBuilderPool.Warmup(inactiveCount: 4, capacity: 256);
using (var pooled = StringBuilderPool.Get(capacity: 256))
{
    var sb = pooled.StringBuilder;
    sb.Append("Hello ");
    sb.Append("World!");

    string result = sb.ToString();
    UnityEngine.Debug.Log(result);
} // StringBuilder is cleared and returned to pool
```

## Requirements

- Unity 6.0 or newer
- .NET Standard 2.1 compatible editor

## Installation

Install via NuGet (NuGetForUnity supported).

## Notes

This package is intended for use in Unity Editor only.