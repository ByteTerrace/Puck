# Plan: live camera as a first-class viewport content source

This document travels with the branch. Status: **M0(a)/M1a/M1b/M1c/M2a/M2b DONE & GPU-verified (2026-07-01) on an
RTX 4070 + HD Pro Webcam C920.** A live webcam is now a first-class per-viewport content source, authored in the run
document, sampled into the SDF/effects pipeline, and rendered at full engine FPS between the camera's own arrivals.
**Next up:** M3 (the GPU-resident DXVA NV12 zero-copy tier — the remaining transport optimization; the CPU tier already
renders without stalling the pump), M4 robustness extras (MJPEG, auto re-open on replug), and M5 genlock (latency
phase-align — the flavor is decided). This file is the executable summary, updated with what shipped and verified and the
concrete steps left for each next milestone.

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
- **Genlock to the camera — accepted; flavor decided: latency phase-align.** The render loop phase-locks to camera
  arrival, NOT rate-locks to it (see *Genlock* below). The chosen flavor is latency phase-align: keep the full render
  rate and bias the present deadline so a present fires as soon as possible after each camera arrival.
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

### Genlock (latency phase-align)
Genlock does **not** mean rate-locking the engine to a 30/60 fps camera — that would violate the high-FPS mandate. It
means phase-locking so a fresh camera frame reaches photons with minimum latency while the engine keeps rendering at full
VRR rate. The chosen flavor is **latency phase-align**: keep the full render rate and bias the present deadline so a
present fires as soon as possible after each camera arrival. (A harmonic/PLL lock — render at an integer multiple of
camera rate with a phase-tracking PLL — was considered and set aside; latency phase-align is simpler and keeps the full
render rate.)

It adds a **new external-clock ingestion seam** feeding camera arrival timestamps into
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
- **M1b — Live content-source node. ✅ DONE & GPU-verified (2026-07-01).** `LiveCameraNode` (`src/Puck.Demo/LiveCameraNode.cs`,
  the `--camera` / `camera` graph node) packages the M1a producer as a *persistent per-frame* source: its own bespoke
  Direct3D 12 device produces an animated `sdf-child` frame into a shared storage image every frame and hands the host a
  `Surface { SharedHandle }`; the existing Vulkan `SurfaceCompositor` imports + presents it with **no new host code**
  (verified template). Data-driven & first-class at the graph level via a new `CameraNode : NodeDocument` (`$type`
  `"camera"`, host forced Vulkan, `produce` rejected, validator guards a directx host) + a `GraphBuilder` arm — the same
  seam `showcase`/`world` use. Drains the producer each frame via `FinalizeForExport` (correctness-floor cadence; the
  keyed-mutex + ring optimization is a later milestone). `OnDeviceLost` tears down + rebuilds (new handle re-imported).
  **Verified on the RTX 4070:** `--camera --exit-after-seconds 3` opened the window, presented the live foreign-produced
  feed, and exited 0 with no crash; full solution builds; M0a/M1a gates still green; schema regenerated (drift covered).
  This is the running skeleton M2 fills with Media Foundation.
