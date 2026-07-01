# Plan: live camera as a first-class viewport content source

This document travels with the branch. Status: **M0(a) implemented & verified (2026-07-01); M0(b)/(c) deferred to
M1.** The document-layer first-class seam is landed and gated green; the GPU-layer seams follow with their first
consumer. The scope below was validated against the current code by a fan-out reading pass and then
stress-tested by an adversarial design review that rated the first-pass design "weak" on all three axes; this file is
the corrected, executable summary. The one remaining design fork (genlock flavor) is called out as an explicit open
item to resolve when we reach it — not now.

## Vision

A user's **live computer camera** (webcam / capture device) becomes a **content source that plugs into a viewport
exactly like any other**, and its frames are consumable as a **sampled input to the SDF/effects pipeline** (raymarch
into it, displace, pixelate, threshold, …). Two non-negotiables set by the author:

- **First-class / plug-and-play.** A camera must be *authorable per viewport in the run document*, interchangeable with
  any other source — not a bolt-on special case.
- **Very high framerate** on ~RTX 4060-class hardware. The steady-state per-frame transport cost must be near-zero; the
  camera must never stall the render pump.

## Locked decisions

- **Live hardware camera, not a virtual SDF camera.** A "camera" here is a real capture device. (A *virtual* SDF camera
  kind would already be first-class today — one `CameraDocument`-derived `$type` — and none of this plan would apply.)
- **Full per-viewport `ViewportSource` — schema break accepted.** `Viewport.Camera` becomes `Viewport.Source`, a new
  abstract polymorphic record. This is a breaking change to a strict (`[JsonUnmappedMemberHandling(Disallow)]`) v1 leaf
  record and requires migrating every embedded/`--run` document. Accepted deliberately: it is the only thing that makes a
  camera *interchangeable with a viewport source*, which is the mandate.
- **Genlock to the camera — accepted, flavor deferred.** The render loop will phase-lock to camera arrival, NOT
  rate-lock to it (see *Genlock* below). The specific flavor (latency-phase vs harmonic-PLL) is the one open design item,
  resolved when we reach the pacer work (post-M3).
- **Zero-copy is the high-FPS path; CPU upload is a correctness floor.** The existing shared-handle import layer is the
  target; the CPU-upload path is a fallback that must be quarantined *and instrumented*, never the silent default.
- **Windows first (Media Foundation), cross-platform seam designed up front.** Linux (V4L2) and macOS (AVFoundation)
  are real later verticals, but the neutral seams are shaped now so they don't force a break later.

Outcome: a run document can declare `viewports[i].source = { "$type": "live-camera", … }`; the frame renders at high FPS
via zero-copy import, is sampled by an SDF/effects stage, and the present phase-locks to camera arrival.

## Why this is NOT a bolt-on (three foundational gaps)

The hard GPU assets exist; the gap is that the current seams assume "every viewport is a virtual SDF camera" and "every
shared surface is a stable, in-process, same-adapter D3D12 handle." A live camera violates both. Three foundations must
land *before* any capture code:

### 1. Per-viewport source is not document-describable today
`Viewport` hard-codes `CameraDocument? Camera` (`src/Puck.Scene/Viewport.cs`). The *only* existing non-camera source is
`WorldNode.Child` — a **global bool** (`src/Puck.Scene/NodeDocument.cs`), wired **imperatively** in
`DemoRootNode.CreateWorldNode` (the "bottom-right viewport" child surface), that requires **exactly 4 viewports**. There
is no path from a per-viewport document field to `WorldProducerNode.children[slot]`.

### 2. The zero-copy import seam is Win32-NT-handle-shaped, not neutral
`VulkanNativeExternalMemoryApi` is 100% `VkImportMemoryWin32HandleInfoKHR` (handle types `0x40` D3D12Resource / `0x2`
OpaqueWin32). It cannot express: a Linux **dma-buf fd**, a macOS **IOSurface/MTLTexture**, a **cross-device sync object**
(keyed mutex / semaphore), or a **planar/YCbCr format**. Cameras deliver **NV12** on every GPU-resident path; the current
format model is RGBA8/BGRA8 only and there is no `VkSamplerYcbcrConversion` anywhere.

