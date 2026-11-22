# s1-research-grounding: residency and memory as the code holds them

Read at the integration tip when the round ran; line citations drift, symbols do not. Read-only lane:
no source edited, no commit, no World started (see "What was not read" at the end).

Labels: **[opened]** = read in source at this tree, `file:line` as the Read/Grep output printed it;
**[inference]** = my reasoning from opened code; **[unverified]** = nobody in this lane checked it.
All paths are under `src/` unless stated.

---

## 1. Residency today: every place that decides what is baked, loaded, landed, kept or released

### 1.1 `SdfMeshDemand` (Puck.SdfVm/SdfMeshDemand.cs) — the demand points

- **Driven by:** the residency's captured frame's views, once per capture: `SdfWorldResidency.Capture`
  calls `MeshDemand.Observe(frame: frame, height: m_height);` then `m_meshDresser.Dress(frame: frame)`
  (SdfWorldResidency.cs:921-922) **[opened]**. `Observe` skips a view that is `Elsewhere`, has no region height,
  or a non-finite eye (SdfMeshDemand.cs:66), and takes pixels-a-metre from `view.DemandHeight` else the region's
  share of the output height (SdfMeshDemand.cs:68). A view with `DemandWithinView` demands only inside its widened
  frustum (`WithinViewMargin = 0.5f`, SdfMeshDemand.cs:29; `Demands`, :221-227) **[opened]**.
- **One demand object per residency:** `public SdfMeshDemand MeshDemand { get; } = new();`
  (SdfWorldResidency.cs:182), handed to the bake (`new SdfMeshPathBake { Demand = MeshDemand, ...`, :154) and to
  the tables (`tables.MeshDemand = MeshDemand;`, :534) **[opened]**. No cross-residency union.
- **What it decides:** ordering only (`Priority`, :159-173: diagonal past a pixel → `1f + density`, else the pixel
  share), and the pixel a tier is read at (`PixelAt`, :111-127). No velocity lead: `Observe` stores
  `camera.Position` only (:67) **[opened]**. A root camera's `Demands` is always true (distance alone, no frustum,
  no occlusion) (:221) **[opened]**.
- **Bound:** none; it is a ranking. **Under pressure:** nothing.

### 1.2 `SdfMeshTier` + `SdfMeshPathBake.Retier` (SdfMeshTier.cs, SdfMeshPathBake.Tiers.cs) — detail by demand, inside the region budget

- **Driven by:** `MeasureDemands` measures only the groups whose demand moved: set, moved by their slot or copy, or
  made due by the demand points' travel (`SdfMeshPathBake.Demand.cs`, `SdfMeshDemand.Travel`, `SdfMeshTier.Slack`),
  and every group over `MeasureFrames` frames after a change no travel bounds; `Retier` decides once a measure (`if (++m_decideFrames < SdfMeshTier.MeasureFrames) { return; }`,
  Tiers.cs:234) **[opened]**. Demanded tier = coarsest rung under half a pixel (`PixelShare = 0.5f`, SdfMeshTier.cs:18;
  `Of`, :84), held to `Finest`/`Coarsest` rungs (`Clamp`, :104) **[opened]**.
- **Bound:** `RegionBudgetBytes => ((m_regionLimitBytes == 0L) ? long.MaxValue : ((m_regionLimitBytes / 8L) * SdfMeshTier.BudgetEighths));`
  (Tiers.cs:65), `BudgetEighths = 7` (SdfMeshTier.cs:54). The limit is read each frame from the residency's own tables
  in `ObserveResidency` (`m_regionLimitBytes = ((long)tables.MeshRegionLimitBytes);`, SdfMeshPathBake.Residency.cs:351)
  **[opened]**. So the budget is per residency and is **unbounded before the first `ObserveResidency`** (long.MaxValue).
- **Under pressure:** finer tiers held back (`heldBack`, Tiers.cs:354-357); spare groups coarsened largest saving first
  (Tiers.cs:365-377); when nothing is spare for `HoldFrames`, every demand is biased half a rung, up to
  `MaxBias = 16` (SdfMeshTier.cs:47; Tiers.cs:379-388); bias taken back after `BiasQuietFrames = 600`, doubling on a
  bounce (SdfMeshTier.cs:51; Tiers.cs:386, 389-397) **[opened]**. **Nothing is ever dropped**: a group always draws one
  complete tier; there is no eviction of a group, only coarsening (FinishReplacements retires only a *replaced* group,
  Tiers.cs:193-196) **[opened]**.
- **In flight:** at most `MaxInFlight = 64` replans (SdfMeshTier.cs:38) **[opened]**.

### 1.3 `SdfMeshPathBake` admission and its worker pool (SdfMeshPathBake.cs, SdfMeshPathBake.Residency.cs) — what bakes when

- **Driven by:** `Prioritize` sets `group.Priority = MeasureDemand(group, priority: true)` for undone groups and raises
  a leader to its followers' max (src/Puck.SdfVm/SdfMeshPathBake.Residency.cs:90-106); the shared pool takes the highest-priority queued work
  (`TakeWork`, src/Puck.SdfVm/SdfMeshPathBake.Residency.cs:290-309) **[opened]**. Lattice batches take candidates in `ByRank` order
  (SdfMeshPathBake.cs:662) **[opened]**.
- **Bound (CPU memory):** a group starting its final lattice reserves its peak from the process account:
  `if (!group.Memory.TryReserve(available: available, peak: peak)) {` (SdfMeshPathBake.cs:709), paced by
  `HostMemory.AvailablePhysicalBytes()` read at most every `AvailableSeconds = 0.25` (:760-774) **[opened]**. The owner
  is `MemoryAccount.Shared.CreateOwner("mesh-bake-group")` (:2396) **[opened]**.
- **Under pressure:** deferred while another group of the bake is admitted; with none admitted it is admitted
  regardless (`group.Memory.Reserve(peak: peak);`, :718), so a bake always progresses (:715-719) **[opened]**.
- **Worker pool:** one static pool for every residency's bake, `Workers => Math.Max(val1: 1, val2: Environment.ProcessorCount)`
  (src/Puck.SdfVm/SdfMeshPathBake.Residency.cs:53), started with `Task.Run` (:223) **[opened]**. This is not `Puck.Hosting.Jobs.JobPool`.
