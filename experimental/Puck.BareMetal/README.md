# Puck.BareMetal

A minimal, no-runtime ("bare metal") C# environment for Puck — the foundation for the
**"fake Linux host at boot"** target. It is built on **[`Puck.Runtime`](runtime/)**, our own
freestanding core library that defines `System.Object`, `System.String`, `System.Array`,
the primitives, spans, and the Windows / UEFI startup — with **no garbage collector and no
BCL**.

Heap memory is handled entirely by a vendored, statically-linked
[**mimalloc**](https://github.com/microsoft/mimalloc): every managed allocation (`new`)
flows through mimalloc rather than the OS allocator, with no `mimalloc.dll` dependency. See
[Memory: mimalloc](#memory-mimalloc).

## Licensing

Puck.BareMetal ships under the **repository's license** (PolyForm Noncommercial 1.0.0,
dual-licensed with a paid commercial option — see [`LICENSE`](LICENSE) and
[`/LICENSING.md`](../../LICENSING.md)), the same as the rest of Puck. That dual-license model
depends on the tree containing **no copyleft code**, so everything here is either ByteTerrace's
own work or a vendored component under a **permissive** license (MIT / Apache-2.0 / BSD) that
allows redistribution under our terms. Each vendored component keeps its own license and
copyright — see [`NOTICE.md`](NOTICE.md).

This subtree still lives outside `/src`, keeps its own [`Directory.Build.props`](Directory.Build.props)
to break the MSBuild inheritance chain, and is excluded from `Puck.slnx`, so the freestanding
build context never leaks into the engine.

## Provenance

[`Puck.Runtime`](runtime/) is ByteTerrace's own. The NativeAOT runtime ABI it must match (the
`MethodTable` layout, the `Rhp*` runtime exports, the startup/static-init contract) and the minimal
`System.*` type shapes are derived from **dotnet/runtime**'s MIT-licensed `System.Private.CoreLib` /
`Runtime.Base`. See [`NOTICE.md`](NOTICE.md).

## Why this isn't a normal `ProjectReference`

`Puck.Runtime` *is* the core library — it defines `System.Object` and friends. You cannot reference
it from a normal `net10.0` project that already has CoreLib; it **replaces** CoreLib. A
`Puck.BareMetal` program is compiled by pointing the NativeAOT IL compiler (ILC) at `Puck.Runtime`
as the *system module* (`IlcSystemModule`) and emitting a freestanding native binary.

## Layout

- `runtime/` — `Puck.Runtime`, the freestanding core library (the ILC system module); ours.
- `mimalloc/` — vendored MIT mimalloc source (verbatim, `v3.3.2`); the heap allocator,
  compiled from source into the freestanding link.
- `compat/` — ByteTerrace shims bridging puck + mimalloc to the stock ILC runtime
  contract (managed shims + `compat/native/puck-rt.c` and `compat/native/mimalloc-glue.c`
  native glue).
- `build/` — the reusable bare-metal build recipe (`Puck.BareMetal.props` + `.targets`).
  Every NativeAOT/link/size decision lives here, so any puck program shares one
  audited pipeline instead of copy-pasting MSBuild plumbing.
- `samples/Hello/` — proof-of-compile boot/hello stub. Its `Program.cs`, plus a ~20-line
  `.csproj` that just imports `build/` and sets an assembly name.

## Building & running (Windows)

