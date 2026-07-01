# Plan: live camera as a first-class viewport content source

**Status:** the camera is a first-class, sampled, per-viewport content source — done and hardware-verified. A run
document can declare `viewports[i].source = { "$type": "live-camera", "fit": "fill" }` and the live C920 feed renders
into that viewport slot, sampled by the SDF world compositor, alongside SDF-camera viewports (see
`docs/examples/world-camera.json`). The document seam, zero-copy keystone, live `--camera` node, Media Foundation
CPU-upload capture, and the per-viewport sampled integration are all in. Remaining work is the perf and platform
milestones **M3–M6** (GPU-resident zero-copy tier, formats/robustness, genlock, cross-platform).

## Vision

A user's **live computer camera** (webcam / capture device) becomes a **content source that plugs into a viewport
exactly like any other**, and its frames are consumable as a **sampled input to the SDF/effects pipeline** (raymarch
into it, displace, pixelate, threshold, …). Two non-negotiables:

- **First-class / plug-and-play.** A camera is *authorable per viewport in the run document*, interchangeable with any
  other source — not a bolt-on special case.
- **Very high framerate** on ~RTX 4060-class hardware. Steady-state per-frame transport cost must be near-zero; the
  camera must never stall the render pump.

Target authoring: `viewports[i].source = { "$type": "live-camera", … }`; the frame renders at high FPS via zero-copy
import, is sampled by an SDF/effects stage, and the present phase-locks to camera arrival.

## Locked decisions

- **Live hardware camera, not a virtual SDF camera.** A "camera" here is a real capture device. (A virtual SDF camera
  is already first-class — one `CameraDocument`-derived `$type` — and none of this plan applies to it.)
- **Full per-viewport `ViewportSource` — schema break accepted.** `Viewport.Camera` becomes `Viewport.Source`, an
  abstract polymorphic record. Breaking a strict (`[JsonUnmappedMemberHandling(Disallow)]`) leaf record and migrating
  every document is accepted: it is the only thing that makes a camera interchangeable with a viewport source.
- **Genlock to the camera — latency phase-align, full VRR (flavor DECIDED).** The render loop keeps rendering at free
  VRR and biases the present deadline toward each camera arrival, smoothed by a light PI loop filter on the phase error —
  NOT a fixed-harmonic rate-lock (see *Genlock*). Chosen because it preserves the VRR mandate and the deterministic sim,
  extends the shipped adaptive pacer rather than adding a new control system, and tolerates the MF/Vulkan clock jitter a
  tight lock could not. A fixed-harmonic PLL is reserved as a DX-only, measurement-gated future step, not the M5 baseline.
- **Zero-copy is the high-FPS path; CPU upload is a correctness floor.** The shared-handle import layer is the target;
  the CPU-upload path is an instrumented fallback, never the silent default.
- **Windows first (Media Foundation), cross-platform seam designed up front.** Linux (V4L2) and macOS (AVFoundation)
  are later verticals; the neutral seams are shaped now so they don't force a break later.

## Architecture

### Document layer (`Puck.Scene`)

The polymorphic source hierarchy is **flattened, not wrapped** — the `orbit`/`perspective` discriminators are hoisted
onto a single `ViewportSource` base rather than nested under a wrapping source, so authoring is one flat discriminator
space and migration is a plain `camera` → `source` rename:

- `ViewportSource` (`src/Puck.Scene/ViewportSource.cs`): abstract record, `[JsonPolymorphic($type)]` with derived types
  `orbit`/`perspective`, plus `internal abstract void Validate(path, errors)`. `live-camera` slots in as a sibling
  `[JsonDerivedType]` leaf at M1c.
- `CameraDocument : ViewportSource` (intermediate abstract; keeps `Build() -> ICamera`). Orbit/perspective are untouched
  leaves.
- `Viewport.Source` (`ViewportSource?`); `Viewport.Validate` requires a source and delegates to `Source.Validate`.
- Registration is by reachability from `PuckRunDocument`; source-gen accepts the two-level hierarchy. `schema/run.schema.json`
  is regenerated via `--emit-schema`.

JSON authoring: `"source": { "$type": "orbit", … }`.