- **Release:** `Retire()` marks every group retired and drops queued work (src/Puck.SdfVm/SdfMeshPathBake.Residency.cs:252-260), called from
  `SdfWorldResidency.Release` (SdfWorldResidency.cs:396) **[opened]**.

### 1.4 `SdfMeshPathBake.ObserveResidency` / `ServeAhead` — when a product takes the draws

- Reads the region limit, named bytes and refusals every frame (src/Puck.SdfVm/SdfMeshPathBake.Residency.cs:349-353), names products ahead to the
  tables (`tables.NameMeshesAhead(meshes: m_aheadMeshes);`, :409), hands draws once every piece `MeshStands`, or once
  `SdfMeshTier.HandFrames = 30` frames have passed (:431; SdfMeshTier.cs:43) **[opened]**. A piece the region refused
  ahead takes the draws "and the piece waits out of the frame as a first mesh does" (comment, :389-390): i.e. that
  piece is not drawn until room appears **[opened]**.

### 1.5 `SdfMeshLibrary` (SdfMeshLibrary.cs) — the shared CPU mesh cache

- **Shared:** one library per presentation, passed to every residency (`meshLibrary: MeshLibrary` in
  WorldScreenBinder.Device.Views.cs:27 and Device.Routed.cs:154) **[opened]**.
- **Bound:** `public long ByteBudget => Math.Max(val1: 0, val2: Math.Min(val1: (byteBudget ?? Account.CacheBudget), val2: (Account.Read().Budget / 16)));`
  (SdfMeshLibrary.cs:33); `CacheBudget => Math.Clamp((DefaultBudget / 12), (16L << 20), (512L << 20));`
  (MemoryAccount.cs:63) **[opened]**. Weight is each mesh's full `StorageBytes` (`{ Bytes = mesh.StorageBytes }`,
  SdfMeshLibrary.cs:315).
- **Under pressure:** LRU eviction (`m_held.TrimTo(weight: ByteBudget);`, :324, :329) and full clear on the account's
  trim (`Trim`, :390-398) **[opened]**. Eviction releases only the library's cache claim; a residency's claim keeps the
  payload alive, but the library then no longer *finds* it, so a later `TryLoad` decodes a second copy
  (`TryTake` misses at :268-271, `TryLoad` falls to `Decode` at :200) **[opened]**. The open item's claim stands
  (its cited lines drifted, §5).

### 1.6 `SdfMeshStore` (SdfMeshStore.cs) — disk

- **Bound:** `DefaultCapacityBytes = (8L << 30)` per store (SdfMeshStore.cs:34), whole identity sets evicted LRU
  through `CacheRetention.EnforceSetsOnWrite` (doc, :19-29) **[opened]**. A disk bound, not memory.
- Reads charge `MemoryAccount.Shared.CreateOwner(name: "mesh-entry")` transiently (:188, :250) **[opened]**.

### 1.7 Restorable mesh parts (SdfMesh.Restore.cs) — what CPU payload a landed mesh lets go

- A store-published native mesh frees its normals and coarser levels' indices when its last need goes
  (`Unneed` → `FreeUnderGate`, SdfMesh.Restore.cs:146-150, 177-184) and restores them from the store on the next need
  (`Need` → `Enqueue`, :138-145; `NeedResident`, :154-175) **[opened]**. A failed restore makes the group rebake
  (`SdfMeshLibrary.Lose`, SdfMeshLibrary.cs:370-378) **[opened]**. Positions, level-0 indices and triangle
  materials are kept.

### 1.8 Mesh region: `SdfWorldTables` + `SdfMeshRegionArena` (SdfWorldTables.MeshRegion.cs, SdfMeshRegionArena.cs) — what lands and stays on the GPU

- **One region per residency's tables:** `m_meshArena = new SdfMeshRegionArena(limitBytes: MeshRegionLimitOf(profile: device.MemoryProfile));`
  (SdfWorldTables.cs:205) **[opened]**.
- **Bound:** `MeshRegionDeviceLocalShare = 4UL` (MeshRegion.cs:217); `MeshRegionLimitOf` clamps the share to
  `[MeshRegionGranuleBytes, MostMeshRegionBytes]` (:225-229), `MostMeshRegionBytes = ((ulong)GpuRegion.MaxByteCount)`
  (:203) **[opened]**. The arena places through one `GpuRangeAllocator` sized to that limit
  (SdfMeshRegionArena.cs:81) **[opened]**. Growth is need + an eighth in 16 MiB granules (`MeshRegionGrownBytes`,
  MeshRegion.cs:255-261) **[opened]**.
- **Landing order:** owed meshes land through background work in `MeshDemand` priority (`RankLanding`,
  SdfWorldTables.MeshLanding.cs:59-76) **[opened]**. A draw whose mesh is owed or refused is filtered out of the
  staged list (MeshRegion.cs:488-498) **[opened]**.
- **Kept:** each placed mesh holds a cached claim on its CPU mesh (`mesh.Acquire(cached: true);`,
  SdfMeshRegionArena.cs:351) because the region re-reads a mesh's words from the mesh when it flushes
  (`MeshSection : IGpuRegionSource`, MeshRegion.cs:845-848; `MeshWordsOf` through `mesh.Hold()`, :752-780)
  **[opened]**. Unnamed meshes are retained up to `RetainedSlackWords = (1 << 23)` (32 MB) (src/Puck.SdfVm/SdfMeshRegionArena.cs:42, 194-196)
  **[opened]**.
- **Under pressure:** unnamed meshes forgotten first (`ForgetUnnamed`, src/Puck.SdfVm/SdfMeshRegionArena.cs:206-212, 364-378); a named mesh with
  no run is refused (`_ = m_refused.Add(item: mesh);`, :211) and its draws wait out of the frame; a relocation packs
  when refused meshes would fit packed or free words pass `PackedShare = 3` times named (:223-231); an account trim
  forgets unnamed meshes on the frame thread (`ServiceMemoryTrim`, MeshRegion.cs:53-66) **[opened]**.
