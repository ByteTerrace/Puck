# Puck.BareMetal

Puck.BareMetal is a freestanding NativeAOT environment for running Puck without
the .NET runtime or a host operating system. `Puck.Runtime` replaces CoreLib,
native glue supplies the ABI expected by the .NET 10 compiler, and mimalloc owns
managed allocation in hosted images. The UEFI target additionally hosts one
ring-3 musl process and the device services needed by RADV.

This subtree is intentionally excluded from `Puck.slnx` and has its own
`Directory.Build.props`. Its compiler and linker settings must not flow into the
engine projects under `src/`.

## License and provenance

Puck-authored code follows the repository license; see [`LICENSE`](LICENSE) and
[`../../LICENSING.md`](../../LICENSING.md). Vendored components retain their own
permissive licenses and notices. [`NOTICE.md`](NOTICE.md) is the component and
source-provenance index.

`Puck.Runtime` is ByteTerrace code implementing ABI shapes required by NativeAOT.
Its `MethodTable`, `Rhp*`, static-initialization, and minimal `System.*` contracts
are informed by the MIT-licensed dotnet/runtime implementation. GPU register and
packet definitions come from the permissively licensed AMD files stored under
`amdgpu/`. Do not remove or rewrite the license and provenance files beside
vendored sources or firmware.

## Layout

- `runtime/` — the freestanding system module that defines core `System` types.
- `compat/` — managed and native ABI shims used by the stock NativeAOT compiler.
- `build/` — reusable Windows and UEFI MSBuild imports.
- `mimalloc/` — unmodified allocator source.
- `lwip/` and `mbedtls/` — network and TLS dependencies for the UEFI host.
- `amdgpu/` — AMD register/UAPI headers and signed Van Gogh firmware.
- `radv/` — scripts for producing the machine-local musl RADV closure.
- `samples/` — focused compiler/runtime probes, the freestanding Vulkan window,
  and the UEFI Linux-process host.
- `docs/` — current host, GPU, hardware, and Deck operating references.

## Why projects import the build

`Puck.Runtime` defines `System.Object` and therefore cannot be referenced by a
normal `net10.0` application that already uses `System.Private.CoreLib`. Each
program imports the BareMetal props and targets, which invoke NativeAOT ILC with
`Puck.Runtime` as `IlcSystemModule` and link the generated object with the
appropriate native entry and platform glue.

The shared build files centralize:

- ILC system-module and trimming settings;
- dehydration policy for frozen objects;
- native glue and allocator compilation;
- direct symbol binding and minimal import libraries;
- UEFI subsystem and entry-point selection;
- section layout and release-size settings.

Applications should override an MSBuild property only when their output contract
requires it; do not copy the linker recipe into individual sample projects.

## Runtime model

The runtime provides the subset exercised by the samples and host:

- core object, string, array, span, and primitive types;
- zero-initialized object and array allocation;
- reference-static spine creation, preinitialized static data, and lazy class
  constructors;
- polymorphic interface dispatch and normal class virtual dispatch;
- selected collections, `ArrayPool<T>`, disposable patterns, and interop shapes;
- direct-bound P/Invoke and the NativeAOT allocation/transition symbols.

Managed memory is not collected. Hosted targets use statically linked mimalloc;
UEFI uses memory obtained before firmware services are released. Managed objects
live for the image or process lifetime. Native resources still require explicit
disposal.

The runtime is not a replacement for the full BCL. Exception handling, reflection,
default interface methods, globalization, and many framework APIs are absent or
narrowly stubbed. Add a contract only when a real program requires it and verify
the emitted NativeAOT ABI rather than assuming CoreCLR behavior.

## Static initialization

The normal NativeAOT bootstrapper is not linked. The native entry point walks the
ReadyToRun `GCStaticRegion`, allocates each reference-static spine, copies a
preinitialized field image when present, and patches the base cell before managed
code begins. Lazy class constructors then run through the compatibility helper.

The hosted helper serializes first access so worker threads cannot observe a
partially initialized static. The UEFI kernel initializes its managed statics
before guest scheduling begins.

## Hosted Windows sample

Build from a Visual Studio Developer PowerShell so the Windows SDK compiler and
linker are available:

```powershell
dotnet publish samples/Hello/Hello.csproj -r win-x64 -c Release
samples/Hello/bin/Release/net10.0/win-x64/publish/Puck.BareMetal.Hello.exe
```

The executable contains no managed runtime or GC. The allocator is linked from
source; no `mimalloc.dll` is required.

`samples/VulkanWindow` demonstrates a freestanding Win32 window and Vulkan
swapchain. It loads `vulkan-1.dll`, uses source-linked blittable bindings, renders
a clear color, and releases Vulkan and Win32 resources deterministically.

## UEFI host

`samples/EfiLinux` is the production-oriented proof surface. It exits firmware,
installs its own CPU and memory environment, initializes the framebuffer and
devices, loads a musl ELF process, and services its Linux ABI from ring 0.

Run the QEMU smoke:

```powershell
samples/EfiLinux/run-qemu.ps1
```

Stage a Steam Deck USB:

```powershell
samples/EfiLinux/stage-deck.ps1 -Target E:\
```

QEMU validates the kernel substrate, guest ABI, network/TLS path, synthetic DRM
node, RADV initialization, and ACO shader compile. It cannot validate RDNA queue
execution. Deck procedures are documented separately.

## Operational documents

- [Ring-3 Linux Process Host](docs/ring3-process-host-plan.md) — boot flow,
  syscall/VFS surface, guest loading, and QEMU verification.
- [AMD Vulkan Host](docs/amd-vulkan-plan.md) — RADV boundary, kernel services,
  and implemented GPU path.
- [gfx1033 Hardware Reference](docs/gfx103-bringup-spec.md) — registers,
  firmware protocol, VMID0, KIQ, and graphics queue.
- [Steam Deck GPU Bring-up](docs/deck-bringup-handoff.md) — hardware runbook,
  current execution boundary, diagnostics, and log retrieval.

## Current constraints

- Managed objects are never reclaimed.
- The UEFI host runs one process and one address space; it is not general Linux.
- The syscall and BCL surfaces are demand-driven and intentionally incomplete.
- QEMU has no target AMD GPU model; hardware command execution requires a Deck.
- The Deck path executes a basic graphics-ring probe, but indirect-buffer fence
  completion and RADV command submission remain unresolved.
