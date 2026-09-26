# Vulkan backend substrate

Puck.Vulkan provides a self-contained Vulkan layer for the Puck engine. It
loads the native Vulkan loader, mirrors the structures it needs, and exposes
small interface-driven APIs so the renderer is built and tested against seams
rather than raw `vkXxx` calls.

It carries **no windowing and no shader compilation**: native window handles arrive as
`NativeSurfaceBinding`/`NativeDisplayKind` from `Puck.Abstractions`, and compiled SPIR-V arrives
as `ShaderStageInfo` from `Puck.Shaders`. This library consumes those and talks to the GPU.

Unmanaged marshaling memory comes through an injected `IAllocator` (`Puck.Abstractions`): the native
API classes take it via constructor, so this library has **no hard dependency on a concrete allocator
or on `Puck.Platform`**. The composition root binds the concrete (e.g. `services.AddPuckAllocator()`).

```text
namespaces  Puck.Vulkan (+ .Bindings, .Interop, .Interfaces, .Apis, .Factories, .Messages)
target      net10.0
deps        Puck.Abstractions (display / surface kinds, IAllocator), Puck.Shaders (shader stage info)
```

> **Everything in this library is public by design.** It is the engine's low-level GPU
> substrate; visibility is intentionally wide so higher layers and tests can reach the
> unsafe plumbing. Do not "tidy up" by reducing accessibility. Every public type and member
> is XML-documented and validated against the official Vulkan specification.

## Key features

- *No binding generator, no wrapper library:* the loader is resolved and every struct is
  mirrored by hand against the Vulkan spec, behind small, interface-driven APIs.
- *A resilient frame loop:* `VulkanFramePresenter.Present` turns swapchain lifecycle codes
  (`SuboptimalKhr`, `ErrorOutOfDateKhr`, not-ready) into an actionable outcome enum instead of
  exceptions, and only (re)records a frame's command buffer after its in-flight fence proves
  prior work retired.
- *Scored physical-device selection:* `VulkanPhysicalDeviceSelector` keeps only devices that
  expose both a graphics- and a present-capable queue family, then scores discrete over
  integrated over virtual over CPU.
- *Injected allocation, no hard `Puck.Platform` dependency:* unmanaged marshaling memory comes
  through an injected `IAllocator`, so this library has no dependency on a concrete allocator
  or platform implementation.

---

## Structure

The library is six cooperating layers, one per folder. Read them as a stack: **Bindings**
at the bottom (raw data), **Factories** plus the frame presenter at the top (the entry
points you call into).

| Folder | Prefix | What lives here |
|--------|--------|-----------------|
| `Bindings/` | `Vk*` | Blittable, P/Invoke-shaped mirrors of Vulkan structs & enums (`VkResult`, `VkImageCreateInfo`, `VkPhysicalDeviceType`, …). |
| `Messages/` | `Vulkan*Request` / `…Result` | `readonly record struct` parameter / return bundles for factory & API calls. |
| `Interfaces/` | `IVulkan*Api`, `IVulkan*Factory` | The contracts—the dependency-injection / mocking seam. |
| `Apis/` | `VulkanNative*Api` | Thin implementations that call the native `vkXxx` entry points through the instance or device command table, grouped by concern. |
| `Factories/` | `Vulkan*Factory` | Build `Interop` objects from request **Messages** by driving the **Apis**. |
| `Interop/` | `Vulkan*` | `IDisposable` wrappers that **own a native handle** (`VulkanInstance`, `VulkanLogicalDevice`, `VulkanSwapchain`, …), the command tables, and the loader. |

### The factory pattern

Every resource follows the same shape, so the whole graph is injectable and the native
calls are mockable in tests:

```text
IVulkan{Thing}Factory.Create(request) ─drives→ IVulkan{Thing}Api (vkXxx) ─returns→ Vulkan{Thing}  (IDisposable handle owner)
```

An API class is constructed once and shared; a factory holds the API(s) it needs and turns
a typed request into a live, owning wrapper.

Plus a handful of top-level helpers: `VulkanException`, `VulkanResultExtensions`,
`VulkanFramePresenter`, `VulkanPhysicalDeviceSelector`, `VulkanQueueSubmitter`,
`VulkanNativeLibrary`, `VulkanMarshalHelpers`, `Utf8StringArray`, `VulkanPipelineLayouts`, and small
value types (`VulkanQueueFamilySelection`, `VulkanPushConstantBinding`, `VulkanVertexBufferBinding`,
`VulkanShaderStageFlags`).