- **Device refusal:** `SdfWorldTables.EnsureMeshRegionCapacity` catches a refused
  growth, clips placements to the buffer it holds and leaves those rows absent.
  Only refusal of the draw-record floor itself throws. Reserve-displaced chains
  still stage in that transaction, including a mesh the clip refuses.
- **Shrink:** `SdfWorldTables.FitMeshRegion` considers both relocation and
  forgotten slack, so freeing an unused tail can shrink without relocating
  standing geometry.

### 1.9 `GpuRegion` (Puck.Abstractions/Gpu/Residency/GpuRegion.cs) — every table's residency

- **Bound:** `MaxByteCount` (just under 4 GiB, GpuRegion.cs:107); no byte budget **[opened]**.
- **Host copy:** native, outside every account: `NativeMemory.AllocZeroed` (:394, :950, :1177), `NativeMemory.Alloc`
  (:1046) **[opened]**. A sourced region (the mesh region) keeps only its leading bytes (`Shadow`, doc :37-42).
- **Staging:** `StagingFloorBytes = (16UL << 20)` per staged slot, replaced when it holds more than four times a flush
  (:110-114) **[opened]**.
- **Under pressure:** growth throws if the device refuses (above).

### 1.10 `SdfMeshIrradiance` probe window + `ProbeCache` (SdfMeshIrradiance.cs) — lighting residency

- **One volume per residency** (`MeshIrradiance.Dispose()` in `SdfWorldResidency.Release`, SdfWorldResidency.cs:389)
  **[opened]**.
- **Window driven by:** `WindowCamera` = `frame.Views[0].Camera.Position` unless that view is `Elsewhere`
  (SdfMeshIrradiance.cs:573-575); the finest level anchors ahead of `Views[0]` (`WindowAnchor`, :925-938); but
  `WindowLow` centres each level on "the box of the cells of every view's camera this residency renders ... when that
  box and the first camera ... fit" (comment :944-954; loop over `frame.Views` from index 1, :966-976) **[opened]**.
  Probe urgency uses every view (`InAnyView(center: center, snapshot: in snapshot)` at :1919, over
  `CaptureViewCameras`, :1225-1235) **[opened]**. So the window is *one* window anchored on view 0 that stretches to
  cover the other views when they fit; it is not per-view.
- **ProbeCache bound:** none in bytes. `Retain(low, high)` forgets probes outside the window's resident cells only once
  `table.Count > cells` (:2890-2922) **[opened]**. Bytes are measured for the report (`Bytes()`, :2862-2880;
  `probe-cache-bytes=` in `DescribeCache`, :321) **[opened]**. A reader spins: `Thread.SpinWait(iterations: 1);`
  (:2944) **[opened]**.
- **Under pressure:** an account trim drops the scene's retained geometry (`TrimRetained`, subscribed at :1359)
  **[opened]**; probes are not trimmed.

### 1.11 `SdfMeshIrradianceScene` (trace scene, CPU + GPU) — what the lighting traces

- **Driven by:** every static draw of the frame, not the window: `StaticKey` folds `frame.MeshDraws` (:1290-1335) and
  `StartSceneBuild` snapshots every static draw (:1363-1374) **[opened]**. While the bake is pending, a scene is built
  from what landed at most every `PendingSceneInterval = 4 s` (:270, `ScenePendingDue` :1267-1269) **[opened]**.
- **Bound:** `SdfMeshIrradianceScene.RetainedTriangles` caps the geometry kept for later builds. `Content.Release` hands
  back its arrays through `RentedArrays`, which returns them to the shared pool or, past 1 MiB, to its bounded large pool.
  `SdfMeshIrradiance` reports the active hierarchy to `MemoryAccount` through its sampled scene owner. GPU side:
  `GpuArena` over a `GpuRangeAllocator` that throws "The probe scene exceeds its packed
  node address space." (src/Puck.SdfVm/SdfMeshIrradianceScene.Gpu.cs:187-201) **[opened]**, in a region from `CreateProbeRegion`
  (MeshRegion.cs:35-38) **[opened]**.
- The scene holds mesh claims (`m_meshClaims`, src/Puck.SdfVm/SdfMeshIrradianceScene.cs:22), which keep the static meshes' CPU payloads alive
  **[opened]**.

### 1.12 `WorldViewGraphHost.ReserveArrivals` and the arrival share (Puck.World.Client/WorldViewGraphHost.cs)

- **Driven by:** a walk from the main instance, roots and every producer the root reads this frame (the synthesized footprint, each placed view and pane) through session windows; the first
  hidden window onto an arriving world and every window on the way back are kept (`ReserveArrivals`, which
  `CompleteFootprints` calls first, with `ReachForArrival` taking each step) **[opened]**. The walk
  admits only producers that are portals (`if (!m_portals.Contains(item: producer) || ...) { return; }`, in
  `ReachForArrival`) **[opened]**.
- **What it does:** a kept window that is Unseen/Small/Hidden (`RendersForArrival`) renders at
  `WorldPortalVisibility.ArrivalShare` (`public const double ArrivalShare = 0.0625;`) in the footprint pass that
  `CompleteFootprints` runs, in `DecideFootprint` and in `RootFilmedViews` **[opened]**.
- **Why it exists (the stand-in):** `SdfWorldResidency.MeshesArriving => (MeshPathBake.Pending || (m_tables?.MeshesArriving ?? false));`
  with the doc "only frames that render one of its views bring them" (SdfWorldResidency.cs:223-230) **[opened]**,
  because background work binds only after an upload: `internal bool BindsBackgroundWork => ((m_uploads > 0UL) && (m_bindingRevision == m_uploadedBindingRevision) && (m_backgroundProgram is not null));`
  (SdfWorldTables.Bindings.cs:61) and `SdfGpuWork.Step` refills only `if (tables.BindsBackgroundWork)` (SdfGpuWork.cs:412)
  **[opened]**.
- **Bound:** none.

### 1.13 Routed and session residencies (Puck.World/WorldScreenBinder.Device.*.cs)

