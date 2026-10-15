# CrowRx.Core

The foundational core library for the CrowRx ecosystem, providing high-performance utilities, extension methods, and a flexible type conversion system.

## API Reference

### 1. Logging System (`Log` Static Class)
A conditional logging system that supports custom logger injection and build-specific log filtering.

*   **`Log.SetLogger(ILogger? logger)`**: Injects a custom implementation of `ILogger`.
*   **`Log.Info`, `Warning`, `Error`, `Assertion`, `Exception`**: Logs messages based on defined compilation symbols.

**Example:**
```csharp
// Set a custom logger (e.g., Unity Debug)
Log.SetLogger(new UnityLogger()); 

Log.Info("System initialized.");
Log.Error("Failed to load data.");
```

**Compilation Symbols:**
`CROWRX_LOG_INFO`, `CROWRX_LOG_WARNING`, `CROWRX_LOG_ERROR`, `CROWRX_LOG_ASSERT`, `CROWRX_LOG_EXCEPTION`, `CROWRX_LOG_ALL`, `UNITY_EDITOR`

---

### 2. Enum Extensions (`EnumValueExtension` and `EnumAttributeExtension` Static Classes)
Utilities for extracting the exact underlying enum value, comparing enum values, and caching member attributes.

*   **`ToValue<TEnum, TValue>()`**: Returns the underlying value without boxing. `TValue` must match the enum's actual underlying type; a mismatch throws an exception.
*   **`TryToValue<TEnum, TValue>(out value)`**: Returns whether the underlying type matches and supplies the value on success.
*   **`EqualsValue(enum2)`**: Compares enum numeric values while preserving signedness and the full integer range.
*   **`GetAttributeOfType<TEnum, TAttribute>()`**: Retrieves the first matching member attribute without boxing the enum argument. Results are cached and attribute instances are shared between callers. Missing attributes and unnamed flag combinations return null.

`ToInt` and `EqualsEnum` are obsolete. The former one-type-argument `GetAttributeOfType<T>()` overload has been removed; specify both the enum and attribute types.

**Example:**
```csharp
using System.ComponentModel;

enum Status
{
    [Description("Active")]
    Active = 1,
    Inactive = 0
}

int value = Status.Active.ToValue<Status, int>(); // 1
bool converted = Status.Active.TryToValue<Status, int>(out int rawValue);
bool isActive = Status.Active.EqualsValue(Status.Active);
var desc = Status.Active.GetAttributeOfType<Status, DescriptionAttribute>();
```

---

### 3. Object & String Extensions (`ObjectExtension` Static Class)
Robust type conversion and string parsing utilities.

*   **`ConvertTo<T>()`**: Converts an object to type `T` (supports `Nullable` and `InvariantCulture`).
*   **`ParseTo<T>()`**: Parses a string into type `T` (supports primitives, `Enum`, and `Nullable`).
*   **`TryConvertTo<T>(out result)`**, **`TryParseTo<T>(out result)`**: Return success separately from the result, distinguishing a valid zero from a failed conversion. Expected failures return false without logging.

All conversions use `InvariantCulture`. `ConvertTo` and `ParseTo` return the default value on expected failures and send internal diagnostics to the configured logger. Unexpected custom-converter exceptions propagate to the caller. Internal diagnostics are independent of the public conditional logging methods.

**Example:**
```csharp
object raw = "123.45";
float val = raw.ConvertTo<float>(); // 123.45f

string input = "Active";
Status status = input.ParseTo<Status>(); // Status.Active
bool parsed = "0".TryParseTo(out int zero); // true, zero == 0
```

---

### 4. Value Conversion System (`ValueConverter` Class)
A caching-based conversion system to minimize parsing overhead for string arrays.

*   **`GatValue<T>(index)`**: Gets and caches the parsed value (Note: method name is `GatValue` in source).
*   **`TryGetValue<T>(index, out result)`**: Returns the cached conversion status and value without logging failures.
*   **`SetValue<T>(index, value)`**: Sets a value and updates the internal string source.

Each slot caches results and success status for every requested type. Updating a slot invalidates all of its type caches. The converter is not thread-safe; callers must synchronize shared access.

**Example:**
```csharp
string[] data = { "100", "True", "2.5" };
var converter = new ValueConverter(data);

int id = converter.GatValue<int>(0); // 100 (parsed and cached)
bool flag = converter.GatValue<bool>(1); // True
```

---

### 5. String Utilities (`StringExtension` Static Class)
Helpers for extracting delimited text and capitalizing the first character.

*   **`SubstringBetween(start, end)`**: Returns the text between the first ordinal match of `start` and the next ordinal match of `end`. Returns an empty string when the source is null or empty, or either delimiter is missing. Delimiters must be non-null.
*   **`ToUpperFirst()`**: Uppercases only the first character using invariant casing, leaving the remaining characters unchanged. Returns an empty string for a null or empty source, and the original string when the first character is already unchanged by uppercasing.

**Example:**
```csharp
using CrowRx;

string content = "<name>CrowRx</name>".SubstringBetween("<name>", "</name>"); // "CrowRx"
string title = "crowRx".ToUpperFirst(); // "CrowRx"
```

---

### 6. Type Utilities (`TypeExtension` Static Class)
Helpers for checking type assignability and formatting readable type names.

*   **`InheritsFrom(target)`**, **`InheritsFrom(target, isIncludeSelf)`**: Checks whether the target type is assignable from the source type, including base classes and implemented interfaces. An identical type matches by default; pass `false` to exclude it. Returns false when either type is null.
*   **`GetRealTypeName()`**: Returns a short display name with readable generic arguments, such as `Dictionary<String,Int32>`. Omits namespaces and declaring types.
*   **`GetRealTypeFullName()`**: Returns a display name including namespaces and declaring types, such as `System.Collections.Generic.List<System.Int32>`. Nested types use dots instead of `+`.

Both name helpers support arrays (including multidimensional arrays), pointers, by-reference types, and generic parameters. They throw `ArgumentNullException` for a null type. These names are for display; use `AssemblyQualifiedName` when an identifier must distinguish assemblies.

**Example:**
```csharp
using System.Collections.Generic;
using CrowRx;

bool implementsInterface = typeof(List<int>).InheritsFrom(typeof(IEnumerable<int>)); // true
bool excludesSelf = typeof(List<int>).InheritsFrom(typeof(List<int>), false); // false
string name = typeof(Dictionary<string, int>).GetRealTypeName(); // "Dictionary<String,Int32>"
string fullName = typeof(List<int>).GetRealTypeFullName(); // "System.Collections.Generic.List<System.Int32>"
```

---

### 7. Cancellation Token Source Utilities (`CancellationTokenSourceExtension` Static Class)
Owner-managed cancellation and disposal of a token source.

*   **`Release()`**: Requests cancellation unless it has already been requested, then disposes the source in a `finally` block. A null source is ignored. Exceptions from cancellation callbacks propagate, but the source is still disposed.

Call this from the owner when operations no longer need to access the token source. It does not wait for asynchronous operations to finish and does not guarantee safe repeated calls after disposal.

**Example:**
```csharp
using System.Threading;
using CrowRx;

var source = new CancellationTokenSource();
CancellationToken token = source.Token;
// Pass token to the owned operation and finish any access to source before cleanup.
source.Release();
```

## Requirements
- **Unity 6.0 or newer**
- **.NET 10.0 SDK** (for compilation; see `global.json`)
- **.NET Standard 2.1** compatibility

## Installation
Install via NuGet or [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity).

## License
This project is licensed under the [MIT License](LICENSE).