- **M1c — Sampled + per-viewport first-class. ✅ DONE & GPU-verified (2026-07-01).** A camera is now an authorable
  per-viewport source, interchangeable with an SDF camera, and **sampled** into the effects pipeline.
  - Document layer: `LiveCameraViewportSource : ViewportSource` (`$type "live-camera"`, with authored `pixelSize`/
    `quantize` effect knobs), registered by reachability; `ViewportBuilder` gained a third output (`LiveCameraSlot[]`) and
    synthesizes a **placeholder** camera per live-camera slot (its SDF render is discarded, overwritten by the pane —
    exactly the child-slot contract); `JsonSdfFrameSource` carries `LiveCameraSlots`.
  - GPU layer: `CameraPaneNode` (a HOST-device node like `ChildSurfaceNode`) opens the default webcam via the neutral
    `ICameraCaptureService`, uploads each frame with `IGpuSurfaceUpload`, and **samples** it through `resample.comp`
    (`WriteCombinedImageSampler`) into the pane's exact pixel rect — the `pixelSize`/`quantize` knobs drive the retro
    pixelation + quantization. A missing camera feeds an animated pattern through the identical path (always demoable).
    `WorldPaneChildren` centralizes the slot→node map (legacy child + one `CameraPaneNode` per live-camera viewport),
    wired at all four world host sites.
  - **Design deviation from the plan:** `WorldNode.Child` was **kept**, not dropped — it is load-bearing for the
    `--validate-world-child` cross-backend parity gate. The live-camera path layers on top of the *same* children
    machinery (a document-authored per-viewport source is strictly more general than the bool), so first-classness is
    achieved while the child parity gate keeps passing. Because the CPU-upload path is already ShaderReadOnly→sampled and the
    composite reads a same-device General-layout image, the deferred M0(b) neutral external-memory descriptor + planar/
    YCbCr and M0(c) `Surface` layout tag are **not needed on the CPU tier**; they return as M3 prerequisites (the NV12
    zero-copy path is where a `Surface` layout tag and a planar/YCbCr import descriptor actually bite).
  - **Verified on the RTX 4070 + HD Pro Webcam C920:** `--run docs/examples/world-camera.json` renders a 2x2 with three
    SDF orbit views and the live webcam in the bottom-right, upright + correctly colored, captured via
    `PUCK_CAPTURE_FRAME`. `--validate-world` / `--validate-world-child` cross-backend parity still pass; schema
    regenerated with the `live-camera` type.
  - **High-FPS follow-up (shipped):** the pane originally re-uploaded (synchronous `SubmitAndWait`) every rendered frame.
    A monotonic `FrameVersion` on the capture seam now lets `CameraPaneNode` **skip** the upload/dispatch/submit when the
    camera has not advanced, so the engine renders at full rate between the camera's ~30 fps arrivals — the high-FPS
    property for the correctness-floor tier, without the DXVA work.
- **M2a — MF capture foundation. ✅ DONE (2026-07-01); MF path pending hardware bring-up.** The neutral seam +
  Media Foundation implementation + wiring are in place and build clean; the fallback is verified here; the real capture
  awaits a machine with a webcam.
  - Neutral seam (`Puck.Platform`): `ICameraCaptureService` (`IsSupported` + `TryOpenDefault`),
    `ICameraCaptureSession : IFrameCaptureSource` (latest-frame-wins, B8G8R8A8Unorm CPU pixels), `NullCameraCaptureService`,
    `LatestFrameBuffer` (the async→sync bridge, lock-based double-copy). DI via `AddCameraCapture` (MF on Windows / Null
    else), called by the composition root parallel to `AddPlatformWindowing`.
  - MF impl (`Puck.Platform/Windows/Win32MediaFoundationCamera*.cs`): a dedicated **MTA grabber thread** owns all MF
    state — `MFStartup`, enumerate the default video-capture device, a **video-processing** `IMFSourceReader`, negotiate
    **RGB32**, then a sync `ReadSample` loop publishing each frame into `LatestFrameBuffer`. Hand-rolled COM interop
    (vtable slots declared in order, real signatures only on called methods). Degrades gracefully: any failure →
    `TryOpenDefault` returns false → fallback.
  - `LiveCameraNode` wiring: resolves `ICameraCaptureService`, opens the default camera; on success uses the CPU-upload
    tier (hand the host `Surface { Pixels }`, `SurfaceCompositor` uploads+presents — **no new host code**); otherwise the
    M1b test pattern. Logs which tier is active (**tier telemetry**).
  - **Verified on this machine (no webcam):** `--camera` — MFStartup + `MFCreateAttributes` + `SetGUID` +
    `MFEnumDeviceSources` all executed correctly (0 devices found → clean fallback), exit 0, no crash; full solution
    builds. So the enumeration interop is exercised; the device-open + frame-read path (ActivateObject, media-type
    negotiation, ReadSample, ConvertToContiguousBuffer, buffer Lock) is UNVERIFIED and is the hardware bring-up.
  - **Hardware bring-up caveats to confirm:** RGB32 row orientation (top-down vs bottom-up) and any contiguous-buffer row
    padding; sample-null stream-tick handling; refcount hygiene on the device-enum array.
- **M2b — Hardware bring-up. ✅ DONE & verified (2026-07-01, RTX 4070 + HD Pro Webcam C920).** A real feed presents.
  Added a bring-up gate `--validate-camera-live` (`CameraLiveProbeNode`) that opens the default device through the
  neutral `ICameraCaptureService`, polls until a frame arrives, and dumps `artifacts/camera-live.png`. Findings on the
  C920 via the video-processing source reader: the full capture path (`ActivateObject` → media-type negotiation →
  `ReadSample` → `ConvertToContiguousBuffer` → `Lock` → publish) works; RGB32 is **top-down** (no vertical flip needed);
  colors are correct (B8G8R8A8 → the GPU format handles the swizzle; only the raw-byte PNG probe swaps B/R); and the
  contiguous buffer is **tightly packed** (stride 2560 = 640·4, no row padding). So the M2a caveats (bottom-up / padding)
  do **not** apply to this device — the interop was correct as-written. Read-loop errors + end-of-stream are now logged
  (a disconnected device freezes the pane on its last frame rather than stopping silently).