- A routed scene gets its own residency on first resolve (`RoutedResidencyOf`, Device.Routed.cs:110-126;
  `CreateRoutedResidency`, :132-165) and is disposed when the presenter stops presenting it (:200-215) **[opened]**.
  A session no scene proof lets share another stream's scene gets a residency of its own (`CreateSessionResidency`,
  Device.Views.cs:11-45) **[opened]**. Each is a full `SdfWorldResidency`: own tables, own mesh region at its own
  quarter-of-device limit (SdfWorldTables.cs:205), own `SdfGpuWork` (SdfWorldTables.cs:400), own irradiance volume
  **[opened]**. Release is whole: `Release` disposes irradiance, tables, dresser, retires the bake
  (SdfWorldResidency.cs:382-399) **[opened]**.
- **Demand mapping that already exists:** a window's view is the viewer's eye fitted through the glass, measured in
  the display pixels the glass spans and bounded to its frustum:
  `? (view with { DemandHeight = shown, DemandWithinView = true })` (WorldScreenBinder.Device.Session.cs:135-137;
  `WindowDemandOf` → `WorldWindowFrustumFit.ShownHeight`, :143-146), and the same in `WorldSessionSceneEmitter`
  (WorldSessionSceneEmitter.cs:494-497) **[opened]**. What does not exist is demand from that fitted eye *without*
  the window rendering.

### 1.14 `SdfGpuWork` budgets (SdfGpuWork.cs) — per residency

- `GpuShare = 0.25`, `UploadBytesPerSecond = (SlotCount * UploadChunkBytes) * 60.0` (240 MiB/s), `SlotCount = 4`,
  `IdleSeconds = 2.0` (SdfGpuWork.cs:197-212) **[opened]**, one instance per tables
  (`m_gpuWork = scope.Own(created: new SdfGpuWork(device: device, name: ObjectOwner));`, SdfWorldTables.cs:400)
  with no static/shared budget field (grep of statics in SdfGpuWork.cs found only constants) **[opened]**.
  **[inference]** N live residencies each claim a quarter of the GPU and 240 MiB/s of upload.

---

## 2. Memory owners today: every allocator and budget

| Owner | Where | Bounded by | Bounded or high-water |
|---|---|---|---|
| `MemoryAccount.Shared` | Puck.Hosting/Jobs/MemoryAccount.cs:29 | `DefaultBudget` = 3/4 installed RAM (:58), `Headroom` 1/32 clamped 16..512 MiB (:60), settable via `host.memory budget` (HostMemoryCommandModule.cs:95-100) | Admission budget only; `Change` never refuses (`MemoryAccount.Owner.Change`) |
| Account owners actually created | `"mesh"`, `"mesh-materials"` (SdfMesh.Storage.cs:190-209, 377-399); `"mesh-bake-group"` (SdfMeshPathBake.cs:2396); `"mesh-bake-sampled"`, `"mesh-bake-readback"` (SdfMeshBakeGpuWork.cs:149, 193); `"mesh-entry"` (SdfMeshStore.cs:188, 250); `"mesh-decode"` (SdfMeshStoreCodec.cs:93) | Only the bake group reserves (`TryReserve`); payloads only report | Payloads: high-water. Bake: bounded by admission |
| Account trim subscribers | `SdfMeshLibrary.Trim` (SdfMeshLibrary.cs:322), `SdfWorldTables.RequestMeshTrim` (MeshRegion.cs:307), `SdfMeshIrradiance.TrimRetained` (SdfMeshIrradiance.cs:1359) | Fired by `SetBudget` lowering or `host.memory collect` (MemoryAccount.cs:80; HostMemoryCommandModule.cs:106) | Nothing fires a trim from a reading of pressure **[opened: only those two callers of `TrimCaches`]** |
| `ShaderPipelineMemoryBudget` | the pipeline's budget file, since replaced by a reservation in the account | `DeviceLocalShare = 4UL` of `GpuMemoryProfile.DeviceLocalBytes` per pipeline instance; 512 MiB when unreported | Bounded per instance; refuses a replacement (`SHADERPIPE_BUDGET`); not a reservation from anything |
| Mesh region (per residency) | SdfWorldTables.MeshRegion.cs:217-229 | Quarter of device-local, ≤ ~4 GiB | Bounded per residency, not across residencies |
| Every other `GpuRegion` (program tables, probe tables, probe scene, ...) | GpuRegion.cs | `MaxByteCount` only | High-water; grows, shrinks only where an owner calls `Shrink` |
| `GpuRegion` host copies | GpuRegion.cs:394, 950, 1046, 1177 | None; `NativeMemory` outside `IAllocator` and the account | High-water |
| `GpuMemoryPool` | Puck.Abstractions/Gpu/Device/GpuMemoryPool.cs:24 | No budget; `allocate` "throws when the device refuses" (:35-36) | High-water; frees an empty block only if it was a resource's own or the key keeps another empty one (:174-184) |
| `DirectXHeapArena` | Puck.DirectX/DirectXHeapArena.cs:25-55 | 64 MiB heaps (`HeapBytes`, :27) per (heap type, category, role) | High-water |
| `VulkanMemoryArena` | Puck.Vulkan/VulkanMemoryArena.cs:18-42 | 64 MiB blocks (`BlockBytes`, :20) per (type, role, image) | High-water |
| `GpuDeviceMemoryWork` (`memory.<backend>`) | Puck.Abstractions/Gpu/Counters/GpuDeviceMemoryWork.cs:32-81 | None: a ledger of allocated/released/peak by role | Observation only |
| Staging (regions) | `StagingFloorBytes` 16 MiB per staged slot (GpuRegion.cs:110-114) | Shrinks past 4x a flush | Soft-bounded |
| Background work readbacks | `SdfGpuWork` slots, released after `IdleSeconds = 2.0` idle (SdfGpuWork.cs:182-185, 201) | Idle release | High-water while busy |
| `SdfMeshLibrary` | SdfMeshLibrary.cs:33 | min(`CacheBudget` 16..512 MiB, Budget/16) of cache-only + claimed meshes | Bounded (but see 1.5) |
| Trace scene CPU (`Node[]`, entries, instances) | SdfMeshIrradianceScene.cs:66-96, 116 | `RetainedTriangles` 8 M for retained geometry; live geometry unbounded | High-water; managed, `ArrayPool.Shared` |
| `ProbeCache` | SdfMeshIrradiance.cs:2851-2922 | Window cells only, no bytes | High-water per window; blocks kept while any kept probe names them (open item) |
| Irradiance volume words | `m_volume = new uint[(layout.TotalCount * SdfMeshIrradianceProbe.ProbeWords)];` (SdfMeshIrradiance.cs:656) | Layout size | Bounded by layout, per residency |
| Composed-document store | `private static readonly ConcurrentDictionary<string, WorldComposedDocument> ComposedDocuments` (Puck.World.Schema/WorldDefinitionFileSource.Cache.cs:26) | None; `ForgetComposedDocuments` (WorldDefinitionFileSource.cs:1252) has **no caller in `src/`** (only tests) | High-water for the process |
| Material cut lists | `SdfMeshMaterialCut*.cs` has no `MemoryAccount`/`TryReserve`/`IAllocator` (grep, no matches) | None | Unbounded per bake |
| Managed per-group bake working memory | open-items "Memory ownership" (SdfSampledLattice `Refine`, `CrossingPoints`, `GradientPoints`) **[unverified this lane]** | None | Unbounded |
| Device budget reading | none: grep for `QueryVideoMemoryInfo|memory_budget|MEMORY_BUDGET|IDXGIAdapter3|RegisterVideoMemoryBudgetChangeNotificationEvent` in `src/` finds only the name `ShaderPipelineMemoryBudget` | — | — |