The native link needs the **Windows SDK** (a standard
[NativeAOT prerequisite](https://aka.ms/nativeaot-prerequisites): "Desktop development
with C++"), and `cl.exe` is used to build the small freestanding C glue. Build from a VS
Developer shell so `link.exe`, `cl.exe`, and the SDK `LIB` paths are set:

```powershell
& "<VS>\Common7\Tools\Launch-VsDevShell.ps1" -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
dotnet publish samples/Hello/Hello.csproj -r win-x64 -c Release
.\samples\Hello\bin\Release\net10.0\win-x64\publish\Puck.BareMetal.Hello.exe
# -> Puck.BareMetal: hello from puck + mimalloc - NativeAOT, no GC, no .NET runtime.
```

The result is a **~105 KB self-contained native executable** (107,008 bytes) with no .NET
runtime, no GC, and no BCL. It imports only OS libraries (`kernel32`, `advapi32`, and the
Universal CRT) — there is **no `mimalloc.dll`**; the allocator is linked in statically. The
bulk of the size is mimalloc itself; the puck + glue code is a few KB (see
[Optimizing for size](#optimizing-for-size)).

## How the build works

The whole recipe lives in [`build/`](build): [`Puck.BareMetal.props`](build/Puck.BareMetal.props)
sets the properties and [`Puck.BareMetal.targets`](build/Puck.BareMetal.targets) the custom
targets. A bare-metal program is just those two imports plus its own name — see
[`samples/Hello/Hello.csproj`](samples/Hello/Hello.csproj).

1. `puck/puck.csproj` compiles the vendored source into `puck.dll` (`NoStdLib`).
2. The program runs the stock NativeAOT **ILC** with `IlcSystemModule=puck`, so puck
   *is* the core library. The ILC exports the managed `Main` as `__managed__Main`; the
   binary's OS entry is the native `PuckStart` (see [Statics & startup](#statics--startup)),
   which initializes GC statics and then calls `__managed__Main`.
3. **Dehydration is disabled** (`IlcDehydrate=false`). With it on (the default under
   `OptimizationPreference=Size`), ILC packs frozen data — including string literals — into
   a blob that a *startup* routine expands into a zero-init section. We strip the normal
   startup, so that routine never runs; leaving dehydration on makes every frozen object
   (every `ldstr`) read as zero. Disabling it emits frozen data in place.
4. `BuildNativeRuntimeGlue` compiles the freestanding C inputs with `cl.exe`: the runtime
   glue (`compat/native/puck-rt.c`), the whole vendored allocator as one TU
   (`mimalloc/src/static.c`), and the mimalloc bridge (`compat/native/mimalloc-glue.c`).
5. The SDK's default link pulls the **full** NativeAOT runtime (bootstrapper, GC, event
   pipe, brotli/zlib, a wide OS import-lib set). The `LinkPuckMinimal` target strips it
   to a minimal link — `app.obj + native glue + mimalloc + kernel32 + advapi32 +
   ucrt` — by *deriving* the removal set from the SDK's own
   `@(NativeLibrary)`/`@(SdkNativeLibrary)` lists (keeping only the OS import libs puck +
   mimalloc need), so it tracks SDK changes instead of a hand-maintained name list.
6. `compat/` supplies small shims for the stock ILC's runtime contract beyond what
   `Puck.Runtime` ships: managed (`ThrowHelpers`, `System.Buffer`, `System.Runtime.TypeCast`,
   `IDynamicInterfaceCastable`) and native (`compat/native/puck-rt.c`: the JIT-transition
   symbols `RhpReversePInvoke`/`Return`, `RhpGcPoll`, `RhpTrapThreads`, `RhpFallbackFailFast`,
   `RhpNewArrayFast`, plus the `memset`/`memcpy`/`memmove` block intrinsics).

## Memory: mimalloc

puck's object allocator (`StartupCodeHelpers.AllocObject`, behind `RhpNewFast` /
`RhpNewArrayFast`) calls `PuckAllocZeroed`, which is backed by a statically-linked
[mimalloc](https://github.com/microsoft/mimalloc) — so **every** managed `new` is a mimalloc
allocation, with no `mimalloc.dll` at runtime.

Making a full C allocator work in a no-CRT freestanding image takes a small bridge,
[`compat/native/mimalloc-glue.c`](compat/native/mimalloc-glue.c):

- **TLS without the CRT.** mimalloc keeps its per-thread heap in a `__declspec(thread)`
  variable; the glue defines the magic `_tls_used` TLS directory (normally the CRT's
  `tlssup.obj`) so the Windows loader sets up static TLS and runs the TLS callbacks.
- **libc shims.** mimalloc references a few libc functions (`abort`, `atexit`, `getenv`,
  `strtol`, `fputs`) that, taken from the real CRT, would touch uninitialised CRT state since
  CRT startup never runs; the glue supplies freestanding versions. (`memset`/`memcpy`/
  `memmove` come from `puck-rt.c`.)
- **Explicit init.** mimalloc self-initialises from a `.CRT$XIU` constructor that only the
  CRT runs, so the glue calls `mi_process_init()` / `mi_thread_init()` once on first use.

mimalloc is compiled `MI_DEBUG=0 MI_STAT=0 MI_SECURE=0` for a lean release allocator. Since
puck has no GC, nothing is ever freed; allocation goes through a real, production allocator
rather than one OS call per object.

## Statics & startup

Reference-typed (**GC**) static fields — `static IFoo s_foo;`, generic `Holder<T>.Instance`,
etc. — need a per-type "spine" object allocated and registered before first use. Normally
NativeAOT's native bootstrapper does this (via `InitializeModules`), but this build
strips the bootstrapper. So a custom entry point, **`PuckStart`** (in
[`compat/native/puck-rt.c`](compat/native/puck-rt.c), wired via `EntryPointSymbol`),
runs the GC-static slice of that init before calling `__managed__Main`:

1. read the `GCStaticRegion` bounds from `__ReadyToRunHeader` (absolute section rows);
2. walk the region (relative-pointer entries), and for each uninitialized type allocate its
   spine via `RhpNewFast` (mimalloc) and patch the base cell.

The algorithm mirrors NativeAOT's MIT-licensed `InitializeStatics`; with no GC the
spines simply live forever (no GC-handle rooting needed). This makes **all GC statics work**
— which is also the gate behind a generic-static DI container, `Dictionary<,>`,
`EqualityComparer<T>.Default`, and `Array.Empty<T>`.

> **Limitation:** pre-initialized GC static *data* (statics with compile-time constant initial
> values) is not yet copied — those spines come back zeroed. Reference-typed service/DI
> statics are null-initial, so they are correct; constant-initialized static arrays/values are
> a known TODO.

## Optimizing for size

The binary is a freestanding boot artifact, so the build drops everything a managed host
would carry but a bare-metal program never queries. mimalloc itself is ~100 KB of allocator
and dominates the final image; the size work below is about keeping *everything else* — the
puck + glue code and the PE container — as lean as possible. Without mimalloc the same
pipeline produces a **5,120-byte** binary (down from the stock NativeAOT default of
**7,680 bytes**, −33%). The knobs, in `build/Puck.BareMetal.props`/`.targets`:

- **No debug info** — `DebugType=none` (which also stops the SDK from forcing
  `NativeDebugSymbols` back on), removing ILC `-g`, the linker `/DEBUG` + `/SOURCELINK` +
  `/NATVIS`, and every `.pdb`. The whole subtree's `Directory.Build.props` likewise emits no
  managed PDB, so a stray `puck.pdb` no longer rides along into publish.
- **No Win32 version resource** — `IlcGenerateWin32Resources=false` drops the default
  ~1.5 KB `VS_VERSION_INFO` section nothing reads at boot.
- **Fewer PE sections** — the layout is folded to just `.text / .rdata / .data / .reloc`.
  The near-empty `.modules` (8 bytes) and the `.pdata` unwind table are merged into `.rdata`
  (the PE exception data-directory entry is preserved, so OS unwinding is unaffected); each
  section otherwise costs a full 512-byte file block of padding. `.data` (writable) and
  `.reloc` (ASLR fixups — kept on purpose) stay separate. (The mimalloc build adds a `.CRT`
  section and a TLS template for mimalloc's init constructor and thread-local heap.)
- **Invariant globalization, folded method bodies, system resource keys**, and cl.exe `/O1`
  (size) for the native glue round it out.

Each knob is a default, not a law: a program that wants, say, shippable version metadata can
set the one property back in its own `.csproj`.

## Status

- ✅ `Puck.Runtime` — our own freestanding core library — under the repository's PolyForm license.
- ✅ License segregation in place (LICENSE, NOTICE, isolated build props, excluded from `Puck.slnx`).
- ✅ `Puck.Runtime.dll` compiles with the stock .NET 10 toolchain (UEFI + Windows).
- ✅ Stock .NET 10 ILC generates native code with `IlcSystemModule=Puck.Runtime`.
- ✅ Links freestanding and **runs**: a ~105 KB native exe (107,008 bytes), no .NET runtime,
      no `mimalloc.dll`, exit 0.
- ✅ String literals, the indexer/`Length`, `stackalloc`, and P/Invoke all work.
- ✅ **Heap allocation works through statically-linked mimalloc**: the sample allocates a
      256 KB `byte[]`, verifies it is zero-initialized, and write/read round-trips it before
      printing — `new` → `RhpNewArrayFast` → `PuckAllocZeroed` → `mi_zalloc`.
- ✅ **Object model verified**: interface dispatch, class allocation, constructor injection,
      and (via `PuckStart`'s GC-static init) **GC statics — non-generic *and* generic**.
- ✅ A **reflection-free DI substrate** runs end-to-end on puck: a generic-static service
      holder + factory-lambda registration + constructor injection + interface resolution.
- ✅ **A minimal Vulkan window runs freestanding** (`samples/VulkanWindow`): a Win32 window
      (hand-written user32 interop + `UnmanagedCallersOnly` WndProc) with Vulkan brought up on
      it — instance, Win32 surface, device/queue, swapchain, render pass, and an
      acquire/submit/present loop that **clears the window to a color**. `vulkan-1.dll` is
      loaded dynamically; `Puck.Vulkan`'s blittable `Vk*` bindings are reused **as source**.
      ~120 KB, depends only on OS DLLs, exits cleanly.
- ✅ **Deterministic teardown** (`System.IDisposable` + a `DisposalScope`): resources register
      and are disposed LIFO; the Vulkan window explicitly `vkDestroy*`s its objects and
      `DestroyWindow`s, in reverse order, instead of leaning on process exit.
- ✅ Reusable, warning-clean build (`build/Puck.BareMetal.props` + `.targets`).
- ⏳ **Polymorphic interface dispatch** (`RhpInitialDynamicInterfaceDispatch`) — interface
      calls only work devirtualized (single implementer) today; multi-impl dispatch (which a
      real DI container needs) is the next foundational runtime piece. Abstract-base-class
      (vtable) polymorphism works now, which is why `DisposalScope` uses a `Disposable` base.
- ⏳ Pre-initialized GC static *data* copy; a real DI container. (No GC, so `Dispose` frees
      only *native* resources — managed objects live until process exit, by design.)
- ⏳ The "fake Linux host at boot" entry point.