---

## Capabilities

| Concern | Factory | API(s) | Interop result |
|---------|---------|--------|----------------|
| Instance | `IVulkanInstanceFactory` | `IVulkanInstanceApi` | `VulkanInstance` |
| Surface | `IVulkanSurfaceFactory` | `IVulkanSurfaceApi` | `VulkanSurface` |
| Physical device |—(`VulkanPhysicalDeviceSelector`) | `IVulkanPhysicalDeviceApi` | `VkPhysicalDevice` |
| Logical device + queues | `IVulkanLogicalDeviceFactory` | `IVulkanLogicalDeviceApi` | `VulkanLogicalDevice` |
| Swapchain | `IVulkanSwapchainFactory` | `IVulkanSwapchainApi`, `IVulkanSwapchainSupportApi` | `VulkanSwapchain` |
| Render pass | `IVulkanRenderPassFactory` | `IVulkanRenderPassApi` | `VulkanRenderPass` |
| Framebuffers | `IVulkanFramebufferSetFactory` | `IVulkanFramebufferSetApi` | `VulkanFramebufferSet` |
| Shader module | `IVulkanShaderModuleFactory` | `IVulkanShaderModuleApi` | `VulkanShaderModule` |
| Graphics pipeline | `VulkanGpuPipelineFactory` (`IGpuPipelineFactory`) | `IVulkanGraphicsPipelineApi` | `VulkanGraphicsPipeline` |
| Command buffers | `IVulkanCommandResourcesFactory` | `IVulkanCommandResourcesApi`, `IVulkanCommandBufferRecordingApi` | `VulkanCommandResources` |
| Frame sync | `IVulkanFrameSynchronizationFactory` | `IVulkanFrameSynchronizationApi` | `VulkanFrameSynchronization` |
| Buffers (every usage) |—(`VulkanBuffer.Create`) | `IVulkanBufferApi` | `VulkanBuffer` |
| Descriptors / samplers |—| `IVulkanDescriptorApi` |—|
| Frame present |—(`VulkanFramePresenter`) | `IVulkanFramePresentationApi` |—|
| Offscreen image |—| `IVulkanOffscreenImageApi` |—|
| Pipeline statistics |—| `IVulkanPipelineStatisticsApi` |—|

---

## The native loader

`VulkanNativeLibrary` resolves and lazily loads the platform loader—`vulkan-1` on Windows,
`libvulkan.so.1` on Linux and FreeBSD, `libvulkan.1.dylib` on Apple platforms, and
`libvulkan.so` on Android. Override it **before the first Vulkan call** when you need a
specific loader (for example, a console SDK backend):

```csharp
using Puck.Vulkan.Interop;

VulkanNativeLibrary.LibraryPathOverride = "/path/to/libvulkan.so.1"; // throws if set too late
```

---

## Command tables

Every entry point is resolved once, when its instance or device is created, into one
table that every API reads:

- `VulkanInstanceCommands` holds the instance-level entry points, including the
  physical-device and surface queries, resolved through `vkGetInstanceProcAddr` inside
  `IVulkanInstanceApi.CreateInstance`. `VulkanInstance.Commands` owns it.
- `VulkanDeviceCommands` holds the device-level entry points, resolved through
  `vkGetDeviceProcAddr` inside `IVulkanLogicalDeviceApi.CreateLogicalDevice`, so each call
  goes straight to the driver rather than through the loader's dispatch trampoline.
  `VulkanLogicalDevice.Commands` owns it, and destroying the device disposes it.

Each table's constructor also takes the `VulkanProcResolver` it resolves through. The
host registers one resolver over the loader, whose `vkGetInstanceProcAddr` and
`vkGetDeviceProcAddr` load on its first resolution, and the native instance and device
APIs take it through their constructors; a law builds a resolver over lookups that stand
in for the driver, so its tables are built exactly as real ones are.

The APIs take the table itself, not a raw `VkDevice` or `VkInstance`, so a call is one
field load and one indirect call, with no lookup. Code that needs the raw handle reads
`Handle` from the table. Core entry points are required, and a missing one fails table
creation. An extension entry point is `null` when its extension is not enabled, and the
caller checks it. Swapchain creation, for example, checks the whole `VK_KHR_swapchain` set
once, so acquire and present call straight through.