`host.memory` reads the account, the allocator, managed heap and process, then the `memory.*` sources as separate
observations, and states "unified physical total is unavailable" (HostMemoryCommandModule.cs:48, 69) **[opened]**.

`GpuMemoryProfile` is fixed at device creation (`DirectXDeviceContext.m_memoryProfile`, DirectXDeviceContext.cs:49,
165; `VulkanLogicalDevice.MemoryProfile { get; init; }`, VulkanLogicalDevice.cs:45) **[opened]**. On Direct3D 12 UMA
it reports `DeviceLocalBytes = dedicatedVideoMemory + sharedSystemMemory` (GpuMemoryProfile.cs:74-83) **[opened]**,
the same physical RAM `MemoryAccount.DefaultBudget` takes 3/4 of **[inference]**.

---

## 3. Where the residency decision's steps would attach

| Step | Code it changes | What it replaces |
|---|---|---|
| **Device budget readings** | A live reading beside the static profile on `IGpuDeviceContext` (`GpuMemoryProfile MemoryProfile { get; }`, Puck.Abstractions/Gpu/Device/IGpuDeviceContext.cs:34); Direct3D 12 in `DirectXNativeDeviceApi.ReadMemoryProfile(IDXGIAdapter1* adapter, ...)` (DirectXNativeDeviceApi.cs:231; `IDXGIAdapter3` needed for `QueryVideoMemoryInfo` **[inference]**); Vulkan beside `VulkanNativePhysicalDeviceApi.GetMemoryProfile` (VulkanNativePhysicalDeviceApi.cs:319; needs the extension enabled at device creation **[inference]**); fields in `HostMemoryCommandModule.Describe` (:54-75) | The static shares: `MeshRegionLimitOf` (MeshRegion.cs:225), `ShaderPipelineMemoryBudget.For` (:22), and the bake's `HostMemory.AvailablePhysicalBytes` pacing (SdfMeshPathBake.cs:766-774) on unified memory |
| **One byte authority** | `MemoryAccount` (Puck.Hosting). `GpuRegion` lives in Puck.Abstractions, below Puck.Hosting, so it can only report through `IAllocationObserver` (Puck.Abstractions/Memory/OwnedBuffer.cs:6-10), the interface `MemoryAccount.Owner` already implements (MemoryAccount.cs:106). Puck.Shaders references Puck.Hosting (Puck.Shaders.csproj:14), so the pipeline budget can reserve directly. `GpuDeviceMemoryWork` would become an owner feed or the account would read it | `ShaderPipelineMemoryBudget`'s separate quarter; each residency's own region limit (SdfWorldTables.cs:205); per-residency `SdfGpuWork` budgets (SdfWorldTables.cs:400); `RegionBudgetBytes` read from one residency's tables (Tiers.cs:65) |
| **Eviction by priority** | `SdfMeshPathBake.Retier` spare/bias logic (Tiers.cs:232-408) reading one cross-residency reservation; `SdfMeshRegionArena.Plan` refusal (src/Puck.SdfVm/SdfMeshRegionArena.cs:197-213) and `ForgetUnnamed`; `SdfWorldTables.EnsureMeshRegionCapacity` (the same region growth door) catching a device refusal and lowering the limit; the library's weight (SdfMeshLibrary.cs:315, 321-330) charging only cache-only payloads | Arena refusal that leaves draws out (a hole), the throw on refused growth, "coarsen every demand evenly" bias as the only response |
| **Mapped demand through portal faces** | `SdfMeshDemand` gains points not from `frame.Views` (`Place` exists for offline use, SdfMeshDemand.cs:81-94); the producer is the fitted window camera already computed in `WorldScreenBinder.SharedViewOf` (Device.Session.cs:117-138) and `WorldSessionSceneEmitter` (WorldSessionSceneEmitter.cs:494-497); a residency must pack and upload its tables with no view rendered so `BindsBackgroundWork` (src/Puck.SdfVm/SdfWorldTables.Bindings.cs:61) holds | `ReserveArrivals`/`ReachForArrival`/`RendersForArrival` (`WorldViewGraphHost`), the `ArrivalShare` cases (in `CompleteFootprints`' footprint pass, `DecideFootprint` and `RootFilmedViews`), `WorldPortalVisibility.ArrivalShare`, and the arriving-world reads (`WorldScreenBinder.Nesting.cs:389`) |
| **Lighting on the demand points** | `SdfMeshIrradiance.WindowCamera`/`WindowAnchor`/`WindowLow`/`CaptureViewCameras` (:573-575, :925-985, :1225-1235) reading the residency's `SdfMeshDemand` instead of re-deriving from `frame.Views`; `StaticKey`/`StartSceneBuild` (:1290-1374) filtering to the window's reach; `ProbeCache.Retain` (:2890) by bytes from the account; `ScenePendingDue` (:1267) | The second demand derivation; the whole-frame trace scene; cell-count retention |
| **Authored areas** | Candidates for "an existing volume vocabulary": `WorldPlacementSpatialVolume` with `WorldPlacementSpatialRole` `Occupation`/`Clearance`/`Influence` (Puck.World.Schema/WorldPlacementSpatial.cs:13-20, 51-57), whose influence channel "is opaque to the engine and is never interpreted" (:44); and `WorldGravityArea` bounds (WorldGravity.cs:69-75). Neither has openings. Consumption point: `SdfMeshPathBake.Prioritize`/`MeasureDemand` (src/Puck.SdfVm/SdfMeshPathBake.Residency.cs:90-106, 324-336) | Nothing (new) |
| **Terrain tiles** | `SdfMeshBakeGroup` forms groups by instance, slot or union (`Selector` "its slot, `-(index + 1)` of its first separate instance, or WorldSelector", SdfMeshBakeGroup.cs:26-28); grep finds no `Terrain`/`Tile` class in `src/` | A whole large instance as one group with one tier and one demand box **[inference]** |

---

## 4. Duplicates and stand-ins

### Duplicates (two mechanisms, one job)

1. **Two device-local quarter budgets, uncoordinated:** `MeshRegionDeviceLocalShare = 4UL` (MeshRegion.cs:217) and
   `ShaderPipelineMemoryBudget.DeviceLocalShare = 4UL` (:14), both over the device-local share helper on
   `GpuMemoryProfile`, neither a reservation from anything. The mesh one is per residency, so two routed
   residencies plus a pipeline instance can "own" three quarters at once **[inference]**.
2. **Demand points derived twice from `frame.Views`:** `SdfMeshDemand.Observe` (SdfMeshDemand.cs:49-74) and
   `SdfMeshIrradiance.CaptureViewCameras`/`WindowLow`/`WindowCamera` (:1225-1235, :966-976, :573-575), with
   different filters (the bake skips zero-height and non-finite views; the irradiance skips only `Elsewhere` in
   `CaptureViewCameras`).
3. **Four byte ledgers for overlapping things:** `MemoryAccount` owners (host payloads), `GpuDeviceMemoryWork` (device
   allocations by role), the library's own LRU weight over `StorageBytes` (SdfMeshLibrary.cs:50-54, 315), and the
   region's named bytes copied into the bake (`m_regionNamedBytes`, src/Puck.SdfVm/SdfMeshPathBake.Residency.cs:352).
4. **Five retention policies in five currencies:** library LRU in bytes (SdfMeshLibrary.cs:324), arena slack in words
   (`RetainedSlackWords`, src/Puck.SdfVm/SdfMeshRegionArena.cs:42), trace-scene retention in triangles (`RetainedTriangles`, src/Puck.SdfVm/SdfMeshIrradianceScene.cs:116),
   probe retention in window cells (`ProbeCache.Retain`, :2890-2895), store retention in disk bytes
   (SdfMeshStore.cs:34). The selector's "eviction by priority" needs one currency.
5. **Several schedulers for residency work:** the bake's static pool (`Task.Run`, src/Puck.SdfVm/SdfMeshPathBake.Residency.cs:223), `JobPool`
   (Puck.Hosting/Jobs/JobPool.cs:18, used by Puck.World/Puck.World.Server per grep), the irradiance's `Task.Run`
   worker, build and refill (SdfMeshIrradiance.cs:737, 1387, 1556), restore work, bake readbacks and mesh-store writes
   on `ThreadPool` (`BoundedWorkQueue`, Puck.Hosting/Jobs/BoundedWorkQueue.cs), and a per-residency `SdfGpuWork` budget (SdfWorldTables.cs:400). The decision's "engine
   job scheduler" the selector feeds does not exist as one type (§6, assumption 7).
### Stand-ins (where the decision's design should be)

1. **Rendering hidden windows to make worlds load:** `ArrivalShare = 0.0625` renders (`WorldViewGraphHost.CompleteFootprints`,
   `DecideFootprint` and `RootFilmedViews`), because background bake/landing binds only after an upload (src/Puck.SdfVm/SdfWorldTables.Bindings.cs:61; SdfGpuWork.cs:412).
2. **Whole residency released and rebuilt on a routed world's absence:** Device.Routed.cs:200-215 disposes the whole
   residency (tables, region, probes); its arena slack and probes are lost; the library and store are what make the
   return cheap.
3. **Holes instead of coarse:** a refused first mesh's draws are filtered out (MeshRegion.cs:492-497); nothing draws a
   coarser stand-in or haze for it **[inference: no fallback path found on the refusal branch]**.
5. **Whole trace scene per change:** every static draw, rebuilt top level, while the bake is pending every 4 s
   (§1.11).
6. **Relocation re-lands every mesh from the host** (`m_meshesOwed.ReplaceWith(meshes: m_meshArena.Placements.Keys);`,
   MeshRegion.cs:448-450) **[opened]**.
7. **CPU payload kept for the region's re-flush:** the arena's cached claim per placed mesh (src/Puck.SdfVm/SdfMeshRegionArena.cs:351) and
   `MeshWordsOf` re-reading through `Hold` (MeshRegion.cs:752-780); restore covers only normals and coarser indices.
8. **Unified-memory pacing by host free RAM:** the bake's admission reads `HostMemory.AvailablePhysicalBytes`
   (SdfMeshPathBake.cs:706-709), a stand-in for a device/unified budget.
9. **Library forgets claimed meshes** (1.5): reload of a second copy instead of finding the live one.

---

## 5. Stale documentation (claims the code no longer matches)

This list is what the round found when it ran. The pages it names have changed since: items 1, 4 and 9 are corrected
in `residency.md` and item 8 in `arrival-proportional.md`. Step numbers in this report are the campaign outline's as it stood when the round ran: 2 one byte authority, 3 attribution, 4 duplicate-copy removal, 5 priority eviction, 6 mapped portal demand, 7 shared lighting demand, 8 areas, terrain and moons, 9 managed churn. [The plan](../streaming-and-memory.md#the-build-order) builds the same work as its steps 1 and 3, 2, 6, 5, 4, 9, 10 and 11, in that order.

1. **streaming-and-memory.md, table row "Lighting on the same demand points: Not built: the probe window follows the
   first view"**, residency.md step 3 "the probe window follows every view, not `Views[0]` alone", and open-items
   Residency step 2 — partly stale: `WindowLow` already centres each level on the box of every view's camera when it
   fits (SdfMeshIrradiance.cs:944-981) and probe urgency already tests every view (:1237-1240, :1919). What remains:
   one window anchored on view 0, a second demand derivation, a whole-frame trace scene, no byte bound.
2. **open-items Residency step 2** cites `SdfMeshIrradiance.cs:456-458` for `Views[0]`/`WindowCamera`; now
   `WindowCamera` is :573-575 and `WindowAnchor` :925-928.
3. **open-items Residency step 3** "replacing the blanket baking share (`WorldViewGraphHost.ResetFootprints`). A
   window nobody sees still demands everything about its camera": `ResetFootprints` (`WorldViewGraphHost`)
   only clears lists; the share is applied in `ReserveArrivals`/`CompleteFootprints`/`DecideFootprint`/`RootFilmedViews`.
   And a fitted window demands only inside its frustum at the viewer's density
   (`DemandWithinView`, Device.Session.cs:135-137; SdfMeshDemand.cs:221-227), so "everything about its camera" holds
   only for unfitted session views.
4. **open-items Residency step 4** "removes the throw past `GpuRegion.MaxByteCount`" and **residency.md** "so no
   allocation throws past `GpuRegion.MaxByteCount`": the arena's limit is clamped to `MostMeshRegionBytes`
   (MeshRegion.cs:203, 225-229), so that throw is already unreachable for the mesh region; the remaining throws are
   the device refusing `CreateDeviceLocal` in `GpuRegion.GrowDestination` (GpuRegion.cs:964-969) and `GrowReserve`'s
   draw-record overflow (src/Puck.SdfVm/SdfMeshRegionArena.cs:411-413).
5. **open-items "Memory ownership"** cites `GpuRegion.cs:331,760,854,980` for `NativeMemory.Alloc*`; now :394, :950,
   :1046, :1177.
6. **open-items "Memory on the Deck tier"** cites `SdfMeshLibrary.cs:32`, `:285-304`; now `ByteBudget` :33,
   `Admit`/`Grow` :321-330, the renumbered-variant path in `TryTake` :282-305. The defect it describes still holds.
7. **open-items "Memory on the Deck tier", composed-document store** "(`WorldDefinitionFileSource.cs`,
   `ForgetComposedDocuments` after boot ...)": no World code calls `ForgetComposedDocuments`; the only callers are
   tests (grep: tests/Puck.World.Transpiler.Tests, tests/Puck.World.Machines.Tests, tests/Puck.World.Games.Tests). The
   store is a process-static dictionary (WorldDefinitionFileSource.Cache.cs:26). Reads as if a release after boot
   exists; it does not.
