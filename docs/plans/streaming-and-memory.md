# Streaming and memory

Residency is how Puck exists at the scale it aims for: when every zone,
character and server is a world, a player's machine holds what that player can
observe and little else. [Residency follows what is observed](../decisions/residency.md)
settled the design; this plan builds it, with the memory work inside it rather
than beside it, and states how the frame, bake and memory bars are read so
that quality and performance stop pulling against each other.

The owner's direction: streaming has not had "the proper focus/priority"; memory
usage and handling "could be SIGNIFICANTLY improved if we simply tried"; the
bars are "a good forcing function, but they end up hurting us sometimes too".
The owner agreed to this plan's shape: a research round, then streaming's
remaining steps with the memory work folded in. The research round is done, and
this page is its synthesis: [where residency stands](#where-residency-stands),
[the decisions](#the-decisions) the build rests on, and
[the build order](#the-build-order). An adversarial review from another family
reads this page before the first build lane starts.

## How the bars are read

Most of the conflict between the quality and performance bars has been
measurement, not trade-off: a comparison of two kinds of run, a machine loaded
by other lanes, a display that had turned off and dropped the GPU to idle
clocks. So:

- **One recipe for both bars.** The same build state, a quiet machine
  (`puck lease quiet`), the display on and composing, the same camera path,
  and the frame, bake and memory readings taken in the same window. A quality
  change and its cost are judged together, like with like.
- **A regression is a design problem, never a reason to drop quality.** When a
  look costs too much, the cost moves to where a viewer looks: detail demanded
  by what a view resolves ([bake demand tiers](bake-demand-tiers.md)), not
  detail removed.
- **Budgets per system.** Each place and system owns a share of the frame, the
  bake and memory, read through `world.budget` and `host.memory`. A change that
  needs more finds it within its system or makes its case; a regression has an
  owner instead of blocking every lane.
- **Gates where readings are stable, trends where they drift.** Frame time at
  fixed stops and bytes per entity are stable enough to gate on. The
  whole-world cold bake and peak memory drift with content and machine state;
  they are watched as trends, and the gated form of each is per unit of work
  (bake time per million samples, resident bytes against the budget).

## Where residency stands

Each line below was read in the source for this page; the research round's
reports were leads, and where one disagreed with the code the code won. A
symbol is cited by name so `puck docs links` keeps it honest.

The decision's order of work, as built:

| Step | State |
|---|---|
| Measure | Partial. `world.residency` reads time until near groups land, resident triangles by tier, the region's held, named and limit bytes, the probe cache's bytes and the trace scene's CPU arrays. No device budget is read: `IGpuDeviceContext.MemoryProfile` is a size fixed when the device is created, and nothing in `src/` calls DXGI's `QueryVideoMemoryInfo` or enables `VK_EXT_memory_budget`. |
| Near-first ordering | Built: `SdfMeshDemand.Priority`, and detail bounded by what each view resolves (`SdfMeshTier`). A demand point is a view's eye with no lead along its velocity. |
| A selector across residencies | Not built. Each residency owns its own `SdfWorldResidency.MeshDemand`, its own bake budget (`SdfMeshPathBake.Retier`), its own mesh region and its own background GPU budget. Nothing unites them. |
| Lighting on the same demand points | Partly built. Each level of the probe window already centres on the box of every view's camera when it fits (`SdfMeshIrradiance.WindowLow`), and probe urgency reads every view. What remains: the window is anchored on the first view (`SdfMeshIrradiance.WindowCamera`), the lighting derives its own view list (`SdfMeshIrradiance.CaptureViewCameras`) instead of reading the residency's demand, the trace scene is built from every static draw of the frame (`SdfMeshIrradiance.StaticKey`), and `SdfMeshIrradiance.ProbeCache` is bounded by window cells, not bytes. |
| Demand mapped through portal faces | The mapping is built; its use is not. A fitted window's view is already the viewer's eye taken through the glass, measured in the display pixels the glass spans and held to its frustum (`SdfViewSnapshot.DemandHeight`, `SdfViewSnapshot.DemandWithinView`), and a routed scene's frame carries every attached window's view (`WorldRoutedScene.Dress`). What is missing is arrival without a render: a residency's upload is submitted only for a scheduled render (`SdfWorldPasses.PrepareRender`), so an unseen window still renders at the least share (`WorldViewGraphHost.ReserveArrivals`, `WorldPortalVisibility.ArrivalShare`). |
| Eviction under a byte budget | Not built. Under pressure a group is planned again at a coarser tier or every demand is coarsened half a rung (`SdfMeshTier.Biased`); no group ever leaves. A mesh the arena cannot place is refused and its draws leave the frame (`SdfMeshRegionArena.Refused`), which shows as a hole. A growth the device refuses still ends the World (`GpuRegion.Grow` from `SdfWorldTables.EnsureMeshRegionCapacity`). |
| Authored areas | Not built. |
| Terrain tiles, then the moons | Not built: a bake group is an instance, a slot or a union, so one large instance has one tier and one demand box. |

What the memory side holds today:

- **The account is a host-memory admission promise.** `MemoryAccount.DefaultBudget`
  is three quarters of installed memory. Its owners are mesh payloads, bake
  workspaces and the store's transient decode and entry scratch. Only the bake
  reserves (`MemoryAccount.Owner.TryReserve`); a payload's charge never refuses
  (`MemoryAccount.Owner.Change`), and one job is admitted whatever the budget
  holds (`MemoryAccount.Owner.Reserve`). It does not see device memory, the
  managed heap, `GpuRegion`'s host copies or staging.
- **Device bytes are counted and unbudgeted.** `GpuDeviceMemoryWork` counts every
  allocation both backends make, by role, at the driver's size. Nothing admits
  against it.
- **Two holders each take a quarter of the device, per instance.**
  `SdfWorldTables.MeshRegionLimitOf` gives every residency's mesh region a
  quarter of `GpuMemoryProfile.DeviceLocalBytes`, and
  `ShaderPipelineMemoryBudget.For` gives every pipeline instance another. On a
  Direct3D 12 unified-memory adapter that figure is dedicated plus shared system
  memory (`GpuMemoryProfile.FromDirectX`): the same memory the account takes
  three quarters of.
- **Background GPU work multiplies the same way.** Each residency's tables own an
  `SdfGpuWork` with its own `SdfGpuWork.GpuShare` of GPU time and its own
  `SdfGpuWork.UploadBytesPerSecond`.
- **Nothing reacts to pressure it reads.** `MemoryAccount.TrimCaches` runs only
  when `host.memory budget` lowers the budget or `host.memory collect` asks.

A reading at the meadow on the lead machine, in the commit that landed this
page, agrees with the source. The account sees less than a tenth of the
process's private bytes. Four residencies each carry a quarter-of-device region
limit, so the limits sum past the device; only the main world's region is
large there, the nested worlds naming a few megabytes to a few tens each, so
the hazard is a hub of large worlds and not yet a stop of the debug world.
Device-local bytes allocated in the first minutes are close to three times the
bytes held, and the peak stands above the held bytes by about the main
region's size, which fits a region that grows by allocating a larger buffer
and copying into it
**[inference: no counter attributes device allocations by owner yet]**. The
managed heap is larger than the mesh payloads the account does see, most of it
on the large object heap.

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

Four reports fed this page, kept beside it with their cited sources:
[shipped MMO streaming](streaming-and-memory/mmo-streaming.md),
[GPU virtual geometry and texturing with feedback](streaming-and-memory/gpu-streaming.md),
[device budgets on the bar devices](streaming-and-memory/device-budgets.md), and
[a reading of Puck's own residency and memory code](streaming-and-memory/puck-today.md).
Each is a snapshot of the round; where one disagrees with this page or the
current source, this page and the source win. They disagreed in four places, and each
is settled where it bears on a decision:

- **How much of a budget to spend.** AMD advises 80% of total memory; Unreal's
  Direct3D 12 path takes 90% of the DXGI budget; a licensee calls the whole
  budget safe. [Decision 2](#decision-2-what-is-read-how-often-and-how-much-is-spent)
  takes a share of the reported budget, never of raw memory.
- **How often to read.** AMD says not every frame; its own allocators read
  every frame or every thirty operations; Microsoft says to watch roughly once
  a frame. The same decision reads on the host's change signal and corrects
  between readings from Puck's own ledger.
- **What the non-local group means on unified memory.** Native Direct3D 12
  reports one pool; a translation layer answers the non-local group with the
  local budget to satisfy another engine. The same decision never adds them.
- **Whether coarse coverage is always resident.** Nanite keeps a root page for
  everything; the two streaming reports warn against a floor for every world
  ever joined. [Decision 5](#decision-5-the-unit-is-a-tier-and-the-coarsest-tier-is-the-floor)
  keeps a floor per live residency within its far distance, and names what
  gives way when even that does not fit.

No shipped game the round read publishes a whole-client byte authority or its
eviction order, so the borrowed parts are shapes (coarse first, neighbours by
topology, keep-alive, disclosure apart from presentation), and the authority is
Puck's own design.

## The invariant

Every byte a presentation holds is charged once to one account, in the pool of
the physical memory it occupies, and what the account admits never passes that
pool's spend limit. A miss changes only what is shown, and is counted.

What enforces it today: nothing, for device bytes. What will: the allocation
doors both backends already route every counted allocation through (the callers
of `GpuDeviceMemoryWork.CountAllocated`, whose `GpuDeviceMemoryWork.IsDeviceLocal`
is the one rule for which row a role counts in) feed the account, so a device
byte cannot exist uncharged; an elastic owner grows only through
`MemoryAccount.Owner.TryReserve`. Where the guarantee stops: the managed heap,
driver memory, swapchain images and other processes are measured and never
admitted, and `MemoryAccount.Owner.Reserve` admits one job past the budget so
a bake always settles. Until [step 3](#the-build-order) lands, the account
reports the invariant and does not hold it; no step may claim otherwise.

The second invariant is the residency decision's and is unchanged: only
presentation follows the observer, so nothing here reaches a rule, a door or a
state hash.

## The decisions

Each decision names its options, the evidence, what the favoured one closes and
leaves open, and the strongest case against it. **[opened]** marks code this
page read; **[reading]** a measurement whose numbers are in the landing commit;
**[external]** a published source, linked under [sources](#sources);
**[inference]** this page's reasoning.

### Decision 1: one account, with pools by physical memory

**The question.** What "one byte authority" means when a discrete card has two
physical memories and a handheld has one.

| Option | Closes | Fails at |
|---|---|---|
| A. Keep separate budgets and divide the quarter among residencies | The limits summing past the device | Every new holder needs its own share; unified memory is still counted twice |
| B. A device budget authority beside `MemoryAccount`, as a GPU allocator library keeps | Device admission | Two authorities; on unified memory both promise the same bytes |
| C. One account whose owners charge a pool: host memory and device memory on a discrete card, one pool on unified memory | All of the above | The account must learn pools, and every device allocation must reach it |

**Evidence.** The residency decision already says one reservation "from the job
system's single physical byte-accounting owner", and the
[job system memo](engine-job-system.md#4-memory-ownership) names `MemoryAccount`
as that owner. `GpuRegion` sits in a project below the account's, so it can
report only through `IAllocationObserver`, which `MemoryAccount.Owner`
implements **[opened]**; the backends reference only the abstractions, so the
ledger feeds the account through the same interface, wired by the host. On a
unified adapter Direct3D 12's local group and its system pool are one memory
**[external: D3D12 memory pool]**, and RADV on an APU reports two heaps that
split one memory two thirds to one third **[external: RADV]**.

**Favoured: C.** `GpuMemoryProfile.UnifiedMemory` chooses the layout. On a
discrete card, device-local allocations charge the device pool, and
host-visible ones (staging, upload, readback) charge the host pool, because
that is the memory they occupy. On unified memory every owner charges the one
pool. The managed heap and native allocations always charge host memory.

**Closes.** The double count on unified memory; the two unrelated quarters; the
account seeing a tenth of the process. **Leaves open.** A pool's budget is only
as good as its reading ([decision 2](#decision-2-what-is-read-how-often-and-how-much-is-spent)).
**Strongest case against.** `MemoryAccount.Owner.Change` never refuses, so a
pool is a ledger until owners reserve; the answer is that
[decision 3](#decision-3-one-reservation-across-residencies-with-floors-and-classes)
makes every elastic holder reserve, and the fixed ones are charged at their
real size so the elastic ones shrink around them.

### Decision 2: what is read, how often, and how much is spent

**The question.** Where a pool's budget comes from on each backend, how the
reading stays current without a driver call inside an allocation, and what
share of it Puck admits.

**The readings.**

- **Direct3D 12.** `IDXGIAdapter3::QueryVideoMemoryInfo` gives a budget and the
  process's usage per segment group. On a discrete adapter the local group is
  the device pool and the non-local group is the system memory staging lives
  in; on a unified adapter only the local group is read, and the two are never
  added **[external: D3D12 memory pool, D3D12MA, DXVK]**. The adapter is held
  for the device's life; today `DirectXNativeDeviceApi.ReadAdapter` opens a
  factory and an `IDXGIAdapter1` for each read **[opened]**.
- **Vulkan.** `VK_EXT_memory_budget` gives a budget and usage per heap, enabled
  when the device lists it (`VulkanLogicalDeviceFactory` enables no such
  extension today **[opened]**). Heaps fold into pools by the rule
  `GpuMemoryProfile` already uses for device-local heaps; on unified memory
  every heap folds into the one pool, because RADV's split is not physical.
  Without the extension the heap sizes stand in and the reading says it is an
  estimate.
- **Host.** `HostMemory.AvailablePhysicalBytes`, the committed managed heap and
  the native allocator's committed bytes, as `host.memory` already prints them.
  On unified memory the one pool's budget is the smaller of the device's
  reported budget and what the host has free plus what Puck already holds.

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
first reading. On Vulkan the frame
thread refreshes at a low fixed rate and at once after any refused allocation
or reservation. Between refreshes, usage is the last reading plus the ledger's
change since, so admission never calls the driver. No thread spins or polls.

**How much.** A share of total memory is the wrong base: the DXGI budget is
already what the OS grants this process net of the others, and it moves when
the player switches away. The spend limit is the reported budget less the
larger of a tenth of it and the account's headroom. A tenth is a starting
value in one place; [step 2](#the-build-order)'s fixed-stop readings with the
budget-change count move it. The floors
([decision 3](#decision-3-one-reservation-across-residencies-with-floors-and-classes))
are passed to `SetVideoMemoryReservation` as the working set Puck cannot go
without **[external: Microsoft residency]**. On Linux no memory manager absorbs
an overcommit, so the same limit is a hard ceiling there and reserving before
allocating is mandatory **[external: NVIDIA Vulkan guidance; inference for the
Deck]**.

**Closes.** A budget that ignores other programs and the player switching away;
an `ID3D12Device` or `VkDevice` refusal as the first sign of pressure.
**Leaves open.** The Deck's real numbers and whether one margin fits both
backends ([open questions](#open-questions-and-their-owners)).

### Decision 3: one reservation across residencies, with floors and classes

**The question.** How the residencies of a presentation, the view targets, the
shader pipeline and the lighting share a pool, instead of each taking a
fraction nobody granted.

| Option | Closes | Fails at |
|---|---|---|
| A. Divide a fixed share by the count of live residencies | Limits past the device | A painting's quarter-scale room and the occupied valley get equal shares |
| B. Each holder reserves from the account by class; the elastic class is one reservation the selector splits by demand | The above, and it scales with any count of worlds | Needs the selector the decision names |
| C. One mesh region for the whole presentation | Sharing by construction, and one copy of a mesh several worlds draw | Needs [decision 8](#decision-8-pages-are-three-things-and-only-one-changes)'s backing first: one region grown by copy doubles everything at once |

**Favoured: B now, with C as the direction.** Holders, in admission order:

1. **Fixed by the views.** View targets, cascades, screens, program tables and
   an installed shader pipeline graph are charged at their real size. The
   pipeline's replacement peak (`ShaderPipelineMemoryAccount.PeakBytes`)
   becomes a reservation against the device pool; a refused one keeps the
   installed graph running, as `ShaderPipelineMemoryAccount.RefusalCode` does
   today.
2. **Bounded transfer.** Staging, upload and readback hold a reservation sized
   by the landing rate, and one budget of GPU time and upload bytes serves the
   presentation, shared by priority, in place of a quarter of the GPU per
   residency.
3. **Residency floors.** What each live residency cannot draw without: its draw
   records and the coarsest tier of each group within its far distance
   ([decision 5](#decision-5-the-unit-is-a-tier-and-the-coarsest-tier-is-the-floor)).
4. **Elastic detail.** One reservation for mesh detail, the lighting's trace
   scene and the probe cache across every residency: what the pool's spend
   limit leaves after the classes above and the measured rows.
5. **Caches.** The `Cached` category, in bytes, trimmed first.
6. **Measured, never admitted.** The managed heap, driver and swapchain memory.
   They are rows, so the elastic class shrinks when they grow. No hard limit on
   the managed heap: it would turn pressure into an exception where everything
   else turns it into a coarser picture.

**How the elastic class is split.** Not by fractions. Demand is already
measured in display pixels of the one viewer (a window's demand is the pixels
its glass spans), so one detail bias serves the whole presentation: the
selector sums what every residency's draws would name at the current bias and
raises or lowers one bias to hold the sum under the reservation. A residency's
region limit becomes what its own draws name at that bias plus room for a
replacement, in place of a quarter of the device; `SdfMeshTier.BudgetEighths`
and the bias logic in `SdfMeshPathBake.Retier` keep working, reading the
selector's grant where they read one residency's limit. The error an
over-budget view shows is then the same number of pixels in every world it
sees, which is the fair split and needs no tuning. A growth reserves the old
and new buffer together before it allocates, and a device refusal lowers the
pool's budget to what is held instead of throwing.

**Evidence.** Each residency reads its own limit and budgets alone, and its
budget is unbounded until the first limit is read
(the bake's region budget, read in `SdfMeshPathBake.ObserveResidency`)
**[opened]**. The quarter was sized so one region and the copy its growth holds
fit beside everything else ([the region's budget](bake-demand-tiers.md#the-regions-budget));
four residencies each holding that limit void the sizing as soon as a second
one is large **[reading: the limits; only one region is large today]**.
Shipped practice splits the same way: a floor that must be resident and an
elastic pool that streams **[external: Microsoft residency]**.

**Closes.** Limits summing past the device; a quarter of the GPU per residency;
the refused growth that ends the World; the unbounded first plan.
**Leaves open.** The floors of many worlds can themselves pass a small pool;
decision 5 says what gives way. **Strongest case against.** One bias punishes
the occupied world for a hungry window. It does not, because a window's demand
is measured by the display pixels it covers: a small or unseen window names
few bytes at any bias.

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
at `WorldPortalVisibility.ArrivalShare`. The mapped eye is not missing: an
attached window's fitted view is in the frame whether or not it renders
**[opened]**.

| Option | Fails at |
|---|---|
| A. Keep rendering hidden windows at the least share | A render per arriving world per frame; a world behind a rate-held camera view never arrives; the search and its three share cases to maintain |
| B. Stage every held residency that still owes work, whether or not a render reads it | Each residency's own GPU budget, until decision 3 shares it |
| C. A second pump outside the render graph | A second owner of uploads and fences |

**Favoured: B.** Staging moves from "a render asked" to "the residency owes
work": the package stages each held residency whose meshes are arriving, after
it prepares them. `SdfWorldResidency.MeshesArriving` loses its "only frames
that render one of its views bring them" clause, and the arrival search, the
least-share cases and the arriving-world query are deleted. Two things change
with it. An unseen window contributes no demand point, so its world holds the
floor and nothing finer (a residency with no demand point already demands the
coarsest tier of every group); detail arrives as the glass comes into view and
its shown height grows. And `world.wait residency` reads every held residency,
where `SdfWorldPasses.PendingMeshesIn` reads only what the latest schedule
displays. A residency no render reads is pumped under the budget and never
driven at the GPU's pace (`SdfGpuWork.Drive` is for a frame the graph
withholds), so a world nobody sees cannot hold a frame.

**Closes.** The stand-in the decision's step 4 names; the one extra view; the
world behind a camera view that never arrives; `unbound` pumps. **Leaves
open.** Lookahead beyond attached windows (a door two rooms away) is authored
areas' and the velocity lead's, in [the build order](#the-build-order).
**Strongest case against.** An upload for a world nobody sees costs GPU time
the frame could use. It is bounded by the one shared background budget, it is
far less than a render, and it is the work that makes the crossing free.

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
coarsest tier is its own unrefined lattice (`SdfMeshTier.Coarsest`), a real
surface and not a proxy **[opened]**. Baked offline at its coarsest tier
(`puck mesh audit --world worlds/debug --cpu --tier 24`), the whole debug world
is about a twentieth of the triangles it has at its finest, and a few hundred
megabytes of region for every group of the world at once **[reading]**. That
bounds the floor within one stop's reach from above, and it decides the option
below: the floor is affordable.

| Option | Closes | Fails at |
|---|---|---|
| A. Coarsen only, as today | Nothing new | Relief needs memory first; a refusal is a hole; first arrival is a hole |
| B. The coarsest tier of each group in reach is a second, always-resident product; the demanded tier is detail above it | Eviction with no bake and no overlap; refusal and first arrival show the floor | The floor's bytes, measured small |
| C. Collapse levels inside one tier own compacted vertices, and fine levels drop | Finer steps | A new mesh layout in the codec, the region and every kernel, for a step a tier already is |
| D. A cluster hierarchy with mixed-detail cuts inside a group | The finest granularity | A new mesher contract and a seam proof across tiers; nothing in reach needs it yet |

**Favoured: B.** The residency decision's "coarse levels owning compacted
vertices" predates demand tiers; a tier already owns its vertices, so the
floor is a tier and option C is not built. Under it:

- A group in reach draws its floor until its demanded tier stands, and again
  the moment that tier is evicted or refused. Evicting is handing the draws
  back and forgetting the detail's words, in the selector's priority order,
  lowest first. Nothing is baked to free memory.
- The floor lands first, nearest first, so an arriving place is whole and
  coarse before it is sharp. This is the coarse-first shape the shipped games
  share **[external: EVE download on demand; Nanite deep dive]**.
- A group beyond the far distance draws nothing, as now, and reads as haze.
- When the floors themselves do not fit, two things give way, in this order,
  and each is counted: an unseen window's world drops to its draw records and
  its portal shows its authored fallback; then a residency's resident reach is
  drawn in from the far distance, farthest groups first, with the haze drawn
  in to the same distance. No group nearer than the haze is ever absent, which
  is what makes "never a hole" a property of the mechanism.
- A relocation copies each kept mesh within the device, as a growth already
  does, instead of landing every mesh again from the host
  (the open item in [residency and streaming](open-items.md#residency-and-streaming)).

**Closes.** The hole on refusal, on first arrival and on a replacement the
region has no room for (`SdfMeshRegionArena.AheadRefused`); relief that costs
memory; a floor that scales with worlds ever joined (it follows live
residencies and their reach). **Leaves open.** A single huge group still has
one tier ([decision 8](#decision-8-pages-are-three-things-and-only-one-changes)).
**Strongest case against.** Two resident meshes for a near group. The floor is
a small fraction of the detail it stands under, and it is what lets every other
byte be evictable.

### Decision 6: what a landed mesh keeps on the CPU

**The question.** Which CPU copies can go once the GPU copy is resident, and
what each release needs first.

**Evidence.** A store-published mesh already lets its normals and coarser
levels' indices go and restores them through one door (`SdfMesh.Hold`) when a
reader needs them. Positions, level-zero indices and triangle materials stay,
for four readers **[opened]**: the region holds a cached claim on every placed
mesh because it reads a mesh's words again for a relocation, a moved mesh and
another residency's landing; the lighting's trace scene claims every static
mesh and builds its CPU hierarchy from positions and level-zero indices; a
renumbered variant reads the materials; a device loss lands everything again.
The mesh library also forgets a mesh a residency still holds, so a later plan
decodes a second copy (`SdfMeshLibrary.TryTake` misses it). Every one of these
bytes is a cache: the store holds the same planes, and the field can derive
them again.

**Favoured: a landed mesh keeps nothing a reader cannot ask for through the
hold, and each reader is retired in the order its prerequisite lands.**

| Copy | Needs first | Then |
|---|---|---|
| Dynamic and glass meshes' positions and level-zero indices | Nothing: the trace scene leaves them out | Released through the existing door |
| The library's second copy | Nothing | A payload stays findable while any claim lives, and the library's weight charges cached-only payloads (`MemoryCategory.Cached`) |
| Static meshes' positions and level-zero indices | Decision 5's device-side relocation, so a pack reads no host words; the lighting tracing a tier sized to its probe spacing with placement on the GPU trace ([decision 9](#decision-9-lighting-reads-the-same-demand-and-pays-on-demand)) | Released; a device loss restores from the store as a warm boot does |
| The trace scene's CPU hierarchy | Probe placement and depth classification reading the GPU trace | Freed |
| The composed-document store | Confirming what a reload and a hot reload read: `WorldDefinitionFileSource.ForgetComposedDocuments` has no caller in `src/` | Released after boot where nothing reads it |
| Native allocator slack | [Step 2](#the-build-order) naming it | Collected when an arrival settles |

**Closes.** A second copy of most of what the region holds; the library's
double load. **Leaves open.** The managed heap is the larger owner and its
tenants are not yet named; step 2 names them before anything here claims the
Deck target. **Strongest case against.** Restores from disk on a turn. The
rule that prevents it: a release happens only when no reader on the frame's
path reads those words, and restores are counted (`world.residency`'s store
line), so a release that causes them shows.

### Decision 7: prediction decides arrival, and observation decides retention

**The question.** Whether GPU feedback replaces CPU-derived demand.

**Evidence.** Demand is computed on the CPU from planned boxes, which works
for a group that has never landed, and a root camera demands in every
direction and through walls, so a turn finds its meshes
([the region's budget](bake-demand-tiers.md#the-regions-budget)). That same
blindness is why the meadow settles half a rung coarse: detail nobody can see
holds bytes detail in view needs. The occlusion pass already classifies every
draw each frame (`SdfMeshOcclusion`); nothing reads it back per group
**[opened]**. Virtual geometry and virtual texturing both separate what is
wanted from what is resident and used, and both keep a coarse answer for a
miss **[external: Nanite deep dive; software virtual textures]**.

| Option | Fails at |
|---|---|
| A. CPU demand only | Hidden detail is kept while visible detail coarsens |
| B. The GPU emits the requests | A group that is not resident cannot report; a turn has no history; first arrival has none |
| C. The CPU predicts what to bring; the GPU reports what was drawn, and that orders what leaves | One readback, late by a few frames |

**Favoured: C.** Wanted detail stays what a view resolves, computed from
planned boxes, with the root camera's all-round demand kept for arrival. The
GPU adds the one fact only it has: a bit per group, set when any pass drew it
(views, cascades and glass alike, so a shadow caster out of view is still
used), written by the existing cull and read back through the background
work's completed slots. Under pressure, detail no pass has drawn for a grace
interval is spare before any visible detail is coarsened. With room to spare
nothing changes. A dropped or late batch means unknown, never unused. The
baseline is an ordinary storage buffer and a readback every bar device has;
hardware sampler feedback and sparse residency are not part of it.

**Closes.** The bias at the meadow where occlusion would free the bytes.
**Leaves open.** How a fast turn feels when hidden detail has left
([the owner's questions](#the-owners-questions)). **Strongest case against.**
Feedback is late. The retier's hold already absorbs a frame-late answer, and
the floor is what a late answer shows.

### Decision 8: pages are three things, and only one changes

**The question.** Whether pages replace whole-mesh landing.

"Page" names three different things in the systems the round read, and Puck's
answer differs for each.

- **Transport is already paged.** Landing uploads a mesh in bounded pieces
  under the upload budget and publishes it once every piece stands. Nothing
  changes.
- **Publication stays whole.** What a frame draws is always a complete mesh of
  one tier, which holds by construction
  ([what publishes](bake-demand-tiers.md#what-publishes-and-what-the-frame-draws)).
  Fixed-size pages below the mesh would put a page-table read in every vertex
  fetch and buy nothing, because Puck's units are complete surfaces already.
  The unit shrinks only where a group is too large to have one tier: a group's
  spatial chunks (`SdfMeshChunks`, with seams already locked within a tier)
  become the selector's units, an unchunked group being one chunk. That is the
  decision's terrain-tile step, and it needs a seam rule across tiers first.
- **Backing changes.** The region is one buffer that grows by allocating a
  larger one and copying, so its peak is the old and the new together, and one
  region addresses under four gibibytes (`GpuRegion.MaxByteCount`). Two
  backings remove the copy:

| Option | Gives | Costs |
|---|---|---|
| Sparse backing of the one buffer | No shader change; memory committed as the region fills | Reserved resources and sparse binding are capabilities, not guarantees **[external: Microsoft residency]**; the addressing ceiling stays |
| Slabs: fixed-size buffers, each mesh inside one, a draw naming its slab | No capability question; growth adds a slab and an emptied slab returns to the device; no ceiling; one store for every residency, so a mesh several worlds draw is resident once | Every kernel that reads mesh words indexes an array of buffers |

**Favoured.** Slabs shared by the presentation are the direction, because they
also make decision 3's option C true and returning a whole buffer is the
reliable way to give memory back **[external: Microsoft residency]**. The
choice between slabs and sparse backing is not made here: it rests on a
capability reading on the three bar devices and on the indexed read's cost in
the views stage, which a design page cannot measure. It is
[step 7](#the-build-order), a prototype with a GPU grant, and it is taken
before any slab code is written. Until then growth reserves its peak
(decision 3), so the copy is admitted, not hidden.

### Decision 9: lighting reads the same demand, and pays on demand

**The question.** What "lighting on the same demand points" still means, now
that the window already follows every view.

**Evidence.** The gaps are the trace scene's input, the byte bound and the
second demand derivation, not the window's shape **[opened]**. Every residency
also owns an irradiance volume (`SdfWorldResidency.MeshIrradiance`) and stages
it on every prepared frame, seen or not. The trace scene holds the
tier it traces, so a retier wave is paid again in the lighting
([the lighting follows the tier](bake-demand-tiers.md#the-lighting-follows-the-tier)).

**Favoured.** The lighting reads the selector's demand points instead of
deriving its own. It is a demand class of its own whose "pixel" is the probe
spacing, so the trace scene traces a tier whose error fits the spacing and
stops following every view-driven retier; whether that tier is the floor is
the step's first reading. The scene covers what the window reaches. The probe
cache, the scene's retained geometry and the arena's slack become `Cached`
owners in bytes under the account, in place of cells, triangles and words. A
residency's lighting is built when one of its views is first seen, so an
unseen window's world costs its floor and no volume. The cache reader's spin
(`Thread.SpinWait`) becomes stable publication or a host wait.

**Closes.** The second derivation; three retention currencies; a fixed
lighting cost per world at a hub with many portals. **Leaves open.** What the
lighting must keep beyond recent hits (blockers a ray passes) is the lighting
lane's to establish before observation evicts anything it traces.

### Read at the scale Puck aims for

The purpose asks what each answer becomes when every zone, character and
server is a world.

- **A hub with many portals.** Every attached window is a residency. Today each
  takes a quarter of the device, a quarter of the GPU's background time, a
  hidden render while it arrives, and a lighting volume. After decisions 3, 4,
  5 and 9 an unseen one costs its draw records and floor, lands without a
  render, and shares one budget. What remains is the fixed cost of a
  residency's tables, which step 2 measures; if that alone grows past the
  pool, it is a session and document question and gets its own design.
- **A crowd of characters.** Spatial eviction does not thin a crowd standing in
  one place. Disclosure and presentation stay apart: a recipient keeps every
  fact its grants allow, and the floor is what a character costs when its
  detail does not fit **[external: Guild Wars 2, the end of culling]**. What a
  character shows at its floor is an authored look
  ([the owner's questions](#the-owners-questions)).
- **Federation.** A cache is re-derivable only while its inputs are obtainable.
  Evicting GPU bytes keeps the store's entry; evicting the store keeps the
  compiled field; nothing here may discard a document a demanded group needs.
  A hash never admits content: identity equality may share public bytes, and
  the grants still decide who sees them.
- **Edges by topology.** A neighbour through a portal is near however far it
  lies in world coordinates **[external: EVE download on demand]**. Mapped
  demand already carries that; authored areas add the edges a window does not
  show, and a door's opening is a deadline, never a wait
  ([a door never waits](../decisions/residency.md#a-door-never-waits-for-loading)).

## The build order

Each step lands on its own reading, taken in one window under
[the recipe](#how-the-bars-are-read) on both backends, at the hero spawn and
the meadow, cold and warm. A windowed reading is a verification request to the
lead; a lane's own finish line is the build and a headless run. Evidence goes
in the landing commit.

| Step | Changes | Replaces or deletes | Reading that judges it |
|---|---|---|---|
| **1. Device readings and pools** | A budget reading beside `GpuMemoryProfile` from both backends; pools on `MemoryAccount`; the ledger feeding the account; `host.memory` per pool. [Brief below](#step-1-as-a-brief). | The per-read adapter in `DirectXNativeDeviceApi.ReadAdapter` for memory; `host.memory`'s "unified physical total is unavailable" | Exists: the `memory.<backend>` kinds, `host.memory`. Adds: per pool, budget, usage as the API reports it, usage by the ledger, spend limit, reserved, an estimate mark and a budget-change count. API usage and the ledger agree up to driver and swapchain memory; Direct3D 12 and Vulkan report budgets for one card within a stated margin; the count moves when another GPU program starts |
| **2. Every gigabyte has an owner** | `GpuRegion` host copies and staging report through `IAllocationObserver`; device allocations carry an owner family at the creation doors, with allocated and released per family; the managed heap and allocator slack as measured rows; the trace scene, volume and probe cache as owners; a fixed-cost line per residency in `world.residency` | The open items' owner table, which becomes counters; the inference about growth by copy | Owner rows against a page walk of the process: the unexplained remainder is named and small. A suspended heap walk names the large object heap's tenants. The spend margin is set from the budget-change count |
| **3. One reservation** | The selector, one per presentation, holding the elastic reservation and the one detail bias; region limits granted from it; growth reserving old and new; a device refusal lowering the pool; the pipeline's peak as a reservation; one background GPU and upload budget; the floors passed to `SetVideoMemoryReservation`; a pressure reading firing `MemoryAccount.TrimCaches` | `SdfWorldTables.MeshRegionDeviceLocalShare`, `ShaderPipelineMemoryBudget.DeviceLocalShare`, `GpuMemoryProfile.DeviceLocalShare`; a bias per bake; a budget per `SdfGpuWork`; the bake's own free-memory pacing on unified memory; the throw from `SdfWorldTables.EnsureMeshRegionCapacity` | Exists: `region-limit`, `region-named`, `detail-bias`, `refusals`. Adds: the selector's line (grant per residency, the shared bias) and a pool argument to `host.memory budget`. With four residencies the limits sum under the device pool's spend limit; lowering the device budget in steps along a fixed path raises the one bias with `refusals` at zero and no throw; `SHADERPIPE_BUDGET` still refuses |
| **4. Staged by demand** | The package stages every held residency that owes work; an unseen window demands the floor; `world.wait residency` reads every held residency | `WorldViewGraphHost.ReserveArrivals` and its reach and kept sets, the three `WorldPortalVisibility.ArrivalShare` cases, the arriving-world query, the pane-mapping law that pins them | Exists: the region line's `owed`, the background work's `unbound`, the render graph's record counts (`render.record`). With every window onto a routed world out of sight, its `owed` reaches zero, `unbound` stays flat and no view of it is scheduled; time until the occupied area has landed is no worse; frame time at the stops falls by the hidden renders |
| **5. The floor and eviction** | Each group in reach plans its coarsest tier as a kept product; draws name it until detail stands and when detail is evicted or refused; eviction by the selector's priority; the resident reach and its haze; relocation by device-side copy | The hole on refusal, on first arrival and on a replacement with no room; relocation landing every mesh from the host; coarsening as the only relief | Adds: `floor-bytes`, `floor-shown`, `evicted`, `evicted-bytes`, `reach`. Along the same lowered-budget path, no draw waits out of the frame, `evicted` rises while `retiers` stays flat, and captures from every side of each stop show no missing surface. A cold arrival's captures show the place whole before it is sharp |
| **6. CPU copies** | [Decision 6](#decision-6-what-a-landed-mesh-keeps-on-the-cpu)'s table, each row when its prerequisite has landed | The library forgetting claimed meshes; the cached claim as a reason to keep host words | Exists: account `live` and `cached`, `mesh-released-now`, the store's restores. Account bytes at a settled stop fall against `region-named`; restores across a turn and a relocation stay bounded |
| **7. The backing prototype** | A capability read on the three bar devices and a views-stage measurement of an indexed slab read; then slabs or sparse backing | Growth by copy; a region per residency, if slabs win | The capability table; the views stage's cost with and without the index; then allocated bytes per landed byte falling toward one, and the bytes of meshes placed in more than one region |
| **8. Observed use** | A drawn bit per group from the cull, read back; unobserved detail spare first | Visible detail coarsening while hidden detail is kept | Adds: observed and unobserved detail bytes, hidden bytes reclaimed. At the meadow under one reservation, `detail-bias` against today's; across a half turn, `owed-error` and `owed-frames` with a capture burst |
| **9. Lighting on the same demand** | [Decision 9](#decision-9-lighting-reads-the-same-demand-and-pays-on-demand) | The second demand derivation; retention in cells and triangles; a volume per unseen world; the reader's spin | Exists: `probe-cache-bytes`, `trace-scene-cpu-array-bytes`. Probe and scene bytes bounded along a valley flight; a retier wave rebuilds no trace scene; the gallery's lit exhibits read as their plaques say |
| **10. Areas, chunks, then the moons** | Authored areas on the gallery's floors (the owner's suggestion), with the stairwell, lift shaft and window bands as openings; a demand point led along its velocity; chunks as units with a seam rule across tiers, for terrain ([the valley](valley-and-moons.md)'s residency phase); the moons through routed residency | A whole large instance as one unit | Time until the occupied floor has landed, with and without the areas; under a lowered budget unoccupied floors leave first; terrain demand follows the observed neighbourhood |
| **11. Managed churn** | An allocation trace across an arrival, then pooling or streaming its sources, with the [job system packet](open-items.md#engine-job-system)'s list | Per-arrival allocation the trace names | Collections and allocated bytes in the arrival window |

Steps 1 and 2 change no behaviour and can run beside any other lane. Step 3 is
the first that can change a picture, and step 5 the first that changes what a
miss looks like; each of those lands with captures. Scheduling is not rebuilt
here: the bake's pool and the lighting's workers move onto the one pool in the
[job system](engine-job-system.md#6-build-order)'s J1, and the selector hands
it comparers, never a second executor. An authored area needs a spatial role
of its own: the nearest vocabularies are a placement's volumes, whose
influence channel the engine never interprets by contract
(`WorldPlacementSpatialRole`), and gravity areas, and neither has openings.

### Step 1 as a brief

**Outcome.** `host.memory` prints, for each pool the machine has, what the
device says Puck may use and is using beside what Puck's own ledger counts,
and `MemoryAccount` holds a budget per pool that follows the device. No
consumer changes: both quarter shares stay until step 3.

**What to build.**

1. A budget reading in `Puck.Abstractions`, beside `GpuMemoryProfile`: budget
   and usage for device-local memory and for host-visible memory, whether the
   figures are estimates, and a serial that moves when the device signals a
   change. `IGpuDeviceContext` returns the current one beside
   `IGpuDeviceContext.MemoryProfile`, without a driver call when nothing
   marked it stale.
2. Direct3D 12: hold the adapter as `IDXGIAdapter3` for the device's life;
   read the local group, and the non-local group only when the profile is not
   `GpuMemoryProfile.UnifiedMemory`; register the budget-change event and test
   it on the frame thread without waiting.
3. Vulkan: enable `VK_EXT_memory_budget` when the device lists it and read it
   beside `VulkanNativePhysicalDeviceApi.GetMemoryProfile`; fold heaps by the
   profile's own device-local rule, every heap into one on unified memory;
   refresh at a low fixed rate on the frame thread; mark the reading an
   estimate when the extension is absent.
4. `MemoryAccount`: a pool per owner (host or device, one pool on unified
   memory), with budget, owned and reserved bytes per pool, admission against
   the owner's pool, and a door that takes a device reading and sets the
   device pool's budget and spend limit. Existing owners stay in the host
   pool.
5. `GpuDeviceMemoryWork` reports each row's held bytes to an
   `IAllocationObserver` the host hands it, so the account carries a device
   owner and a transfer owner; the backends gain no reference to the account.
6. `HostMemoryCommandModule` prints the pools and the reading.

**Invariant.** A byte the ledger counts is charged to exactly one pool.
`GpuDeviceMemoryWork.CountAllocated` is the one door and
`GpuDeviceMemoryWork.IsDeviceLocal` the one rule; the observers are called
under its lock. On unified memory there is one pool, so the two rows cannot be
counted as separate capacity.

**Independent sources.** Each API is read from at least two: for DXGI,
Microsoft's reference, D3D12MA's budget code and DXVK's implementation of the
same interface; for Vulkan, the Khronos reference, VMA's budget code and RADV's
heap code. Say where they disagree.

**Finish line.** Build the projects touched and run a headless World:
`host.memory` prints the host pool and names the device pool absent. The
windowed reading on both backends is a verification request: at the hero spawn
and the meadow, the pool lines, the API usage against the ledger with the gap
named, the two backends' budgets for the one card, and the change count before
and after a second GPU program starts.

**Not in this step.** No reservation, no limit change, no trim, no attribution
by owner.

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
qualification on the same mechanism. One consequence of the haze ruling is
worth the owner's eye when step 5 lands: on a device where even the floors do
not fit, the haze stands nearer than the authored far distance.

## Open questions and their owners

| Question | Owner | Decides |
|---|---|---|
| The Deck's numbers: RADV's heaps and budgets, how far the GPU reaches into system memory under SteamOS, free memory with the Steam client running, and whether GPU pages count against the host's free memory | A lane with the hardware, after step 1 | The one pool's formula in decision 2; the 10 GB target becomes a qualification on a real reading |
| Whether Vulkan's budget on an RTX card tracks the DXGI budget | Step 1's windowed reading | Whether one spend margin fits both backends |
| The spend margin | Step 2 | The constant in decision 2 |
| Sparse binding, reserved resources, memory priority and pageable device-local memory on the three bar devices | Step 7 | Slabs or sparse backing; whether priority classes are passed to the driver |
| What happens over budget on an RTX card under Linux | A Linux boot of an RTX machine | How strictly Linux discrete must reserve before allocating |
| A second, independent reading of GPU bytes on Linux (the DRM client statistics) | Step 2 | The Deck's attribution |
| The fixed cost of one residency | Step 2 | Whether a hub of many portals needs a session-scale design beyond this plan |
| The floor within one stop's reach, on a device | Step 5's first reading | The floor class's size; the offline bake bounds it from above |
| Which tier the lighting traces | Step 9's first reading | Whether the floor and the trace tier are one product |
| How long detail may go unobserved before it is spare | Step 8, with a turn's captures | The grace interval, and the first owner question |

## Systems considered

Rule 7 binds a plan as it binds code. What this page found already built, and
extends:

- **The account and its doors.** `MemoryAccount`, its categories, owners,
  `TryReserve` and the trim event are the authority; no second budget type.
- **The ledger.** `GpuDeviceMemoryWork` is the device's measured truth and
  becomes the account's feed, not a parallel count.
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
- **Chunks.** `SdfMeshChunks` is the sub-group unit terrain needs.
- **The pool.** `JobPool` and the job system's J1 own scheduling.
- **Visibility.** `SdfMeshOcclusion` is the feedback's producer.

## Sources

Only what a decision rests on. Puck is independent work: each is a second
opinion, none is the specification.

- D3D12 memory pool: [D3D12_MEMORY_POOL](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_memory_pool),
  which states that on a unified adapter the system pool and the local segment
  group are the same memory.
- Microsoft residency: [Residency](https://learn.microsoft.com/en-us/windows/win32/direct3d12/residency),
  for the budget's behaviour, the reservation, one pool against two, watching
  for negative changes about once a frame, and destroying heaps as the reliable
  release; with [QueryVideoMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo)
  and [the change notification](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-registervideomemorybudgetchangenotificationevent).
- D3D12MA: [the allocator's budget](https://gpuopen-librariesandsdks.github.io/D3D12MemoryAllocator/html/class_d3_d12_m_a_1_1_allocator.html),
  for the non-local budget reading zero on an integrated adapter and for
  correcting usage between fetches.
- DXVK: [its DXGI adapter](https://raw.githubusercontent.com/doitsujin/dxvk/master/src/dxgi/dxgi_adapter.cpp),
  for the non-local group answered with the local budget on a unified adapter.
- Vulkan memory budget: [VkPhysicalDeviceMemoryBudgetPropertiesEXT](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceMemoryBudgetPropertiesEXT.html).
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

This page reopened the Microsoft pages and the RDNA guide; the others are as
the research round's lanes opened them.