The backend-neutral `IGpu*` services carry no device value. The renderer creates them
with itself as their device context (`IGpuDeviceContext.Services`), and each `VulkanGpu*`
adapter reads the table from that context's logical device when it makes a call.

Every buffer goes through one `IVulkanBufferApi`. A caller states the buffer's usage
(`VulkanBufferUsageFlags`), the memory it needs (`VulkanBufferMemory`), and its size;
`Create` makes the buffer, picks a memory type, allocates and binds the memory, and returns
`VulkanBufferHandles`, and `Destroy` releases them. The API also maps and unmaps the memory.
`VulkanBuffer` owns one buffer when a caller wants an owner: it maps a host-coherent buffer
once and keeps it mapped, and it implements the neutral buffer interfaces: a storage buffer,
or a geometry buffer created with the vertex and index usages its `GpuBufferUsage` declares.

Every image is a `VulkanGpuImage`, created with the Vulkan usage its declared
`GpuImageUsage` maps to (`VulkanGpuFormats.ToVkImageUsage`: a color image is also a
transfer source and destination, a depth image only a depth attachment) and viewed
through the aspect its format needs. An image whose view cannot be created is destroyed
with its memory before the failure propagates, and so is an exportable image, whose shared
handle is closed too. A `VulkanGpuRenderPass` is a `VkRenderPass` over the colors and the
optional depth attachment of a `GpuRenderPassDescription`: an attachment that loads
begins in its attachment layout, one that clears or discards begins undefined, and a
color attachment ends in its declared final layout. `VulkanGpuFramebuffer` binds one to
its images' views, and the recorder begins it with one clear value per attachment:
opaque black for a color, the declared `GpuDepthAttachment.ClearDepth` for the depth.
A transition into or out of the depth-attachment layout covers the image's depth aspect.

`VulkanGpuPipelineFactory` creates every graphics pipeline, the presenter's blit included,
from its description's groups: opaque, with one blend state per color attachment, a
depth-stencil state that tests and writes exactly when the render pass has a depth
attachment, and a dynamic viewport and scissor. `VulkanGpuRecorder` begins a render pass by
setting a viewport of negative height over the area the pass draws, so clip-space +y is the
top of the attachment as on Direct3D 12, and a scissor over the same area. The presenter's
recorder (`VulkanCommandBufferRecorder`) sets the same viewport over the swapchain image, and
its compositor creates the blit for the swapchain's render pass through
`VulkanGpuRenderPass.Borrow`, which wraps a render pass another owner keeps. `BindIndexBuffer`
and `DrawIndexed` record `vkCmdBindIndexBuffer` and `vkCmdDrawIndexed`.

| Kind | Usage | Memory |
|---|---|---|
| Storage (host-written) | `Storage` (storage, transfer source, transfer destination) | `HostCoherent` |
| Storage (device-written) | `Storage` | `DeviceLocal` |
| Indirect arguments | `Storage` with `IndirectBuffer` | `HostCoherent`, or `DeviceLocal` when the device writes them |
| Upload staging | `Storage` | `HostCoherent` |
| Geometry | `VertexBuffer`, `IndexBuffer`, or both, as declared | `HostCoherent` |
| Readback | `TransferDestination` | `HostCoherent` |

`HostCoherent` and `DeviceLocal` fail when no permitted memory type carries their
properties; `PreferDeviceLocal` falls back to the first permitted type.
`VulkanMemoryTypes.FindIndex` makes that choice for buffers and images alike.

Every child object is released through `VulkanDeviceCommands.Destroy`, which takes the
object type's `vkDestroy*` entry point from the table and skips a zero handle; its memory
overload also frees the memory bound to a buffer or image. The instance's children, the
surface and the debug messenger, are released the same way through
`VulkanInstanceCommands.Destroy`. Those two skips are the only zero-handle guards in the
backend: every owner, factory cleanup and failed-creation path passes its handles straight
through, zero or not, and a zero handle makes no native call. Surface creation refuses an
instance without `vkDestroySurfaceKHR`, so a surface that exists can always be destroyed.