- **M3 — Windows MF GPU-resident zero-copy tier. NEXT.** The remaining transport optimization: keep the camera GPU-resident
  end to end so a frame reaches the compositor without the host round-trip the CPU tier does. Steps: (1) a **D3D11 device
  LUID-matched** to the host adapter; (2) `IMFDXGIDeviceManager` + `MF_SOURCE_READER_D3D_MANAGER` on the reader config;
  (3) `IMFSample`→`IMFDXGIBuffer`→`ID3D11Texture2D` extraction; (4) a **persistent ring** of
  `D3D11_RESOURCE_MISC_SHARED_NTHANDLE | KEYED_MUTEX` textures; (5) per-frame `CopyResource` decode→ring with keyed-mutex
  acquire/release; (6) hand the **stable** shared NT handle into the existing `VulkanSurfaceImport`/`DirectXGpuSurfaceImport`
  (imported once per ring slot); (7) **NV12→RGB** — the external-memory format model is RGBA8/BGRA8 today, so this adds a
  planar/YCbCr import descriptor + `VkSamplerYcbcrConversion` (gap #2) or a VideoProcessor MFT pass. **Success:** per-frame
  transport = one copy + mutex + convert, no queue drain; high-FPS at 1080p measured via `PUCK_TIMING`. The M1c non-stalling
  CPU tier already keeps the pump free between camera arrivals; M3's win is dropping the per-camera-frame host round-trip +
  upload (largest at high camera resolution).
- **M4 — Format + robustness. IN PROGRESS.** Done: newest-frame-wins **late-frame drop** (the `FrameVersion` skip);
  **device-loss rebuild** through `CameraPaneNode.OnDeviceLost` (mirrors the `ChildSurfaceNode` pattern; the CPU-side
  camera session survives GPU device loss); **unplug handling** (the read loop exits + logs, the pane holds its last frame,
  no crash). Next: MJPEG webcams (vendor HW MJPEG MFT + VideoProcessor fallback), *automatic re-open* on device replug
  (currently holds the last frame; add re-enumeration), keyed-mutex timeout handling / ring sizing (land with M3's ring).
- **M5 — Genlock (latency phase-align). NEXT.** Flavor is decided: latency phase-align — keep the full render rate and bias
  the present deadline so a present fires as soon as possible after each camera arrival. Add the external-clock ingestion
  seam feeding camera arrival timestamps into `LauncherWindowHostedService.ResolveRenderPeriod` / the deadline re-anchor
  logic (the loop is internally clocked today and consumes only present timing). It becomes a three-clock system (sim
  240 Hz / display VRR / camera); reconcile the MF-100ns / QPC / present-timing domains, and use the DirectX scanout
  timestamp (`GetFrameStatistics.SyncQPCTime`) where available (the Vulkan present-timing value tracks period, not absolute
  vblank). See [[vrr-present-timing-status]].

## Open items

- **Cross-process / cross-adapter camera** is out of scope: the zero-copy ordering assumes an in-process, same-adapter
  producer. A foreign-process/adapter camera would need a shared-fence handshake that does not exist.
- **One unifying content-source host abstraction** across `WorldProducerNode` (slot-dict) and `ViewportCompositorNode`
  (pane-list) — a symmetry cleanup; can follow if the `--viewports` (non-SDF) path also grows live camera.

## Implementation notes

- The `Viewport.Camera → Source` migration fails `Parse` if a stale literal is left un-migrated (strict `Disallow`);
  the `--check-run` regression covers it.
- On M3: "zero-copy" is *one copy + keyed-mutex + convert*. If DXVA falls to software / a mismatched LUID, the node lands
  on the CPU-upload tier — the tier telemetry line names which tier is active so that is visible.
- M3's import layer is per-frame-free only with **stable** handles; import the N persistent ring handles once (rotating
  fresh handles per frame would re-import every frame).
- Latency phase-align under a free-running 30/60 fps camera and 120 Hz+ VRR shows each camera frame for a few render
  frames by design (the SDF/effects content still updates every frame) — expected, and the minimum-latency present is the
  point.
