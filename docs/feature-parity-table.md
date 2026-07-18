# Backend Feature Parity: Vulkan vs Direct3D 12

Grounded comparison of `Puck.Vulkan` (+ `Puck.Vulkan.Presentation`) and `Puck.DirectX` / Direct3D 12
(+ `Puck.DirectX.Presentation`) against the neutral seam in `Puck.Abstractions`. Every row is read from the actual
code and classified, so no-op sentinels show as stubs, not features.

A capability the two APIs simply model differently (D3D12's static samplers, its Win32-only surfaces, the
single D3D12-openable shared-handle type) is marked ◆ **by design** — that cell is _correct as written_, not work to
be done. Reach for ⦿/❌/🟡 only when there is a real gap a future change could close.

Gate sweep verified on the NVIDIA RTX 4070, Win11 26200. Puck supports exactly
four GPUs — RTX 2070 (Turing), RTX 4070 (Ada, the box above), Steam Machine
(AMD RDNA3, RADV/Linux), Steam Deck (AMD Van Gogh RDNA2, RADV/Linux) — of
which only the RTX 4070 is locally testable; the other three ride on the
cross-backend parity gates plus documented driver/Mesa floors, not a local
run. See the [agent guide](agent-guide.md#gpu-support-and-shader-builds)
for the full compile/device capability-floor contract (SPIR-V 1.6 / Shader
Model 6.6 / fp16 / subgroup-size-control).

> **Note:** the offscreen proofs cited below live in the `Puck.Post` battery as the named
> `reverse-share`, `indirect`, and `resample` stages.

**Legend:** ✅ full · 🟡 partial (real, closeable gap) · ⦿ stub (no-op/sentinel/throws) · ❌ absent (not built) · ◆ by design (intrinsic API difference — correct as-is, not a gap)

### Device / Adapter / Instance
| Capability | VK | DX | Note |
|---|---|---|---|
| Device + queue creation | ✅ | ✅ | Symmetric |
| Adapter selection | ✅ | ✅ | VK scored selector; DX selects purely by caller LUID |
| LUID extraction + cross-backend match | ✅ | ✅ | VK is the LUID producer, DX the consumer |
| WARP / software fallback | ◆ | ✅ | DX exposes `EnumWarpAdapter`; Vulkan has no software-adapter concept in its model — ◆ by design, not a feature VK is missing |
| Feature-level / version probing | ✅ | ✅ | Vulkan probes `vkEnumerateInstanceVersion`, requires API 1.3 for the engine's SPIR-V 1.6 kernels, and separately checks the selected device's `apiVersion`. Direct3D 12 walks feature levels 12.2→11.0 and enforces Shader Model 6.6 after device creation. |
| Lazy/deferred device + `Func<luid>` | ◆ | ✅ | DX defers device creation to await the cross-backend match; VK is the LUID *producer*, so it has nothing to wait on — ◆ by design, not a missing path |
| Validation / debug layer | ✅ | ✅ | Both fully wired and drained to the console (`[vulkan-debug]` / `[d3d12-debug]`). **VK validation is on by default**; **DX validation is opt-in via `PUCK_D3D12_DEBUG`, off by default** — `EnableDebugLayer` poisons the next `D3D12CreateDevice` (`0x887A0007`) on this RTX 4070 / Win11 26200, so the layer (and any `[d3d12-debug]` output) only exists when the env var is set. With it set, the full gate sweep **and** the live cross-API present are clean (the only `[vulkan-debug]` line is an unrelated Epic/EOS duplicate-layer warning). A non-NVIDIA driver under-advertises the D3D12-resource handle as importable and logs two import errors there despite the import working — driver-specific, not a code defect. |
| Debug message callback | ✅ | ✅ | VK `VK_EXT_debug_utils` messenger (registered when validation is on, destroyed before the instance); DX `ID3D12InfoQueue` drained to the console on every `WaitIdle` |

### Presentation / Swapchain
| Capability | VK | DX | Note |
|---|---|---|---|
| Windowed present | ✅ | ✅ | Symmetric `ISurfacePresenter` |
| Swapchain resize | ✅ | ✅ | VK also handles out-of-date/suboptimal |
| Present-mode select (vsync/mailbox/immediate) | ✅ | ✅ | Neutral `PresentationOptions.PresentMode` (Vsync/Mailbox/Immediate) honored by both — VK maps to FIFO/MAILBOX/IMMEDIATE and feeds the selector; DX maps to the `Present` sync interval (1/0) plus an `ALLOW_TEARING` swapchain+present for Immediate where the display supports it |
| Adaptive present policy | ✅ | ✅ | Neutral `PresentMode.Adaptive` — VK maps to `FIFO_RELAXED` (adaptive vsync; tears only on a late frame); DX takes the same `ALLOW_TEARING` swapchain+present path as Immediate where the display supports it. This policy does not itself claim that the display advertises VRR; that is reported independently by `IDisplayTimingInfo`. `--present-mode adaptive` |
| Signal timing + advertised VRR capabilities | ✅ | ✅ | Backend-neutral `IDisplayTimingInfo`; Win32 reads the active physical signal rational from `QueryDisplayConfig`, maps each target to its effective EDID through SetupAPI, and recognizes explicit DisplayID Adaptive-Sync, HDMI Forum VRR, and AMD FreeSync ranges. Fixed desktop modes and generic EDID frequency limits are never called VRR. Cloned targets are intersected; non-Win32 providers currently report unknown. |
| Adaptive (display-aware) pacer | ✅ | ✅ | `LauncherWindowHostedService` uses `max(real Vmin, min(real Vmax, active signal) − 3 Hz)` only for positively advertised VRR. Unknown/unsupported VRR falls back to the active physical signal without an invented floor; explicit targets are capped only at that physical signal, not at an unrelated adaptive bound. Display changes discard stale facts and retry transiently unavailable topology queries. `IPrecisionWaiter` supplies the high-resolution wait. Presentation-only — fixed-step simulation is untouched. |
| Closed-loop present-timing feedback | ✅ | ✅ | Neutral `IPresentTimingFeedback` returns a display-confirmed present timestamp (QPC≡Stopwatch ticks); the pacer phase-locks `nextRenderDeadline` to it (else open-loop). DX reads `IDXGISwapChain::GetFrameStatistics` after Present (DISJOINT → unavailable). VK chains `VkPresentIdKHR` + waits via `vkWaitForPresentKHR` (`VK_KHR_present_id`/`present_wait`), gated on device support — when absent it stays open-loop. Both fall back gracefully; render-side only |
| Surface-format select | ✅ | ✅ | Neutral `PresentationOptions.SurfaceFormat` honored by both — VK picks the supported surface format matching the desired `VkFormat`; DX sets the swapchain, resize, and blit-PSO render-target format |
| Host non-Win32 windows (Wayland/Xcb) | ✅ | ◆ | D3D12 is a Win32 API — Wayland/Xcb aren't in its model; throws on non-Win32 surfaces — ◆ by design, not an unfinished port |
| Live VK↔DX backend swap | ✅ | ✅ | `BackendSwitcher`, backend-neutral. |
| Windows compositor window/monitor capture | ✅ | ✅ | Windows Graphics Capture (Windows 10 2004/build 19041+) owns production, fixed output extent/cadence, and bounded lifetime. Two transports carry the frame to the host: the **Vulkan** host samples completed latest-result-wins CPU BGRA frames through the neutral surface upload; the **Direct3D 12** host instead provisions round-robin shared simultaneous-access textures into which WGC copies each frame (same-adapter Direct3D 11→Direct3D 12 `CopyResource`, event-query drained), sampled directly with no CPU round-trip, while the CPU readback continues at a divided cadence for the glow tap. The same feed captures a whole monitor via `CreateForMonitor` (primary-first 0-based index), reusing both transports. Non-Windows hosts report unsupported. The Tier-B `capture` POST stage verifies the CPU path (observable pixels, nonblocking consumption, hostile target states, resource reclamation, lenient primary-monitor scenario); the Tier-C `capture-share` stage verifies the Direct3D 12 shared-texture transport end to end. |

### Graphics / Raster
| Capability | VK | DX | Note |
|---|---|---|---|
| Graphics pipeline (vtx+frag) | ✅ | ✅ | DX hardcodes topology/cull/blend; VK from request |
| Offscreen render target | ✅ | ✅ | Both sampleable |
| Render pass / framebuffer | ✅ | ✅ | DX uses first-class render passes (`BeginRenderPass`/`EndRenderPass` with a PRESERVE store op) on Windows 10 1809+ (`ID3D12GraphicsCommandList4`); `OMSetRenderTargets` emulation is the fallback below. The RENDER_TARGET state transition stays a barrier either way — D3D12 render passes do not move resource state |
| Draw verbs + dynamic scissor | ✅ | ✅ | `BindGraphicsPipeline`/`BindVertexBuffer`/`BindDescriptorSet`/`SetScissor`/`Draw` on both. Scissor is dynamic (VK `vkCmdSetScissor` + dynamic state; DX `RSSetScissorRects`); `Draw` → `vkCmdDraw` / `DrawInstanced`. Topology is fixed triangle-list and there is no neutral `SetViewport` — VK bakes the viewport into the pipeline, DX re-sets it from the render-pass extent |
| Graphics-pipeline texture + storage-buffer binding | ✅ | ✅ | Both bind N image-samplers + a storage buffer to the **graphics** pipeline. VK: combined-image-sampler array + `STORAGE_BUFFER` (vtx+frag). DX: SRV descriptor table (`t0..tN-1`) + a static sampler at `s0`, with the storage buffer as a read-only SRV `StructuredBuffer` (the upload heap forbids UAVs and `u0` is the render target) |
| Depth / stencil | ❌ | ❌ | Unmodeled (the SDF showcase needs none) |

### Compute
| Capability | VK | DX | Note |
|---|---|---|---|
| Compute pipeline + dispatch | ✅ | ✅ | DX folds set-layout/layout/pipeline into one token |
| Indirect compute dispatch | ✅ | ✅ | `IGpuComputeRecorder.DispatchIndirect` reads the (x,y,z) group counts from a GPU buffer (`CreateIndirectArgs`): VK `vkCmdDispatchIndirect`; DX `ExecuteIndirect` + a cached DISPATCH command signature. The 12-byte `VkDispatchIndirectCommand`/`D3D12_DISPATCH_ARGUMENTS` layout is identical. GPU-verified by the `indirect` stage (`Dispatch` == `DispatchIndirect` **bit-for-bit** on both backends) and wired into the live render path — `WorldProducerNode`'s Stage-2 composite dispatches indirectly from a host-written args buffer; the captured `world` run-document frame is byte-identical to the direct-dispatch capture on both backends, and the Post `world` parity gate stays green. (Indirect *draw* is absent — see below.) |
| GPU-driven cull (indirect dispatch from a GPU-computed grid) | ✅ | ✅ | `WorldProducerNode`'s beam prepass + a single-thread cull-args reduction compute the surviving-tile bounding box **on the GPU** and write it into the Stage-1 "views" **indirect** dispatch args (a device-local buffer) plus a bbox-origin buffer; the SDF march then covers only that bbox (the all-empty sky margin is never dispatched) and the source-agnostic compositor flattens the remaining empty tiles to a constant — no CPU readback in the frame loop. GPU-verified: the Post `world` / `world-child` stages stay green cross-backend (≤ ±1-LSB), a live `world` run document on `--backend directx` runs without device-removal, and with GPU timing armed the views pass is confirmed bounded to the bbox. |
| Storage image | ✅ | ✅ | VK STORAGE+SAMPLED; DX UAV texture |
| Device-local GPU-writable buffer | ✅ | ✅ | VK's `CreateDeviceLocal` allocates **device-local** (not host-visible) backing memory — a GPU-only storage buffer never host-mapped, matching the D3D12 default-heap UAV buffer |
| Image-layout transition | ✅ | ✅ | DX uses **Enhanced Barriers** (`OPTIONS12`) — a texture barrier carrying real sync + access scopes (from the neutral stage/access masks) and first-class layouts (the neutral `oldLayout` honored directly; `Undefined` → `LAYOUT_UNDEFINED` + discard), the Vulkan image-layout peer. The legacy resource-state barrier (per-resource state dict) is the fallback when Enhanced Barriers are unsupported |
| Memory / UAV barrier | ✅ | ✅ | DX uses an **Enhanced Barriers** global barrier carrying real sync + access scopes from the neutral masks (the Vulkan `VkMemoryBarrier` peer); the legacy scopeless UAV barrier is the fallback |
| Inline ray tracing / acceleration structures | ✅ | ✅ | **Both**: a per-frame TLAS over the SDF scene drives ray-query culling + soft shadows inside the SDF march, from one neutral node (proven by the `world-ray-query` stage). VK `VK_KHR_ray_query`; DX DXR 1.1 (`ID3D12GraphicsCommandList4`). Neutral seam: `GpuComputeBindingKind.AccelerationStructure` + `IGpuAccelerationStructure` |
| Descriptor-array binding (Count>1) | ✅ | ✅ | `GpuComputeBinding.Count` — both write per array element |
| GPU performance counters (timestamp queries) | ✅ | ✅ | Neutral timing seam (`IGpuTimingPool`/`Factory`/`Recorder` + `GpuTimestampCapabilities`). VK wraps the `VkQueryPool` timestamp API; DX uses `ID3D12QueryHeap` + `ResolveQueryData`→READBACK buffer + `GetTimestampFrequency`. `WorldProducerNode` brackets per-pass GPU-ms + share-of-frame (with GPU timing armed), double-buffered read (no stall), pixel-neutral. Both report period 1ns/64-bit on the RTX 4070; views pass ~85% (march-bound). The foundation for performance validation. Pipeline-statistics queries are a future extension behind the same seam |
| Host the compute world on-screen (same-device) | ✅ | ✅ | Both host `WorldProducerNode` on their own device and blit its storage image to their own swapchain — **no cross-API import**. Vulkan is the default; a `world` run document on `--backend directx` runs it same-device on the D3D12 host (`DirectXComputeWorldHostNode`). Cross-backend *composition* — one backend's content presented by the other host — is also live in **both** directions (zero-copy import); see asymmetry 1. |

### Descriptors / Samplers
| Capability | VK | DX | Note |
|---|---|---|---|
| Storage-image / buffer writes | ✅ | ✅ | — |
| Combined-image-sampler write | ✅ | ◆ | DX writes only the SRV; the sampler is static in the root sig — ◆ idiomatic D3D12, not a defect |
| Sampled-image binding in **compute** seam | ✅ | ✅ | A read-only texture filtered through a sampler inside a compute kernel (`GpuComputeBindingKind.SampledImage`) — the compute analogue of the graphics texture binding, for scaling/filtering an arbitrary-resolution source into a differently sized destination. VK: a combined-image-sampler set-layout binding (the existing `WriteCombinedImageSampler` path) with a `CreateSampler(filter)`-chosen `VkSampler`. DX: an SRV (`t0`) plus one CLAMP **static sampler** baked into the compute root signature at `s0` (filter from the pipeline's `samplerFilter`). GPU-verified by the `resample` stage: a nearest identity resample == source bit-for-bit and a 2x linear upscale matching cross-backend (≤ ±1 LSB) on both backends. The foundation for filtered / arbitrary-res viewport sources. |
| Dynamic sampler object | ✅ | ◆ | DX `CreateSampler` returns sentinel `1`, Destroy is a no-op — samplers are static in D3D12 — ◆ by design, not a stub to fill in |
| Multiple independent sets per pool | ✅ | ✅ | **D3D12 `AllocateSet` sub-allocates the heap (bump cursor + per-layout slot span)** |
| Push / root constants | ✅ | ✅ | Small inline pipeline data via the neutral `GpuPushConstantBinding` (compute **and** graphics recorders). VK native `vkCmdPushConstants` (range in the pipeline layout); DX maps it to root 32-bit constants at `b0` via `Set{Graphics,Compute}Root32BitConstants` (byte offset/size → dword counts) — idiomatic mechanism difference, functionally complete on both |

### Surface Sharing / Transfer
| Capability | VK | DX | Note |
|---|---|---|---|
| Exportable render target + storage image | ✅ | ✅ | — |
| Shared-handle **export** | ◆ | ✅ | VK exports OPAQUE_WIN32 only (Vulkan-openable); a D3D12-openable export isn't in Vulkan's model, so cross-backend sharing routes through a D3D12-owned resource instead — ◆ by design. DX's handle is openable by both |
| **Import a foreign backend's handle** | ✅ | ◆ | VK imports D3D12 handles (sampled **and** writable storage); DX has no Vulkan-handle path because Vulkan's OPAQUE_WIN32 isn't D3D12-openable — ◆ by design. The VK→D3D direction is reached instead by making the resource **D3D12-owned** and having Vulkan import it writable, not by D3D opening a VK handle |
| GPU readback / CPU upload | ✅ | ✅ | — |

### Buffers / Shaders / Memory
| Capability | VK | DX | Note |
|---|---|---|---|
| Host-visible / device-local buffers | ✅ | ✅ | Both host-visible and device-local real on each backend |
| Vertex buffer (create + upload) | ✅ | ✅ | Create-and-upload in one call (no separate `Write`): VK buffer + host-visible/coherent `VkDeviceMemory` (map/copy); DX committed UPLOAD-heap resource (map/copy), the handle carrying the `D3D12_VERTEX_BUFFER_VIEW` |
| Shader module | ✅ | ✅ | VK compiles SPIR-V; DX pins DXIL/DXBC blob |
| Bytecode validation + content-hash | ✅ | ✅ | Both backends' `Create` validates the bytecode format up front (`ShaderBytecode.ValidateFormat` — SPIR-V / DXBC-container magic + size), rejecting malformed bytecode instead of forwarding it to the driver; content-addressed caching of file loads is the shader loader's role |
| Pooled (VMA-style) device allocator | ❌ | ❌ | Neither pools; raw alloc per resource |

### Absent on both (shared ceiling, not a portability gap)
| Capability | VK | DX | Note |
|---|---|---|---|
| Indirect *draw* | ❌ | ❌ | Indirect compute **dispatch** is wired and at parity (see Compute); indirect *draw* (`vkCmdDrawIndirect` / graphics `ExecuteIndirect`) is unbuilt — no GPU-driven-geometry consumer in the SDF showcase to verify it against |
| Async compute / multi-queue / timeline | ❌ | ❌ | Single graphics queue, binary fences |

## The asymmetries that actually matter for plug-and-play

1. **Cross-API zero-copy primitives are verified in both producer directions.** Both directions use a
   **Direct3D 12-owned** shared resource because a Direct3D 12 `CreateSharedHandle` NT handle is the only handle
   type both backends can open. `camera-share` verifies Direct3D 12-produced content imported by Vulkan;
   `reverse-share` verifies Vulkan writing into a Direct3D 12-owned resource that Direct3D 12 reads back. The
   run-document `world` graph currently renders on its host backend; preflight rejects a `graph.produce` value that
   disagrees with `host.backend` until the shared world renderer re-hosts the cross-backend producer path.
2. **The D3D12 descriptor model — one idiomatic divergence remains.** Static samplers, no dynamic sampler object
   (idiomatic D3D12, not a defect). `AllocateSet` bump-allocates each set its own heap region, so N independent sets per
   pool behave like Vulkan.
3. **Either backend can host the window and the SDF world on the same device.** Cross-backend resource exchange is
   available through the neutral sharing primitives and verified by Post, but is not currently exposed by the
   run-document `world` graph.
4. **Absent on *both* (a shared ceiling, not a portability gap):** indirect *draw* (indirect compute *dispatch* is
   wired and at parity — see the `indirect` stage), async/multi-queue/timeline, depth/stencil, and a VMA-style pooled
   allocator. The sampled-image compute binding is **not** in this list — it is wired and at parity on both
   (see the `resample` stage and Descriptors / Samplers). Hardware ray tracing is **not**
   in this list either — it is present and at parity on both via the inline ray-query world.