The neutral `TransitionBuffer` records a `VkBufferMemoryBarrier` over the whole buffer with
the declared access and stage scopes; `MemoryBarrier` records a global `VkMemoryBarrier`.

Layouts are created and destroyed through `VulkanPipelineLayouts`. The compute pipeline API
creates its own for a pipeline described by bindings: an optional descriptor set layout
over the pipeline's bindings, and a pipeline layout over that set and an optional
push-constant range; a failed creation leaves neither alive. A pipeline described with a
`GpuPipelineLayoutDescription`, which every graphics pipeline is, takes the layout
`VulkanPipelineLayouts.Create` makes from `VulkanGroupLayouts.Plan` instead: one set layout
per planned set number, empty where no group sits, each binding with the pipeline's stage
flags, and a pipeline layout over every set layout in set order and the planned push range.
`VulkanGpuPipelineFactory` creates it before the pipeline and hands it to the pipeline API,
which neither creates nor destroys a layout it is handed, and the pipeline owns it. The
graphics pipeline API takes only a handed layout (`VulkanGraphicsPipelineCreateRequest`
requires `PipelineLayoutHandle`), and its viewport and scissor are always dynamic state the
drawing command buffer sets.

A `VkDescriptorSet` handle cannot say which group it belongs to, so the logical device's
`VulkanDescriptorSetGroups` (`VulkanLogicalDevice.SetGroups`) records it. A grouped pipeline
records its set layouts under their set numbers for as long as it lives, `AllocateSet`
records a set of one of them under that group and its pool, and `DestroyPool` forgets the
pool's sets. `BindDescriptorSet` binds a set at its group as `firstSet` on either bind point
and refuses, by name, a set bound at any other group; a set of any other layout belongs to
group 0. A group's constant buffer is a uniform buffer descriptor, its separate image a
sampled image in the shader-read-only layout, and its sampler a sampler descriptor.

---

## Marshalling

Arrays a native call reads come from `VulkanMarshalHelpers.AllocateArray<T>`, which copies
a list into one block from the injected `IAllocator`. Extension and layer names go through
`Utf8StringArray`, which owns the pointer array and every NUL-terminated string. Both turn
an allocator's `null` into an `OutOfMemoryException`. If one string of an array cannot be
allocated, the strings already allocated and the array itself are freed before the
exception propagates. Single structures a call reads are stack locals, because every
create call reads them synchronously. Every shader stage names its entry point through
`VulkanMarshalHelpers.MainEntryPoint`, one static `"main"u8` that is never freed.

---

## Result handling

Native calls return `VkResult`. Two extensions on it carry all error handling:

```csharp
using Puck.Vulkan;

result.IsSuccess();                                   // VkResult >= Success
result.ThrowIfFailed(operation: "vkCreateInstance");  // throws VulkanException on failure
```

`VulkanException` carries the failing `Operation` name and the `Result` code. Note that
swapchain status codes (`SuboptimalKhr`, `ErrorOutOfDateKhr`) and not-ready codes are **not**
treated as hard failures by the frame presenter—see below.

---

## Quick start—bootstrap order

Resources must be created in a strict order. Assuming the factories are already wired up
(each holding the API dependencies it needs):

```csharp
using Puck.Vulkan;
using Puck.Vulkan.Factories;

// 1. Instance — picks the right surface extension for the display kind.
VulkanInstance instance = instanceFactory.Create(
    applicationName: "Puck.World",
    displayKind: NativeDisplayKind.Win32,   // from Puck.Abstractions
    enableValidation: true
);

// 2. Surface — from a native window binding (NativeSurfaceBinding, from Puck.Abstractions).
VulkanSurface surface = surfaceFactory.Create(instance: instance.Commands, binding: nativeSurfaceBinding);

// 3. Physical device — scored selection (see below).
VkPhysicalDevice physicalDevice = new VulkanPhysicalDeviceSelector(physicalDeviceApi: physicalDeviceApi)
    .Select(instance: instance, surface: surface);

// 4. Logical device + queues — enables optional features when supported.
VulkanLogicalDevice device = logicalDeviceFactory.Create(instance: instance, physicalDevice: physicalDevice);

// 5. Presentation chain.
VulkanSwapchain      swapchain    = swapchainFactory.Create(/* device, surface, supportDetails, w, h */);
VulkanRenderPass     renderPass   = renderPassFactory.Create(logicalDevice: device, swapchain: swapchain);
VulkanFramebufferSet framebuffers = framebufferSetFactory.Create(logicalDevice: device, renderPass: renderPass, swapchain: swapchain);

// 6. Pipeline + per-frame resources.
VulkanShaderModule         shader   = shaderModuleFactory.Create(stageInfo: stage, logicalDevice: device);
VulkanGraphicsPipeline     pipeline = pipelineFactory.Create(/* device, renderPass, swapchain, shaders, ... */);
VulkanCommandResources     commands = commandResourcesFactory.Create(logicalDevice: device, commandBufferCount: imageCount);
VulkanFrameSynchronization sync     = frameSyncFactory.Create(logicalDevice: device, renderFinishedSemaphoreCount: imageCount);
```

