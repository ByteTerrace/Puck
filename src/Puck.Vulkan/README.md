# Puck.Vulkan

A **from-scratch Vulkan layer** for the Puck engine — no binding generator,
no third-party wrapper. It loads the native Vulkan loader itself, mirrors the structures it
needs, and exposes everything through small, interface-driven APIs so the renderer is built
(and tested) against seams rather than raw `vkXxx` calls.

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

---

## Layers

The library is six cooperating layers, one per folder. Read them as a stack: **Bindings**
at the bottom (raw data), **Factories** plus the frame presenter at the top (the entry
points you call into).

| Folder | Prefix | What lives here |
|--------|--------|-----------------|
| `Bindings/` | `Vk*` | Blittable, P/Invoke-shaped mirrors of Vulkan structs & enums (`VkResult`, `VkImageCreateInfo`, `VkPhysicalDeviceType`, …). |
| `Messages/` | `Vulkan*Request` / `…Result` | `readonly record struct` parameter / return bundles for factory & API calls. |
| `Interfaces/` | `IVulkan*Api`, `IVulkan*Factory` | The contracts — the dependency-injection / mocking seam. |
| `Apis/` | `VulkanNative*Api` | Thin implementations that marshal to the native `vkXxx` entry points, grouped by concern. |
| `Factories/` | `Vulkan*Factory` | Build `Interop` objects from request **Messages** by driving the **Apis**. |
| `Interop/` | `Vulkan*` | `IDisposable` wrappers that **own a native handle** (`VulkanInstance`, `VulkanLogicalDevice`, `VulkanSwapchain`, …). |

### The factory pattern

Every resource follows the same shape, so the whole graph is injectable and the native
calls are mockable in tests:

```
IVulkan{Thing}Factory.Create(request) ─drives→ IVulkan{Thing}Api (vkXxx) ─returns→ Vulkan{Thing}  (IDisposable handle owner)
```

An API class is constructed once and shared; a factory holds the API(s) it needs and turns
a typed request into a live, owning wrapper.

Plus a handful of top-level helpers: `VulkanException`, `VulkanResultExtensions`,
`VulkanFramePresenter`, `VulkanPhysicalDeviceSelector`, `VulkanQueueSubmitter`,
`VulkanNativeLibrary`, `VulkanMarshalHelpers`, and small value types
(`VulkanQueueFamilySelection`, `VulkanPushConstantBinding`, `VulkanVertexBufferBinding`,
`VulkanShaderStageFlags`).

---

## Capabilities

| Concern | Factory | API(s) | Interop result |
|---------|---------|--------|----------------|
| Instance | `IVulkanInstanceFactory` | `IVulkanInstanceApi` | `VulkanInstance` |
| Surface | `IVulkanSurfaceFactory` | `IVulkanSurfaceApi` | `VulkanSurface` |
| Physical device | — (`VulkanPhysicalDeviceSelector`) | `IVulkanPhysicalDeviceApi` | `VkPhysicalDevice` |
| Logical device + queues | `IVulkanLogicalDeviceFactory` | `IVulkanLogicalDeviceApi` | `VulkanLogicalDevice` |
| Swapchain | `IVulkanSwapchainFactory` | `IVulkanSwapchainApi`, `IVulkanSwapchainSupportApi` | `VulkanSwapchain` |
| Render pass | `IVulkanRenderPassFactory` | `IVulkanRenderPassApi` | `VulkanRenderPass` |
| Framebuffers | `IVulkanFramebufferSetFactory` | `IVulkanFramebufferSetApi` | `VulkanFramebufferSet` |
| Shader module | `IVulkanShaderModuleFactory` | `IVulkanShaderModuleApi` | `VulkanShaderModule` |
| Graphics pipeline | `IVulkanGraphicsPipelineFactory` | `IVulkanGraphicsPipelineApi` | `VulkanGraphicsPipeline` |
| Command buffers | `IVulkanCommandResourcesFactory` | `IVulkanCommandResourcesApi`, `IVulkanCommandBufferRecordingApi` | `VulkanCommandResources` |
| Frame sync | `IVulkanFrameSynchronizationFactory` | `IVulkanFrameSynchronizationApi` | `VulkanFrameSynchronization` |
| Storage buffer | `IVulkanStorageBufferFactory` | `IVulkanStorageBufferApi` | `VulkanStorageBuffer` |
| Vertex buffer | `IVulkanVertexBufferFactory` | `IVulkanVertexBufferApi` | `VulkanVertexBuffer` |
| Descriptors / samplers | — | `IVulkanDescriptorApi` | — |
| Frame present / readback | — (`VulkanFramePresenter`) | `IVulkanFramePresentationApi`, `IVulkanFrameReadbackApi` | `VulkanFrameReadbackBuffer` |
| Offscreen image | — | `IVulkanOffscreenImageApi` | — |
| Timestamps / stats | — | `IVulkanQueryPoolApi`, `IVulkanPipelineStatisticsApi` | — |
| Ray tracing (optional) | — | `IVulkanAccelerationStructureApi` | — |