8. **arrival-proportional.md, "### The store decode" (under "What exists")** describes the pre-D1 path in the present
   tense ("`SdfMeshLibrary.TryLoad` renumbers into more managed arrays ... (`SdfMeshLibrary.RenumberManaged`) and copies
   the result into native storage"). D1 landed: `Decode` reads into `SdfMesh.Native(...)` storage via
   `disk.TryReadInto(... storage: storage, taking: taking)` (SdfMeshLibrary.cs:238-248), and `RenumberManaged` runs only
   for a library without an allocator (`Renumber`, :401).
9. **residency.md "A level is demanded where the coarser level's recorded error would project past about a pixel,
   with a hysteresis band"**: the built rule is a tier at half a pixel (`PixelShare = 0.5f`, SdfMeshTier.cs:18) with
   `HoldFrames = 8` hysteresis (:23), measured every `MeasureFrames`; level-of-detail selection among a mesh's collapse
   levels is a separate mechanism.
10. **residency.md "Residency selector ... feeds the engine job scheduler; its machine-byte budget is a reservation
    from the job system's single physical byte-accounting owner"** describes types that do not exist: there is no
    selector type (the per-residency `SdfMeshDemand` + `SdfMeshPathBake.Retier` play its part), and no reservation
    from `MemoryAccount` for any GPU byte. Not wrong as design, but streaming-and-memory's "Where residency stands"
    table does not list it as unbuilt.
11. **residency.md "Demand points ... each led along its velocity by the landing latency"**: not built
    (`Observe` stores position only, SdfMeshDemand.cs:67); bake-demand-tiers step 5 records lookahead as deferred, but
    streaming-and-memory's table does not list it.
12. **streaming-and-memory.md "`MemoryAccount` ... sees only mesh payloads and bake workspaces"**: also the transient
    store-entry, decode, sampled and readback scratch owners (§2); minor.
13. **bake-demand-tiers.md "Each residency budgets alone. ... the nested worlds are small today (tens of
    megabytes)"**: the code confirms each residency budgets alone; the "tens of megabytes" figure **[unverified]** and
    is a reading that belongs in a commit, not the memo.

