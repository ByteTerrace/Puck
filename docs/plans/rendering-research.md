# Rendering research

The synthesis of the rendering research round. Nine area lanes each mapped one
part of the renderer from its code, gathered what has been measured, and judged
outside work against it: the mesh bake with its tiers and residency; global
illumination, sky and exposure; shadows; visibility and submission; shading,
the temporal resolve and the image; glass, water, portals and nested views;
GPU work scheduling and the frame thread; foliage, instancing and terrain; and
presentation. The round follows two of the owner's rulings
([AGENTS.md](../../AGENTS.md#puck-is-pre-release)): a research round precedes
a major area of work, and Puck is free to combine and not only copy, so every
decision weighs a Puck-native combination beside the borrowed technique.

This page decides across the areas, in
[the MMO foundations memo](mmo-foundations/synthesis.md)'s manner: for each
decision the options, the evidence, what each option closes and where it
fails, the favoured option, the strongest case against it, and the ruling it
rests on. It replaces no plan. [Illumination](illumination.md),
[visibility](visibility.md), [virtual geometry](virtual-geometry.md),
[streaming and memory](streaming-and-memory.md),
[transmission](transmission.md), [author shaders](author-shaders.md),
[the terrain pass](terrain-pass.md), [dense landscapes](dense-landscapes.md),
[the UI overhaul](ui.md), [the benchmark](benchmark.md) and
[the job system](engine-job-system.md) keep their steps. This page says where
two of them meet, where the round's evidence re-ranks a step, which mechanism
serves several areas, and in what order the cross-area work depends on itself.
[Open items](open-items.md#next) ranks the work; nothing here is a queue.

A reviewer from another model family read the synthesis against the code and
raised thirty-five findings before its host stopped it. Each is answered in
place, and [the review](#the-review) lists them with what changed in the build
order and which parts of the page the review did not reach. The build order is
therefore a corrected proposal: the owner agreed to the summary's priorities
before the review and compares the two orders before this one is treated as
settled.

Labels. **[opened]**: this page reopened the code or document at this tree,
and the words or values are what the tool printed, with the `file:line` a lead
at this revision. **[record]**: taken from an area lane's map or judgement and
not reopened. **[reading]**: a measurement from the lead machine's windows,
kept in the commit or plan passage that recorded it; it is a premise to read
again under [the one recipe](streaming-and-memory.md#how-the-bars-are-read)
and qualifies no device. **[inference]**: this page's reasoning or arithmetic.
An outside claim is **[confirmed]** when the round's verification pass found
the quoted words in the source, **[partial]** when the source says something
narrower (the narrowing is stated), and **[unverified]** when it was read only
through a summarizing fetch or an abstract, or was not checked. A
**[confirmed]** label covers one quotation; it says nothing about whether the
technique fits Puck. The pass checked 75 claims, those under each area's first
three candidates, and 23 of them came back partial, refuted or unreachable
([the tally](#how-the-records-were-weighed)); every other outside claim is as
a summary reported it. Two claims the pass refuted are used nowhere. Published
figures keep their hardware and predict nothing for Puck.

## Summary

- **The frame misses its bar on the frame thread before the GPU matters.** One
  thread runs the tick and then produce, and most of produce has no breakdown
  by part. The first work is a reading (decision 1). Then the frame's extract
  follows what a scheduled render reads (decision 2), and the tick is
  shortened by partitions and scheduling, never by a frame of latency
  (decision 3).
- **The irradiance relight is bound by round trips, and the cold bake may
  be.** Probe jobs take about 25 ms a round trip through the frame's queue
  for a millisecond or two of GPU work, and a cold bake uses about five
  seconds of GPU time in a minute. One pattern answers the relight
  (decision 4). For the bake it is a candidate until one run under the
  recipe gives the critical path (decision 10). Raising the background
  share's use waits for a budget that follows measured headroom, because the
  root frame and the share already sum past the period.
- **What the owner sees as lighting and shadows "off basically everywhere" has
  several independent causes, and three are contracts, not tuning.** No encode
  stands between linear scene light and an encoded working image on any path a
  view takes to the display (decision 5). A shadow's softness, its search
  reach and what a receiver reads outside coverage depend on which cascade or
  which kind of view answers (decision 6). Indirect light, and the exposure
  that meters it, follow a lighting change minutes late (decisions 4 and 7).
- **One tier chain can serve three consumers, and the light where it
  qualifies.** A group's coarsest admissible tier, kept resident, is what a
  miss shows, what a far cascade draws and what a far copy draws. The light
  traces a tier chosen by probe spacing and shares that floor only where a
  lighting check passes (decisions 7, 8 and 9).
- **One instance table and one classification** serve the camera, every
  cascade, nested views and glass, and the same record split serves indexed
  draws, member boxes and motion that restages nothing (decisions 11 and 12).
- **Demand has floors, and presented pixels divide what is left.** A world
  owes some work before any of its pixels show (a first publication behind an
  approaching portal, a screen that lights a visible room). Past those floors,
  extent, rate, quality, lighting budget and background GPU time follow a
  view's share of the picture, a measure only an engine composing worlds into
  one frame has (decisions 2, 4, 7 and 14).
- **Every reading is from one class of GPU.** No area has a reading on the
  RTX 2060 or the Steam Deck, so every ranked gain is a premise there
  (decision 1).
- **Looks are authored.** The display transform, exposure, a light's
  softness, a far shadow's lag, the night and the quality tables are data the
  standard package and any author sets over a neutral engine default. The
  engine's work is to carry each as authored data, and the owner's list holds
  engine behaviour and owner-only actions
  ([authored looks and the owner's calls](#decided-by-evidence-authored-looks-and-the-owners-calls)).

## How the records were weighed

Nine lanes read the code and the world outside. They summarized 166 sources,
of which 140 could be fetched and 26 could not. A verification pass then
reopened the claims under each area's first three candidates: 63 claims from
36 outside sources and 12 claims about 10 repository files.

| Area | Confirmed | Partial | Refuted | Unreachable |
|---|---|---|---|---|
| Mesh bake, tiers, residency and streaming | 8 | 1 | 0 | 0 |
| Visibility, culling and draw submission | 5 | 3 | 0 | 0 |
| Global illumination and probes | 7 | 2 | 0 | 0 |
| Shadows | 4 | 2 | 0 | 0 |
| Shading, the temporal resolve and the image | 5 | 4 | 0 | 0 |
| Glass, water, portals and nested views | 5 | 2 | 1 | 0 |
| GPU work scheduling and the frame thread | 7 | 0 | 0 | 1 |
| Foliage, dense landscapes and instancing | 6 | 3 | 0 | 0 |
| Presentation | 5 | 3 | 1 | 0 |
| All | 52 | 20 | 2 | 1 |

A confirmed claim means the quoted words are in the source. Most fetches went
through a summarizing tool, so a source the pass did not reach is known only by
its summary, and each ranked candidate below the third in an area carries
unchecked claims. Both refuted claims were about the repository, and the
unreachable one was a slide deck's quotation.

Where an area disagreed with the code, with another area or with a plan,
these readings changed the page:

1. **Two areas disagree on the tick.** Presentation ranks first a tick pump on
   its own thread that publishes a snapshot; scheduling rejects overlapping the
   tick with produce. Decision 3 settles it against a frame of latency, on the
   register's ruling, and the open item that calls the move "the presentation
   view's last cut" is stale against the job memo.
2. **What bounds the cold bake has not been read under the recipe.** One
   sampled trace read the bake's workers busy for 237 of about 660
   thread-seconds, "neither CPU- nor GPU-bound but paced by takes"; runs with
   the display composing beside other lanes' builds read the bake CPU-bound
   and sharing its processors; and the quiet runs of the same record had the
   display off and the GPU at idle clocks, where background work completes at
   the compositor's pace **[opened]** (the message of the change that made a
   withheld frame drive its bake). The packet's header says CPU-bound. None of
   these is a quiet machine with the display on and composing, which is the
   recipe. So neither cut is ranked ahead of the other until one such run
   gives the critical path (decision 10).
3. **Level yields disagree.** The mesh-bake reference and the handbook say the
   first two collapse levels keep 11% and 6% of the stored triangles; a later
   cold bake's done line reads 29% and 19% of the merged triangles
   **[reading]**. Neither is used: decision 8 starts with a counter.
4. **A deeper level chain was proposed and is already measured out.** The
   shadows area asks for a deeper error chain for far casters. Virtual
   geometry's D1 measured four collapse levels to 128 cells (the oak fell from
   3,052 triangles to 1,440) and three field-level experiments. Decision 8
   answers the far cascade with a coarser tier.
5. **Indexed draws were proposed three times**, by the bake, visibility and
   shadows areas, as three candidates. They are one probe (decision 11). The
   streaming prototype's "indexed read" cost is a different thing: buffers
   indexed through a descriptor array, not an index buffer.
6. **Two refuted premises are dropped.** One said the depth pyramid records
   for a view whose quality disables ambient occlusion because no pass reads
   the flag; the surface and views-filter stages read it. The other attributed
   a research gap to open items that the file does not hold.
7. **Recording's share has two readings.** The job memo's opening says a third
   of produce; its build order says about a millisecond since residency
   staging moved into preparation. The newer one holds for recording, and
   neither attributes the rest (decision 1).
8. **An open item is stale.** It says the occlusion's visible bit checks no
   identity. The kernel resets a draw's history when its generation differs
   **[opened]**
   (`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-occlusion.comp.hlsl:249`).
9. **A source one area could not read, another did.** The scheduling area's
   pass confirmed quotes from the Lumen and Assassin's Creed Shadows decks and
   from Destiny's, which the presentation area could not fetch. Karis's
   temporal supersampling notes are listed as fetched by one critic and unread
   by the judgement that needed them, and are treated as unread.
10. **Borrowed figures that do not transfer.** Cyberpunk's distant shadows
    refresh "every few seconds" **[confirmed]**; at the debug world's
    24-minute day a held cascade of 480 m radius tolerates about 0.4 mrad of
    light turn and would refresh about eleven times a second **[inference]**.
    Precomputed radiance transfer states linearity for lighting coefficients,
    and its interreflection is exact only under distant, spatially invariant
    lighting **[partial]**, so decision 7 rests the factored relight on Puck's
    own check. Wihlidal's compaction keeps order inside a wavefront and only
    optionally across them **[partial]**, which the visibility plan's sentence
    overstates.
11. **A plan the round restated too strongly.** The streaming plan favours a
    lighting tier resolved by probe spacing and asks, as its step's first
    reading, whether a group's floor alone suffices
    ([streaming decision 9](streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand)).
    The round's areas wrote the floor as the decision. Decisions 7 and 8 keep
    the plan's rule.
12. **A built fix that covers less than its name.** The unmerged lighting
    lane keeps several probe jobs in flight and commits a batch once. It was
    built and never run in a World, its readings are of the tree it branched
    from, and it still grants work per rendered frame **[opened]**
    (`SdfMeshIrradiance.Grant` at that lane's commit). Decision 4 separates
    the two.

## The decisions

### Decision 1: what is read before anything is built

**What must be decided.** Which readings gate the build. Most of the round's
ranked gains are arithmetic no counter can confirm, and no area has a reading
off the RTX 4070.

**What exists.** `host.frames` splits an iteration into coarse phases and
`world.counters render.record` splits recording; `world.gpu-timing`,
`world.counters gpu`, `world.residency` and [the benchmark](benchmark.md)'s
per-chapter report exist. What is missing:

| Missing reading | Why it gates | Home |
|---|---|---|
| Produce by part: pump, capture, pack, lighting stage, exposure, schedule, package prepare, record, flush | Produce reads about 17 ms and the tick about 8 ms on Vulkan **[reading]**; the record sum is about 1 ms, so most of produce is unattributed | The job system packet, on `RenderRecordWork`'s pattern |
| The displayed interval per present, with its outcome (shown, skipped, queued, unknown) and the signal it came from | `host.frames` reads the loop's interval, and a skipped Vulkan present is counted but never placed on the timeline. The Vulkan presenter polls the present two frames back once, stamps a success with the CPU's clock and advances the id on a timeout too (`VulkanFramePresenter.RecordPresentTiming`) **[opened]**, so neither says when a frame was displayed or dropped | The benchmark's stage 8, extended; a host that gives no display time reports that as a capability, and the column is absent |
| Produce captures and prepares, counted by what asked for them (a scheduled render, an owed upload, the freshness question) | Decision 2's saving is the captures no scheduled render reads, and the freshness question prepares too | The job system packet |
| The GPU frame per chapter split into the root view, nested views and background work, beside the background share's occupancy | Decision 4 raises occupancy, and the root frame and the share already sum past the period on the round's readings | `world.gpu-timing`, `world.counters gpu` |
| One cold bake with worker activity, per-take waits, GPU timestamps and the end-to-end interval taken together, on a quiet machine with the display composing and an empty store | Which of decision 10's cuts is on the critical path | The bake packet |
| Shadow queries issued and hit-and-light pairs tested by the relight, apart from the scheduler's predicted charge | Decision 7's stores are sized by executed work | `world.counters gpu` |
| Triangles drawn by level and by tier offset from demand; bytes of each level no pass selected | Decision 8's inference has no counter | `world.counters gpu`, `world.residency` |
| The occupied share of each cascade's receiver cells | Admits or refuses demand paging; sizes decision 11's caster cull | Virtual geometry D8's reading |
| One session node and one nested-view node per pass, with each home session, routed session and camera view named by the residency it reads | Decisions 2, 11 and 14: the boot world's sessions are views of its one residency, and a routed session has its own | Visibility's owed readings |
| What an empty indirect command costs on each bar device | Whether compaction alone recovers the walk | Visibility V1a |
| A cascade pass split into command walk, vertex work, fill and clears | Which of three fixes pays | A capture in the lead's window |
| What restages about 2,600 records a frame with nothing moving | It may be a defect cheaper than any step that claims its saving | Visibility |
| Collections and bytes per iteration, attributed | 47 collections pausing 674 ms in 600 iterations **[reading]** | The job system packet |
| Any pass on the RTX 2060 or the Steam Deck, on each backend the device runs: Direct3D 12 and Vulkan on the RTX 2060 and the RTX 4070, Vulkan on the Steam Deck | Visibility's M2, the author-shader matrix, illumination's step 0 and transmission's budgets are all unread | One GPU leg for four plans |

**Options.** 1a builds the ranked candidates on arithmetic. 1b takes one
reading stage first, changing no behaviour. 1c takes each candidate's own
step 0 as it starts.

**Favoured.** 1b for the table, 1c for the measurements a single candidate
needs (the shared-hit fold, the gather's variants, the drain's pass counts,
the meter compiled out, each foliage prototype audited per tier).
**Strongest case against:** readings delay fixes the owner can already see.
The fixes of decisions 5 and 6 that rest on the code alone do not wait; only
work whose rank depends on a number does. A reading stage that only attributes
cost also proves no later change paid, so from stage 1 on each check names the
dividend its change claims and what would show it false
([the build order](#the-build-order)).

**Decided.** Readings first. It rests on "Hunt real problems with every
instrument" and "Evidence is taken as a player takes it"
([AGENTS.md](../../AGENTS.md#puck-is-pre-release)): the readings are live
counters and the benchmark's reports in real time, and nothing is recorded as
a baseline.

### Decision 2: the frame's extract follows what is shown

**What must be decided.** What the frame thread does for a world, a view or a
body that no presented pixel reads.

**What exists.** `HostLoop.Run` steps the simulation and then calls produce on
the same thread **[opened]** (`src/Puck.Launcher/HostLoop.cs:169-184`).
Produce begins every package's frame before it schedules: `PackagesBeginFrame`
runs ahead of `RenderGraphScheduler.Schedule` **[opened]**
(`src/Puck.Shaders/Graph/RenderGraphRuntime.cs:1253-1267`). The SDF package
then pumps and prepares every residency it holds, shown or not **[opened]**
(`src/Puck.SdfVm/SdfWorldPasses.cs:480-484`):

```csharp
        foreach (var residency in m_residencies.Keys) {
            if (!residency.IsReleased) {
                _ = residency.Prepare(context: in context);
            }
        }
```

Each prepare captures its frame: render poses, the state mirror, the
effectors' field probes, the camera's clearance march and the pose pack
**[record]**. The schedule's own inputs ask for the same prepare. Before it
schedules, the runtime asks every package whether an instance's last render
still stands (`RenderGraphRuntime.WithUnchanged`), and the residency answers
by preparing **[opened]** (`src/Puck.SdfVm/SdfWorldResidency.cs:644-646`):

```csharp
    public bool IsUnchanged(in FrameContext context, int view) {
        if (
            !Prepare(context: in context) ||
```

A second schedule runs only when a release left an output standing
(`RenderGraphRuntime.RescheduleAfterRelease`) **[opened]**, so a window that
a camera turn reveals, which releases nothing, gets no correction in its
frame. A hidden window per arriving world renders at
`WorldPortalVisibility.ArrivalShare` because only a scheduled render stages a
residency, and every boot-world session is filmed into the frame whether or
not its instance renders (`WorldScreenBinder.FilmHomeViews`) **[opened]**.
The pose pack looks each swing's driver up by an ordinal string compare on
every pack **[opened]** (`src/Puck.World.Client/WorldGaitDrivers.cs:610-626`).

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 2a. Record views in parallel waves now (the job memo's R1) | The serial record sum is about 1 ms on the debug world **[reading]** | About half a millisecond | Its own admission rule; it stays held |
| 2b. A cheap demand and freshness snapshot first; then the schedule; then capture, prepare and stage only what a scheduled render, an owed upload or another consumer's floor reads; film a home session only when its instance renders | Destiny extracts only what is visible and creates job chains only for views in the frame (Tatarchuk, slides 57 and 73) **[confirmed]**; [streaming decision 4](streaming-and-memory.md#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render) | Extract that grows with residencies held; the hidden render per arriving world; a world behind a rate-held view that never arrives | Moving the begin-frame call alone saves nothing, because the freshness question prepares; a dependency a prepare reveals has no same-frame schedule today; per-view state keyed by index shifts when a view is skipped |
| 2c. Cut the named parts at their cause: field queries, restaged records, interpreted motion (decision 12) | Field queries are about a third of the meadow's frame **[reading]** | The largest attributed parts | Parts the breakdown has not named |
| 2d. Fork what remains on the one pool | The pose pack is 0.9 ms of a 3.4 ms begin-frame **[reading]** | What stays after 2b and 2c | Waits on the control plane's bounded waits and J1 |
| 2e. No steady-state allocation on the frame and background paths, cut at the sources a trace names | One iteration allocated 68 MB, and the worst frames carried pauses of 20 to 28 ms **[reading]** | Hitches at any average frame rate, from whichever thread allocates | A collector mode switch would be a stopgap beside the cause fix |

**Favoured.** The breakdown, then 2b, then 2c in the order the breakdown
gives, then 2d, with 2e beside all of them. 2b has four parts, and the first
is what makes the others pay:

- **The snapshot.** A residency answers "does this view's last render stand"
  and "what does this frame demand" from state that needs no capture: the
  camera and view signature inputs, pending bake draws, settling lighting, a
  waiting source and each publication's change stamp. `Prepare` splits into
  that cheap step and the heavy capture, and only the cheap step runs before
  the schedule.
- **The bounded correction.** The runtime's own comment says preparation can
  rebuild live footprints. When a scheduled prepare reveals a dependency the
  snapshot missed, the frame schedules once more for it. A second level
  revealed by that pass waits a frame behind its authored fallback and is
  counted.
- **State by view, not by index.** Exposures, the occlusion's previous
  visible set and the temporal history are keyed by the view's identity, so a
  skipped view shifts nothing ([visibility V5](visibility.md#v5-multi-view-and-shadow-work)).
- **Floors.** An unseen residency keeps what another consumer is owed
  (decision 14's demand floors): it is staged under the shared budget and
  never drives.

**Strongest case against:** the snapshot is a second answer to a question
`Prepare` answers today, and two answers can disagree. The snapshot is
therefore the only freshness answer, and the heavy step reads its result
instead of deciding again.

**Decided.** A view no consumer reads costs the frame thread nothing, and a
world nobody sees costs its rate tier, its floors and its owed uploads. It
rests on work proportional to change and on the streaming plan's decision 4,
whose five parts are this decision's design. The field-query cuts stay with
visibility V2a and V2b and build no second answer cache beside
`WorldFieldAnswer`.

### Decision 3: where the tick runs

**What must be decided.** Whether the tick leaves the frame thread, since a
frame that steps carries a whole tick ahead of produce.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 3a. Serial, as now | The loop above | Nothing | The sum of tick and produce is the frame |
| 3b. The tick forks partitions the frame thread joins inside the step, with rated work kept off tick frames | The job memo's contract; snapshot partitions are built | The tick's wall time, at no latency | Kit bodies and rule gates stay serial until their transaction is redesigned |
| 3c. A tick pump on its own thread publishes a snapshot; the present pump only produces | The interval falls from the sum to the longer part: about 25 ms to 17 ms on the round's readings **[inference]** | A long tick as a long frame | The frame shows the tick before the one its input reached, up to an iteration later; no source on the hand-off's latency was read |
| 3d. The tick overlaps produce (Destiny's pipelined model) | Destiny's deck **[confirmed]** for extract, not for latency | The serial sum | The same latency |

**Favoured.** 3b, after decision 2, in the order
[the job memo](engine-job-system.md#6-build-order) fixes: the control plane's
bounded waits (C0), then the pool with a fork that returns an operation its
forking thread owns (J1), then the crossing-scan and rigid partitions (T3,
T2). The order is a dependency. A pump-side join that passes its wait budget
keeps the logical tick across host iterations and lets the control phase run;
new partitions forked through today's same-call join would hold input and the
console for as long as the slowest partition. Only readings and the built
snapshot partitions precede J1. **Strongest case against:** while the
frame is CPU-bound on a fixed-refresh display a late frame already costs a
refresh, so 3c would lose nothing there. That holds only while the bar is
missed, and the cause fix is to stop missing it.

**Decided.** The tick stays ahead of produce and is shortened by partitions
and scheduling. It rests on the register: "A lower tier, a coarser shadow
snap, reduced contact detail or a frame of latency is never the fix for a
missed bar"
([the pipeline foundation](../decisions/rendering.md#the-pipeline-foundation)),
on the job memo's withdrawal of the pipelined simulation worker ("no input
latency is added and no tick runs under a produce"), and on the purpose's
weight on feel. The presentation view's cut that makes produce read no server
state is still built, for the silo and the view
([runtime plan](runtime-and-delivery.md#the-presentation-view)). Option 3c
returns only on a reading: tick and produce each under the period, their sum
over it after decision 2, and a measured hand-off. It is then the owner's
call, because it reverses a recorded ruling on what the engine does.

### Decision 4: background GPU work without round trips

**What must be decided.** How the relight, the trace, the bake's sampling and
landing use the device, given that round trips and not compute bound the
relight and may bound the bake.

**What exists.** The probe client admits one query at a time **[opened]**
(`src/Puck.SdfVm/SdfMeshProbeGpuWork.cs:249`):

```csharp
            if (m_inFlight || !tables.BindsBackgroundWork) { return false; }
```

Its coordinator blocks on each answer (`SdfMeshProbeGpuWork.Submit`), every
relight job is followed by a commit job, and the relight budget is credited
per rendered frame, held to two frames' worth (`SdfMeshIrradiance.Grant`)
**[opened]**, so a presenter at 30 FPS is granted half the work a second of
one at 60. The service has `SdfGpuWork.SlotCount` slots and draws on one
budget for every residency of a presentation: `SdfGpuWork.GpuShare` of the
GPU's time as a rate of wall time (`SdfGpuWorkBudget`) **[opened]**. On one
windowed run of the tree the unmerged lighting lane branched from, the probe
jobs read 25 ms a round trip, the service's occupancy 0.05 of its share, and
the finest level's lighting age reached 44 s after a night switch
**[opened]** (that lane's commit message; the repro is in
[the packet](open-items.md#lighting-and-gi)). A group's bake is the same
shape: up to five narrowing lattices, its final slabs, up to eleven
crossing-root rounds, then gradients and materials, each a readback
**[record]**. A quiet cold bake reads medians of 60.6 s on Direct3D 12 and
64.7 s on Vulkan, both over the minute **[opened]** (the third window's
commit message), with about 4 s of GPU work in a 68 s busy span
**[reading]**. Bake batches are cut by tap count at a fixed
`SdfMeshPathBake.NanosecondsPerTap`, and landing runs at the constant
`SdfGpuWork.UploadBytesPerSecond` on every device.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 4a. Raise the share or the slot count | None | Nothing: occupancy is 0.05 of the share | The round trips remain |
| 4b. Several probe jobs in flight and one commit per batch; then the budget in probes a second | The first half is built on an unmerged lane and has never run in a World: one admission door makes a query that replaces shared probe state run alone, and a batch's last job commits only the rows its batch wrote. The second half is not built: the lane grants per frame as the base tree does. A shipped probe system holds a fixed ray budget (Apers, 100k rays and about 6k probes a frame) **[confirmed]** | Most of the relight's round trips; throughput that halves at 30 FPS | Bounce order changes inside a batch; a per-second grant needs authored units, a bounded catch-up and a lockstep drain that stays a function of its inputs, whatever the play cadence |
| 4c. A device-resident work list: probe state on the device, a select kernel with a deterministic key, compaction into indirect arguments clamped to a ray budget, one recorded chain; the host reads counters | "Fixed update cost, variable lighting latency" (Lumen) **[confirmed]**; Lumen still downloads its feedback to the CPU, so no source shows a device-resident scheduler with a history-independent drain | Host listing, sorting and per-probe readback | A recorded chain cannot be cut, so the clamp is a kernel input (one 4.4 million-ray dispatch ran past the device timeout **[record]**) |
| 4d. The bake's front end in one round trip per stage: crossing roots and gradients in the kernel, narrowing chained on the device | Dreams ran a hierarchical GPU evaluator on a console **[unverified]**; no source measures GPU root search | About twenty round trips a group falling to about four **[inference]** | A root rule written by hand in C# and again in HLSL is two implementations, whoever is named to keep them equal. The rule (`SdfSampledLattice`'s crossing root: its bracket, tolerance, round limit and thirding) needs one source that both evaluators are lowered from or consume |
| 4e. Background jobs reach the queue before the nodes record, and a wait names the timeline value it needs | Read from code: the post-upload pump runs inside the frame's open batch, whose fences alias the batch's owner **[record]** | A job answered at the next step, not the next frame | A second native submit a frame |
| 4f. A second queue for background compute | A Khronos sample's 21.8 ms against 22.9 ms **[unverified]** | Long jobs delaying the frame behind them | Nothing read covers the bar devices' queues; a probe with a stop rule only |
| 4g. The share follows measured headroom, and nested views, uploads, lighting and bake are priced in one unit | The root frame is about 12.6 ms of GPU before nested views, and the background share is 4.2 ms: 16.8 ms against a 16.7 ms period **[reading]**. The GPU and upload credit is already one budget for a presentation's residencies; the relight's per-frame credits and the bake's fixed cost a tap are the units outside it, and host servicing still costs per residency | A share that fits no frame once it is used; budgets in several units | Feedback can hunt; the price enters the scheduler as an input so lockstep stays reproducible |

**Favoured.** 4g's first half comes first: before any step raises occupancy,
`SdfGpuWorkBudget` gains a bounded headroom policy. The share is what the
measured root and nested GPU frame leaves of the period, between a floor that
keeps lighting from starving and today's `SdfGpuWork.GpuShare` as the
ceiling; under overload discretionary background work yields first, then
nested views' rate, and the floors last. A lockstep drain ignores the
measured share, so it stays a function of its inputs. Then 4b, each increase
in throughput read beside the root frame and the lighting age in the same
window, on both backends. Then 4c and 4e. 4d is a candidate whose rank waits
for the cold bake's critical-path reading (decision 10) and whose first step
is the root rule's one source. The rest of 4g, one unit across consumers, is
a design round once the share is used. 4f only as the probe. **Strongest
case against:** today's occupancy of 0.05 limits nothing a successful worker
does tomorrow, and the share already overruns the period on paper, so
without the headroom policy the first fix to lighting latency is paid in
frame time.

**Decided.** Background work is a chain the device runs, several in flight,
budgeted in wall time against measured headroom and answered without waiting
a frame. It rests on work proportional to change, on the illumination plan's
rule that overload degrades latency and never frame time, and on one
implementation: `SdfGpuWork` stays the one service, `SdfGpuWorkBudget` the
one budget, and the job system's `Lighting` class supplies the policy. Waits
stay on the host's primitives through `HostWaits`. The relight and trace
allowances and the lighting age are authored (`render.indirect`'s `budget`);
their unit becomes work a second.

### Decision 5: one scene-to-display boundary

**What must be decided.** What kind of value an SDF view hands the root graph,
and where authored colour and light become display values.

**What exists.** The working image is defined as display-referred and encoded
on the sRGB curve, 1 at SDR white ([the shader reference](../reference/shaders.md)).
The display encode writes it as it is in SDR and decodes it with the sRGB
curve for HDR **[opened]**
(`src/Puck.Shaders/Assets/Shaders/Runtime/display-encode.frag.hlsl:55-59`):

```hlsl
    if (encode.colorSpace == DisplayScRgb) {
        return float4(((puckSrgbToLinearExtended(color) * encode.whiteScale) + dither), 1.0);
    }

    float3 sdr = saturate(color + dither);
```

An SDF view's composite multiplies linear luminance by the exposure and
stores it, and the root's `place` pass adds at most the filmic curve, whose
comment says "no gamma encode follows it" **[opened]**
(`src/Puck.Shaders/Assets/Shaders/Graph/place.comp.hlsl:20-21`). Authored hex
enters shading as linear reflectance: "the render path is gamma-naive end to
end" **[opened]** (`src/Puck.Maths/HexColor.cs:11-12`), while keyed colours
decode the same hex before blending (`WorldKeyResolver`). So linear light is
shown as if it were encoded in SDR and decoded a second time in HDR. The
meter aims a scene mean at 12.5 / (100 × 1.2) = 0.104; written as a code that
is about 1% of white in display light, where an encode would give code 0.36
**[inference]**. The debug world authors `compensation: 2`, "Two stops over
the meter keep the world's pale look" **[opened]**
(`worlds/debug/hall.puck:1513-1530`), which lifts that one point of the
curve. The terrain pass already expects the standard transfer: its grey card
must read 118/255, the sRGB code of 18% **[opened]**
([terrain pass, step 1](terrain-pass.md#order-of-work)).

A view reaches the display by more than one path, and `place` is on only
some of them **[opened]**. A lone full-screen view with no tonemap, sharpen,
bloom or matte is not placed at all: the root composes no pass for it
(`WorldRootGraph.PlacesViews`) and the view stands for the world
(`src/Puck.World.Client/WorldViewGraphHost.cs:831-833`). A pane goes through
the same placement package with an image that is already display-referred. A
view a camera films for a screen applies its own bloom and publishes the
result directly (`SdfViewSnapshot.BloomStandalone`, the `bloom-apply` pass of
`SdfWorldPackage`). So an encode added to the place pass would miss the lone
world and the filmed view, and one added to the shared placement would encode
a pane twice.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 5a. Keep the value and tune with compensation | None | Nothing | Tones are right at one exposure point; albedo taken as linear over-reflects; screen pictures are multiplied as luminance while encoded; HDR output is wrong |
| 5b. Shade in linear scene light; decode authored colour at the one door that packs it; decode an encoded screen picture before its nits multiply and in the room-glow reduction; convert once on every edge from scene light to the working image, after bloom and the authored transform | Brightness belongs before the tonemap, not as a post gamma (Lottes, slide 54) **[confirmed]**; a shipped engine blends in a linear space with 1.0 tied to a reference luminance (Gran Turismo 7) **[partial]**: stated for its tone mapping, not its whole pipeline | The transfer at every level; one colour convention; the grey card; HDR | Every place's look moves at once; `SdfPhotometry.RangeThreshold` is a share of display white and lengthens light ranges unless restated; the sharpen assumes a 0 to 1 range |
| 5c. Declare the view display-referred by design and fix only the HDR branch | The filmic comment | The HDR double decode | The photometric decision: lights in lux, the frame in nits; a linear scene sample and an encoded pane sample still mean different things in one image |

**Favoured.** 5b, with the boundary stated at the edges of the graph and
not at one pass:

- **Every image a view or a source publishes declares its domain**: scene
  light (linear, in nits, exposed or not) or the working image (encoded on
  the sRGB curve, 1 at SDR white). A consumer declares the domain it reads,
  and a graph that joins two different domains without the conversion does
  not compose.
- **The conversion is the last stage of the view's own graph**, so no
  consumer can bypass it: bloom is applied there for every view (the two
  bloom-apply paths become one), then the authored transform, then the
  encode. A lone world, a split view, a filmed view and a view under a matte
  all publish a working image. The root's place pass only places and
  sharpens, and a pane's contract does not change.
- **A view consumed as scene light is not converted.** A window's view
  publishes unexposed luminance that the view looking through its glass
  exposes (`SdfViewSnapshot.Unexposed`), which is an edge from scene light to
  scene light. A filmed view shown on a screen is a working image, and the
  screen decodes it like any encoded picture before its nits multiply.
- **The transfer is the engine's and the curve is the author's.** The encode
  is required and has no parameter. The transform before it is authored data
  with a neutral default of none
  ([authored looks](#decided-by-evidence-authored-looks-and-the-owners-calls)).

**Strongest case against:** the whole debug world is tuned against the
present transfer, so it needs a full pass of the tour; a lone view gains a
pass it did not have; and no fetched source states the requirement in words.
The basis is the code's own disagreement and the transfer functions
`transfer.hlsli` already cites.

**Decided, by evidence.** One conversion, at the edge where scene light
becomes a working image, in the producing view's graph; panes, the HUD and
the display encode keep their contract. It rests on the photometric decision
([register](../decisions/rendering.md#lights-screens-and-the-camera-are-photometric))
and on correctness being the engine's ("a surface drawn black by a lighting
defect is a defect, not a style"). What a sample means is not a look, so no
preference for the present tones keeps the present transfer: an author who
wants that appearance writes it as a transform, and it is judged on SDR and
HDR. The filmic curve's constants in engine HLSL are a leak under "Authors
rule" and move into authored data in the same lane. Every exposure, tonemap,
bloom and material judgement waits for this fix, so it lands before the
layers migrate to programs (decision 13).

### Decision 6: one shadow answer for a light at a receiver

**What must be decided.** What a receiver reads for a shadowed light, so the
answer does not change with the cascade or the kind of view that gives it.

**What exists.** The first cascade that holds the point wins, with no band
**[opened]**
(`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-surface-stage.hlsli:254`):

```hlsl
        if (any(abs(plane) > 0.97) || (depth <= c.near) || (depth >= c.far)) {
            continue;
        }
```

The blocker search and the filter are sized in texels of whichever cascade
answers, `clamp(penumbra, 1.0, SdfMeshShadowMaxTexels)` with 14 texels the
cap, and a point no cascade holds returns 1.0 as a queried value
**[opened]**. At a 60-degree 16:9 view the near texel is 1.86 cm and the far
one 13.9 cm, so a penumbra is capped at 26 cm on one side of the boundary and
1.95 m on the other **[inference]**. The search's offsets lie on the shadow
map's plane, and each tap's depth is compared with the receiver's separately
(`sdfMeshShadowGap`) **[opened]**. The 26 cm is therefore a lateral reach: a
wide caster directly above a near receiver is found at any depth, while a
blocker whose edge lies more than 26 cm to the side on the light's plane is
not, which cuts the outer part of every penumbra wider than that
**[inference]**. Whether cascades are read at
all is one flag per view, and glass sets it false **[opened]**
(`sdf-light-stage.hlsli:189`, `sdf-glass-stage.hlsli:119`); session and camera
views draw no cascades. Every `Always` or `Auto` directional is a slot
candidate at `max(0, lux)`, and the order looks at the mode before the
luminance **[opened]** (`src/Puck.World.Client/WorldShadowAllocator.cs:97`):

```csharp
        if (left.Mode != right.Mode) { return (left.Mode == WorldShadowMode.Always); }
```

The day cycle's moon is `illuminance: 0lx, shadow: auto`, the debug key is
`shadow: always`, and `SdfMeshShadowCascades.Fit` checks only that a slot
holds a directional **[opened]**. So by day an unlit moon holds the second
slot and its 40 m cascade, and at night the unlit key holds the near and far
cascades **[inference]**; the reading at noon is owed. Slots are not roles.
The allocator keeps a selected light in the slot it already holds
(`WorldShadowAllocator.AssignTargets`), the environment writes that slot
number into the GPU table, and `Fit` returns no cascades when slot 0 holds no
directional, before it looks at slot 1 **[opened]**. So refusing a dark light
is not enough: through a handoff with both lights lit the moon can keep
slot 1 while slot 0 empties, and every cascade goes. Each cascade's sphere
sits on the view's forward axis, so a camera turn moves the far cascade's
centre and ends its hold, and glass shadows keep three optical buffers at
full resolution whatever the tier, 144 of the 252 MB a shadowed view holds
**[record]**.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 6a. A receiver contract in metres: a lateral search radius, an axial blocker gap and a filter radius, each from the light's slope and an authored maximum; a one-texel floor for antialiasing only; wide taps read from a pyramid of the same cascade; and a blend band | The PCSS deck names only a minimum filter clamp **[confirmed]**; a band tests both cascades and interpolates (Microsoft) **[confirmed]**, with no width given; the pyramid follows the ambient occlusion taps' own rule and has no outside source | The 7-fold jump in softness; the moving seam; the short lateral search | A pyramid tap darkens slightly; wide sparse filters lean on the views filter and the temporal resolve; a nearest-depth pyramid alone cannot say which body or peel layer supplied a tap, so each reduced texel carries its depth, body and layer together |
| 6b. A light is admitted by what it contributes, and cascade roles are assigned to admitted lights by name: zero luminance admits nothing, the primary role (near and far) goes to the brightest admitted directional whichever slot it holds, and a handoff fades between roles through the authored fade slots | Sun and moon are one global shadow system in a shipped game (Dybisz) **[confirmed]** | One whole cascade by day; the moon's coverage by night | The exchange at the horizon must not chatter; a light that keeps its slot through a handoff must still take the primary role when the other goes dark |
| 6c. A per-pixel, per-slot answer: "queried, no cascade holds it" takes the probes' share for that light | The register already rules that a view with no cascade takes the sun's visibility from the volume | Sunlit surfaces beyond coverage; nested views and glass become the same rule's degenerate case | Probe spacing at the edge of coverage; a cold volume darkens until it fills |
| 6d. Eye-centred, rotation-invariant cascades as one indexed set authored as radii, with far-field entries past 120 m held between refreshes | Each clipmap level covers twice the radius of the last (Unreal) **[unverified]**; distant shadows are separate and refreshed over frames (Dybisz) **[confirmed]** | A far hold that any camera turn ends; three named cascades in 132 references **[record]**; the terrain cascade as a fourth case | Coarser texels for narrow views; the far field needs sliced refresh or a stated lag at the authored day speed |
| 6e. The transmission term's identity peel in place of the exit layer | [Its plan](transmission.md); no outside source answers the question | A self-shadow term that failed three reviews; a doubled raster per cascade | 144 MiB against 96 MiB for three cascades; the Deck's budget is unmeasured |
| 6f. Glass shadow storage sized to the shadow tier and cleared only where glass casters project | Read from code; the additive optical depth is order-independent and is kept | Most of a view's shadow memory and a 48 MiB clear per cascade draw | A moving glass caster's new footprint must be cleared too |
| 6g. Cached or sparse virtual shadow maps, a per-frame projection refit, moment maps, traced sun shadows | [Systems considered](#systems-considered) | | Each fails on the moving sun, the wind or the baseline |

**Favoured.** 6b and 6c as one lane, since the probes' share in 6c must
follow the same light identity the role follows. The lane maps admitted
lights to roles explicitly and is checked through a continuous exchange in
both directions, not only at a cold seek to either end. 6a in two parts. Its
prototype is a bounded experiment behind a lever that leaves the shipped
receiver untouched: the metre-space filter compared with a brute-force filter
at one stop, with a deep blocker directly above and one displaced to the
side. Its production rewrite is one lane with the transmission plan's
`transmit-plane` step, which rewrites the same function, and follows
`transmit-identity`, which supplies the body table and the layers a wide tap
must read consistently
([transmission's steps](transmission.md#implementation-steps)). 6d after
decision 11's caster demand, behind a design memo. 6e by its plan.
**Strongest case against 6a:** nothing outside Puck shows a metre-space
contract over a depth pyramid, which is why the prototype comes first.

**Decided.** A light is shadowed by the cascade that holds the point and by
the probes where none does, per pixel, in every view and on glass, and a
penumbra's width is a function of the light and the gap alone. It rests on
the illumination plan's second decision and on
[the register](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material).
A light's softness is its authored `angularRadius`; the maximum penumbra,
the band and a far-field entry's refresh are authored values with neutral
defaults
([authored looks](#decided-by-evidence-authored-looks-and-the-owners-calls)).
Point and spot lights keep the probes' share until the illumination plan's
local shadow residency lands; this decision does not move it.

### Decision 7: lighting cost proportional to what changed

**What must be decided.** What each kind of change costs the volume: a
light's strength, its direction, a mesh detail swap, a lamp switched.

**What exists.** A probe's local part is reused only when two global
versions match **[opened]** (`src/Puck.SdfVm/SdfMeshIrradiance.Gpu.cs:307-308`):

```csharp
            if (!candidate.Trace && m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var previous) && previous.Shading is not null &&
                previous.Geometry == snapshot.Geometry && previous.LocalVersion == lighting.LocalVersion) {
```

So any scene change, a view-driven retier included, re-traces every probe's
shadow rays to every point light in range. The scheduler charges about
13,800 rays a fresh probe in the debug world **[reading]**, and that figure
is a prediction: `RelightRays` counts each light whose range reaches the
probe's origin times the hit count, while the kernel tests each hit's
material, distance, attenuation and facing before it asks for a ray
**[opened]**. Rays issued are uncounted. A light's range is the square root
of its weight times the brightest exposure any key reaches, over
`SdfPhotometry.RangeThreshold` (`SdfPhotometry.RangeOf`), the kernel tapers
the light to zero at that range, and a local light of zero weight is left
out of the capture **[opened]**. The exposure term is the likely reason most
of the world's point lights reach each probe. Each
residency owns a whole volume and its own budget: at the high tier a table of
about 72 MB in up to four copies and a hit cache of about 90 MB
**[inference]**, with the level table fixed in engine code
(`SdfMeshIrradiance.LevelsOf`). Every relight shades every hit of every
probe, though neighbours hit the same walls. The cubes hold absolute
radiance, so a change of colour or strength is a full relight and a fresh
convergence, which is why the view meters day after a night switch. The sky
map has three renderers: the device kernel; a CPU render that decides a
refresh, run when a procedural sky's packed inputs differ from the last
candidate's (`SdfSkyEnvironmentRefresh.Owes`); and a CPU render on the frame
thread for a lighting capture, run when the sky is an indirect source
**[opened]**. An image-backed sky is refreshed by its acquired picture's
publication and mapping, which the same decision watches apart from the
packed inputs.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 7a. Source-factored transfer: the relight stores light per unit of each source group, and publication combines the groups with the frame's current sources | Transport is linear in a source's radiance at fixed geometry, materials and support **[inference]**; one probe system's paper answers an abrupt change with a hysteresis heuristic (Majercik) **[partial]**, and a shipped one freezes lighting at 60 Hz (Apers) **[confirmed]** | A multiply for the edits that keep a source's support: a directional's strength and colour, the sky's uniform gain, a local light's colour; the stale night meter | A local light's strength is not such an edit today, because its range and taper follow its weight; two lamps in one group cannot be scaled apart; a change of the sky's shape is not a scale; a source at zero must still be relit per unit; a cube per group per probe costs memory that grows with groups |
| 7b. Shared hit lighting: each surface entry lit once, keyed from the exact cached hits | The illumination plan's step with its adoption bar; a screen-fed cache takes effect "only when they are in view" (Brixelizer) **[confirmed]**; a leak boolean in the key (GI-1.0) **[confirmed]**; 160 MiB at 2^22 elements (SHaRC) **[confirmed]** | Shadow rays and gathers per sweep, by the fold | The fold is unmeasured; error is bounded by measurement |
| 7c. Light visibility as geometry: one bit per light per entry, dirtied only by the segments a changed box crosses; reach sized by the live exposure floor | "We cache geometry here instead of lighting" (Lumen) **[unverified]**; a light-space tap at probe hits (Apers, slide 27) **[unverified]** | The whole-window relight after a detail swap; a recoloured lamp tracing anything | About 179 MB if stored per texel, so it needs 7b's entries |
| 7d. The light traces a tier no view moves: a lighting demand class resolved by the probe spacing of the finest level that reaches the group, which is the group's floor wherever the floor passes a lighting check | [Streaming decision 9](streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand); the owner's ruling that the trace reads a mesh "which never changes with the camera", which either tier meets | Relight waves as detail moves; level 0's last standing reader | A floor can cover a wall and still erase a recess or join two surfaces either side of a probe, so coverage does not qualify it for light; a third product for a group near a fine level |
| 7e. A coarse-to-fine drain: converge the far level, then the storey level, then the finest | None outside; a harness count decides | Whole-window passes from no bounce, with no history | Indoors a coarse start may be poor |
| 7f. One sky, rendered on the device and identified by its packed procedural inputs together with each acquired picture's identity, sampling transform and revision | None needed: a duplicate found by reading; the publication watch the refresh already keeps supplies the picture's half | Two CPU renderers, one on the frame thread | The refresh decision lags a readback; identical packed inputs can bind new pixels, so the key without the picture is wrong; a cold or absent picture must stay an explicit state |
| 7g. One lighting budget and one probe pool across residencies: floors first, the rest split by presented pixels, open-air probes at a coarse level's cadence, a moved window published slot by slot, the level table authored in the standard package | Probe sleeping saves 30 to 50% (Majercik) **[unverified]**; nested volumes share one atlas (Brixelizer) **[unverified]** | A full volume and budget per world shown; a cold area dark until its whole window is measured; a look in engine data | A small share starves without a floor; a slept probe lights a passing body with older light; a pooled volume may serve only viewers whose delivered projection is the same (decision 14) |
| 7h. Emitters integrated over their solid angle | No source was fetched | The hall's blotches under its ceiling strips | Not judged: it needs its own round |

**Favoured.** Decision 4b first, so GPU time is what a measurement reads.
Then 7f. Then 7a before 7b, so entries are built once per group. 7c after
7b, sized by the counted queries and hit-and-light pairs of decision 1 and
not by the predicted charge. 7d with the streaming plan's step 9. 7e on its
harness count. 7g with the job system's `Lighting` class and decision 4g.

7a is built for a stated class of edits:

- **A source group** is a set of sources that always scale together: each
  shadowing directional, the sky under one gain, and each local light alone
  unless an author binds several to one value.
- **Multiply-only edits** keep a group's support, attenuation, direction and
  materials. A directional's strength and colour, the sky's gain and a local
  light's colour qualify today.
- **A local light's strength** qualifies once its range is sized from the
  brightest value its authored strength can reach, as the exposure term
  already is, so the support is fixed and a dimmer or a switch is a scale.
  That is a bounded fixed-support basis: it lengthens some ranges, and its
  cost in rays and in probes reached is read before it is adopted.
- **Everything else invalidates.** A moved light, a changed cone or range, a
  sky whose shape changed, a material or a geometry change invalidates the
  entries it reaches, and those relight.
- **Memory is priced first**: groups times probes times a cube, at the debug
  world's light count and at a zone's.

**Strongest case against 7a:** nothing fetched applies it to a dynamic probe
volume, so its check is Puck's own. Cubes after an edit equal a from-scratch
drain's within `ConvergedShare` in four cases: a colour-only change, one of
two lamps changed, a light taken from zero to on, and a receiver at the old
range boundary of a brightened lamp.

**Decided.** A geometry change costs a trace, a direction change a relight,
and a strength or colour change a multiply where the source's support is
unchanged; a detail swap costs only the segments it crosses. It extends the
illumination plan's first decision by one level and keeps the drain a
function of its inputs.

### Decision 8: one projected-error budget and one tier chain

**What must be decided.** What decides the detail a consumer draws, and what
stands in for a group far away, in a cascade, in the light and on a miss.

**What exists.** Three constants in two rules **[opened]**: a tier is the
coarsest cell at most `SdfMeshTier.PixelShare` (0.5) of a display pixel at the
nearest demand point; a level is drawn where its error projects to at most
`SdfMeshDetail.CoarserPixels` (1), with `SdfMeshCollapse.CellShares` budgets
of a quarter of a cell and two cells. The two rules project differently: the
tier by Euclidean distance over the focal length (`SdfMeshDemand.PixelAt`),
the level by forward depth times the frustum-corner stretch
(`SdfMeshDetail.Of`). At its demanded tier a group's first level therefore
always qualifies and its second about half the time, so level 0 is drawn only
for boxes at the eye plane, membered and glass draws and groups clamped at
their finest rung **[inference]**. Its index and material words stay
resident, about 1.1 GB of a 3 GB region at 71 M triangles **[inference]**,
and the collapse that makes the other levels is 28.7% of bake CPU
**[reading]**. A shared prototype bakes once at its largest placement scale
and at the tier its nearest copy demands **[opened]**
(`src/Puck.World.Client/WorldRigidAssets.Static.cs:107`), so one fern beside
the player sets every fern's detail. The collapse stops at two cells, and
nothing stands in for a draw far away.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 8a. Keep both rules | None | Nothing | The overlap above |
| 8b. Count what is drawn; give tier and level one projection function with a host twin; then re-pitch, or land level 0's own words as a separate run | Sub-pixel error is imperceptible only "so long as the error is subpixel" under temporal antialiasing (Karis) **[confirmed]** | Three constants become one budget and its split | A re-pitch moves the look; level 0 cannot leave before 7d |
| 8c. A chain per geometry whose rungs are each measured against the field: the demanded tier's levels, then the kept floor, then the tiers other copies demand; each copy and cascade reads the coarsest rung qualified for its metric | Nanite picks a cut per cluster by its own projected error **[partial]**: the cut is a parent and child test, and the source does not say each instance selects its own; its root page is always resident **[confirmed]**; the coarsest tier of the debug world is about a twentieth of its finest triangles **[reading]** | Far copies and far casters drawing the nearest copy's tier; the collapse's floor for repeated things | A collapse's error is monotone only among simplifications of one contour (`SdfMeshCollapse.Levels`), and a level can be missing; a re-contoured tier starts from other geometry and other witnesses, and the floor's admissibility certifies coverage, not deviation. So nothing orders two tiers' errors until both are measured against one reference, and a coarse rung can keep a wall while moving an edge past the budget |
| 8d. A deeper collapse chain | Measured out (weighing, item 4) | | Topology never changes |
| 8e. Stand parents above copies | Virtual geometry D3 and G7 | Draw counts for unique arrangements | Unique geometry per arrangement; measured against 8c, not before it |
| 8f. A cluster hierarchy | Watched in virtual geometry D4 | Partly visible large draws | A second builder; no bar device is shown geometry-bound |
| 8g. Ground cover thinned by a stable rank per occurrence under an authored curve, with the ground's own program carrying coverage | Stochastic thinning breaks temporal history when done per pixel (Evans, slide 118) **[unverified]** | Shimmer and cascade load beyond the distance a blade resolves | Whole instances must leave, never per-pixel alpha; the default keeps everything |

**Favoured.** 8b's counters and one function now. 8c audited per tier before
anything is built (`puck mesh audit --cpu --tier`), then built with the
streaming plan's floor as the chain's first added rung. A rung enters a
chain only with a recorded deviation from the authored field, the one
reference every tier shares, and a record of what distance misses: a part
that contoured to nothing, a hole that closed, a feature lost. A consumer
takes a transition only when the coarser rung's recorded deviation fits its
budget in pixels or texels, and otherwise keeps the finer rung. 8e becomes a
measured exception over repeated arrangements. 8g after decision 11's table.
**Strongest case against 8c:** several tiers resident cost bytes, the audit
of every rung costs bake time, and a rung that fails keeps its group at a
finer tier than the chain promised. The budget is the library's one.

**Decided.** One budget in display pixels or texels, one projection function,
one chain of rungs qualified against the field. The kept floor is what a
miss shows, what a far cascade draws and where a far copy ends; the light
reads it only where decision 7's lighting check passes. It rests on the
owner's one-pixel and texel rulings
([register](../decisions/rendering.md#a-raster-pass-draws-only-what-it-can-show)),
on a derived product being a cache identified by its inputs, and on
"representations are chosen by measured cost". The budgets' values are
quality levers: the rulings' values stay the engine's defaults, and a quality
table may author others
([authored looks](#decided-by-evidence-authored-looks-and-the-owners-calls)).

### Decision 9: residency gives memory back and never shows a hole

**What must be decided.** Nothing new in design: the streaming plan's
decisions 4, 5, 7, 8 and 9 stand. The round decides what sizes them and what
it adds.

**What exists.** No group ever leaves a region; a refused or first mesh is a
hole; relief means landing a coarser tier beside the fine one; the region
grows by copy under about 4 GiB; root-camera demand is by distance alone, so
hidden detail holds bytes while visible detail takes the bias **[record]**.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 9a. The floor, eviction and observed retention on a backing that gives memory back, as the plan orders them | "Always having the root page means we always have something to render"; requests carry a priority and low-priority pages are evicted (Karis) **[confirmed]** | The hole; relief that costs memory; hidden detail outranking seen detail | A floor that contours a thin wall to nothing; feedback a few frames late |
| 9b. Arrival requested by the GPU's culling traversal alone | Nanite's own path | | A group that is not resident cannot report; a turn has no history |
| 9d. Refinement hints from resident geometry, beside the host's prediction: a drawn floor or parent reports that a finer tier would have been used, through the observation channel the plan already builds | [Streaming decision 7](streaming-and-memory.md#decision-7-prediction-decides-arrival-and-observation-decides-retention) keeps prediction for arrival and reads back what was used | Speculative refinement displacing detail in view, if it is measured | A readback a few frames late; it must never weaken floor admission |
| 9c. Shader-visible pages below the mesh, or slabs | The slab prototype's read cost about a fifth more on the surface pass **[reading]** | | A read in every vertex fetch |

**Favoured.** 9a. The round adds three things. Decision 8's counters come
first, because they decide how large a floor and a detail tier are. The
landing rate becomes a measured rate per device, as the bake's GPU share
already is. And demand leads along the camera program's bound and cancels
what leaves reach, which the plan already places in its step 10. 9d is held
as an experiment: it opens if overfetch or refinement latency is still
measured once observation decides retention.

Eviction is judged by physical bytes. Deleting a group's rows can free
nothing while a reader in flight or a live neighbour pins its block, and only
the contiguous fallback is enabled at this tree, so a count of evictions
proves no relief. The stage that lands eviction carries the streaming plan's
own acceptance
([its qualification table](streaming-and-memory.md#sparse-backing-design-for-review)):
peak and settled native bytes held, outstanding reservations and the last
reader's retirement through a real budget reduction, with a shared block and
the forced fallback, and a named obstruction when the budget cannot be
reached.

**Decided.** As the streaming plan decides, on the owner's ruling that a
group not landed or evicted "shows its coarsest level ... never a hole"
([residency](../decisions/residency.md#the-owners-rulings-on-look-and-policy)).

### Decision 10: the bake

**What must be decided.** What brings the cold bake inside its minute with
margin, and what closes the contour defects that block authoring.

**What exists.** The quiet bake reads just over the bar on both backends
(decision 4), and what bounds it under the recipe is unread (the weighing,
item 2). Land is 34% of its processor time, the collapse 28.7%
**[reading]**. Private memory passes 10 GB in the first minute **[record]**.
Each backend keeps its own store because the identity hashes the mask and
bake kernels' bytecode, with the CPU closure, an applied hot reload and the
entry format's version (`SdfMeshStore.IdentityOf`, `SdfMeshBakeIdentity`)
**[opened]**. Six of the fourteen blend exhibits
audit red, and the Moth's shin groups carry seven tenths of her flipped
triangles where a blade tapers to a rim thinner than the cell
([bake detail](open-items.md#bake-detail)). A grass blade part is a prism
that bakes at the 2 mm floor and draws about 474 triangles where its
polyhedron has about a dozen **[inference]**.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 10a. A contour rule for a cell that cannot resolve its faces: a crossing whose gradient is a blend gives no plane; a row beside a crease takes the crease's vertices; a face narrower than the cell closes to one crease or refines | The truncated-QEF placement is experimental, with no treatment of folds (Ju) **[confirmed]**; a manifold criterion "may still contain intersecting polygons" (Schaefer) **[confirmed]**; no source covers two creases in one cell | Red exhibits; locked places that block levels | More refinement costs bake time |
| 10b. Lossless lowering: drop collinear outline runs on meshes that abut nothing; lower a part exactly where its program proves a closed-form surface | Visibility V3a; no outside source | Blade triangles by more than ten-fold, in the mesh pass and every cascade | It helps flat or closed-form parts only |
| 10c. The front end of decision 4d | Decision 4 | The round trips | Its rank is unread: nothing isolates the takes' share of the bake under the recipe |
| 10d. The Land stage: refuse on the removed vertex first, one witness structure, the second sheet pass limited, chunks scheduled largest first on the one pool, storage a worker owns | A clustering pipeline fell from about 9 to under 3 minutes on the same class of fixes (Kapoulkine) **[unverified]** | At most the collapse's share; collections while arriving | Its rank is unread for the same reason |
| 10e. Contouring on the GPU | No source measures it | | A second mesher, and nothing shows the CPU is the bound |
| 10f. One store both backends read, keyed by a canonical identity of what derives a product | The packet's own caution: the backends' forest legs differ in aggregate triangle count, so "a shared identity alone does not halve the cold cost" ([bake proportional to change](open-items.md#bake-proportional-to-change)) | A second derivation of products another backend already derived | Identical HLSL can compile under different options or generated interfaces; a first writer can hide which backend derived what is drawn; and a warm hit from the other backend's store is not a cold bake |

**Favoured.** 10a and 10b now, since each stands alone. 10c and 10d are
both candidates, ranked by the critical path of decision 1's one run: worker
activity, per-take waits, GPU timestamps and the end-to-end interval, taken
together on a quiet machine with the display composing and an empty store.
Kernel edits land batched, because each moves the bake identity. 10f is
judged apart from the bar. The bar is each backend's own derivation from an
empty store, timed with the other backend's store absent; reuse across
backends is then proven in both directions by geometry, materials, seams and
appearance in motion, and an entry records which backend derived it.
**Strongest case against:** the bar is missed by seconds on the readings
there are, and 10a spends bake time on refinement; each lands with the
bake's seconds beside it.

**Decided.** One mesher, asked again where a cell cannot hold its features;
identity from what was authored; a cheaper collapse judged by the field audit
at each rendered level and never by equality (rule 4). A contour fix is
judged by more than its flipped count, which an empty or missing patch also
lowers: the audited groups stay non-empty, their `open` and `uncovered`
measures do not rise, and the seams are looked at from the sides that fail.
It rests on the one-minute ruling and on the one-contouring rule.

### Decision 11: one instance table and one classification

**What must be decided.** How draws reach the GPU when cost must follow what
a pass shows.

**What exists.** Every phase writes one command a candidate, "empty where it
draws nothing", over every staged row **[opened]**
(`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-occlusion.comp.hlsl:17-18`).
Only the far cascade gets the receiver mask and the two-phase test
**[opened]** (`src/Puck.SdfVm/SdfWorldPassRecorder.cs:898`), and its mask marks
every covered camera pixel though the surface pass reads it only where the
near cascade does not hold the point **[record]**. At the lobby the two
cascades cost 3.9 ms against the mesh pass's 1.7 ms **[reading]**. A membered
draw has no box (`SdfMeshBox.TryOf`) and draws level 0 in every pass. Draws
are non-indexed, three vertex invocations a triangle. Glass visibility is
decided twice, on the host and in the kernel.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 11a. Ordered compaction, then (geometry, level) buckets on a stable identity (visibility V1a, V1b) | Empty indirect draws "are actually not free": 2.97 ms to 0.9 ms over 125,000 culled objects on a GTX 1070 (Logdahl) **[confirmed]**; 250,000 objects in two indirect calls, a benchmark (Haar) **[confirmed]** | The walk in every phase of every view; commands that scale with population | Mostly unique geometry gains only the compaction; every identity consumer moves in the lane |
| 11b. Receiver demand admits casters in every cascade: one mark per view, a receiver marking every cascade texel any of its lookups can read | The far cascade's full refresh fell from 23,125 draws to 4,138 and from 8.5 ms to 2.9 ms at one stop **[reading]**; 1.5 to 2 times on a game scene on a 2010 GPU with CPU occlusion queries (Bittner) **[partial]**; the four-texel hierarchical test (Sakmary) **[confirmed]** | Far casters drawn for receivers that never read them; box-only near and second cascades | The mark starts from the opaque camera mesh (`sdfReceiverMark`), so it misses every reader this page adds: both cascades inside decision 6a's band, the blocker search's and the filter's footprint, both shadow slots, and glass, which has no opaque depth where a pane stands against sky. An off-camera caster that shadows only that pane is culled. The exit layer's and the counting fragment stage's shares are read before either changes |
| 11c. Hardware-indexed draws with the triangle taken from the primitive id, first over today's layout | No source measures it; a packed layout once cut the loaded region 34% and slowed the mesh and cascade passes, and was reverted **[opened]** (the commit that packed the attributes) | Vertex work per corner in every raster pass | The Deck's support for the primitive id is unverified; the packed layout returns only if indexed draws make the passes no slower |
| 11d. Boxes for membered draws from member boxes under live transforms (virtual geometry G3a, G3b) | The plan's own inference, to be proven by the posed audit | Characters and falls drawn everywhere at level 0 | A non-conservative box hides a limb |
| 11e. Rigid draws that read their slot, and the record split | About 2,000 records restaged a frame at one stop **[reading]** | Restaging per moved body | Attribute the 2,600 at rest first |
| 11f. One read of a residency's instance rows per frame, shared by its views; per-view and per-cascade lists and histories; the glass list from the kernel | No outside source; [visibility V5](visibility.md#v5-multi-view-and-shadow-work) | Dispatches, barriers and two host twins | Sessions do not map to residencies one to one: the boot world's sessions are views of its one residency (`WorldScreenBinder.FilmHomeViews`), and a routed session has its own. Only the invariant row decode is shared, since frustum, depth, phase and held history differ per view, and the late phase depends on the current depth |
| 11g. An early-depth variant, a packed target, a single-pass pyramid | The smaller buffer pays on bandwidth-limited parts (Burns) **[unverified]** | Fill and bandwidth, most on the Deck | Each is a measured test |
| 11h. Clusters inside a draw | Triangle and cluster culling gave under 10% of geometry time (Haar) **[unverified]** | Partly visible large draws | Admitted only by a bar-device measurement |

**Favoured.** Visibility's order stands: V2a and V2b ahead of V1a. 11b's
first step is the mask for the near and second cascades under today's
receiver rule, in which an opaque surface reads the first cascade that holds
it and glass reads none; 11d runs beside it. The mask is then a conservative
union by construction. The receiver stage and the marking kernel call one
function that returns every cascade texel a receiver can read (each slot,
each cascade in the band, the search and filter footprint), glass receivers
mark from the glass list, and a cascade with no mark yet (the first frame
after a cut, a view that has not rendered) keeps every caster. Decisions 6a
and 6c each land with the mask's extension for the reads they add. 11c lands
with V1b so the command format and both recorders change once. 11f after
V1b. 11g and 11h on their measurements.
**Strongest case against 11a:** recording is not most of the frame on the
RTX 4070, so the claim is scale and health, and compaction is measured apart
from grouping.

**Decided.** One table of instances with a stable identity, one kernel that
classifies it for every pass, and commands that equal what survives. It rests
on one implementation (every step extends `SdfMeshOcclusion`) and on a stable
depth-tie order.

### Decision 12: authored motion and populations at zone scale

**What must be decided.** How motion, placement and terrain stay authored
programs when a zone holds orders of magnitude more than the meadow.

**What exists.** A swing is interpreted from the document each frame, by
name (decision 2). Gust phase is a literal computed per blade from its tile's
origin, so each tile is its own prototype. A scatter becomes one placement
row per point at compile time. Terrain sectors bake at 2.8 m cells, not the
1.56 m they declare, because a scoped creation's lattice spans a sphere about
its origin; relief is capped at 4 m and nothing shadows past 120 m
**[record]**.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 12a. One motion program form, lowered once: a CPU evaluator, a GPU kernel for slots no rule or contact reads, one bound deriver, and per-instance inputs | Bounds computed from skinning matrices beat a vertex offset's (Nanite Foliage, experimental) **[confirmed]**; a per-instance wind vector and stiffness, with phase from world position (GPU Gems 3) **[confirmed]**; vertex wobbling is "painful if not impossible" for deep hierarchies (Remedy) **[confirmed]** | Per-frame interpretation; posing, journalling and restaging of eligible slots; a prototype per tile | CPU and GPU floats must agree closely enough for a pick; a loose swept box costs culling |
| 12b. Occurrence streams evaluated per residency cell as cached fixed-point buffers, each occurrence identified by what its compiled placement means | Placement generated on the GPU and copied back is deterministic and locally stable (van Muijden) **[unverified]** | A compiled document that grows with population | Ground projection per cell must be job work. A stable identity is not enough for a harvested plant: a cell evicted and derived again from the scatter alone grows it back, and an identity seeded by cell or by enumeration order changes when the same placements are spelled through another template or a cell is split |
| 12c. Terrain as source tiles of one relief series, with a far-field cascade | The streaming plan's seam rule; an erosion filter with analytic derivatives its author calls inaccurate **[unverified]** | Sectors as one tier; relief at prop scale | No terrain source was read: a terrain round precedes tiles at scale |
| 12d. Per-frame generation by work graphs; generated blades; vertex-offset wind | | | Not on the baseline; a second representation; unbounded motion |

**Favoured.** 12a in two stages (the lowering and its CPU evaluator, then
the kernel). 12b after decision 11's table, in two layers. An occurrence's
identity is derived from the compiled placement's meaning, so two spellings
of one scatter give the same identities in any generation order. What play
changes (a plant harvested, a thing moved) is sparse authoritative state in
the simulation, keyed by that identity and applied over the stream; the
per-cell buffers stay a cache that any eviction may drop. 12c by the terrain
pass's order, after its round.

**Decided.** The engine accelerates the authored program and derives its
bounds; it writes no grass, wind or terrain path in C#. It rests on "Puck
code over engine code" and "Ergonomic is optimal": a tile placed sixteen
times, or a scatter spelled once, costs what it means.

### Decision 13: shading and the image

**What must be decided.** How many shading paths exist, and what the
temporal history and the visibility record cost.

**What exists.** The programs' lever defaults off, "the temporary
programs-versus-layers measurement lever" **[opened]**
(`src/Puck.World.Client/WorldRenderSettings.cs:34`), so the default frame
shades with the fixed layers. Views is the largest pass, 3.7 ms at the
lobby; its kernel runs at 255 registers on the RTX 4070 under Vulkan, and the
probe gather is about a third of it **[reading]**. The temporal history is
`SdfWorldPackage.HistorySurfaceWords` (47) words a pixel, 188 bytes, in one
copy per frame slot; 40 of them are an eight-phase window rewritten whole
each frame **[record]**.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 13a. One shading path: the bar devices' matrix, bins per program only where a device misses, then the layers deleted | Building per-material tile worklists cost "no more than 10 % of frame time" in that paper's scenes (Burns) **[confirmed]**. The figure is list construction only. The paper's remark on tile size is about its separate 16 by 8 lighting kernel **[partial]** and gives no threshold for a material dispatch or for Puck's 8 by 8 tiles | Two implementations of surface shading; terrain and weather materials | No reading off the RTX 4070; list build, dispatch and shade are unmeasured apart, under one material and under mixed ones; the owner decides if no column passes on a device |
| 13b. The history split: the phase window in one in-place buffer, only the reprojected words per slot | No source covers an in-place history | About 370 MB at 1280 by 900 **[inference]**; 35 of 40 window words no longer rewritten | A cross-frame barrier, designed and reviewed first |
| 13c. A planar visibility record | The same change took the history surface off the L2 bound | Strided reads in resolve, filter and composite | Views reads every field; keep whichever layout a reading favours |
| 13d. The gather attributed, then evaluated once per view at a decoupled rate | No source transfers: outside systems that shade below full rate also denoise | A third of views; the repeat in glass | Stair-stepping at visibility edges |
| 13e. The meter on a bounded sample grid shared with the screen-emission reduction | A shipped title meters a reduced image **[unverified]** | Two reductions; a cost that grows with resolution | Drop it if the meter compiled out saves under a tenth of a millisecond |

**Favoured.** 13a after decision 5, so the migration's captures are judged
once; bins or a tile size are chosen from the three phases measured apart on
the bar devices. 13b behind its design. 13c, 13d and 13e each on its
reading.

**Decided.** One shading path, with the plan's own rule for choosing it. It
rests on one implementation and on "Puck code over engine code".

### Decision 14: nested views, glass and water

**What must be decided.** What a view through a window, a screen or a pane
of glass costs, and what it may differ in from the same scene seen directly.

**What exists.** Two identical constants turn ambient occlusion and all
cascades off for every session and camera view at any size
(`WorldSessionSceneEmitter.ReducedQuality`,
`WorldScreenBinder.CameraViewQuality`) **[opened]**. A window's image is
sized by its slab's projected bounds, and any corner at or behind the near
plane makes it the full image **[opened]**
(`src/Puck.World.Client/WorldPortalVisibility.cs:65`). When one glass box is
in the frustum the glass passes clear `SdfWorldPackage.GlassSumWords` (16)
words a pixel **[opened]** and shade the whole grid **[record]**. An eye
inside a glass solid sees no tint, and the game design names a dive
**[record]**.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 14a. A view no consumer reads costs nothing; a world's demand is its consumers' floors first and its presented pixels second | Decision 2; the streaming plan builds a residency's lighting when the selector demands its world ("First sight is too late a deadline"); a screen lit by external content keeps lighting its room live (the owner's ruling) | The hidden render; filming unseen sessions | Presented pixels alone cannot tell an idle unseen world from one that owes a first publication behind an approaching portal, or from a screen out of view whose glow lights a visible room; picking and the trace are consumers too |
| 14b. Fit each window to the part of its glass the consumer shows | Read from code; no outside measurement | A full-extent view for a sliver; under-sampling before a crossing | A sliding rectangle swims unless it snaps to a lattice on the glass |
| 14c. One authored view quality sized by presented pixels, in place of the two constants | The owner's ruling that a visible trade-off switches only where barely perceptible, by its size on screen | A lighting and antialiasing pop at every crossing; a look built into the engine twice | A tier change that swaps the fragment rebuilds the view's graph |
| 14d. A still feed stands: extend the freshness predicate the runtime already has | The runtime already lets a view's last render stand (`RenderGraphRuntime.WithUnchanged`, the residency's last-rendered signature, the temporal history's convergence rule) **[opened]**, so a camera that has converged on a still room may stand today | Whatever cause keeps one named still feed rendering, once it is attributed | A frozen scene is not enough while lighting converges, the temporal window fills, or an external picture or its mapping changes; a reused publication must also honour who it is delivered to |
| 14e. Glass work in proportion to visible glass: glass draws tested against opaque depth, tile occupancy, binned shading, a sparse clear | Occupancy-driven cost in a shipped transparency system **[unverified]** | A full-image clear and shade for one pane | One more atomic a fragment; a Vulkan device loss near these passes is unattributed |
| 14f. The glass raster's facing from the vertex stage; no atomics that add zero | Read from code | Three position resolves a fragment | A repack risks the exact sums |
| 14g. The eye inside a medium, from one field query per view | No source covers it | Untinted water from below | The waterline must not pop |
| 14h. Weighted or moment transparency, per-pixel lists, stencil portals, planar reflections, layered multi-view | | | They approximate sums Puck has exactly, or add a second nesting mechanism |

**Favoured.** 14a with decision 2. The one selector
(`SdfResidencySelector`) aggregates what each consumer asks of a residency
and of a publication: a predictive arrival floor, an indirect dependency
such as a screen's light, picking and the trace. It counts work several
consumers share once, and only then divides discretionary quality and rate
by presented pixels. 14b and 14e as lanes of their own.

14c in two steps. The two constants turn shadows and ambient occlusion off
for a nested view even when it fills the screen, so merging them into one
constant would keep a visible choice in engine code for every author. The
first step deletes both and reads an authored table instead: the engine's
default restricts nothing, and the standard package authors what a nested
view gives up at each presented size, which a world overrides. The second
step, after decision 6c makes a view with fewer cascades degrade per pixel,
adds what the table needs to switch without a pop.

14d starts from an attribution and not from a new mechanism: name why one
still feed renders today, change that cause, and count renders. An external
frame, an exposure change and newly converging lighting must each still wake
the view. 14f and 14g after 14e.

Reuse honours the recipient. A portal session can be delivered frames, a
redacted projection or a replica, and "Revocation changes the recipient view
immediately" ([worlds](../architecture/worlds.md)). Two audiences at one
camera, world and tick can be permitted different pixels. A republished
frame, a shared probe volume or any publication reused across consumers is
therefore keyed by the projection it was derived from and that recipient
view's generation wherever they affect what is visible, and is shared only
between consumers whose authorized inputs are identical. This is a contract
decisions 7g and 14d must carry; no leak has been reproduced.

**Decided.** A nested view is the same view at another size: its floors come
from its consumers, its extent, rate and quality follow its presented pixels
through one authored table, and its shadow answer is decision 6's. It rests
on the frame graph's decision that views are instances ("Layered views
return only if the counts call for them",
[register](../decisions/rendering.md#the-frame-graph-and-nesting)) and on
the owner's ruling that glass is a surface of its own after opaque
visibility.

### Decision 15: presentation

**What must be decided.** How a produced frame reaches the display, what the
overlay costs, and how a machine's picture and a screen's light enter the
frame.

**What exists.** On Vulkan the acquire and the fence wait run after produce
with a zero timeout, and a full swap chain makes the outcome a skipped
present **[opened]** (`src/Puck.Vulkan/VulkanFramePresenter.cs:16`):

```csharp
    private const ulong FrameAcquireTimeoutNanoseconds = 0;
```

The two backends pace by different shapes, and the ring depths differ per
layer. The overlay's header states its cost: "every pixel walks every
submitted record" **[opened]**
(`src/Puck.Overlays/Assets/Shaders/overlay-unified.frag.hlsl:1`), about
0.9 ms with the benchmark panel up **[reading]**. A machine's video is
uploaded once per completed tick **[opened]**
(`src/Puck.Hosting/Graph/MachineVideoSourceUpload.cs:137-139`), and the debug
world ticks at 30 Hz. The machine's output returns the sequence of the frame
it wrote (`IMachineVideoOutput.WriteFrame`); the upload reads only whether
that is positive and advances a stamp of its own on every write **[opened]**
(`MachineVideoSourceUpload.Write`), so an unchanged frame is uploaded again
under a new stamp. Every pixel walks every bound screen as a light with
no reach and no visibility term **[record]**. Only Windows implements the
display-timing and precision-wait seams **[opened]**.

| Option | Evidence | Closes | Fails at |
|---|---|---|---|
| 15a. One record per present, joined by present id: the attempt, the submit, the display outcome where the host gives one (shown, dropped, unknown), the signal it came from, and queue depth | The present wait "lacks important details such as the latency of the present operation itself" **[confirmed]**; the present-timing proposal relies on a second present-id extension, and whether the bar devices' drivers expose either is unread **[partial]** | Judder an even loop interval hides; the unexplained frame rate | A true display time on Vulkan needs an extension the bar devices may lack, so its absence is a reported capability with a named follow-up, and the record is checked against a compositor trace where one exists |
| 15b. One admission contract: on a valid surface the host is admitted to a queue slot before it renders, on both backends, so it renders no frame it has no slot for | "Your rendering loop should wait for the swap chain to signal ... before it begins rendering every frame" (Microsoft, archived) **[confirmed]**; one or two more buffers than frames queued (NVIDIA) **[confirmed]** | Work thrown away under backpressure; two pacers alternating | Admission cannot promise display: a compositor may supersede a submitted frame and a surface may be lost. Image count, the CPU's lead and how long a GPU reader keeps a resource are three limits, each derived from its actual readers; the vendor guidance cited here already separates buffers from queued frames. A wait for a window that never displays stays bounded and leaves the host serving ticks, uploads, input and shutdown |
| 15c. The overlay as bounded, clipped geometry blended in place, text from an atlas texture | The UI decision; one bilinear sample and a median (Chlumsky) **[unverified]** | Cost by pixels times records; a second full-resolution image | The diagnostic focus treatment reads the scene |
| 15d. Machine frames shown at the machine's cadence through a presentation-time queue; the machine's own frame sequence, with a source's replacement or reset, decides every downstream refresh | Read from code, unconfirmed on a running World | A scrolling cartridge at half its rate; uploads of unchanged frames | Audio must follow the same clock; no display shows every machine frame once at every rate, since a 60 Hz machine repeats frames on a 120 Hz display and skips some on a 45 Hz one, so the contract is the latest eligible frame at a bounded age with repeats and skips counted |
| 15e. Screens join the one light selection, with reach from luminance and area and visibility from the probes | No verified source | Glow through walls; a walk that grows with screens bound | A 32-bit mask at hundreds of screens |
| 15f. Linux display timing, a precision waiter and a display request | No source was read | An unpaced Steam Deck | Needs a research pass and the hardware |

**Favoured.** 15a first; it is the benchmark's stage 8 with the outcome
added, and it counts production avoided, submits accepted and display drops
observed as three figures. 15b once decision 2 lets frames fit the period.
15c on the UI plan's step 2, after its measurement. 15d after a capture
confirms the half-rate display. 15e with decision 7c's reach. 15f with a
Deck. What a fixed-refresh display shows when the loop cannot hold its rate
(a steady whole-refresh divisor, or frames as they arrive) is a setting the
mechanism serves either way
([authored looks](#decided-by-evidence-authored-looks-and-the-owners-calls)).

**Decided.** It rests on "Puck plays nice with its host" (waits through the
host's primitives, no polling as a steady state), on
[the UI decision](../decisions/ui.md#drawing-screens-and-mods) and on the
machines decision that neither visibility nor presentation cadence controls a
machine's advancement: which frame is shown is presentation, never
simulation.

## What the areas share

Each row is one job the code does in several places. The decision named
leaves one.

| One job | Today | Decision |
|---|---|---|
| A reduced quality for a nested view | Two identical constants in two projects | 14c |
| Rendering the sky map | The device kernel, a CPU render to decide a refresh, a CPU render per lighting capture | 7f |
| Deciding what a pass can show | The kernel, a host twin (`SdfMeshCull`, `SdfMeshBox`) that glass and rates still read, and a light-plane projection written out five times in HLSL | 11f |
| The sun's visibility at a point | Cascades in some views, a probe bit in others, chosen per view | 6c |
| Detail by projected size | Three constants and two projection formulas | 8b |
| Background budgets | A pass-pixel budget, a wall-time GPU share, per-frame relight credits, a fixed cost per tap, a host share per service | 4g |
| Frames in flight | The graph's 3, the tables' ring of 2, the Vulkan presenter's 2, the Direct3D 12 swap chain's 3, the work service's 4 slots | 15b, 4e |
| Knowing a submission finished | Fences aliased to a batch's owner, beside the device timeline | 4e |
| CPU workers | The bake's pool, the lighting's loops, thread-pool tasks, the job pool | J1 |
| Keyed range placement | The probe scene's arena and the mesh region's | The lighting packet |
| A colour's meaning | Linear for shading, decoded for keyed blends, encoded for sources and the encode | 5b |
| A tone curve | The root's filmic constants, the cel style's curve, the cel program's shoulder | 5, as authored data |
| Applying a view's bloom | The root's place pass for a placed view, and the view's own `bloom-apply` pass for a filmed one | 5b |
| Surface shading | Programs and fixed layers | 13a |
| A motion's value and bound | An interpreter by name, the audit's fixed-point pose, a measured reach and a measured speed | 12a |
| A mesh store | One per backend | 10f |
| An image reduced to a statistic | The composite's histogram and the screen emission's grid | 13e |

**Stale text found while reading**, each fixed by the step that touches it
or by the next documentation sweep: the rendering skill and the mesh-bake
reference size the mesh region at 1.75 GB; the mesh-bake reference and the
handbook give the levels' yields as 11% and 6%; the kernels reference says
the glass clear zeroes eight words a pixel where `SdfWorldPackage.GlassSumWords`
is 16; the illumination plan's rejection table says the cascades take 48 MiB;
`SdfMeshIrradiance`'s summary still says the views pass multiplies the sky
shares by the live sky; the streaming plan cites a spin wait the code no
longer holds; the lawn's page says each static row bakes on its own; and the
two open items named in the weighing's items 1 and 8. The bake packet's
header (item 2) is unsettled, not stale, until the critical-path run.

## Puck-native combinations

| Combination | What it buys | Where it fails |
|---|---|---|
| One tier chain, measured against the field | The floor is a tier of the same geometry, baked by the one contouring and identified by its inputs, so the miss surface, a far cascade's casters and a far copy need no authored proxy and no second derivation; the light's trace geometry is the same floor wherever it passes the lighting check. Every rung has one reference, the authored field, which a mesh-only engine does not have | A floor that contours a thin part to nothing; a floor that covers a wall and still loses the recess the light needs; a switch between tiers that shows on thin geometry |
| The field is there at bake time | A cell that cannot hold its features is asked again; a crossing root is a loop the kernel closes; a closed-form part is lowered exactly because the program names it; error is measured against the field | The CPU twin disagrees with the device below its resolution; every kernel edit rebakes |
| Exact, view-independent probe hits | Entries keyed from exact hits leave no cold surface after a teleport or through a portal; visibility is geometry, dirtied by what changed; a drain is a function of its inputs | The fold is unmeasured; a factored sky is exact only for a uniform change |
| Consumers' floors, then presented pixels | Portals and nested views are worlds, so the selector knows a residency's consumers and their share of the picture before the frame: floors cover what is owed before a pixel shows, and extent, rate, quality and budgets then follow one number | Pixel share alone starves a world that owes a first publication or lights a room from out of view; measured prices can hunt |
| Programs with derivable bounds | Motion, placement and shading are declarative, so a swept box, a cell's population and a pixel's cost are derived, never guessed | Mixed creations stay on the CPU; a loose bound costs culling |
| Publication identity | A machine frame's sequence, a view's change stamp and a sky's packed inputs with its acquired pictures decide every downstream refresh, and a publication carries the projection and recipient-view generation it was made for | A wrong "unchanged" is a frozen picture; a reuse that ignores the recipient shows what was withdrawn |
| The receiver mask from the visibility record | One structure serves caster culling, the occlusion-aware rate floor and the admission reading for sparse shadow storage | A disocclusion since the mask was built |

## The build order

The round's order by dependency, as the review corrected it. Each stage's
steps are filed in the plan named in its decision, and
[open items](open-items.md#next) ranks them among everything else. Every
check is read on Direct3D 12 and on a windowed Vulkan boot at
`world.quality high`, in real time on the benchmark's route, under the one
recipe; a check that claims the frame bar reads the slowest frame and names
the bar devices it was read on. No stage depends on a path only stronger
hardware runs.

From stage 1 on, a check states the dividend its change claims and what
would show it false, on the same moving route, with the work counted beside
the time: instances visible and processed, draws and dispatches, bytes held
and the slowest frame. Filing a cause for a slow frame is a reading; it
closes no stage.

| Stage | Lands | Needs |
|---|---|---|
| 0. Readings | Decision 1's table and each candidate's step 0; no behaviour changes | A quiet window; one GPU leg on the RTX 2060 and the Steam Deck |
| 1. What the player sees everywhere | Decision 5's conversion in the view's graph, with the filmic constants moved to authored data; decision 6's light admission, cascade roles and per-pixel fallback as one lane; decision 4's headroom policy on the one budget, then the probe jobs in flight and the grant per second; decision 10's contour rule and lossless lowering; the nested views' quality as an authored table (decision 14c's first step) | Stage 0's GPU split, for the headroom policy; the lead's reference pass of the tour after decision 5 |
| 2. The frame thread and the round trips | Decision 2's snapshot, schedule, bounded correction and state by view; the allocation cuts; the control plane's bounded waits (C0), then the pool and its owned fork (J1), then decision 3's partitions (T3, T2); decision 4's device list and early hand-off; the bake's first cut, whichever the critical path ranks (the front end from one root rule, or the Land stage on the pool); decision 7's one sky and the factored transfer for edits that keep support; `transmit-identity`, then decision 6's receiver contract with `transmit-plane`; decision 11's masks as a conservative union, and member boxes | Stage 0's breakdown and critical path; stage 1's headroom policy, before the device list raises occupancy |
| 3. One table, one chain | Buckets with indexed draws and slot-resolved records; the shared row read per residency and the kernel's glass list; decision 8's function, counters and field-qualified chain; the streaming plan's backing, floor and eviction, and the lighting demand class; decision 7's shared hits and visibility bits; the bake's second cut, if a reading shows it pays; one store, judged apart from the bar; the one shading path; the cascade set's design | Stage 2; the shader matrix's bar-device legs |
| 4. Scale | Decision 12's kernel, occurrence streams with their identity and authoritative changes, and tiles; decision 6's far-field entries and the remaining transmission lanes; decision 14's demand floors, fitted windows, size-keyed quality, freshness and glass; one lighting pool and one priced GPU budget, keyed by recipient; decision 13's history split; decision 15's admission, overlay, machine cadence and Linux seams | Stage 3; the terrain round; a Steam Deck for the Linux seams |

Stage 1's lanes are independent and run together. In stage 2 the extract and
the probe list are independent of each other; C0, J1 and the partitions are
serial; and `transmit-identity` precedes the receiver contract, which is one
lane with `transmit-plane`. In stage 3 the buckets, the indexed draw and the
slot-resolved record change the command format and the record once. Stage
4's items are independent except where the table names a need.

### Stage 0 is done when

- A `puck benchmark` run on each backend reports, per chapter: produce by
  part, summing to `host.frames`' produce-work within 5%; captures and
  prepares by what asked for them; triangles by level per pass; each
  cascade's occupied cells; and the GPU frame split into the root view,
  nested views and background work.
- The displayed interval is reported per present wherever the host gives
  one, joined by present id with its signal named, and on Windows it agrees
  with a compositor trace of the same presents, as the benchmark's stage 8
  requires. A device and backend pair that gives no display time says so in
  the report, with the follow-up that would supply it. The report then says
  why the frame rate reads as it does, or that it cannot yet.
- The same readings exist for each bar device and backend pair at the meadow
  and the lobby.
- One cold bake under the recipe gives the critical path that ranks decision
  10's cuts.
- The night-switch repro and the meadow report shadow queries issued and
  hit-and-light pairs tested.

### Stage 1 is done when

- **The conversion.** A grey card reads 118/255 within 3 codes under its
  manual EV through every path a view takes: a lone world, a split view, a
  filmed view shown on a screen, a view beside a pane, and the scRGB path,
  where a float capture shows a patch authored at N nits at N nits. A graph
  that joins scene light to a working-image input without the conversion
  fails to compose. The dividend is one meaning for a sample; any path where
  the card reads differently shows it false.
- **Shadow roles.** At noon `world.counters gpu pass sdf.world$shadow-second`
  shows no draws. Through a continuous exchange of sun and moon, forward and
  reversed, no frame is without cascades, a capture shows a shadow beyond
  40 m while the sun's slot empties, and each end matches a cold seek to it.
  A capture across the 120 m edge shows no hard line and no sunlit interior.
- **Lighting latency inside headroom.** On the night-switch repro
  `level-0-age-p50` stays under the authored age at 60 and at 30 FPS, with a
  full ring and with a lighting change in the middle of a batch, on both
  backends. In the same window the root frame's slowest frame is no slower
  than before the change; an age that falls only by taking frame time shows
  the dividend false. The lawn stops metering as day.
- **The contour.** `puck mesh audit` reads no flipped triangle on the
  fourteen blend exhibits, with every audited group non-empty and its `open`
  and `uncovered` measures no higher than before, and about a dozen triangles
  a blade part. The blend seams and the moving Moth's rim are captured from
  the sides that failed.
- **Nested quality.** One lit exhibit reads the same as the root view,
  through a full-size portal and through a camera, and an author's override
  of the table changes the nested views alone. `puck search` finds neither
  constant.
- The Valley, Entrance and Night chapters show each of these in a presented
  burst.

### Stage 2 is done when

- **The extract.** Captures counted by cause show none that no scheduled
  render, owed upload or floor read, and a scratch World holding extra
  unseen residencies with no floors reads the same begin-frame time and the
  same capture count. A turn that reveals two levels of windows, in a frame
  that releases nothing, shows the first level in that frame and the second
  behind its fallback for one counted frame. A capture count the reorder
  leaves unchanged shows the dividend false.
- **The pool before the partitions.** A console verb issued while a tick's
  join is overdue answers within one iteration. Through a cold bake the
  process's threads are counted as pool workers, the frame and control
  threads, and dedicated threads, each against the job memo's figure. The
  slowest frame of the Meadow and Entrance chapters is stated with its cause
  before and after, and the tick's share of it falls.
- **Lighting by what changed.** A change of only a directional's colour or
  strength, or of the sky's gain, relights no probe and matches a
  from-scratch drain within `ConvergedShare`. So do one of two lamps changed,
  a light taken from zero to on, and a receiver at a brightened lamp's old
  range boundary. A panorama that moves under unchanged sky parameters
  refreshes the sky, and an unchanged procedural sky under a moving camera
  does not.
- **The bake.** `puck bench startup --bake` under `puck lease quiet` reads
  under a minute on each backend from that backend's own empty store, with
  the cut the critical path ranked first landed. If the front end lands, a
  group takes about four round trips and its roots pass bracket, residual
  and thin-feature checks against the authored field on both backends.
- **The receiver contract.** A walk through the cascade boundary shows one
  penumbra width either side and no seam. A deep blocker directly above and
  one displaced to the side both shadow as the brute-force filter does. A
  thin occluder beside a wrapped back face holds in motion on both backends.
- **Caster demand.** With culling on and off, moving captures agree for
  backlit glass against sky, across the band and on the first frame after a
  cut. Commands executed equal survivors in every raster pass.

### Stage 3 is done when

- **The table.** Commands sit near the visible (geometry, level) pairs with
  picks, motion and portal straddles intact, and the route's counts of
  visible and processed instances and of dispatches, read before and after,
  show commands falling with what survives. Two opposite-facing home windows
  and a routed window each read their rows once per residency.
- **The chain.** Walking up to one tree changes no far tree's triangles.
  Thin and offset contours hold through each tier transition in eye and
  shadow views, and a rung whose recorded deviation fails the consumer's
  budget is not taken.
- **Residency.** A cold arrival burst shows no frame with a missing surface.
  Through a real budget reduction, settled native bytes held fall to the
  budget once the last reader retires, with a shared block and the forced
  contiguous fallback exercised; where they cannot, the report names the
  pinned floor or the fragmentation. Eviction and retier counts are read as
  diagnostics.
- **The light's tier.** With the eye moving and probe demand held, a lit
  exhibit and a thin partition keep their light. A retier wave rebuilds no
  trace scene, and trace rebuilds are counted apart from lighting error.
- **One shading path.** The `world.shade` lever is gone and the migrated
  tour reads as its references in motion. List build, dispatch and shade are
  read apart, under one material and under mixed ones, on each bar device.
- **One store.** Each backend's cold bake is timed from its own empty store,
  and reuse across backends is shown in both directions by audit, materials,
  seams and captures in motion.
- The slowest frame of every chapter is read on all three bar devices and is
  no slower than at stage 2's close on any chapter. Each frame over a
  device's bar has a named cause filed in open items.

### Stage 4 is done when

- **Motion at scale.** A scratch meadow at four times the tiles, with the
  added tiles populated and on the route, reads a pose-pack time that
  follows the animated instances in view and not the tiles placed; both
  counts are in the report.
- **Populations.** A belt at sixteen times its area boots inside the bake
  and memory bars. A harvested occurrence stays harvested through eviction,
  re-entry and a replay, and two spellings of one scatter give the same
  identities.
- `valley-rim` shows ridge shadows with the bar held under a moving sun.
- **Floors.** A nested world deliberately overloaded leaves the main world's
  lighting age unchanged. In the same run an approaching portal's
  destination has its first publication before its first pixel, and a
  changing screen out of view still moves the light of a visible room.
- **Recipients.** Two sessions at one pose with different projections show
  different pictures. One is restricted while its camera is held, and its
  next publication, lighting included, reflects the restriction.
- **Still feeds.** A named still feed that renders today stands once its
  cause is fixed, by a count of renders, and an external frame, an exposure
  change and newly converging lighting each wake it.
- **Admission.** Under sustained backpressure, minimize and restore, and
  swapchain recreation on Vulkan, production avoided, submits accepted and
  display drops observed are counted apart; no frame is rendered without a
  slot, and the host keeps serving ticks, input and shutdown.
- **Machine cadence.** A scrolling test cartridge, at display rates below,
  equal to and above the machine's, shows the latest eligible frame at a
  bounded age against the audio clock, with repeats and skips counted and no
  upload of an unchanged frame.
- On the Steam Deck the display line names a signal.
- The slowest frame of the route holds 16.7 ms on the RTX 2060 and the RTX
  4070 and 22.2 ms on the Steam Deck.

## Invariants the order rests on

Each row is a property a step above depends on, the one place that enforces
it, and where that guarantee stops. Most are not enforced yet, and the row
says so; the step that builds the mechanism builds the enforcement with it.

| Invariant | What enforces it | Where the guarantee stops |
|---|---|---|
| Scene light is converted exactly once before a working-image consumer reads it (decision 5) | To build: every graph port declares its domain and composition refuses a mismatch, with the conversion in the producing view's graph. Nothing enforces it at this tree | The values a kernel writes inside a declared domain, which the grey card through each path covers |
| Every cascade texel a receiver can read is marked, or the cascade culls nothing (decision 11b) | To build: the receiver stage and the marking kernel call one function for the set of reads, and an unmarked cascade keeps every caster. At this tree the far cascade's mark covers its one kind of reader | A disocclusion since the mask was built, which the culling comparison in motion covers |
| A lit, admitted light holds the primary cascade role (decision 6b) | To build: one resolver assigns roles, and `SdfMeshShadowCascades.Fit` reads roles instead of slot 0 | The fade between roles, which is authored and is checked by the exchange walk |
| A reused publication was derived from the projection its consumer may see (decisions 7g, 14d) | To build: the reuse key's type carries the recipient view's generation, so no reuse can be written without it. Nothing enforces it at this tree, and it stays under adversarial review until a check covers it ([hazards](../development/hazards.md#silent-invariants)) | What the authority chooses to deliver, which is [disclosure](disclosure.md)'s |
| A group in reach always has a surface to draw (decision 9) | The streaming plan's floor, built by its decision 5 | A floor that fails admissibility, which keeps a finer tier |
| A lighting-converged frame's light is a function of its scene, lighting and window (decisions 4, 7) | A frame the graph withholds drives its background work instead of pumping it under budgets (`SdfWorldResidency.FrameWithheld`); the headroom policy and the grant per second must stay out of that drive | Presented play, where budgets apply and the lighting's age varies |
| A query that replaces shared probe state runs alone, and a batch's commit copies only rows that batch wrote (decision 4b) | The unmerged lane's one admission door (`SdfMeshProbeGpuWork.TryTake`), by its own account | A running World, where the lane has not been measured |
| The crossing-root rule has one definition (decision 4d) | To build with the front end: both evaluators derived from one source. At this tree the rule exists on the CPU only | Floating-point differences between evaluators, which the bracket and residual checks measure |
| An occurrence's play state survives its cell's eviction (decision 12b) | To build: changes are simulation state keyed by the canonical identity, and a cell's buffer is a cache | An identity that changes with spelling, which the equivalence check covers |

## Decided by evidence, authored looks and the owner's calls

Decided here, by the code, a reading or a recorded ruling, with no owner
call: readings first (1); the extract by snapshot and schedule (2); the tick
ahead of produce, built in the job memo's order (3); background work as
device chains budgeted against measured headroom (4); one conversion at the
edge from scene light to the working image, and one colour convention (5);
the receiver contract, light admission by role and the per-pixel fallback
(6); what each lighting change costs, for the edits that keep a source's
support (7); one projection function and one chain qualified against the
field (8); the streaming plan's order with its additions and its physical
acceptance (9); the contour rule and exact lowering, with the bake's other
cuts ranked by one run (10); the table, the conservative mask for every
cascade and the indexed probe (11); the motion program form and occurrence
streams with a canonical identity (12); one shading path (13); consumers'
floors before pixel share, the fitted window and glass by coverage (14); the
present record and admission (15).

### Authored looks

A look value is an authored parameter with a neutral engine default
([AGENTS.md](../../AGENTS.md#puck-is-pre-release), "Authors rule" and "The
Puck standard is authored"). The standard package (`worlds/standard/`)
chooses the values it wants and any author overrides them. The debug world's
values are its artists' to set, judged against the references by the lead
and the look lanes; the owner corrects a look and is asked to choose none.
For each look value this page touches, the table names the surface that
carries it. Where the state says "to build", the engine work is the
mechanism and its neutral default, never the value.

| Look | Authored surface | State | Engine default |
|---|---|---|---|
| The display transform: the tone curve and its constants | `render.tonemap` (`WorldTonemap`) | A switch exists, naming one curve whose constants are in engine HLSL. To build with decision 5: the curve as authored data or a program in the standard package, chosen per world and per camera | None: the encode alone |
| Exposure: the manual value, the bounds, the compensation, the meter's band, percentiles and centre weight, the adaptation time and the local share | `render.exposure` (`WorldRenderExposure`) | Exists. The value, the bounds, the compensation and the hold bind to a literal, a state row or keys on a clock | `SdfExposure.Default` |
| Exposure per place | The same members, bound to a state row that a world's rules write on entering a place, as the gallery's inspection lever pins its hold | Exists for a stepped change. To build: a blend across a seam (a doorway, a portal) as authored data | No per-place behaviour |
| A light's softness | A directional's `angularRadius` (`WorldRenderLight.Directional`), bindable | Exists | The pinned penumbra |
| The widest penumbra and the cascade blend band | Members of `render.shadowCoverage` (`WorldRenderShadowCoverage`) | To build with decision 6a | The light's own geometry, uncapped, and a band one filter footprint wide |
| Shadow reach: the near, far and caster distances, and later the cascade radii | `render.shadowCoverage` | Exists as three distances. To build with decision 6d: the radii as one authored set | The schema's defaults |
| A far shadow's lag | A refresh interval or an angular step on a far-field cascade entry | To build with decision 6d | No lag: an entry refreshes as its light moves |
| Which lights shadow, and how a handoff fades | A light's `shadow` mode; `render.shadowLights`, `shadowFadeSlots`, `shadowFadeTicks` and `shadowOverflow` | Exists | One slot and an instant handoff |
| The night | The sky layers, the lights and the exposure bounds keyed on a day clock, as `worlds/debug/night.puck` and `worlds/debug/day.puck` author them | Exists. [The register](../decisions/rendering.md#lights-screens-and-the-camera-are-photometric) rules that how night reads is the author's | No night look |
| Indirect light: the tier, the source gains, the bounces, the lighting grid, the relight and trace allowances and the lighting age | `render.indirect` (`WorldRenderIndirect`) | Exists. With decision 4 the allowances' unit becomes work a second. To build with decision 7g: the probe levels (`SdfMeshIrradiance.LevelsOf`) as authored data | The Medium tier and unbounded allowances |
| The quality presets | `render.low`, `render.medium` and `render.high` (`WorldQualityPreset`), authored in `worlds/standard/quality.puck` | Exists | No presets |
| What a nested view gives up at each presented size | A table in the quality presets | To build with decision 14c, in place of `WorldSessionSceneEmitter.ReducedQuality` and `WorldScreenBinder.CameraViewQuality` | No restriction |
| The detail budgets: a tier's share of a pixel, a level's error in pixels and its hysteresis | Members of the quality presets | To build with decision 8b, in place of `SdfMeshTier.PixelShare` and `SdfMeshDetail.CoarserPixels` | The recorded rulings' values |
| Ground cover's thinning with distance | A curve on a scatter | To build with decision 8g | Everything is kept |
| Bloom, sharpening, the cel style and the atmosphere | `render.bloom`, `render.upscaleSharpness`, `render.toon` with the materials' styles, and `render.atmosphere` | Exist | Off or absent |
| What a fixed-refresh display shows when the loop cannot hold its rate | A presentation setting beside the quality presets, which a player's option overrides | To build with decision 15b, over `PresentPacingPolicy` | Frames as they arrive |
| The back of a portal's glass | A property of the portal | To build. Decision 14b's facing cull applies where the authored back shows no image | No image |

No step of the build order waits for a look to be chosen: a mechanism lands
with its neutral default and with the standard's value beside it. After
decision 5 every place is judged again against its reference, because the
compensation each place authors today stands in for the missing encode.

Open items still lists several of these values among the owner's sittings
and calls: the exposure calibration, the outdoor lighting direction's shadow
softness, the terrain pass's compensation, the tonemapping and
exposure-per-place entries awaiting review, and the back of a portal's
glass. Sweeping those entries to match this section is open work for the
lead, since it changes the owner's list on another page.

### The owner's calls

The owner's list holds engine behaviour and owner-only actions. None of
these blocks a step above except the hardware.

1. **Whether a frame of latency ever returns** (decision 3, option 3c). It
   comes back only on the reading decision 3 names, and it would reverse a
   recorded ruling.
2. **The interpreter restricted to edit time** on a device where no column
   of the shader matrix passes. The author-shader plan sends that to the
   owner with the readings (decision 13).
3. **A recorded ruling's engine default.** The one-pixel level, its
   hysteresis and a cascade's texel rule are the owner's rulings and stay the
   engine's defaults. An author's quality table may differ; the default
   moves only on the owner's word, with captures from every side
   (decision 8).
4. **The transmission plan's recorded calls and the Steam Deck's budgets**,
   each with the design's choice first
   ([the plan's calls](transmission.md#the-owners-calls)). The plan's Deck
   leg consumes them (decision 6e).
5. **The RTX 2060 and the Steam Deck**, for stage 0's device legs and stage
   4's Linux seams: the readings need the hardware.
6. **The corrected order.** The owner agreed to the summary's priorities
   before the review, and compares the order above with the draft's
   ([what changed in the build order](#what-changed-in-the-build-order)).

Two recorded calls gate nothing here. The streaming plan's questions (how
pressure feels on a turn, what a character shows at its floor) "have
defaults and block no step" ([open items](open-items.md#owner-decisions)).
Feet inside session scenes changes how much field work a session scene owes,
and no step waits for it.

## What would change the answer

- **The produce breakdown names a part no decision covers.** Decision 2's
  order follows the breakdown, not this page.
- **Tick and produce each fit the period and their sum does not**, after
  decision 2. Option 3c returns with a measured hand-off, as the owner's call.
- **The measured headroom is below the lighting's floor.** If the GPU frame
  alone misses the bar once the CPU stops binding, the lighting gets its
  floor and no more, and the views pass (decision 13d) and the cascades
  (decisions 6 and 11) lead it.
- **The cold bake's critical path is its processors and not its takes**, or
  the reverse. The cut on the path lands first (decision 10), and the other
  waits for a reading that shows it pays.
- **A cube per source group costs more memory than a zone has.** Decision
  7a narrows to the directionals and the sky, and local lights keep the
  relight.
- **A bar device reads differently.** A Steam Deck bound on vertex work moves
  10b, 11c and the far cascade's floor tier ahead of the table; one bound on
  bandwidth moves 11g and 13c up.
- **The shared-hit fold is under four to one.** 7c then needs another store
  (compressed rows), and hardware ray query is measured sooner.
- **The floor fails the lighting check for most groups near a fine level.**
  The light's tier is then a third product there, its bytes are priced
  against the budget, and the streaming plan's option C is the fallback.
- **The floor fails its admissibility on thin geometry.** The far cascade
  keeps the collapse's coarsest level for those groups.
- **A rung's deviation from the field costs too much bake time to record.**
  The chain stops at the floor, whose coverage check the bake already makes.
- **Per-copy tiers pop on thin parts in motion.** The chain stops at the
  floor, and stand parents and the aggregate curve carry the far field.
- **Indexed draws do not speed the passes on any bar device.** The packed
  layout does not return, and clusters lose their cheapest precondition.
- **The metre-space filter cannot match a brute-force reference** within the
  views filter's residue. The band stays and the pyramid is replaced by more
  taps in the near cascade only.
- **A capture shows machine frames are not dropped** at the debug world's
  tick. 15d reduces to the unchanged-frame answer.
- **An author wants the present appearance.** It is written as a transform
  over the corrected transfer, in the world or the standard package, and
  judged on SDR and HDR. The working image's convention changes only with
  every producer and consumer together.
- **A bar device's driver gives no display time.** Its displayed interval
  stays an absent column, and the frame bar on that device is read from the
  loop's interval with that limit stated.

## Research the round leaves owed

- **Terrain.** No terrain source was read. Clipmap and chunked terrain,
  seams between tiers of dual-contoured meshes and height-field hybrids are
  weighed against the overlap rule before tiles are built at scale.
- **Small and area emitters, and many lights.** Solid-angle or polygonal
  integration at probe hits, light range from emitted luminance, and
  clustered selection, for decisions 7h, 7c and 15e.
- **Linux and the Steam Deck.** Presentation-time and tearing protocols, the
  compositor's limiter, timer wake jitter and the driver's present wait.
- **Display timing on the bar devices.** Which present-timing and present-id
  extensions each device's driver exposes, read from the device, before the
  present record is designed around either.
- **The temporal history.** Karis's notes and the survey's body, before the
  window's layout or sample count changes.
- **A shared store across GPUs.** Floating-point reproducibility of the bake
  kernel across vendors, before one store is judged.
- **GPU root search and compaction.** Nothing read measures them.
- **Nested views in shipped engines** and transparency with bounded memory,
  if decision 14 leaves a crossing frame near two views' cost.

## Systems considered

Each decision extends a mechanism Puck has; none adds a second. The scheduler
of views (`RenderGraphScheduler`) takes residency capture as one more rated
item. The one background service (`SdfGpuWork`, `SdfGpuWorkBudget`) keeps
every job kind, and the probe client (`SdfMeshProbeGpuWork`) already chains
a cache kernel, a compaction and an indirect dispatch. The one classification
door (`SdfMeshOcclusion`) takes compaction, buckets, every cascade's mask and
the glass list. Demand and tiers stay `SdfMeshDemand` and `SdfMeshTier`,
under `SdfResidencySelector` and `MemoryAccount`, with products in
`SdfMeshLibrary` and `SdfMeshStore`. The rate tiers (`SdfUpdateRateTiers`),
the sight seam (`SdfPortalSight`) and the field answers (`WorldFieldAnswer`)
serve unseen work. Shading keeps `SdfShadeProgram` and the bins of
`SdfShadeDispatch`, which glass reuses. Static sharing stays
`WorldRigidAssets` and moved transforms `SdfMovedTransforms`. Waits stay
`HostWaits`, workers `JobPool`, completion `GpuQueueTimeline`, pacing
`PresentPacingPolicy`, the frame's timeline `HostFrameTimeline`, and colour
conversion `ImageSourceConversion`.

Considered outside and not taken, with what would reopen each:

| Technique | Verdict | Reason | What would change it |
|---|---|---|---|
| Virtual shadow maps with cached pages | Reject (virtual geometry D8 stands) | Light motion and deforming casters invalidate cached pages; the day clock and the wind run by default; the one account of a moving sun could not be fetched | Static redraw with unchanged projections measured dominating a cascade |
| Sparse shadow pages without caching | Deferred to a reading | The culling half is the receiver mask already; the storage half is unmeasured | The occupied share of a cascade's cells measured small |
| A per-frame projection refit, cascade scrolling, static slices for the sun, baked time-of-day visibility | Reject | Each gives up the snapped, held fit or assumes a still light or a fixed world | Nothing while the clock is authored |
| Moment, variance and exponential shadow maps | Reject | Bleeding across storeys. For translucent casters the moment paper's experiments blend sorted geometry and name unsorted alternatives such as stochastic transparency **[unverified]**, so ordering is one implementation and not a requirement; Puck keeps its additive optical depth because that sum is already exact in any order, and each alternative adds cost or noise | A concrete deficiency in the optical-depth path on layered glass |
| Traced or field-marched sun shadows | Reject at the baseline | A field march in the frame, or a second implementation with no stable baseline to match | Decision 6 landed and a measurement on the RTX 4070 |
| A cluster hierarchy with GPU traversal; a simplifier library as a dependency | Watch | A second builder beside the one contouring; weak on aggregates; Puck is independent work | Virtual geometry G9's measurement |
| Mesh shaders, work graphs, device-generated commands | Enhancement only | Not on the RTX 2060, the Deck or both backends | The baseline holds and the path is measured to pay, with a fallback |
| A compute rasterizer | Deferred | Hardware rasterization is the qualified baseline. The cited benchmark's depth-only variant runs on mobile parts with 32-bit atomics **[unverified]**, so a portable subset exists; a visibility id with depth, clipping, depth ties and Puck's cost on it are unqualified, and no pass is shown bound on what it would replace | A bar device measured bound on small-triangle raster, with the atomic width or other feature the chosen variant needs named |
| Arrival requested by the GPU alone, pages below the mesh, slabs | Reject | Decision 9: a group that is not resident cannot report | Nothing |
| Refinement hints from resident geometry | Held as an experiment | Decision 9d: prediction keeps arrival and turns, and the hint rides the observation channel | Overfetch or refinement latency still measured once observation decides retention |
| Transition cells between tiers, geomorphing | Reject | Both tie a tile to its neighbour's tier or add a position per vertex | Pops shown in motion after decision 8 |
| Contouring or placement on the GPU; gradient-free contouring | Reject; watch | A second mesher; only an abstract was read | The paper read in full |
| Attribute quadrics, a triangle cap | Reject | They cannot model creases; detail follows projected size | Nothing |
| Screen-fed or reservoir radiance caches and per-pixel gathers, as systems | Reject; ideas taken | Per view, stochastic or history-dependent, which breaks the drain | A sparse cache whose cold behaviour is measured no worse |
| Hysteresis, random rotation, moment visibility, probes switched off, frozen lighting, a warm-started drain | Reject | The relight is a fixed quadrature; stale light is the defect | Nothing |
| Hardware ray query for probe rays | Deferred | Decision 7 removes most of the rays it would speed | Shadow walks still dominating after 7b |
| Ray-traced supersampling of edges; cone-traced supersampling; exposure-aware resolve weights | Reject; not judged | Needs hardware ray tracing; an unread paper; no source | A shimmer on a bright edge in a moving capture |
| A visibility record of 4 to 8 bytes | Reject | It moves work into a kernel that is register-limited | Nothing |
| A bloom threshold, GPU-resident exposure, image-space local tonemapping, glare, a default grade table, fused post passes | Reject or deferred | The settled bloom decision; host consumers of the exposure; halos; a grade or a glare is a look, which an author adds as data and the engine does not default | A measured need for the mechanism; an author's program for the look |
| Weighted, moment or voxel transparency; per-pixel lists; a two-layer deep buffer | Reject | They approximate sums Puck has exactly or need unbounded storage | Many overlapping translucent layers |
| Stencil portals, layered multi-view, planar reflections, a screen-space march on glass as baseline | Reject or gated | A second nesting mechanism; the water plan's gate | A crossing frame near two views' cost |
| A tick pump or a pipelined tick | Reject as a frame-time fix | Decision 3 | Decision 3's reading |
| Parallel recording now, a submission thread, fibers, collector mode switches | Held or reject | Its admission rule; single-threaded contracts; the host rule; a stopgap beside a cause fix | A record sum of several milliseconds |
| Per-frame generation, generated blades, vertex-offset wind, impostors, a learned field, height-field terrain as the engine's | Reject | Decision 12; a second representation | A terrain round's evidence |
| Outline text as the baseline, compute binning for 2D, an oblique near plane, texel shading, a vendor latency kit, exclusive fullscreen | Reject or later | Costlier at small sizes; no evidence read; a second mechanism; one vendor; it takes the display from the host | Large text in the world; a measured need |

## The review

A reviewer from another model family read the synthesis against the code,
findings only and with no GPU measurement. Its host stopped it for low memory
after it had written thirty-five findings and four passages it found sound,
and it is run again on this page. Each finding was checked in the current
source before it changed anything. All thirty-five hold. "Fixed" means the
page changed where the last column says; one finding is fixed with part of
the original step kept, and the reason is given.

| Finding | Severity | What it found | Disposition | Where |
|---|---|---|---|---|
| RR-01 | High | The proposed output boundary is optional: a lone view, a pane and a filmed view each bypass or double it | Fixed | Decision 5: the domain is declared at graph edges and the conversion is in the producing view's graph, checked through five paths |
| RR-02 | High | A geometry floor can pass its silhouette check and still fail lighting | Fixed | Decisions 7d and 8: probe spacing selects the light's tier, and the floor is shared where a lighting check passes |
| RR-03 | High | Caster demand omits receivers the plan itself introduces | Fixed, first step kept | Decision 11b: the mask is a union by construction. The near and second cascades' mask stays an early step, because at this tree an opaque surface reads the first cascade that holds it and glass reads none, so no reader exists that the opaque mark misses |
| RR-04 | Medium | The 26 cm limit is lateral | Fixed | Decision 6: lateral reach, axial gap and filter radius are stated apart, with a check for each failure |
| RR-05 | High | The job pool lands after the work that requires it | Fixed | Decision 3 and stage 2: C0, then J1, then the partitions; threads counted by kind |
| RR-06 | High | The budget that higher GPU occupancy needs lands two stages late | Fixed | Decision 4: a headroom policy on the one budget, in stage 1, before any step raises occupancy |
| RR-07 | High | The proposed lighting basis changes when lamp strength changes | Fixed | Decision 7a: source groups, the multiply-only class, a fixed support for local strength, memory priced first, four comparisons with a fresh drain |
| RR-08 | High | Schedule-first extraction depends on a freshness query that already extracts | Fixed | Decision 2: a snapshot before the schedule, a bounded correction, state keyed by view |
| RR-09 | Medium | A stable view already republishes its last output | Fixed | Decision 14d: the existing predicate is extended after one feed's cause is attributed |
| RR-10 | High | Rejecting a dark light need not promote the lit one | Fixed | Decision 6b: roles assigned by name, the exchange walked in both directions |
| RR-11 | High | The shadow receiver rewrite precedes the identity resources it reads | Fixed | Decision 6a and stage 2: the prototype behind a lever, the production rewrite after `transmit-identity` |
| RR-12 | High | The unmerged lighting lane still grants work per frame | Fixed | Decision 4b: the built batch commit and the unbuilt grant per second are separate steps |
| RR-13 | Medium | The sky's cache key omits the acquired image | Fixed | Decision 7f, and the three renderers restated as conditional paths |
| RR-14 | Medium | Sessions do not map one to one to residencies | Fixed | Decisions 1 and 11f |
| RR-15 | Medium | Sharing a store can turn the second backend's cold run into a warm hit | Fixed | Decision 10f: each backend's bar is timed from its own empty store; the claim that the bar is met was wrong and is removed |
| RR-16 | High | The cold bake's ordering rests on a different display state | Fixed | The weighing's item 2 and decisions 1 and 10: one run under the recipe ranks the cuts |
| RR-17 | Medium | A predicted ray charge is reported as executed work | Fixed | Decision 7 and decision 1's reading |
| RR-18 | High | Stage 0 requires a display observation a host may never give | Fixed | Decisions 1 and 15a and stage 0: a capability, or an absent column with its follow-up |
| RR-19 | Medium | Admission, queue depth and physical display carry different guarantees | Fixed | Decision 15b |
| RR-20 | Medium | The machine-cadence check requires impossible frame rates | Fixed | Decision 15d and stage 4 |
| RR-21 | High | A coverage-qualified tier has no error bound across tiers | Fixed | Decision 8c: every rung measured against the field |
| RR-22 | High | The eviction check can pass without releasing physical memory | Fixed | Decision 9 and stage 3 |
| RR-23 | Medium | The GPU bake would create a second authoritative root rule | Fixed | Decision 4d: one source for both evaluators |
| RR-24 | Medium | Two non-blocking owner questions became stage prerequisites | Fixed | The owner's calls, and each stage's needs |
| RR-25 | Medium | The tile-size result is from a different kernel | Fixed | Decision 13a: the inference is removed and the three phases are measured apart |
| RR-26 | Medium | The moment shadow paper permits unsorted implementations | Fixed | The outside techniques table |
| RR-27 | Medium | Compute rasterization is classed by the wrong capability | Fixed | The outside techniques table, as its own row |
| RR-28 | High | Present pixel share cannot express every consumer of a world | Fixed | Decisions 2 and 14a: consumers' floors first |
| RR-29 | Medium | A successful contour check can hide lost surfaces | Fixed | Decision 10 and stage 1 |
| RR-30 | Medium | Stage 1 still hard-codes a visible nested-view policy | Fixed | Decision 14c's first step, in stage 1 |
| RR-31 | Medium | Attribution alone lets a slower stage pass | Fixed | The build order: a dividend and its falsifier per check, and no chapter slower at stage 3's close |
| RR-32 | Medium | Predictive arrival and GPU refinement requests can coexist | Fixed | Decision 9d |
| RR-33 | High | Sharing a publication must respect the recipient view | Fixed | Decision 14 and the invariants |
| RR-34 | Medium | A harvested occurrence needs authoritative state outside the cell cache | Fixed | Decision 12b |
| RR-35 | Medium | The transfer convention is treated as an owner's look choice | Fixed | Decision 5 and what would change the answer |

The four passages the review found sound are unchanged: decision 3's tick
order and latency ruling, the stale occlusion premise (the weighing's
item 8), decision 14's diagnosis of nested views, and the two outside
figures kept in their original scope (Apers's ray budget and Cyberpunk's
distant shadow refresh).

### What changed in the build order

The order of the stages is the summary's: readings, what the player sees,
the frame thread and the round trips, the table and the chain, then scale.
What moved between them:

- **Stage 0** gained the display outcome as a capability or an absent column
  for each device and backend pair (RR-18), captures counted by cause
  (RR-08), the GPU frame's split (RR-06), one cold bake that gives its
  critical path (RR-16), counted shadow queries (RR-17) and sessions named
  by residency (RR-14).
- **Stage 1** gained the headroom policy, which the draft had in stage 4
  (RR-06), ahead of the probe jobs, now two steps (RR-12). The colour
  boundary moved from the place pass to the view's graph and is checked
  through five paths (RR-01, RR-35). Slot admission became cascade roles in
  one lane with the per-pixel fallback (RR-10). "The two quality constants
  made one" became the authored table, from stage 4 (RR-30). The contour
  check gained coverage (RR-29). Its look pass is the lead's (RR-24).
- **Stage 2** gained the pool, from stage 3, between the control plane's
  waits and the partitions (RR-05), and `transmit-identity`, from stage 4,
  ahead of the receiver contract (RR-11). The extract gained its snapshot
  (RR-08), the factored transfer narrowed to edits that keep support
  (RR-07), the sky's key gained its pictures (RR-13), the caster masks
  widened (RR-03), and the bake's front end stopped being the stage's fixed
  cut: the critical path picks between it, built from one root rule, and
  the Land stage (RR-16, RR-23).
- **Stage 3** lost the pool and takes whichever bake cut stage 2 did not.
  Its chain is qualified against the field
  (RR-21), its eviction by physical bytes (RR-22), and the light's tier is
  the lighting demand class (RR-02). One store is judged apart from the bar
  (RR-15), shading's phases are measured apart (RR-25), and no chapter may
  be slower at its close (RR-31).
- **Stage 4** lost the headroom policy, the quality table's first step and
  `transmit-identity`. It gained consumers' floors (RR-28), reuse keyed by
  recipient (RR-33), occurrence identity with authoritative changes
  (RR-34), the freshness attribution (RR-09) and restated admission and
  machine-cadence checks (RR-19, RR-20). "The owner's reviews" left its
  needs (RR-24).

### What the review did not reach

The reviewer left no account of what it had read, so this list is inferred
from where its findings and its four sound passages point. No finding and no
"sound" stands against the parts below, which is an absence of review and
not agreement:

- Decision 1's readings, apart from the display row and the device matrix.
- Options 4c, 4e and 4f.
- Options 6d, 6e and 6f.
- Options 7b, 7e and 7h, and 7c and 7g beyond the one point each drew.
- Options 8b, 8e, 8f and 8g.
- Options 10a, 10b and 10d as designs; only their checks and their order
  drew findings.
- Options 11a, 11c, 11d, 11e, 11g and 11h.
- Options 12a and 12c.
- Options 13b to 13e.
- Options 14b, 14e, 14f and 14g.
- Options 15c, 15e and 15f.
- [What the areas share](#what-the-areas-share),
  [the research owed](#research-the-round-leaves-owed), most rows of the
  outside techniques table, and the source lists.
- Everything the corrections added: the snapshot, the domain declaration,
  cascade roles, the headroom policy, the source-group class, the
  field-qualified chain, the authored looks and the invariants.

## Sources

The decisions cite a source by its first author or its product; each list
below gives the title and the address it was read at. A source is listed as
confirmed when the verification pass found in it at least one quotation this
page uses. That is a statement about the quotation: nine of those sources
also carried a claim the pass narrowed, and the narrowing is stated where
the claim is used.

**Confirmed.**

- Karis, Stubbe and Wihlidal, "A Deep Dive into Nanite Virtualized Geometry", SIGGRAPH 2021 Advances: <https://advances.realtimerendering.com/s2021/Karis_Nanite_SIGGRAPH_Advances_2021_final.pdf>
- Tatarchuk, "Destiny's Multithreaded Rendering Architecture", GDC 2015: <https://advances.realtimerendering.com/destiny/gdc_2015/Tatarchuk_GDC_2015__Destiny_Renderer_web.pdf>
- Wright, Narkowicz and Kelly, "Lumen: Real-time Global Illumination in Unreal Engine 5", SIGGRAPH 2022 Advances: <https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Lumen-Wright%20et%20al.pdf>
- Apers, "Shipping Dynamic GI on Frostbite", SIGGRAPH 2024 Advances: <https://advances.realtimerendering.com/s2024/content/EA-GIBS2/Apers_Advances-s2024_Shipping-Dynamic-GI.pdf>
- Leblanc and Conte, "Raytracing the World of Assassin's Creed Shadows", SIGGRAPH 2025 Advances: <https://advances.realtimerendering.com/s2025/content/Advances%202025%20-%20Raytracing%20the%20world%20of%20Assassin's%20Creed%20Shadows.pdf>
- "GI with AMD FidelityFX Brixelizer", GDC 2024: <https://gpuopen.com/gdc-presentations/2024/GDC2024_GI_with_AMD_FidelityFX_Brixelizer.pdf>
- "GI-1.0: A Fast Scalable Two-Level Radiance Caching Scheme": <https://ar5iv.labs.arxiv.org/html/2310.19855>
- NVIDIA Spatial Hash Radiance Cache, integration guide: <https://github.com/NVIDIAGameWorks/Spatial-Hash-Radiance-Cache/blob/main/docs/Integration.md>
- Majercik and others, "Scaling Probe-Based Real-Time Dynamic Global Illumination for Production": <https://arxiv.org/html/2009.10796v3>
- "Advanced Soft Shadow Mapping Techniques", NVIDIA, GDC 2008: <https://developer.download.nvidia.com/presentations/2008/GDC/GDC08_SoftShadowMapping.pdf>
- "Cascaded Shadow Maps", Microsoft: <https://learn.microsoft.com/windows/desktop/DxTechArts/cascaded-shadow-maps>
- Dybisz and Witanowski, "Shadows Optimizations in Cyberpunk 2077", SIGGRAPH 2021 Talks: <https://history.siggraph.org/wp-content/uploads/2022/06/2021-Talks-Dybisz_Shadows-Optimizations-in-Cyberpunk-2077.pdf>
- Logdahl, "27,000 dragons and 10,000 lights": <https://logdahl.net/p/gpu-driven>
- Haar and Aaltonen, "GPU-Driven Rendering Pipelines", SIGGRAPH 2015 Advances: <https://advances.realtimerendering.com/s2015/aaltonenhaar_siggraph2015_combined_final_footer_220dpi.pdf>
- Sakmary, "Virtual Shadow Maps", thesis: <https://dcgi.fel.cvut.cz/en/theses/2025/sakmamat/>
- Burns and Hunt, "The Visibility Buffer", JCGT 2(2): <https://jcgt.org/published/0002/02/04/paper.pdf>
- Lottes, "Advanced Techniques and Optimization of HDR (VDR) Color Pipelines", GDC 2016: <https://gpuopen.com/download/GdcVdrLottes.pdf>
- "Driving Toward Reality: Physically Based Tone Mapping and Perceptual Fidelity in Gran Turismo 7", SIGGRAPH 2025: <https://blog.selfshadow.com/publications/s2025-shading-course/pdi/s2025_pbs_pdi_slides_v1.1.pdf>
- Ju, Losasso, Schaefer and Warren, "Dual Contouring of Hermite Data": <https://www.cs.wustl.edu/~taoju/research/dualContour.pdf>
- Schaefer, Ju and Warren, "Manifold Dual Contouring": <https://www.cs.wustl.edu/~taoju/research/dualsimp_tvcg.pdf>
- meshoptimizer release notes: <https://github.com/zeux/meshoptimizer/releases>
- Remedy at REAC 2024: <https://enginearchitecture.org/downloads/REAC_2024_Remedy.pdf>
- "Nanite Foliage", Epic: <https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-foliage>
- GPU Gems 3, chapter 16, "Vegetation Procedural Animation and Shading in Crysis": <https://developer.nvidia.com/gpugems/gpugems3/part-iii-rendering/chapter-16-vegetation-procedural-animation-and-shading-crysis>
- Bevy pull request 17413, two-phase occlusion culling: <https://github.com/bevyengine/bevy/pull/17413>
- "Reduce latency with DXGI 1.3 swap chains", Microsoft (archived): <https://learn.microsoft.com/en-us/previous-versions/windows/apps/dn448914(v=win.10)>
- "Advanced API Performance: Swap Chains", NVIDIA: <https://developer.nvidia.com/blog/advanced-api-performance-swap-chains>
- The `VK_EXT_present_timing` and `VK_KHR_present_wait2` proposals: <https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_present_timing.html>, <https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_present_wait2.html>

**Partial**: the source holds the words but says something narrower.

- Sloan, Kautz and Snyder, "Precomputed Radiance Transfer for Real-Time Rendering in Dynamic, Low-Frequency Lighting Environments": <https://www.cs.jhu.edu/~misha/ReadingSeminar/Papers/Sloan02.pdf>
- Wihlidal, "Optimizing the Graphics Pipeline with Compute", GDC 2016: <https://gdcvault.com/play/1023109/Optimizing-the-Graphics-Pipeline-With> (the page holds an overview; the slides' text was read from an archive, and its indirect-execution quotation could not be reached)
- Bittner and others, "Shadow Caster Culling for Efficient Shadow Mapping" (draft): <https://www.cg.tuwien.ac.at/research/publications/2011/bittner-2011-scc/bittner-2011-scc-draft.pdf>
- "Distance Field Soft Shadows", Epic: <https://dev.epicgames.com/documentation/unreal-engine/distance-field-soft-shadows-in-unreal-engine>

**Read, and not checked word for word**: through a summarizing fetch, a text
extraction or an abstract, or under a candidate the pass did not reach.

- Geometry, bake and streaming: Evans, "Learning from Failure" (Dreams): <https://advances.realtimerendering.com/s2015/AlexEvans_SIGGRAPH-2015-sml.pdf>; Kapoulkine, "Billions of triangles in minutes": <https://zeux.io/2025/09/30/billions-of-triangles-in-minutes/> and "Meshlet size tradeoffs": <https://zeux.io/2023/01/16/meshlet-size-tradeoffs/>; Garland and Heckbert on quadrics with attributes: <https://mgarland.org/files/papers/quadric2.pdf>; Carrera and others, "Dual Contouring of Signed Distance Data" (abstract): <https://www.alphaxiv.org/abs/2604.00157>; Lengyel, "Voxel-Based Terrain for Real-Time Virtual Simulations": <https://transvoxel.org/Lengyel-VoxelTerrain.pdf>; Keeter, "Massively Parallel Rendering of Complex Closed-Form Implicit Surfaces": <https://www.mattkeeter.com/research/mpr/keeter_mpr20.pdf>; Kuth and others, "Towards Practical Meshlet Compression": <https://arxiv.org/abs/2404.06359>; the Sampler Feedback specification: <https://microsoft.github.io/DirectX-Specs/d3d/SamplerFeedback.html>; Direct3D 12 memory management strategies: <https://learn.microsoft.com/en-us/windows/win32/direct3d12/memory-management-strategies>; Vulkan sparse resources: <https://docs.vulkan.org/spec/latest/chapters/sparsemem.html>; Ziman on DirectStorage: <https://gpuopen.com/gdc-presentations/2023/GDC-2023-DirectStorage-optimizing-load-time-and-streaming.pdf>; RTX Mega Geometry: <https://developer.nvidia.com/blog/nvidia-rtx-mega-geometry-now-available-with-new-vulkan-samples>; Barrett, sparse virtual textures: <https://silverspaceship.com/src/svt>
- Visibility and submission: Arntzen, "Modernizing Granite's mesh rendering": <https://themaister.net/blog/2024/01/17/modernizing-granites-mesh-rendering/>; Tellusim's compute raster benchmark: <https://tellusim.com/compute-raster/>; FidelityFX SPD: <https://gpuopen.com/fidelityfx-spd/>; Mara, McGuire and Luebke, "Lighting Deep G-Buffers": <https://research.nvidia.com/sites/default/files/pubs/2013-12_Lighting-Deep-G-Buffers/Mara2013DeepGBuffer.pdf>; the `VK_EXT_device_generated_commands` proposal: <https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_device_generated_commands.html>; mesh nodes in work graphs: <https://devblogs.microsoft.com/directx/d3d12-mesh-nodes-in-work-graphs/> and <https://gpuopen.com/learn/work_graphs_mesh_nodes/work_graphs_mesh_nodes-intro/>; the work graphs specification: <https://microsoft.github.io/DirectX-Specs/d3d/WorkGraphs.html>; "Virtual Geometry in Bevy 0.14": <https://jms55.github.io/posts/2024-06-09-virtual-geometry-bevy-0-14>
- Illumination: Majercik and others, "Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Fields": <http://www.jcgt.org/published/0008/02/01/paper-lowres.pdf>; "Signed Distance Fields Dynamic Diffuse Global Illumination": <https://arxiv.org/html/2007.14394v1>; Godot's SDFGI article: <https://godotengine.org/article/godot-40-gets-sdf-based-real-time-global-illumination/>; Wright, "Dynamic Occlusion with Signed Distance Fields": <https://advances.realtimerendering.com/s2015/DynamicOcclusionWithSignedDistanceFields.pdf>; DDGI resampling: <https://arxiv.org/html/2108.05263v1>; holographic radiance cascades: <https://arxiv.org/abs/2505.02041>; split radiance cascades: <https://arxiv.org/pdf/2607.20384>; grid-based reservoirs: <https://research.nvidia.com/labs/rtr/publication/boksansky2021rendering>; Unity's probe volume streaming: <https://docs.unity.com/en-us/engine/6000.0/manual/lighting-overview/lighting/probevolumes/streaming>; Lumen's technical details: <https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine>
- Shadows: Lauritzen, Salvi and Lefohn, sample distribution shadow maps: <https://www.advances.realtimerendering.com/s2010/Lauritzen-SDSM(SIGGRAPH%202010%20Advanced%20RealTime%20Rendering%20Course).pdf>; Pettineo, "A Sampling of Shadow Techniques": <https://therealmjp.github.io/posts/shadow-maps>; "Virtual Shadow Maps", Epic: <https://dev.epicgames.com/documentation/unreal-engine/virtual-shadow-maps-in-unreal-engine>; Peters and others, improved moment shadow maps: <https://www.jcgt.org/published/0006/01/03/paper-lowres.pdf>; Tan and others, RTSDF: <https://www.scitepress.org/PublishedPapers/2022/109962/pdf/index.html>; FidelityFX Denoiser: <https://gpuopen.com/manuals/fidelityfx_sdk/techniques/denoiser/> and Hybrid Shadows: <https://gpuopen.com/learn/hybrid-shadows/>; ray traced shadows, Ray Tracing Gems chapter 13: <https://boksajak.github.io/files/RTG1_RayTracedShadows.pdf>; Olsson, Billeter and Assarsson, virtual shadow maps for many lights: <https://www.cse.chalmers.se/~uffe/More%20Efficient%20Virtual%20Shadow%20Maps%20for%20Many%20Lights.pdf>; Lefohn, Sengupta and Owens, resolution-matched shadow maps: <https://faculty.digipen.edu/~gherron/references/References/ShadowAlgorithms/ResolutionMatchedShadowMaps.pdf>; Giegl and Wimmer, fitted virtual shadow maps: <https://www.cg.tuwien.ac.at/research/publications/2007/GIEGL-2007-FVS>; Stephano's virtual shadow maps: <https://ktstephano.github.io/portfolio/virtual-shadow-maps>
- Shading and the image: the Gran Turismo Sport course outline: <https://history.siggraph.org/?p=188541>; Marrs and others, adaptive temporal antialiasing: <https://research.nvidia.com/sites/default/files/pubs/2018-08_Adaptive-Temporal-Antialiasing/adaptive-temporal-antialiasing-preprint.pdf>; Jimenez, next generation post processing: <https://www.iryoku.com/next-generation-post-processing-in-call-of-duty-advanced-warfare/>; Chapman, dynamic local exposure: <https://john-chapman.github.io/2017/08/23/dynamic-local-exposure.html>; Wronski, localized tonemapping: <https://bartwronski.com/2016/08/29/localized-tonemapping/>; FSR 2.1 notes: <https://gpuopen.com/meet-fidelityfx-super-resolution-2-1>; Godot on pipeline compilations: <https://docs.godotengine.org/en/stable/tutorials/performance/pipeline_compilations.html>; Karis, "High Quality Temporal Supersampling" (listed, not read by the judgement): <https://de45xmedrsdbp.cloudfront.net/Resources/files/TemporalAA_small-59732822.pdf>; Pedersen, temporal reprojection in INSIDE: <https://s3.amazonaws.com/arena-attachments/655504/c5c71c5507f0f8bf344252958254fb7d.pdf>; Temporal Super Resolution, Epic: <https://dev.epicgames.com/documentation/en-us/unreal-engine/temporal-super-resolution-in-unreal-engine>; the Khronos PBR Neutral tone mapper: <https://github.com/KhronosGroup/ToneMapping/blob/main/PBR_Neutral/README.md>
- Glass, water and nested views: adaptive voxel-based transparency: <https://advances.realtimerendering.com/s2025/content/AVBOIT_SIG2025_MDROBOT-final.pdf>; moment-based transparency: <https://momentsingraphics.de/?page_id=210>; McGuire and Bavoil, weighted blended transparency: <https://www.jcgt.org/published/0002/02/09/paper.pdf>; single layer water and Substrate, Epic: <https://dev.epicgames.com/documentation/unreal-engine/single-layer-water-shading-model-in-unreal-engine>, <https://dev.epicgames.com/documentation/unreal-engine/overview-of-substrate-materials-in-unreal-engine>; Claybook at GDC 2018: <https://gdcvault.com/play/1025030/Advanced-Graphics-Techniques-Tutorial-GPU>; "Rendering Portals in Virtual Reality": <https://arxiv.org/html/2601.20722v1>; the CS50 portal notes: <https://cs50.harvard.edu/games/2018/notes/11/>; Lengyel, oblique view frustum clipping: <https://www.terathon.com/lengyel/Lengyel-Oblique.pdf>
- Scheduling and the frame: O'Donnell, "FrameGraph": <https://www.slideshare.net/slideshow/framegraph-extensible-rendering-architecture-in-frostbite/72795495>; the Render Dependency Graph and parallel rendering overviews, Epic: <https://dev.epicgames.com/documentation/unreal-engine/render-dependency-graph-in-unreal-engine>, <https://dev.epicgames.com/documentation/en-us/unreal-engine/parallel-rendering-overview-for-unreal-engine>; Gyrling on fibers (overview only): <https://gdcvault.com/play/1022187/Parallelizing-the-Naughty-Dog-Engin>; Arntzen, "Render graphs and Vulkan": <https://themaister.net/blog/2017/08/15/render-graphs-and-vulkan-a-deep-dive/>; "Vulkan Dos and Don'ts": <https://developer.nvidia.com/blog/vulkan-dos-donts>; the Khronos async compute sample, queue priority and frame-in-flight pages: <https://docs.vulkan.org/samples/latest/samples/performance/async_compute/README.html>, <https://docs.vulkan.org/tutorial/latest/Advanced_Vulkan_Compute/08_Asynchronous_Compute/04_queue_priority.html>, <https://docs.vulkan.org/tutorial/latest/Synchronization/Frame_in_Flight/01_introduction.html>; NVIDIA on async compute and on synchronization: <https://developer.nvidia.com/blog/advanced-api-performance-async-compute-and-overlap>, <https://developer.nvidia.com/blog/advanced-api-performance-synchronization/>; the GDC 2017 async compute deep dive: <https://gpuopen.com/download/GDC2017-Asynchronous-Compute-Deep-Dive.pdf>; .NET latency modes: <https://learn.microsoft.com/en-us/dotnet/api/system.runtime.gcsettings.latencymode>, <https://learn.microsoft.com/dotnet/standard/garbage-collection/latency>; Reflex: <https://www.nvidia.com/en-gb/geforce/news/reflex-low-latency-platform>; Zanni, synchronized tracing of implicit surfaces: <https://arxiv.org/html/2304.09673>
- Foliage and terrain: van Muijden, "GPU-Based Procedural Placement in Horizon Zero Dawn": <https://www.guerrilla-games.com/media/News/Files/GDC2017_VanMuijden_GPUBasedProceduralPlacementInHorizonZeroDawn.pdf>; McLaren, deferred texturing in Horizon Forbidden West: <https://www.guerrilla-games.com/read/adventures-with-deferred-texturing-in-horizon-forbidden-west>; procedural generation with work graphs: <https://gpuopen.com/learn/work_graphs_mesh_nodes/work_graphs_mesh_nodes-procedural_generation/>; neural geometric level of detail: <https://research.nvidia.com/labs/toronto-ai/nglod/assets/nglod.pdf>; Simplygon's vegetation optimization: <https://www.simplygon.com/features/vegetationoptimization>; Broadleaf: <https://80.lv/articles/broadleaf-developing-a-real-time-solution-for-rendering-trees/>; instancing leaves with mesh shaders: <https://jysandy.github.io/posts/mesh-shaders>; Johansen's erosion filter: <https://blog.runevision.com/2026/03/fast-and-gorgeous-erosion-filter.html>; the Ghost of Tsushima grass and wind talks (abstracts only): <https://gdcvault.com/play/1027214/contactUs>, <https://gdcvault.com/play/1027350/Blowing-from-the-West-Simulating>
- Presentation: the `VK_KHR_present_wait` reference page: <https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_present_wait.html>; dynamic rate control, libretro: <https://docs.libretro.com/development/cores/dynamic-rate-control/>; Lengyel, font rendering from glyph outlines: <https://jcgt.org/published/0006/02/02/paper.pdf>; Chlumsky, Sloup and Simecek, multi-channel distance fields: <https://dcgi.fel.cvut.cz/wp-content/wpallimport-dist/publications/pdf/publications-2018-sloup-cgf-msdf-paper.pdf>; Vello's architecture page: <https://skia.googlesource.com/external/github.com/linebender/vello/+/refs/heads/sparse/ARCHITECTURE.md>; Levien, a sort-middle architecture for 2D: <https://raphlinus.github.io/rust/graphics/gpu/2020/06/12/sort-middle.html>; Hillesland, texel shading: <https://gpuopen.com/learn/texel-shading/>; the view instancing specification: <https://microsoft.github.io/DirectX-Specs/d3d/ViewInstancing.html>; Persson, practical clustered shading: <https://www.humus.name/Articles/PracticalClusteredShading.pdf>; the Wayland commit-timing protocol: <https://wayland.app/protocols/commit-timing-v1>

**Not fetched or not reachable.** Nothing on this page rests on them.

- "Virtual Shadow Maps in Fortnite Battle Royale Chapter 4": <https://www.unrealengine.com/tech-blog/virtual-shadow-maps-in-fortnite-battle-royale-chapter-4>
- Acton, "CSM Scrolling": <https://advances.realtimerendering.com/s2012/insomniac/Acton-CSM_Scrolling(Siggraph2012).pdf>
- Sakmary, "Resolution Matched Virtual Shadow Maps": <https://cescg.org/wp-content/uploads/2025/04/Sakmary-Resolution-Matched-Virtual-Shadow-Maps.pdf>
- "ReSTIR GI": <https://diglib.eg.org/handle/10.1111/cgf14378>
- Conty Estevez and Kulla, many lights with adaptive tree splitting: <https://fpsunflower.github.io/ckulla/data/many-lights-hpg2018.pdf>
- Heitz and others, linearly transformed cosines course notes: <https://www.advances.realtimerendering.com/s2016/s2016_ltc_rnd.pdf>
- Epic on auto exposure in 4.25, and on Lumen in Fortnite: <https://www.unrealengine.com/tech-blog/how-epic-games-is-handling-auto-exposure-in-4-25>, <https://www.unrealengine.com/tech-blog/lumen-brings-real-time-global-illumination-to-fortnite-battle-royale-chapter-4>
- Yang and others, "A Survey of Temporal Antialiasing Techniques" (abstract only): <https://research.nvidia.com/labs/rtr/publication/yang2020survey>
- "Cone-Traced Supersampling With Subpixel Edge Reconstruction": <https://doi.org/10.1109/TVCG.2023.3343166>
- Fry, HDR colour grading and display in Frostbite: <https://ea.com/frostbite/news/high-dynamic-range-color-grading-and-display-in-frostbite>
- Lagarde and de Rousiers, "Moving Frostbite to Physically Based Rendering": <https://blog.selfshadow.com/publications/s2014-shading-course/frostbite/s2014_pbs_frostbite_slides.pdf>
- Schmitz and others, contouring on the GPU: <https://diglib.eg.org/items/c94e2e75-fc6a-4c55-8e77-c6ef52460288>
- "Nanite GPU-Driven Materials", GDC 2024: <https://schedule.gdconf.com/session/nanite-gpu-driven-materials/899486>
- "Skipping Spheres": <https://diglib.eg.org/bitstream/handle/10.2312/cgvc20241219/cgvc20241219.pdf>
- "Real-Time GPU Tree Generation": <https://diglib.eg.org/handle/10.2312/hpg20251168>
- Loubet and Neyret, hybrid mesh-volume levels of detail: <https://diglib.eg.org/handle/10.1111/cgf13138>
- Concurrent linked lists on the GPU: <https://diglib.eg.org/handle/10.2312/CGF.v29i4pp1297-1304>
- Rough refraction, screen-space refraction by Newton's method, and constant-time refraction: <https://core.ac.uk/download/49936561.pdf>, <https://jcgt.org/published/0015/01/03/paper.pdf>, <https://highperformancegraphics.org/2025/publications/posters/Constant-Time%20Light%20Refraction%20Simulation%20-%20HPG2025.pdf>
- Umbra's introduction to occlusion culling: <https://medium.com/@Umbra3D/introduction-to-occlusion-culling-3d6cfb195c79>
- Qualcomm on multiview extensions: <https://www.qualcomm.com/developer/blog/2023/10/reducing-rendering-work-and-memory-operations-stereoscopic-scenes-new-multiview-extensions>
- The Reflex developer page: <https://developer.nvidia.com/Reflex>
- Quilez, distance functions: <https://iquilezles.org/articles/distfunctions>

---

[Plans](README.md) · [Open items](open-items.md) · [Rendering](rendering.md) · [Decisions](../decisions/rendering.md)
