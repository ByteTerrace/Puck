# s1-research-gpu: GPU-observed residency for Puck

Research lane (Codex) on the integration branch of the time. Research only: no source changes, builds, Worlds, or commits. Implementation and device measurements are proposed below, not performed.

## Scope and initial ruling

Read `AGENTS.md`, `docs/decisions/residency.md`, `docs/plans/bake-demand-tiers.md`, `docs/plans/streaming-and-memory.md`, and `.claude/skills/rendering/references/mesh-bake.md`; applied rendering and delegate skills. The brief overrides the delegate skill's usual build, link-check and commit workflow.

The useful immediate adaptation is GPU feedback into the existing selector, rather than a second streaming scheduler. Keep predictive view/portal demand for arrivals, add observed usage for retention and eviction, and distinguish desired missing detail from resident detail actually used. GPU observation stays presentation-only. It must never gate authoritative doors, simulation or contact.

Local constraints from the requested documents:

- `residency.md`: one selector across views and residencies; byte reservation from one physical accounting owner; offscreen shadow casters and GI blockers remain demand; misses degrade presentation only. Decision step 5 is byte-budget eviction.
- `bake-demand-tiers.md`: bake **tier** and draw **collapse level** are different. Each tier is a complete, input-identified mesh carrying collapse levels. Whole-tier publication is currently the safety boundary. Root demand is deliberately omnidirectional so a turn can reveal any nearby group; portal demand is aperture-bounded. GPU classification exists but is not yet read back per group for demand.
- `streaming-and-memory.md`, as its outline stood when the round ran: campaign step 2 is one byte authority, 3 attribution, 4 duplicate-copy removal, 5 priority eviction/compacted coarse vertices, 6 mapped portal demand, 7 shared lighting demand, 8 areas/terrain/moons, 9 managed churn. These campaign numbers differ from the residency decision's numbers.
- Mesh-bake reference: field sampling on GPU, contouring on CPU, stored meshes; one GPU mesh region. Derived bytes are caches: eviction loses rebuild/load time, never authored truth.

Step numbers in this report are the campaign outline's as it stood when the round ran: 2 one byte authority, 3 attribution, 4 duplicate-copy removal, 5 priority eviction, 6 mapped portal demand, 7 shared lighting demand, 8 areas, terrain and moons, 9 managed churn. [The plan](../streaming-and-memory.md#the-build-order) builds the same work as its steps 1 and 3, 2, 6, 5, 4, 9, 10 and 11, in that order.

## Evidence method

Every external source cited below will be opened, accompanied by an exact short supporting quote and its URL. Observations, deductions and proposed Puck designs will be distinguished. No hardware qualification is claimed in this lane.

## Opened primary evidence

The quotes below are deliberately short. PDF page numbers are one-based PDF pages, including title slides. Claims describe the dated publication, not a claim that every current engine release has identical internals. All URLs in this evidence section were opened. Two PDFs exceeded the browser fetch limit; I fetched their exact URLs with Python's HTTP client and inspected their relevant page text with `pypdf` in memory. Those are direct document fetches, not search summaries. No result below relies only on a search summary.

### E1. Nanite: hierarchy, requests, pages and eviction

**Epic's 2021 deep dive.** [Karis, Stubbe and Wihlidal, A Deep Dive into Nanite Virtualized Geometry](https://advances.realtimerendering.com/s2021/Karis_Nanite_SIGGRAPH_Advances_2021_final.pdf), PDF pp. 45–46, 122–127. Supporting sentences from speaker notes:

> It also evicts low-priority pages to make room in memory.

> Always having the root page means we always have something to render, no matter what.

The hierarchy is a DAG built by clustering triangles, grouping clusters, simplifying groups and reclustering. Selection must maintain a complete, crack-free cut. Fixed-size physical pages pack variable amounts of geometry into one GPU buffer; a split group activates only when all its parts arrive. The root page stays resident. Small culling metadata remains resident beyond the currently loaded geometry, allowing traversal to request desired detail directly. GPU culling emits page-range requests prioritized by projected LOD error, including refreshes for resident pages and requests from shadow views. CPU readback is asynchronous; CPU adds dependency pages, schedules high-priority reads, evicts low-priority pages, installs arrivals and patches pointers. This is demand paging, not an automatic GPU page-fault handler.

**Epic's overview.** [Nanite Virtualized Geometry Overview, “How does Nanite work?”](https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-virtualized-geometry-in-unreal-engine):

> During import, meshes are analyzed and broken down into hierarchical clusters of triangle groups.

The overview describes view-dependent cluster switching with matching boundaries, and fine-grained on-demand geometry streaming. This corroborates the design without making the 2021 implementation's numerical constants a Puck requirement.

