# Ground: where a debug World's memory goes (lane ground-memory)

Grounding pass, read-only. Tree: `worktree-agent-aa95247f5b9b74dcd` at the main landing. Every claim carries its kind:
**[M]** measured in this pass, **[R]** recorded earlier (open-items / job-system memo / an earlier World log, cited),
**[S]** derived from the source (file:line), not reproduced. No windowed World and no cold bake were run (the brief
forbade both), so everything about meshes, GI and the GPU is [R] or [S].

## What this pass measured

One headless debug World (`puck_world_start`, headless, `--devices none`), `host.memory` at boot (tick ~296), after
600 ticks (tick 896) and after `host.memory collect`, and two `dotnet-gcdump` heap snapshots (boot, tick 896),
aggregated by type (a scratch script; the gcdump report's first column is per-object size,
so a type's bytes are size x count).

| Reading | Boot (t≈296) | t=896 | After `collect` |
|---|---:|---:|---:|
| Process private | 536 MiB | 544 MiB | 521 MiB |
| Managed heap / committed | 282 / 414 MiB | 306 / 414 MiB | 183 / 435 MiB |
| LOH | 155 MiB | 157 MiB | 61 MiB |
| Collections gen0/gen1/gen2 | 149/104/35 | 150/105/36 | 154/108/38 |
| Account live/cached/reserved | 0/0/0 | 0/0/0 | 0/0/0 |
| mimalloc committed / reserved | 0.1 / 2.4 MB | 3.5 / 1,076 MB | 2.4 / 1,076 MB |
| Account budget / headroom | 25,497,047,040 / 536,870,912 B | same | same |

- **A headless World is about 0.55 GB and does not grow** [M]: 600 ticks cost one gen0 collection and 8 MiB of
  private bytes. All of boot's churn (149 gen0, 35 gen2 in ~22 s) is document compile and boot.
- **Headless holds no mesh, no GI and no residency** [M]: its log has no `[sdf.mesh-bake]` or `[sdf.mesh-gi]` line
  and `world.residency` is not registered. So of the 10.5–14.5 GB a windowed World holds [R], about 10–14 GB is
  presentation, and a player's build carries all of it.
- Live heap by type at t=896 [M] (gcdump, 261 MB live, 1.21 M objects): `byte[]` 66 MB in 10,631 arrays (the composed
  document store reads 43,550,900 B through `world.status`, an upper bound it documents), `FieldCellDelta[]` 17 MB
  (`WorldStateChangeDisclosure.cs:65`, one sent-delta array per row), `int[]` 9 MB, the authoring tree
  (`ShapeDocument` 7.9 MB, `DocumentVector3` 7.1 MB, `DocumentScalar` 2.3 MB, `ShapeSwingDocument` 2.2 MB,
  `DocumentQuaternion` 1.7 MB: about 25 MB), strings 6.8 MB, per-body arrays (`Principal[]` 4.4 MB:
  `WorldBody.ScriptInput.cs:8-10`, three per body; `WorldInputRecord[]` 4.2 MB; 590 `WorldBody` 3.2 MB).
  By assembly: CoreLib 110 MB, World.Protocol 19, World.Schema 14, World.Authoring 13, Assets 13, World.Server 10,
  State 10, SignedDistance 7.

## Breakdown table

Bytes for a fresh windowed debug World on the lead machine (Direct3D 12, RTX 4070, 31.7 GiB) unless marked. "Player"
says whether a player's build of the debug world carries it.

| Holder | Bytes | Evidence | Player |
|---|---:|---|---|
| Simulation, documents, State, protocol, command catalogs (all of headless) | 0.52–0.57 GB private; 183 MB live managed after collect | [M] above | Yes, except the dev rows below |
| — composed document store (static, uncapped, one image per path+catalog) | ≤ 43.5 MB | [M] `world.status` "held 66 (43550900 bytes)"; `WorldDefinitionFileSource.cs:1849-1869` | Dev (hot reload); a player's build can `ForgetComposedDocuments` after boot (`:1263`) |
| — authoring document tree kept after compile | ~25 MB managed | [M] gcdump (ShapeDocument, DocumentVector3, ...) | Probably not needed by a player's build booting a compiled world; unconfirmed whether that path still holds it |
| — JIT'd code (69,128 methods, 11.4 s compile) | tens of MB code heap (unmeasured) | [M] `host.memory` jit counters | Release publishes ReadyToRun (`Puck.World.csproj:19`) |
| Mesh payloads, CPU, native (mimalloc, accounted) | 1.41–1.61 GB live after collect | [R] open-items "Memory ownership": D3D12 1,409,528,905 B, Vulkan 1,607,238,606 B; [S] `SdfMesh.Storage.cs:77-136` | Yes |
| — of which vertex positions+normals | ~550 MB | [S] 22,898,992 vertices x 24 B (earlier tour log, `done residency=world`) | Yes |
| — level-0 indices + triangle materials | ~565 MB | [S] 35,250,712 triangles x 16 B | Yes |
| — levels 1 and 2 | ~114 MB | [S] 4,364,180 + 2,765,102 triangles x 16 B | Yes |
| — chunked parents' second copy of indices, renumbered variants | ~0.2–0.4 GB (the remainder) | [R] open-items low item "A chunked mesh still stores its triangles' indices twice" (`SdfMesh.Storage.cs` NativeCopy) | Yes |
| mimalloc committed beyond account live (free pages, bake workspaces, fragmentation) | ~1.0–1.2 GB at tour start (allocator 2.6 GB vs ~1.5 GB live) | [R] Sweep packet: "the native allocator 2.6 GB" | Yes |
| Managed committed (GI scene, probe caches, irradiance volume, bake temporaries, GC headroom) | 3.2 GB committed; LOH ~1.5 GB after collect | [R] Sweep packet; [R] "the settled LOH still reads about 1.5 GB after host.memory collect" | Yes |
| — GI trace scene (world residency: 952 geometries, 31,125,659 triangles) | est. 0.5–0.7 GB managed (Node 28 B, ~2 nodes per 4-7-triangle leaf, int per leaf position) | [S] `SdfMeshIrradianceScene.cs:95-107, 561-622`; [R] earlier log `[sdf.mesh-gi] scene draws=952 triangles=31125659` | Yes |
| — GI scene build transient (pieces 28 B + centroids/order 16 B per piece) | est. ~1.3 GB per full build, beside the old scene | [S] `SdfMeshIrradianceScene.cs:584-611, 707-710` | Yes |
| — GI retained geometry no draw uses (cached mesh claims) | ≤ 8,000,000 triangles: ~0.25 GB of mesh payload + ~0.15 GB nodes | [S] `SdfMeshIrradianceScene.cs:60, 270-281`; claims `:20-21` | Yes |
| — probe cache (per residency with GI) | 2,200 B a probe (hit block 2,040 + light block 160) + ~100 B table; a full High window is 61,440 probes = ~135 MB; **no byte bound, never evicted except on resize** | [S] `SdfMeshIrradiance.cs:2236-2258` (blocks), `:2266-2417` (cache), `:506-517` (only clear), `:106` (MaxProbes); [R] 38 MB after a nine-stop hall walk | Yes |
| — irradiance volume + refill/restore words | 61,440 x 235 words x 4 B = 57.8 MB each, up to three arrays | [S] `SdfMeshIrradiance.cs:509, 1206, 1635`; `SdfMeshIrradianceProbe.cs:12,22` | Yes |
| Mesh region, GPU (device-local; on a Deck, shared system memory) | 1.37 GB used, held at need + 1/8 rounded to 16 MiB: ~1.55 GB | [R] Residency packet "Mesh bytes by level ... 1.37 GB used"; [S] `SdfWorldTables.MeshRegion.cs:115` | Yes |
| Mesh region staging (host-visible), after a whole-region flush | up to the whole region (1.37 GB) in one slot until that slot next flushes | [S] `GpuRegion.cs:1558-1603` (`StagingFor` shrinks only at the slot's next flush); [R] "the first staging writes the whole region (1.37 GB) at once" | Yes |
| Region host copies (`NativeMemory.Alloc*`, outside allocator and account) | unmeasured | [S] `GpuRegion.cs:331` and growth at `:760, 854, 980`; [R] open-items low item | Yes |
| Brick pool (world residency only) | 64 MB | [S] `SdfBrickPoolLayout.cs:22` | Yes |
| Render targets, history, shadows, sky, 11 session residencies' targets, nested views | unmeasured; history alone 188 B/pixel (47 words), 216 MB per 1280x900 surface | [R] Sweep packet "History grew from 6 to 47 uint words per output pixel"; per-pipeline counts exist (`pipeline.budget`, `ShaderPipelineMemoryBudget.cs`) | Yes (the debug world's sessions are its content) |
| **Unattributed**: process private minus managed committed minus mimalloc | ~8.4 GB of 14.2 GB at tour start | [R] Sweep packet: "the rest other native memory and the device's mapped heaps" | Unknown until attributed |
| Tour growth (74 stops) | +9.4 to +11.5 GB (10.5 → 19.9–22.2 GB) | [R] Sweep packet | Yes, for any player who travels |
| One garden crossing | +5.3 to +8.0 GB | [R] Sweep packet | Yes |
| Cold bake working set | private 11.2 GB at ready, 16.2 GB at 83 s, 313 GCs | [R] Bake packet (settle-2) | Yes on a player's first boot unless a warm store ships (owner question below) |
| — per admitted group, accounted | ≤ (65^3 + 4,000,000) x 96 B ≈ 410 MB | [S] `SdfMeshPathBake.cs:1515-1524`, `SdfSampledLattice.cs:259, 979-986`, `SdfWorldPackage.Mesh.cs:50` | Yes |
| — per group, managed and unaccounted | `Vector3[]` fine points ≤ 48 MB, `bool[]` masks, contour lists, crossing rounds | [S] `SdfSampledLattice.cs:437-450` (Refine), [R] open-items medium item | Yes |
| — warm-load decode transient | the whole loaded set once in managed arrays before the native copy (~1.4 GB on a full warm boot) | [S] `SdfMeshStoreCodec.cs:177, 214, 321` then `SdfMesh.Storage.cs:77`; `SdfMeshLibrary.cs:173-199` | Yes |
| Debug tools resident (console catalog, diagnostic draws, input history, rings) | < 20 MB together | [M] gcdump: command dictionaries ~0.6 MB, `WorldInputRecord[]` 4.2 MB | Mostly dev |

**Development-only overhead is small: under about 100 MB.** The premise that compile caches, test hooks and debug tools
explain the footprint is wrong. A player's build of the debug world carries essentially everything above.

## Premises for the memory lane, verified or corrected

1. *"A garden crossing re-plans the residency while holding two mesh sets."* **Verified in mechanism [S], cause
   identified; not reproduced.** The library forgets meshes that residencies still hold:
   - `SdfMeshLibrary.ByteBudget` is `min(CacheBudget, Budget/16)`, at most 512 MiB (`SdfMeshLibrary.cs:32`,
     `MemoryAccount.cs:61`), and `Retain` charges every indexed mesh against it, including meshes a residency holds,
     then evicts the oldest index entries (`SdfMeshLibrary.cs:285-304`). The world's live set is ~1.4–1.6 GB, so at
     most about a third of the live meshes stay findable.
   - On a re-plan, a group that is not kept (`Keeps`, `SdfMeshPathBake.cs:1138-1142`: same selector, slot, materials and
     geometry) asks the library (`:1034`). For an evicted geometry `TryTake` fails although the payload is alive,
     so the group loads it from the store (`Begin`, `:1160-1176`; `TryLoad`, `SdfMeshLibrary.cs:158-207`). That decodes
     into managed arrays (`SdfMeshStoreCodec.cs:177, 214, 321`) and makes a second native copy (`SdfMesh.Storage.cs:77`).
   - Meanwhile the new group draws the previous group's mesh at that selector (`SdfMeshPathBake.cs:1057-1061`). The
     retired old groups release only when their job ends and the mesh is replaced (`:2136-2141, 2213-2228`). So the old
     copy and the new copy coexist until landing.
   - Downstream: the GI scene keys draws by mesh reference (`SdfMeshIrradiance.cs:2203` `DrawKeyComparer`), so every reloaded
     copy is new geometry. The scene rebuilds those triangles beside the old scene and retains up to 8 M triangles of
     the old (`SdfMeshIrradianceScene.cs:60, 270-281`). The mesh region lands the new words while the old are drawn.
   - For static groups the renumber path is dynamic-only (`Renumbers`, `:1145-1156`). A static group whose material
     ids shifted is therefore not kept and depends on the library to share its geometry.
   - What moved 1,048 groups on that crossing (a palette shift, selectors renumbered by the body leaving and returning,
     or the far distance) is not read yet. The lane reads the crossing's `[sdf.mesh-bake] start ... baking= loading=
     shared=` line first.
2. *"The cold bake passes 16 GB private within a minute with 313 GCs."* **Recorded [R]; mechanism confirmed [S].**
   - Admission is paced by an account whose budget is 3/4 of installed RAM (`MemoryAccount.cs:56`): 25.5 GB here
     [M], ~11 GB on a Deck. The budget does not see the managed heap, the GPU or anything outside the account.
   - A group's peak counts only its owned native storage (`:1515-1524`). The managed temporaries (fine-point
     `Vector3[]`, masks, contour lists) are invisible to it, and they are what the 313 collections pay for.
   - Workers = `ProcessorCount` for the bake (`SdfMeshPathBake.Residency.cs:43`) plus `ProcessorCount - 2` GI drain
     workers (`SdfMeshIrradiance.cs:181`), so simultaneous working sets scale with cores.
   - The "admitted alone" rule always progresses (`SdfMeshPathBake.cs:611-616`), correctly, but so a single 410 MB
     group always fits.
3. *"10.5–14.5 GB private at a fresh World."* **Recorded [R]; corrected in composition.** Simulation is 0.55 GB [M].
   Of the rest, ~2.6 GB is mimalloc (mostly mesh payloads), 3.2 GB managed (GI scene and caches, bake and decode
   transients), and **~8.4 GB is unattributed** "other native and mapped". That bucket is the largest and is the
   lane's first measurement, before any fix. Puck has no GPU budget or usage reading: no DXGI
   `QueryVideoMemoryInfo` or `VK_EXT_memory_budget` in `src/` [M, search]. It has only a static
   `GpuMemoryProfile` and the per-pipeline `ShaderPipelineMemoryBudget` (`ShaderPipelineMemoryBudget.cs:12-29`).
4. *"A tour doubles it."* **Recorded [R]; new candidate cause [S].** The probe cache keys probes by world cell, is
   cleared only on a window resize (`SdfMeshIrradiance.cs:506-517`) and has no byte bound. A lockstep tour drains each
   stop's window fully (Sweep packet), so each new stop can add up to a full window (~135 MB for a High window over three
   levels), per residency with GI. 74 stops can account for several GB of the 9–11 GB growth. Light blocks churn on
   every relight under the day cycle, and a block lives while any probe slices it. Unconfirmed. `world.residency` and
   `[sdf.mesh-gi]` print `probe-cache-bytes` (`SdfMeshIrradiance.cs:234-248`, through `SdfWorldResidency.cs:203`), so one windowed tour reading per stop
   settles it.
5. *"The account budgets memory."* **Corrected.** The account sees mesh payloads, bake group workspaces, the point
   table and worker scratch (`SdfMesh.Storage.cs:80-172`, `SdfMeshPathBake.cs:681, 2130`,
   `SdfMeshPathBake.Residency.cs:65`). It does not see:
   - the managed heap (GI scene, probe cache, volume, decode and bake temporaries, documents);
   - `GpuRegion` host copies (`NativeMemory`);
   - device memory, upload and staging heaps, driver memory;
   - `ArrayPool<byte>.Shared` retention from store decode (`SdfMeshStoreCodec.cs:75`; unconfirmed size).

   Its budget is not the process's: on a unified-memory Deck the GPU's allocations come from the same 16 GB the
   account calls 3/4 of its own.
6. *"Development-only overhead is a large share."* **Corrected:** under ~100 MB (table above).
7. Stale comment [S]: `Puck.World.csproj:26-28` says the LOH keeps "baked meshes" live. The payloads have been native
   since `mem-m1`, so the managed 1.5 GB settled LOH is now GI and caches.

## What a Deck-tier budget would require

The Steam Deck has 16 GB LPDDR5 shared by CPU and GPU, 8 logical processors, and a 1280x800 display. Against a
working figure of ~10 GB for process plus GPU (the owner has not set one), a fresh debug World today needs about
12–16 GB: 10.5–14.5 GB private plus ~1.5–3 GB device-local, which on the Deck are the same memory. A tour and a crossing
add 5–11 GB, and a cold bake passes 16 GB. Rough targets:

| Holder | Today | Deck target | How (mechanism to reuse) |
|---|---:|---:|---|
| Simulation + documents | 0.55 GB | 0.55 GB | Already fits; drop the composed-document store after boot in a player build |
| Mesh payload, CPU | 1.4–1.6 GB | ≤ 0.9 GB | Keep only what CPU readers read: GI and picking read level-0 positions and indices; normals and levels 1–2 can be released after landing and re-derived from the store. Drop the chunked parent's second index copy (open-items low item). Share static copies by prototype (Bake packet [high]) |
| Mesh region, GPU | ~1.55 GB | ≤ 1.2 GB | The same static-copy sharing; the residency's eviction step 4 under one byte budget |
| GI (scene + probe caches + volumes, all residencies) | est. 1.5–2.5 GB managed | ≤ 0.6 GB | Byte-bound `ProbeCache` (Residency step 2), native scene arrays owned by the scene (job-system memo §4 item 3), a byte bound on retained triangles |
| Render targets, sessions, history | unmeasured | ≤ 1.5 GB at 1280x800 | Read `pipeline.budget` per instance first |
| Unattributed native/mapped | ~8.4 GB | ≤ 1.5 GB | Attribute first (VMMap or an ETW heap and VirtualAlloc trace of a windowed World, plus a new device-memory reading) |
| GC headroom and fragmentation | ~1–1.5 GB | ≤ 0.5 GB | Follows from moving large arrays native |
| Transient allowance (crossing, rebake, scene rebuild, cold bake) | 5–8 GB spike; > 5 GB cold | ≤ 1.5–2 GB | The library fix removes the duplicate-set spike. Bake admission must count managed temporaries (move them native) and take its budget from a process-wide figure that includes device memory |

The order of shrinkage needed: the unattributed bucket first (largest, unknown), then the crossing spike and the tour's
growth (unbounded caches), then GI's managed working set, then the mesh copies.

## Mechanisms a fix must reuse (and not duplicate)

- **`MemoryAccount`** (`src/Puck.Hosting/Jobs/MemoryAccount.cs`) is the one byte ledger and admission authority. Its
  categories Live, Cached and Retirement already distinguish a cache claim from a consumer's. The fix moves the
  library's budget onto Cached bytes, not a new counter.
- **`SdfMesh` payload claims** (`SdfMesh.Storage.cs:196-296`) already know whether only cached claims hold a payload
  (`Classify`). The library index should stay findable while any claim is alive (a live-index entry dropped at the
  last release) and charge its budget only for cached-only payloads. That is the memory-pooling page's "eviction
  removes a cache claim while a residency can still use a mesh" (`memory-pooling.md:59`), finished.
- **`IAllocator` / `MimallocAllocator` / `OwnedBuffer<T>`** for every native mover (GI blocks, scene nodes, bake
  temporaries, `GpuRegion` host copies), per the job-system memo's native-storage list (§4 items 2–3).
- **`TrimRequested` / `host.memory collect` / `host.memory budget`** for pressure, already wired into the library,
  irradiance, worker scratch and mesh region.
- **`world.residency`, `[sdf.mesh-gi]` describe lines (`probe-cache-bytes`, `volume-bytes`, `table-*-bytes`),
  `world.budget` (region used/held/ceiling), `pipeline.budget`, `host.memory`** as the readings. Add the device
  memory reading as one more field of `host.memory`, from the backends, not a new verb.
- **`ShaderPipelineMemoryBudget` / `GpuMemoryProfile`** hold the device-local figure today. A process budget that
  includes device memory should be one authority (the account), with pipeline budgets as reservations from it, rather
  than a second budget beside it.
- The residency decision's machine byte budget (`docs/decisions/residency.md`) and Residency packet step 4 (eviction
  under one byte budget) are the home for GPU-side mesh limits.

## Draft brief: memory lane (next wave)

> Lane id: `memory-deck`. Writing lane on its own branch; GPU leg granted (windowed Direct3D 12 and a Vulkan boot of
> the debug world under `puck lease gpu`); no cold bake on the lead machine except the one stated below.
> Load: `rendering` (mesh-bake, mesh-gi references), `dotnet-performance` (data-and-memory), `verification`.
> Read: `docs/plans/next-wave/ground-memory.md`, `docs/plans/memory-pooling.md`,
> open-items "Memory ownership", "Residency and streaming", the Sweep packet's two [high] memory items.
>
> **Goal.** A fresh debug World, a full tour and a garden crossing fit a Deck-tier figure (working: 10 GB process plus
> device memory; report against it, do not gate on it) with no growth across a tour beyond bounded caches.
>
> **Do, in order.**
> 1. **Attribute before fixing.** Add device-memory usage and budget to `host.memory` from both backends (DXGI
>    `QueryVideoMemoryInfo` local and non-local; `VK_EXT_memory_budget`), cite at least two sources for each API.
>    Then, on one windowed Direct3D 12 World at `debug.go desktop` settled, account for private bytes: managed by
>    generation, mimalloc, account categories, `GpuRegion` host copies (count them), upload and staging heaps,
>    driver. Use an ETW VirtualAlloc/heap trace or VMMap. Close the ~8.4 GB "other" bucket to named holders.
> 2. **Fix the crossing's duplicate set at the cause.** The library keeps a geometry findable while any claim on its
>    payload lives, and its byte budget charges only cached-only payloads (reuse `Payload.Classify` and
>    `MemoryCategory.Cached`; no new counter). Read the crossing's `start` line to name why 1,048 groups moved. If the
>    cause is a static palette shift, extend renumber-sharing to static groups. Measure the garden round trip's private
>    and device peak before and after.
> 3. **Bound the GI caches.** `ProbeCache` byte-bounded (evict far cells, keep the window), retained triangles bounded
>    by bytes, and the GI blocks and scene arrays native under the account (job-system memo §4 item 3). Read
>    `probe-cache-bytes` per stop across a 74-stop tour before and after; growth must flatten.
> 4. **Make the bake's admission see its whole peak.** Move the fine-point rounds, masks and contour storage native
>    (open-items medium item). Derive the account's budget from a process figure that includes device memory on
>    unified-memory devices. One cold bake on Direct3D 12 with `host.memory budget` set to the Deck figure must settle
>    and must stay under it in private plus device bytes.
> 5. Record the CPU mesh copy's actual readers. Propose (do not build) releasing normals and LOD levels after
>    landing, with the bytes it saves.
>
> **Invariants to name in the commit bodies.** Library: a payload is findable iff it holds a claim (enforced by
> the payload's release, one door). Probe cache: bytes ≤ bound after every landing (enforced in the cache's one write
> path). Admission: a group's reservation ≥ its native and managed peak (enforced by `PeakOf`, one function).
>
> **Evidence.** `host.memory` (with device fields) at boot, after the tour and after `collect`; the crossing's peak;
> `world.residency` per stop; a windowed Vulkan boot of the debug world after the GPU-side changes. Determinism is
> untouched (presentation only).
>
> **Stop** when steps 1–3 land with their readings. Step 4 or 5 can hand back as a finding if step 1 reorders the
> priorities.

## Open questions for the owner (Needs you)

- The Deck figure: process plus GPU on a 16 GB unified-memory device. ~10 GB is this pass's working assumption.
- Whether a player's build may ship a prewarmed mesh store as a cache (derived from inputs, never compared by a
  gate). Otherwise every player's first boot is a cold bake, whose working set must then fit the Deck figure.