Every `Vulkan*` wrapper is `IDisposable` and owns its handle—**dispose in reverse creation
order** (per-frame resources → pipeline → framebuffers → render pass → swapchain → device →
surface → instance). A factory that fails partway destroys what it created before the
failure propagates: the instance factory its messenger and instance, and the logical-device
factory its pipeline cache and device when a queue or the cache file fails.
`VulkanRenderer` builds steps 1–4 as one chain and tears down every link it built when any
link fails, so a later attempt starts clean. The swapchain and pipeline factories take several more
parameters, elided as `/* ... */`; consult the factory interface in `Interfaces/` (or the
generated API reference) for the precise signature.

---

## Physical-device selection

`VulkanPhysicalDeviceSelector.Select` enumerates devices, keeps only those that expose
**both** a graphics-capable and a present-capable queue family for the surface, and scores
the rest:

- Base score by device type: discrete `400`, integrated `300`, virtual `200`, CPU `100`.
- `+25` when a single queue family can do both graphics and present (fewer queues).

The highest score wins; it throws if no device supports both graphics and present.

---

## Memory profile

When `VulkanLogicalDeviceFactory` creates a device it reads the physical
device's memory profile beside its identity and hangs it on
`VulkanLogicalDevice.MemoryProfile`; the renderer reports it as
`IGpuDeviceContext.MemoryProfile`. `GpuMemoryProfile.FromVulkan` fills it from
the device type and the memory types and heaps of
`vkGetPhysicalDeviceMemoryProperties`:

- The device-local bytes are the sum of the heaps flagged device-local, and the
  largest device-local heap is the biggest of them.
- The host-visible device-local bytes are the biggest device-local heap that a
  memory type reaches as device-local, host-visible and host-coherent. On a
  discrete adapter that is its aperture, often 256 MiB, and it is zero when
  there is none.
- An integrated or CPU device with such a type is coherent unified memory.

