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
  group in reach. The owner confirmed this scope: the minute covers what the
  boot's views demand, and a tier a later view asks for is a cache miss,
  derived and counted. The minute stays absolute.
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

**Normal play conditions**, as the owner confirmed the reading
([residency](../decisions/residency.md#the-owners-rulings-on-look-and-policy)):
a fresh process on a bar device, on each backend the device
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
| A measure | Built for memory. `world.residency` reads time until near groups land, resident triangles by tier, the region's held, named and limit bytes, the selector's line, the probe cache's bytes and the trace scene's CPU arrays; `host.memory` reads each pool's limit, held, promised and retired bytes and each segment's budget (`IGpuDeviceContext.MemoryReading`). |
| Near-first ordering | Built: `SdfMeshDemand.Priority`, and detail bounded by what each view resolves (`SdfMeshTier`). A demand point is a view's eye with no lead along its velocity. |
| A selector across residencies | Built for bytes and background GPU time (`SdfResidencySelector`): one detail bias, a budget for each residency's bake, a reservation for each mesh region and one budget of background GPU work. Each residency still owns its `SdfWorldResidency.MeshDemand` and its mesh region, and the selector does not yet order work across residencies. |
| Lighting on the same demand points | Partly built. Each level of the probe window already centres on the box of every view's camera when it fits (`SdfMeshIrradiance.WindowLow`), and probe urgency reads every view. What remains: the window is anchored on the first view (`SdfMeshIrradiance.WindowCamera`), the lighting derives its own view list (`SdfMeshIrradiance.CaptureViewCameras`), the trace scene is built from every static draw of the frame (`SdfMeshIrradiance.StaticKey`), and `SdfMeshIrradiance.ProbeCache` is bounded by window cells, not bytes. |
| Demand mapped through portal faces | The mapping is built; its use is not. A fitted window's view is the viewer's eye taken through the glass, measured in the display pixels the glass spans and held to its frustum (`SdfViewSnapshot.DemandHeight`, `SdfViewSnapshot.DemandWithinView`), and every attached window's view is a demand point whether or not it renders (`SdfMeshDemand.Observe` leaves out only a view that is elsewhere, has no region or no valid camera). What is missing is arrival without a render: a residency's upload is submitted only for a scheduled render (`SdfWorldPasses.PrepareRender`), so an unseen window still renders at the least share (`WorldViewGraphHost.ReserveArrivals`, `WorldPortalVisibility.ArrivalShare`). |
| Eviction under a byte budget | Not built. Under pressure a group is planned again at a coarser tier or every demand is coarsened half a rung (`SdfMeshTier.Biased`); no group ever leaves. A mesh the arena cannot place is refused and its draws leave the frame (`SdfMeshRegionArena.Refused`), which shows as a hole. A growth the account or the device refuses leaves the region as it is, and the placements it could not hold are refused the same way (`SdfMeshRegionArena.Clip`). |
| Authored areas | Not built. |
| Terrain tiles, then the moons | Not built: a bake group is an instance, a slot or a union, so one large instance has one tier and one demand box. |

What the memory side holds today:

- **The account admits every pool.** `MemoryAccount` holds what each pool holds
  and has promised under its limit, at three doors: a host owner's
  `MemoryAccount.Owner.TryReserve`, a device reservation's
  `MemoryAccount.DeviceReservation.TryReserve`, and the ledger's grant before
  each native device allocation (`GpuDeviceMemoryWork.TryAdmit`). Its host
  owners are mesh payloads, bake workspaces, the store's transient scratch,
  the regions' host copies, the trace scene and the probe cache; a payload's
  charge never refuses (`MemoryAccount.Owner.Change`). The managed heap beyond
  those owners, the allocator's slack and the runtime are measured rows, not
  admitted ones.
- **Progress is forced once in the process.** A bake with nothing admitted
  admits its next group whatever the pool holds only while no other owner
  holds the allowance (`MemoryAccount.Owner.TryReserveForced`, reported by
  `SdfMeshBakeReport.AdmittedAlone`).
- **Device bytes are admitted before they exist.** `GpuDeviceMemoryWork`
  counts an allocation only under a grant, at the driver's size, in the
  segment the device placed it in. Resources share blocks of
  `GpuMemoryPool.BlockBytes`, a resource past half a block takes its own, and
  a key keeps one emptied block.
- **Background GPU work has one budget, and is serviced when a job
  completes.** Every residency's `SdfGpuWork` draws on its presentation's
  `SdfGpuWorkBudget`: `SdfGpuWork.GpuShare` of GPU time and
  `SdfGpuWork.UploadBytesPerSecond` for all of them together, banked by wall
  time. Its jobs run on the presenting queue, recorded on the frame thread,
  and are stepped at the frame's pumps and inside the host's wait between
  frames (`HostWaits.Pace`), which a job's fence, a client's offer and the
  budget's next credit end early, so a slow present no longer paces the bake
  or the lighting ([step 3](#step-3-one-reservation)). The bake's admission
  measures its backlog in wall time (`SdfMeshBakeBacklog`). The swap chain's
  own wait and a frame that never waits are still stepped at pumps alone.
- **The region gives memory back only by replacing itself.** It grows by
  allocating a larger buffer and copying (`GpuRegion.Grow`), and shrinks the
  same way (`GpuRegion.Shrink` from `SdfWorldTables.FitMeshRegion`), so both
  need the new buffer while the old one stands, and both reserve the two
  together.
- **Pressure fires a trim.** A limit that falls under what its pool holds, a
  device refusal or storage admitted past a limit fires
  `MemoryAccount.TrimCaches` on the frame thread, at most once a second
  (`MemoryAccount.ServicePressure`).

The duplicates this campaign removes, each one mechanism where the code has
several:

| One job | Today |
|---|---|
| A share of device memory | One: a reservation in the account (`MemoryAccount.ReserveDevice`), which the mesh regions and the shader pipelines hold |
| Demand points | `SdfMeshDemand.Observe` and the lighting's own `SdfMeshIrradiance.CaptureViewCameras`, with different filters |
| A byte ledger | `MemoryAccount` owners fed by `GpuDeviceMemoryWork`; the mesh library's own weight (`SdfMeshLibrary.ByteBudget`) and the region's named bytes copied into the bake (`SdfMeshPathBake.ObserveResidency`) remain |
| Retention | The library in bytes, the arena's slack in words (`SdfMeshRegionArena.RetainedSlackWords`), the trace scene in triangles (`SdfMeshIrradianceScene.RetainedTriangles`), the probe cache in window cells; the store's disk bound (`SdfMeshStore.DefaultCapacityBytes`) is a fifth, and stays, because disk is its own resource |
| Scheduling residency work | The bake's own static pool, the lighting's and the restore's thread-pool work and `JobPool`; the GPU budget is one (`SdfGpuWorkBudget`) |

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

What enforces the first, at one door each:

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
  (`MemoryAccount.Owner.TryReserveForced`,
  [decision 3](#decision-3-one-reservation-across-residencies-with-floors-and-classes)).

Where it stops: an allocation a door makes without counting is invisible to
the account, and the reading that shows one is the API's usage against the
ledger; swapchain images and driver memory never reach a door; the envelope is
measured, and until it is sampled at a rate (it is sampled when `host.memory`
asks) the host pool's limit is its budget and the machine's free memory, not
the budget less the envelope. Admission stops growth and gives nothing back: a
limit that falls under what a pool holds leaves it standing and read as over
the limit until [step 7](#step-7-the-backing) and
[step 5](#step-5-the-floor-and-eviction).

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
  The adapter is held for the device's life (`DirectXVideoMemory`).
- **Vulkan.** `VK_EXT_memory_budget` gives a budget and usage per heap, enabled
  when the device lists it (`VulkanLogicalDeviceFactory`). Each heap is a segment with its own figures;
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

**Favoured.** One refresh rule for both backends
(`GpuMemoryReadingRefresh`): the frame thread refreshes at the top of a frame
when the API signals a change and otherwise at a low fixed rate (once a
second), and on Vulkan at once after any refused allocation or reservation. On
Direct3D 12 the budget-change event
(`RegisterVideoMemoryBudgetChangeNotificationEvent`) is that signal; Windows
sets it once at registration **[external: DXVK]**. The event signals budget
changes only, never usage, so a reading refreshed on the event alone keeps its
first usage for the process's life **[reading]**: the fixed rate is what keeps
usage current on both backends. A reading's serial moves only when a budget,
the segments or the estimate change, never for usage, because what derives
from it (the spend limits) derives from budgets. Between refreshes, usage is
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

**Forced progress.** A job admitted whatever the budget holds is what makes
work always settle. It is one allowance for the process
(`MemoryAccount.Owner.TryReserveForced`): one forced job at a time across every
bake, drawn from the workspace class, in place of one per bake. A job whose peak passes the whole class is
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
  changes in its budget or whole-mesh unit. Every slice additionally retains
  its destination-issued writable-page lease through completion.
- **Publication stays whole.** What a frame draws is always a complete mesh of
  one tier, which holds by construction
  ([what publishes](bake-demand-tiers.md#what-publishes-and-what-the-frame-draws)).
  Shader-visible pages below the mesh would put a page-table read in every vertex
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
3. Backing is released. Sparse pages lose their mappings after their last
   readers, then return to the backend pool. Only an emptied physical block
   returned to the device reduces held bytes. With the contiguous fallback,
   release without a replacement requires releasing the region whole and
   landing its floors again, which is counted and is the last resort.
4. After the fence, the row falls. Relief is claimed here and nowhere earlier:
   a sum of grants and named bytes cannot show it.

Compaction, where a store is fragmented, takes its room from the workspace
class reserved in advance, never from the pool it is trying to relieve. The
backend pool's kept empty block (`GpuMemoryPool`) is released by the same
pressure, through that pool, not through a second trimming allocator.
Rows remain published during physical compaction: queue order excludes reads
between copy-out and copy-back. Page generations guard writes and publication;
a device-wide timeline guards reuse and release, even for a hidden residency.
The region reserves page coverage; the pool owns the block's remaining slack
once. Virtual holes alone never justify a sparse arena relocation.

| Option | Gives | Costs |
|---|---|---|
| Sparse backing of the one buffer | No shader change; memory committed as the region fills and decommitted as it empties | Reserved resources and sparse binding are capabilities, not guarantees **[external: Microsoft residency]**; the addressing ceiling stays |
| Slabs: fixed-size buffers, each mesh inside one, a draw naming its slab | No capability question; growth adds a slab and an emptied slab returns to the device; no single-buffer addressing ceiling; one store for every residency, so a mesh several worlds draw is resident once | Every kernel that reads mesh words indexes an array of buffers; a mesh larger than a slab takes its own; slab count is bounded by descriptors; fragmentation inside slabs |

**Direction after the prototype.** Slabs were favoured for sharing geometry
across residencies and returning complete buffers. [The first
reading](#the-prototype-as-built) found material indexed-read cost on Direct3D
12, so sparse backing of the one region buffer is now the direction wherever
the device allows it. It preserves the direct shader read, the region per
residency and its addressing ceiling; it does not deliver decision 3's shared
mesh store. A device without sparse support uses today's admitted growth by
copy behind the same region, never a second store. The remaining paired
readings and hardware qualifications have owners in [step 7's
design](#sparse-backing-design-for-review).

Because physical relief depends on it, the backing lands before
[step 5](#the-build-order) claims any. Until then growth reserves its
replacement peak (decision 3), so the copy is admitted, not hidden, and step
3 claims only that growth stops. The sparse design below is proposed, not
implemented or hardware-qualified.

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
a full scene build holds its pieces beside the old scene. Traced at full
detail, the scene is among the largest owners on both sides
(`trace-scene-written-bytes`, `trace-scene-cpu-array-bytes`) **[reading]**,
and its upload is part of how long a boot waits for the device meter. Before a level first
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
  alive while any kept probe names it, so a tour leaves several times a
  probe's nominal bytes for each kept probe; the kept probes of mostly
  forgotten blocks are re-homed when the cache compacts (its `Retain`).

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
| [7. Backing prototype and design](#step-7-the-backing) | Sparse design after the prototype's first reading; contiguous fallback retained | Step 3's admission stands; qualification readings need a GPU grant |
| [3. One reservation](#step-3-one-reservation) | Admission before allocation; growth that stops at the limit; background work serviced on completion. Built; its windowed readings are owed | 1, 2 |
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

The route segments are the `seam walk:` tests of
[`walkthrough-tests.puck`](../../worlds/debug/walkthrough-tests.puck) and their
stops are [`seam-walk.sweep.json`](../../worlds/debug/seam-walk.sweep.json):
`puck bench frames --plan worlds/debug/seam-walk.sweep.json --stop <stop> --window`
([the verb](../reference/cli.md#puck-bench-frames)). The three speeds are the
walk (5 m/s), the run (10 m/s, Left Shift held) and the run with the dash on each
cooldown (0.48 m a tick); the lift crossing is entered at each, and its car is
called through the `liftCall` mailbox, the `gallery.lift` command's own write,
because a test's schedule admits no authored command. Free flight is not walked
yet: the fastest leg is the dash. A crossing between authorities drops the seat's
held controls, so a leg re-presses the stick on a fixed cadence, as a player
does.

**Check.** The report carries every reading of
[what a window reads](#what-a-window-reads) that exists today, and names each
one that does not exist yet as absent, never as nothing. Today's absences and
the step that supplies each: floor shown, pending and failed (5); a fallback
shown (3 and 5); reach drawn in (10); lighting owed (9); evictions and
re-requests (5 and 8); the latency of each stage from demand to first drawn (10);
each physical row's owners and residual (2); and the transfer's reserve and commit ticks, the seat's commands per tick and the voices
the mixer started and released, which wait on the
[transfers packet](open-items.md#transfers-federation-and-server). The window
carries the body's pose and `audio.state`'s device counts for the cross-world
row, and the recording of the mixed sound stays `capture.start`'s, not yet driven
by the window. The baseline legs on both backends are in the landing commit:
where today's seams are.

### Step 1 as a brief

**Built.** `host.memory` prints the host pool, the device pool (named absent on
unified memory or without a device), the device reading the account last
recorded, each segment's API budget, usage and reservation figures beside the
bytes the ledger placed in it (`usage-beyond-ledger`), and each ledger segment
row. Step 1 landed these as ledgers; [step 3](#step-3-one-reservation) admits
against them.

- **The reading.** `GpuMemoryReading` holds, per segment, a
  `GpuSegmentReading`: budget, usage, and where the API has them the bytes
  available for reservation and the current reservation. It also holds whether
  the figures are estimates, a serial, and a state (absent, lost, read), so a
  device that is absent, lost or being recreated never reads as zeros.
  `IGpuDeviceContext.MemoryReading` returns it without a driver call;
  `IGpuDeviceContext.RefreshMemoryReading` refreshes it, and each host calls
  that at the top of a frame (`DeviceMemoryReadings.Refresh`, which also
  records it in the account).
- **Direct3D 12.** `DirectXVideoMemory` holds `IDXGIAdapter3` for the device's
  life (`IDirectXDeviceApi.OpenAdapter`), reads the local group and, on a
  discrete adapter only, the non-local group, keeps every field, and queries
  when the budget-change event, tested without waiting, is set and otherwise
  once a second (`GpuMemoryReadingRefresh`), since the event signals budget
  changes and never usage. It is created with the device and released with it,
  so a recreated device registers again.
- **Vulkan.** `VulkanLogicalDeviceFactory` enables `VK_EXT_memory_budget` when
  the device lists it. `VulkanRenderer.RefreshMemoryReading` reads it once a
  second through `IVulkanPhysicalDeviceApi.ReadMemoryBudget` under the same
  rule. On both backends the serial moves when a budget changes, never for
  usage. Without the extension the heap sizes stand in, marked estimated.
- **Segments and pools.** `GpuMemorySegments` is the one place a segment's
  pool is decided: a heap's pool follows `GpuMemoryProfile`'s device-local
  rule, and every segment lies in host memory on unified memory. A backend
  registers its device's segments with the ledger as it creates the device
  (`GpuDeviceMemoryWork.BeginDevice`).
- **Placement in the ledger.** `GpuDeviceMemoryWork.CountAllocated` takes the
  segment the allocation was placed in. Direct3D 12 computes it with
  `DirectXDeviceMemory.SegmentOf` from the heap's properties
  (`GetCustomHeapProperties` on that adapter, for a committed resource as for
  a placed one's heap). Vulkan reads it from the heap of the memory type the
  allocation used (`GpuMemorySegments.OfMemoryType`). Per segment the ledger
  keeps held, peak, allocated and released bytes (`GpuSegmentLedger`) and counts
  placement disagreements (`gpu.memory.placement-disagreements`).
  `GpuDeviceMemoryWork.IsDeviceLocal` chooses only the role row.
- **The account.** `MemoryAccount.ReadPools` reads the pools
  (`MemoryPoolReading`: budget, spend limit, admitted and reserved bytes, and
  from step 2 the physical rows). A host owner charges host memory. Each ledger
  segment row reports to the account (`MemoryAccount.DeviceOwners`), which the
  presenter registrations hand the ledger, so the backends hold no reference to
  the account, and a family's row names part of its segment's row and adds
  nothing. `MemoryAccount.Record` takes a reading and records each segment's
  budget and spend limit (the budget less the larger of a tenth of it and the
  headroom).

**Invariant.** A byte the ledger counts is charged to exactly one pool, chosen
by where it was placed. `GpuDeviceMemoryWork.CountAllocated` is the one door,
and its segment argument, resolved through the segments the device registered,
is the one input to the pool; a count on a device that registered none is
refused, and the observers are called under the ledger's lock. Where it stops:
an allocation a door makes without counting is invisible to the ledger, and the
reading that shows one is the API's usage against the ledger per segment;
swapchain images and driver memory never reach the door. Direct3D 12 names no
pool for a shader-visible descriptor heap, so those are counted in the local
group, and the CPU-only descriptor heaps are not counted.

**Where the sources disagree.** On a unified adapter Microsoft says the system
pool and the local group are one memory, DXVK answers the non-local query with
the local heaps, and D3D12MA queries both groups and treats the non-local
capacity as zero; Puck reads the local group alone. Vulkan's specification
bounds a heap's budget by its size and requires it non-zero, while VMA clamps a
budget past the heap size and replaces a zero one with 80% of the heap, as
driver bugs; Puck prints what the driver reports. RADV splits an APU's free
memory two thirds to its device-local heap and one third to the other, each
heap's budget its free share plus its own usage.

**What remains.** The windowed reading on both backends is a verification
request to the lead: at the hero spawn and the meadow, the pool and segment
lines, each segment's API usage against the ledger with the gap named, the
allocations whose role and placement disagree, the reservation figures, the
two backends' budgets for the one card, and the change count before and after
a second GPU program starts. A unified-memory device's reading is the Deck
lane's ([open questions](#open-questions-and-their-owners)).

### Step 2: every gigabyte has an owner

**Built.** `host.memory` measures the account and prints each pool as its
physical rows, which alone add, each with the part its owners name and its
residual.

- **A charge names its row.** `IAllocationObserver.Change` takes the
  `MemoryBacking` the storage lies in, stated by the storage that makes the
  charge: an `OwnedBuffer` is `NativeAllocator` with an allocator and `Managed`
  without one, a `GpuRegion`'s host copy is `NativeDirect`, and a ledger row is
  `DeviceBlocks`. `MemoryAccount.Owner` keeps its bytes per row and refuses a
  row its kind cannot hold (device blocks on an admitted owner, a host row on a
  device pool's owner or a segment's).
- **The rows** (`MemoryAccount.ReadPools`, `MemoryRowReading`): device blocks
  per pool, charged by the ledgers' segment owners
  (`MemoryAccount.DeviceOwners`); native allocator, the allocator's committed
  bytes; native direct, the sum of its owners' charges; managed, the
  collector's committed bytes; and runtime and driver, the page walk's private
  bytes less the other host rows (`HostMemory.WalkProcess`: `VirtualQuery` over
  the address space on Windows, `/proc/self/smaps_rollup` on Linux). The
  measured rows are a sample `MemoryAccount.Measure` records with its age
  (`HostMemorySample.Take`); the account never calls the allocator, the
  collector or the driver. A residual skew makes negative reads as nothing and
  is counted (`negative-residuals`).
- **Device families.** Each counted device byte is named by at most one owner
  family, `GpuObjectName.MemoryFamily` (the owner without its `$` instance
  suffixes, then the part), at the creation door that knows the name: a
  dedicated allocation by the family `GpuDeviceMemoryWork.CountAllocated`
  takes, a shared block by each placement's `GpuDeviceMemoryWork.CountPlaced`
  from the arena that placed it (`DirectXHeapArena`, `VulkanMemoryArena`).
  Each family row keeps held, peak, allocated and released bytes per segment
  (`GpuFamilyLedger`) and reports to an observed owner, so a family that churns
  reads apart from one that holds. The segment row's residual
  (`GpuSegmentLedger.Residual`) is the room inside shared blocks and the
  emptied blocks kept for reuse. This replaces summing the render-graph nodes'
  `owned-bytes` gauges: the families come from the ledger, at placement, for
  every creator alike, and add to the segment rows by construction.
- **Host owners.** The tables' region host copies
  (`sdf.world.region-host-copies`, native direct); the trace scene's CPU
  hierarchy and the probe caches, as sampled owners in the managed row
  (`MemoryAccount.CreateSampledOwner`, read at each measure). The irradiance
  volume's device memory is named by its families.
- **Fixed cost.** `world.residency` prints each residency's fixed cost
  (`SdfWorldTables.DescribeFixedCost`): its tables' bytes and region host
  copies less the mesh region, beside the mesh region's own.
- **Heap walk.** `host.memory heap [min-mib]` asks the runtime for its own heap
  dump over an EventPipe session the process opens on itself
  (`ManagedHeapWalk`): one blocking full collection with every managed thread
  suspended, during which the runtime writes every live object and root. It
  lists the large objects' totals and each tenant of at least the stated size
  (4 MiB by default) by type and first holder.

**Invariant.** Per pool, the rows are disjoint and each byte a ledger or owner
counts lies in exactly one; an owner names a part of exactly one row per
charge, chosen by the storage that made it, and the parameter
`IAllocationObserver.Change` takes makes every charge state it. Per segment, the
families never name more than the row holds: `CountPlaced` refuses a placement
in a block a family names whole, in a block never counted, or past a block's
size, and a freed block takes its placements' names with it. Where it stops: a
sampled owner names what its subsystem derives at the instant of the measure
(the probe cache's figure excludes object headers); the managed and allocator
rows are samples at nearby instants, not one; whether a driver maps device
blocks into the process's private bytes is the driver's, so on a discrete card
the runtime-and-driver row may hold part of the host pool's device blocks, and
the walk cannot tell.

**Replaces.** The inference that device allocations beyond the held bytes are
growth by copy (the families' allocated and released bytes read it); the
trailing "unified physical total is unavailable" (each pool's `physical`
total, with `complete` saying whether every row holds a figure).

**What remains.** The windowed reading is a verification request to the lead:
on Direct3D 12 and Vulkan at the hero spawn and the meadow, settled, the rows
of each pool with their residuals, the walk's private bytes against
`process-private` and the share the named owners explain, the families against
the segment rows, and whether the driver's mapped device blocks show in the
walk's private bytes (the host pool's device blocks against the
runtime-and-driver row before and after a large upload). A cold bake's
residual in the native rows is named with a native allocation trace, and so
is what the host-visible blocks hold above a warm boot's. The spend margin is
set from the bytes a budget drop takes, on that reading. The DRM client
statistics half (`/proc/self/fdinfo` `drm-memory-*`) is owed to a Linux
machine, and a unified-memory device's reading to the Deck lane.

### Step 3: one reservation

**Built.** Admission is the account's for every pool, at one arithmetic, and
the fixed shares are gone.

- **The door.** A backend asks before every native device allocation, at the
  size the device will be asked for (`GpuDeviceMemoryWork.TryAdmit`, from
  `DirectXHeapArena`, `DirectXDeviceMemory.Admit`, the descriptor heaps and
  `VulkanDeviceCommands.AllocateCounted`), and
  `GpuDeviceMemoryWork.CountAllocated` takes only the `GpuMemoryGrant` that
  answer made. The account answers for the segment's pool and the segment's
  own limit (`IGpuMemoryOwners.Admit`). A grant stands as a promise until it
  is counted, returned or refused. What a grant takes from its payer's
  reservation it takes under the account's lock and holds until the
  allocation is counted, so two grants made before either is counted cannot
  spend one reservation twice; a grant that is returned or refused gives it
  back.
- **Classes.** Every owner states a `MemoryClass`. A class that gives way
  (`MemoryClasses.GivesWay`: workspace, support, elastic, caches) is refused
  when its pool or segment has no room, and the creation door throws
  `GpuMemoryRefusedException`, which a region's growth and shrink catch.
  Fixed and floor storage is admitted past the limit and counted
  (`MemoryAdmissionReading.OverLimitGrants`), which is the presentation whose
  least does not fit; a holder states its floor with its reservation, and a
  mesh region's draw records are one (`SdfResidencyGrant.ReserveFloorGrowth`).
  A holder marks the allocations of a replacement it can do without
  (`GpuMemoryAdmissionScope`): inside the scope a fixed owner's allocation
  that finds no room is refused too. A mesh region's staging is named for an
  owner of its own (`SdfResidencySelector.RegionStagingOwner`), outside the
  region's reservation and its class: on unified memory, where both lie in
  one pool, a flush that stages more than any before it is admitted and
  counted, never refused, so an upload does not throw for want of room.
- **Reservations.** A holder reserves the peak its resources hold before it
  allocates (`MemoryAccount.ReserveDevice`, `MemoryAccount.DeviceReservation`),
  under the owner its objects are named for (`GpuObjectName.MemoryOwnerOf`).
  An allocation the reservation covers adds nothing to what the pool has
  promised. A mesh region reserves the standing buffer and its replacement
  together before the plan that asks for the growth
  (`SdfResidencyGrant.ReserveGrowth`) and lowers the reservation when the old
  buffer's readers finish (`SdfResidencyGrant.Released`). A shader pipeline
  reserves what a candidate adds before its build starts and keeps that
  promise until the build is taken, whether it installs, fails or is
  discarded; its reservation is always what it owns and what it has been
  promised, so releasing an earlier graph never lowers it under a running
  build's promise. A refused reservation is
  `ShaderPipelineMemoryAccount.RefusalCode`, with the installed graph still
  running, and so is a candidate's allocation the pool cannot hold: the
  device allocates more than the objects hold, and the candidate's storage is
  created inside the scope above.
- **The device's own refusal.** A native allocation that fails for want of
  memory hands its grant back (`GpuMemoryGrant.Refuse`): the segment's limit
  falls to what it holds until its budget is read differently or the refusal
  has stood its wait, the reading is refreshed at once
  (`GpuDeviceMemoryWork.DeviceRefusals`), and the caller keeps what it had.
  After the wait (`MemoryAccount.RefusalProbeInterval`) the next allocation is
  admitted on the reading and probes the device; a probe the device refuses
  doubles the wait, up to `MemoryAccount.MostRefusalProbeInterval`, and one
  it makes ends the refusal.
- **The selector.** `SdfResidencySelector`, one for a device's residencies,
  holds the one detail bias, grants each bake its budget
  (`SdfResidencyGrant.Budget`: what the residency fills and an equal part of
  the room the elastic budget has left, or its part of the excess), and
  reserves each mesh region. The elastic budget is what the pool's limit
  leaves the regions, less room for the largest one's replacement. The bias
  rises when the budget has pressed with no residency holding detail it can
  give up and falls once every residency fits half a rung finer
  (`SdfResidencyGrant.Decide`). Whether the budget presses is read at the
  decision, from the limit as it stands, what every residency names and the
  deciding residency's own report: another residency's report was made
  against the limit of its own last decision, so a limit that has been
  restored raises nothing.
- **Forced progress.** One owner in the process holds the forced allowance at a
  time (`MemoryAccount.Owner.TryReserveForced`); a bake that finds it taken
  waits as it waits for room.
- **The host pool.** Its limit is the budget, or the machine's free memory with
  what the pool held when that was read, when that is smaller, less the
  headroom: what the pool takes after a reading comes out of it, so one
  reading is spent once. The account reads the free memory itself
  (`MemoryAccount.AvailableInterval`), and the bake's own reading is gone. A
  mesh store read reserves the scratch it reads and expands an entry in
  before it allocates it, under one owner
  (`MemoryAccount.Owner.TryReserveWithin`): admitted with room or as the
  forced job, and otherwise after a wait no longer than
  `SdfMeshStore.WorkspacePatience`. A read that is not admitted is a miss
  that keeps its entry (`SdfMeshStore.WorkspaceRefusal`), and the mesh is
  derived or read again.
- **Pressure.** A pool that passes its limit by more than it did at the last
  reading, a device refusal or storage admitted past a limit fires one trim
  on the frame thread (`MemoryAccount.ServicePressure`, from
  `DeviceMemoryReadings.Refresh`). A pool that stands over a limit it cannot
  get back under asks once, not at every reading, and an owner that asks
  again for the peak it was refused is one refusal.
- **The OS reservation.** The draw records of every residency are the floors
  the selector records (`MemoryAccount.RecordFloor`), and the host passes
  them to `IGpuDeviceContext.ReserveMemory`, which on Direct3D 12 is
  `SetVideoMemoryReservation` on the local group, clamped to what the reading
  says may be reserved.
- **One background budget.** Every residency's `SdfGpuWork` draws on its
  selector's `SdfGpuWorkBudget`; a service that found it empty makes the next
  accrual bank its whole gap, up to `SdfGpuWorkBudget.MostBankSeconds`.
- **Servicing on completion.** Background work is stepped on the recording
  thread at three kinds of point: the frame's pumps, a held frame's drive, and
  the host's wait between frames. That wait is serviced (`HostWaits.Pace`):
  the pump thread sleeps on the pacer's deadline and one wake event together,
  one operating-system wait on the high-resolution timer and the event
  (`IPrecisionWaiter.TryWait` with a wake), and runs the work registered for
  it (`HostWaits.Serve`, a `WaitService`) when the event is set or the time a
  service asked for comes. Three things set it. A job's fence, asked as the
  job is submitted (`IGpuSubmissionFence.SignalOnCompletion`): Direct3D 12
  sets the event's own handle from `ID3D12Fence::SetEventOnCompletion`, and
  on Vulkan, which has no such call, one thread for the process waits each
  watched fence in the driver and sets it (`VulkanFenceWatch`). A client with
  a job to take or a slot given back (`SdfGpuWork.Wake`), which the bake calls
  when work of its pool has finished since its last take
  (`SdfMeshPathBake.Offered`). And time: a step the budget held asks to run
  again when the budget next holds credit (`SdfGpuWorkBudget.UntilHolds`). A
  step inside the wait takes no job within `SdfGpuWork.HostBudget` of the
  wait's deadline, and a failure there is kept for the next pump, where the
  host's recovery catches a lost device. A held frame's drive
  (`SdfGpuWork.Drive`) waits on the same event through the door, with a
  deadline, in place of an unbounded fence wait.
- **The backlog as time.** The bake's admission compares two wall times the
  pool measures of itself (`SdfMeshBakeBacklog`): what its workers take to
  drain the stages queued and those of groups admitted and still on their way,
  against what a new admission takes to reach them
  (`SdfMeshBakeBacklog.Round`). A take right after a burst of answers
  therefore admits what the workers finish before those groups arrive,
  whatever the frame rate.
- **Leases.** Background admission requires the tables' frame queue. The
  device timeline therefore covers jobs and frame readers with one retirement
  proof, including hidden tables. Pending carries remain explicit holds until
  submitted or cancelled. Job fences wake the host and protect ring-slot
  rewrites (`SdfGpuWork.EndLeases`); upload counts never certify retirement.
- **A job's hold.** A slot's buffers, its readback among them, come from one
  door (`SdfGpuWorkRecording.HostBuffer`, `SdfGpuWorkRecording.DeviceBuffer`,
  `SdfGpuWorkRecording.CopyToReadback`), which raises the service's
  reservation before it creates one (`SdfGpuWorkHold`, workspace under
  `SdfResidencySelector.WorkOwner`, in the device's pool and the host's) and
  lowers it when the buffer goes. A job the pool has no room for is handed
  back, and its client is offered a slot again once one comes back or
  `SdfGpuWork.DeferralRetry` has passed, while the other clients' jobs go on;
  a client with no slot of the service in use reserves its job as the
  reservation's floor, as does one with no job in flight whose job has waited
  `SdfGpuWork.ForcedPatience`. The account's floor is
  measured in the owner's own bytes, so it admits the shared block the device
  places a small resource in.
- **The clamped credit.** A settled charge is at most the wall time its job
  was in flight (`SdfGpuWorkBudget.MeasuredWithin`), and the GPU credit stops
  at `SdfGpuWorkBudget.MostDebtSeconds` of debt; both are counted on the
  budget's line (`clamped`, `floored`).
- **Readings.** `host.memory` prints each pool's limit, held, peak, retired
  and promised bytes, its room, what stands past the limit, the forced job and
  the counted exceptions, the bytes by class, and each ledger segment's limit,
  held, peak and promised bytes; `host.memory budget [host|device]
  <bytes|auto>` lowers a pool's budget by hand. `world.residency` prints the
  selector's line: the pool, each class's bytes, the elastic budget, the bias
  and each residency's grant.

**Invariants, and what enforces each.**

- *A device byte is admitted before it is counted.* The type: only
  `GpuDeviceMemoryWork.TryAdmit` makes a `GpuMemoryGrant`, and
  `GpuDeviceMemoryWork.CountAllocated` takes nothing else. It stops where a
  backend allocates without counting, which the API's usage against the
  ledger shows.
- *What a pool holds and has promised stays under its limit through every
  admission, apart from the forced job and storage that cannot give way, both
  counted.* One private arithmetic in the account, which its three doors call
  and nothing else adds a promise past. A limit that falls leaves what is held
  standing, read as `MemoryAdmissionReading.OverLimit`.
- *A grant is settled once.* `GpuMemoryGrant` refuses a second settlement.
- *A reservation's bytes are spent once.* A grant's credit and the promise it
  comes out of change together under the account's lock.
- *A pipeline's promise ends with its build.* The reservation is one sum of
  what the instance owns and its promises, set at every event; the build's
  promise is cleared where the build is taken, on each of its three ways out.
- *One forced job at a time.* One field in the account, taken and released
  under its lock.
- *A region's buffers are allocated only under its reservation.* The tables
  reserve before they plan (`SdfResidencyGrant.ReserveGrowth`) and the arena
  places nothing past what was reserved (`SdfMeshRegionArena.SetLimit`). It
  stops at the owner: every residency's region shares one account entry, so a
  region holding more than its own peak hides as much of another's promise.
- *The budgets are one sum.* The selector computes every residency's budget
  from one reading under one lock; the residencies' budgets add up to the
  elastic budget.
- *A job's completion wakes the host's wait.* One door submits a job
  (`SdfGpuWork`'s own submit), and it asks the slot's fence for the wake as it
  does, so no job is in flight without one. The wake may come early or twice;
  a step reads the fences either way. It stops at the host: a thread that
  never paces steps the work at its frame's pumps alone.
- *A service runs only between a frame's present and the next control
  phase.* Only the pacer's form of the door services (`HostWaits.Pace`); a
  wait inside an iteration never re-enters one.
- *What a job reads is neither rewritten nor released before its fence.* The
  lease is stamped in the one door that submits a job, and the tables' two
  doors read it: the retirement door and the upload. It holds on any queue;
  on the frame's own queue the upload's wait for its predecessor already
  implies it, so the doors never wait and `lease-waits` reads zero.
- *What a job holds is reserved before it is allocated.* The slot door is the
  only place a slot's buffer is created, and only it names objects for the
  work's owner. It stops at the owner (every service shares one account
  entry, so another service's idle buffers stand against a floor until they
  go, within `SdfGpuWork.IdleSeconds`) and at the clients' own tables and
  caches, which outlive a job.
- *The backlog is wall time on both sides.* `SdfMeshBakeBacklog.Holds` is the
  one test, and the pool feeds it: each worker times the stage it ran, and a
  group's first queued stage times its lead.
- *The GPU credit lies between a bank's worth and the debt's floor.* One
  private write lowers it (`SdfGpuWorkBudget`), which drops what passes the
  floor, and a settled charge passes `SdfGpuWorkBudget.MeasuredWithin` first.

**Why the frame thread, and not a queue and thread of its own.** The cause is
that only the frame thread records and submits, and it did so at two fixed
points of its iteration, so work that completed between them waited for the
next. Two changes remove that: have the frame thread take the work up
wherever it would otherwise sleep, or give the work a thread and queue. The
first is built.

- The frame thread's long sleep is the pacer's wait, on both backends: a
  target the swap chain cannot reach in a whole number of refreshes, and
  every target on a variable-refresh display, is paced by the timer
  (`PresentPacingPolicy.Resolve`), and that wait is now ended by the work.
  Nothing in it spins: the thread sleeps in one operating-system wait and
  each wake has work behind it or a time a service named.
- A second recording thread needs every device object the jobs touch to be
  safe from two threads: the submission batch and its fences, the recorder's
  resource states on Direct3D 12, descriptor writes, the memory arenas and the
  work counters are single-threaded by contract
  (`GpuBatchedSubmissionFence`, `GpuSubmissionBatch`). It also needs jobs to
  read the tables without the frame thread, which the leases above make
  possible but which no part of the tables does yet. Its gain over the built
  form is confined to the time the frame thread is busy or blocked in a
  driver call, which is under a frame.
- A second queue would let the driver's scheduler interleave a long job with
  the frame, in place of cutting jobs to `SdfGpuWork.FrameShareSeconds`. That
  is a rendering gain, not a servicing one, and it is what would justify the
  cost above; the leases are what such a queue needs, so it moves the
  service's submit and keeps the rest.

**How the job system's pool takes this.** The pool owns CPU work
([the engine job system](engine-job-system.md#1-the-scheduler)); the frame
thread stays the one thread that records and submits, and drains each result
at its own install point. Three seams are the ones it moves, none replaced:
the bake's static pool, whose queue and workers `SdfMeshBakeBacklog` reads
and whose stage and lead timings it is fed, becomes the `Bake` class's ready
queue and the pool's worker count; `SdfMeshPathBake.Offered` becomes the
bake channel's signal that it holds a result; and a channel whose reader is
the frame thread registers a `WaitService` whose wake its producers set, so
the pacer's wait is the frame thread's drain between frames for every such
channel, as it is for the fences here.

**Replaced.** The mesh region's and the shader pipeline's quarter of the
device and the helper they shared; a bias per bake; a budget per `SdfGpuWork`;
a forced job per bake; the bake's own free-memory pacing; the throw from the
mesh region's growth; the observed owner that stood outside admission.

**What remains of the step.**

- *Waits the door does not service.* The swap chain's own wait
  (`ISwapChainPacing.TryWaitForFrame`, a native wait of up to
  `ISwapChainPacing.MaxRefreshesPerPresent` refreshes) and a present that
  blocks in the driver are not ended by the work. At one refresh a present
  that costs nothing: the frame's pumps come every refresh. Past it only
  Direct3D 12 paces off the swap chain (its sync interval; Vulkan's FIFO has
  none, so the timer paces every Vulkan target below the display's rate, and
  is serviced), so a Direct3D 12 host at two to four refreshes a present
  steps the work at its pumps alone, and the serialized lighting, a job at a
  time, feels that first. The
  [control plane's step C0.6](engine-job-system.md#the-steps) puts the
  Direct3D 12 frame latency waitable on the door, where it is serviced as the
  timer is; the reading below at such a target says how much is lost until
  then.
- *A frame that never waits.* A frame bound by its own CPU or GPU work past
  its period has no pacer wait, and steps the work at its two pumps.
- *The workspace class's size.* Bake workspaces reserve against the host pool,
  and no job is split or failed by name for passing a class of its own.
- *The lighting's device memory* (the trace scene's region, the feedback
  volume, the hit and meter caches) and each client's small tables are fixed
  storage until they reserve: never refused, and outside the elastic budget.
  They outlive a job, so they are a holder's reservation, not a job's.
- *Retired bytes by segment.* A holder states them and names a pool.
- *Priority across residencies.* The budgets and the background budget are
  shared equally and first come.

**Check.** The laws: `MemoryAdmissionLawTests` (held plus promised along a
falling budget, a covered allocation, two grants before either is counted,
the floor, the device's refusal and its probe, the one forced job, the host's
free memory spent once, transient work that waits for room, the pressure trim
fired once), `SdfResidencySelectorLawTests` (four residencies' grants under
the limit at every step of a falling budget, the one bias, a report made
before the limit was restored, a region's growth under its reservation, its
staging on unified memory, the work budget's rate), the tables' law that a
region with no room keeps its buffer and refuses the meshes past it without
throwing, the store's law that a read with no workspace is a miss that keeps
the entry, and the pipeline's laws: the refusal by the account, a failed
candidate's promise, an earlier graph released under a running build, and a
candidate whose allocations the pool cannot hold. For the servicing:
`HostWaitsLawTests` (the pacer's wait serves when woken and when a service
asks, never sooner than `HostWaits.LeastServiceDelay`, and ends on its
deadline), `GpuSubmissionBatchLawTests` (a completion signal follows a
submission through its batch, and one owed to a submission that never reaches
the queue is set, not lost), `SdfWorldTablesWorkLawTests` (a completed job is
answered and the next taken inside the wait with no pump; a failure there is
kept for the next pump; a job's lease holds what it reads under a fence no
later submission completes; what a job holds is reserved before it is
allocated and a job with no room waits; a service with nothing in flight
reserves its job as its floor; a reversed timestamp pair charges the
estimate), `SdfMeshBakeBacklogLawTests` (the same queue admits or waits by
the lead), `SdfResidencySelectorLawTests` (the budget's debt is bounded
whatever a job reports) and `MemoryAdmissionLawTests` (a floor admits the
shared block its resource is placed in). The windowed readings are verification requests to the lead: the
lowered-budget path (`host.memory budget device` in steps at the meadow, on
both backends: `over-limit`, `detail-bias`, `refusals` and `growth-refusals`
at each step, with captures), and the two below.

*The slow-present reading.* From a cold store on each backend, with
`world.target 10` and then `world.target 5` set before the bake settles, read
`world.residency` twice some seconds apart. What decides it: the budget's
`gpu-charged` over the wall time between the readings reaches
`SdfGpuWork.GpuShare` while the bake owes meshes, at both targets (before,
it fell with the target: a job a pump for the serialized lighting, and a
bank of `SdfGpuWorkBudget.MostBankSeconds` at five frames a second);
`gpu-work`'s `services` and `service-jobs` rise between the readings, since
the jobs are now taken inside the wait; and the bake's done line reads fewer
`idle` takes of its `takes` and a lower `per-take` `waiting` than the frame
rate alone gave. `lease-waits`, `clamped` and `floored` read zero, and
`deferred` reads zero unless the device pool is full. A third target, which
the swap chain paces on Direct3D 12 and the timer on Vulkan (`world.target 20`
on a fixed 60 Hz display), reads the part the door does not service: the
Direct3D 12 share there against Vulkan's is the loss.

*The quiet cold bake.* `puck bench startup worlds/debug --bake` on each
backend. The bake's GPU time is held to the share by design, so servicing
does not raise it; what it removes is waiting: an answer reaches the workers
when its job completes and a lattice is taken when it is ready, in place of
a frame later each, and admission keeps the workers fed. What decides it: the
done line's `idle` takes fall as a share of `takes`, `per-take` `waiting`
falls, the `cpu=[…]` stage totals stand, and the interval falls by the time
the workers stood idle. If `budget-holds` now dominates the takes, the share
is what holds the minute, and raising it while a boot has nothing worth
protecting is a call on the frame during a cold boot.

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

**Builds.** The prototype below supplies the capability read and the indexed
read's first result. Next is [the sparse design for
review](#sparse-backing-design-for-review), before backend implementation:
reserved resources on Direct3D 12, sparse buffers on Vulkan, and the existing
contiguous backing where either is unavailable. Both use [the pressure
transaction](#decision-8-pages-are-three-things-and-only-one-changes) and the
backend pool's release of its kept empty blocks under pressure.

**Replaces.** Growth by copy and shrinking by replacement on the sparse path.
The fallback retains both. One region per residency remains on both paths.

**Check.** The capability table. The cost of the views, glass, cascade,
lighting-trace and picking kernels with and without the index, on both
backends (`world.gpu-timing`), with the fragmentation, the largest payload,
the descriptor count and the kept blocks priced. Then: allocated bytes per
landed byte fall toward one, with page tails and physical-block slack reported
separately; under a forced budget drop the ledger's held bytes fall after the
fences with no allocation made to free them. The checks below distinguish a
hole in a buffer, a returned page and actual physical release.

#### The prototype as built

This section records the prototype at `e9c1d485c9`. Its slab views and switch
were removed by the continuation's fallback piece; any remaining paired
prototype measurement uses that frozen revision, not the current interface.
The capability read remains live.

**The capability read.** Each backend reads, at device creation, how the
device can back a large buffer and give memory back, into the record that
already holds its binding limits (`GpuDeviceCapabilities.Backing`, flags
`GpuBackingSupport`, with the native figures beside them:
`ReservedResourcesTier`, `ResourceAddressBits`, `SparseAddressSpaceBytes`,
`StorageBufferOffsetAlignment`). Direct3D 12 reads options' tiled resources
tier, the virtual address support query and whether the device implements
`ID3D12Device1` and `ID3D12Device3` (`DirectXFeatureReads.Capabilities`,
`GpuDeviceCapabilities.WithDirectXBacking`); Vulkan reads the sparse base
features, the sparse properties, a sparse-binding queue family, the descriptor
indexing feature and the two memory extensions
(`VulkanNativePhysicalDeviceApi.GetDeviceCapabilities`,
`VulkanLogicalDeviceFactory`'s `BackingOf`, which also prints a `[backing]`
line at device creation). `world.counters gpu` prints them on its
`capabilities` line (`backing=` and the figures), and its JSON form carries
them; a device whose backend never read them prints no `backing=`, so none is
told apart from unasked.

| Capability | RTX 4070 (this machine) | RTX 2060 | Steam Deck (Vulkan, RADV) |
|---|---|---|---|
| Sparse binding, buffer residency, aliasing, a binding queue, strict non-residency | Yes on both backends (`world.counters gpu`: Vulkan `sparse-binding+sparse-residency-buffer+sparse-residency-aliased+sparse-queue+sparse-non-resident-strict`; Direct3D 12 the same without aliasing) | Expected on NVIDIA's Vulkan driver; no vendor page this lane could fetch states it, so it is **unverified** until a reading on the card | Yes: RADV sets each from `enable_sparse`, which is `pdev->info.has_sparse` unless disabled, and `has_sparse` holds from GFX7 except four older APUs **[external: RADV backing]** **[external: RADV backing]**; the Deck's GPU is GFX10.3. Every queue family carries `VK_QUEUE_SPARSE_BINDING_BIT`, and a dedicated sparse queue may be added **[external: RADV backing]** |
| Sparse address space | 1 TiB (`sparse.address-space=1099511627776`, Vulkan) | Unverified | `pdev->info.virtual_address_max` when sparse is enabled **[external: RADV backing]** |
| Memory priority (`VK_EXT_memory_priority`) | Yes on both backends | Yes on Turing and newer (NVIDIA's 535.43.02 notes, Linux; listed on Windows drivers before it) **[external: NVIDIA 535]** | Yes: `.EXT_memory_priority = true`, `.memoryPriority = true` **[external: RADV backing]** |
| Pageable device-local memory (`VK_EXT_pageable_device_local_memory`) | Yes on both backends; Direct3D 12 also answers `EnqueueMakeResident` | Yes on Turing and newer, the notes spelling it `VK_EXT_pageable_device_memory` **[external: NVIDIA 535]** | **No**: absent from RADV's extension table **[external: RADV backing]**; an open Mesa issue asks for it **[external: RADV backing]** |
| Storage-buffer arrays indexed non-uniformly | Yes on both backends | Expected (Puck now requires it of every Vulkan device) | Yes: `.shaderStorageBufferArrayDynamicIndexing = true`, `.shaderStorageBufferArrayNonUniformIndexing = true` **[external: RADV backing]** |
| Storage-buffer offset alignment | 16 bytes (Vulkan) | Unverified | 4 bytes **[external: RADV backing]** |
| Direct3D 12 reserved resources | Tier 4, 40 address bits a resource | Unverified | Through vkd3d-proton: a tier from RADV's sparse features (`d3d12_device_determine_tiled_resources_tier`, none without sparse binding, aliasing, buffer and 2D image residency and a sparse queue), and a hard-coded 40 address bits a resource **[external: vkd3d-proton]** |

Direct3D 12 has no capability to read for paging: every heap is paged by the
operating system's memory manager, and destroying a heap is the reliable
release **[external: Microsoft residency]**, so `PageableDeviceLocal` is set on
every Direct3D 12 device by definition, not by a query. The Deck's lack of
pageable device-local memory means an over-budget allocation there fails or
spills rather than paging, which is decision 2's reason to reserve before
allocating on every device.

**The indexed read.** Every kernel reads the mesh region through one door,
`frame/sdf-mesh-words.hlsli`: a draw's record through `sdfMeshRecordWord`, a
mesh's own words (vertices, triangle materials, indices, level table,
members) through `sdfMeshWord`. The prototype divided a word index by a
64 MiB slab's word count and used the remainder as its place. It bound the
region a second time as 64 views, enough for `GpuRegion.MaxByteCount`, in
the World, mesh and probe sets. A slab past the region's end bound the
unused-member buffer. Its generated switch was `SDF_MESH_SLAB_INDEX`:
off, `sdfMeshWord` reads the one binding directly, as before; on, it indexes
the views with `NonUniformResourceIndex`, which is the read real slabs would
put in every kernel (records stay one direct table, as they would beside
slabs). The views are bound either way, so the two builds differ only in the
kernels' reads.

- *The invariant:* no kernel reads a mesh word except through the door, so the
  switch reaches every read. *What enforces it:* the door ends by defining
  `sdfMeshRegion` as a name that does not exist, so any read of the region
  after it fails to compile; every reader of mesh words includes it
  (`frame/sdf-mesh.hlsli`, `shade/sdf-probe-trace.hlsli`). *Where it stops:* a
  kernel that declared and read the region before including the door would
  escape; none does, and the bake reads no mesh word.
- *Who reads mesh words* (from the compiled modules with the switch on, each
  declaring `ShaderNonUniform`): surface, views (plain, interpreter and
  shade sets), glass (both), resolve (reprojection), the mesh and both shadow
  vertex stages, the glass raster fragments, occlusion selection (the level
  table), and the probe trace, relight and meter. Picking reads none on the
  GPU: `SdfWorldPickReadback` copies visibility record words, and the CPU
  diagnostics read `SdfMesh` buffers.
- *What it costs the build:* nothing to the stored meshes. `puck mesh identity
  --against` the base build reads the same identity on both backends (code 0
  members differ, both bake kernels the same), so a comparison's two builds
  share one mesh store.
- *What it cost every device:* the prototype required
  `shaderStorageBufferArrayDynamicIndexing` and
  `shaderStorageBufferArrayNonUniformIndexing` (`VulkanLogicalDeviceFactory`),
  which every bar device reports; its device chain enabled a feature flag
  past a structure's first (`VulkanFeatureFlag`).

**The measurement.** Two builds of one commit, `SdfMeshSlabs.IndexedRead`
false and true, frozen as `puck measure request` artifacts
(`--before-input-tree`, `--after-input-tree`), each with a solution build or
`-p:PuckShaderSpirvEnabled=true`. In a quiet window, for each build and each
backend, a windowed debug World with `world.lockstep on` before the first tick,
`world.wait bakes 600`, `world.rate pause`, `world.exposure pin`,
`world.cadence off`, `world.rates off`, then `world.gpu-timing off`, `on`,
and a full window of `world.gpu-timing` at the hero spawn and the meadow
stop. The passes it reads: `sdf.world$surface`, `$views` (and its shade sets
when present), `$glass`, `$glass-shade`, `$resolve`, `$mesh` (the vertex
stage and the occlusion kernel), `$shadow-near`, `$shadow-far`,
`$shadow-second`. The lighting trace runs off the frame: read the
residency's `gpu-work=[…gpu:…ms…jobs…]` in `world.residency` once the bakes
settle and again at `[sdf.mesh-gi] done`, and divide the difference by its
jobs. `world.counters gpu summary` beside each window must read the same draws,
vertices and texels in both builds (the switch changes no counted work), or the
timing compares different frames. Priced beside it, from the same runs:

- *The descriptor count:* `SdfMeshSlabs.Count` descriptors in every set that
  binds the region (a World set per ring slot, each mesh pass set, each probe
  set), read from `pipeline.inspect world` and the descriptor writes in
  `world.counters gpu`.
- *The kept blocks:* the device rows' residual in `host.memory` (room inside
  shared blocks and the empty block `GpuMemoryPool` keeps per key).
- *The largest payload and fragmentation:* no counter prints the largest
  placed mesh yet; `SdfMeshRegion.PlacedBytesOf` over the store's meshes gives
  both offline, the largest against the slab size and the tail each slab
  would leave unfilled at that size. The allocator lane adds the reading.

**What each reading decides.**

- *A capability missing on a bar device rules that backing out as the
  baseline* (upper hardware enhances; the baseline runs everywhere). On the
  table so far neither is ruled out: the Deck has sparse buffers, the 2060
  reading is owed.
- *The index's cost decides between them.* Within the noise of a paired
  window on every pass and both backends, slabs win, as favoured: no
  capability question, an emptied slab returns to the device, no
  single-buffer ceiling, and one store for every residency. Material on any
  pass, sparse backing of the one buffer wins on devices whose reading
  allows it, because it changes no kernel, and the region per residency and
  its ceiling stay.
- *The Deck is not measured here.* An RDNA2 lane scalarizes a descriptor index
  that differs across a wave in a loop over its distinct values (an inference
  from how AMD's compiler treats non-uniform descriptors, unverified here), so
  NVIDIA's cost is a lower bound for it. A windowed reading on the Deck is owed
  before slabs land their kernel path; a slab size larger than 64 MiB (a
  larger `SdfMeshSlabs.WordShift`) lowers the distinct slabs a wave meets and
  is the lever to try there.
- *The descriptor count and the largest payload size the slab:* the count
  must fit every set's pool on every device, and a mesh larger than a slab
  takes a slab of its own.

**The first reading: material on Direct3D 12.** At the hero spawn on the RTX
4070, the switch on against off, two boots off around one on, with the same
counted draws: `surface` about a fifth slower, `shadow-far` and `resolve`
several percent slower, `views` slightly slower, against a boot-to-boot spread
near one percent. By the rule above, sparse backing of the one buffer is the
direction on devices that allow it, and slabs do not land their kernel path.
On Vulkan at the meadow the pair did not hold its counted work equal (the
residency settles differently after the arrival, so the two boots drew a fifth
apart); where the work was close, `surface` and `views` showed no cost and
`resolve` a few percent. A Vulkan reading with the draws held equal (a stop
whose residency settles the same, or a frozen frame) is still owed; the door
and the switch stay, so the comparison can be read again.

#### Sparse backing design for review

**Status and scope.** The sm7b continuation answers the review below and lands
the first, contiguous-fallback piece. No sparse backend is enabled yet. The
capabilities are support
readings, not an enabled allocation path: `VulkanLogicalDeviceFactory.BackingOf`
reads sparse features, but `FeatureIndicesOf` does not enable them and
`BuildQueues` requests only the graphics/present families. The native mapping
entry points are absent. The 4070 readings support this direction; the Deck
column is driver-source evidence, and the 2060 is unverified. Absence of
pageable device-local memory does not disqualify sparse buffers: Puck must
unbind and free its allocations, rather than rely on overcommit.

**The choice.** Preserve `SdfMeshRegionArena` as the one mesh placement store,
`GpuRegion` as its staging/copy owner, and the direct word read. Add an optional
range-backed destination to the region's existing owned-buffer mechanism.
Backends supply resource creation and mapping operations; page coverage,
transaction states and retirement belong in `Puck.Abstractions`, with mesh
range demand supplied by `SdfWorldTables`. Ordinary regions keep their current
policy. No SDF vocabulary belongs in the backend contract.

The alternative, shader-visible slabs, remains rejected by the first reading.
A native allocation per hardware page would give immediate physical release
but multiply handles and hit Vulkan's allocation-count limit. Keeping all
physical memory behind one giant allocation would preserve that problem in
another form: unmapping could never release part of it. Use native pages
suballocated from the existing backend pool instead, and expose its slack.
This keeps the direct read while making physical-block release possible.

##### Page size, address space and physical blocks

There are two relevant granularities below a mesh, neither a publication unit:

| Quantity | Direct3D 12 | Vulkan |
|---|---|---|
| Native mapping page | 64 KiB; query the reserved buffer's tiling to verify its coverage | The created buffer's `VkMemoryRequirements.alignment`; never infer it from storage-buffer descriptor alignment |
| Physical allocation block | Initially the existing `DirectXHeapArena.HeapBytes` | Initially the existing `VulkanMemoryArena.BlockBytes`, rounded up if the native page requires more |
| Mapping | Reserved buffer tile to heap tile | Buffer byte range to device-memory byte range |

Microsoft documents [64 KiB reserved-resource
tiles](https://learn.microsoft.com/en-us/windows/win32/direct3d12/uploading-resources).
The Vulkan specification defines [sparse buffer page
size](https://docs.vulkan.org/spec/latest/chapters/sparsemem.html)
through memory requirements; [sparse
binds](https://docs.vulkan.org/refpages/latest/refpages/source/VkSparseMemoryBind.html)
require aligned resource offsets, memory offsets and sizes. A mesh may cross
any number of pages. Several meshes may occupy one page, counted once; an
empty range is not a free page while a neighbour, a landing or a retired
reader still needs any byte of it.

Create one virtual resource for the region's lifetime, with its usable extent
bounded by `GpuRegion.MaxByteCount` and the backend's actual buffer/view
limits, rounded down to whole native pages. Check the native resource size,
descriptor range and address arithmetic together; do not bind a four-GiB
descriptor merely because virtual address bits allow a four-GiB resource.
Track virtual reservations across the device, including scratch: Vulkan's
`sparseAddressSpaceSize` is a device-wide capacity, not a per-world allowance.
Creation validates that capacity before selecting sparse backing. A virtual
reservation is reported separately and is never charged as committed VRAM.
Driver bookkeeping still appears in the API usage reading.

Logical extent, mapped pages and physical allocation bytes become separate
readings. `GpuRegion.ByteCount` remains a logical bound; it can no longer mean
the sparse destination's held bytes. Reserve space once, then map only the
union of the draw-record prefix, placed mesh ranges and admitted in-flight
landing ranges. Do not map all holes below the highest address, nor allocate
a host shadow of the virtual extent. Keep `GpuRegion.Shadow` and bounded
landing. Growing the draw prefix uses the arena's existing relocation path,
with old pages retained until its readers finish.

Extend `GpuMemoryPool` to lend page-aligned physical ranges as well as placed
resources, using its existing `GpuRangeAllocator` tickets. Add sparse backing
to the pool key so nonmovable ordinary resources cannot pin a sparse block.
Do not add an allocator beside `DirectXHeapArena` or `VulkanMemoryArena`.
Physical blocks may serve several regions; this shares free physical space,
not mesh identities or payloads between residencies. Initially keep the
existing block size to avoid an unmeasured tuning choice. New allocation
counts must respect the device-wide Vulkan limit as well as byte admission;
the [device limits](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceLimits.html)
are distinct constraints. Allocation-count pressure requests the same trim
and defers elastic growth; it does not create an uncounted allocation.

##### Backend resource and queue contract

**Direct3D 12.** Require tiled-resource tier 2 or greater and successful
validation of the buffer description. Create a row-major buffer with the
ordinary region's storage and copy usages through
[CreateReservedResource](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-createreservedresource).
It starts with virtual memory only. Physical pages come from admitted DEFAULT
buffer heaps. Map ranges through
[UpdateTileMappings](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12commandqueue-updatetilemappings),
one call per supplying heap as needed; unmap with the NULL range flag. Use
mapping flags NONE: Microsoft's [mapping-flags
reference](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_tile_mapping_flags)
says NO_HAZARD is unsupported.

The binding queue is the existing direct/frame queue. Flush its pending
`GpuSubmissionBatch` through `DirectXDeviceContext.CommandQueueHandle` before
mapping; never retain a raw queue that skips that door. Queue the map before
initialization/upload, and signal a monotonically identified submission fence
after the mapping batch. Unmapping is queued only after the retirement proof
below. Keep the heap alive through the fence after unmapping, including if
its reserved resource has already stopped being published. Use the backend's
ordinary transfer/UAV-to-reader barriers for contents, plus the appropriate
aliasing synchronization on physical-page reuse; mapping alone neither
initializes data nor replaces [resource
barriers](https://learn.microsoft.com/en-us/windows/win32/direct3d12/using-resource-barriers-to-synchronize-resource-states-in-direct3d-12).
The implementation must express that synchronization in the backend's
enhanced-barrier path, rather than relying on implicit decay.

**Vulkan.** Enable both `sparseBinding` and `sparseResidencyBuffer`, and create
the buffer with both corresponding sparse creation flags, storage usage and
transfer source/destination usage. Sparse binding alone requires the whole
buffer to be bound and cannot implement holes. Choose a compatible
device-local memory type from the buffer's memory-type bits through the
existing memory selection and segment attribution. Neither sparse aliasing,
strict non-residency, memory priority nor pageable device-local memory is a
prerequisite; never intentionally read an unbound byte.

Prefer the graphics queue when its family advertises sparse binding;
otherwise request one queue from a sparse-capable family during logical
device creation. Retrieve it once in the device's queue owner. Native calls
go through the queue submission door, which flushes deferred graphics work
and serializes host access, including when the sparse and graphics handles
are the same. No copy, draw or dispatch moves to the binding-only queue, so
buffer access stays in the graphics family. Any future data-access queue
must join the lifetime and ownership-transfer contract before using pages.

Use [vkQueueBindSparse](https://docs.vulkan.org/refpages/latest/refpages/source/vkQueueBindSparse.html)
with buffer binds, not `vkBindBufferMemory`. Order it explicitly even on the
graphics queue. Use one device timeline, through the existing
`VulkanQueueSubmitter.Signal` door and `VkTimelineSemaphoreSubmitInfo` on
the sparse bind. The graphics checkpoint flushes deferred readers and signals
value N; the binding batch waits N and signals N+1; the first graphics
submission touching the range waits N+1 at its first accessing stage (transfer
or compute). Every signal of this shared timeline is ordered after its prior
signal, including when queues differ: numeric value order alone is not an
execution dependency. A successful bind call is not a completed bind. Puck
already enables timeline semaphores on its Vulkan 1.3 device; this adds no
feature floor. A never-used range has no reader dependency, but still joins
the device timeline's signal order.

Binding synchronization supplies execution ordering, not the availability
and visibility of copied contents. Keep transfer-write to shader-read/write
dependencies in the graphics submissions and memory dependencies across
compaction copies, as required by the [Vulkan synchronization
chapter](https://docs.vulkan.org/spec/latest/chapters/synchronization.html).
An unbind uses a null memory handle. Only its completed timeline value permits returning the
physical range. Device loss is teardown, never a successful retirement.

As an independent implementation check, vkd3d-proton's
[sparse queue submission](https://raw.githubusercontent.com/HansKristian-Work/vkd3d-proton/master/libs/vkd3d/command.c)
also orders sparse binds with semaphore edges and retains resources through
completion. It uses timeline semaphores too. No API
disagreement was found on the required ordering. This is corroboration, not
a translation layer to copy or evidence of Puck running on the hardware.

##### One transaction from admission to publication

The destination owns each page's state: absent, reserved for mapping, mapping
submitted, writable, published, retiring, unbinding, then absent again.
Every state carries the device generation, page generation and placement
ticket. Only the destination issues a writable-page lease, covering an exact
byte range and its pages' generations, once its mapping dependency can be
ordered before writes. `Write`, `WriteRuns`, `OweSourced`, carry and landing
recording pass through that lease door. An unleased page cannot acquire owed
words. `GpuRegion` clips joined and paired copy runs at unleased pages; if the
run table fills it emits another bounded copy, never merges across a hole.
`CompleteLanding` checks the recorded lease before crediting each slice, and
`MeshStands` requires all credited ranges' generations still match. Retiring
invalidates the page generation immediately; a later mapping cannot revive an
old completion. A refused or stale slice is requeued, with its rows absent.
Callers cannot return a raw pool placement or mark a mapping complete.
Cancellation follows the same retirement path.

1. The arena proposes ranges, without publishing them or relinquishing the
   old complete tier. The backing computes newly needed pages, excluding
   already held pages and counting a shared boundary page once. Physical
   placement is planned under the pool's lock; its one-use plan identifies
   both reusable space and the exact new block bytes it needs. Consuming a
   stale plan redoes admission, never silently allocates a larger block.
2. Extend `SdfResidencyGrant.ReserveGrowth` to reserve that physical plan,
   through `MemoryAccount`, before making the ranges usable. Sparse growth
   reserves existing and retiring page coverage plus the newly required
   pages, never the new block's full size. The pool separately admits the
   block's net slack under a reclaimable pool owner. `payerBytes` is the
   plan's new page coverage, so a 64 KiB demand in a new 64 MiB block reserves
   64 KiB for the region and counts the remainder once as pool slack. The
   fallback still reserves old and new buffers together. Keep pool-to-account
   lock order as in today's native allocation callback; do not call the pool
   while holding the selector/account lock.
3. Each native block allocation still passes `GpuDeviceMemoryWork.TryAdmit`
   at its full size and `CountAllocated` with its grant and actual segment.
   Reuse of an already allocated page transfers attribution without a new
   allocation count. Page mappings are placements (`CountPlaced`), never a
   second device-byte charge. The pool owns block slack once; the residency
   owns its committed page coverage once. A pool block shared by residencies
   cannot be charged in full to every residency.
4. Queue mapping, acquire its writable-page leases, then upload only needed
   words through the existing landing budget. Buffers need no blanket clear:
   no reader, including `world.rows check`, may inspect holes or padding.
   Do not run `GpuRegion.RecordCarry`'s full-destination clear over sparse
   holes, clear live neighbouring words, or carry the old region on growth.
   A new range within a live page writes its own bytes only. The stable
   resource and offsets mean existing descriptors and landed bytes stay put.
5. Publish through the existing whole-mesh landing door only when every
   required page lease still has its recorded generation and every upload has been submitted with the
   dependency that precedes its readers. Keep the old tier, or the existing
   absent record, until then. An admission/allocation refusal returns only
   unused promises and leaves prior publication intact. An unsubmitted map
   can be cancelled immediately; submitted work keeps its storage through
   completion. Failed submissions leave affected ranges unpublished and
   retained until their actual completion or device teardown, never freed
   based on a reusable fence's initial signaled state. A grown draw prefix
   can move a mesh with no prior complete tier; `StageMeshDraw` stages it
   absent until its destination is leased and its words land. It cannot use
   the old synchronous moved-mesh write to bypass admission.

The selector must stop subtracting the largest sparse virtual capacity as
replacement headroom (`SdfResidencySelector.UsableOf`). Retain that allowance
for contiguous replacements, and reserve bounded mapping/compaction work in
the workspace class. Extend the grant's created/grown/released bookkeeping to
consume backing results, rather than reconstructing them from `ByteCount`.
`SdfMeshRegionArena.SetLimit` still bounds addresses; sparse byte admission
also checks each placement's physical plan, because a high address with
holes is not its committed cost. Draw-prefix floors are page-rounded, with
pages shared with elastic ranges counted once. Step 5 adds tier floors to
that same coverage; step 7 does not invent a second eviction policy.

Reservations must identify the particular region's payer, not just the
shared `SdfResidencySelector.RegionOwner` aggregate. Extend the existing owner
registration/naming to give each region a child owner and aggregate its
report, so one region cannot spend another's pending page credit. Staging
retains `RegionStagingOwner` and its separate admission, including on UMA.
Held physical blocks, mapped coverage, live ranges, promised additions,
retired coverage and pool slack are reported separately. Retired is a subset
of held, not another sum. `SdfResidencyGrant.Released` reduces a reservation
only after the backing transaction's release; the ledger falls only when
`ReleaseHeap` or `vkFreeMemory` actually releases a block. OS usage may lag.

##### Shrink, eviction and physical relief

On pressure, `MemoryAccount.ServicePressure` still requests trim and
`SdfWorldTables.ServiceMemoryTrim` services it on the frame thread. Stop
elastic growth, cancel unsubmitted additions and let the selector raise its
bias. Remove unwanted names through the arena and step 5's tier policy.
Unpublishing changes live range counts, not backing held.

Seal the removed ranges against new readers and writers. Account for landing,
the draw-prefix copy, views, shadows, GI/trace and all recorded-but-not-yet-
submitted work. All present readers use the frame queue. A device-wide queue
checkpoint flushes the batch containing the last readers and signals a
monotonic value on one `ID3D12Fence` or Vulkan timeline. Retirement reads that
device value, never the residency's upload count, and `ServiceMemoryTrim`
polls it even when the residency is not rendered. A pending contiguous carry
remains a reader until submitted or cancelled; a checkpoint cannot retire
its source beforehand. Waiting merely for the most recent upload does not
cover draws submitted after it. A transaction pins the region and
its pages while that proof is pending; an abandoned submission is not proof
that some earlier successful reader finished.

Queue unmapping behind that reader checkpoint on the GPU, without first
waiting on the host. Return the physical range through the pool's ticketed
return only once the host observes the unmap timeline complete. No reuse by
another resource, heap destruction or memory freeing precedes that proof.
A logical shrink releases empty tail pages in this way
without resizing the virtual resource; eviction can release empty interior
pages too. A page still intersecting a live range stays mapped, even if most
of its bytes are empty. A renewed request during retirement takes a new
generation after the unmap completes; it cannot resurrect the old ticket.

Add pressure trimming to `GpuMemoryPool` itself. Under pressure its normally
kept empty block is released, and a block that empties when a pending unmap
finishes is released too: the trim request persists through the pending
retirements. No live placement is freed by this trim. Microsoft explicitly
distinguishes unmapping from [destroying heaps to reclaim
residency](https://learn.microsoft.com/en-us/windows/win32/direct3d12/residency).
Vulkan likewise requires freeing the actual memory allocation. Therefore
free-page counts alone never satisfy step 7's relief check.

**Fragmentation has two levels.** Partly live virtual pages retain their
tails. On sparse backing, unused virtual words alone never trigger the
arena's density-based pack: holes cost no backing. Logical relocation runs
only when no free run holds a demanded mesh or the virtual extent is exhausted,
and pays its upload and workspace costs. Page-aligning meshes of at least one
page is an optional measured placement choice, not another arena. Empty physical pages
scattered among blocks need a different operation: packing physical backing
without changing a mesh address. Extend the generic pool's placement
mechanism with a victim-excluding relocation plan, not a second range
allocator. It reserves holes in other existing sparse blocks before moving
anything. Never mix these movable blocks with ordinary resources.

For physical packing, pre-admit and allocate one device-local scratch buffer
large enough for one native page per active page size, plus its command and
binding workspace, while there is room. Reuse `IGpuRecorder.CopyBuffer` and
the same mapping transaction: seal readers; copy a source page to scratch;
complete the copy; unmap it and bind its unchanged virtual address to the
reserved destination page; wait binding-ready; copy scratch back; establish
the reader barrier and reopen readers. Keep the old physical source until
both unmap and copy-back complete successfully, so an unsubmitted or refused
replacement bind can retain/restore it. An unknown submission outcome pins
both placements until resolved or the device generation is torn down.
Scratch is reusable only after copy-back completes.
Here **seal** means no reader is ordered between copy-out and copy-back,
not that live mesh rows are unpublished. Rows and addresses remain live;
the queue dependency delays their next reads until copy-back and its reader
barrier. Compaction never blanks geometry to move physical backing.
There is no simultaneous sparse alias, no mesh-row change and no whole-region
copy. The copied bytes include every live neighbour on the page. All steps
are ordered through the frame/binding queues above and charged to background
work; bound each batch so ordinary rendering can run between transactions.

Pack only when other blocks already have enough free pages to empty a victim.
If workspace was not admitted before pressure, or live virtual-page tails
leave too few holes, report that obstruction and continue eviction/trim; do
not allocate memory to free memory. Step 5 can evict more optional tiers or
use already reserved logical-relocation workspace. Its last resort remains
whole-region release and counted floor restoration. Thus sparse backing
guarantees release of empty blocks without replacement, not that arbitrary
name eviction immediately releases the same number of physical bytes.

##### Fallback and failure boundaries

Select backing once when creating a region on a device generation. Missing
sparse features, a usable binding queue or a valid virtual resource select
today's contiguous destination automatically, with a diagnostic reason.
Keep the same arena, shader read, staging, publication and admission. A native
OOM during page commitment is pressure on the selected path; do not allocate
a contiguous replacement as a retry. Device loss recreates the region on the
new device and chooses again. Programming/validation errors are defects,
not reasons to silently downgrade.

On the fallback, `GpuRegion.Grow` and `Shrink` retain their admitted copy or
relanding paths. A shrink needing a new buffer may be refused under pressure;
retain the old one and report the remaining held bytes. Only a complete,
fenced region release needs no replacement allocation. This preserves the
bar-device baseline without pretending it has sparse relief. Sparse mode
does not solve the single-region address ceiling or geometry duplicated
across worlds; those remain explicit costs of the chosen shader path.

The first implementation piece removes the prototype's unconditional
slab-array bindings and Vulkan storage-buffer indexing requirements, with
callers and generated shader interfaces together. Sparse and contiguous
direct reads need no such feature. The remaining prototype readings use
the frozen prototype revision.

##### Invariants and their enforcement

Every new enforcement below is **proposed**; existing types alone do not yet
prove sparse lifetime correctness. The reviewer checks the state machine
before implementation, and the implementation gets the GPU-lifetime review
that [the hazards](../development/hazards.md#silent-invariants) requires.

| Invariant | One enforcing place | Where the guarantee stops |
|---|---|---|
| No write touches an unleased page and no mesh reader sees stale or partial contents | Destination-issued writable-page leases on every write, run clipping at unleased pages, and generation checks at both `CompleteLanding` and `MeshStands` | Native shader address bugs still require device validation and readback witnesses; strict non-residency is not a repair |
| Growth maps new pages without moving existing words | Range-backed destination's stable resource and page table; its grow path has no carry | Logical arena relocation remains an explicit, counted operation |
| A page is held for the union of live, landing and retiring ranges | Destination-owned coverage and generation records; pool placements stay private | A future queue or reader must join the lease contract, not borrow the handle |
| No physical reuse precedes last use and unmap completion | Device-wide queue checkpoint and mapping transaction alone produce the release proof consumed by `GpuRetirementQueue` and the pool's ticketed return; trim polls independently of uploads | Device loss tears down the generation; it never manufactures completion |
| Every physical byte is admitted once and released once | `GpuMemoryGrant`, `GpuDeviceMemoryWork`, pool allocation/free callbacks and one-use placement plans | Driver-private overhead is read from API usage, not inferred from mappings |
| Reservations price physical backing and cannot spend another region's credit | Region-specific account owner plus `SdfResidencyGrant` consuming the backing plan | Budget changes may leave old allocations over limit; the counted floor exception remains |
| Binding never passes queued readers or hidden frame batches | Existing backend queue door extended with the mapping transaction and explicit Vulkan semaphore edges | Merely possessing a native queue handle cannot be an alternate submission API |
| Pressure release makes no new device allocation | Pool trim and victim relocation use only existing holes and preallocated workspace | Insufficient holes/workspace is reported, not claimed as relief |
| Sparse and contiguous paths have one mesh store and publication rule | `GpuRegion` destination selection under `SdfMeshRegionArena` | Sharing mesh identities across residencies is outside this step |

##### Implementation sequence and open readings

**Piece 1: contiguous fallback and retirement foundation.** The store, arena,
growth/carry and shader word door remain single. The unused 64-view slab
array is gone from each World/mesh/probe set and from the generated interfaces.
Vulkan no longer requires or enables storage-buffer array indexing for those
reads. `GpuQueueTimeline` has one native completion object per device
generation: `DirectXQueueTimeline` signals an `ID3D12Fence` after the queue
door flushes its batch, and `VulkanQueueTimeline` uses the existing timeline
submit door. Vulkan explicitly enables the feature already required by its
[graphics API floor](https://docs.vulkan.org/spec/latest/chapters/features.html#features-requirements).

`GpuRetirementQueue.Hold` keeps pending carries and the irradiance feedback
copy source; `Poll` checkpoints eligible objects together, then releases only
the completed prefix. `SdfWorldTables.ServiceMemoryTrim` polls even without
a trim request or render, and `SubmitUpload` polls after submission. A pending
carry genuinely still needs its source and is not promised immediate relief.
Failed signals retain all objects and burn their values; a retirement queue
refuses a different timeline instance. `GpuQueueTimelineLawTests` exercises
hidden-owner retirement, a carry beside an independently releasable object,
signal failure and device-generation replacement. Native ordering and the
actual debug-world release remain windowed verification requests.

**Piece 2: destination write proof.** `GpuBufferPages` owns writable coverage
and generation stamps. Only it issues `Lease` values; revocation makes existing
leases stale before remapping can publish new coverage. `GpuRegion.StageOwed`
intersects the coalesced upload runs with writable pages without joining the
result again, sizes its request table to those exact runs, and validates their
leases at recording. The region's address ceiling reserves room for the
worst-case split table at the minimum supported mapping page, 4 KiB.
An ordinary contiguous growth preserves proofs through its ordered carry;
shrink and disposal revoke them. No native sparse destination is selected yet.

Landing retains the whole mesh's lease from its first slice. A slice is counted
only if it was recorded and that generation still stands at completion;
`MeshStands` checks the same retained proof before a row can become live.
A moved mesh's synchronous write requires a lease too; without one it is owed
and its rows are staged absent until background landing completes. The
device-free page laws cover holes inside joined and paired runs, request tables
larger than the original run limit, revocation between staging and recording,
and remapping without reviving a stale proof. The mesh-arrival laws cover
generation changes across a job and the moved-mesh path.

Background resource retirement uses the same device timeline as frame readers.
`SdfGpuWork.Enter` requires the actual frame queue before admitting a job;
counting wrappers do not define a second queue. A checkpoint after retirement
covers all preceding jobs without an extra per-job retirement counter or signal.
The job fences remain responsible for wakeups, readbacks and ring-slot rewrites.

**Pieces still to build, in dependency order.** Add native reserved/sparse
resources and mapping queues, page-only
residency admission with pool-owned slack, sparse arena placement policy, and
unmap/empty-block trim plus physical compaction. These remain step 7 work;
this piece claims neither a sparse path chosen nor a budget-drop physical
release. Both backends must stay bootable at each piece, and the final hardware
check below still gates any claim that sparse relief works.

**Design review disposition (sm7b, e9c1d485c9).** These are design closures;
the implementation status below distinguishes them from enforced code.

| Finding | Answer and enforcement to build |
|---|---|
| H1 | Accepted. Destination-only writable-page leases carry device/page generations through every write, clipping, slice completion and `MeshStands`. Retirement invalidates the generation before any address can be reused. The whole-mesh publication door alone was insufficient. |
| M1 | Accepted. One device queue timeline replaces residency upload counts as retirement proof; trim polls it for hidden residencies too. A carry not yet submitted remains an explicit pending use. |
| M2 | Accepted. Region promises cover pages only; native block admission consumes that page credit and admits net pool slack separately. Two regions sharing one block cannot each reserve its slack. |
| M3 | Accepted. A moved mesh without writable pages is owed and staged absent. No synchronous moved-mesh upload bypasses the lease. |
| M4 | Accepted. Vulkan uses its enabled timeline facility and existing submit door, with ordered signals across graphics and binding queues. Binary semaphore recycling is removed from the design. |
| M5 | Accepted. Sparse placement packs only for an unsatisfied contiguous range or address exhaustion, never free-word density. Page alignment for large meshes is a measured optional choice. |
| M6 | Accepted. Sealing forbids a reader between copy-out and copy-back; it does not unpublish rows. Copy-back and its barrier precede resumed readers. |
| L1 | Accepted. D3D12 requires tier 2; tier 1 NULL-mapping behavior is unsuitable. |
| L2 | Accepted as a qualification requirement, not a guessed constant. The hardware lane reads API usage for N empty and N fully mapped regions at several N and extents, subtracts tracked blocks, and derives a conservative per-virtual-page overhead reservation in the workspace class before qualifying sparse. Driver overhead remains observed, not a claimed exact admission bound; changing budget readings can still refuse a map. Until measured, no hardware row claims this coverage. |
| L3 | Accepted. The first accessing submission waits at transfer or compute, as appropriate, instead of a global all-commands wait for every map. The quiet window measures binding latency and queue stalls. |
| L4 | Accepted. No page clear without a named reader. Upload covers every byte a published mesh reads; diagnostics follow the same leased ranges. |
| L5 | Accepted. GPU execution dependencies order readers before unbind. The host observes completion only for ticket return, cross-resource reuse or native free; it does not wait before queuing unbind. |

**Unverified source claims have owners.** The backend implementer checks
`UpdateTileMappings` ordering against execute and enhanced barriers with a
recording-driver witness and native validation, not inference from a raw queue
call. The reserved-resource tiling query verifies the 64 KiB page on hardware.
Vulkan's device-wide sparse-address allowance is tracked across resources;
a separate binding queue never accesses buffer contents, so graphics remains
their access family. The separate-family witness and validation check that
assumption. No remap relies on data inheritance: physical packing explicitly
copies bytes back. The lead's Linux window measures RADV bind host cost.
vkd3d-proton remains independent corroboration, never the specification.

The sm7b follow-on implementation lane owns the first five rows after review;
the lead owns assigning and running the hardware windows. None ran in this
design lane. Each check reads actual work rather than a saved output baseline.

| Step | Mechanical check and decision |
|---|---|
| Enable and select backing | Report supported versus enabled features, actual binding family/queue, native page size, virtual extent, buffer/view limits and allocation-count limit. Force capability absence and confirm the same region uses contiguous backing. Boot the debug world on both backends. |
| Add physical plans and admission | Exercise two residencies sharing a block, overlapping page coverage, cancellation and a new-block boundary under a falling budget. Reconcile held plus promised with the account, mapped plus slack with blocks, and show no reservation credit is spent twice. Refusal preserves the prior complete tier. |
| Map and grow | Land payloads crossing page boundaries with holes between them; read back known words on both sides after growth. Resource identity and old offsets stay fixed, carried bytes are zero, initialized/uploaded bytes cover only the admitted ranges/pages. Exercise a draw-prefix growth too. |
| Retire and unmap | Delay a real reader, leave a frame batch deferred, cancel a landing, then evict/shrink and demand the same address again. Native call tracing must show last-reader completion before unmap and unmap completion before return/free/reuse. Include failed submission and device loss. Use device-free scheduling witnesses for interleavings a World cannot reliably force. |
| Trim and compact | Force a budget drop with wholly empty pages, then fragmented physical blocks. Show page counts fall before or with block counts, held bytes fall only on native free, kept empty blocks reach zero under pressure, and no device allocation occurs during relief. Page copy/readback preserves words at stable virtual addresses; scratch and moved bytes are counted. Insufficient workspace and live-page tails must print their obstruction. |
| Qualify on the debug world | Lead: hero spawn, meadow and the seam walk on Direct3D 12 and Vulkan, warm shared mesh store; read `host.memory`, `world.residency`, `world.counters gpu`, `world.gpu-timing`, and `world.rows check on`, then lower and restore the pool budget. Captures must retain complete floors without holes, and held bytes must fall after the fences. Quiet before/after timing prices mapping, initialization, physical packing and ordinary frame reads separately. |

Open readings remain owned, rather than deferred to backend guesswork:

- **Lead's prototype window:** finish the Vulkan indexed-read pair and meadow
  stop, with the prototype's matching-work checks. They complete the evidence;
  the Direct3D 12 result already selects the sparse direction.
- **Step 7 hardware lanes:** read the 2060's two backends and boot the Deck
  under SteamOS. Confirm page alignment, address/view limits, queues and
  real budget-drop release. Driver-source support is not a device qualification.
- **Step 7 allocator lane and lead's quiet window:** measure block slack,
  partly live page bytes, allocation count, map batches and scratch-copy work
  on the debug world with several residencies. Decide whether the existing
  physical-block size needs changing, using the same pool implementation;
  do not change the native page size or add shader indirection to tune it.
- **Step 7 backend implementer and lifetime reviewer:** verify the enhanced
  barrier spelling for physical-page reuse and exercise the separate sparse
  queue case. Recording-driver witnesses cover the latter when the available
  device has one combined queue; hardware validation remains labelled as such.

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
held bytes fall. A relocation, which is rare and is read from the landing
line's `relocations` count and its reason, moves no byte across the bus:
`owed` does not rise by the kept meshes' words, and their draws stay in the
frame until the copy completes. Captures from every side of each stop show no
missing
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
store's restores. First, the `[sdf.mesh-bake] start` line of a garden
crossing, with its `baking=`, `loading=` and `shared=` counts, says why so
many groups move there, before the library changes. The physical native row at a settled stop falls against
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
traced tier, at the gallery's lit exhibits and a thin partition, with the
indirect light compared against the full-detail scene's before and after, the
scene's bytes on both sides, and the boot's wait for the meter. After a tour,
`probe-cache-blocks` stays near the kept probes' own count. GPU
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
| Sparse binding, page alignment, actual binding queues, address/view limits and physical release on the three bar devices | Step 7 hardware lanes: the 4070 support reading is taken; qualify the implementation on both backends, read the 2060, and verify the Deck's driver-source column on hardware ([design](#sparse-backing-design-for-review)) | Sparse-path availability and its automatic contiguous fallback; priority remains optional |
| The indexed read's remaining Vulkan and meadow pairs | Lead's quiet window ([the measurement](#the-prototype-as-built)) | Completes the prototype evidence; the material Direct3D 12 result already selects sparse backing |
| Sparse block slack, partly live pages, map/compaction cost and enhanced-barrier reuse spelling | Step 7 allocator/backend implementers, lifetime reviewer and lead's quiet window ([design](#sparse-backing-design-for-review)) | Physical-block size and qualification of the pressure transaction, without changing the direct shader read |
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
  owner's promise is the handoff's arithmetic, and a device reservation is the
  same arithmetic over the bytes the ledger names.
- **The ledger.** `GpuDeviceMemoryWork` is the device's measured truth, the
  account's feed and the grant's one door, not a parallel count.
- **The backend pool.** `GpuMemoryPool` is where blocks are made, kept and
  freed; pressure release extends it.
- **The region and retirement.** `GpuRegion`, `SdfMeshRegionArena`,
  `SdfWorldTables.Retire` and `GpuRetirementQueue` own staging, placement and
  completion. Step 7 adds range-backed destinations and binding completion to
  those mechanisms; it does not fork the mesh store or the shader read.
- **The queue door.** `GpuSubmissionBatch`,
  `DirectXDeviceContext.CommandQueueHandle` and
  `VulkanLogicalDevice.GraphicsQueue` own submission ordering. Sparse native
  operations extend that door, with explicit bind dependencies.
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

Only what a decision rests on. API specifications define their native
contracts; implementations and guidance supply independent second opinions,
never Puck's design. Step 7's native API citations are beside the rules they
support in [the sparse design](#sparse-backing-design-for-review).

- D3D12 memory pool: [D3D12_MEMORY_POOL](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_memory_pool),
  which states that on a unified adapter the system pool and the local segment
  group are the same memory, and which pool a heap's memory comes from.
- Microsoft residency: [Residency](https://learn.microsoft.com/en-us/windows/win32/direct3d12/residency),
  for the budget's behaviour, the reservation, one pool against two, watching
  for negative changes about once a frame, and destroying heaps as the reliable
  release; with [QueryVideoMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo),
  [its fields](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/ns-dxgi1_4-dxgi_query_video_memory_info)
  and [the change notification](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-registervideomemorybudgetchangenotificationevent).
- Heap type to memory pool: [GetCustomHeapProperties](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-getcustomheapproperties(uint_d3d12_heap_type)),
  whose tables put `DEFAULT` in L1 and `UPLOAD` and `READBACK` in L0 on a
  discrete adapter and every heap type in L0 on a unified one.
- D3D12MA: [the allocator's budget](https://gpuopen-librariesandsdks.github.io/D3D12MemoryAllocator/html/class_d3_d12_m_a_1_1_allocator.html)
  and [its source](https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/D3D12MemoryAllocator/master/src/D3D12MemAlloc.cpp):
  `HeapPropertiesToMemorySegmentGroup` (local on a unified adapter, L1 local
  and L0 non-local, and for an unknown pool `DEFAULT` or `GPU_UPLOAD` local),
  `CurrentBudgetData::UpdateBudget` querying both groups with no unified-memory
  check, the non-local capacity zero on an integrated adapter, and usage
  corrected between fetches by the blocks allocated since.
- DXVK: [its DXGI adapter](https://raw.githubusercontent.com/doitsujin/dxvk/master/src/dxgi/dxgi_adapter.cpp),
  for the non-local group answered with the local heaps on a unified adapter
  and for the budget event set at registration, as Windows does.
- Vulkan memory budget: [VkPhysicalDeviceMemoryBudgetPropertiesEXT](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceMemoryBudgetPropertiesEXT.html),
  for a budget and usage per heap, each budget non-zero and at most its heap's
  size, zeros past the heap count, and values that change with activity outside
  the implementation; structure type values from
  [vulkan_core.h](https://raw.githubusercontent.com/KhronosGroup/Vulkan-Headers/main/include/vulkan/vulkan_core.h).
- VMA: [staying within budget](https://gpuopen-librariesandsdks.github.io/VulkanMemoryAllocator/html/staying_within_budget.html)
  and [its source](https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator/master/include/vk_mem_alloc.h):
  `UpdateVulkanBudget` clamps a budget past the heap size and replaces a zero
  one with 80% of the heap, and without the extension the budget is 80% of
  the heap.
- RADV: Mesa's RADV physical-device source, read through
  [a mirror](https://raw.githubusercontent.com/chaotic-cx/mesa-mirror/main/src/amd/vulkan/radv_physical_device.c),
  for the two heaps an APU reports (two thirds and one third of its memory) and
  how their budgets split the free memory the same way.
- RADV backing: the same [physical-device source](https://raw.githubusercontent.com/chaotic-cx/mesa-mirror/main/src/amd/vulkan/radv_physical_device.c)
  for the sparse features and properties (each `enable_sparse`, from
  `radv_sparse_enabled`), `sparseAddressSpaceSize`, the storage-buffer offset
  alignment of 4, the storage-buffer array indexing features,
  `EXT_memory_priority`, the queue families' `VK_QUEUE_SPARSE_BINDING_BIT`, and
  no `EXT_pageable_device_local_memory` in its extension table; with
  [ac_gpu_info](https://raw.githubusercontent.com/chaotic-cx/mesa-mirror/main/src/amd/common/ac_gpu_info.c)
  for `has_sparse` (GFX7 and above, less four older APUs) and
  [Mesa issue 9224](https://gitlab.freedesktop.org/mesa/mesa/-/issues/9224),
  which asks RADV to implement the pageable extension.
- NVIDIA 535: [the 535.43.02 driver notes](https://forums.developer.nvidia.com/t/254959),
  which add `VK_EXT_memory_priority` and the pageable device memory extension
  for Turing and newer GPUs.
- vkd3d-proton: [its device source](https://raw.githubusercontent.com/HansKristian-Work/vkd3d-proton/master/libs/vkd3d/device.c),
  for `d3d12_device_determine_tiled_resources_tier` (the tier from Vulkan's
  sparse features and queue) and the fixed 40
  `MaxGPUVirtualAddressBitsPerResource`.
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
