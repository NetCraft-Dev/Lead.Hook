# Lead.Hook

A dual-mode hook engine for .NET 10 — load-time IL rewriting **and** runtime native patching. No Harmony, no detours library.

```powershell
dotnet add package Lead.Hook
```

## Two modes

| Mode | When it runs | How | Reversible |
|---|---|---|---|
| `ILRewrite` | Before the assembly loads | Mono.Cecil rewrites IL instructions | No — permanent for the loaded assembly |
| `RuntimePatch` | After methods are JIT-compiled | Overwrites the native entry with `jmp [rip+addr]` | Yes — `Unpatch()` restores the original bytes |

## IL rewriting — hook types

| HookType | IL instruction | Description |
|---|---|---|
| `CallSite` | `call` / `callvirt` | Redirect method calls to your replacement |
| `MethodBody` | Method IL body | Replace the entire method body (works with static classes, reflection-safe) |
| `NewObj` | `newobj` | Intercept object creation, replace with a factory method |
| `FieldRead` | `ldfld` / `ldsfld` | Intercept field reads |
| `FieldWrite` | `stfld` / `stsfld` | Intercept field writes |
| `TypeCheck` | `isinst` / `castclass` | Intercept `as` / `is` checks |
| `Box` | `box` / `unbox.any` | Intercept boxing and unboxing |
| `FunctionPointer` | `ldftn` / `ldvirtftn` | Intercept function pointer acquisition |
| `LocalRead` | `ldloc`, `ldloc.s`, `ldloc.0-3` | Intercept local variable reads |
| `LocalWrite` | `stloc`, `stloc.s`, `stloc.0-3` | Intercept local variable writes |
| `Constant` | `ldc.i4.*`, `ldc.i8`, `ldc.r4`, `ldc.r8`, `ldstr` | Intercept constant loads |

## Quick start — IL rewriting

```csharp
using Lead.Hook;

var engine = new HookBuilder()
    .Hook("MyApp.TextPrinter", "GetText")
        .With(typeof(MyReplacement), "GetText")

    .Hook("MyApp.SecretKeeper", "GetSecret", HookType.MethodBody)
        .With(typeof(SecretReplacement), "GetSecret")

    .Hook("MyApp.ConfigLoader", ".ctor", HookType.NewObj)
        .With(typeof(ConfigFactory), "Create")

    .Hook("MyApp.UserProfile", "Name", HookType.FieldRead)
        .With(typeof(FieldHooks), "GetName")

    .Build();

var result = engine.RewriteWithResult("TargetApp.dll");
```

## Host scoping and insertion placement

By default a rule matches its anchor **everywhere** in the assembly. `InType` / `InMethod` narrow it to one host method, and `Placement` decides whether the replacement call replaces the anchor or is inserted next to it.

```csharp
var engine = new HookBuilder()
    .AddRule(new HookRule("MyApp.Host", "Compute", typeof(Probe), nameof(Probe.OnBefore),
        HookType.CallSite, PatchMode.ILRewrite,
        inType: "MyApp.Host", inMethod: "Run",
        placement: HookPlacement.Before))
    .Build();
```

| Placement | Effect | Replacement signature |
|---|---|---|
| `Replace` (default) | Replaces the anchor — the original call no longer runs | Matches the callee's arguments (`this` counts for instance calls) |
| `Before` | Keeps the anchor, calls the replacement right before it | Matches the **host method's** parameters (`this` counts) |
| `After` | Keeps the anchor, calls the replacement right after it | Same as `Before` |

Notes:

- `InType` and `InMethod` are independent and optional. Leaving both empty restores the original global behavior.
- Several rules may share one anchor; the first rule whose host scope matches wins.
- `Before` / `After` apply only to instruction-level hook types (`CallSite`, `NewObj`, `FieldRead`, `FieldWrite`, `TypeCheck`, `Box`, `FunctionPointer`). `MethodBody` always replaces.
- An inserted call reads the host's parameters and never touches what the anchor already pushed, so the surrounding stack stays balanced.

### Anchoring inside a method body

`LocalRead`, `LocalWrite` and `Constant` anchor on a position inside the host method rather than on a callee. For these three, `OriginalType` / `OriginalMethod` name the **host method**, not a referenced entity.

| Hook type | Extra parameter | Selects |
|---|---|---|
| `LocalRead` | `localIndex` | Reads of that local slot (0-based) |
| `LocalWrite` | `localIndex` | Writes to that local slot |
| `Constant` | `constantValue` | Loads of that exact constant, compared by boxed type |

