# CrowRx.Trigger

A Trigger utility system library for CrowRx.

## Requirements
- Unity 6.0 or newer
- .NET Standard 2.1 compatible editor

## Installation
Install via NuGet (NuGetForUnity supported).

## Notes
This package is intended for use in Unity Editor only.

## Creating a group from a collection

Use `ConditionGroup.Create` to construct a new base group from a collection. It copies non-null child references in order, validates the complete graph, and returns an uninitialized, inactive group. Duplicate references, cycles, and null entries inside nested groups are rejected. Null or empty input produces an empty group with `IsValid == false`.

```csharp
ConditionGroup group = ConditionGroup.Create(conditions, ConditionCombineType.And);
group.Initialize();
```

Initialization can also be performed by the existing `Listener` initialization path. Creation does not initialize children or invoke per-child addition hooks. If enumeration or validation fails, no group is returned; side effects performed by the input enumerator remain caller-owned.

Use the existing `AddCondition` and `AddConditions` methods to add to an existing group or to configure a derived root such as `ConditionGroupShareTarget<TTarget>`. Those methods retain their hooks, immediate initialization for an initialized parent, and partial-success behavior. The factory always returns a base `ConditionGroup` and copies the input collection while sharing the child instances.

## Stopping direct group checks

When calling `ConditionGroup.CheckAsync` directly, use `await group.StopChecksAsync()` to cancel and drain retained child and nested group checks, even if the group was never activated. Cleanup failures are reported through the returned task. This method preserves activation state; call `Deactivate()` after cleanup if the group was activated.

```csharp
try
{
    await group.CheckAsync(cancellationToken);
}
finally
{
    await group.StopChecksAsync();
}
```

Call check, stop, and condition mutation methods from the same execution context. `AddCondition` rejects recursive additions to the same group and rechecks its state and graph after configuration and initialization callbacks. If a callback activates the parent or starts its checks, the outer addition fails without appending the candidate. Callback side effects are owned by the caller.
