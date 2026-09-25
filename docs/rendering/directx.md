# Direct3D 12 backend substrate

The low-level Direct3D 12 (DXGI + D3D12) backend for the Puck engine. Where
`Puck.Vulkan` hand-binds a flat C loader, DirectX is COM-based, so this project leans on
**[Microsoft.Windows.CsWin32](https://github.com/microsoft/CsWin32)**—Microsoft's actively
maintained, AOT-friendly P/Invoke + COM source generator—to emit the raw bindings. That
fits Puck's existing spirit: the engine already uses source-generated interop
(`[LibraryImport]` in `Puck.Platform`), and CsWin32 generates `unsafe`, pointer-and-vtable,
zero-marshaling code in the same shape as the rest of the codebase.

```text
namespaces  Puck.DirectX (+ .Interfaces, .Apis, .Interop, .Messages)
target      net10.0  (Windows-only at runtime; surface annotated [SupportedOSPlatform("windows8.1")])
deps        Microsoft.Windows.CsWin32 (build-only, PrivateAssets=all — no runtime dependency)
```

The `DirectXGpu*` types implement the neutral GPU contracts from
`Puck.Abstractions`: compute pipelines, descriptor allocation, storage
buffers and images, shared-surface export, and queue submission. Swapchains and the frame loop live in
`Puck.DirectX.Presentation`, mirroring the split between `Puck.Vulkan` and
`Puck.Vulkan.Presentation`.

Compute, graphics, and readback use one legacy resource-barrier model on the
same direct queue. Texture owners register their initial state and remove it
on disposal; the one recorder, `DirectXGpuRecorder`, carries that state across
compute and graphics passes. Buffer states last only for one command list,
because Direct3D 12 returns every buffer to `COMMON` after each `ExecuteCommandLists`. A buffer's
first transition in a list starts from the state its declared prior access
implies, so callers declare the access that actually preceded it: the SDF
engine declares each pass's buffer uses in `SdfFrameBufferPlan`, and a shader
pipeline takes each access's prior state from its plan (`ShaderPipelineAccess`),
which records every pass's ordered accesses with the prior state and barrier. A read through a
read-write binding is declared with the write the binding permits, because
the buffer must stay in `UNORDERED_ACCESS` for it. An upload-heap buffer stays
in `GENERIC_READ` for its whole life. A write followed by another access in
`UNORDERED_ACCESS` records a UAV barrier on that buffer alone.
`tests/Puck.DirectX.Tests` checks these barrier decisions without a device.
Sampled images permit both pixel and compute shader reads. Global UAV barriers
order repeated image writes, and raw UAV views clear storage buffers. Temporary clear
descriptors retire with their fenced command buffers, including reload and reset.

## Key features

- *Source-generated bindings, not hand-written P/Invoke:* CsWin32 emits the
  raw COM surface from `NativeMethods.txt`, `unsafe` and pointer/vtable-shaped
  in the same style as the rest of the codebase's interop.
- *No runtime COM marshaling:* `NativeMethods.json` sets `allowMarshaling:
  false`, so COM interfaces come through as `unsafe` structs with
  function-pointer vtables—no marshaling ceremony, no GC pressure.
- *A WARP fallback for headless verification:* software rendering is always
  available through `IDirectXDeviceApi.CreateWarpDevice`, so CI and
  no-GPU machines can still exercise the device path.
- *One failure shape:* every native call funnels through
  `HResultExtensions.ThrowIfFailed`, so a failing `HRESULT` always becomes a
  `DirectXException` carrying the operation name and the code.

---

## Structure

The layering mirrors `Puck.Vulkan` so the two backends read the same way:

| Folder | Prefix | What lives here |
|--------|--------|-----------------|
| `Messages/` | `DirectX*` | `readonly record struct` projections of native data (`DirectXAdapterDescription`). |
| `Interop/` | `DirectX*`, `Dxgi*` | The `IDisposable` handle owner (`DirectXDevice`) and shared low-level helpers (`DxgiInterop`, `HResultExtensions`). |
| `Interfaces/` | `IDirectX*Api` | The contracts—the dependency-injection / mocking seam. |
| `Apis/` | `DirectXNative*Api` | Thin implementations that marshal to the generated DXGI / D3D12 entry points. |

The neutral-contract implementations (`DirectXGpu*`) and the resource-state
tracking (`DirectXBufferStates`, `DirectXResourceStates`) sit at the project root.
So do the four builders every call site uses, in `Puck.DirectX` and its
presentation project alike: `DirectXBarriers.Transition` (a whole-resource
transition, no flags), `DirectXDescriptorHeaps.Create` (node mask zero; type,
size and shader visibility vary), `DirectXBuffers.CreateCommitted` (a
format-less row-major buffer with no heap flags; size, heap type, initial state
and resource flags vary), and `DirectXTextures.CreateCommitted` (a single-mip,
single-sample 2D texture on a default heap; extent, format, heap flags, initial
state, resource flags and optimized clear value vary). `DirectXTextures.OfUsage`
and `InitialStateOf` give an image the flags, clear value and initial state its
declared `GpuImageUsage` needs: `DirectXGpuImage`, `DirectXGpuExportableImage`
and the surface upload's texture all go through them.

A Direct3D 12 render pass is data: `DirectXGpuRenderPass` holds the
description's DXGI formats, which a pipeline state object is created for, and
`DirectXGpuFramebuffer` owns the render-target and depth-stencil views of the
images it binds. `DirectXGpuRecorder.BeginRenderPass` transitions each
attachment into its attachment state, clears an attachment that clears with
`ClearRenderTargetView` or `ClearDepthStencilView` (a clear inside a render
pass is disallowed; a depth attachment clears to its
`GpuDepthAttachment.ClearDepth`), begins a first-class render pass whose
ending access stores or discards, and sets the viewport and scissor to the
area the pass draws; `EndRenderPass` leaves a color attachment declared
shader-readable in the shader-read state and every other attachment where it
was. A geometry buffer is an upload-heap buffer bound by its GPU virtual address,
so it needs no view object of its own: `BindVertexBuffer` and `BindIndexBuffer`
build a vertex and an index view over ranges of it, and `DrawIndexed` records
`DrawIndexedInstanced`. `DirectXGpuPipelineFactory` creates an opaque PSO whose
attribute *n* is `POSITIONn`, with the render pass's formats and, exactly when
the pass has a depth attachment, a depth test that writes; the pipeline-library
identity covers the formats, the depth test and each attribute's format, offset
and semantic index. A framebuffer or pipeline that does not match its render
pass is refused before the device is touched.

A command list has separate compute and graphics root signatures, so
`BindPipeline`, `BindDescriptorSet` and `PushConstants` name their
`GpuBindPoint`: a compute pass binds and pushes at `Compute`, a draw at
`Graphics`. The recorder cannot tell a wrong bind point from the handle, and
one writes to a root signature the bound pipeline never set, which is
undefined behavior on the device.

Top-level helpers: `DirectXException` (carries the failing operation + `HRESULT`) and
`DirectXFeatureLevel` (a managed mirror of `D3D_FEATURE_LEVEL`).

### The CsWin32 surface

`NativeMethods.txt` lists exactly the APIs to generate; `NativeMethods.json` sets
`allowMarshaling: false` so COM interfaces come through as `unsafe` structs with
function-pointer vtables (no runtime COM marshaling, no GC ceremony). The generated
`Windows.Win32.*` types are `internal` to this assembly—only Puck types cross the public
boundary.

---

## Quick start

```csharp
using Puck.DirectX;
using Puck.DirectX.Apis;

var adapterApi = new DirectXNativeAdapterApi();
var deviceApi = new DirectXNativeDeviceApi();

foreach (var adapter in adapterApi.EnumerateAdapters()) {
    var maxLevel = deviceApi.ProbeMaxFeatureLevel(adapterLuid: adapter.AdapterLuid);
    // adapter.Description, adapter.DedicatedVideoMemory, adapter.IsSoftware, maxLevel ...
}

// WARP is always available — handy for headless/CI verification with no GPU.
using var device = deviceApi.CreateWarpDevice(minimumFeatureLevel: DirectXFeatureLevel.Level110);
```

`DirectXDevice` owns its `ID3D12Device` and releases it exactly once on `Dispose`—dispose
it like any other Puck handle owner.

## Capabilities

| Concern | Interface | Native call(s) | Result |
|---------|-----------|----------------|--------|
| Adapter enumeration | `IDirectXAdapterApi` | `CreateDXGIFactory2`, `IDXGIFactory4::EnumAdapters1` | `IReadOnlyList<DirectXAdapterDescription>` |
| Feature-level probe | `IDirectXDeviceApi` | `D3D12CreateDevice` (null device) | `DirectXFeatureLevel?` |
| Device creation | `IDirectXDeviceApi` | `D3D12CreateDevice` | `DirectXDevice` (owns `ID3D12Device`) |
| Software fallback | `IDirectXDeviceApi` | `IDXGIFactory4::EnumWarpAdapter` + `D3D12CreateDevice` | `DirectXDevice` (WARP) |
| Memory profile | `IDirectXDeviceApi` | `ID3D12Device::CheckFeatureSupport` (architecture, options 16), `IDXGIAdapter1::GetDesc1` | `GpuMemoryProfile` |
| Binding capabilities | `IDirectXDeviceApi` | `ID3D12Device::CheckFeatureSupport` (options, root signature, shader model, options 19) | `GpuDeviceCapabilities` |

Every feature query goes through `DirectXFeatureReads` over an
`IDirectXFeatureSupport`, which returns the query's `HRESULT` rather than
throwing. A runtime that does not know a feature, root signature version or
shader model answers `E_INVALIDARG`, and each read falls back as its
documentation states: options 19 to the heap sizes every binding tier
guarantees, the version and model queries to the next one down, the
architecture query to the default memory profile, and the Shader Model floor to
below. A bring-up that fails after its device exists releases the device and
everything created with it, so the context's next use creates the device again.

---

## Memory profile

When `DirectXDeviceContext` creates its device it reads the device's memory
profile beside its identity (`IDirectXDeviceApi.GetMemoryProfile`) and reports
it as `IGpuDeviceContext.MemoryProfile`. `DirectXNativeDeviceApi.MemoryProfile`
fills it from three native structures:

- `D3D12_FEATURE_DATA_ARCHITECTURE`: `UMA` with `CacheCoherentUMA` is coherent
  unified memory.
- `DXGI_ADAPTER_DESC1`: unified memory is one pool of the dedicated and shared
  memory together, all of it host-writable; a discrete adapter's pool is its
  dedicated video memory.
- `D3D12_FEATURE_DATA_D3D12_OPTIONS16`: a discrete adapter's dedicated memory is
  host-writable only when it supports GPU upload heaps.

A device that will not answer the architecture query reports the default
profile, which selects the staged copy. What the profile is for, and how a
policy is chosen from it, is described under
[the Vulkan memory profile](vulkan.md#memory-profile).

---

## Descriptor heaps

A device has two shader-visible descriptor heaps, `DirectXShaderVisibleHeaps`:
a CBV/SRV/UAV heap of the size the device reports
(`GpuDeviceCapabilities.ViewHeapSize`) and a sampler heap of its reported
`SamplerHeapSize`. `DirectXGpuBindings` creates them when the context brings a
device up and releases them when the context releases it, on `Recreate` and
`Dispose`, so a recreated device has a fresh pair. The heaps never grow.

- **A pool is a range.** `CreatePool` admits one range of the view heap through
  the device's `GpuDescriptorHeapBudget`, and a pool holding samplers one
  range of the sampler heap too, and `DestroyPool` returns them, so the next
  pool that fits receives them. `AllocateSet` places each set inside its
  pool's ranges. A pool no free range holds is refused with
  `GPU_DESCRIPTOR_HEAP`, and so is a pool whose views fit and whose samplers
  do not.
- **Owners are admitted before they allocate.** `IGpuBindings.CanAdmit` checks
  a candidate's whole statement of pools and allocates nothing. The pipeline
  node checks a candidate at install and a float preview when it is selected;
  each SDF engine creation site checks through `SdfWorldEngine.CheckAdmission`,
  and the unified overlay checks its one pool before it creates its resources.
  A candidate that does not fit is refused by name, and whatever is installed
  keeps presenting.
- **A group's samplers are descriptors.** A pipeline created from a
  `GpuPipelineLayoutDescription` binds through the root signature
  `DirectXRootSignatures.CreateLayout` creates from `DirectXRootLayout.Plan`: a
  view table per group, a sampler table for a group that holds samplers, the
  pushed index as one root constant at `b0` in space 4, and no static sampler.
  A set of such a group takes its view table from its pool's view range and
  its sampler table from its pool's sampler range. Every other pipeline still
  reads its samplers as static samplers in its root signature.
- **Every command list binds the pair once.** `DirectXGpuRecorder.BeginCommandBuffer`
  binds both heaps after the reset, so `BindDescriptorSet` sets only the
  descriptor table.
- **A clear takes a slot, not a heap.** A storage clear needs a GPU handle in
  the bound view heap and a CPU handle in a CPU-only heap. The device keeps
  `DirectXShaderVisibleHeaps.ClearDescriptors` of each: a range of the view heap
  admitted with the heaps, mirrored slot for slot by one CPU-only heap. The
  command list that records a clear holds its slot until it is reset or
  released.
- **The heaps count as device memory.** Both shader-visible heaps count under
  `memory.directx` as device-local allocations of their descriptors at the
  device's increment, and their release ends those entries before the device's
  teardown ends the device.

The surface compositor and the surface upload in `Puck.DirectX.Presentation`
still create shader-visible heaps of their own, which they bind on command
lists of their own.

---

## Result handling

Native calls return `HRESULT`. `HResultExtensions.ThrowIfFailed(operation)`
turns a failing code into a `DirectXException` carrying the operation name and the
`HRESULT`, matching `Puck.Vulkan`'s `VulkanException` pattern. (`EnumWarpAdapter` has no
non-throwing overload and surfaces the framework's COM exception directly—it effectively
never fails.)

A removed device is not an ordinary failure. `DXGI_ERROR_DEVICE_REMOVED`,
`DXGI_ERROR_DEVICE_RESET` and `DXGI_ERROR_DEVICE_HUNG` become the neutral
`DeviceLostException`, which the host's device-loss recovery catches. The calls a
removal reaches on a working device (mapping a resource, resetting and closing a
command list, signalling a queue and arming a fence event) go through
`DirectXCommandCalls` over an `IDirectXCommandCalls`, which calls each vtable slot
and returns its `HRESULT`, because the generated wrappers throw a `COMException`
that recovery never sees. The loss then carries the device's own
`ID3D12Device::GetDeviceRemovedReason`, such as `DXGI_ERROR_DRIVER_INTERNAL_ERROR`
for a page fault.

A drain before releasing objects follows one rule: a removed device counts as
drained. `DirectXCommandCalls.Drain` returns instead of throwing when the signal or
the event arm reports a removal, because a removed device runs no further work and
its fences read complete. Every release path drains that way (a surface upload's
and an exportable image's `Dispose`, the context's own `Dispose`), so a node
releasing its objects inside `IRenderNode.OnDeviceLost` never throws. Every frame
path waits through `SignalAndWait`, which throws, so a loss mid-frame reaches
recovery.

---

## Pipeline library

Every device a `DirectXDeviceContext` creates gets a `DirectXPipelineLibrary`,
an `ID3D12PipelineLibrary` seeded from the file kept for that adapter and
driver. The file is named from the device identity the context read when it
created the device, so an adapter whose `CheckInterfaceSupport` will not report
the user-mode driver version still keeps its library on disk, under a driver
version of zero. Every compute and graphics pipeline creation asks the library first,
the presenter's blit included, and stores what it had to create. A pipeline's
name in the library is a hash of everything that defines it: its bytecode, its
serialized root signature, and the fixed state the caller sets. A changed
kernel is therefore a new name rather than a mismatch. The context writes the
library back to disk before it releases the device, and an engine pipeline
build writes it back when it finishes (`IGpuPipelineCache.Persist`).

The runtime validates a library blob when the library is created. A blob from
another adapter or driver, or a corrupt one, is refused, reported on standard
error as `[pipeline-cache] discarded <path>: <reason>`, and the library starts
empty. A device without `ID3D12Device1`, or a library the runtime cannot create
at all, creates its pipelines uncached. A hit is a successful library load.

The file's location, the shared-root write, and the per-backend counts are the
same as on Vulkan, and so is retention: Direct3D 12 keeps its eight most
recently used library files across all its adapter directories and deletes the
rest when a device opens its file. See
[the Vulkan pipeline cache](vulkan.md#pipeline-cache).

## Constraints and invariants

- **Windows-only by construction.** Every type that touches Win32 is
  `[SupportedOSPlatform("windows8.1")]`; consumers on a platform-neutral target framework
  will (correctly) get `CA1416` until they guard or annotate.
- **Don't hand-write P/Invoke here.** To reach a new API, add its name to
  `NativeMethods.txt` and let CsWin32 generate it; then wrap it behind an `IDirectX*Api`.
- **COM lifetime is manual.** Every `IDXGIxxx`/`ID3D12xxx` pointer obtained must be
  `Release`d. The APIs use `try/finally` around transient factories and adapters; persistent
  objects are owned by an `IDisposable` `Interop` wrapper.

## Core types

| Type | Role |
|------|------|
| `IDirectXAdapterApi` / `DirectXNativeAdapterApi` | Adapter enumeration. |
| `IDirectXDeviceApi` / `DirectXNativeDeviceApi` | Feature-level probing, device creation, and the WARP software fallback. |
| `DirectXDevice` | The `IDisposable` handle owner for a created `ID3D12Device`. |
| `DirectXAdapterDescription` | A `readonly record struct` projection of one enumerated adapter's native data. |
| `DirectXFeatureLevel` | A managed mirror of `D3D_FEATURE_LEVEL`. |
| `DirectXException` / `HResultExtensions` | The failing-`HRESULT`-to-exception seam every native call funnels through. |
| `DirectXGpu*` (root and `Interop`) | The `Puck.Abstractions` GPU-contract implementations: compute pipelines, descriptor allocation, storage and geometry buffers, images, render passes and framebuffers, shared-surface export, queue submission. The image factories translate the neutral device context and pixel format once, through `DirectXGpuImageRequest.From`. |

## Verification

`tests/Puck.DirectX.Tests` checks the backend's device-free decisions: which
barrier a buffer transition records, and how the lazily created device context
reports a device the host cannot create. Its device laws run on a software
(WARP) device without the debug layer, and skip on a host without one: the
teardown's memory entries and the shader-visible heaps
(`DirectXShaderVisibleHeapsLawTests`).
Driver behavior is verified by running the engine on Direct3D 12 and by
`puck parity`, which boots the authored parity world
(`tests/Puck.Parity/parity.world.json`) offscreen once per backend and gives
each of the world's scheduled captures three verdicts: its content gate, its
exact `stateHash`, and per-tile pixels under the contract versioned beside
the world:

```powershell
dotnet test tests/Puck.DirectX.Tests/Puck.DirectX.Tests.csproj -c Release
dotnet build Puck.slnx -c Release
dotnet src/Puck.Cli/bin/Release/net10.0/Puck.Cli.dll parity
```

## Packaging

`ByteTerrace.Puck.DirectX` depends on `Puck.Abstractions` (the neutral GPU
contracts it implements) and, build-only, `Microsoft.Windows.CsWin32`
(`PrivateAssets="all"`—a source generator, never a runtime dependency of a
consumer). `Puck.DirectX.Presentation`, `Puck.World` and `Puck.Launcher.Windows`
depend on it for the Direct3D 12 backend; presentation, windowing, and shader
compilation live upstream in `Puck.DirectX.Presentation`.

## Documentation

- [Rendering](README.md)
- [Engine overview](../overview.md)
- [Contributing to Puck](../development/contributing.md)
- [API reference](../api/index.md)