```csharp
// callback right before local slot 0 is written
new HookRule("MyApp.Host", "Compute", typeof(Probe), nameof(Probe.OnWrite),
    HookType.LocalWrite, PatchMode.ILRewrite,
    localIndex: 0, placement: HookPlacement.Before)

// swap the constant 5 for whatever OnConst() returns
new HookRule("MyApp.Host", "Compute", typeof(Probe), nameof(Probe.OnConst),
    HookType.Constant, PatchMode.ILRewrite, constantValue: 5)
```

`Replace` signatures follow the instruction's stack effect instead of the host's parameters: `LocalRead` and `Constant` push a value, so the callback takes zero params and returns it; `LocalWrite` consumes a value, so the callback takes one. `Before` / `After` still pass the host's parameters as usual.

`Constant` compares by boxed type — `5` (int) and `5L` (long) are different anchors.

## Quick start — runtime patching

```csharp
using Lead.Hook;

using var runtime = new RuntimeHookEngine();

// Patch a static method
runtime.Patch(typeof(MyClass).GetMethod("StaticMethod")!,
              typeof(MyReplacement).GetMethod("StaticMethod")!);

// Patch an instance method (the replacement receives 'this' as first param)
runtime.Patch(typeof(MyClass).GetMethod("InstanceMethod")!,
              typeof(MyReplacement).GetMethod("InstanceMethod")!);

// Now calls to MyClass.StaticMethod() and obj.InstanceMethod() are redirected

// Unpatch a specific method
runtime.Unpatch(typeof(MyClass).GetMethod("StaticMethod")!);

// Unpatch everything
runtime.UnpatchAll();
```

## Mixed mode — IL rewrite plus runtime patch

```csharp
var engine = new HookBuilder()
    // IL rewrite rules (applied at assembly load)
    .Hook("MyApp.TextPrinter", "GetText")
        .With(typeof(MyReplacement), "GetText")

    // Runtime patch rules (applied to already-loaded methods)
    .Hook("MyApp.LiveService", "Process", HookType.CallSite, PatchMode.RuntimePatch)
        .With(typeof(LiveReplacement), "Process")

    .Build();

// IL rewrite happens here
var result = engine.RewriteWithResult("TargetApp.dll");

// Runtime patches are applied automatically for PatchMode.RuntimePatch rules.
// The runtime engine is also reachable directly:
engine.ApplyRuntimePatch("MyApp.AnotherType", "Method", typeof(Replacement), "Method");
engine.RemoveRuntimePatch("MyApp.AnotherType", "Method");
```

## Replacement signatures

```csharp
// CallSite (instance method): first param is 'this'
public static int Add(object self, int x) => 0;

// CallSite (static method): no 'this'
public static string GetText() => "replaced";

// MethodBody: same as CallSite
public static string GetSecret() => "replaced";

// NewObj: same params as the constructor, returns the replacement object
public static ConfigShadow Create(string env) => new("hacked-" + env);

// FieldRead (instance): receives 'this'
public static string GetName(object self) => "replaced";

// FieldRead (static): no params
public static string GetRole() => "admin";

// FieldWrite (instance): receives 'this' plus the value
public static void SetName(object self, string value) { }

// FieldWrite (static): receives the value
public static void SetRole(string value) { }

// TypeCheck: receives object, returns object?
public static object? CheckType(object obj) => obj;

// Box: receives the value type, returns object
public static object BoxInt(int value) => value;

// Unbox: receives object, returns the value type
public static int UnboxInt(object value) => (int)value;

// FunctionPointer: returns IntPtr
public static IntPtr GetPtr() => IntPtr.Zero;
```

## How runtime patching works

1. Forces JIT compilation of both the original and the replacement method via `RuntimeHelpers.PrepareMethod`
2. Resolves the real native entry point by following the PreJitStub indirect jump
3. Backs up the original method's native code
4. Writes an absolute jump to the replacement method
5. On unpatch, restores the original bytes

**Platform support:**

| Platform | Memory protection | Jump encoding | Status |
|---|---|---|---|
| Windows x64 | `VirtualProtect` | `FF 25 00 00 00 00 <addr>` | Tested |
| Linux x64 | `mprotect` | `FF 25 00 00 00 00 <addr>` | Supported |
| macOS x64 | `mprotect` | `FF 25 00 00 00 00 <addr>` | Supported |
| Linux ARM64 | `mprotect` | `LDR X16, [PC]; BR X16` | Supported |
| macOS ARM64 | `mprotect` | `LDR X16, [PC]; BR X16` | Supported |

**Limitations:**

- Methods must be JIT-compiled before patching — call them once first
- Tiered compilation may re-JIT methods, which can overwrite a patch

## Requirements

.NET 10, C# 14. The only dependency is `Mono.Cecil`.

## Repository

<https://github.com/NetCraft-Dev/Lead.Hook>

## License

MIT
