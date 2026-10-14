# CrowRx.Trigger

A Trigger utility system library for CrowRx.

## Requirements
- Unity 6.0 or newer
- .NET Standard 2.1 compatible editor

## Installation
Install via NuGet (NuGetForUnity supported).

## Notes
This package is intended for use in Unity Editor only.

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