### 3. `Surface` carries no layout tag, and there are two inconsistent host seams
A composited pane wants the source left in **General/UAV** layout (`WorldProducerNode.SourceViewForSlot` throws
otherwise); an SDF sampler wants **ShaderReadOnly** (`ResampleNode` + `WriteCombinedImageSampler`). `Surface` has no field
to say which, so a camera used both ways must special-case its exit layout. Separately, `WorldProducerNode.children`
(slot-keyed dict) and `ViewportCompositorNode` (ordered pane list) are two different source-hosting shapes; a
truly-first-class source wants one unifying abstraction both consume.

## Architecture

### Document layer (`Puck.Scene`) — IMPLEMENTED (M0a)
**Design deviation from the original sketch: FLATTENED, not wrapped.** Rather than a `SdfCameraSource` that *wraps* a
`CameraDocument` (which would nest discriminators — `"source":{"$type":"camera","camera":{"$type":"orbit"}}`), the
`orbit`/`perspective` discriminators are hoisted onto a single `ViewportSource` base and `CameraDocument` becomes an
intermediate grouping class under it. Authoring is one flat discriminator space and migration is a plain rename:
- `ViewportSource` (new, `src/Puck.Scene/ViewportSource.cs`): abstract record, `[JsonPolymorphic($type)]` +
  `[JsonDerivedType(typeof(OrbitCameraDocument),"orbit")]` + `[JsonDerivedType(typeof(PerspectiveCameraDocument),"perspective")]`,
  with `internal abstract void Validate(path, errors)`. `live-camera` slots in later as a sibling `[JsonDerivedType]` leaf.
- `CameraDocument : ViewportSource` (intermediate abstract; keeps `Build()->ICamera` + `ToRadians`; its polymorphic
  attributes were removed and hoisted to `ViewportSource`). Orbit/perspective are untouched leaves → byte-identical build.
- `Viewport.Camera` (`CameraDocument?`) → `Viewport.Source` (`ViewportSource?`); `Viewport.Validate` now requires a
  `Source` ("a viewport requires a source") and delegates to `Source.Validate`.