Checked and still accurate: no device budget query in `src/`; `GpuRegion.Grow` throws on device refusal; the material
cut is outside the account; a root camera demands through walls; `ProbeCache` spins (`Thread.SpinWait`, :2944) and
is not byte-bounded; the arrival search walks session windows only (portal producers, `WorldViewGraphHost.ReachForArrival`);
R0, S1, D1 and R1h exist (`SdfMeshStagingCause.cs`, `sdfMeshResolveHit` in `frame/sdf-mesh.hlsli`, `SdfMeshStorage.cs`)
and R1a/R1b do not.

---

## 6. Riskiest assumptions the campaign's plan makes about the current code, ranked

1. **"Demand mapped through portal faces retires the least-share renders" (campaign step 6).** *Against:* demand alone
   does not make a world arrive. A residency's bake samples and its meshes land only after its tables packed and
   uploaded (`BindsBackgroundWork`, src/Puck.SdfVm/SdfWorldTables.Bindings.cs:61; `SdfGpuWork.Step`, src/Puck.SdfVm/SdfGpuWork.cs:412; `SdfWorldResidency.MeshesArriving` doc,
   :223-230), which today happens only in frames that render one of its views. The step needs a "prepare and upload a
   residency with no view rendered" path, which the plan does not name. *For:* the mapping itself exists: a fitted
   window's view is the viewer's eye through the glass, at the viewer's density, within its frustum
   (Device.Session.cs:117-146). The step is decoupling demand from rendering, not computing a mapped eye.