The identity is recorded and never branched on. The profile is branched on in
two places. `ShaderPipelineMemoryBudget.For` bounds a shader pipeline instance's
device memory by a share of the device-local bytes (see
[the memory budget](../reference/shaders.md#memory-budget)).
`GpuResidency.Select` chooses where a region the host writes every
frame lives from the profile and the region's size. A region within
one-sixteenth (`GpuResidency.HostVisibleShare`) of the host-visible
device-local heap is written in place on coherent unified memory when no
submission reading it is in flight while the host writes, and through a
per-frame ring otherwise, which is every per-frame owner's case. Any other region, and any region on a device that
reports no host-visible device-local memory, is staged and copied by a compute
dispatch. `GpuRegion` writes a region under whichever policy was chosen, over
the neutral buffer, descriptor and recorder interfaces, so both backends share
it. A shader pipeline instance owns every region its graph writes from the host,
a package's (the overlay's buffer) and a host buffer port's (an uploaded source's),
and records their staged copies in one command buffer ahead of its frame's passes,
behind a memory barrier ordering earlier reads before the copies and followed by a
buffer barrier per copied buffer (see
[the region copy](../reference/shaders.md#the-region-copy)). Direct3D 12 fills the same profile from its own queries; see
[its memory profile](directx.md#memory-profile). `pipeline.inspect` prints the
profile and the policy chosen for a pipeline instance's parameter bytes.

`GpuResidency.RingMemory` places a ring's buffers in the device-local aperture
on a discrete adapter that exposes one: `CreateHostVisibleDeviceLocal`
allocates a `DEVICE_LOCAL`, `HOST_VISIBLE` and `HOST_COHERENT` memory type
(Direct3D 12 uses a `GPU_UPLOAD` heap), counted under `memory.vulkan` because
it is the adapter's memory. On unified memory (`GpuMemoryProfile.UnifiedMemory`:
an integrated or CPU device, or Direct3D 12's `UMA`) a ring's buffers are
ordinary host-visible buffers in the device's one pool.

---

## The frame loop

`VulkanFramePresenter.Present` runs the full **acquire → record → submit → present** dance
and turns swapchain lifecycle codes into actionable outcomes instead of exceptions:

```csharp
using Puck.Vulkan;

VulkanFramePresentationOutcome outcome = presenter.Present(
    commandResources: commands,
    frameSynchronization: sync,
    logicalDevice: device,
    recordAcquiredImage: imageIndex => {
        // Called inside the post-fence-wait window — every prior submission has retired,
        // so (re)recording *this one* image's command buffer cannot race the GPU.
    },
    swapchain: swapchain
);

switch (outcome.Result) {
    case VulkanFramePresentationResult.Presented:                     break; // outcome.ImageIndex valid
    case VulkanFramePresentationResult.Skipped:                       break; // frame not ready; try next tick
    case VulkanFramePresentationResult.RecreatePresentationResources: break; // out-of-date/suboptimal: rebuild swapchain + framebuffers
    case VulkanFramePresentationResult.ResetVulkanResources:          break; // device/surface lost: tear down and rebuild
}
```

Two design points worth knowing:

- **Lazy per-image recording.** The command buffer for the acquired image is (re)recorded
  *after* the in-flight fence wait proves prior work retired—via the `recordAcquiredImage`
  callback—so recording never races in-flight GPU work.
- **Careful suboptimal handling.** `SUBOPTIMAL_KHR` is a success code with a *pending*
  semaphore signal, so the presenter renders and presents the frame anyway (the present call
  reports suboptimal again and recreation routes from there), rather than abandoning a
  semaphore mid-signal.

For the simpler "record, then read the results back" case (offscreen rendering, headless
work), `VulkanQueueSubmitter.SubmitAndWait` batches command buffers into a single submit plus
one `vkQueueWaitIdle`, and `VulkanSurfaceReadback` copies the rendered image into a
host-coherent `VulkanBuffer` and reads it back to the CPU.

---

## Optional GPU features

The logical-device factory probes and enables these only when the device fully supports them;
callers still re-probe before relying on a path, and fall back otherwise:

- **Pipeline executable properties**—`VK_KHR_pipeline_executable_properties` for diagnostics
  (compiled register counts, etc.); pixel-neutral read-back via `IVulkanPipelineStatisticsApi`.
- **Storage-image-without-format**—`shaderStorageImage{Read,Write}WithoutFormat`, needed to
  write image views whose format (commonly BGRA8) has no storage-image format qualifier.
- **External semaphores and timeline semaphores**—`VK_KHR_external_semaphore_win32` and the
  `timelineSemaphore` feature, which let the device wait on a Direct3D 12 shared fence.
  `IGpuSurfaceTransferFactory.TryImportFence` imports the fence's NT handle into a timeline
  semaphore (`VulkanSharedFence`, `VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_D3D12_FENCE_BIT`) whose value
  is the fence's, and refuses by name on a device created without the extension.

## Waiting on another device

A Direct3D 11 producer (a camera, a desktop capture) writes into shared targets and signals a
Direct3D 12 shared fence after each write; the consumer's submission waits for the value on the
GPU, with no keyed mutex and no CPU wait on either side. The consumer adds the wait with
`IGpuQueueSubmitter.AddExternalWait` when it acquires the image, and `VulkanGpuQueueSubmitter`
puts every wait added since its last submission into the next one's wait list: the imported
semaphore, its value in a chained `VkTimelineSemaphoreSubmitInfo`, and
`VK_PIPELINE_STAGE_ALL_COMMANDS_BIT`, so no stage of the batch runs early. A submission with no
command buffers keeps the list. `VulkanQueueSubmitter`'s submits take the wait semaphores and
values directly. The order back, from consumer to producer, is the CPU slot lease the consumer
releases once its own fence has signalled.

---

## Pipeline cache

Every logical device owns a `VulkanPipelineCache`. `VulkanLogicalDeviceFactory`
creates it with the device, seeded from the file kept for that device and
driver, and passes it to every `vkCreateComputePipelines` and
`vkCreateGraphicsPipelines` call. The device writes it back to disk before it
is destroyed, and an engine pipeline build writes it back when it finishes
(`IGpuPipelineCache.Persist`). The cache is internally synchronized, so
pipelines may be created on several threads at once.

A file is loaded only when its header matches the device: header version one,
the device's vendor and device IDs, and its `pipelineCacheUUID`, which the
device identity records as `pipeline-cache.uuid`. A file that
fails that check, or that `vkCreatePipelineCache` refuses, is reported on
standard error as `[pipeline-cache] discarded <path>: <reason>`, and the cache
starts empty. A hit is read from creation feedback (core in Vulkan 1.3): a
creation the driver marks as answered by the application's pipeline cache. A
driver that reports no valid feedback counts every creation as a miss.

The file lives under the host's state root, at
`pipeline-cache/<backend>/<vendor>-<device>-<driver>/<kernel-set>.bin`. The
device segment is the device identity's `CacheKey`, the same format on both
backends: the PCI vendor and device IDs in four hexadecimal digits and the raw
driver version in sixteen (`10de-2786-000000008d8d8000`). A driver version the
backend could not read is zero and still names a file. The host names the
kernel set with a hash of the SDF kernels it ships
(`SdfWorldKernels.ContentKey`), so a driver update or a kernel change starts a
new file rather than loading one that could never hit.

Each backend keeps at most eight `.bin` files (`GpuPipelineCacheFile.RetainedFiles`),
counted across all its device directories, and evicts the least recently used.
Opening a device's file, and every creation the cache answers, sets that file's
last-write time to now. Opening then keeps the backend's eight most recently
written files, the opened file always among them even before its first write,
deletes the rest, and removes each device directory left empty. Equal times
fall to the ordinally lesser path. Several worktrees or sessions running
different commits against one state root therefore keep each other's files
instead of starting cold, while a directory named for an old driver or an old
key format ages out. Pruning never touches another backend's tree or a `.tmp`
file, and a directory holding a `.tmp` is not empty. A file another process
holds open cannot be deleted on Windows; it is reported on standard error as
`[pipeline-cache] not pruned <path>: <reason>` and left in place.

Several Worlds may share the root: a write goes to a temporary file and
replaces the cache in one rename, so a reader never sees a partial file. A
rename another process blocks leaves the old file whole, and the next write
retries. `GpuPipelineCacheFile` in `Puck.Abstractions` owns that layout, the
retention, the read and the write for both backends; `GpuPipelineCacheStore`
holds the directory and the kernel-set key.

Each backend counts its pipelines through a `GpuPipelineCacheWork`, an
`IWorkCounterSource`: `gpu.created.pipelines`, `gpu.pipeline-cache.hits`,
`gpu.pipeline-cache.misses`, and `gpu.pipeline-cache.pruned`, the `.bin` files
deleted beyond the eight (removed directories are not counted). Every pipeline
created is exactly one hit or one miss, and the counts survive device loss.
`VulkanProcResolver` counts every procedure it resolves under the
`procedures.vulkan` source: `vulkan.procedures.device-resolved` and
`vulkan.procedures.instance-resolved`, found or not. Each resolver counts into its
own `Work`, and the host's resolver is registered once, so its counts cover every
table the host builds and a device recreated after a loss adds its table again.

## Constraints and invariants

- **Don't reach for raw `vkXxx`.** Go through an `IVulkan*Api`; if a call is missing, add it
  to the relevant API interface + `VulkanNative*Api` implementation, not inline.
- **Handles are owned.** Each `Vulkan*` interop type disposes its handle exactly once. Respect
  creation / teardown order; don't double-wrap a handle.
- **`VkResult` is a value, not always an error.** Use `.ThrowIfFailed(op)` for genuine
  failures, but let the frame presenter's outcome enum drive swapchain recreation—don't throw
  on `ErrorOutOfDateKhr` / `SuboptimalKhr` / `NotReady`.
- **Public is intentional.** Wide visibility is a deliberate design choice for this layer; a
  large public surface is not a smell here.
- **Bindings are spec-faithful mirrors.** A `Vk*` struct is a byte-identical ABI mirror of its
  `vulkan_core.h` counterpart (deviations are flagged `EXCEPTION` in the type's `<remarks>`);
  document and use fields by their Vulkan-spec meaning.
- See the [generated API reference](../api/index.md) for full member docs.

## Core types

Beyond the factory/API/interop triads in [Capabilities](#capabilities):
`VulkanException`, `VulkanResultExtensions` (`.IsSuccess()`/`.ThrowIfFailed(operation)`),
`VulkanFramePresenter`, `VulkanPhysicalDeviceSelector`, `VulkanQueueSubmitter`,
`VulkanNativeLibrary` (loader resolution and `LibraryPathOverride`),
`VulkanMarshalHelpers` and `Utf8StringArray` (see [Marshalling](#marshalling)),
`VulkanPipelineLayouts`, and the small value types `VulkanQueueFamilySelection`,
`VulkanPushConstantBinding`, `VulkanVertexBufferBinding`, `VulkanShaderStageFlags`.

## Debug names

Every object the backend creates carries a debug name taken from its creator,
a `GpuObjectName` passed to the creating member of `GpuDeviceServices`. The
name joins the owner (an SDF engine, a graph instance, a package), the part of
it the object is (a table, a pipeline, a pass), an optional detail within that
part, and an index for one of several alike, usually a frame slot:
`sdf.world/viewports[1]`, `overlay/pass`, or `sdf.world/region-copies` for a
pool. A name holds no handle, counter or clock, so an object has the same name
on every run.

`VulkanGpuObjectNaming` applies names through `vkSetDebugUtilsObjectNameEXT`,
and only when the device was created with validation (`--debug-layers`) and
`VK_EXT_debug_utils`. Otherwise naming returns before it formats anything, so a
normal run builds no strings. The validation layer prints the name in
brackets after the handle, so a leak report reads
`VkBuffer 0x30000000003[law/leaked]` inside the `[vulkan-debug] validation`
line; `VulkanValidationLivenessTests` holds that.

## Verification

`tests/Puck.Vulkan.Tests` checks the backend's device-free decisions: native
marshalling under an allocator that refuses one allocation, the usage and
memory every storage and geometry buffer is created with, the usage and view
aspect an image's declared usages map to, that an image or exportable image
whose view cannot be created is destroyed with its memory, and that every handle
kind reaches the zero-handle guard through the API its owners call. That law
drives command tables built over a resolver whose destroy entry points record
rather than reach a driver. A chain law runs the renderer's boot chain through
the real factories and physical-device selector over a recording driver, fails
it at each link (instance, debug messenger, surface, physical device, device
identity, device, each queue, the pipeline-cache file), and checks that every
object created before the link is destroyed once in reverse order and that no
later link runs; a completed chain is destroyed the same way by `Dispose`. The readback, upload
staging and acceleration-structure buffers are created inside paths that also
record device commands, so only their usage constants are checked. It creates
no real instance or device. The backend itself is verified by running the engine on
Vulkan and by `puck parity`, which boots the authored parity world
(`tests/Puck.Parity/parity.world.json`) offscreen once per backend and gives
each of the world's scheduled captures three verdicts: its content gate, its
exact `stateHash`, and per-tile pixels under the contract versioned beside the
world:

```powershell
dotnet test tests/Puck.Vulkan.Tests/Puck.Vulkan.Tests.csproj -c Release
dotnet build Puck.slnx -c Release
dotnet src/Puck.Cli/bin/Release/net10.0/Puck.Cli.dll parity
```

## Packaging

`ByteTerrace.Puck.Vulkan` depends on `Puck.Abstractions` (display/surface
kinds, `IAllocator`) and `Puck.Shaders` (`ShaderStageInfo`, compiled SPIR-V).
`Puck.Vulkan.Presentation` and both launcher backends
(`Puck.Launcher.Windows`, `Puck.Launcher.Linux`) depend on it for the Vulkan
backend; it has no hard dependency on `Puck.Platform` or a concrete
allocator, and it carries no windowing or shader-compilation dependency of
its own.

## Documentation

- [Rendering](README.md)
- [Engine overview](../overview.md)
- [Contributing to Puck](../development/contributing.md)
- [API reference](../api/index.md)