`LiveCameraSource` payload (lands at M1c): `{ DeviceId/title, RequestedWidth/Height, RequestedFps, PixelFormatHint,
Fit: sample|fill }`.

### Build + wiring (M1c — implemented; see the M1c milestone for the as-built form)

- `ViewportBuilder.Build` gives a `LiveCameraSource` slot a **placeholder** camera (its SDF view is overridden), and a
  sibling `ViewportBuilder.LiveSources` returns the slot → `LiveCameraSource` **data** map. (The builder lives in
  `Puck.Scene` and cannot construct `Puck.Demo` GPU nodes, so it emits the source data, not an `IRenderNode` — the demo
  turns each slot into a `CameraChildNode` where the world's GPU services exist.)
- The demo builds the slot → `IRenderNode` children map inside each world host node via `WorldChildren.Build`, which
  merges the live-camera slots with the legacy `--world-child` test child. `liveSources` is threaded through
  `GraphBuilder` → `CreateWorldRootNode`/`CreateWorldNode` → `VulkanComputeWorldHostNode`/`DirectXComputeWorldHostNode`/
  `CrossBackendComputeWorldNode`/`WorldProducerNode`.
- **`WorldNode.Child` was KEPT** (the plan's "delete it" was not done): the additive children-map thread is lower-risk
  and keeps the existing child/parity gates green; `LiveCameraSource` is what makes the source per-viewport
  document-describable end-to-end.

### The node (`LiveCameraNode : Puck.Hosting.IRenderNode`)

- Standard node shape: `Descriptor{Name, SurfaceId.New()}`, `ProduceFrame(in FrameContext) → Surface`, `OnDeviceLost()`,
  `Dispose`. Device via `context.Host.TryResolveCapability<IGpuDeviceContext>` (never DI); neutral services via injected
  `IGpuComputeServices` — so it runs unchanged on Vulkan and D3D12, like `ChildSurfaceNode`/`ResampleNode`.
- Owns the async→sync bridge: a **latest-frame-wins** buffer. The platform grabber thread (MF worker / V4L2 poll /
  AVFoundation callback) publishes the newest frame; `ProduceFrame` binds the current one. All threading is confined to
  the node; the downstream single-threaded pull contract is preserved. Newest frame wins, stale frames dropped, pump
  never blocks. (Today: `LatestFrameBuffer` lock-based double-copy; a lock-free triple buffer is a later optimization.)
- Follows the allocation discipline of `WorldProducerNode`: build GPU resources once, pre-size to the **max** pane
  extent and render into a sub-rect, rebind descriptors only on image-view change, use **non-blocking `Submit`** — never
  `SubmitAndWait`.

### Transport tiers (selected by capability at open; both behind the one node)

- **Zero-copy / GPU-resident (default; M3).** Windows: MF with `MF_SOURCE_READER_D3D_MANAGER` (an `IMFDXGIDeviceManager`
  wrapping a D3D11 device LUID-matched to the host adapter); DXVA HW decode yields GPU-resident NV12 textures. MF frames
  are **not born shareable**, so the node keeps a small ring (N≈3) of **persistent**
  `D3D11_RESOURCE_MISC_SHARED_NTHANDLE | KEYED_MUTEX` textures, does one on-GPU `CopyResource` decode→shared per frame,
  and hands the **stable** handle into the existing `VulkanSurfaceImport`/`DirectXGpuSurfaceImport` (import each stable
  handle **once** at open; every frame is then a cache hit). Steady-state = one `CopyResource` + one keyed-mutex
  handshake + one NV12→RGB pass, no queue drain. NV12→RGB is GPU-side (in-shader ycbcr or VideoProcessor MFT), never CPU.
- **CPU-upload fallback (correctness floor, in use today).** No D3D manager / no shareable path → `IGpuSurfaceUpload`
  of RGB32 CPU pixels. Resources are reused, but the path serializes per frame (`vkQueueWaitIdle` /
  `WaitForGpu(INFINITE)`), which caps FPS. It must (a) emit a **tier-telemetry** signal so a silent fall-to-slow (e.g. a
  DXVA→software / LUID-mismatch fallback) is diagnosable, and (b) eventually get a staging ring + fence pipelining. Its
  advertised framerate is honestly stated as a floor. **Implemented mitigation:** `CameraChildNode` uploads + resamples
  only when the device delivers a NEW frame (a monotonic `ICameraCaptureSession.FrameVersion`); on unchanged frames it
  reuses the persistent output and does no submit, so the per-frame serialization happens at the camera's own rate
  (~30 fps), not the render rate — the pump renders freely between arrivals.

### SDF/effects consumption (M1c)

The camera surface is bound as a sampled texture exactly like `ResampleNode`: a `GpuComputeBindingKind.SampledImage`
binding + `CreateSampler` + `DescriptorAllocator.WriteCombinedImageSampler`, with the surface left **ShaderReadOnly** for
that consumer. The NV12 case threads a `VkSamplerYcbcrConversion` through the descriptor allocator (new plumbing).

### Genlock (flavor DECIDED: latency phase-align, PI-filtered, full VRR; M5)

Genlock means phase-locking so a fresh camera frame reaches photons with minimum latency while the engine keeps
rendering at full VRR rate — **not** rate-locking the engine to a 30/60 fps camera.

**Decision (latency phase-align).** Keep rendering at free VRR and bias the present deadline toward each camera arrival,
driven by a **light PI loop filter on the camera-phase error** (not a naive per-arrival jerk — the PI filter steals the
PLL's phase *stability* without its rate-lock or lock-loss failure modes). Rationale over a harmonic PLL:
- **Preserves the VRR mandate** — a fixed-harmonic lock pins the render rate to a multiple of the camera *below* panel
  max, abandoning the shipped VRR closed loop; phase-align keeps the full free rate.
- **Determinism-clean** — it perturbs only the present/pacing layer (already wall-clock), leaving the 50400-tick
  fixed-point sim + snapshot replay untouched. (The PLL's "Puck-native" edge is clean *timing structure* vs the camera,
  not sim reproducibility, which neither approach changes.)
- **Incremental, not a new control system** — it extends the adaptive pacer's existing deadline re-anchor with one
  camera term, rather than adding a phase detector + loop + lock acquisition/loss + tuning.
- **Tolerates the clock jitter** — a coarse PI nudge survives MF's 100 ns worker-thread timestamps and Vulkan's imprecise
  scanout clock; a tight lock would hunt on them.

**Reserved (not the baseline): a fixed-harmonic PLL**, DX-only and behind a **measurement gate** — pursue it only if
`PUCK_TIMING` shows phase-wander is the dominant latency term a lock would remove AND a use case justifies trading free
VRR for zero camera-beat (latency-critical AR-style, camera-as-primary-content). It would lock to DirectX's
`GetFrameStatistics.SyncQPCTime` (Vulkan's present-timing clock isn't clean enough). The arrival-timestamp ingestion seam
+ phase detector built for latency-align is ~80% of the PLL substrate, so nothing is wasted if it is ever needed.

The chosen path still adds a **new external-clock ingestion seam** feeding camera arrival timestamps into
`LauncherWindowHostedService.ResolveRenderPeriod` / the deadline re-anchor logic (the loop is internally clocked today
and consumes only present timing) — a **three-clock system** (sim / display VRR / camera). Clock-domain asymmetry:
DirectX exposes a true scanout timestamp (`GetFrameStatistics.SyncQPCTime`), but the Vulkan present-timing value is not
a usable absolute scanout clock (correct for period, lags true vblank), and MF stamps in 100 ns units on a worker
thread — so the phase math is firmer on DX and needs a clock-domain reconciliation on Vulkan.

## Milestones

**Done:**

- **M0a — Document seam.** Abstract `ViewportSource` (flattened); `Viewport.Camera → Source`; every embedded literal +
  example doc migrated; schema regenerated. Every document parses and builds byte-identically.
- **M1a — Zero-copy keystone gate** (`--validate-camera`, `src/Puck.Demo/CameraValidationNode.cs`). A bespoke D3D12
  device dispatches a compute shader into an exportable storage image it owns, hands it off in External/COMMON, and the
  Vulkan host imports that shared handle zero-copy and reads back the foreign content — proving compute-dispatch *into* a
  D3D12 exportable storage image works.
- **M1b — Live content-source node** (`LiveCameraNode`, the `--camera` / `camera` graph node). Packages the M1a producer
  as a persistent per-frame source handing the host a `Surface { SharedHandle }` that the existing `SurfaceCompositor`
  imports and presents with no new host code. Data-driven via `CameraNode : NodeDocument` (`$type "camera"`) + a
  `GraphBuilder` arm. `OnDeviceLost` tears down + rebuilds (new handle re-imported).
- **M2a — MF capture foundation.** Neutral seam in `Puck.Platform` (`ICameraCaptureService` / `ICameraCaptureSession :
  IFrameCaptureSource` / `NullCameraCaptureService` / `LatestFrameBuffer`; DI via `AddCameraCapture`). MF impl
  (`Win32MediaFoundationCamera*.cs`): an MTA grabber thread owns all MF state — startup, enumerate default video-capture
  device, video-processing `IMFSourceReader`, negotiate RGB32, `ReadSample` loop → `LatestFrameBuffer`. Hand-rolled COM
  interop (vtable slots in order, real signatures only on called methods). Any failure degrades to the fallback.
  `LiveCameraNode` resolves the service and, on success, uses the CPU-upload tier; otherwise the M1b test pattern. Tier
  telemetry logs which is active.
- **M2b — Hardware bring-up (Logitech C920).** The full MF frame-read path presents a live feed through `--camera`. All
  three bring-up unknowns resolved favorably, no per-frame fix needed: RGB32 arrives **top-down** (default stride
  `+2560`) and **tightly packed** (buffer = W·H·4, no row padding), and MF `RGB32` memory order (B,G,R,X) matches
  `B8G8R8A8Unorm` (the blit forces alpha to 1.0, so the undefined X byte is irrelevant). A one-shot format-telemetry line
  (`LogFirstFrame`) reports negotiated size, buffer length vs the packed expectation, and stride sign, so a future
  device/platform that pads or delivers bottom-up is diagnosable immediately. A repeatable bring-up gate
  `--validate-camera-live` (`CameraLiveProbeNode`) opens the default device, polls until a frame arrives, and dumps
  `artifacts/camera-live.png` — 0 = pass/skip (no device is lenient), 2 = infra-fail.
- **M1c — Sampled + per-viewport first-class (hardware-verified).** A live camera is now an authorable per-viewport
  document source, sampled into the SDF world compositor. Chain:
  - **Document layer.** `LiveCameraSource : ViewportSource` (`$type "live-camera"`, `src/Puck.Scene/LiveCameraSource.cs`)
    with `{ DeviceId?, Fit: sample|fill, RequestedWidth/Height/Fps }`; the `CameraFit` enum. `ViewportBuilder.Build` gives
    a live slot a placeholder camera (its SDF view is overridden) and `ViewportBuilder.LiveSources` surfaces the slot →
    source map.
  - **Consumption node.** `CameraChildNode` (`src/Puck.Demo/CameraChildNode.cs`) uploads the newest CPU-pixel frame
    (`IGpuSurfaceUpload`) onto the **world's** device and SAMPLES it (`GpuComputeBindingKind.SampledImage` + `CreateSampler`
    + `WriteCombinedImageSampler`, the stated end goal) through `resample.comp` into a rect-sized **General-layout** storage
    image — exactly the integer-copy child contract `WorldProducerNode` already composites, so **no compositor/shader
    change**. The resample both scales the fixed-res camera to the animating slot extent and applies `Fit` (stretch vs
    center-crop via `srcOrigin`/`srcSize`) — which also removes the fullscreen-`--camera` edge-smear.
  - **Wiring.** `WorldChildren.Build` merges the document's live-camera slots with the legacy `--world-child` test child;
    `liveSources` is threaded through `GraphBuilder` → `CreateWorldRootNode`/`CreateWorldNode` → every world host node.
    **Deviation from the original plan:** `WorldNode.Child` was **kept** (not deleted) — the additive children-map thread
    is lower-risk and keeps the `--world-child`/`--validate-world-child`/parity gates green; the mandate (camera as a
    per-viewport document source) is met by `LiveCameraSource` regardless.
  - **Verified:** `docs/examples/world-camera.json` (a 2×2 split, bottom-right = `live-camera` `fit:fill`) renders the C920
    feed sampled into its viewport alongside three SDF orbit views; `--check-run` OK; the world/parity/camera gates stay
    green; full solution builds.
  - **Note — the deferred M0(b)/(c) GPU seams did NOT land here and remain deferred to M3.** The CPU-upload tier reaches
    the sampled surface via `IGpuSurfaceUpload` (host pixels → sampled image) — it needs no neutral external-memory
    descriptor, no planar/YCbCr, and no `Surface` layout tag (the camera child produces a General-layout storage image
    directly). Those seams are only needed by M3's zero-copy NV12 tier, where they now belong.

**Remaining:**

- **M3 — Windows MF GPU-resident zero-copy tier.** `MF_SOURCE_READER_D3D_MANAGER` DXVA NV12 → one `CopyResource` into
  the keyed-mutex ring → existing import path; NV12→RGB in-shader or VideoProcessor MFT. Success: per-frame transport =
  one copy + mutex + convert, no queue drain; measured high-FPS at 1080p on RTX 4060-class HW via `PUCK_TIMING` counters.
- **M4 — Format + robustness.** MJPEG webcams (vendor HW MJPEG MFT + VideoProcessor fallback), late-frame drop policy,
  ring sizing, keyed-mutex timeout handling, device-unplug recovery through `OnDeviceLost`.
- **M5 — Genlock (latency phase-align, decided).** Add the camera-arrival external-clock ingestion seam; feed arrival
  timestamps into the adaptive pacer's deadline re-anchor with a **PI loop filter on the camera-phase error** (full VRR
  preserved, sim untouched); reconcile the MF/QPC/present-timing clock domains; handle the DX-vs-Vulkan
  scanout-timestamp asymmetry (bias off DX `SyncQPCTime`; coarser correction on Vulkan). Not in scope: the reserved
  DX-only fixed-harmonic PLL (measurement-gated future step). Success: measured camera-to-photon latency drops and holds
  stable via `PUCK_TIMING`, with no VRR-rate or sim-determinism regression.
- **M6 — Cross-platform verticals.** Linux V4L2: `VIDIOC_EXPBUF` dma-buf fd → `VkImportMemoryFdInfoKHR` (`DMA_BUF_BIT_EXT`)
  + DRM format modifier + ycbcr (Vulkan-only). macOS AVFoundation: IOSurface-backed `CVPixelBuffer` → `CVMetalTextureCache`
  (native Metal) or `VK_EXT_metal_objects` (MoltenVK). Both slot into the same `ICameraCaptureService` / `LiveCameraNode`
  / triple buffer / neutral import descriptor; only the platform capture impl and external-memory handle type differ.
  Budget as real work — the external-memory API shares nothing with the FD/Metal paths except the C# interface name.

## Open items & gotchas

- ~~**Genlock flavor** (M5): latency phase-align vs harmonic/PLL lock.~~ **DECIDED: latency phase-align (PI-filtered,
  full VRR); fixed-harmonic PLL reserved DX-only behind a measurement gate.** See *Genlock* + M5.
- **Cross-process / cross-adapter camera is out of scope.** The zero-copy ordering assumes an in-process, same-adapter
  producer; a foreign-process/adapter camera would need a shared-fence handshake that does not exist.
- **One unifying content-source host abstraction** across `WorldProducerNode` (slot-dict) and `ViewportCompositorNode`
  (pane-list) — nice-to-have for symmetry; can follow M1c if the `--viewports` (non-SDF) path also needs live camera.
- **Stable-handle assumption.** The import layer's per-frame-free behavior requires **stable** handles; the M3 ring
  imports N persistent handles once. Rotating fresh handles per frame would re-import (frame-0 cost) every frame.
- **Silent tier fall-to-slow.** If DXVA falls to software / mismatched-LUID, the node lands on the stall-y upload tier;
  tier telemetry exists to make that loud.
- **Genlock duplication.** Genlocking to a free-running 30/60 fps camera under 120 Hz+ VRR visibly duplicates/drops
  camera content by design (SDF/effects content still updates every frame). Acceptable; latency-critical use is why M5
  exists.