2. **"`MemoryAccount` becomes the one authority across native, managed, device and staging bytes" (step 2).**
   *Against:* the account is a host-RAM admission promise (`DefaultBudget` 3/4 installed RAM, MemoryAccount.cs:58)
   whose `Change` never refuses (`MemoryAccount.Owner.Change`); device bytes live in a separate per-backend ledger
   (`GpuDeviceMemoryWork`) with no reservation; `GpuRegion` sits below Puck.Hosting and can only report through
   `IAllocationObserver`; on a discrete GPU host and device-local are two heaps, on UMA one (and Direct3D 12's UMA
   `DeviceLocalBytes` already counts shared system memory, GpuMemoryProfile.cs:74-83). "One authority" has to mean one
   account with per-heap budgets, or it double-counts on unified memory. *For:* the seams exist (`IAllocationObserver`,
   owners, trim events, `host.memory owners`).
3. **"Eviction by priority" (step 5) assumes an evictable unit below the group.** *Against:* a residency holds exactly
   one tier's mesh per group; the only responses are coarsening (Tiers.cs:365-388) or refusal, and refusal leaves the
   group's draws out of the frame (MeshRegion.cs:492-497): a hole, against the owner's "never a hole" ruling
   (residency.md). Coarse collapse levels live inside each tier's mesh, so "dropping a fine level frees its vertices"
   needs the compacted-vertex layout first. *For:* `Retier`'s spare list is already a priority-ordered coarsening.
4. **"Cut duplicate copies: release the CPU copy once the GPU copy is resident" (step 4).** *Against:* the region
   holds a cached claim on every placed mesh (src/Puck.SdfVm/SdfMeshRegionArena.cs:351) and re-reads its words from the CPU mesh for moved meshes,
   relocations, coarsened runs and other residencies (MeshRegion.cs:448-468, 752-780); relocation re-lands from the host
   (open item); the trace scene claims static meshes (src/Puck.SdfVm/SdfMeshIrradianceScene.cs:22); restore exists only for normals and coarser
   indices of store-published meshes. Releasing positions/level-0 needs GPU-side compaction and GPU probe placement
   first. *For:* the restore door (`SdfMesh.Hold`, need counting) is a proven pattern to extend.
5. **"Each residency budgets alone is fine while nested worlds are small."** *Against:* every routed or non-disclosing
   session residency gets its own quarter-of-device region limit (SdfWorldTables.cs:205) and its own `SdfGpuWork`
   with `GpuShare = 0.25` and 240 MiB/s upload (SdfWorldTables.cs:400; SdfGpuWork.cs:199, 212). Byte and GPU-time
   budgets multiply with residency count. *For:* regions grow only on need; how many residencies the debug world runs
   at once is **[unverified]** here.
6. **"Lighting on the same demand points" is mostly unbuilt (step 7).** *Against:* the window already stretches to
   cover every view and probe urgency already reads every view (SdfMeshIrradiance.cs:944-981, :1919). The real gaps are
   the trace scene's input (all static draws, :1290-1374), the byte bound, and the duplicate demand derivation. The
   plan may over-size this step and under-size the scene work.
7. **"An engine job scheduler exists for the selector to feed" (residency.md).** *Against:* `Puck.Hosting.Jobs` holds
   `JobPool` (a fork-join for partitions), `JobWorkers` and `MemoryAccount`; residency work runs on the bake's own
   static pool, `Task.Run` workers and per-residency `SdfGpuWork` (§4.5). Any step that says "the scheduler orders it"
   has nothing to attach to yet.
8. **"Device budget readings drop in" (step 2).** *Against:* the profile is a value fixed at device creation; the
   Direct3D 12 path holds an `IDXGIAdapter1` (DirectXNativeDeviceApi.cs:231), and Vulkan's budget needs an extension
   enabled at device creation **[inference]**; nothing polls a live reading. *For:* both read paths are in one
   function per backend, so the attach point is small.
9. **"Authored areas reuse an existing volume vocabulary" (step 8).** *Against:* the nearest vocabularies are placement
   spatial volumes (an influence channel the engine must never interpret, WorldPlacementSpatial.cs:44) and gravity
   areas; neither has openings, and reading a channel name for residency would be the engine-vocabulary leak AGENTS.md
   forbids. A new role is needed. *For:* both carry bounded shapes on placements already.
10. **"Terrain tiles as units in the same selector" (step 8).** *Against:* bake groups are instance/slot/union units
    (SdfMeshBakeGroup.cs:26-28); no spatial tiling of a large instance exists, so a large terrain instance gets one
    tier from its nearest point **[inference]**. This is a bake-planning change, not a selector input.
11. **"`world.residency` and `world.wait residency` read what each step is judged by."** *Against (open items,
    re-checked in part):* `world.wait residency` reads only displayed contributors; the per-group `all-resident`
    stamp is not reset by a device loss **[unverified this lane]**. Low risk; judged-by readings need these fixed or
    worked around.
12. **"A refused growth lowers the limit and coarsens" (step 5).** *For:* one call site (`EnsureMeshRegionCapacity`,
    the same region growth door) and the budget already re-reads the limit each frame (src/Puck.SdfVm/SdfMeshPathBake.Residency.cs:351). Low risk.

---

## What was not read

- No World was started: no `host.memory`, `world.residency` or `world.counters` reading backs any figure here, so
  every byte or count claim is from source constants, not a run. A headless World builds no renderer, so it would
  not have shown the region, probes or trace scene anyway. A measurement request for the lead: on a windowed World at
  the hero spawn and the meadow, `host.memory owners 0`, `world.residency`, `world.counters memory`, to read the number
  of live residencies and each one's `region-limit`/`region-named` (assumption 5) and `probe-cache-bytes`/
  `trace-scene-cpu-array-bytes`.
- The decision's "probe table of about 193 MiB" figure, the open items' readings (pagefile section, JIT counts,
  post-bake private bytes), and SdfSampledLattice's managed working memory were not re-checked.

