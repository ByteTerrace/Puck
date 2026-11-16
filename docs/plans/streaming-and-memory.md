# Streaming and memory

Residency is how Puck exists at the scale it aims for: when every zone,
character and server is a world, a player's machine holds what that player can
observe and little else. [Residency follows what is observed](../decisions/residency.md)
settled the design. This plan builds it, with the memory work inside it, and
it is the one home for residency and memory work: the selector, the byte
account, eviction and its floors, the source tile and its seam rule, lighting
residency, and the readings that judge them. Other plans and packets point
here for those and keep only what is theirs.

The owner's direction: streaming has not had "the proper focus/priority"; memory
usage and handling "could be SIGNIFICANTLY improved if we simply tried"; the
bars are "a good forcing function, but they end up hurting us sometimes too".

The page runs in this order: [how the bars are read](#how-the-bars-are-read),
[the seam walk](#the-seam-walk) that accepts the campaign,
[where residency stands](#where-residency-stands),
[what is guaranteed](#what-is-guaranteed-and-where-it-stops),
[the decisions](#the-decisions) and [the build order](#the-build-order).

## How the bars are read

Most of the conflict between the quality and performance bars has been
measurement, not trade-off: a comparison of two kinds of run, a machine loaded
by other lanes, a display that had turned off and dropped the GPU to idle
clocks. So:

- **One recipe for every bar.** The same build state, a quiet machine
  (`puck lease quiet`), the display on and composing, the same camera path,
  and the frame, bake and memory readings taken in the same window. A quality
  change and its cost are judged together, like with like.
- **The frame bar is the worst frame.** Performance is judged by the slowest
  frame at the highest quality, with counted work beside it
  ([the pipeline foundation](../decisions/rendering.md#the-pipeline-foundation)):
  every frame under the device's bar at `world.quality high`. An average, a
  median or a percentile is a diagnostic and never qualifies a device, and
  counted work explains a change without standing in for the timing. A window
  that crosses something (an arrival, a portal, a budget drop) is read by its
  slowest frame like any other.
- **The cold-bake bar is the boot's demanded set.** The bar's words are "the
  debug world's meshes bake from an empty store in one minute at most per
  backend on the lead machine". It is read by `puck bench startup --bake` on
  each backend from an empty store: the interval from the first mesh-bake
  start until every residency the boot holds has completed its bake at the
  boot pose. Under demand tiers a tier exists because a view demands it, so
  what bakes in that interval is what the boot's views demand: each group at
  the tier a boot view resolves and, once step 5 lands, the floor of every
  group in reach. That scope is derived from the bar's words and the
  derived-product ruling (a tier a later view asks for is a cache miss,
  derived and counted), not ruled by the owner in those terms; the owner may
  widen it. The minute stays absolute.
  Detail a later view owes is streaming cost, read by
  [the seam walk](#the-seam-walk), and the bake of every tier a tour demands is
  a trend beside the bar.
- **The memory bar is physical bytes.** The resident-byte target is set on the
  RTX 4070, and the Steam Deck is a second qualification on the same
  mechanism: the debug world's process plus GPU memory at or under 10 GB on
  the Deck tier at every settled checkpoint. Both are read from the disjoint
  physical rows of [the account](#what-adds-and-what-only-names), never by
  adding observations that overlap.
- **The seam bar is the seam walk.** A crossing in normal play shows nothing
  ([below](#the-seam-walk)).
- **A regression is a design problem, never a reason to drop quality.** When a
  look costs too much, the cost moves to where a viewer looks: detail demanded
  by what a view resolves ([bake demand tiers](bake-demand-tiers.md)), not
  detail removed.
- **Budgets per system.** Each place and system owns a share of the frame, the
  bake and memory, read through `world.budget` and `host.memory`. A change that
  needs more finds it within its system or makes its case; a regression has an
  owner instead of blocking every lane.
- **Bars are judged; trends explain.** The four bars above are each judged on
  its own reading. A tour's whole bake, peak memory along a tour and the
  per-unit rates (bake time per million samples, bytes per entity) drift with
  content and machine state: they are watched as trends and say why a bar
  moved, and none replaces a bar.

## The seam walk

The campaign is accepted by walking through the debug world's crossings and
finding nothing. The owner's ruling
([residency](../decisions/residency.md#the-owners-rulings-on-look-and-policy)):
a seam is whatever breaks immersion for the player in normal play conditions,
and a crossing is seamless when nothing a player sees, hears or feels in the
controls gives it away: "no hitch, pause, correction, camera jump, dropped
input, audio cut, lighting snap or late surface". Two things follow.

- The miss ruling and the seam ruling are different bars. "Never a hole" says
  what a miss may look like: the floor, the haze, a portal's fallback, a held
  image. Each of those satisfies it and is still a seam when a player sees
  it. On the walk every counted miss is a failure.
- A transfer that shows only in logs or tooling is not a seam. The walk judges
  what reaches the player (the picture, the sound, the controls) and uses
  counters only to say why something showed.

### What it walks

Three crossings, each a route segment in
[`worlds/debug/walkthrough-tests.puck`](../../worlds/debug/walkthrough-tests.puck),
the [walkthrough](walkthrough.md)'s mechanism: a controller program drives the
body from before the crossing's first demand to well past it, back again, and
through an abrupt turn at the threshold.

| Crossing | Kind | What it loads |
|---|---|---|
| The valley into the gallery: across the floor and through the entrance into the hall | An open zone into a walled interior, in one world | Terrain and far field giving way to interior detail, occlusion and areas, lighting from open sky to enclosed, exposure |
| A painting into the garden (the sweep's `garden` stop crosses it) | A portal into a routed world | Mapped demand, staging by demand, a second residency's floors and lighting, the crossing transaction, sound and input across worlds |
| The lift between the gallery's levels | Vertical travel through stacked floors, in one world | Demand led along velocity, floors a few metres away behind slabs, the shaft's lighting |

Each is walked at three speeds: walking, running, and the fastest travel the
standard package authors (the dash and free flight; the lift rides at its
authored speed and is entered at each). A frameless threshold between worlds
joins the walk when the standard package has its `threshold` template
([debug world content](open-items.md#debug-world-content)).

**Normal play conditions**, as this page reads the ruling until the owner
corrects it: a fresh process on a bar device, on each backend the device
ships, with the mesh store as a player's build ships it (prewarmed), taking
the first crossing after boot and then the return. The window is display-paced
with the ordinary lighting budget and temporal history: no `world.wait`, no
pause, no lockstep and no pinned exposure at the threshold, since each hides
what the walk measures. Two more legs are walked and reported beside those and
are never the acceptance: an empty store, and a lowered budget. A settled
capture of the same poses is taken apart, as the picture the burst is judged
against.

### What a window reads

One correlated window of presented frames for each leg, opened before the
crossing's first demand and closed after the return.

| Reading | From | Passes when |
|---|---|---|
| What the player received | A presented burst across the crossing (the tour's `presented` captures) and a recording of the mixed sound | No frame shows a missing surface, a floor where detail belongs, a fallback or held image, haze nearer than authored, a tier change, a crack, a lighting or exposure snap or a ghost; the sound has no cut, restart or underrun |
| Frame continuity | `host.frames` over the window, with `world.gpu-timing`, `render.record` and `gpu.queue.executes` to attribute | The slowest frame is under the device's bar; no tick is shed (`steps.shed`); every long frame has a named cause (upload, copy, bake, collection pause, compile, queue wait) |
| Demand met before use | `world.residency` across the window: `owed`, `owed-frames`, `tiers-owed`, `owed-error`, `refusals`, `relocations`, `unbound`, `late-stagings`; and what the steps add: floor shown, floor pending, fallback shown, reach drawn in, lighting owed, evictions and re-requests, and the latency of each stage from demand to first drawn | Every degradation count is zero in frames a view shows; a product a counter left out because it was refused or unknown is not zero |
| Physical bytes | `host.memory` per pool and segment: budget, usage as the API reports it, the ledger's held, peak and retired, reserved, and each row's residual | Held plus reserved stays under the spend limit; the residuals are named |
| Body, controls and sound across worlds | On the painting leg: the transfer's reserve and commit ticks, the body's pose and velocity either side, the seat's commands per tick, and the voices the mixer started and released | No tick without the body, none with two; no held control dropped and no edge repeated; no voice restarted that the author did not restart |

A pass proves the walked route under the walked conditions. It does not prove
arbitrary travel into content the machine cannot obtain in time: a first visit
to a world whose source must still be fetched has no unconditional guarantee,
its miss is counted, and its door still opens on simulation terms
([a door never waits](../decisions/residency.md#a-door-never-waits-for-loading)).
Ordinary reachable crossings are closed by fixing throughput, prediction and
continuity at their cause, never by holding a door or by calling haze seamless.

### Where a seam can show

Each way a seam can show at a crossing from an open zone into a city, the step
of [the build order](#the-build-order) that closes it, and what is left to
other work with its home. A row this plan does not close is measured by the
walk all the same.

| The player perceives | Closed here by | Left to |
|---|---|---|
| A late or missing surface; a silhouette changing from floor to detail; a thin wall or a sign vanishing | Floors first and admissible (step 5), staging by demand (4), the observed and speculative classes (8), the lead and areas (10) | Rows that keep their place across a hand-over: [arrival proportional to change](arrival-proportional.md) |
| A painting's fallback or a held image turning into the place; haze standing nearer than authored | Steps 3 and 5 count each; prediction (10) is what makes zero reachable | Nothing: it is this plan's |
| A crack or a line where terrain tiles of different detail meet | [The seam rule](#the-source-tile-and-its-seam-rule) (10) | Tile size and relief: [the terrain pass](terrain-pass.md). A parent drawn in place of its children: [virtual geometry](virtual-geometry.md) |
| A stall from an upload, a copy, a clear or a relocation | One background budget in bytes and time (3), backing that grows without a copy (7), relocation within the device (5) | A view node's first submission after a reconfiguration blocking on Vulkan: [GPU robustness](open-items.md#vulkan-regressions-and-gpu-robustness) |
| Bake, decode or disk starvation on an approach, an abrupt turn or a reveal | The lead and cancellation (10), bounded speculative work (3) | One pool with classes: the [job system](engine-job-system.md#6-build-order)'s J1 |
| A collection pause as an arrival allocates | Managed churn (11), CPU copies (6) | The allocation list in the [job system packet](open-items.md#engine-job-system) |
| A hitch from JIT, the loader or a pipeline-state compile on the first crossing | Nothing; the fresh-process leg measures it | [Edit to running, hot reload and boot](open-items.md#edit-to-running-hot-reload-and-boot) (the code warmer, a ReadyToRun comparison) |
| A frame-thread hitch from rebuilding the draw list, claims or tables on an arrival or a portal straddle | Nothing | [Arrival proportional to change](arrival-proportional.md) and the [visibility plan](visibility.md)'s instance split |
| A light leak, black or popping indirect light, a relight wave, a missing offscreen caster | Lighting ready before first sight, a trace tier apart from view tiers, support kept though offscreen (9) | What the light computes: [illumination](illumination.md) and [lighting and GI](open-items.md#lighting-and-gi); the cold shaft's difference between backends: [GPU robustness](open-items.md#vulkan-regressions-and-gpu-robustness) |
| An exposure, sky, atmosphere or tone snap at the gate | Nothing; a window already shows unexposed luminance its viewer exposes | Exposure blended at a seam: [awaiting the owner's review](open-items.md#awaiting-the-owners-review); continuous exposure and the frame handoff: [the valley and the moons](valley-and-moons.md). An authored change is judged against what was authored |
| A stale temporal image, a ghost, a change of scale, camera or up at the handoff | Nothing | [The valley and the moons](valley-and-moons.md)'s approach and handoff; row generations in [arrival proportional to change](arrival-proportional.md) |
| A reserve or commit delay, a body absent or doubled, a snapshot gap, a correction | Nothing: presentation demand stays out of authority | [Seams](../decisions/worlds-and-federation.md#seams) and [transfers](open-items.md#transfers-federation-and-server) |
| Held movement stopping, a tap or hold edge repeating, focus or target changing | Nothing. A crossing between authorities resets input holds and edges today (`WorldTransferActionContinuity`) | [Transfers](open-items.md#transfers-federation-and-server), through the seat's existing command path, never a second input route |
| Music, ambience or a voice restarting; a mute gap; the listener jumping | Nothing. An emitter whose identity changes re-enters from silence (`WorldAudioDirector`) | [Transfers](open-items.md#transfers-federation-and-server), with the [audio reference](../reference/audio.md) as the mixer's contract |
| A first visit waiting on definitions, assets or keys; a peer that cannot supply a floor | A local miss is derived again and counted | Obtaining and pinning a source: [the MMO foundations](mmo-foundations.md) and [hub and saves](hub-and-saves.md) |
| An eviction and re-request loop, paging, or the handheld competing with its own shell for memory | Admission before allocation (3), physical release (7, 5), re-admission held off (8), the Deck's readings | Nothing: it is this plan's |

## Where residency stands

Each line below is cited by symbol so `puck docs links` keeps it honest; where
a research report disagrees with the code, the code wins.

| The decision asks for | State |
|---|---|
| A measure | Partial. `world.residency` reads time until near groups land, resident triangles by tier, the region's held, named and limit bytes, the probe cache's bytes and the trace scene's CPU arrays. No device budget is read: `IGpuDeviceContext.MemoryProfile` is a size fixed when the device is created, and nothing in `src/` calls DXGI's `QueryVideoMemoryInfo` or enables `VK_EXT_memory_budget`. |
| Near-first ordering | Built: `SdfMeshDemand.Priority`, and detail bounded by what each view resolves (`SdfMeshTier`). A demand point is a view's eye with no lead along its velocity. |
| A selector across residencies | Not built. Each residency owns its own `SdfWorldResidency.MeshDemand`, its own bake budget (`SdfMeshPathBake.Retier`), its own mesh region and its own background GPU budget. Nothing unites them. |
| Lighting on the same demand points | Partly built. Each level of the probe window already centres on the box of every view's camera when it fits (`SdfMeshIrradiance.WindowLow`), and probe urgency reads every view. What remains: the window is anchored on the first view (`SdfMeshIrradiance.WindowCamera`), the lighting derives its own view list (`SdfMeshIrradiance.CaptureViewCameras`), the trace scene is built from every static draw of the frame (`SdfMeshIrradiance.StaticKey`), and `SdfMeshIrradiance.ProbeCache` is bounded by window cells, not bytes. |
| Demand mapped through portal faces | The mapping is built; its use is not. A fitted window's view is the viewer's eye taken through the glass, measured in the display pixels the glass spans and held to its frustum (`SdfViewSnapshot.DemandHeight`, `SdfViewSnapshot.DemandWithinView`), and every attached window's view is a demand point whether or not it renders (`SdfMeshDemand.Observe` leaves out only a view that is elsewhere, has no region or no valid camera). What is missing is arrival without a render: a residency's upload is submitted only for a scheduled render (`SdfWorldPasses.PrepareRender`), so an unseen window still renders at the least share (`WorldViewGraphHost.ReserveArrivals`, `WorldPortalVisibility.ArrivalShare`). |
| Eviction under a byte budget | Not built. Under pressure a group is planned again at a coarser tier or every demand is coarsened half a rung (`SdfMeshTier.Biased`); no group ever leaves. A mesh the arena cannot place is refused and its draws leave the frame (`SdfMeshRegionArena.Refused`), which shows as a hole. A growth the device refuses still ends the World (`GpuRegion.Grow` from `SdfWorldTables.EnsureMeshRegionCapacity`). |
| Authored areas | Not built. |
| Terrain tiles, then the moons | Not built: a bake group is an instance, a slot or a union, so one large instance has one tier and one demand box. |

What the memory side holds today:

- **The account is a host-memory admission promise.** `MemoryAccount.DefaultBudget`
  is three quarters of installed memory. Its owners are mesh payloads, bake
  workspaces and the store's transient decode and entry scratch. Only the bake
  reserves (`MemoryAccount.Owner.TryReserve`); a payload's charge never refuses
  (`MemoryAccount.Owner.Change`). It does not see device memory, the managed
  heap, `GpuRegion`'s host copies or staging, and at a settled stop it accounts
  for under a tenth of the process's private bytes **[reading]**.
- **Progress is forced once per bake.** A bake with nothing admitted admits
  its next group whatever the budget holds (`MemoryAccount.Owner.Reserve`,
  reported by `SdfMeshBakeReport.AdmittedAlone`), and every residency has its
  own bake, so several residencies each force a job at once.
- **Device bytes are counted by role, after the fact, and unbudgeted.**
  `GpuDeviceMemoryWork` counts every allocation both backends make at the
  driver's size, by the role the caller asked for and never by where the
  driver placed it: a preferred device-local request that fell back to another
  memory type still counts as device-local (`VulkanBufferMemory.PreferDeviceLocal`,
  `VulkanMemoryTypes.FindIndex`). Each door counts after the native allocation
  succeeded (`VulkanMemoryArena`, `DirectXHeapArena`), so nothing can refuse
  one. Resources share blocks of `GpuMemoryPool.BlockBytes`, a resource past
  half a block takes its own, and a key keeps one emptied block.
- **Two holders each take a quarter of the device, per instance.**
  `SdfWorldTables.MeshRegionLimitOf` gives every residency's mesh region a
  quarter of `GpuMemoryProfile.DeviceLocalBytes`, and
  `ShaderPipelineMemoryBudget.For` gives every pipeline instance another. On a
  Direct3D 12 unified-memory adapter that figure is dedicated plus shared system
  memory (`GpuMemoryProfile.FromDirectX`): the same memory the account takes
  three quarters of. With four residencies the limits sum past the device.
- **Background GPU work multiplies the same way, and is paced by
  presentation.** Each residency's tables own an `SdfGpuWork` with its own
  `SdfGpuWork.GpuShare` of GPU time and its own
  `SdfGpuWork.UploadBytesPerSecond`. Its jobs take work only at frame pumps, so
  a slow present paces the bake and the lighting, and a boot that withholds
  frames progresses only by staging them.
- **The region gives memory back only by replacing itself.** It grows by
  allocating a larger buffer and copying (`GpuRegion.Grow`), and shrinks the
  same way (`GpuRegion.Shrink` from `SdfWorldTables.FitMeshRegion`), so both
  need the new buffer while the old one stands.
- **Nothing reacts to pressure it reads.** `MemoryAccount.TrimCaches` runs only
  when `host.memory budget` lowers the budget or `host.memory collect` asks.

The duplicates this campaign removes, each one mechanism where the code has
several:

| One job | Today |
|---|---|
| A share of device memory | The mesh region's quarter and the shader pipeline's quarter, both through `GpuMemoryProfile.DeviceLocalShare`, neither a reservation |
| Demand points | `SdfMeshDemand.Observe` and the lighting's own `SdfMeshIrradiance.CaptureViewCameras`, with different filters |
| A byte ledger | `MemoryAccount` owners, `GpuDeviceMemoryWork`, the mesh library's own weight (`SdfMeshLibrary.ByteBudget`) and the region's named bytes copied into the bake (`SdfMeshPathBake.ObserveResidency`) |
| Retention | The library in bytes, the arena's slack in words (`SdfMeshRegionArena.RetainedSlackWords`), the trace scene in triangles (`SdfMeshIrradianceScene.RetainedTriangles`), the probe cache in window cells; the store's disk bound (`SdfMeshStore.DefaultCapacityBytes`) is a fifth, and stays, because disk is its own resource |
| Scheduling residency work | The bake's own static pool, the lighting's and the restore's thread-pool work, `JobPool`, and a budget per `SdfGpuWork` |

## How the research was weighed

Four reports fed this page and are kept beside it with their cited sources:
[shipped MMO streaming](streaming-and-memory/mmo-streaming.md),
[GPU virtual geometry and texturing with feedback](streaming-and-memory/gpu-streaming.md),
[device budgets on the bar devices](streaming-and-memory/device-budgets.md), and
[a reading of Puck's own residency and memory code](streaming-and-memory/puck-today.md).
Each is a record of its round: where one disagrees with this page or the
current source, this page and the source win, and a step number in a report is
the earlier outline's. Their sources disagreed in four places, each settled in
the decision it bears on: how much of a budget to spend (from 80% of total
memory to the whole reported budget), how often to read it (from every frame
to never per frame), and what the non-local group means on unified memory, all
in [decision 2](#decision-2-what-is-read-how-often-and-how-much-is-spent); and
whether coarse coverage is always resident, in
[decision 5](#decision-5-the-unit-is-a-tier-and-the-coarsest-tier-is-the-floor).

No shipped game the round read publishes a whole-client byte authority or its
eviction order, so the borrowed parts are shapes (coarse first, neighbours by
topology, keep-alive, disclosure apart from presentation), and the authority is
Puck's own design.

## What is guaranteed, and where it stops

Two statements, kept apart because only the first is a bound.

**Admitted bytes.** Every byte in an admitted class is charged once, to the
pool of the physical memory it occupies, before it exists, and the admitted
classes together do not pass that pool's spend limit. The admitted classes are
device allocations, native storage held by an owner, and the workspaces of
bakes, scene builds and transfers. Two exceptions pass the limit, and both are
counted: the one job forced so that work settles, and a presentation whose
least does not fit the device
([what gives way](#decision-5-the-unit-is-a-tier-and-the-coarsest-tier-is-the-floor)).
A miss changes only what is shown, and is counted.

**The envelope.** The process also holds memory Puck measures and does not
admit: the managed heap beyond its named tenants, the native allocator's
slack, the runtime's code and loader heaps, driver and swapchain memory. Each
is a row, and what the elastic class may hold is the spend limit less the
fixed classes and these rows, so their growth shrinks detail. When the elastic
class reaches nothing and they still grow, the bound has ended: that state is
printed and counted, never hidden, and no simulation is unloaded to make room.
The campaign moves bytes from the envelope into the admitted classes, and its
reading is how much of each pool is still only measured.

What enforces the first, once [step 3](#the-build-order) lands, at one door
each:

- **Device memory.** A backend allocates a block only with a grant from the
  admission door, and `GpuDeviceMemoryWork.CountAllocated` takes that grant, so
  a device byte cannot be counted without one and a grant is made nowhere
  else. The door is asked at the size the device will be asked for (a block of
  `GpuMemoryPool.BlockBytes` or a resource's own block), not at the size of the
  resource that prompted it; a resource placed in a block that has room asks
  for nothing.
- **Native storage.** An elastic owner grows only through
  `MemoryAccount.Owner.TryReserve`.
- **Forced progress.** One job in the process, not one per bake, may be
  admitted past the budget so work always settles
  ([decision 3](#decision-3-one-reservation-across-residencies-with-floors-and-classes)).

Where it stops: an allocation a door makes without counting is invisible to
the account, and the reading that shows one is the API's usage against the
ledger; swapchain images and driver memory never reach a door; the envelope is
measured. Until step 3 lands the account reports all of this and holds none of
it, and no step may claim otherwise.

The second invariant is the residency decision's and is unchanged: only
presentation follows the observer, so nothing here reaches a rule, a door or a
state hash.

### What adds and what only names

Readings of one process overlap, so the account says which rows add and which
only name a part of one.

**Physical rows are disjoint and are the only rows that add.**

| Row | Is | Read from |
|---|---|---|
| Device blocks | Every block the backends hold from the device, per segment, each once: used ranges, the room inside a block, a kept empty block and a retired resource whose fence has not passed | The ledger (`GpuDeviceMemoryWork`) |
| Native committed | What the native allocator has committed: every native payload and the allocator's slack | `IAllocatorStatistics` |
| Native outside the allocator | Direct native allocations, such as `GpuRegion`'s host copies today | Each such owner; the row goes to nothing as they move onto the allocator |
| Managed committed | What the collector has committed: every managed object, a managed-backed `OwnedBuffer`, and every array rented from the shared pool, in use or kept by the pool after its return (`RentedArrays`) | The collector's last reading, with its age |
| Runtime and driver private | Code and loader heaps, stacks and the driver's host-side memory | The residual of a page walk of the process |

On a discrete adapter the device pool is the device blocks in the adapter's
own memory, and the host pool is everything else, the device blocks placed in
system memory included. On unified memory every row is one pool.

**Attribution rows name a part of exactly one physical row and add nothing.**
A mesh payload's owner names part of native committed; the trace scene's
arrays name part of managed committed; a mesh region names part of the device
blocks and, for its host copy, part of a native row. For every physical row
the account prints the residual, the row less what its owners name: slack for
the allocator, unnamed tenants for the managed heap, unnamed blocks for the
device. A released byte stays in its physical row until the backing is given
back: a rented array returned to the pool moves from its tenant to the
residual, and a retired buffer moves to `MemoryCategory.Retirement`, and
neither is counted as relief until the row itself falls.

**Observations are never added.** Process private bytes, the working set, the
API's own usage figure and the OS's per-process GPU counters each overlap the
rows above: private bytes include the address space of mapped device memory,
which on a discrete adapter is mostly not resident, and the API's usage
includes driver and swapchain memory beyond the ledger. They check the rows
and are printed beside them with the overlap named.

**Sampling.** Owner charges and reservations are read together under the
account's lock (`MemoryAccount.ReadOwners`), and the ledger under its own. The
allocator's figure, the collector's and the API's are samples taken at other
instants, each printed with its age. Admission uses what the account holds
exactly (charged and reserved bytes) plus the last sample of each measured
row; it never calls the collector or the driver. A residual that skew makes
negative prints as nothing and is counted.

### The large host owners

Each owner of host memory large enough to matter, with what bounds it. "Named"
means measured and printed with no bound: it is what the envelope still holds.

| Owner | Bounded by | Step |
|---|---|---|
| Mesh payloads on the CPU | The selector's grant: a payload exists because a tier is resident or cached, and [decision 6](#decision-6-what-a-landed-mesh-keeps-on-the-cpu) releases the planes a landed mesh no longer needs | 3, 6 |
| Bake workspaces: final lattices, contour tables, the material cut's lists (`SdfMeshMaterialCut`) | The workspace class's reservation, with one forced job in the process; a group whose peak passes the class is split or failed by name | 3 |
| The trace scene's CPU hierarchy and a scene build's pieces beside the old scene | A reservation for the build's peak; the hierarchy goes when placement reads the GPU trace | 9 |
| `GpuRegion`'s host copies and staging | Reserved with the region they mirror, at the same grant | 2, 3 |
| The probe cache's blocks | A cached owner in bytes, trimmed first | 9 |
| The composed-document store | Released after boot where nothing reads it | 6 |
| Speculative work: bakes, loads and decodes for a predicted demand | A share of the workspace class, cancelled when the demand leaves or pressure asks | 3, 10 |
| Rented arrays the shared pool keeps | Large rents move to owned storage under an owner | 2, 11 |
| Native allocator slack | Collected when an arrival settles and on pressure (`IAllocatorStatistics`); named | 2 |
| The managed heap's other tenants | Named by a heap walk; each one past a stated size becomes an owner with a capacity. No hard limit on the heap: it would turn pressure into an exception where everything else turns it into a coarser picture | 2, then per tenant |
| Runtime code and loader heaps | Named; not controllable at run time | 2 |
| Driver memory and swapchain images | Named, as the API's usage less the ledger | 1 |
| Capture and media buffers, and a pagefile-backed section no Puck code creates | Named by step 2's page walk before anything is cut | 2 |

## The decisions

Each decision names its options, the evidence, what the favoured one closes and
leaves open, and the strongest case against it. **[opened]** marks code this
page read; **[reading]** a measurement whose numbers are in a landing commit;
**[external]** a published source, linked under [sources](#sources);
**[inference]** this page's reasoning.

### Decision 1: one account, with pools by physical memory

**The question.** What "one byte authority" means when a discrete card has two
physical memories and a handheld has one.

| Option | Closes | Fails at |
|---|---|---|
| A. Keep separate budgets and divide the quarter among residencies | The limits summing past the device | Every new holder needs its own share; unified memory is still counted twice |
| B. A device budget authority beside `MemoryAccount`, as a GPU allocator library keeps | Device admission | Two authorities; on unified memory both promise the same bytes |
| C. One account whose owners charge a pool: host memory and device memory on a discrete card, one pool on unified memory | All of the above | The account must learn pools, and every device allocation must reach it before it is made |

**Evidence.** The residency decision already says one reservation "from the job
system's single physical byte-accounting owner", and the
[job system memo](engine-job-system.md#4-memory-ownership) names `MemoryAccount`
as that owner. `GpuRegion` sits in a project below the account's, so it can
report only through `IAllocationObserver`, which `MemoryAccount.Owner`
implements **[opened]**; the backends reference only the abstractions, so the
ledger and the admission door reach the account through interfaces the host
wires. On a unified adapter Direct3D 12's local group and its system pool are
one memory **[external: D3D12 memory pool]**, and RADV on an APU reports two
heaps that split one memory two thirds to one third **[external: RADV]**.

**Favoured: C, with three words kept apart.**

- A **pool** is one physical memory. `GpuMemoryProfile.UnifiedMemory` chooses
  the layout: device and host on a discrete card, one on unified memory.
- A **segment** is what an API budgets: a DXGI segment group or a Vulkan heap.
  Every segment lies in one pool, and several may lie in the same one (RADV's
  two heaps).
- A **role** is what the caller asked for (`GpuMemoryRole`). It attributes; it
  does not place.

An allocation is charged once, to the pool of the segment it was placed in,
and it is admitted only when that pool's spend limit and that segment's own
limit both hold. The segment is a second condition on the same charge, never a
second charge. The pool is derived from placement: on Vulkan the heap of the
memory type the allocation used, on Direct3D 12 the segment group of the
heap's memory pool on that adapter **[external: D3D12 memory pool]**. The
ledger keeps both, so an allocation whose role and placement disagree is
counted instead of debited to the wrong memory. The managed heap and native
allocations always charge host memory.

**Closes.** The double count on unified memory; the two unrelated quarters; a
fallback placement debited to the wrong pool; a pool with room admitting an
allocation into a heap that has none. **Leaves open.** A limit is only as good
as its reading ([decision 2](#decision-2-what-is-read-how-often-and-how-much-is-spent)).
**Strongest case against.** `MemoryAccount.Owner.Change` never refuses, and a
count made after an allocation cannot refuse either, so a pool is a ledger
until allocations ask first. That is true, and it is why the ledger
([step 1](#step-1-as-a-brief)) and admission (step 3) are separate steps and
the first claims nothing of the second.

### Decision 2: what is read, how often, and how much is spent

**The question.** Where a limit comes from on each backend, how the reading
stays current without a driver call inside an allocation, and what share of it
Puck admits.

**The readings.**

- **Direct3D 12.** `IDXGIAdapter3::QueryVideoMemoryInfo` gives, per segment
  group, a budget, the process's usage, the bytes available for reservation
  and the current reservation; all four are kept. On a discrete adapter the
  local group is the device pool's segment and the non-local group is a
  segment of the host pool; on a unified adapter only the local group is read,
  and the two are never added **[external: D3D12 memory pool, D3D12MA, DXVK]**.
  The adapter is held for the device's life; today
  `DirectXNativeDeviceApi.ReadAdapter` opens a factory and an `IDXGIAdapter1`
  for each read **[opened]**.
- **Vulkan.** `VK_EXT_memory_budget` gives a budget and usage per heap, enabled
  when the device lists it (`VulkanLogicalDeviceFactory` enables no such
  extension today **[opened]**). Each heap is a segment with its own figures;
  heaps are never summed into one budget, since a heap with room does not help
  an allocation that must go in another. Without the extension the heap sizes
  stand in and the reading says it is an estimate.
- **Host.** `HostMemory.AvailablePhysicalBytes`, the committed managed heap and
  the native allocator's committed bytes, as `host.memory` already prints them.

**How the limits are formed.** A segment's limit is its reported budget less
the larger of a tenth of it and the account's headroom. The host pool's limit
is the smaller of `MemoryAccount.DefaultBudget` and what the host has
available plus what Puck's physical rows already hold, less the headroom. A
discrete card's device pool is limited by its segments.
On unified memory the one pool takes the host's limit, because it is the
host's memory, and a device allocation obeys its segment's limit as well: host
bytes are never held under the smaller figure the GPU can reach, and a device
allocation is never admitted on pool room alone. Which of the two binds on the
Deck is a reading
([open questions](#open-questions-and-their-owners)), and it chooses nothing
but numbers in this formula. A tenth is a starting value in one place;
[step 2](#the-build-order)'s readings move it, from the bytes a budget drop
takes and how fast the selector answers, not from a count of drops.

**How often.** The sources disagree on the rate and agree on the substance:
cache the reading, refresh it on a signal or at a low rate, and correct usage
in between from your own counters **[external: RDNA guide, D3D12MA, VMA,
Microsoft residency]**. Puck plays nice with its host, so:

| Option | Fails at |
|---|---|
| Query inside every admission | A driver call on the allocation path |
| Poll on a timer thread | A thread that exists only to poll |
| Refresh on the host's signal, correct from the ledger between | Vulkan has no signal |

**Favoured.** On Direct3D 12 the budget-change event
(`RegisterVideoMemoryBudgetChangeNotificationEvent`) marks the reading stale,
and the frame thread refreshes a stale reading at the top of a frame; Windows
sets the event once at registration **[external: DXVK]**, which gives the
first reading. On Vulkan the frame thread refreshes at a low fixed rate and at
once after any refused allocation or reservation. Between refreshes, usage is
the last reading plus the ledger's change since, so admission never calls the
driver. No thread spins or polls.

**The OS reservation.** The device bytes of the floors
([decision 3](#decision-3-one-reservation-across-residencies-with-floors-and-classes))
are passed to `SetVideoMemoryReservation` as the working set Puck cannot go
without, clamped to what the reading says is available for reservation and
passed again when that changes **[external: Microsoft residency]**. The call
can fail and its result is recorded. It is a hint the OS weighs, never
capacity the account counts on. On Linux no memory manager absorbs an
overcommit, so the same limit is a hard ceiling there and reserving before
allocating is mandatory **[external: NVIDIA Vulkan guidance; inference for the
Deck]**.

**Closes.** A budget that ignores other programs and the player switching away;
an `ID3D12Device` or `VkDevice` refusal as the first sign of pressure; one
scalar standing for heaps with their own limits. **Leaves open.** The Deck's
real numbers and whether one margin fits both backends
([open questions](#open-questions-and-their-owners)).

### Decision 3: one reservation across residencies, with floors and classes

**The question.** How the residencies of a presentation, the view targets, the
shader pipeline and the lighting share a pool, instead of each taking a
fraction nobody granted.

| Option | Closes | Fails at |
|---|---|---|
| A. Divide a fixed share by the count of live residencies | Limits past the device | A painting's quarter-scale room and the occupied valley get equal shares |
| B. Each holder reserves from the account by class; the elastic class is one reservation the selector splits by demand | The above, and it scales with any count of worlds | Needs the selector the decision names |
| C. One mesh store for the whole presentation | Sharing by construction, and one copy of a mesh several worlds draw | Needs [decision 8](#decision-8-pages-are-three-things-and-only-one-changes)'s backing first: one region grown by copy doubles everything at once |

**Favoured: B now, with C as the direction.** Holders, in admission order:

1. **Fixed by the views.** View targets, cascades, screens, program tables and
   an installed shader pipeline graph, each admitted at its real size before
   it is allocated. A refused target keeps the one it replaces; the pipeline's
   replacement peak (`ShaderPipelineMemoryAccount.PeakBytes`) becomes a
   reservation, and a refused one keeps the installed graph running, as
   `ShaderPipelineMemoryAccount.RefusalCode` does today.
2. **Workspace and transfer.** Staging, upload and readback, a bake's and a
   scene build's working peak, and a compaction's room: one reservation for
   the process, sized by the landing rate, which every job draws from before
   it starts and returns when it completes. One budget of GPU time and upload
   bytes serves the presentation, shared by priority, in place of a quarter of
   the GPU per residency, and it is a rate of wall time: a completed job's
   share is taken up again on its completion through a host wait, not only at
   the next frame's pump, so a slow present no longer paces the bake.
3. **Residency floors.** What each demanded residency cannot draw without: its
   draw records, the floor of each group within its reach, and its lighting
   floor ([decision 5](#decision-5-the-unit-is-a-tier-and-the-coarsest-tier-is-the-floor),
   [decision 9](#decision-9-lighting-reads-the-same-demand-and-pays-on-demand)).
4. **Support.** Detail a view does not see and the picture still needs: the
   demanded tier within the interaction reach, since picking reads the GPU,
   and shadow casters and light blockers within their support regions
   ([residency is broader than visibility](../decisions/residency.md#one-selector-passive-by-default-explicit-by-choice)).
5. **Elastic detail.** One reservation for mesh detail, the lighting's finer
   levels and their trace scene across every residency: what the limit leaves
   after the classes above and the measured rows.
6. **Caches.** The `Cached` category, in bytes, trimmed first.
7. **Measured, never admitted.** [The envelope](#what-is-guaranteed-and-where-it-stops)'s
   rows.

**How the elastic class is split.** Not by fractions. Each consumer measures a
group in its own resolving unit: a view in display pixels of the one viewer (a
window's demand is the pixels its glass spans), a cascade in its shadow texel,
the lighting in its probe spacing. A product is wanted at the finest tier any
consumer that uses it asks. A pool's grant is its spend limit less its
measured rows, and the selector holds one sum under it:

> fixed + workspace + floors + support + observed detail at bias *b* ≤ the grant

and raises or lowers the one detail bias *b* to hold it. On a discrete card a
product costs bytes in both pools, and the one bias holds the sum in each.
**Speculative detail**, wanted by a prediction and used by no pass, is not in
that sum: it is admitted into what the sum leaves spare, lowest priority last,
and is the first thing to leave. So hidden detail can never coarsen detail in view, and
letting it go buys a finer visible tier because it was never counted against
one. Until [decision 7](#decision-7-prediction-decides-arrival-and-observation-decides-retention)'s
feedback exists, every wanted product counts as observed, which is today's
behaviour. A residency's region limit becomes what its own draws name at the
bias plus room for a replacement, in place of a quarter of the device;
`SdfMeshTier.BudgetEighths` and the bias logic in `SdfMeshPathBake.Retier`
keep working, reading the selector's grant where they read one residency's
limit. The error an over-budget view shows is then the same number of pixels
in every world it sees, which is the fair split and needs no tuning.

**Admission before allocation.** The account already holds the arithmetic of a
handoff: an owner's promise is its reserved peak less its bytes, so a byte
charged to the owner that reserved it consumes the promise and adds nothing
(`MemoryAccount.Owner.TryReserve`, `MemoryAccount.Owner.Change`). What is
missing is the door. Every controllable device allocation names the owner that
pays for it and asks before the native call, at the size the device will be
asked for:

1. The caller reserves its peak. A growth reserves the old and the new buffer
   together.
2. The backend's block allocation asks the admission door for a grant against
   that owner: the pool's spend limit and the segment's limit both hold, or
   the answer is no and nothing was allocated.
3. The native allocation runs. If the device refuses, the grant is returned,
   the segment's limit is lowered to what is held, the reading is refreshed
   and the caller keeps what it had. Nothing throws.
4. `GpuDeviceMemoryWork.CountAllocated` takes the grant and charges the block
   to the owner, which consumes the promise.
5. A released resource stays charged as retirement until its fence passes and
   the block is freed; only then does the row fall.

An allocation with no reservation behind it draws on unreserved room or is
refused. Two callers can no longer spend the same spare bytes, because the
promise is taken under the account's lock before either allocates.

**Forced progress.** `MemoryAccount.Owner.Reserve` admits a job whatever the
budget holds so that work always settles. It becomes one allowance for the
process: one forced job at a time across every bake, drawn from the workspace
class, in place of one per bake. A job whose peak passes the whole class is
split where it can be (a lattice baked in parts) and failed by name where it
cannot (`SdfMeshBakeReport.Failed`).

**Evidence.** Each residency reads its own limit and budgets alone, and its
budget is unbounded until the first limit is read
(the bake's region budget, read in `SdfMeshPathBake.ObserveResidency`)
**[opened]**. The quarter was sized so one region and the copy its growth holds
fit beside everything else ([the region's budget](bake-demand-tiers.md#the-regions-budget));
four residencies each holding that limit void the sizing as soon as a second
one is large. Shipped practice splits the same way: a floor that must be
resident and an elastic pool that streams **[external: Microsoft residency]**.

**Closes.** Limits summing past the device; a quarter of the GPU per residency;
the refused growth that ends the World; the unbounded first plan; a forced job
per residency; background work paced by presentation. **Leaves open.** Stopping
growth is not giving memory back: this decision keeps a pool from growing past
its limit and says nothing about a pool already over one, which is
[decision 8](#decision-8-pages-are-three-things-and-only-one-changes)'s. The
fixed classes of many worlds can themselves pass a small pool; decision 5 says
what gives way. **Strongest case against.** One bias punishes the occupied
world for a hungry window. It does not, because a window's demand is measured
by the display pixels it covers, and a window no view observes is speculative
and outside the sum.

### Decision 4: a residency is staged by its demand, not by a render

**The question.** How a world behind a portal becomes resident before its glass
is seen, without rendering views nobody looks at.

**Evidence.** The render graph's package already pumps and prepares every
residency it holds each frame, seen or not (`SdfWorldPasses.BeginFrame`), and a
prepared residency packs its frame. Only a scheduled render stages it
(`SdfWorldPasses.PrepareRender` calls `SdfWorldResidency.Stage`), and staging
is where the frame's upload is submitted (`SdfWorldTables.SubmitUpload`).
Background work binds only after an upload of the current binding revision
(`SdfWorldTables.BindsBackgroundWork`), so a residency that packs without
uploading stops landing and counts `unbound` pumps instead **[opened]**. That
is the whole reason `WorldViewGraphHost.ReserveArrivals` renders hidden windows
at `WorldPortalVisibility.ArrivalShare`. The demand is not missing: an attached
window's fitted view is a demand point whether or not it renders, measured by
the display pixels its glass spans from the viewer **[opened]**.

| Option | Fails at |
|---|---|
| A. Keep rendering hidden windows at the least share | A render per arriving world per frame; a world behind a rate-held camera view never arrives; the search and its three share cases to maintain |
| B. Stage every held residency that owes an upload, whether or not a render reads it | Each residency's own GPU budget, until decision 3 shares it |
| C. A second pump outside the render graph | A second owner of uploads and fences |

**Favoured: B.** Staging moves from "a render asked" to "the residency owes an
upload". Five things make that true, and each is part of the step:

- **What "owes" means.** Meshes arriving, or the binding revision ahead of the
  last upload, or a program or table upload pending. A predicate over mesh
  bytes alone loses a binding revision and strands the background work again.
- **A hidden stage pumps and never drives.** `SdfWorldResidency.Stage` drives
  the background work at the GPU's pace when its frame is withheld
  (`SdfGpuWork.Drive`). A residency no render reads is staged under the shared
  budget only, so a world nobody sees cannot hold a frame.
- **A residency first held this frame is staged this frame.** The package
  holds a new residency while it refreshes its entries, after the prepare
  loop, so the stage reaches it there or the frame is counted as one late.
- **Sight is published, not inferred from a render.** A window's demand point
  stays whether or not its glass is seen: the pixels the glass spans from the
  viewer are what its first sight will resolve, so the world behind it is
  ready before the glass shows. What sight changes is the class of that
  demand: a world no view observes is speculative
  ([decision 7](#decision-7-prediction-decides-arrival-and-observation-decides-retention)),
  admitted into spare and first to leave. The schedule's sight of each window
  (`WorldViewGraphHost.PortalSightOf`) is what says so.
- **The wait reads what was admitted.** `world.wait residency` waits on every
  held residency's admitted demand, where `SdfWorldPasses.PendingMeshesIn`
  reads only what the latest schedule displays, and it lists a world the
  selector has let go apart from one that still owes, so a capture never
  waits on a world that was unloaded on purpose.

`SdfWorldResidency.MeshesArriving` loses its "only frames that render one of
its views bring them" clause, and the arrival search, the least-share cases
and the arriving-world query are deleted once chained portals and a rate-held
filmed view land without them; the hidden render is not kept as a stand-in.
`SdfWorldResidency.Staged` keeps its late staging for a graph installed during
the frame, counted as it is now.

**Closes.** The stand-in; the one extra view; the world behind a camera view
that never arrives; `unbound` pumps; `world.wait residency` settling while an
unseen routed world still owes. **Leaves open.** Lookahead beyond attached
windows (a door two rooms away) is authored areas' and the velocity lead's, in
[the build order](#the-build-order). Why `world.wait residency` can time out
in the painting wing after its bakes finish is not yet known (continuous new
demand, or an upload that cannot retire); the step reads it again under the
new wait. **Strongest case against.** An upload for
a world nobody sees costs GPU time the frame could use. It is bounded by the
one shared background budget, it is far less than a render, and it is the work
that makes the crossing free.

### Decision 5: the unit is a tier, and the coarsest tier is the floor

**The question.** What leaves under pressure, and what a viewer sees in its
place. The owner's ruling is in
[the residency decision](../decisions/residency.md#the-owners-rulings-on-look-and-policy):
a group that has not landed, or was evicted, "shows its coarsest level, which
stays resident within the far distance; where no level is resident it reads as
distance haze in the world's sky colour, never a hole."

**Evidence.** A residency holds exactly one tier's mesh per group. Freeing
detail today means baking or loading a coarser tier and landing it beside the
fine one before the fine one goes, so relief needs memory first. A refused
mesh's draws are filtered out of the staged list, and a group's first mesh has
nothing to draw meanwhile **[opened]**; the rendering reference says plainly
that this "shows as a hole". Every tier is a complete mesh identified from its
inputs, cached in the library and the store on its own, and each group's
coarsest tier is its own unrefined lattice (`SdfMeshTier.Coarsest`)
**[opened]**. Baked offline at its coarsest tier
(`puck mesh audit --world worlds/debug --cpu --tier 24`), the debug world is
about a twentieth of the triangles it has at its finest, a few hundred
megabytes of vertex, index and material words **[reading]**. That is a partial
estimate of payload and no more. It leaves out the groups the CPU evaluator
refuses, skin attributes and member tables, draw records
(`SdfMeshRecord.DrawWords` words each), block granularity and every body past
one standing at rest for each look, and it says nothing of whether a coarse
mesh still covers what it stands for. The floor is probably affordable and is not
yet shown to be **[inference]**.

| Option | Closes | Fails at |
|---|---|---|
| A. Coarsen only, as today | Nothing new | Relief needs memory first; a refusal is a hole; first arrival is a hole |
| B. The coarsest admissible tier of each group in reach is a second, kept product; the demanded tier is detail above it | Eviction with no bake and no overlap; refusal and first arrival show the floor | The floor's bytes, to be measured by class |
| C. Collapse levels inside one tier own compacted vertices, and fine levels drop | Finer steps | A new mesh layout in the codec, the region and every kernel, for a step a tier already is |
| D. A cluster hierarchy with mixed-detail cuts inside a group | The finest granularity | A new mesher contract and a seam proof across tiers; nothing in reach needs it yet |

**Favoured: B.** A tier already owns its vertices, so the floor is a tier and
option C is not built. Under it:

- **A floor must be admissible.** `SdfMeshTier.Coarsest` bounds a lattice's
  spacing; it certifies neither that the mesh is non-empty nor that it covers
  what the finer tiers cover. A thin wall or a line of lettering can contour
  to nothing at its coarsest rung, and keeping an empty mesh resident fixes
  nothing. A group's floor is therefore the coarsest tier that passes a
  coverage check made when it is baked, as the mesh audit's `uncovered` and
  `open` metrics check a prototype offline; for most groups that is the
  coarsest rung. The check is part of the bake, so the floor tier follows from
  the group's inputs like any identity, and both products come from the one
  contouring path: there is no second mesher for floors.
- **A group in reach draws its floor until its demanded tier stands, and again
  the moment that tier is evicted or refused.** Evicting is handing the draws
  back and forgetting the detail's words, in the selector's priority order,
  lowest first. Nothing is baked to free memory.
- **The floor lands first, nearest first**, so an arriving place is whole and
  coarse before it is sharp. This is the coarse-first shape the shipped games
  share **[external: EVE download on demand; Nanite deep dive]**. A group
  whose field changes (a new geometry key) bakes its new floor first and draws
  the old floor until it lands.
- **A group beyond the far distance draws nothing**, as now, and reads as haze.
- **A relocation copies each kept mesh within the device**, as a growth already
  does, instead of landing every mesh again from the host, and the old draws
  stay drawable until the copy completes.

**What gives way, in order, each rung counted.** When the grant does not hold
everything wanted:

1. Caches are trimmed.
2. Speculative detail leaves.
3. The one bias coarsens observed detail, evenly in display pixels.
4. A world no view observes gives up its floors, its lighting and its tables,
   and its window shows its authored fallback.
5. Observed windows drop to their fallbacks, the fewest display pixels first.
   The occupied world never does.
6. The occupied world's resident reach is drawn in from the far distance,
   farthest groups first, with the haze drawn in to the same distance, and
   never nearer than the interaction reach.
7. Past that the presentation's minimum does not fit this device. Nothing more
   is removed: the state is printed with the shortfall and counted, the fixed
   classes stand above the limit, and a device refusal keeps what each door
   had. A device below a world's minimum is a fact about the content and the
   device, reported, never papered over at run time.

No group nearer than the haze is ever absent, with one exception the mechanism
admits and counts: a group whose floor has not landed yet or failed to bake
has no surface to show. Floor-first ordering and prediction are what keep that
case out of view.

**Where the floor stops being a bound.** A floor follows demanded residencies
and their reach, and that alone bounds neither a hub nor a crowd:

- **A hub.** Many windows in view at once are many floor sets, and none of
  them is unobserved. What bounds a world's cost is that everything it holds
  for presentation is demanded and reclaimable: today a routed world builds a
  full residency with tables sized from the host at attach, and rung 4 needs
  those tables to be built on demand and given back. A world's priced minimum
  is its draw records, its floors in reach and its lighting floor, and rung 5
  is the order among windows when the minimums do not all fit.
- **A crowd.** A crowd beside the eye has no far tail to shed. What bounds it
  is sharing: a mesh is keyed by its geometry, so one mesh can serve every
  body of a look, a character's own cost is its rows, and a crowd's floor cost
  is the count of distinct looks, not of bodies **[inference]**; step 5 prints
  what a body costs to confirm it. A crowd of distinct looks is priced at one
  floor each, and what a character shows when that does not fit is an
  authored look ([the owner's questions](#the-owners-questions)).

**Closes.** The hole on refusal, on first arrival and on a replacement the
region has no room for (`SdfMeshRegionArena.AheadRefused`); relief that costs
memory; a floor that scales with worlds ever joined. **Leaves open.** A single
huge group still has one tier
([the source tile](#the-source-tile-and-its-seam-rule)); the floor's real
bytes by class, and how a skinned floor holds in an extreme pose, are step 5's
first readings. **Strongest case against.** Two resident meshes for a near
group. The floor is a small fraction of the detail it stands under, and it is
what lets every other byte be evictable.

### Decision 6: what a landed mesh keeps on the CPU

**The question.** Which CPU copies can go once the GPU copy is resident, and
what each release needs first.

**Evidence.** A store-published mesh already lets its normals and coarser
levels' indices go and restores them through one door (`SdfMesh.Hold`) when a
reader needs them. That door restores only those two planes: its storage
declines positions, level-zero indices, materials, members and influences
**[opened]**. Positions, level-zero indices and triangle materials stay, and
the region reads a mesh's words again through the hold for a flush, a
relocation, a moved mesh and another residency's landing
(`SdfWorldTables.MeshWordsOf`); the lighting's trace scene claims every static
mesh and builds its CPU hierarchy from positions and level-zero indices,
leaving dynamic and glass meshes out (`SdfMeshIrradianceScene.IsStatic`); a
renumbered variant reads the materials; a device loss lands everything again.
A hold can also run a restore on its caller or wait for one (the restore's
`NeedResident`). The mesh library forgets a mesh a residency still holds, so a
later plan decodes a second copy (`SdfMeshLibrary.TryTake` misses it). Every
one of these bytes is a cache: the store holds the same planes, and the field
can derive them again.

**Favoured: a landed mesh keeps nothing a reader cannot ask for through the
hold, and each reader is retired in the order its prerequisite lands.**

| Copy | Needs first | Then |
|---|---|---|
| The library's second copy | Nothing | A payload stays findable while any claim lives, and the library's weight charges cached-only payloads (`MemoryCategory.Cached`) |
| Native allocator slack | [Step 2](#the-build-order) naming it | Collected when an arrival settles |
| The composed-document store | Confirming what a reload and a hot reload read: `WorldDefinitionFileSource.ForgetComposedDocuments` has no caller in `src/` | Released after boot where nothing reads it |
| Dynamic and glass meshes' positions and level-zero indices | The restore widened to those planes, with a mesh's counts, bounds and slices kept apart from the planes they describe; an inventory of every reader by the path it reads on, not by whether the lighting uses it; and a landing that asks for its words ahead and reads them once they are resident | Released through the same door. Relocation, a second residency's landing and a device loss restore from the store |
| Static meshes' positions and level-zero indices | The row above; decision 5's relocation within the device, so a pack reads no host words; probe placement and depth classification reading the GPU trace ([decision 9](#decision-9-lighting-reads-the-same-demand-and-pays-on-demand)) | Released; a device loss restores from the store as a warm boot does |
| The trace scene's CPU hierarchy | The same migration of placement, depth classification and the diagnostics that read it | Freed |

**The rule that keeps a release from becoming a seam.** A reader on the
frame's path never restores: it reads words a need raised earlier has already
made resident, or it reads nothing this frame. The hold stays the one
ownership door; widening it is not a second restoration path. Restores, store
misses and any restore the frame thread waited for are counted, and the last
must stay at nothing.

**Closes.** A second copy of most of what the region holds; the library's
double load. **Leaves open.** The managed heap is the larger owner and its
tenants are not yet named; step 2 names them before anything here claims the
Deck target. **Strongest case against.** Restores from disk on a turn. The
rule above prevents it, and restores are counted (`world.residency`'s store
line), so a release that causes them shows.

### Decision 7: prediction decides arrival, and observation decides retention

**The question.** Whether GPU feedback replaces CPU-derived demand.

**Evidence.** Demand is computed on the CPU from planned boxes, which works
for a group that has never landed, and a root camera demands in every
direction and through walls, so a turn finds its meshes
([the region's budget](bake-demand-tiers.md#the-regions-budget)). That same
blindness is why the meadow settles half a rung coarse: detail nobody can see
holds bytes detail in view needs. The occlusion pass already selects every
draw each frame (`SdfMeshOcclusion`), and its kernel emits a command for each
phase it keeps a draw in (the entry, the late step, the exit layers and the
glass list); nothing reads that back per group **[opened]**. Virtual geometry
and virtual texturing both separate what is wanted from what is resident and
used, and both keep a coarse answer for a miss **[external: Nanite deep dive;
software virtual textures]**.

| Option | Fails at |
|---|---|
| A. CPU demand only | Hidden detail is kept while visible detail coarsens |
| B. The GPU emits the requests | A group that is not resident cannot report; a turn has no history; first arrival has none |
| C. The CPU predicts what to bring; the GPU reports what was used, and that decides what stays | One readback, late by a few frames |

**Favoured: C.** Wanted detail stays what a consumer resolves, computed from
planned boxes, with the root camera's all-round demand kept for arrival. The
GPU adds the one fact only it has, and it sorts wanted detail into
[decision 3](#decision-3-one-reservation-across-residencies-with-floors-and-classes)'s
two classes.

- **What is recorded.** A product is observed when any phase of any pass
  emitted a command for it this frame: views, cascades and the exit and glass
  lists alike, so a caster out of view is still used. The mark means "selected
  for submission", which is conservative; it does not claim a pixel. The
  entry-visible state alone would miss the exit and glass uses, so the mark is
  set where a command is emitted and nowhere else. It carries the class of
  consumer, so a group only a cascade or the lighting uses is kept at the tier
  that consumer asks and not at a view's.
- **What it is keyed by.** A staged row moves and is reused, so a mark names
  the row with the row's generation and the table's epoch, and the mapping
  from row to group and tier is captured for the slot when it is staged. A
  mark whose generation or epoch no longer matches is stale and means unknown.
- **How it is read.** Through the raster node's own completed slots, as the
  occlusion's sight copies are (`RenderGraphSlotResults`), with the same
  handling of an abandoned slot. No new executor and no second fence owner.
- **What it does not cover.** The lighting's trace and picking are not raster
  passes: the first is demand of its own class, the second is the support
  class. A view held at a lower rate still shows its last image, so its last
  observation stands while that image does.
- **What it changes.** Under pressure, detail no consumer has used for a grace
  interval is speculative: outside the fitted sum and first to leave. With
  room to spare nothing changes. A dropped, late or stale batch means unknown,
  never unused. Detail within the near reach of an eye is observed whatever
  the feedback says ([the owner's questions](#the-owners-questions)).
- **What a prediction may hold.** A predicted product is brought as far as the
  library or the store, where it is cache, and lands on the device when spare
  allows or when it is observed. A product pressure evicted is not admitted
  again until spare has held its bytes for a quiet interval that doubles when
  pressure returns soon after (the bias already waits this way,
  `SdfMeshTier.BiasQuietFrames`), so the same bytes do not leave and return in
  a loop.

The baseline is an ordinary storage buffer and a readback every bar device
has; hardware sampler feedback and sparse residency are not part of it.

**Closes.** The bias at the meadow where occlusion would free the bytes;
a prediction forcing bytes resident that nothing uses. **Leaves open.** How a
fast turn feels when hidden detail has left
([the owner's questions](#the-owners-questions)); whether a mark per draw is
the granularity that pays against one per group or a final-visibility read
([open questions](#open-questions-and-their-owners)). **Strongest case
against.** Feedback is late. The retier's hold already absorbs a frame-late
answer, and the floor is what a late answer shows.

### Decision 8: pages are three things, and only one changes

**The question.** Whether pages replace whole-mesh landing, and how a pool
that is over its limit gets under it.

"Page" names three different things in the systems the round read, and Puck's
answer differs for each.

- **Transport is already paged.** Landing uploads a mesh in bounded pieces
  under the upload budget and publishes it once every piece stands. Nothing
  changes.
- **Publication stays whole.** What a frame draws is always a complete mesh of
  one tier, which holds by construction
  ([what publishes](bake-demand-tiers.md#what-publishes-and-what-the-frame-draws)).
  Fixed-size pages below the mesh would put a page-table read in every vertex
  fetch and buy nothing for publication, because Puck's units are complete
  surfaces already. Where a group is too large to have one tier, the unit
  shrinks in the source, not in the mesh
  ([the source tile](#the-source-tile-and-its-seam-rule)).
- **Backing changes, and backing is what gives memory back.** The region is
  one buffer that grows by allocating a larger one and copying, so its peak is
  the old and the new together, and one region addresses under four gibibytes
  (`GpuRegion.MaxByteCount`). It shrinks the same way. So when a budget falls
  below what a region holds, evicting names frees room inside the buffer and
  returns nothing to the device, and the only release is a smaller
  replacement, which needs memory at the moment there is none. A device-side
  relocation spares the bus, not the replacement.

**The pressure transaction.** Four amounts are kept apart for every store:
backing held from the device, ranges in use, bytes promised to work in
flight, and backing retired behind a fence. A falling limit is answered in
this order, and each step is honest about which amount it moves:

1. Growth stops. No new promise is granted and the bias rises. This needs no
   memory and moves nothing held.
2. Names are evicted. Ranges in use fall; backing does not.
3. Backing is released. With slabs an emptied slab goes back to the device
   with no replacement. With one buffer it cannot, short of releasing the
   region whole and landing its floors again, which is counted and is the last
   resort.
4. After the fence, the row falls. Relief is claimed here and nowhere earlier:
   a sum of grants and named bytes cannot show it.

Compaction, where a store is fragmented, takes its room from the workspace
class reserved in advance, never from the pool it is trying to relieve. The
backend pool's kept empty block (`GpuMemoryPool`) is released by the same
pressure, through that pool, not through a second trimming allocator.

| Option | Gives | Costs |
|---|---|---|
| Sparse backing of the one buffer | No shader change; memory committed as the region fills and decommitted as it empties | Reserved resources and sparse binding are capabilities, not guarantees **[external: Microsoft residency]**; the addressing ceiling stays |
| Slabs: fixed-size buffers, each mesh inside one, a draw naming its slab | No capability question; growth adds a slab and an emptied slab returns to the device; no single-buffer addressing ceiling; one store for every residency, so a mesh several worlds draw is resident once | Every kernel that reads mesh words indexes an array of buffers; a mesh larger than a slab takes its own; slab count is bounded by descriptors; fragmentation inside slabs |

**Favoured.** Slabs shared by the presentation are the direction, because they
also make decision 3's option C true and returning a whole buffer is the
reliable way to give memory back **[external: Microsoft residency]**. The
choice between slabs and sparse backing is not made here: it rests on a
capability reading on the three bar devices and on the indexed read's cost in
every kernel that reads mesh words (views, glass, the cascades, the lighting's
trace and picking), which a design page cannot measure. It is
[step 7](#the-build-order): a prototype of the indexed read with a GPU grant,
measured before the region's allocator changes, pricing fragmentation, the
largest payload, the descriptor count and what the backend pool keeps. Because
physical relief depends on it, the backing lands before
[step 5](#the-build-order) claims any; until then growth reserves its peak
(decision 3), so the copy is admitted, not hidden, and step 3 claims only that
growth stops.

### The source tile and its seam rule

Terrain is the group too large to have one tier. This plan owns its unit and
the rule where two units meet; [the terrain pass](terrain-pass.md) owns what a
tile is made of, and [the valley](valley-and-moons.md) is the place that
proves it.

**The unit is a source tile, and a tile is an ordinary group.** A terrain
placement declares a tile size, and the stamper emits one static instance per
tile, the field cut by the tile's box in the field's own space. Each is its
own bake group, so it has its own tier, demand box, floor and identity, and
the selector needs no new unit. `SdfMeshChunks` stays what it is: a partition
of one tier's contour along that contour's own edges, for drawing and
collapse. Its locked seams hold inside one tier and say nothing between
tiers, because two tiers of one field are two contours with different edges.

**The rule: neighbours overlap, and each tile is closed on its own.**

1. A tile's box is its cell of the tiling grown on every shared face by the
   error of the tile's coarsest tier: one cell of it (`SdfMeshTier.Cell` of
   `SdfMeshTier.Coarsest`) unless the audit measures more. That is a constant
   of the tiling.
2. Every tier of a tile is a closed surface: the cut is closed by a face that
   lies inside its neighbour's solid.
3. So any two neighbours at any two tiers, a floor against the finest
   included, draw overlapping closed solids, and no ray from outside the
   terrain passes between them. "Never a hole" holds across a tile border by
   construction, not by matching vertices.
4. What can still show is a step no taller than the two tiers' summed error,
   where one tile's surface stands proud of the other's. With both at the
   tier their demand asks that is about a display pixel at most, each tier's
   cell being under half of one there (`SdfMeshTier.PixelShare`); with one at
   its floor it is the floor's own counted coarseness. The closing face takes
   the terrain's normal and material at its rim, not the cut's, so a step
   under a pixel shades as the surface.
5. A tile's identity hashes its field, its box and its tier, and never a
   neighbour's tier, so a retier touches only the tiles it retiers.

It is one rule for every field, overhangs and caves included: the closing face
is the skirt, turned inward along the field instead of hung below an edge.

| Rejected | Because |
|---|---|
| Contouring a shared border at the pair's coarser spacing, and transition cells between levels | Both tie a tile's mesh to its neighbour's tier. With a floor allowed beside any tier the pairs multiply, and a retier ripples into every neighbour |
| A border contoured at one fixed spacing for the tiling | Watertight and independent, but a coarse line runs along every border of fine terrain |
| Skirts hanging below each open edge | They assume a height field |
| A locked chunk boundary | It holds within one tier only |

A parent drawn in place of its children is a different contract, replacement
of a whole by a coarser whole, and stays with
[virtual geometry](virtual-geometry.md). If the overlap shows on the rim check,
the fixed-spacing border is the alternative to try.

### Decision 9: lighting reads the same demand, and pays on demand

**The question.** What "lighting on the same demand points" still means, now
that the window already follows every view, and how a place is lit the first
time it is seen.

**Evidence.** The gaps are the trace scene's input, the byte bound and the
second demand derivation, not the window's shape **[opened]**. Every residency
owns an irradiance volume (`SdfWorldResidency.MeshIrradiance`) and stages it
on every prepared frame, seen or not. The trace scene holds the tier it
traces, so a retier wave is paid again in the lighting
([the lighting follows the tier](bake-demand-tiers.md#the-lighting-follows-the-tier)).
Its CPU hierarchy also feeds probe placement
(`SdfMeshIrradianceScene.TryNearestSurface`), depth classification
(`SdfMeshProbeDepth`) and diagnostics, while the GPU copy feeds the trace, and
a full scene build holds its pieces beside the old scene. Before a level first
publishes, a surface is lit by the volume's camera sample as one probe
([mesh GI](../../.claude/skills/rendering/references/mesh-gi.md)), so a place
seen before its window has published shows that stand-in and then steps to
its probes, and a cold lift shaft reads near black until its static draws and
first sweeps complete.

**Which tier the light traces.** A view-driven retier must not change what the
light sees.

| Option | Gives | Costs |
|---|---|---|
| A. The group's floor | A tier fixed by the plan alone; no second product, since the floor is resident anyway | Detail the floor drops no longer shadows; an admissible floor keeps a thin wall by definition, which is what decision 5's check is for |
| B. A tier sized by the probe spacing of the finest level that reaches the group | The same independence from views, with detail where the probes can resolve it | A third product for a group near a fine level |
| C. Follow the view tier and invalidate only probes a changed box can reach | No extra mesh | The retier's traces remain; only the whole-window relight goes |
| D. Converge at a capture only | Nothing at run time | A walk owes a drain at each tick that saw a retier |

**Favoured: the lighting is a demand class of its own whose resolving unit is
the probe spacing, which is B, with A as the case it reduces to wherever the
spacing asks for nothing finer than the floor.** The step's first reading is
whether A alone holds: the gallery's lit exhibits and a thin partition, traced
at the floor against the view tier. If it does, floor and trace tier are one
product. C stays the fallback and D is not built.

**Favoured, for the rest.**

- **One demand.** The lighting reads the selector's demand points instead of
  deriving its own, and the scene covers what the window reaches.
- **Lighting follows demand, not first sight.** A residency's lighting is
  built when the selector demands its world: a window within reach of a led
  eye, an opening of the occupied area, or a view that sees it. First sight is
  too late a deadline: starting then shows the stand-in and a step, or holds
  the picture, or hitches. A world nothing demands still pays for no volume.
- **A lighting floor.** What a first frame needs is part of the floor class:
  the trace scene over the floors in reach and the window's first publication
  about the mapped eye, nearest first. It is sized like a window's mesh
  demand, by the display pixels the glass spans, so a small or distant glass
  asks for the coarse levels only and a volume's levels are allocated as they
  are demanded. Its debt is counted apart from geometry's.
- **What a miss shows.** A window whose destination's geometry floor or
  lighting floor does not stand shows its authored fallback and is counted. A
  place in the occupied world shows the camera sample and is counted. Neither
  is black and neither reads open sky through walls that have not landed.
- **Placement on the GPU trace.** Probe placement, depth classification and
  the diagnostics that read the CPU hierarchy move to the GPU trace. This is
  work of this step, not a consequence of choosing a tier, and it is what
  lets [decision 6](#decision-6-what-a-landed-mesh-keeps-on-the-cpu) free the
  hierarchy and the static meshes' planes.
- **Bytes, and a bounded build.** The probe cache, the scene's retained
  geometry and the arena's slack become `Cached` owners in bytes under the
  account, in place of cells, triangles and words. A scene build reserves its
  peak from the workspace class before it starts. A kept probe's block stays
  alive while any kept probe names it, so the kept probes of mostly forgotten
  blocks are re-homed when the cache compacts.

The cache reader's spin (`Thread.SpinWait`) does not wait for this step: it
breaks the host rule now and is replaced by stable publication or a host wait
as a change of its own.

**Closes.** The second derivation; three retention currencies; a fixed
lighting cost per world at a hub with many portals; a cold first sight; the
CPU scene as a reason to keep host words. **Leaves open.** What the lighting
must keep beyond recent hits (blockers a ray passes) is established here
before observation evicts anything it traces; until then every static draw in
the window's reach is support. **Strongest case against.** A lighting floor
for every demanded world is a cost a hub pays per window. It is sized by the
glass, it is the first thing rung 4 gives back, and the alternative is a
lighting snap at every first sight.

### Read at the scale Puck aims for

The purpose asks what each answer becomes when every zone, character and
server is a world.

- **A hub with many portals.** Every attached window is a residency. Today each
  takes a quarter of the device, a quarter of the GPU's background time, a
  hidden render while it arrives, and a lighting volume. After decisions 3, 4,
  5 and 9 a world behind a window is ready when there is room, gives up
  everything it holds for presentation when there is not, lands without a
  render and shares one budget
  ([where the floor stops being a bound](#decision-5-the-unit-is-a-tier-and-the-coarsest-tier-is-the-floor)).
  If the least the worlds in view cost cannot fit a device, that is a session
  and document question beyond this plan, and step 2's fixed-cost line says
  whether it arises.
- **A crowd of characters.** Spatial eviction does not thin a crowd standing in
  one place. Disclosure and presentation stay apart: a recipient keeps every
  fact its grants allow, and the floor is what a character costs when its
  detail does not fit **[external: Guild Wars 2, the end of culling]**.
- **Federation.** A cache is re-derivable only while its inputs are obtainable.
  Evicting GPU bytes keeps the store's entry; evicting the store keeps the
  compiled field; nothing here may discard a document a demanded group needs.
  A hash never admits content: identity equality may share public bytes, and
  the grants still decide who sees them. Requests across many routed worlds
  carry a generation, are deduplicated and are cancelled when their demand
  leaves, so a distant portal cannot starve the occupied seat.
- **Edges by topology.** A neighbour through a portal is near however far it
  lies in world coordinates **[external: EVE download on demand]**. Mapped
  demand already carries that; authored areas add the edges a window does not
  show, and a door's opening is a deadline, never a wait
  ([a door never waits](../decisions/residency.md#a-door-never-waits-for-loading)).

The debug world's painting wing and its garden at a crowd's count are today's
workloads for these readings. Neither is a hub of large worlds or a crowd of
distinct looks, and scale evidence needs both as places
([debug world content](open-items.md#debug-world-content)).

## The build order

A step's number is its name, which other pages cite, and it stays when the
order changes. The rows are in landing order. Each step lands on its own
check, taken in one window under [the recipe](#how-the-bars-are-read) on both
backends, at the hero spawn and the meadow, cold and warm, and the steps that
claim a seam closed are read again by [the seam walk](#the-seam-walk). A
windowed reading is a verification request to the lead; a lane's own finish
line is the build and a headless run. Evidence goes in the landing commit.

| Step | Delivers | Needs |
|---|---|---|
| [0. The walk's instrument](#step-0-the-walks-instrument) | The seam walk as a run, and today's seams as the baseline | Nothing |
| [1. Device readings and pools](#step-1-as-a-brief) | Budgets per segment, placement in the ledger, pools on the account as ledgers | Nothing |
| [2. Every gigabyte has an owner](#step-2-every-gigabyte-has-an-owner) | The physical rows, their owners and their residuals | Beside 1 |
| [7. The backing prototype](#step-7-the-backing) | Slabs or sparse backing, chosen by measurement | Beside 1 and 2, with a GPU grant |
| [3. One reservation](#step-3-one-reservation) | Admission before allocation; growth that stops at the limit | 1, 2 |
| [4. Staged by demand](#step-4-staged-by-demand) | Arrival without a render | Nothing; shares its budget once 3 lands |
| [7. The backing](#step-7-the-backing) | Memory given back without a replacement | The prototype, 3 |
| [5. The floor and eviction](#step-5-the-floor-and-eviction) | Floors, the order of what gives way, relocation within the device | 3; 7 before it claims relief |
| [6. CPU copies](#step-6-cpu-copies) | The library, the documents and the widened restore; the static rows after 9 | 2; 5 and 9 for the static rows |
| [8. Observed use](#step-8-observed-use) | Speculative detail apart from observed | 5 |
| [9. Lighting on the same demand](#step-9-lighting-on-the-same-demand) | One demand, a lighting floor, placement on the GPU trace | 3; 5 for the floor it traces |
| [10. Areas, tiles, then the moons](#step-10-areas-tiles-then-the-moons) | Authored areas, the velocity lead, source tiles | 5, 8 |
| [11. Managed churn](#step-11-managed-churn) | Arrival allocation removed at its sources | Nothing; its trace is taken now |

Steps 0, 1 and 2 change no behaviour and run beside any other lane. Step 3 is
the first that can change a picture, and step 5 the first that changes what a
miss looks like; each of those lands with captures. Scheduling is not rebuilt
here: the bake's pool and the lighting's workers move onto the one pool in the
[job system](engine-job-system.md#6-build-order)'s J1, and the selector hands
it comparers, never a second executor. Until J1 lands each step keeps the
executor it finds.

### Step 0: the walk's instrument

**Builds.** The three route segments of [the seam walk](#what-it-walks) at
their three speeds, and a window over a route stop's own timeline: `puck bench
frames` measures a plan's stop after settling it, and the tour holds its
captures under lockstep, so neither reads a crossing as a player takes it. The
bench gains a window that spans a route stop's timeline display-paced with
nothing settled first, reporting the slowest frame, the counters' change
across the window and a presented burst.

**Check.** The report carries every reading of
[what a window reads](#what-a-window-reads) that exists today, and names each
one that does not exist yet as absent, never as nothing. The baseline legs on
both backends are in the landing commit: where today's seams are.

### Step 1 as a brief

**Outcome.** `host.memory` prints, for each pool and each segment the machine
has, what the device says Puck may use and is using beside what Puck's own
ledger counts by placement, and `MemoryAccount` carries the pools as ledgers.
Nothing admits against them yet: `MemoryAccount.Owner.TryReserve` answers
exactly as it does today, both quarter shares stay, and no reading trims a
cache. Step 3 switches admission.

**What to build.**

1. A budget reading in `Puck.Abstractions`, beside `GpuMemoryProfile`: for each
   segment (a DXGI segment group or a Vulkan heap) the pool it lies in, its
   budget and usage, and where the API has them the bytes available for
   reservation and the current reservation; whether the figures are estimates;
   and a serial that moves when the device signals a change.
   `IGpuDeviceContext` returns the current one beside
   `IGpuDeviceContext.MemoryProfile`, without a driver call when nothing marked
   it stale. A device that is absent, lost or being recreated returns a
   reading that says so, never zeros.
2. Direct3D 12: hold the adapter as `IDXGIAdapter3` for the device's life;
   read the local group, and the non-local group only when the profile is not
   `GpuMemoryProfile.UnifiedMemory`; keep every field the query returns;
   register the budget-change event and test it on the frame thread without
   waiting; unregister it at teardown and register again on a recreated
   device.
3. Vulkan: enable `VK_EXT_memory_budget` when the device lists it and read it
   per heap beside `VulkanNativePhysicalDeviceApi.GetMemoryProfile`; a heap's
   pool follows the profile's own device-local rule, every heap in the one
   pool on unified memory; refresh at a low fixed rate on the frame thread;
   mark the reading an estimate when the extension is absent.
4. Placement in the ledger: `GpuDeviceMemoryWork.CountAllocated` takes, beside
   the role, the segment the allocation was placed in. On Vulkan that is the
   heap of the memory type the allocation used, the one
   `VulkanMemoryTypes.FindIndex` returned, which a preferred request may have
   taken as a fallback. On Direct3D 12 it is the segment group of the heap
   type's memory pool on that adapter, for a committed resource as for a
   placed one. The ledger keeps held, peak and released bytes
   per segment as well as per role, and counts the allocations whose role and
   placement disagree. `GpuDeviceMemoryWork.IsDeviceLocal` keeps choosing a
   role's row and is not used to choose a pool.
5. `MemoryAccount`: pools as ledgers (host and device, one pool on unified
   memory), each with a budget, a spend limit and owned and reserved bytes,
   and a door that takes a device reading and records the pools' and segments'
   budgets and limits. The door fires no trim and changes no admission.
   Existing owners stay where `TryReserve` counts them, against the budget it
   reads today; the device owners are charged and printed and stay outside
   the sum `TryReserve` subtracts, on a discrete card and on unified memory
   alike. How that is represented (owners that only observe, or a pool that
   does not admit) is the lane's, and step 3 deletes it.
6. `GpuDeviceMemoryWork` reports each segment's held bytes to an
   `IAllocationObserver` the host hands it, so the account carries a device
   owner per segment; the backends gain no reference to the account.
7. `HostMemoryCommandModule` prints the pools, the segments and the reading.
   On unified memory the pool's line prints the sum once, so its ledgers are
   not read as separate capacity.

**Invariant.** A byte the ledger counts is charged to exactly one pool, chosen
by where it was placed. `GpuDeviceMemoryWork.CountAllocated` is the one door
and its segment argument the one input to the pool; the observers are called
under its lock. Where it stops: an allocation a door makes without counting is
invisible to the ledger, and the reading that shows one is the API's usage
against the ledger per segment; swapchain images and driver memory never reach
the door.

**Independent sources.** Each API is read from at least two: for DXGI,
Microsoft's reference, D3D12MA's budget code and DXVK's implementation of the
same interface; for a heap type's memory pool on an adapter, Microsoft's
reference and D3D12MA; for Vulkan, the Khronos reference, VMA's budget code and
RADV's heap code. Say where they disagree.

**Finish line.** Build the projects touched and run a headless World:
`host.memory` prints the host pool and names the device pool absent. The
account alone shows the shadow holds: one sequence of `TryReserve` calls
answers the same with and without device bytes charged, on the discrete and
the unified layout. That check is run in the lane and not kept, since step 3
ends the shadow. The windowed reading on both backends is a verification
request: at the hero spawn and the meadow, the pool and segment lines, each
segment's API usage against the ledger with the gap named, the allocations
whose role and placement disagree, the reservation figures, the two backends'
budgets for the one card, and the change count before and after a second GPU
program starts. A unified-memory device's reading is the Deck lane's
([open questions](#open-questions-and-their-owners)).

**Not in this step.** No reservation, no admission change, no limit change, no
trim, no attribution by owner.

### Step 2: every gigabyte has an owner

**Builds.** The physical rows and residuals of
[what adds and what only names](#what-adds-and-what-only-names). `GpuRegion`'s
host copies and staging report through `IAllocationObserver`; device
allocations carry an owner family at the creation doors, with allocated and
released per family, so the device memory no residency line names (view
targets, cascades, screens, program tables, ring apertures) has a row; the
trace scene, the volume and the probe cache are owners; the managed heap and
the allocator are physical rows with residuals; `world.residency` gains a
fixed-cost line per residency. Like step 1 it changes no admission: its new
owners are charged and printed outside what `TryReserve` subtracts.

**Replaces.** The inference that device allocations beyond the held bytes are
growth by copy; `host.memory`'s "unified physical total is unavailable".

**Check.** For every physical row, its owners and its residual add to the row,
and each residual is named. Against a page walk of the process on Windows and
the DRM client statistics on Linux, the rows explain the process to within a
stated share. A suspended heap walk names the large object heap's tenants, and
each tenant past the stated size is listed with the step that bounds it. What
the process holds after a cold bake that the managed heap, the allocator and
the ledger do not explain is named with a native allocation trace, and so is
what the host-visible blocks hold above a warm boot's: ranges freed inside a
shared block that stays held, and staging that grew with landing. The spend
margin is set from the bytes a budget drop takes.

### Step 3: one reservation

**Builds.** The selector, one per presentation, holding the classes of
[decision 3](#decision-3-one-reservation-across-residencies-with-floors-and-classes)
and the one detail bias; region limits granted from it; the admission door and
its grant at every counted allocation door, view targets, pipelines and
background work included; a growth reserving old and new; a device refusal
lowering the segment's limit and returning the grant; the pipeline's peak as a
reservation; one background budget of GPU time and upload bytes, taken up on
completion; background jobs in bounded pieces that lease the generations they
read until their fence and readers retire and reserve everything they hold to
completion; the process's one forced job; the device bytes of the floors passed to
`SetVideoMemoryReservation`; a pressure reading firing
`MemoryAccount.TrimCaches`. Admission switches for every owner in this one
change, and step 1's shadow is deleted.

**Replaces.** `SdfWorldTables.MeshRegionDeviceLocalShare`,
`ShaderPipelineMemoryBudget.DeviceLocalShare`, `GpuMemoryProfile.DeviceLocalShare`;
a bias per bake; a budget per `SdfGpuWork`; a forced job per bake; the bake's
own free-memory pacing on unified memory; background work taking jobs only at
frame pumps; the throw from `SdfWorldTables.EnsureMeshRegionCapacity`.

**Check.** Exists: `region-limit`, `region-named`, `detail-bias`, `refusals`.
Adds: the selector's line (grant per residency, the shared bias, each class's
bytes), held, peak, retired and promised bytes per pool and segment, and a
pool argument to `host.memory budget`. With four residencies the grants sum
under the device's limit. Lowering the device budget in steps along a fixed
path: held plus promised never passes the limit apart from the one forced job,
which is counted; the one bias rises; `refusals` stays at nothing; nothing
throws. No more than one forced job is admitted at once across every bake.
`SHADERPIPE_BUDGET` still refuses. This step claims that growth stops at the
limit. It does not claim that held bytes fall when the limit drops below them:
that is step 7's and step 5's, and the reading says which bytes are held over
the limit meanwhile.

### Step 4: staged by demand

**Builds.** [Decision 4](#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render)'s
five parts: the owes-an-upload predicate, the hidden stage that pumps and
never drives, the stage of a residency first held this frame, sight published
to the demand, and the wait over admitted demand.

**Replaces.** `WorldViewGraphHost.ReserveArrivals` and its reach and kept sets,
the three `WorldPortalVisibility.ArrivalShare` cases, the arriving-world query
and the pane-mapping law that pins them.

**Check.** Exists: the region line's `owed`, the background work's `unbound`,
`late-stagings`, the render graph's record counts (`render.record`). In three
cases (every window onto a routed world out of sight; a chain of portals; a
world behind a view its rate holds back) the world's `owed` reaches nothing,
`unbound` and `late-stagings` stay flat, no view of it is scheduled and its
background work is never driven. A residency attached this frame lands its
first upload without a render. `world.wait residency` lists an unloaded world
apart, and in the painting wing it either returns after the bakes finish or
names the residency and the upload it waits on. Time until the
occupied area has landed is no worse, and the slowest frame at the stops
falls by the hidden renders.

### Step 7: the backing

**Builds.** First the prototype: a capability read on the three bar devices
(sparse binding, reserved resources, memory priority, pageable device-local
memory) and the indexed slab read in every kernel that reads mesh words. Then
the backing the measurement chooses, with [the pressure
transaction](#decision-8-pages-are-three-things-and-only-one-changes) and the
backend pool's release of its kept empty blocks under pressure.

**Replaces.** Growth by copy; shrinking by replacement; a region per
residency, if slabs win.

**Check.** The capability table. The cost of the views, glass, cascade,
lighting-trace and picking kernels with and without the index, on both
backends (`world.gpu-timing`), with the fragmentation, the largest payload,
the descriptor count and the kept blocks priced. Then: allocated bytes per
landed byte fall toward one; under a forced budget drop the ledger's held
bytes fall after the fences with no allocation made to free them; and, with
slabs, no mesh is placed in more than one store.

### Step 5: the floor and eviction

**Builds.** The coverage check that makes a floor admissible; each group in
reach planning its floor as a kept product, landed first; draws naming the
floor until detail stands and when detail is evicted or refused; eviction by
the selector's priority; [the order of what gives
way](#decision-5-the-unit-is-a-tier-and-the-coarsest-tier-is-the-floor), with
a residency's tables built on demand and given back; relocation by a copy
within the device.

**Replaces.** The hole on refusal, on first arrival and on a replacement with
no room; relocation landing every mesh from the host; coarsening as the only
relief.

**Check.** Adds: floor bytes by class (static, chunked, dynamic, skinned,
glass, lettering), logical and as blocks; the groups whose floor is finer than
their coarsest rung; floor shown, floor pending and floor failed; evicted
groups and bytes; a count for each rung; the reach. Readings: the floor's
bytes at the hero spawn, the painting wing and the garden at a crowd's count,
with the fixed cost of a world and of a character printed. Along the lowered
budget path with wanted demand held fixed, no draw waits out of the frame,
evictions rise while `retiers` stays flat, and after the fences the ledger's
held bytes fall. Captures from every side of each stop show no missing
surface, the gallery's plaques and glass among them, and a skinned body at the
ends of its motion holds together at its floor. A cold arrival's burst shows
the place whole before it is sharp, and counts every frame a group in view
had no floor.

### Step 6: CPU copies

**Builds.** [Decision 6](#decision-6-what-a-landed-mesh-keeps-on-the-cpu)'s
table, each row when its prerequisite has landed. The library, the allocator
slack and the document store wait on nothing but step 2. The widened restore
and the dynamic and glass rows follow. The static rows and the trace scene's
hierarchy follow steps 5 and 9.

**Replaces.** The library forgetting claimed meshes; the cached claim as a
reason to keep host words.

**Check.** Exists: account `live` and `cached`, `mesh-released-now`, the
store's restores. First, the bake's start line on a garden crossing says why
so many groups move there (baking, loading or shared), before the library
changes. The physical native row at a settled stop falls against
`region-named`, not only the account's own bytes. Across a turn, a relocation,
a second residency's landing and a device loss: restores and store misses stay
bounded, no payload is held twice, and no restore is waited for on the frame
thread.

### Step 8: observed use

**Builds.** [Decision 7](#decision-7-prediction-decides-arrival-and-observation-decides-retention):
the mark at command emission with its consumer class, its key, its read
through the completed slots, the split of wanted detail into observed and
speculative, and the hold on re-admission.

**Replaces.** Visible detail coarsening while hidden detail is kept.

**Check.** Adds: batches read, stale and unknown, and their lag in frames;
observed and speculative bytes; bytes kept for each consumer class; products
requested again after an eviction; bytes given back, as the ledger sees them;
the feedback's own cost. At the meadow under one reservation, `detail-bias`
against the reading before, with speculative bytes and visible error beside
it: a lower bias is expected and is not promised by the order of eviction
alone. Across a half turn, `owed-error` and `owed-frames` with a capture
burst, and no product leaving and returning in a loop.

### Step 9: lighting on the same demand

**Builds.** [Decision 9](#decision-9-lighting-reads-the-same-demand-and-pays-on-demand):
the lighting on the selector's demand, the trace tier, the lighting floor
built on demand and ahead of first sight, levels allocated as demanded,
placement and depth classification on the GPU trace, byte-bounded caches and
a reserved scene build.

**Replaces.** The second demand derivation; retention in cells and triangles;
a volume per world nothing demands; the CPU hierarchy as placement's input.

**Check.** Exists: `probe-cache-bytes`, `trace-scene-cpu-array-bytes`,
`probe-cache-blocks`. Adds: the trace tier installed and pending, scene
builds with their bytes and peak, lighting owed in frames a view shows, and
probes invalidated. First reading: the floor against the view tier as the
traced tier, at the gallery's lit exhibits and a thin partition. GPU
placement agrees with the CPU placement it replaces, probe for probe within a
stated tolerance, before the CPU path goes. Probe and scene bytes stay bounded
along a valley flight, and `ProbeCache` is read again after it. A retier wave
rebuilds no trace scene, and a settled wait covers the lighting floor's debt,
where `world.wait bakes` returns today before the trace scene has rebuilt over
swapped meshes. A cold reveal burst at the lift shaft and through a painting
shows no frame lit by the stand-in on the normal-play leg. The gallery's lit
exhibits read as their plaques say.

### Step 10: areas, tiles, then the moons

**Builds.** Authored areas on the gallery's floors (the owner's suggestion),
with the stairwell, the lift shaft and the window bands as openings; a demand
point led along its velocity by the landing latency, and a demand that leaves
a view's reach cancelled; [source tiles under the seam
rule](#the-source-tile-and-its-seam-rule) for terrain; the moons through
routed residency. An authored area needs a spatial role of its own: the
nearest vocabularies are a placement's volumes, whose influence channel the
engine never interprets by contract (`WorldPlacementSpatialRole`), and gravity
areas, and neither has openings.

**Replaces.** A whole large instance as one unit.

**Check.** Areas: time until the occupied floor has landed, with and without
them; under a lowered budget unoccupied floors leave first. The lead: the
latency of each stage (read, decode, bake, upload) with its tail, speculative
work cancelled, and the queue's peak. Tiles: `puck mesh audit` over the tile
groups reads no open and no non-manifold edge at any tier, and neighbours
overlap by the margin; from the rim, at the valley's draw distance and along a
flight, with neighbours at different tiers and a floor forced beside the
finest by a lowered budget, the captures show no crack and no line along a
tile border. The seam walk's fastest legs read nothing.

### Step 11: managed churn

**Builds.** An allocation trace across an arrival, then pooling or streaming
its sources, with the [job system packet](open-items.md#engine-job-system)'s
list. The sources that list already names are taken as they are reached and
do not wait for this step's place in the order.

**Check.** Collections, the duration of each pause and the allocated bytes in
the arrival window, set against the presented frames of the same window: a
count of collections alone cannot say a hitch went.

## The owner's questions

Neither blocks a step; each has a default the build follows until the owner
says otherwise.

1. **How pressure should feel on a turn.** When memory is short, step 8 lets
   detail nobody is looking at leave first, so the view ahead stays sharp and
   a fast turn shows surfaces sharpening for a moment. The alternative keeps
   detail all round and softens everything evenly, as today. The default is
   the first, with detail near the eye kept all round. This is feel, and it
   only shows under pressure.
2. **What a character shows at its floor.** When a crowd's detail does not fit,
   each character draws its coarsest tier. Whether that should instead be an
   authored stand-in (a simpler body, a nameplate) is a look for the standard
   package, wanted once crowds exist.

Already ruled, and followed here: a miss shows the coarsest level and then
haze, never a hole; level switches cut and a late group fades in; the
resident-byte target is set on the RTX 4070 and the Steam Deck is a second
qualification on the same mechanism; a seam is what breaks immersion in normal
play. One consequence of the haze ruling is worth the owner's eye when step 5
lands: on a device where even the floors do not fit, the haze stands nearer
than the authored far distance.

## Open questions and their owners

| Question | Owner | Decides |
|---|---|---|
| The Deck's numbers: RADV's heaps and budgets, how far the GPU reaches into system memory under SteamOS, free memory with the Steam client running, and whether GPU pages count against the host's free memory | A lane with the hardware, after step 1 | Which limit binds in decision 2's formula; the 10 GB target becomes a qualification on a real reading |
| Whether Vulkan's budget on an RTX card tracks the DXGI budget | Step 1's windowed reading | Whether one spend margin fits both backends |
| The spend margin | Step 2 | The constant in decision 2 |
| What happens over budget on an RTX card under Linux | A Linux boot of an RTX machine | How strictly Linux discrete must reserve before allocating |
| The fixed cost of one residency, and of one character | Step 2, then step 5 | Whether a hub of many portals needs a session-scale design beyond this plan |
| Sparse binding, reserved resources, memory priority and pageable device-local memory on the three bar devices; slab size and the store's decode blocks | Step 7 | Slabs or sparse backing; whether priority classes are passed to the driver |
| The floor's bytes by class, and the groups whose floor is finer than their coarsest rung | Step 5's first reading | The floor class's size; whether the floor is affordable on each device |
| Which tier the lighting traces, and what it must keep beyond recent hits | Step 9's first reading | Whether the floor and the trace tier are one product; what observation may evict |
| How long detail may go unobserved before it is speculative, and the granularity of the mark | Step 8, with a turn's captures | The grace interval, the first owner question, and whether a mark per draw pays |
| How long a place stays coarse after pressure ends (`SdfMeshTier.BiasQuietFrames`) | Step 3, on the lowered budget path and back | The quiet interval, once one bias serves every residency |
| Program build and replan time against more than one placement count, and time to the occupied area after an edit that renumbers materials | A `world.residency` reading with the display on, beside step 0 | Whether document and program assembly must become spatially incremental |
| The latency of each stage from demand to first drawn | Step 0's baseline, then step 10 | The lead's length and the queue's bounds |
| Whether the tile overlap shows | Step 10's rim check | The overlap, or a border at one fixed spacing |
| Whether textures need this scheme | Not in this campaign; reopened if step 2 names a texture owner among the large ones | A texture cache on the same account and feedback |

## Systems considered

Rule 7 binds a plan as it binds code. What this page found already built, and
extends:

- **The account and its doors.** `MemoryAccount`, its categories, owners,
  `TryReserve` and the trim event are the authority; no second budget type. An
  owner's promise is already the handoff's arithmetic.
- **The ledger.** `GpuDeviceMemoryWork` is the device's measured truth and
  becomes the account's feed and the grant's last door, not a parallel count.
- **The backend pool.** `GpuMemoryPool` is where blocks are made, kept and
  freed; pressure release extends it.
- **Demand.** `SdfMeshDemand` already unites every view of a residency, carries
  a window's shown height and frustum, and takes a placed eye
  (`SdfMeshDemand.Place`). The selector unites residencies; it does not write
  another demand type.
- **Tiers and their budget.** `SdfMeshTier`, the retier's spare list and bias,
  and the hold that absorbs late answers are the eviction policy's body.
- **The restore door.** `SdfMesh.Hold` and its need counting are how a released
  plane comes back; decision 6 widens what passes through it.
- **Bounded landing.** The landing jobs' pieces and whole publication are the
  transport page.
- **Groups and chunks.** A bake group is the source tile's unit as it stands;
  `SdfMeshChunks` stays the within-tier partition, and `SdfMeshPlanarMerge`
  already collapses a flat closing face.
- **The mesh audit.** Its `open`, `nonmanifold` and `uncovered` metrics are the
  floor's and the tile's checks.
- **The pool.** `JobPool` and the job system's J1 own scheduling.
- **Visibility.** `SdfMeshOcclusion` is the feedback's producer,
  `RenderGraphSlotResults` its completion, and `SdfPortalSight` a window's
  sight.
- **The lighting's stand-in.** The camera sample that lights a surface no
  published probe answers is the lighting's counted miss.
- **The walkthrough.** Route segments, the tour's presented bursts and
  `puck bench frames` are the seam walk's instrument; no new harness.

## Sources

Only what a decision rests on. Puck is independent work: each is a second
opinion, none is the specification.

- D3D12 memory pool: [D3D12_MEMORY_POOL](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_memory_pool),
  which states that on a unified adapter the system pool and the local segment
  group are the same memory, and which pool a heap's memory comes from.
- Microsoft residency: [Residency](https://learn.microsoft.com/en-us/windows/win32/direct3d12/residency),
  for the budget's behaviour, the reservation, one pool against two, watching
  for negative changes about once a frame, and destroying heaps as the reliable
  release; with [QueryVideoMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo),
  [its fields](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/ns-dxgi1_4-dxgi_query_video_memory_info)
  and [the change notification](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-registervideomemorybudgetchangenotificationevent).
- D3D12MA: [the allocator's budget](https://gpuopen-librariesandsdks.github.io/D3D12MemoryAllocator/html/class_d3_d12_m_a_1_1_allocator.html),
  for the non-local budget reading zero on an integrated adapter and for
  correcting usage between fetches.
- DXVK: [its DXGI adapter](https://raw.githubusercontent.com/doitsujin/dxvk/master/src/dxgi/dxgi_adapter.cpp),
  for the non-local group answered with the local budget on a unified adapter.
- Vulkan memory budget: [VkPhysicalDeviceMemoryBudgetPropertiesEXT](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceMemoryBudgetPropertiesEXT.html),
  for a budget and usage per heap.
- VMA: [staying within budget](https://gpuopen-librariesandsdks.github.io/VulkanMemoryAllocator/html/staying_within_budget.html).
- RADV: Mesa's RADV physical-device source, read through
  [a mirror](https://raw.githubusercontent.com/chaotic-cx/mesa-mirror/main/src/amd/vulkan/radv_physical_device.c),
  for the two heaps an APU reports and how their budgets are computed.
- RDNA guide: [AMD's RDNA performance guide](https://gpuopen.com/learn/rdna-performance-guide/),
  for 80% of memory and the advice against querying every frame.
- NVIDIA Vulkan guidance: [Vulkan dos and don'ts](https://developer.nvidia.com/blog/vulkan-dos-donts/),
  for Linux having no memory manager that absorbs an overcommit.
- Nanite deep dive: [A deep dive into Nanite virtualized geometry](https://advances.realtimerendering.com/s2021/Karis_Nanite_SIGGRAPH_Advances_2021_final.pdf),
  for requests by projected error, eviction by priority and the always-resident
  root.
- Software virtual textures: [van Waveren](https://www.mrelusive.com/publications/papers/Software-Virtual-Textures.pdf),
  for wanted against resident, the pinned coarse level and a bias that stops
  oversubscription.
- EVE download on demand: [CCP's announcement](https://www.eveonline.com/news/view/download-on-demand-client-for-eve-online),
  for neighbours fetched across stargates and coarse resources first.
- Guild Wars 2, the end of culling: [ArenaNet](https://www.guildwars2.com/en/news/world-vs-worldthe-end-of-culling/),
  for keeping disclosure apart from how much of a crowd is drawn.