---

## The native loader

`VulkanNativeLibrary` resolves and lazily loads the platform loader — `vulkan-1` on Windows,
`libvulkan.so.1` on Linux and FreeBSD, `libvulkan.1.dylib` on Apple platforms, and
`libvulkan.so` on Android. Override it **before the first Vulkan call** when you need a
specific loader (for example, a console SDK backend):

```csharp
using Puck.Vulkan.Interop;

VulkanNativeLibrary.LibraryPathOverride = "/path/to/libvulkan.so.1"; // throws if set too late
```

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
treated as hard failures by the frame presenter — see below.

---

## Bootstrap order

Resources must be created in a strict order. Assuming the factories are already wired up
(each holding the API dependencies it needs):

```csharp
using Puck.Vulkan;
using Puck.Vulkan.Factories;

// 1. Instance — picks the right surface extension for the display kind.
VulkanInstance instance = instanceFactory.Create(
    applicationName: "Puck.Demo",
    displayKind: NativeDisplayKind.Win32,   // from Puck.Abstractions
    enableValidation: true
);

// 2. Surface — from a native window binding (NativeSurfaceBinding, from Puck.Abstractions).
VulkanSurface surface = surfaceFactory.Create(instanceHandle: instance.Handle, binding: nativeSurfaceBinding);

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

Every `Vulkan*` wrapper is `IDisposable` and owns its handle — **dispose in reverse creation
order** (per-frame resources → pipeline → framebuffers → render pass → swapchain → device →
surface → instance). The swapchain, pipeline, and buffer factories take several more
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
  *after* the in-flight fence wait proves prior work retired — via the `recordAcquiredImage`
  callback — so recording never races in-flight GPU work.
- **Careful suboptimal handling.** `SUBOPTIMAL_KHR` is a success code with a *pending*
  semaphore signal, so the presenter renders and presents the frame anyway (the present call
  reports suboptimal again and recreation routes from there), rather than abandoning a
  semaphore mid-signal.

For the simpler "record, then read the results back" case (offscreen rendering, headless
work), `VulkanQueueSubmitter.SubmitAndWait` batches command buffers into a single submit plus
one `vkQueueWaitIdle`, and `IVulkanFrameReadbackApi` copies the rendered image back to the CPU.

---

## Optional GPU features

The logical-device factory probes and enables these only when the device fully supports them;
callers still re-probe (for example via `IVulkanAccelerationStructureApi.SupportsDevice`)
before relying on a path, and fall back otherwise:

- **Ray query** — `VK_KHR_ray_query` plus the acceleration-structure / deferred-host-ops /
  buffer-device-address bundle, with the matching feature structs chained.
- **Pipeline executable properties** — `VK_KHR_pipeline_executable_properties` for diagnostics
  (compiled register counts, etc.); pixel-neutral read-back via `IVulkanPipelineStatisticsApi`.
- **Storage-image-without-format** — `shaderStorageImage{Read,Write}WithoutFormat`, needed to
  write image views whose format (commonly BGRA8) has no GLSL format qualifier.

---

## Notes for agents

- **Don't reach for raw `vkXxx`.** Go through an `IVulkan*Api`; if a call is missing, add it
  to the relevant API interface + `VulkanNative*Api` implementation, not inline.
- **Handles are owned.** Each `Vulkan*` interop type disposes its handle exactly once. Respect
  creation / teardown order; don't double-wrap a handle.
- **`VkResult` is a value, not always an error.** Use `.ThrowIfFailed(op)` for genuine
  failures, but let the frame presenter's outcome enum drive swapchain recreation — don't throw
  on `OutOfDate` / `Suboptimal` / `NotReady`.
- **Public is intentional.** Wide visibility is a deliberate design choice for this layer; a
  large public surface is not a smell here.
- **Abstraction / shader inputs come from elsewhere.** `NativeDisplayKind` / `NativeSurfaceBinding`
  and the `IAllocator` it marshals through are from `Puck.Abstractions`; `ShaderStageInfo` is from
  `Puck.Shaders`. This library doesn't open windows, compile shaders, or pick a concrete allocator — it
  consumes those, so it has no dependency on `Puck.Platform`.
- **Bindings are spec-faithful mirrors.** A `Vk*` struct is a byte-identical ABI mirror of its
  `vulkan_core.h` counterpart (deviations are flagged `EXCEPTION` in the type's `<remarks>`);
  document and use fields by their Vulkan-spec meaning.
- See the [generated API reference](../../docs/api) for full member docs.