- `ViewportBuilder.Build` casts `Source is CameraDocument` (throws `NotSupportedException` for a non-camera source — the
  seam a live source's slot→`IRenderNode` output grows into at M1).
- Registration is by REACHABILITY from `PuckRunDocument` (as `CameraDocument` already was) — no `PuckSceneJsonContext`
  edit needed; source-gen accepts the two-level hierarchy (verified). `schema/run.schema.json` regenerated via
  `--emit-schema`; the viewport now carries a `source` polymorphic property.

JSON authoring today: `"source": { "$type": "orbit", … }` (was `"camera": { "$type": "orbit", … }`).

**Verification (all green):** full-solution `dotnet build Puck.slnx` clean; `--check-run` on all 6 valid example docs
builds the bit-identical 152-word scene; `world-single-bad-material.json` still cleanly rejected (exit 2, correct
error). Migrated: 7 `docs/examples/*.json` + the 2 embedded literals in `DemoRunDocuments.cs`.

Deferred `LiveCameraSource` payload (M2): `{ DeviceId/title, RequestedWidth/Height, RequestedFps, PixelFormatHint,
Fit: sample|fill }`.

### Build + wiring
- `ViewportBuilder.Build` gains a **third output** alongside `(ICamera[], NormalizedRect[])`: an
  `IReadOnlyDictionary<int, IRenderNode>` of live-source slots (SDF sources still yield an `ICamera`; a `LiveCameraSource`
  yields a `LiveCameraNode` keyed by slot).
- `RunDocument.CreateFrameSource` constructs the `JsonSdfFrameSource` **plus** that slot→node map.
- **Delete `WorldNode.Child`** (bool + its `viewportCount == 4` validation). Change
  `CreateWorldRootNode`/`CreateWorldNode`/`VulkanComputeWorldHostNode`/`DirectXComputeWorldHostNode` signatures from
  `bool withChild` to `IReadOnlyDictionary<int, IRenderNode>? children`. `GraphBuilder`'s WorldNode arm forwards the map.
  This is the change that makes the source per-viewport document-describable end-to-end.

### The node (`LiveCameraNode : Puck.Hosting.IRenderNode`)
- Standard node shape: `Descriptor{Name, SurfaceId.New()}`, `ProduceFrame(in FrameContext) → Surface`, `OnDeviceLost()`,
  `Dispose`. Device via `context.Host.TryResolveCapability<IGpuDeviceContext>` (never DI); neutral services via injected
  `IGpuComputeServices` — so it runs unchanged on Vulkan and D3D12, like `ChildSurfaceNode`/`ResampleNode`.
- Owns the async→sync bridge: an internal **latest-frame-wins triple buffer**. The platform grabber thread (MF worker /
  V4L2 poll / AVFoundation callback) publishes the newest frame index via `Interlocked`; `ProduceFrame` atomically reads
  the current index and binds that surface. All threading is confined to the node; the downstream single-threaded pull
  contract is preserved. Semantics: newest frame wins, stale frames dropped, pump never blocks.
- Follows the allocation discipline of `WorldProducerNode`: build GPU resources once, pre-size the camera image to the
  **max** pane extent and render into a sub-rect (no realloc as the split layout animates), rebind descriptors only on
  image-view change, and use **non-blocking `Submit`** on the shared queue — **never `SubmitAndWait`**.

### Transport tiers (selected by capability at open; both behind the one node)
- **Zero-copy / GPU-resident (default).** Windows: MF with `MF_SOURCE_READER_D3D_MANAGER` (an `IMFDXGIDeviceManager`
  wrapping a D3D11 device LUID-matched to the host adapter); DXVA HW decode yields GPU-resident NV12 textures. MF frames
  are **not born shareable**, so the node keeps a small ring (N≈3) of **persistent** `D3D11_RESOURCE_MISC_SHARED_NTHANDLE
  | KEYED_MUTEX` textures, does one on-GPU `CopyResource` decode→shared per frame, and hands the **stable** handle into
  the existing `VulkanSurfaceImport`/`DirectXGpuSurfaceImport` (import each of the N stable handles **once** at open, then
  every frame is a cache hit). Steady-state cost = **one CopyResource + one keyed-mutex handshake + one NV12→RGB pass** —
  no queue drain. NV12→RGB is GPU-side (in-shader ycbcr or a VideoProcessor MFT pass), never on the CPU.
- **CPU-upload fallback (correctness floor, explicitly stall-y).** No D3D manager / no shareable path → `IGpuSurfaceUpload`
  (RGB32). Resources are reused, but the path ends in `vkQueueWaitIdle` / `WaitForGpu(INFINITE)` — a full per-frame
  serialization that **will** cap FPS. This tier must (a) log/emit a **tier-telemetry** signal so a silent fall-to-slow
  is diagnosable (esp. a DXVA→software/LUID-mismatch fallback), and (b) eventually get a staging ring + fence pipelining
  so the drain leaves the pump thread. Until then its advertised framerate is stated honestly as a floor.

### SDF/effects consumption
The camera surface is bound as a sampled texture exactly like `ResampleNode`: a `GpuComputeBindingKind.SampledImage`
binding + `CreateSampler` + `DescriptorAllocator.WriteCombinedImageSampler`, with the surface left **ShaderReadOnly** for
that consumer. (The NV12 case threads a `VkSamplerYcbcrConversion` through the descriptor allocator — new plumbing, see
gap #2.)

### Genlock (deferred flavor; principle locked)
Genlock does **not** mean rate-locking the engine to a 30/60 fps camera — that would violate the high-FPS mandate. It
means phase-locking so a fresh camera frame reaches photons with minimum latency while the engine keeps rendering at full
VRR rate. Two candidate flavors, decided later:
- **Latency phase-align** — keep the full render rate; bias the present deadline so a present fires ASAP after each
  camera arrival.
- **Harmonic / PLL lock** — render at a fixed integer multiple of camera rate, with a PLL slewing render phase to track
  the camera's free-running crystal. Most Puck-native (preserves deterministic fixed-rate culture; camera arrivals land
  on known render boundaries) but more work.

Either way it adds a **new external-clock ingestion seam** feeding camera arrival timestamps into
`LauncherWindowHostedService.ResolveRenderPeriod` / the deadline re-anchor logic — the loop is internally clocked today
and consumes only *present* timing. It becomes a **three-clock system** (sim 240 Hz / display VRR / camera). Note a real
asymmetry: DirectX exposes a true scanout timestamp (`GetFrameStatistics.SyncQPCTime`) but the Vulkan present-timing
value is not a usable absolute scanout clock (correct for *period*, lags true vblank), and MF stamps in 100 ns units on a
worker thread — so the phase math is on firmer footing on the DX vertical and needs a clock-domain reconciliation on
Vulkan.

## Milestones (foundations first)

- **M0(a) — Document seam. ✅ DONE (2026-07-01).** Abstract `ViewportSource` (flattened, see Architecture);
  `Viewport.Camera → Source`; migrated every embedded literal + example doc; schema regenerated. **Success met:** every
  `--run` doc and `DemoRunDocuments.Synthesize` layout parses and builds byte-identically (152-word scene), full solution
  builds clean, negative doc still rejected.
- **M0(b)/(c) — GPU seams. DEFERRED into M1 (consumer-driven).** (b) Redefine the import seam to carry a neutral
  **external-memory descriptor** (handle-type discriminator + optional DRM modifier + per-plane info + optional sync
  object) and add **planar/YCbCr** to the format model. (c) Give `Surface` a layout discriminator (or unify on
  ShaderReadOnly). *Rationale for deferral:* both are GPU-plumbing with no consumer until M1's static zero-copy node;
  shaping them against that first real consumer avoids a speculative abstraction. They are prerequisites *within* M1, done
  before the node binds a shared surface.
- **M1a — Zero-copy keystone gate. ✅ DONE & GPU-verified (2026-07-01).** `CameraValidationNode` (`--validate-camera`,
  `src/Puck.Demo/CameraValidationNode.cs`) proves the camera *direction* of the zero-copy path: a bespoke Direct3D 12
  device (standing in for a camera's decode device) dispatches `sdf-child.comp.dxil` into an exportable **storage** image
  it owns, hands it off in the External/COMMON state, and the Vulkan **host** imports that shared handle zero-copy and
  reads it back — asserting the foreign-device content survived (spatial variation). Verified on the RTX 4070: `CAMERA
  pass`, exit 0, `px0 != px_center`. Reuses `DirectXComputeWorldDevice` + `DirectXGpuSurfaceExportFactory` +
  `IVulkanExternalMemoryApi` (import direction flipped vs `CrossShareReverseNode`). Confirms the load-bearing bet —
  compute-dispatch *into* a D3D12 exportable storage image — works.
- **M1b — Live content-source binding. REMAINING.** Package the proof as a `LiveCameraNode : IRenderNode` that hands the
  host `Surface { SharedHandle }` each frame (the existing `SurfaceCompositor` import path consumes it with **no new host
  code** — verified template) and/or binds the imported view as a **sampled** SDF/effects input
  (`WriteCombinedImageSampler`). Then the document-layer `LiveCameraSource` + `ViewportBuilder` slot→`IRenderNode` map +
  `WorldNode` rewiring (the per-viewport first-class completion). This is where the deferred M0(b) external-memory
  descriptor + planar/YCbCr and M0(c) `Surface` layout tag land against their first live consumer.
- **M2 — Windows MF live capture, CPU fallback tier.** MF async callback → latest-frame triple buffer →
  `IGpuSurfaceUpload` (RGB32). New `ICameraCaptureService` (`IsSupported` + `TryOpen`) + a `Null` fallback, **DI-registered
  parallel to `AddPlatformWindowing`** (capture has no DI registration today — add it). Ship **tier telemetry** here.
  **Success:** a real webcam renders into a viewport, sampled by SDF; known upload stall accepted and *visible*.
- **M3 — Windows MF GPU-resident zero-copy tier.** `MF_SOURCE_READER_D3D_MANAGER` DXVA NV12 → one `CopyResource` into the
  M1 keyed-mutex ring → existing import path; NV12→RGB in-shader (ycbcr) or VideoProcessor MFT. **Success:** per-frame
  transport = one copy + mutex + convert, no queue drain; measured high-FPS at 1080p on RTX 4060-class HW via
  `PUCK_TIMING` counters (which distinguish the tiers).
- **M4 — Format + robustness.** MJPEG webcams (vendor HW MJPEG MFT + VideoProcessor fallback), late-frame drop policy,
  ring sizing, keyed-mutex timeout handling, device-unplug recovery through `OnDeviceLost`.
- **M5 — Genlock.** Resolve the flavor fork (latency-phase vs harmonic-PLL); add the external-clock ingestion seam;
  reconcile the MF/QPC/present-timing clock domains; handle the DX-vs-Vulkan scanout-timestamp asymmetry.
- **M6 — Cross-platform verticals (seams already shaped in M0).** Linux V4L2: `VIDIOC_EXPBUF` dma-buf fd →
  `VkImportMemoryFdInfoKHR` (`DMA_BUF_BIT_EXT`) + DRM format modifier + ycbcr (Vulkan-only). macOS AVFoundation:
  IOSurface-backed `CVPixelBuffer` → `CVMetalTextureCache` (native Metal) or `VK_EXT_metal_objects` (MoltenVK). Both slot
  into the same `ICameraCaptureService` + `LiveCameraNode` + triple buffer + neutral import descriptor; only the platform
  capture impl and the external-memory handle type differ. Budget as real work — the current external-memory API shares
  nothing with the FD/Metal paths except the C# interface name.

## Open items

- **Genlock flavor** (M5): latency phase-align vs harmonic/PLL lock. Decide when we reach the pacer work.
- **Cross-process / cross-adapter camera** is out of scope: the zero-copy ordering assumes an in-process, same-adapter
  producer. A foreign-process/adapter camera would need a shared-fence handshake that does not exist.
- **One unifying content-source host abstraction** across `WorldProducerNode` (slot-dict) and `ViewportCompositorNode`
  (pane-list) — nice-to-have for true symmetry; can follow M1 if the `--viewports` (non-SDF) path also needs live camera.

## Risks (from the adversarial review)

- The `Viewport.Camera → Source` break fails `Parse` the instant a stale literal is left un-migrated (strict `Disallow`).
  M0's regression test is the guard.
- "Zero-copy" is really *one copy + keyed-mutex + convert*; if DXVA silently falls to software/mismatched-LUID, the node
  lands on the stall-y upload tier. Tier telemetry (M2) exists to make that loud.
- The import layer's per-frame-free behavior assumes **stable** handles; the plan keeps that by importing N persistent
  ring handles once. Rotating fresh handles per frame would re-import (frame-0 cost) every frame and break the claim.
- Genlocking to a free-running 30/60 fps camera under 120 Hz+ VRR visibly duplicates/drops camera content by design (the
  SDF/effects content still updates every frame). Acceptable; latency-critical use is why M5 exists.