**Pool and fallback are different concepts.** [Nanite Technical Details, “Resizing the Nanite Streaming Pool Size” and “Fallback Mesh”](https://dev.epicgames.com/documentation/unreal-engine/nanite-technical-details):

> The fallback mesh is used when Nanite rendering is not supported.

The configurable `r.Nanite.Streaming.StreamingPoolSize` bounds streaming data; a larger pool trades memory for less I/O/decompression. Too small a pool can thrash even at a static view. Always-resident root geometry supplies a normal streaming miss's coarse result. The separately generated conventional fallback mesh serves unsupported rendering paths and other consumers such as collision or ray tracing. These must not be conflated. Minimum Residency reserves root geometry bytes; the streaming pool is not the total renderer memory budget.

**Puck adaptation — recommendation/inference.** The strongest immediate transfer is the request protocol: keep lightweight planned bounds/error/tier metadata available even when payloads leave; report both wanted detail and resident detail used. Puck already has the input identity and whole-tier replacement mechanism. It can add feedback without adopting Nanite's cluster DAG, custom rasterizer or source-mesh import pipeline. Puck's current coarsest tier is the normal miss surface; no conventional “fallback mesh” subsystem is needed for this adaptation. Contact continues to read its authoritative field, never this fallback.

### E2. Virtual Shadow Maps: receiver demand and cached generated pages

[Epic, Virtual Shadow Maps, “Goals,” “Caching,” and “Overflow of Physical Page Pool”](https://dev.epicgames.com/documentation/en-us/unreal-engine/virtual-shadow-maps-in-unreal-engine):

> The pages are cached between frames unless they are invalidated by moving objects or light, which further improves performance.

VSM divides a large virtual shadow map into 128×128 pages, allocating/rendering pages needed by on-screen receivers found from depth. A shared physical texture pool is bounded by `r.Shadow.Virtual.MaxPhysicalPages`. Moving lights invalidate their pages; moving or changed casters invalidate overlapping light-space bounds. Fast motion, cuts and disocclusion expose uncached pages. The documentation prescribes reducing shadow resolution/light load or enlarging the pool on overflow; it does not promise automatically correct shadows at arbitrary exhaustion. Shadow pages are generated render results, unlike Nanite's disk-sourced geometry pages.

**Puck adaptation — hypothesis.** This is a close conceptual fit for derived data: identify a cached shadow result by the light/view mapping and the revisions of casters that can affect it; retain valid pages, regenerate misses. GPU receiver coverage can demand shadow-support geometry, including offscreen casters. However, Puck currently uses cascades, and a VSM replacement is a separate rendering project. First adapt receiver-driven residency and bounded cache attribution; investigate tiled cached shadows only after feedback and eviction work. A mesh retier changes the shadow result even when the authoritative field and light did not move. Invalidating only on transforms would be wrong.

### E3. MegaTexture lineage: predictive terrain windows versus visible-page feedback

**Terrain predecessor.** [van Waveren, Geospatial Texture Streaming From Slow Storage Devices (2007), introduction](https://mrelusive.com/publications/papers/Geospatial-Texture-Streaming-From-Slow-Storage-Devices.pdf):

> The texture data needs to be streamed from some storage device into memory before it can be used for rendering.

The paper describes bounded camera-centered square detail layers and updating their advancing rows/columns. This is predictive spatial working-set maintenance, not evidence of a GPU feedback-driven page requester. A movement spike can outrun streaming and leave detail arriving late. Its relationship to terrain-oriented MegaTexture is a historical inference, corroborated by the named implementation below; it should not be used as proof that every MegaTexture generation used the later RAGE feedback loop.

**Named implementation corroboration.** [id Software's published DOOM-3 source, `MegaTexture.cpp`, `SetViewOrigin`](https://raw.githubusercontent.com/id-Software/DOOM-3/master/neo/renderer/MegaTexture.cpp), exact comment:

> convert the viewOrigin to a texture center, which will be a different conversion for each megaTexture

This implementation converts the camera origin to texture coordinates and updates each level for that center; it also allocates fixed-size level images. It is direct evidence of the named view-centered approach, not evidence that this source exactly matches Quake Wars' shipped streaming pipeline. Its code is consulted as a second opinion, not as a specification or code to port.

**id Tech 5 successor.** [van Waveren, id Tech 5 Challenges (SIGGRAPH 2009), PDF pp. 15–21](https://mrelusive.com/publications/presentations/2009_siggraph/05-JP_id_Tech_5_Challenges.pdf), exact slide bullets:

> Cache handles hits, schedules misses to load in background

> With virtual texturing, you can globally adjust feedback LOD bias until working set fits

GPU feedback feeds analysis and prioritized page fetching; a physical page pool uses free/LRU/locked lists. Disk caching and GPU residency are managed separately. When a view exceeds the pool, feedback bias requests coarser detail. Coarse data is upsampled immediately and finer data blended as it arrives. None of this requires current sampler-feedback hardware.

**Detailed software VT mechanism.** [van Waveren, Software Virtual Textures (2012), §§3.1, 3.4–3.5, 4.5, 5.1–5.2](https://www.mrelusive.com/publications/papers/Software-Virtual-Textures.pdf):

> In other words, the finest mip level pages are replaced with a least recently used (LRU) replacement policy.

A shader page table maps virtual addresses into a fully resident physical atlas. Missing fine data resolves through a resident coarser ancestor; the coarsest mip can be pinned. Depth-tested feedback records virtual coordinates, ideal mip and texture identity; analysis can use a previous frame. Requests are prioritized by quality deficit and coverage. High/low water marks adjust feedback bias to stop oversubscription thrashing. Reuse unmaps a slot before replacing its bytes and publishes the mapping after the new page is complete. The paper also describes transparency and undersampled-feedback failure modes.

**Puck adaptation — recommendation/inference.** Keep prediction and observation together. Root/portal cameras provide first-arrival and turn coverage, while recent GPU use protects valuable detail. Under pressure, hidden fine payloads leave before visible detail is biased. Puck has a stronger recovery option than a fixed authored texture database: if the store entry is absent, the field program can derive the requested tier again. That increases recovery latency and temporary workspace demand; it does not make a miss free. Retain coarse coverage and prioritize visible error recovery over speculative refinements. Do not transfer texture-detail blending mechanically to geometry; Puck's published ruling already chooses subpixel LOD cuts and a short dither for late arrival.

### E4. D3D12 Ultimate sampler feedback: requests are not residency

[Microsoft, Sampler Feedback specification, “How to adopt,” “The MinMip Map,” and “MinMip feedback map”](https://microsoft.github.io/DirectX-Specs/d3d/SamplerFeedback.html):

> The MinMip map is used to ensure samples’ mip levels are clamped to what is actually resident.

Hardware feedback records ideal requested mip regions independently of the clamp. Applications inspect feedback, stream tiles of reserved resources, update their residency maps, and age out unused tiles. A pinned coarse mip/tail provides miss coverage. Feedback does not allocate memory, choose a global byte budget or supply texture data. Tier support is queried; 0.9 and 1.0 have different addressing/view restrictions. Thus a D3D12 Ultimate feature name does not replace capability discovery.

**Independent implementation evidence.** [Intel GameTechDev, SamplerFeedbackStreaming, sections 4–6](https://github.com/GameTechDev/SamplerFeedbackStreaming):

> A tile also cannot be evicted if it is being used by an outstanding draw command.

The sample processes GPU feedback in CPU workers, maintains tile dependencies/reference counts, keeps packed coarse mips, and clamps sampling with an application-built residency map. It also budgets feedback resolve work. Its behind-camera eviction shortcut is sample policy, not a safe Puck rule: a group can support a shadow, GI or another view.

**Puck adaptation — recommendation/inference.** This is most valuable as a generic semantic split: desired tier/level, available tier/level, and used tier/level are separate facts. Hardware sampler feedback applies to texture sampling, not arbitrary mesh-buffer access. Geometry needs explicit shader instrumentation. If Puck virtualizes image textures, hardware feedback can accelerate the same tile-request protocol that a software implementation supplies on Vulkan. It cannot by itself report procedural material evaluations or which SDF-derived mesh group was drawn.

### E5. Vulkan sparse residency: physical mapping is only one layer

[Khronos Vulkan specification, Sparse Resources, feature definitions and “Accessing Unbound Regions”](https://docs.vulkan.org/spec/latest/chapters/sparsemem.html):

> If this property is not present, reads of unbound regions of the image will return undefined values.

The property is `residencyNonResidentStrict`; when supported, unbound reads behave as zero and writes are discarded. `sparseBinding` alone still requires complete binding before use. Separate sparse-residency features permit partially populated buffers/images. Applications allocate physical blocks and change bindings; sparse residency neither chooses eviction policy nor generates an automatic streaming request. A miss must select a known resident coarse mip/stand-in in application logic. Zero is memory-access behavior, not a meaningful visual fallback.

[Khronos, VkPhysicalDeviceFeatures, `shaderResourceResidency`](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceFeatures.html):

> If this feature is not enabled, the `OpImageSparse*` instructions must not be used in shader code.

This separate capability exposes residency status from sparse image operations. Status is not a queue of desired tiles; shader code must turn it into requests, and needs enough virtual-coordinate/LOD metadata to identify them.

[Khronos, vkQueueBindSparse, Description](https://docs.vulkan.org/refpages/latest/refpages/source/vkQueueBindSparse.html):

> Batches begin execution in the order they appear in `pBindInfo`, but may complete out of order.

Rebindings must be ordered against each other and command submissions, commonly with semaphores. Publishing a mapping is not proof that its payload upload or previous readers have completed.

**Puck adaptation — recommendation/inference.** Start with software pages/suballocations in the existing ordinary GPU mesh buffer. They work without sparse-buffer support and keep the selector independent of API page size. Native sparse mapping is a possible allocator/transport optimization: stable virtual addresses and partial physical backing might reduce moves and transient whole-buffer growth copies. It will not solve mixed-level topology, decide what is wanted, or make missing indices safe. For textures, an atlas plus software page table is the portable route; sparse images are an optional realization of the same logical tiles. Query features, per-format granularity, mip-tail rules and sparse queue support on the actual device.

### E6. A published independent renderer: Disney's GPU request and touched-bit caches

[Lee, Zeichner and Li, A Texture Streaming Pipeline for Real-Time GPU Ray Tracing, SIGGRAPH 2025, PDF pp. 1–2](https://cdn.disneyanimation.com/uploads/publications/SIGGRAPH2025_A_Texture_Streaming_Pipeline.pdf):

> A fallback 1x1 tile is used instead, which is always resident for each tile of each face.

A shader forms a Ptex tile key and queries a GPU address hash table; a miss appends a request. CPU workers asynchronously prepare payloads and publish a new map once complete. A capped slab-backed cache uses per-frame touched bits to update LRU; usage recording is enabled near capacity. A bounded request buffer uses probabilistic rejection to reduce duplicate domination. This is a software feedback design, independent of D3D12 sampler feedback. The paper reports a CUDA ray tracer and ongoing rasterizer work, not a demonstrated Vulkan implementation. Progressive accumulation tolerates temporary low-detail samples better than a game's instantaneous rasterized view.

**Puck adaptation — recommendation/hypothesis.** A dense bit per group/level is simpler than per-pixel append requests and can be generated by existing selection or visibility-buffer consumption. It bounds feedback memory and suppresses duplicates by construction. As a hypothesis, always collect cheap selection bits but enable expensive sample/coverage statistics only under pressure. Do not copy the paper's random rejection policy by default: Puck can deterministically deduplicate its finite group IDs and protect all seats from starvation. Reuse its publication principle, through Puck's existing completed-slot readback and retirement mechanisms, rather than creating an independent CPU/GPU ownership path.

### E7. Hardware and apparently conflicting advice

[NVIDIA, DirectX 12 Ultimate announcement (2020), “Sampler Feedback”](https://www.nvidia.com/en-gb/geforce/news/geforce-rtx-ready-for-directx-12-ultimate/):

> It is enabled by a hardware capability in our GeForce RTX architecture called Texture Space Shading.

This places sampler feedback in the RTX architecture generation rather than making it an RTX 40-only feature. **Inference:** the RTX 2060 is a plausible hardware-feedback target, but the exact capability tier must be read from its installed driver. No tier was measured in this lane.

[Valve, Steam Deck tech specs, Compute/RAM](https://www.steamdeck.com/en/tech), exact table entries:

> GPU: 8 RDNA 2 CUs, 1.6GHz (1.6 TFlops FP32)

> 16 GB LPDDR5 on-board RAM (6400 MT/s quad 32-bit channels)

These are the OLED page's specifications, not evidence that every optional Vulkan feature is supported. [Mesa 21.0.0 release notes, New features](https://docs.mesa3d.org/relnotes/21.0.0.html) records:

> Sparse memory support on RADV

This is an independent driver source showing a mature software path, not qualification of a particular Deck/SteamOS driver, format or queue.

[NVIDIA, Advanced API Performance: Sampler Feedback (2023), “Not recommended”](https://developer.nvidia.com/blog/advanced-api-performance-sampler-feedback/):

> Proactively discarding the feedback writes is not required.

NVIDIA advises that all-thread feedback can beat selective writes on its hardware, and cautions against MIRROR/BORDER/MIRROR_ONCE. Microsoft's E4 specification instead permits all addressing modes at tier 1.0 while tier 0.9 restricts them. **Apparent disagreement:** implementation-specific advice versus the full API tier contract. Follow the queried tier and measure the device; do not universalize vendor advice. Likewise, Disney's probabilistic rejection addresses its finite software request queue, not hardware feedback-map cost. These policies solve different bottlenecks.

**Nanite/VSM feature floor.** [Epic, Hardware and Software Specifications, rendering-feature requirements](https://dev.epicgames.com/documentation/en-us/unreal-engine/hardware-and-software-specifications-for-unreal-engine), exact requirement bullet:

> DirectX 12 (with Shader Model 6.6 atomics), or Vulkan (VK_KHR_shader_atomic_int64).

[Epic, Linux Development Requirements, Nanite/VSM row](https://dev.epicgames.com/documentation/en-us/unreal-engine/linux-development-requirements-for-unreal-engine):

> GPU which supports the VK_KHR_shader_atomic_int64 Vulkan extension.

These list shader/atomic capabilities rather than requiring mesh shaders, work graphs or a particular upper-tier GPU. Unsupported Nanite rendering uses the conventional mesh fallback described in E1; conventional shadows remain an engine option in E2. **Puck inference:** the ideas do not require adopting those engine-specific 64-bit-atomic feature floors. A 32-bit feedback bitset and ordinary draw pipeline can implement Puck's first residency stage. Neither these requirements nor the device architecture names establish that Nanite or VSM meets Puck's frame/visual bars on a Deck; that would need a hardware measurement.

## Sources agree on mechanism; policies differ

- Nanite requests projected-error geometry ranges; software VT requests desired image tiles; Disney requests sampled Ptex tiles. All retain usable fallbacks and process arrivals asynchronously, but their observation granularity differs. A culling survivor is not proof it contributed a final pixel.
- Nanite's low-priority page eviction is not evidence of strict pure LRU. id's software VT uses finest-first LRU; Disney tracks use at frame granularity. Puck should select policy from its own quality, byte and re-derivation costs.
- Fixed pool size does not imply graceful overload. Epic explicitly documents Nanite thrashing and prescribes VSM overflow remedies. Puck's existing demand-bias and presentation-only miss contract must be retained and improved, not replaced with an assumption that virtual memory makes capacity disappear.
- MegaTexture's early spatial layers and later id Tech 5 feedback are successive approaches, not contradictory descriptions of one algorithm. The sources opened establish those mechanisms; they do not establish GPU feedback for the original terrain implementation.
- No source establishes that Puck should adopt CUDA, mesh shaders, work graphs, hardware sampler feedback or native sparse resources as its baseline.

## A Puck-native feedback and residency design

Everything in this section is a recommendation or hypothesis derived from the evidence and the requested repository documents. It is not a description of code newly verified or implemented by this lane.

### Yes: GPU observations can drive step 5 without virtualizing geometry first

Use `SdfMeshOcclusion` and the existing per-draw detail selection as the first feedback producer. A record needs the residency generation, stable geometry/group mapping, bake tier, selected collapse level, pass/view class and the observation epoch. GPU work can atomically OR dense bits or reduce per-group minimum desired resolution; CPU reads completed batches asynchronously. Shared instances aggregate to the geometry identity for residency while retaining occurrence/view information for demand and attribution. A material-id renumber must not create a new geometry-use history accidentally.

Keep three observations distinct:

| Observation | Producer and meaning | Selector use |
|---|---|---|
| Wanted | Projected-error selection against lightweight metadata, before residency clamping | Request missing tier/level; account visible quality debt |
| Selected | Culling survivor included in an indirect draw, after both occlusion phases | Conservative recent-use protection; cheap initial implementation |
| Sampled/contributed | Visibility-buffer consumption, texture lookup or relevant trace access | More precise retention/coverage evidence; optional additional instrument |

Selection feedback overestimates actual contribution when the bounds survive but no triangle/pixel does. That is acceptable for an initial conservative eviction policy if the counters name it accurately. A visibility-buffer reduction can later mark surviving surface groups/levels without a write for every triangle. Texture feedback must be written where the texture is really sampled, including glass or authored passes; final opaque pixels alone do not cover every consumer. A trace can require a blocker without returning it as its final hit, so hit-only feedback is insufficient for GI support.

**Avoid self-starvation:** a nonresident group cannot draw and therefore cannot generate “used” feedback. Likewise, a coarse fallback cannot report the missing fine payload's actual use. The wanted path must operate from planned bounds and resolution/error metadata independently of available mesh words. For a previously unbaked group, reuse the decision's planned-box approximation. Keep prediction for movement, turns, portal approaches and first visibility. Never erase unseen groups from the planning roster merely because no feedback arrives.

**A necessary policy change:** adding recent-use as another positive score while preserving omnidirectional root demand as an inviolable full-detail floor would free nothing. Under pressure, split predicted coverage from justified fine retention: protect coarse/prefetch coverage around the eye, but let persistently unobserved fine tiers become spare after a grace interval unless an interaction, imminent reveal, other view, shadow or GI support demands them. This is a proposed refinement of `bake-demand-tiers.md`'s root-camera policy, not a claim it already behaves this way. On a camera cut, explicitly reset/discount visibility history and raise predicted demand. Measure turn quality before setting grace intervals.

### Feedback belongs to all consumers and to one selector

Union root seats, nested/filmed views and mapped portal views that actually contribute, with each view's current display footprint. Carry the source demand class so a tiny screen cannot indefinitely protect geometry at the main view's former resolution. Keep camera, shadow, GI and picking requirements distinct, then combine them through the one selector. No rule here demotes an offscreen caster simply because the root camera did not draw it. Interaction support remains the existing explicit floor, not an inference from a color buffer.

Feedback is delayed evidence, never a complete current truth. A dropped or uncompleted batch means unknown, not “no use.” Feedback from an old document, retired residency, reused draw index, relocated page or changed generation cannot update a new occupant. Age observations in presentation epochs and retain the last valid evidence through readback jitter. The frame thread must not wait synchronously for feedback. Render-graph completed-slot ownership and existing retirement queues described in the rendering skill are the integration doors; do not create another fence owner.

### Byte authority and eviction order

Reserve fallback coverage, working mesh payloads, metadata, feedback/readback, replacement overlap, staging, probe/shadow caches and retirement-pending memory through the campaign's single physical account. On UMA, a host-visible GPU allocation is one physical allocation with multiple views, not two pools of free capacity. Separately allocated CPU mirrors/staging copies are real additional bytes. A retired range remains charged until its readers complete. Freeing logical meshes inside a committed buffer only frees slots for reuse; it does not reduce device allocation by itself. Shrinking/releasing backing storage is a separate operation.

Suggested pressure order, to be measured: cancel stale speculative work; drop unused decoded CPU parts that can restore; evict unobserved fine GPU levels; evict low-priority groups beyond protected coarse coverage; reduce oversubscribed detail only when the retained visible/support working set still cannot fit. Current coarsening may itself require a rebake: reserve its workspace and landing overlap before evicting the last useful representation. If allocation fails, lower the usable reservation and retry with reduced work; do not repeatedly request the same failed growth.

Priority should include projected quality benefit, all-consumer demand, recent use, bytes recovered and predicted next use. **Hypothesis:** store-read/rebuild cost is a useful retention tie-breaker; it must not let expensive invisible groups displace cheap visible necessities. Count “GPU miss, store hit,” “decoded-cache hit,” “store miss, re-derived,” cancellation, retired bytes and fragmentation separately. Re-derivability permits eviction but does not justify CPU/GPU duplicate copies everywhere or uncontrolled rebuilding. Keep recovery time/work bounded by the existing scheduler.

### Could mesh pages replace whole-mesh landing?

**Yes, in two different senses.** A transport page is an allocation/upload unit; a renderable unit is a complete surface replacement. Conflating them causes holes. The existing landing jobs already upload bounded pieces but publish only when every required piece stands; that mechanism is a head start, not evidence that arbitrary partial meshes are renderable.

| Option | What changes for Puck | Value and limitation |
|---|---|---|
| Whole-group/tier eviction with compact coarse vertices | Keep current publication, make each retained level's storage independently reclaimable | Best first step; no hierarchy rewrite. Current coarse indices share fine vertices, so index eviction alone saves too little |
| Page-backed complete chunk/level payloads | Stable logical mesh records name page lists/ranges; upload into free slots in the same region; publish a chunk/level only after all required pages arrive | Bounds allocation granularity and overlap; can reuse holes without moving other meshes. Current shader absolute addressing must be adapted or payloads kept contiguous |
| Existing spatial chunks as demand units | Page and retain chunk collapse levels separately, using their existing locked shared seams | Finer spatial eviction with an existing boundary rule. Do not assume those seams match across independently baked tiers |
| New within-group hierarchy with valid mixed-detail cuts | Derive cluster groups/dependencies/error metadata from Puck's mesher; draw a complete supported cut | Largest granularity benefit, largest change. Requires boundary, material, skin, glyph, glass and picking correctness; cannot be obtained merely by dividing byte arrays |
| Native sparse buffer backing | Same logical pages, optional API backing/mapping | May reduce moves and whole-buffer growth overlap. Does not create topology safety or remove Puck's current shader/storage-view addressing limits |

Begin page work with complete chunk/level manifests. Each manifest is derived from the bake inputs, tier, partition/codec algorithm and level; pages have reproducible identifiers under that manifest. Physical GPU slot numbers, camera names, last-used epochs and current budget never enter derivation identities. A missing store page can be reconstructed by regenerating its mesh/chunk first; independent page *generation* is a separate optimization and must not be promised just because independent page *storage* exists.

For useful page reads, the mesh-store codec needs independently decodable blocks/ranges or a bounded decode cache. Splitting only the upload while decompressing the full entry for every tiny miss can make storage traffic and CPU memory worse. Include decode scratch and directory/page-table cost when choosing page size. Page-local compact vertices duplicate some boundary data but permit actual reclamation; compare that cost with references/dependencies rather than assuming smallest pages win.

Retain a complete coarse representation until its replacement's pages and all dependencies stand. Existing chunks' fixed seam edges can support mixed collapse levels within a tier; arbitrary mixtures of bake tiers require a new seam proof. Once pages are real residency units, picking maps, material lookups, skin attributes, GI trace structures and shadow draws must resolve the same published generation. A page is reusable only after every submitted reader, including support passes and readbacks, completes. Frame-age heuristics alone do not prove that on an asynchronously progressing GPU.

**Current boundary to fix:** `.claude/skills/rendering/references/mesh-bake.md:804` documents an `AheadRefused` replacement giving up the old payload before the new one lands; line 827 documents arena refusals leaving draws out of staging, and line 833 documents device-refused region growth stopping the host. Those are gaps against `residency.md`'s promised coarse/haze miss behavior. Step 5 should close them through protected fallbacks, reserved overlap and recoverable allocation failure; feedback alone cannot repair them. This finding is from the requested documentation, not a newly reproduced run.

### What re-derivability changes at world/MMO scale

Across thousands of routed worlds, resident fallback roots and hierarchy metadata are not free merely because individual entries are small. Keep metadata/fallback coverage scoped to live presentation demand, including approaching portal destinations; never retain one permanent full hierarchy per world the account has ever joined. Worlds still own authority; the client selector owns presentation demand across them. An unseen world can keep simulating while its render cache is evicted completely.

Separate three cache lifetimes: source/compiled field availability; disk mesh products; GPU payloads. Evicting GPU bytes should usually preserve an inexpensive disk path. Disk eviction is also legal because inputs remain, but its rebuild queue must fit the same memory account and foreground quality deadlines. No GPU signal authorizes discarding simulation state or source documents required to rederive a demanded object. Re-derivability also assumes those source inputs remain obtainable; it cannot recover an unavailable remote authored asset from its hash alone.

### Portable baseline and optional acceleration

| Technique | Baseline for RTX 2060 and Steam Deck | Optional path and automatic fallback |
|---|---|---|
| Geometry-use feedback | Existing compute cull/selection, 32-bit storage-buffer bits/reductions, completed asynchronous readback; ordinary indirect draws and vertex shaders | Subgroup aggregation or richer instrumented passes only when measured; keep same group IDs and semantics |
| Mesh paging | Ordinary buffers, software page directory/suballocation, protected coarse payload and explicit retirement | Sparse-buffer backing or mesh-shader dispatch only if queried support and measured savings justify them; same manifests and complete render units |
| Texture feedback | Shader-produced feedback plus software atlas/page table and pinned coarse data | D3D12 hardware sampler feedback and reserved resources when their tiers fit; Vulkan sparse images/status when supported; otherwise software storage/request path |
| Cached shadow/support demand | Compute receiver analysis and bounded ordinary textures/buffers | Tiled cached shadows if they outperform the current cascade path at the visual bar; no dependency on work graphs or hardware ray tracing |

These are capability-level designs, not measured throughput guarantees. They rely on conventional compute/storage operations already within Puck's renderer rather than selecting an upper-device feature as baseline. Valve's/RADV's E7 evidence is insufficient to assert sparse formats, sparse queues, mesh shaders or a sampler-feedback equivalent on the user's Deck installation. Query the real RTX 2060 D3D12 tier and both devices' Vulkan capabilities. Keep one logical request/identity/fallback policy across implementations; only the producer or memory backing differs. Hardware acceleration earns its place with lower cost at equal visual correctness, not by being newer.

## Invariants, enforcement doors and limits

| Invariant | Existing door to extend or proposed enforcement | Guarantee stops at |
|---|---|---|
| Observations change presentation only | Residency decision; selector input never enters rule/state facts | Must review every new feedback consumer; this report adds no type enforcement |
| Used and wanted remain distinguishable | Proposed feedback schema has explicit observation kind and available/requested detail | Selected bits alone do not prove final contribution |
| Every rendered surface uses a complete available representation | Existing `MeshStands`/whole-tier handover; proposed complete page manifest/cut publication | Current first arrivals, refusal and `AheadRefused` gaps named above |
| No slot is reused while old work can read it | Existing completed-slot readbacks and retirement queues; page allocator must receive retired ranges only through that door | A history timestamp or swapchain frame count is not completion proof |
| Cache identity follows only derivation inputs | Existing geometry+bake identities; proposed page manifest derivation | Source availability and backend identity boundaries still matter |
| One physical byte reservation covers every residency | Campaign step 2's proposed account integration | Not yet built according to the plan; no new guarantee claimed |

The rendering skill was useful because it exposed the existing classification, complete-publication, bounded-upload and readback ownership doors. Its mesh-bake reference also prevents an incorrect proposal to invent chunking or claim that current coarse levels already free their vertices. No skill correction is made: this brief permits only its scratch report, and source implementation was not audited for drift.

## Ranked list: five techniques most worth adopting or adapting

Numbers below refer to the **streaming-and-memory campaign**, not the residency decision. Each item includes a future mechanical observation for the lead/build lane. No builds or Worlds were run here.

1. **GPU feedback for group/tier/level use plus missing-detail requests.** Adapt Nanite's wanted-versus-loaded traversal and Disney's compact touched bits at `SdfMeshOcclusion`/selection first. Changes **step 3** to attribute requested, selected, sampled and retained bytes, and **step 5** to evict hidden fine detail before visible coarsening; feed **steps 6–7** from all real views/support consumers. **Check:** at gallery-floor and meadow stops under the same byte reservation, count hidden fine bytes reclaimed, visible owed error, feedback lag/bytes and re-request churn. Include a 180-degree turn, disocclusion, split seats and a portal-only visible group. Decide whether selection bits suffice before adding pixel statistics.

2. **Protected coarse coverage and pressure-aware priority eviction with hysteresis.** Adapt software VT's pinned coarse data and high/low water behavior, using re-derivation cost only as a measured tie-breaker. Changes **step 2** to reserve fallback/overlap/retirement bytes and **step 5** to compact coarse vertices, degrade on miss, and recover from device refusal without ending the World. Uses **step 4** to release decoded CPU parts. **Check:** lower the reservation through several limits during a fixed path; named plus pending physical bytes stay within admission, no stale allocation is read, no missing surface appears where coarse coverage is promised, and a static view stops thrashing. Remove a derived store entry and observe counted reconstruction without authoritative-state change.

3. **Page-backed complete chunk/level payloads in one bounded mesh region.** Adapt the allocation/transport side of virtual geometry before a new cluster hierarchy. Changes **step 4** to permit bounded independent restore/decode, **step 5** to reclaim fine vertex payloads and avoid global relocation, and **step 8** to use terrain chunks/tiles as the same residency units. Native sparse backing remains optional. **Check:** compare whole payload versus page-backed storage on the debug world's largest groups; count live, slack, retired, staging, metadata and decode bytes, plus bytes re-uploaded on one landing. Randomize arrival/eviction order in a bounded prototype and audit all rendered boundaries/levels. Decide page size from total work and bytes, not allocator neatness.

4. **Portable virtual-texture cache with a common feedback contract.** Adapt id's shader page table/coarse miss path, with D3D12 sampler feedback or Vulkan sparse storage only as queried enhancements. Extends **steps 2–3** to texture physical pools, page tables, feedback, mip tails and request queues, and **step 5** to texture residency rather than building a parallel account. Supports **step 8** as authored image-texture demand grows. **Check:** first identify actual texture owners large enough to justify it; then compare software and accelerated producers on a shared texture-heavy debug-world view, recording requested/resident mips, visible error and total sampling/feedback/upload work. Missing fine tiles always resolve to a valid resident fallback. Procedural SDF materials are not texture data merely because VT is available.

5. **Receiver/support-driven caching for shadows and GI.** Adapt VSM's allocate-where-receivers-need-it and invalidate-what-changed principles; keep GI blockers/casters as first-class demand. Changes **step 7** to bound support caches and trace resolution by actual view demand, strengthens **step 5**'s offscreen retention, and follows **step 6**'s mapped portal demand. A full tiled shadow-map replacement is a later measured option. **Check:** stop the camera and count reused versus dirty pages/probes; then move one caster, change one light, retier one mesh and reveal one portal. Only affected support is rebuilt, no required offscreen blocker is evicted, and shadow/GI quality holds with bounded cache bytes. Compare against Puck's existing cascades and trace scene before committing to VSM-style storage.

## Open questions and owners

1. **How much budget is lost to root-camera omnidirectional fine demand versus actual multi-pass use?** Owner: feedback implementation lane/lead quiet window. Instrument before choosing eviction grace and prediction horizons; compare the two gallery floors, meadow and the same camera-turn path. This decides how to refine root policy without visible turn debt.
2. **Which feedback granularity pays: draw/chunk bits, group-level reductions, or final visibility pixels?** Owner: rendering lane. Measure feedback and readback work, false-positive retained bytes and per-view fairness with the same selection records; do not launch three independent streaming mechanisms.
3. **How much latency is store read/decode, landing, rebake or feedback?** Owner: attribution lane, campaign step 3. Timestamp the asynchronous stages and count per-request bytes/work. This sets lookahead and queue limits; no timing result is claimed by this report.
4. **Can current chunk boundaries support all desired independent resident levels, including skinned/glass/glyph groups?** Owner: mesh-bake lane. Audit the exact supported set and mixed-level boundaries; do not infer across-tier seam safety from within-tier collapse locking. If it fails, keep whole-unit publication or build a derived dependency hierarchy.
5. **What page size and decode block layout minimize total cost?** Owner: store/region lane. Vary logical pages and codec blocks on existing debug-world meshes; count slack, duplicated boundaries, page tables, random-read amplification and workspace peaks. Rebuilds may initially remain whole-chunk operations.
6. **How is fallback coverage maintained when its own total exceeds the current reservation?** Owner: residency lead. Bound roots by live far/aperture demand and use the already ruled haze/portal fallback when no geometry fits. A “root always resident for every joined world forever” policy cannot scale. Verify graceful behavior at forced low limits.
7. **Which actual optional capabilities exist on both bar devices' installed drivers?** Owner: platform/GPU qualification lane. Read D3D12 feedback/tiled-resource tiers and Vulkan buffer/image residency, format block/mip-tail properties and sparse queue availability; measure ordinary-page versus sparse backing. No hardware leg was granted to this research lane.
8. **What must GI retain beyond recent ray hits?** Owner: lighting lane, step 7. Establish conservative blocker support, acceleration-structure dependencies and invalidation on tier changes before using GPU observations to evict them.
9. **Which texture sources warrant VT now?** Owner: attribution/rendering lead. Separate authored static images, font atlases, procedural materials, live screens and render targets; only bounded cacheable texture products should enter this scheme. Content-heavy textures may rank below mesh-copy removal in the first wave.
10. **How much CPU state can disappear after GPU publication?** Owner: step 4 memory lane. Inventory picking, GI, store restore and renderer consumers, then remove mirrored payloads through their existing ownership doors. GPU-driven requests are not sufficient proof that every CPU copy is dead.
11. **How are stale work and request floods bounded across many routed worlds?** Owner: job-system/residency lead. Give requests generation keys, deduplication, cancellation and per-view priorities under one byte authority; demonstrate that a distant portal or expensive re-bake cannot starve the occupied seat.
