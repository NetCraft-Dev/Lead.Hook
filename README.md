# Lead.Hook

A hook engine for .NET 10 with three patching modes — load-time IL rewriting, runtime native patching, and runtime method-body injection through the CLR ReJIT API. No Harmony, no detours library.

```powershell
dotnet add package Lead.Hook
```

## Three modes

| Mode | When it runs | How | Reversible |
|---|---|---|---|
| `ILRewrite` | Before the assembly loads | Mono.Cecil rewrites IL instructions | No — permanent for the loaded assembly |
| `RuntimePatch` | After methods are JIT-compiled | Overwrites the native entry with an absolute jump | Yes — `Unpatch()` restores the original bytes |
| `RuntimeInject` | On code that is already running | The native layer submits a new method body through ReJIT | No |

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
| `Probe` | Method entry and every exit | Keep the original body and instrument around it; safe on hot methods that may get inlined |
| `Mark` | Method entry | Report once on entry, without timing |
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

By default a rule matches its anchor **everywhere** in the assembly. `InType` / `InMethod` narrow it to one host method, `Placement` decides whether the replacement call replaces the anchor or is inserted next to it, and `Ordinal` picks one occurrence when the anchor matches several times.

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
- `Ordinal` (0-based) selects which occurrence to hook when the same anchor appears more than once in scope.
- Several rules may share one anchor; the first rule whose host scope matches wins.
- `Before` / `After` apply only to instruction-level hook types (`CallSite`, `NewObj`, `FieldRead`, `FieldWrite`, `TypeCheck`, `Box`, `FunctionPointer`). `MethodBody` always replaces, and the method-level `Probe` / `Mark` ignore placement entirely.
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

## Mixins

A mixin moves members out of a source type and into a target type, optionally adding interfaces to the target along the way.

```csharp
var engine = new HookBuilder()
    .AddMixin(new MixinRule(
        targetType: "MyApp.Player",
        sourceAssembly: "MyApp.Extras",
        sourceTypeName: "MyApp.Extras.PlayerExtras",
        interfaces: new[] { new TypeRef("MyApp.Extras.IExtras") }))
    .RegisterMixinSource("MyApp.Extras", () => File.ReadAllBytes("MyApp.Extras.dll"))
    .Build();
```

How it works, and what to watch out for:

- **It moves, it does not copy.** The members are transferred out of the source type, leaving an empty shell behind. Mod code must not keep using the source type afterwards.
- **Load-time only.** Mixins change type layout, which ReJIT cannot do, so they never apply to `RuntimePatch` / `RuntimeInject`.
- **Interface injection turns the moved methods virtual.** Interface dispatch only goes through the vtable, so a plain moved method makes the CLR report the interface as unimplemented and throw `TypeLoadException`. Moved public instance methods are marked `IsVirtual` + `IsNewSlot` — the latter is required, otherwise they would silently override a base-class member of the same name.
- **Types are referenced by name, not loaded.** `TypeRef` builds a metadata reference from the name because mixins run before the target assembly enters memory; resolving by reflection at that point would pull unwritten assemblies in early.
- **Nested types are not supported**, and a source that cannot be resolved — or that points at the target itself — is skipped silently. Catching those is the caller's job at assembly-build time.

## Loading rewritten assemblies

`RewritingLoadContext` rewrites on load, which is the only reliable moment before JIT. An assembly loaded this way always runs instrumented code, inlined or not.

```csharp
var engine = new HookBuilder()
    .Hook("NetCraft.Game.World", "Tick")
        .With(typeof(TickProbe), nameof(TickProbe.OnTick))
    .Build();

var context = new RewritingLoadContext(
    name: "hooks",
    directory: AppContext.BaseDirectory,
    engine: engine,
    excluded: new[] { "NetCraft.ModLoader" },
    prefixes: "NetCraft");

var assembly = context.LoadFromAssemblyName(new AssemblyName("NetCraft.Game"));
```

- `excluded` lists assemblies that must never be rewritten. It has to include the assembly that holds your probe types — otherwise the child context loads a second copy of them and reports land in a different set of statics.
- A read or rewrite failure sets `LastError` and falls back to loading the original bytes rather than failing the load. `RewrittenAssemblies` records what was actually rewritten.
- The context is created with `isCollectible: false`.

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

// Call the original implementation from a patched method
var original = runtime.GetTrampoline<Func<int, int>>(typeof(MyClass).GetMethod("StaticMethod")!);

// Unpatch a specific method, or everything
runtime.Unpatch(typeof(MyClass).GetMethod("StaticMethod")!);
runtime.UnpatchAll();
```

`ActivePatches` lists what is currently applied, and patching the same method twice throws `InvalidOperationException`.

## Runtime method-body injection

`RuntimeInject` rewrites a method the same way `ILRewrite` does, but hands the new body to the CLR at runtime instead of before load. It goes through the native layer, which hooks the profiling API.

```csharp
if (RuntimeInjector.IsAvailable)
{
    var methods = RuntimeInjector.Inject(assemblyBytes, "MyApp");
    // methods is the list of "Type::Method" entries that were registered
}
```

The native component ships inside the package as a RID-specific asset. A profiler has to be in place *before* the CLR starts, so the library cannot enable it by itself — `ProfilerBootstrap` prepares the information and the caller starts the process:

```csharp
var startInfo = new ProcessStartInfo("NetCraft.ServerExe");
if (ProfilerBootstrap.Apply(startInfo))
    log.Info(ProfilerBootstrap.Describe());

Process.Start(startInfo);
```

`Apply` fills in all three variables — `CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER` and `CORECLR_PROFILER_PATH` — so the CLSID never has to be copied by hand. Under a single-file publish, where the native library is unpacked to a temporary directory, pass its path explicitly with `Apply(startInfo, nativeLibraryPath)`.

The switch is read once at process start: enabling the profiler later has no effect and requires a restart. Expect the target process to run without ReadyToRun and start up slower — disabling ReadyToRun images is a prerequisite for ReJIT.

Limits worth knowing before you reach for it:

- The body is rewritten from the **original bytes**, so it does not contain changes made by load-time rewriting. If both modes hit the same method, the load-time version is replaced wholesale.
- A method can only be injected once — `GetReJITParameters` claims a request by module plus method, so a second registration for the same method is silently ignored.
- No generics, no field or string or type tokens, no local-variable rewriting, no exception-handling clauses, and only one-dimensional zero-based arrays.

## Replacement signatures

```csharp
// CallSite (instance method): first param is 'this'
public static int Add(object self, int x) => 0;

// CallSite (static method): no 'this'
public static string GetText() => "replaced";

// MethodBody: same as CallSite
public static string GetSecret() => "replaced";

// Probe / Mark: same parameters as the host method, plus a label argument when
// LabelArgumentIndex is set
public static void OnTick(object self) { }

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
- Tiered compilation re-JITs methods as they warm up and overwrites the entry, which wipes the jump. Marking the target `MethodImplAttributes.NoOptimization` disables both inlining and tiering and keeps the patch stable, at the cost of the method's JIT optimization
- The jmp overwrite is being phased out in favor of `RuntimeInject`

## Requirements

.NET 10, C# 14. The only managed dependency is `Mono.Cecil`. The native profiler layer ships in the package for `win-x64`, `linux-x64` and `osx-x64`; other platforms are not covered.

## Repository

<https://github.com/NetCraft-Dev/Lead.Hook>

## License

MIT
